namespace CodeMap.Daemon;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Handlers;
using CodeMap.Query;
using Microsoft.Extensions.Logging;

/// <summary>
/// Maintains latest-only per-repository update queues. Queries keep using the active workspace
/// while a new branch state is prepared; overlay batches become visible atomically.
/// </summary>
public sealed class RollingIndexCoordinator : IRollingIndexStatusProvider
{
    private readonly RuntimeConfiguration _runtime;
    private readonly Core.Interfaces.IGitService _git;
    private readonly IndexHandler _indexHandler;
    private readonly WorkspaceManager _workspaceManager;
    private readonly Core.Interfaces.IOverlayStore _overlayStore;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _sticky;
    private readonly ILogger<RollingIndexCoordinator> _logger;
    private readonly RuntimeActivityTracker _activity;
    private readonly RollingIndexStateStore _stateStore;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, QueueState> _queues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RollingSolutionStatus>> _statuses =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SolutionId> _priorities =
        new(StringComparer.OrdinalIgnoreCase);

    public RollingIndexCoordinator(
        RuntimeConfiguration runtime,
        Core.Interfaces.IGitService git,
        IndexHandler indexHandler,
        WorkspaceManager workspaceManager,
        Core.Interfaces.IOverlayStore overlayStore,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry sticky,
        RuntimeActivityTracker activity,
        ILogger<RollingIndexCoordinator> logger)
    {
        _runtime = runtime;
        _git = git;
        _indexHandler = indexHandler;
        _workspaceManager = workspaceManager;
        _overlayStore = overlayStore;
        _repoRegistry = repoRegistry;
        _sticky = sticky;
        _activity = activity;
        _logger = logger;
        _stateStore = new RollingIndexStateStore(runtime.DataDirectory);
        _sticky.SetSolutionRequestedCallback(RequestPriority);
    }

    public void Schedule(RollingRepositoryRequest request)
    {
        if (_shutdown.IsCancellationRequested) return;
        var key = Normalize(request.RepositoryPath);
        var queue = _queues.GetOrAdd(key, _ => new QueueState());
        lock (queue.Gate)
        {
            queue.Pending = request;
            if (queue.Runner is null || queue.Runner.IsCompleted)
                queue.Runner = Task.Run(() => RunQueueAsync(key, queue, _shutdown.Token), CancellationToken.None);
        }
    }

