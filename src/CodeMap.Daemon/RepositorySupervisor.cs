namespace CodeMap.Daemon;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Discovers configured repositories and solutions, observes HEAD without waiting for indexing,
/// and delegates rolling updates to a latest-only per-repository queue.
/// </summary>
public sealed class RepositorySupervisor : BackgroundService
{
    private static readonly HashSet<string> ExcludedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", "packages", ".codemap", ".codex",
    };

    private readonly RuntimeConfiguration _runtime;
    private readonly IndexHandler _indexHandler;
    private readonly IGitService _git;
    private readonly RollingIndexCoordinator _rolling;
    private readonly RuntimeActivityTracker _activity;
    private readonly ILogger<RepositorySupervisor> _logger;
    private readonly ConcurrentDictionary<string, string> _lastObservedHead =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _solutionLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastScheduledRepositoryState =
        new(StringComparer.OrdinalIgnoreCase);
    private int _running;
    private int _targetCount;

    public bool IsRunning => Volatile.Read(ref _running) != 0;
    public int ObservedSolutionCount => Volatile.Read(ref _targetCount);

    public RepositorySupervisor(
        RuntimeConfiguration runtime,
        IndexHandler indexHandler,
        IGitService git,
        RollingIndexCoordinator rolling,
        RuntimeActivityTracker activity,
        ILogger<RepositorySupervisor> logger)
    {
        _runtime = runtime;
        _indexHandler = indexHandler;
        _git = git;
        _rolling = rolling;
        _activity = activity;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((_runtime.Config.RepositoryRoots?.Count ?? 0) == 0 &&
            (_runtime.Config.Repositories?.Count ?? 0) == 0)
            return;

        Volatile.Write(ref _running, 1);
        _logger.LogInformation("Repository supervisor started");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var targets = DiscoverTargets();
                Volatile.Write(ref _targetCount, targets.Count);

                foreach (var repositoryGroup in targets
                             .Where(target => target.IsRolling)
                             .GroupBy(target => target.RepositoryPath, StringComparer.OrdinalIgnoreCase))
                {
                    if (stoppingToken.IsCancellationRequested) break;
                    await ObserveRollingRepositoryAsync(repositoryGroup.ToList(), stoppingToken).ConfigureAwait(false);
                }

                var commitTargets = targets.Where(target => !target.IsRolling).ToList();
                if (commitTargets.Count > 0)
                {
                    using var publication = _activity.BeginPublication();
                    foreach (var target in commitTargets)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await ObserveCommitTargetAsync(target, stoppingToken).ConfigureAwait(false);
                    }
                }

                var delaySeconds = targets.Count == 0
                    ? 3
                    : Math.Max(1, targets.Min(target => target.WatchIntervalSeconds));
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            await _rolling.StopAsync().ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<RepositorySolutionTarget> DiscoverTargets()
    {
        var configDirectory = Path.GetDirectoryName(_runtime.ConfigPath) ?? _runtime.BaseDirectory;
        var targets = new Dictionary<string, RepositorySolutionTarget>(StringComparer.OrdinalIgnoreCase);
        var explicitlyConfiguredRepositories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in _runtime.Config.Repositories ?? [])
        {
            var repositoryRoot = RuntimeConfiguration.ResolvePath(repository.Root, configDirectory);
            explicitlyConfiguredRepositories.Add(repositoryRoot);
            var solutions = ResolveExplicitSolutions(
                repositoryRoot, repository.Solutions, repository.DiscoverSolutions);
            var defaultSolution = ResolveAndValidateDefault(
                repositoryRoot, repository.DefaultSolution, solutions);
            foreach (var solution in solutions)
                AddTarget(targets, CreateTarget(
                    repositoryRoot, solution, defaultSolution,
                    repository.AutoIndex, repository.WatchGitHead, repository.WatchIntervalSeconds,
                    repository.IndexMode, repository.UpdateStrategy,
                    repository.SkipUnaffectedSolutions, repository.RetentionDays,
                    repository.MaxRollingBranches, repository.FullRebuildChangeThreshold,
                    repository.BranchSeedMode, repository.BranchSeedCandidateCount,
                    repository.BranchSeedMinimumSimilarity, repository.StrictGenerationPublish,
                    repository.ServePreviousIndexWhileUpdating));
        }

        foreach (var rootConfig in _runtime.Config.RepositoryRoots ?? [])
        {
            var root = RuntimeConfiguration.ResolvePath(rootConfig.Path, configDirectory);
            var repositories = rootConfig.DiscoverGitRepositories
                ? DiscoverGitRepositories(root, rootConfig.Exclude)
                : IsGitRepository(root) ? [root] : [];

            foreach (var repositoryRoot in repositories)
            {
                if (explicitlyConfiguredRepositories.Contains(repositoryRoot)) continue;
                var solutions = rootConfig.DiscoverSolutions
                    ? IndexHandler.DiscoverSolutionPaths(repositoryRoot)
                    : [];
                var defaultSolution = ResolveAndValidateDefault(
                    repositoryRoot, rootConfig.DefaultSolution, solutions);
                foreach (var solution in solutions)
                    AddTarget(targets, CreateTarget(
                        repositoryRoot, solution, defaultSolution,
                        rootConfig.AutoIndex, rootConfig.WatchGitHead, rootConfig.WatchIntervalSeconds,
                        rootConfig.IndexMode, rootConfig.UpdateStrategy,
                        rootConfig.SkipUnaffectedSolutions, rootConfig.RetentionDays,
                        rootConfig.MaxRollingBranches, rootConfig.FullRebuildChangeThreshold,
                        rootConfig.BranchSeedMode, rootConfig.BranchSeedCandidateCount,
                        rootConfig.BranchSeedMinimumSimilarity, rootConfig.StrictGenerationPublish,
                        rootConfig.ServePreviousIndexWhileUpdating));
            }
        }

        return targets.Values
            .OrderBy(target => target.RepositoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(target => target.IsDefault)
            .ThenBy(target => target.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ObserveRollingRepositoryAsync(
        IReadOnlyList<RepositorySolutionTarget> targets,
        CancellationToken ct)
    {
        var first = targets[0];
        try
        {
            var snapshot = await _git.GetRepositorySnapshotAsync(
                first.RepositoryPath,
                ct).ConfigureAwait(false);
            var observation = snapshot.Branch + "\n" +
                              snapshot.HeadCommit.Value + "\n" +
                              snapshot.WorkingTreeFingerprint;
            var firstObservation = !_lastScheduledRepositoryState.TryGetValue(first.RepositoryPath, out var previous);
            var changed = !firstObservation && !string.Equals(previous, observation, StringComparison.Ordinal);
            _lastScheduledRepositoryState[first.RepositoryPath] = observation;
            if (!(firstObservation && first.AutoIndex) && !(changed && first.WatchGitHead)) return;

            _rolling.Schedule(new RollingRepositoryRequest(
                first.RepositoryPath,
                snapshot.Branch,
                snapshot.HeadCommit,
                targets.Select(target => new RollingSolutionTarget(
                    target.SolutionPath,
                    SolutionId.GetRepositoryRelativePath(target.RepositoryPath, target.SolutionPath),
                    SolutionId.FromPath(target.RepositoryPath, target.SolutionPath),
                    target.IsDefault)).ToList(),
                first.IsIncremental,
                first.SkipUnaffectedSolutions,
                first.RetentionDays,
                first.MaxRollingBranches,
                first.FullRebuildChangeThreshold,
                snapshot,
                first.BranchSeedMode,
                first.BranchSeedCandidateCount,
                first.BranchSeedMinimumSimilarity,
                first.StrictGenerationPublish,
                first.ServePreviousIndexWhileUpdating));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Rolling repository observation failed for {Repository}",
                Path.GetFileName(first.RepositoryPath.TrimEnd(Path.DirectorySeparatorChar)));
        }
    }

    private async Task ObserveCommitTargetAsync(RepositorySolutionTarget target, CancellationToken ct)
    {
        if (!target.AutoIndex && !target.WatchGitHead) return;
        try
        {
            var head = await _git.GetCurrentCommitAsync(target.RepositoryPath, ct).ConfigureAwait(false);
            var firstObservation = !_lastObservedHead.TryGetValue(target.Key, out var previous);
            var changed = !firstObservation && !string.Equals(previous, head.Value, StringComparison.OrdinalIgnoreCase);
            _lastObservedHead[target.Key] = head.Value;
            if (!(firstObservation && target.AutoIndex) && !(changed && target.WatchGitHead)) return;

            var gate = _solutionLocks.GetOrAdd(target.Key, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
            try
            {
                var result = await _indexHandler.HandleAsync(new JsonObject
                {
                    ["repo_path"] = target.RepositoryPath,
                    ["solution_path"] = target.SolutionPath,
                    ["commit_sha"] = head.Value,
                }, ct).ConfigureAwait(false);
                if (result.IsError)
                    _logger.LogError("Commit index failed for {Solution}: {Error}",
                        Path.GetFileName(target.SolutionPath), result.Content);
                else
                    _logger.LogInformation("Commit baseline ready for {Solution} at {Head}",
                        Path.GetFileName(target.SolutionPath), head.Value[..8]);
            }
            finally { gate.Release(); }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Repository observation failed for {Solution}",
                Path.GetFileName(target.SolutionPath));
        }
    }

    internal static IReadOnlyList<string> DiscoverGitRepositories(
        string rootPath,
        IReadOnlyList<string>? configuredExcludes = null)
    {
        if (!Directory.Exists(rootPath)) return [];
        var excludedNames = new HashSet<string>(ExcludedDirectoryNames, StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in configuredExcludes ?? [])
            foreach (var segment in pattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (segment is not "**" and not "*") excludedNames.Add(segment.Trim('*'));

        var results = new List<string>();
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(rootPath));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                if (IsGitRepository(directory)) results.Add(directory);
                foreach (var child in Directory.GetDirectories(directory))
                    if (!excludedNames.Contains(Path.GetFileName(child))) pending.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static IReadOnlyList<string> ResolveExplicitSolutions(
        string repositoryRoot,
        IReadOnlyList<string>? configuredSolutions,
        bool discoverSolutions)
    {
        if (configuredSolutions is not { Count: > 0 })
            return discoverSolutions ? IndexHandler.DiscoverSolutionPaths(repositoryRoot) : [];
        return configuredSolutions
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static string? ResolveAndValidateDefault(
        string repositoryRoot,
        string? configuredDefault,
        IReadOnlyList<string> solutions)
    {
        if (string.IsNullOrWhiteSpace(configuredDefault)) return null;
        var resolved = Path.GetFullPath(Path.IsPathRooted(configuredDefault)
            ? configuredDefault
            : Path.Combine(repositoryRoot, configuredDefault));
        var match = solutions.FirstOrDefault(solution =>
            string.Equals(Path.GetFullPath(solution), resolved, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            throw new InvalidOperationException(
                "Configured defaultSolution must exist and be one of the discovered solutions.");
        return match;
    }

    private static RepositorySolutionTarget CreateTarget(
        string repositoryRoot,
        string solution,
        string? defaultSolution,
        bool autoIndex,
        bool watchGitHead,
        int watchIntervalSeconds,
        string indexMode,
        string updateStrategy,
        bool skipUnaffectedSolutions,
        int retentionDays,
        int maxRollingBranches,
        int fullRebuildChangeThreshold,
        string branchSeedMode,
        int branchSeedCandidateCount,
        double branchSeedMinimumSimilarity,
        bool strictGenerationPublish,
        bool servePreviousIndexWhileUpdating) =>
        new(
            repositoryRoot,
            solution,
            autoIndex,
            watchGitHead,
            Math.Max(1, watchIntervalSeconds),
            string.Equals(indexMode, "rollingBranch", StringComparison.OrdinalIgnoreCase),
            string.Equals(updateStrategy, "incremental", StringComparison.OrdinalIgnoreCase),
            defaultSolution is not null && string.Equals(solution, defaultSolution, StringComparison.OrdinalIgnoreCase),
            skipUnaffectedSolutions,
            Math.Max(1, retentionDays),
            Math.Max(1, maxRollingBranches),
            Math.Max(1, fullRebuildChangeThreshold),
            branchSeedMode,
            Math.Max(1, branchSeedCandidateCount),
            branchSeedMinimumSimilarity,
            strictGenerationPublish,
            servePreviousIndexWhileUpdating);

    private static void AddTarget(
        IDictionary<string, RepositorySolutionTarget> targets,
        RepositorySolutionTarget target) => targets[target.Key] = target;
}

internal sealed record RepositorySolutionTarget(
    string RepositoryPath,
    string SolutionPath,
    bool AutoIndex,
    bool WatchGitHead,
    int WatchIntervalSeconds,
    bool IsRolling,
    bool IsIncremental,
    bool IsDefault,
    bool SkipUnaffectedSolutions,
    int RetentionDays,
    int MaxRollingBranches,
    int FullRebuildChangeThreshold,
    string BranchSeedMode,
    int BranchSeedCandidateCount,
    double BranchSeedMinimumSimilarity,
    bool StrictGenerationPublish,
    bool ServePreviousIndexWhileUpdating)
{
    public string Key => $"{Path.GetFullPath(RepositoryPath)}|{Path.GetFullPath(SolutionPath)}";
}
