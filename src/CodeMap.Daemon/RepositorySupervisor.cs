namespace CodeMap.Daemon;

using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Mcp.Handlers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Discovers configured repositories/solutions and maintains a baseline for each watched HEAD.
/// Work is serialized per solution and failures are isolated so one broken solution cannot stop others.
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
    private readonly ILogger<RepositorySupervisor> _logger;
    private readonly ConcurrentDictionary<string, string> _lastObservedHead =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _solutionLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public RepositorySupervisor(
        RuntimeConfiguration runtime,
        IndexHandler indexHandler,
        IGitService git,
        ILogger<RepositorySupervisor> logger)
    {
        _runtime = runtime;
        _indexHandler = indexHandler;
        _git = git;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if ((_runtime.Config.RepositoryRoots?.Count ?? 0) == 0 &&
            (_runtime.Config.Repositories?.Count ?? 0) == 0)
        {
            return;
        }

        _logger.LogInformation("Repository supervisor started using {ConfigPath}", _runtime.ConfigPath);
        while (!stoppingToken.IsCancellationRequested)
        {
            var targets = DiscoverTargets();
            foreach (var target in targets)
            {
                if (stoppingToken.IsCancellationRequested) break;
                await ObserveTargetAsync(target, stoppingToken).ConfigureAwait(false);
            }

            var delaySeconds = targets.Count == 0 ? 3 : Math.Max(1, targets.Min(t => t.WatchIntervalSeconds));
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken).ConfigureAwait(false);
        }
    }

    internal IReadOnlyList<RepositorySolutionTarget> DiscoverTargets()
    {
        var configDir = Path.GetDirectoryName(_runtime.ConfigPath) ?? _runtime.BaseDirectory;
        var targets = new Dictionary<string, RepositorySolutionTarget>(StringComparer.OrdinalIgnoreCase);
        var explicitlyConfiguredRepos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var repository in _runtime.Config.Repositories ?? [])
        {
            var repoRoot = RuntimeConfiguration.ResolvePath(repository.Root, configDir);
            explicitlyConfiguredRepos.Add(repoRoot);
            foreach (var solution in ResolveExplicitSolutions(repoRoot, repository.Solutions))
            {
                AddTarget(targets, new RepositorySolutionTarget(
                    repoRoot, solution, repository.AutoIndex, repository.WatchGitHead,
                    Math.Max(1, repository.WatchIntervalSeconds)));
            }
        }

        foreach (var rootConfig in _runtime.Config.RepositoryRoots ?? [])
        {
            var root = RuntimeConfiguration.ResolvePath(rootConfig.Path, configDir);
            var repositories = rootConfig.DiscoverGitRepositories
                ? DiscoverGitRepositories(root)
                : IsGitRepository(root) ? [root] : [];

            foreach (var repoRoot in repositories)
            {
                if (explicitlyConfiguredRepos.Contains(repoRoot)) continue;
                var solutions = rootConfig.DiscoverSolutions
                    ? IndexHandler.DiscoverSolutionPaths(repoRoot)
                    : [];
                foreach (var solution in solutions)
                {
                    AddTarget(targets, new RepositorySolutionTarget(
                        repoRoot, solution, rootConfig.AutoIndex, rootConfig.WatchGitHead,
                        Math.Max(1, rootConfig.WatchIntervalSeconds)));
                }
            }
        }

        return targets.Values
            .OrderBy(t => t.RepositoryPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.SolutionPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task ObserveTargetAsync(RepositorySolutionTarget target, CancellationToken ct)
    {
        if (!target.AutoIndex && !target.WatchGitHead) return;
        var key = target.Key;
        try
        {
            var head = await _git.GetCurrentCommitAsync(target.RepositoryPath, ct).ConfigureAwait(false);
            var firstObservation = !_lastObservedHead.TryGetValue(key, out var previous);
            var changed = !firstObservation && !string.Equals(previous, head.Value, StringComparison.OrdinalIgnoreCase);
            _lastObservedHead[key] = head.Value;
            if (!(firstObservation && target.AutoIndex) && !(changed && target.WatchGitHead)) return;

            var gate = _solutionLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, ct).ConfigureAwait(false)) return;
            try
            {
                // The observed SHA is fixed for this job. If HEAD changes while indexing, the
                // next observation schedules the newer commit and the stale job is never relabelled.
                var result = await _indexHandler.HandleAsync(new JsonObject
                {
                    ["repo_path"] = target.RepositoryPath,
                    ["solution_path"] = target.SolutionPath,
                    ["commit_sha"] = head.Value,
                }, ct).ConfigureAwait(false);

                if (result.IsError)
                    _logger.LogError("Auto-index failed for {Repo} / {Solution}: {Error}",
                        target.RepositoryPath, target.SolutionPath, result.Content);
                else
                    _logger.LogInformation("Baseline ready for {Repo} / {Solution} at {Head}",
                        target.RepositoryPath, target.SolutionPath, head.Value[..8]);
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Repository observation failed for {Repo} / {Solution}",
                target.RepositoryPath, target.SolutionPath);
        }
    }

    internal static IReadOnlyList<string> DiscoverGitRepositories(string rootPath)
    {
        if (!Directory.Exists(rootPath)) return [];
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
                    if (!ExcludedDirectoryNames.Contains(Path.GetFileName(child)))
                        pending.Push(child);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Continue with other repositories when one subtree cannot be read.
            }
        }
        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsGitRepository(string path) =>
        Directory.Exists(Path.Combine(path, ".git")) || File.Exists(Path.Combine(path, ".git"));

    private static IReadOnlyList<string> ResolveExplicitSolutions(
        string repoRoot,
        IReadOnlyList<string>? configuredSolutions)
    {
        if (configuredSolutions is not { Count: > 0 })
            return IndexHandler.DiscoverSolutionPaths(repoRoot);

        return configuredSolutions
            .Select(path => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(repoRoot, path)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddTarget(
        IDictionary<string, RepositorySolutionTarget> targets,
        RepositorySolutionTarget target) => targets[target.Key] = target;
}

internal sealed record RepositorySolutionTarget(
    string RepositoryPath,
    string SolutionPath,
    bool AutoIndex,
    bool WatchGitHead,
    int WatchIntervalSeconds)
{
    public string Key => $"{Path.GetFullPath(RepositoryPath)}|{Path.GetFullPath(SolutionPath)}";
}