    public IReadOnlyList<RollingSolutionStatus> GetStatuses(string repoPath)
    {
        var key = Normalize(repoPath);
        return _statuses.TryGetValue(key, out var statuses)
            ? statuses.Values.OrderBy(status => status.SolutionPath, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
    }

    public int TrackedSolutionCount => _statuses.Values.Sum(statuses => statuses.Count);

    public int ActiveQueueCount => _queues.Values.Count(queue => queue.Runner is { IsCompleted: false });

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        _sticky.SetSolutionRequestedCallback(null);
        var runners = _queues.Values.Select(queue => queue.Runner).Where(task => task is not null).Cast<Task>().ToArray();
        try { await Task.WhenAll(runners).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task RunQueueAsync(string key, QueueState queue, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            RollingRepositoryRequest? request;
            lock (queue.Gate)
            {
                request = queue.Pending;
                queue.Pending = null;
            }
            if (request is null) return;

            try
            {
                using var publication = _activity.BeginPublication();
                await ProcessAsync(key, request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Rolling update failed for repository {Repository}", SafeRepositoryLabel(request.RepositoryPath));
            }
        }
    }

    private async Task ProcessAsync(string key, RollingRepositoryRequest request, CancellationToken ct)
    {
        var repoId = await _git.GetRepoIdentityAsync(request.RepositoryPath, ct).ConfigureAwait(false);
        var orderedTargets = request.Solutions
            .OrderByDescending(target => target.IsDefault)
            .ThenBy(target => target.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentStates = orderedTargets.ToDictionary(
            target => target.SolutionId,
            target => _stateStore.Load(repoId, target.SolutionId, request.Branch));

        foreach (var target in orderedTargets)
            _repoRegistry.RegisterSolution(request.RepositoryPath,
                new SolutionRegistration(target.SolutionId, target.RelativePath, target.SolutionPath));
        var defaultTarget = orderedTargets.FirstOrDefault(target => target.IsDefault);
        if (defaultTarget is not null)
            _repoRegistry.SetDefaultSolution(request.RepositoryPath, defaultTarget.SolutionId);

        if (string.Equals(request.Branch, "HEAD", StringComparison.Ordinal))
        {
            foreach (var target in orderedTargets)
            {
                var detachedWorkspace = WorkspaceFor(repoId, request.Branch, target.SolutionId, request.HeadCommit);
                var state = await FullBuildAsync(
                    request, repoId, target, detachedWorkspace,
                    "detached HEAD uses commit-specific baseline mode", ct).ConfigureAwait(false);
                Publish(key, state);
                _sticky.Set(request.RepositoryPath, target.SolutionId, detachedWorkspace.Value);
            }
            return;
        }

        // A branch created at an already indexed commit aliases the existing logical state.
        if (currentStates.Values.All(state => state is null))
        {
            var reusable = orderedTargets
                .Select(target => _stateStore.FindAtCommit(repoId, target.SolutionId, request.HeadCommit))
                .ToList();
            if (reusable.All(state => state is not null))
            {
                foreach (var (target, reused) in orderedTargets.Zip(reusable))
                {
                    var state = reused! with
                    {
                        Branch = request.Branch,
                        HeadCommit = request.HeadCommit,
                        IndexedCommit = request.HeadCommit,
                        IndexState = RollingIndexState.UpToDate,
                        PossiblyStale = false,
                        Affected = false,
                        ChangedFileCount = 0,
                        UpdatedSymbolCount = 0,
                        Strategy = RollingUpdateStrategy.Reused,
                        LastUpdatedAt = DateTimeOffset.UtcNow,
                        LastError = null,
                    };
                    await RestoreWorkspaceAsync(request.RepositoryPath, target, state, ct).ConfigureAwait(false);
                    SaveAndPublish(key, state);
                    _sticky.Set(request.RepositoryPath, target.SolutionId, state.WorkspaceId.Value);
                }
                _logger.LogInformation("Branch state reused at {Head}; no full rebuild", Short(request.HeadCommit));
                return;
            }
        }

        if (!request.Incremental && currentStates.Values.Any(state => state?.IndexedCommit != request.HeadCommit))
        {
            foreach (var target in orderedTargets)
            {
                var workspace = WorkspaceFor(repoId, request.Branch, target.SolutionId, request.HeadCommit);
                var rebuilt = await FullBuildAsync(
                    request, repoId, target, workspace,
                    "configured update strategy requires commit baseline", ct).ConfigureAwait(false);
                Publish(key, rebuilt);
                _sticky.Set(request.RepositoryPath, target.SolutionId, workspace.Value);
            }
            return;
        }

        var summaryWatch = Stopwatch.StartNew();
        var affectedCount = 0;
        var skippedCount = 0;

        var remainingTargets = new List<RollingSolutionTarget>(orderedTargets);
        while (remainingTargets.Count > 0)
        {
            var selectedIndex = 0;
            var defaultStillFirst = remainingTargets.Count == orderedTargets.Count && remainingTargets[0].IsDefault;
            if (!defaultStillFirst && _priorities.TryRemove(key, out var prioritizedSolution))
            {
                var requestedIndex = remainingTargets.FindIndex(target => target.SolutionId == prioritizedSolution);
                if (requestedIndex >= 0) selectedIndex = requestedIndex;
            }
            var target = remainingTargets[selectedIndex];
            remainingTargets.RemoveAt(selectedIndex);
            ct.ThrowIfCancellationRequested();
            var state = currentStates[target.SolutionId];
            if (state is null)
            {
                var created = await FullBuildAsync(
                    request, repoId, target,
                    WorkspaceFor(repoId, request.Branch, target.SolutionId, request.HeadCommit),
                    "no compatible rolling state", ct).ConfigureAwait(false);
                Publish(key, created);
                _sticky.Set(request.RepositoryPath, target.SolutionId, created.WorkspaceId.Value);
                currentStates[target.SolutionId] = created;
                affectedCount++;
                continue;
            }

            if (state.IndexedCommit == request.HeadCommit)
            {
                await RestoreWorkspaceAsync(request.RepositoryPath, target, state, ct).ConfigureAwait(false);
                var current = state with
                {
                    HeadCommit = request.HeadCommit,
                    IndexState = RollingIndexState.UpToDate,
                    PossiblyStale = false,
                    Affected = false,
                    Strategy = RollingUpdateStrategy.Reused,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                    LastError = null,
                };
                SaveAndPublish(key, current);
                _sticky.Set(request.RepositoryPath, target.SolutionId, current.WorkspaceId.Value);
                skippedCount++;
                continue;
            }

            Publish(key, state with
            {
                Branch = request.Branch,
                HeadCommit = request.HeadCommit,
                IndexState = RollingIndexState.Checking,
                PossiblyStale = true,
                Affected = false,
                ChangedFileCount = 0,
                UpdatedSymbolCount = 0,
                LastError = null,
            });

            var checkWatch = Stopwatch.StartNew();
            IReadOnlyList<FileChange> changes;
            bool fastForward;
            CommitSha? mergeBase;
            try
            {
                fastForward = await _git.IsAncestorAsync(
                    request.RepositoryPath, state.IndexedCommit, request.HeadCommit, ct).ConfigureAwait(false);
                mergeBase = fastForward ? state.IndexedCommit : await _git.FindMergeBaseAsync(
                    request.RepositoryPath, state.IndexedCommit, request.HeadCommit, ct).ConfigureAwait(false);
                changes = await _git.GetChangedFilesAsync(
                    request.RepositoryPath,
                    fastForward ? state.IndexedCommit : mergeBase ?? state.IndexedCommit,
                    request.HeadCommit,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                var failed = state with
                {
                    Branch = request.Branch,
                    HeadCommit = request.HeadCommit,
                    IndexState = RollingIndexState.FullRebuildRequired,
                    PossiblyStale = true,
                    LastError = Sanitize(ex.Message, request.RepositoryPath),
                    LastCheckDurationMilliseconds = checkWatch.Elapsed.TotalMilliseconds,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
                SaveAndPublish(key, failed);
                var rebuilt = await FullBuildAsync(
                    request, repoId, target,
                    WorkspaceFor(repoId, request.Branch, target.SolutionId, request.HeadCommit),
                    "commit comparison failed", ct).ConfigureAwait(false);
                Publish(key, rebuilt);
                _sticky.Set(request.RepositoryPath, target.SolutionId, rebuilt.WorkspaceId.Value);
                affectedCount++;
                continue;
            }

            var map = _stateStore.LoadImpactMap(repoId, target.SolutionId);
            if (map is null || !string.Equals(map.SolutionPath, target.RelativePath, StringComparison.OrdinalIgnoreCase))
            {
                map = SolutionImpactMap.Build(request.RepositoryPath, target.SolutionPath);
                _stateStore.SaveImpactMap(repoId, target.SolutionId, map);
            }
            var impact = map.Analyze(changes);
            checkWatch.Stop();

            if (!impact.IsAffected && request.SkipUnaffectedSolutions)
            {
                var skipped = state with
                {
                    Branch = request.Branch,
                    HeadCommit = request.HeadCommit,
                    IndexedCommit = request.HeadCommit,
                    IndexState = RollingIndexState.UpToDate,
                    PossiblyStale = false,
                    Affected = false,
                    ChangedFileCount = 0,
                    UpdatedSymbolCount = 0,
                    Strategy = RollingUpdateStrategy.Reused,
                    LastCheckDurationMilliseconds = checkWatch.Elapsed.TotalMilliseconds,
                    LastUpdateDurationMilliseconds = 0,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                    LastError = null,
                };
                SaveAndPublish(key, skipped);
                _sticky.Set(request.RepositoryPath, target.SolutionId, skipped.WorkspaceId.Value);
                _logger.LogInformation("{Solution}: unaffected; {Reason}; skipped", target.RelativePath, impact.Reason);
                skippedCount++;
                continue;
            }

            if (changes.Count > request.FullRebuildChangeThreshold)
            {
                SaveAndPublish(key, state with
                {
                    Branch = request.Branch,
                    HeadCommit = request.HeadCommit,
                    IndexState = RollingIndexState.FullRebuildRequired,
                    PossiblyStale = true,
                    Affected = true,
                    ChangedFileCount = changes.Count,
                    LastCheckDurationMilliseconds = checkWatch.Elapsed.TotalMilliseconds,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                    LastError = $"change threshold exceeded ({changes.Count} files)",
                });
                var rebuilt = await FullBuildAsync(
                    request, repoId, target,
                    WorkspaceFor(repoId, request.Branch, target.SolutionId, request.HeadCommit),
                    $"change threshold exceeded ({changes.Count} files)", ct).ConfigureAwait(false);
                Publish(key, rebuilt);
                _sticky.Set(request.RepositoryPath, target.SolutionId, rebuilt.WorkspaceId.Value);
                affectedCount++;
                continue;
            }

            affectedCount++;
            var updating = state with
            {
                Branch = request.Branch,
                HeadCommit = request.HeadCommit,
                IndexState = RollingIndexState.Updating,
                PossiblyStale = true,
                Affected = true,
                ChangedFileCount = impact.ChangedInputCount,
                Strategy = RollingUpdateStrategy.Incremental,
                LastCheckDurationMilliseconds = checkWatch.Elapsed.TotalMilliseconds,
                LastUpdatedAt = DateTimeOffset.UtcNow,
                LastError = null,
            };
            SaveAndPublish(key, updating);

            var updateWatch = Stopwatch.StartNew();
            var nextWorkspace = WorkspaceFor(
                repoId, request.Branch, target.SolutionId, request.HeadCommit);
            var storageRepoId = SolutionScope.ToStorageRepoId(repoId, target.SolutionId);
            try
            {
                var created = await _workspaceManager.CreateWorkspaceAsync(
                    storageRepoId, nextWorkspace, state.BaselineCommit,
                    target.SolutionPath, request.RepositoryPath, ct).ConfigureAwait(false);
                if (created.IsFailure) throw new InvalidOperationException(created.Error.Message);
                var result = await _workspaceManager.RefreshOverlayAsync(
                    storageRepoId,
                    nextWorkspace,
                    explicitFilePaths: null,
                    ct).ConfigureAwait(false);
                if (result.IsFailure) throw new InvalidOperationException(result.Error.Message);
                if (result.Value.Metrics is { } metrics)
                {
                    _logger.LogInformation(
                        "Rolling delta for {Solution}: mode={Mode}, changed={Changed}, noOp={NoOp}, " +
                        "documents={Documents}, projects={Projects}, symbolsWritten={Written}, " +
                        "symbolsDeleted={Deleted}, relations={Relations}, fallback={Fallback}, " +
                        "gitMs={GitMs:F1}, compileMs={CompileMs:F1}, symbolsMs={SymbolsMs:F1}, " +
                        "referencesMs={ReferencesMs:F1}, overlayMs={OverlayMs:F1}, totalMs={TotalMs:F1}",
                        target.RelativePath,
                        metrics.Mode,
                        metrics.ChangedFiles,
                        metrics.SemanticNoOpFiles,
                        metrics.DocumentsReindexed,
                        metrics.AffectedProjects,
                        metrics.SymbolsWritten,
                        metrics.SymbolsDeleted,
                        metrics.RelationsUpdated,
                        metrics.FallbackReason,
                        metrics.Timings.GitDiff.TotalMilliseconds,
                        metrics.Timings.DirectCompilation.TotalMilliseconds,
                        metrics.Timings.SymbolExtraction.TotalMilliseconds,
                        metrics.Timings.ReferenceExtraction.TotalMilliseconds,
                        metrics.Timings.OverlayWrite.TotalMilliseconds,
                        metrics.Timings.Total.TotalMilliseconds);
                }

                updateWatch.Stop();
                var updated = updating with
                {
                    IndexedCommit = request.HeadCommit,
                    WorkspaceId = nextWorkspace,
                    OverlayRevision = result.Value.NewOverlayRevision,
                    IndexState = RollingIndexState.UpToDate,
                    PossiblyStale = false,
                    UpdatedSymbolCount = result.Value.SymbolsUpdated,
                    LastUpdateDurationMilliseconds = updateWatch.Elapsed.TotalMilliseconds,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
                SaveAndPublish(key, updated);
                _sticky.Set(request.RepositoryPath, target.SolutionId, nextWorkspace.Value);
                if (impact.RebuildMap)
                {
                    var refreshedMap = SolutionImpactMap.Build(request.RepositoryPath, target.SolutionPath);
                    _stateStore.SaveImpactMap(repoId, target.SolutionId, refreshedMap);
                }
                _logger.LogInformation(
                    "{Solution}: incremental, {Files} files, {Symbols} symbols, {Duration:F1}ms",
                    target.RelativePath, impact.ChangedInputCount, result.Value.SymbolsUpdated,
                    updateWatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                updateWatch.Stop();
                var failed = updating with
                {
                    IndexState = RollingIndexState.Failed,
                    PossiblyStale = true,
                    LastError = Sanitize(ex.Message, request.RepositoryPath),
                    LastUpdateDurationMilliseconds = updateWatch.Elapsed.TotalMilliseconds,
                    LastUpdatedAt = DateTimeOffset.UtcNow,
                };
                SaveAndPublish(key, failed);
                await _workspaceManager.DeleteWorkspaceAsync(
                    storageRepoId, nextWorkspace, ct).ConfigureAwait(false);
                _logger.LogError(ex, "Incremental update failed for {Solution}; previous index remains active", target.RelativePath);
            }
        }

        var finalStates = currentStates.Keys
            .Select(solutionId => _statuses[key].GetValueOrDefault(solutionId.Value))
            .Where(state => state is not null)
            .Cast<RollingSolutionStatus>()
            .ToList();
        foreach (var state in finalStates.Where(state => state.IndexState == RollingIndexState.UpToDate))
            _sticky.Set(request.RepositoryPath, state.SolutionId, state.WorkspaceId.Value);

        var removedStates = _stateStore.ApplyRetention(
            repoId, request.RetentionDays, request.MaxRollingBranches);
        var retainedWorkspaceKeys = _stateStore.LoadAll(repoId)
            .Select(state => state.SolutionId.Value + "|" + state.WorkspaceId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var removed in removedStates)
        {
            var workspaceKey = removed.SolutionId.Value + "|" + removed.WorkspaceId.Value;
            if (retainedWorkspaceKeys.Contains(workspaceKey)) continue;
            await _overlayStore.DeleteOverlayAsync(
                SolutionScope.ToStorageRepoId(repoId, removed.SolutionId),
                removed.WorkspaceId,
                ct).ConfigureAwait(false);
        }
        _logger.LogInformation(
            "HEAD transition complete at {Head}: {Solutions} solutions checked, {Affected} affected, {Skipped} skipped, {Duration:F1}ms",
            Short(request.HeadCommit), orderedTargets.Count, affectedCount, skippedCount, summaryWatch.Elapsed.TotalMilliseconds);
    }

    private async Task<RollingSolutionStatus> FullBuildAsync(
        RollingRepositoryRequest request,
        RepoId repoId,
        RollingSolutionTarget target,
        WorkspaceId workspaceId,
        string reason,
        CancellationToken ct)
    {
        _logger.LogWarning("Full rebuild for {Solution}: {Reason}", target.RelativePath, reason);
        var watch = Stopwatch.StartNew();
        var result = await _indexHandler.HandleAsync(new JsonObject
        {
            ["repo_path"] = request.RepositoryPath,
            ["solution_path"] = target.SolutionPath,
            ["commit_sha"] = request.HeadCommit.Value,
        }, ct).ConfigureAwait(false);
        if (result.IsError)
            throw new InvalidOperationException("Full rebuild failed: " + Sanitize(result.Content, request.RepositoryPath));

        var storageRepoId = SolutionScope.ToStorageRepoId(repoId, target.SolutionId);
        var existing = _workspaceManager.GetWorkspaceInfo(storageRepoId, workspaceId);
        if (existing is not null && existing.BaselineCommitSha != request.HeadCommit)
            await _workspaceManager.DeleteWorkspaceAsync(storageRepoId, workspaceId, ct).ConfigureAwait(false);
        var created = await _workspaceManager.CreateWorkspaceAsync(
            storageRepoId, workspaceId, request.HeadCommit,
            target.SolutionPath, request.RepositoryPath, ct).ConfigureAwait(false);
        if (created.IsFailure) throw new InvalidOperationException(created.Error.Message);

        var map = SolutionImpactMap.Build(request.RepositoryPath, target.SolutionPath);
        _stateStore.SaveImpactMap(repoId, target.SolutionId, map);
        watch.Stop();
        var state = new RollingSolutionStatus(
            repoId, target.SolutionId, target.RelativePath, request.Branch,
            request.HeadCommit, request.HeadCommit, request.HeadCommit,
            workspaceId, created.Value.CurrentRevision,
            RollingIndexState.UpToDate, false, true, 0, 0,
            RollingUpdateStrategy.FullRebuild, 0, watch.Elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow, null);
        _stateStore.Save(state);
        return state;
    }

    private async Task RestoreWorkspaceAsync(
        string repoPath,
        RollingSolutionTarget target,
        RollingSolutionStatus state,
        CancellationToken ct)
    {
        var storageRepoId = SolutionScope.ToStorageRepoId(state.RepoId, target.SolutionId);
        var existing = _workspaceManager.GetWorkspaceInfo(storageRepoId, state.WorkspaceId);
        if (existing is not null) return;
        var baseline = await _indexHandler.HandleAsync(new JsonObject
        {
            ["repo_path"] = repoPath,
            ["solution_path"] = target.SolutionPath,
            ["commit_sha"] = state.BaselineCommit.Value,
        }, ct).ConfigureAwait(false);
        if (baseline.IsError)
            throw new InvalidOperationException(
                "Could not restore rolling baseline: " + Sanitize(baseline.Content, repoPath));
        var restored = await _workspaceManager.CreateWorkspaceAsync(
            storageRepoId, state.WorkspaceId, state.BaselineCommit,
            target.SolutionPath, repoPath, ct).ConfigureAwait(false);
        if (restored.IsFailure) throw new InvalidOperationException(restored.Error.Message);
    }

    private void SaveAndPublish(string key, RollingSolutionStatus state)
    {
        _stateStore.Save(state);
        Publish(key, state);
    }

    private void Publish(string key, RollingSolutionStatus state)
    {
        var statuses = _statuses.GetOrAdd(key,
            _ => new ConcurrentDictionary<string, RollingSolutionStatus>(StringComparer.OrdinalIgnoreCase));
        statuses[state.SolutionId.Value] = state;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');
    private void RequestPriority(string repoPath, SolutionId solutionId) =>
        _priorities[Normalize(repoPath)] = solutionId;
    private static string Short(CommitSha sha) => sha.Value[..Math.Min(8, sha.Value.Length)];
    private static string SafeRepositoryLabel(string path) => Path.GetFileName(Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));
    private static string Sanitize(string message, string repoPath) =>
        message.Replace(Path.GetFullPath(repoPath), "<repository>", StringComparison.OrdinalIgnoreCase);
    private static WorkspaceId WorkspaceFor(
        RepoId repoId,
        string branch,
        SolutionId solutionId,
        CommitSha head) =>
        WorkspaceId.From("rolling-" + RollingIndexStateStore.StableId(
            repoId.Value + "\n" + branch + "\n" + solutionId.Value + "\n" + head.Value));

    private sealed class QueueState
    {
        public object Gate { get; } = new();
        public RollingRepositoryRequest? Pending { get; set; }
        public Task? Runner { get; set; }
    }
}

public sealed record RollingRepositoryRequest(
    string RepositoryPath,
    string Branch,
    CommitSha HeadCommit,
    IReadOnlyList<RollingSolutionTarget> Solutions,
    bool Incremental,
    bool SkipUnaffectedSolutions,
    int RetentionDays,
    int MaxRollingBranches,
    int FullRebuildChangeThreshold);

public sealed record RollingSolutionTarget(
    string SolutionPath,
    string RelativePath,
    SolutionId SolutionId,
    bool IsDefault);
