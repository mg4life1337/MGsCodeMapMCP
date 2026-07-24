namespace CodeMap.Daemon;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Handlers;
using CodeMap.Query;
using CodeMap.Roslyn;
using Microsoft.Extensions.Logging;

/// <summary>
/// Builds complete repository generations in staging workspaces and publishes
/// exactly one generation pointer after every solution succeeds.
/// </summary>
public sealed class RollingIndexCoordinator : IRollingIndexStatusProvider
{
    private readonly RuntimeConfiguration _runtime;
    private readonly IGitService _git;
    private readonly IndexHandler _indexHandler;
    private readonly WorkspaceManager _workspaceManager;
    private readonly IOverlayStore _overlayStore;
    private readonly IRepoRegistry _repoRegistry;
    private readonly IWorkspaceStickyRegistry _sticky;
    private readonly RollingGenerationRegistry _generations;
    private readonly ILogger<RollingIndexCoordinator> _logger;
    private readonly RuntimeActivityTracker _activity;
    private readonly RollingIndexStateStore _stateStore;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, QueueState> _queues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RollingSolutionStatus>> _statuses =
        new(StringComparer.OrdinalIgnoreCase);

    public RollingIndexCoordinator(
        RuntimeConfiguration runtime,
        IGitService git,
        IndexHandler indexHandler,
        WorkspaceManager workspaceManager,
        IOverlayStore overlayStore,
        IRepoRegistry repoRegistry,
        IWorkspaceStickyRegistry sticky,
        RollingGenerationRegistry generations,
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
        _generations = generations;
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
                queue.Runner = Task.Run(
                    () => RunQueueAsync(key, queue, _shutdown.Token),
                    CancellationToken.None);
        }
    }

    public IReadOnlyList<RollingSolutionStatus> GetStatuses(string repoPath)
    {
        var key = Normalize(repoPath);
        return _statuses.TryGetValue(key, out var statuses)
            ? statuses.Values
                .OrderBy(status => status.SolutionPath, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];
    }

    public int TrackedSolutionCount => _statuses.Values.Sum(statuses => statuses.Count);

    public int ActiveQueueCount => _queues.Values.Count(
        queue => queue.Runner is { IsCompleted: false });

    public async Task StopAsync()
    {
        _shutdown.Cancel();
        _sticky.SetSolutionRequestedCallback(null);
        var runners = _queues.Values
            .Select(queue => queue.Runner)
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Rolling generation failed for repository {Repository}",
                    SafeRepositoryLabel(request.RepositoryPath));
            }
        }
    }

    private async Task ProcessAsync(
        string key,
        RollingRepositoryRequest request,
        CancellationToken ct)
    {
        var snapshot = request.Snapshot ??
            await _git.GetRepositorySnapshotAsync(request.RepositoryPath, ct)
                .ConfigureAwait(false);
        request = request with
        {
            Branch = snapshot.Branch,
            HeadCommit = snapshot.HeadCommit,
            Snapshot = snapshot,
        };
        _generations.BeginUpdate(
            request.RepositoryPath,
            snapshot,
            request.ServePreviousIndexWhileUpdating);

        var targets = request.Solutions
            .OrderByDescending(target => target.IsDefault)
            .ThenBy(target => target.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var target in targets)
        {
            _repoRegistry.RegisterSolution(
                request.RepositoryPath,
                new SolutionRegistration(
                    target.SolutionId,
                    target.RelativePath,
                    target.SolutionPath));
        }
        var defaultTarget = targets.FirstOrDefault(target => target.IsDefault);
        if (defaultTarget is not null)
            _repoRegistry.SetDefaultSolution(request.RepositoryPath, defaultTarget.SolutionId);

        var compatibility = GetCompatibilityFingerprint();
        var history = _generations.LoadHistory(
            snapshot.RepoId,
            request.RepositoryPath);
        await RecoverIncompleteStagingAsync(
            snapshot.RepoId,
            request.RepositoryPath,
            ct).ConfigureAwait(false);
        _generations.BeginStaging(
            request.RepositoryPath,
            new StagingRepositoryGeneration(
                snapshot.GenerationId,
                snapshot.RepoId,
                targets.Select(target => new StagingWorkspaceBinding(
                    target.SolutionId,
                    WorkspaceFor(
                        snapshot.RepoId,
                        snapshot.Branch,
                        target.SolutionId,
                        snapshot.GenerationId))).ToList()));
        var staged = new ConcurrentBag<SolutionBuildResult>();
        var parallelism = Math.Max(
            1,
            _runtime.Config.IndexingResources?.MaxConcurrentIncrementalSolutions ??
            new IndexingResourceConfig().MaxConcurrentIncrementalSolutions);
        using var solutionGate = new SemaphoreSlim(parallelism, parallelism);
        var totalTimer = Stopwatch.StartNew();
        var published = false;

        try
        {
            var tasks = targets.Select(async target =>
            {
                await solutionGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    var result = await BuildSolutionAsync(
                        key,
                        request,
                        snapshot,
                        compatibility,
                        history,
                        target,
                        ct).ConfigureAwait(false);
                    staged.Add(result);
                }
                finally
                {
                    solutionGate.Release();
                }
            });
            await Task.WhenAll(tasks).ConfigureAwait(false);

            var revalidated = await _git.GetRepositorySnapshotAsync(
                request.RepositoryPath,
                ct).ConfigureAwait(false);
            if (!snapshot.HasSameTarget(revalidated))
            {
                if (await DeleteStagingAsync(staged, ct).ConfigureAwait(false))
                {
                    _generations.CompleteStaging(
                        snapshot.RepoId,
                        request.RepositoryPath,
                        snapshot.GenerationId);
                }
                _generations.Fail(request.RepositoryPath, snapshot);
                Schedule(request with
                {
                    Branch = revalidated.Branch,
                    HeadCommit = revalidated.HeadCommit,
                    Snapshot = revalidated,
                });
                _logger.LogInformation(
                    "Repository changed during indexing; discarded generation {Generation} and queued the newest target",
                    snapshot.GenerationId);
                return;
            }

            var orderedResults = staged
                .OrderBy(result => result.Target.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (orderedResults.Count != targets.Count)
                throw new InvalidOperationException(
                    "Generation is incomplete and cannot be published.");

            var generation = new RepositoryIndexGeneration(
                snapshot.GenerationId,
                snapshot.RepoId,
                snapshot.Branch,
                snapshot.HeadCommit,
                snapshot.WorkingTreeFingerprint,
                compatibility,
                orderedResults.Select(result => result.Binding).ToList(),
                DateTimeOffset.UtcNow);

            // This is the sole query-visible publication point.
            _generations.Activate(request.RepositoryPath, generation);
            published = true;
            _generations.CompleteStaging(
                snapshot.RepoId,
                request.RepositoryPath,
                snapshot.GenerationId);
            foreach (var result in orderedResults)
            {
                _stateStore.Save(result.Status);
                Publish(key, result.Status);
            }

            await ApplyRetentionAsync(
                snapshot.RepoId,
                request.RetentionDays,
                request.MaxRollingBranches,
                ct).ConfigureAwait(false);
            _logger.LogInformation(
                "Published repository generation {Generation} at {Head}: {Solutions} solutions in {Duration:F1}ms",
                generation.GenerationId,
                Short(snapshot.HeadCommit),
                generation.Solutions.Count,
                totalTimer.Elapsed.TotalMilliseconds);
        }
        catch
        {
            if (!published &&
                await DeleteStagingAsync(staged, ct).ConfigureAwait(false))
            {
                _generations.CompleteStaging(
                    snapshot.RepoId,
                    request.RepositoryPath,
                    snapshot.GenerationId);
            }
            if (!published)
                _generations.Fail(request.RepositoryPath, snapshot);
            throw;
        }
    }

    private async Task<SolutionBuildResult> BuildSolutionAsync(
        string key,
        RollingRepositoryRequest request,
        RepositorySnapshot snapshot,
        IndexCompatibilityFingerprint compatibility,
        IReadOnlyList<RepositoryIndexGeneration> history,
        RollingSolutionTarget target,
        CancellationToken ct)
    {
        var timer = Stopwatch.StartNew();
        var workspace = WorkspaceFor(
            snapshot.RepoId,
            snapshot.Branch,
            target.SolutionId,
            snapshot.GenerationId);

        Publish(key, CreateTransientStatus(
            snapshot,
            target,
            workspace,
            RollingIndexState.Checking));

        if (request.Incremental &&
            string.Equals(
                request.BranchSeedMode,
                "closestCompatible",
                StringComparison.OrdinalIgnoreCase))
        {
            var exactSeed = await FindExactSeedAsync(
                snapshot,
                compatibility,
                history,
                target,
                ct).ConfigureAwait(false);
            if (exactSeed is not null)
            {
                var exactStorageRepoId = SolutionScope.ToStorageRepoId(
                    snapshot.RepoId,
                    target.SolutionId);
                try
                {
                    await AttachSeedAsync(
                        request.RepositoryPath,
                        target,
                        exactStorageRepoId,
                        exactSeed.Solution,
                        ct).ConfigureAwait(false);
                    var forked = await _workspaceManager.ForkWorkspaceAsync(
                        exactStorageRepoId,
                        exactSeed.Solution.WorkspaceId,
                        exactSeed.Solution.OverlayRevision,
                        workspace,
                        ct).ConfigureAwait(false);
                    if (forked.IsFailure)
                        throw new InvalidOperationException(forked.Error.Message);

                    timer.Stop();
                    return CreateResult(
                        snapshot,
                        target,
                        workspace,
                        exactSeed.Solution.BaselineCommit,
                        exactSeed.Solution.OverlayRevision,
                        RollingUpdateStrategy.Reused,
                        affected: false,
                        changedFiles: 0,
                        symbols: 0,
                        timer.Elapsed,
                        exactSeed.Solution.RelevantInputs);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _workspaceManager.DeleteWorkspaceAsync(
                        exactStorageRepoId,
                        workspace,
                        ct).ConfigureAwait(false);
                    _logger.LogWarning(
                        ex,
                        "{Solution}: exact seed fast path failed; continuing with validated fallback",
                        target.RelativePath);
                }
            }
        }

        var impactMap = SolutionImpactMap.Build(
            request.RepositoryPath,
            target.SolutionPath);
        _stateStore.SaveImpactMap(snapshot.RepoId, target.SolutionId, impactMap);
        var targetInputs = await CaptureInputsAsync(
            request.RepositoryPath,
            snapshot,
            impactMap,
            ct).ConfigureAwait(false);

        var decision = await SelectSeedAsync(
            request,
            snapshot,
            compatibility,
            history,
            target,
            impactMap,
            targetInputs,
            ct).ConfigureAwait(false);

        if (!request.Incremental ||
            decision.Selected is null ||
            !decision.UseIncremental)
        {
            return await FullBuildAsync(
                request,
                snapshot,
                target,
                workspace,
                impactMap,
                targetInputs,
                decision.Reason,
                timer,
                ct).ConfigureAwait(false);
        }

        var seed = decision.Selected;
        var storageRepoId = SolutionScope.ToStorageRepoId(
            snapshot.RepoId,
            target.SolutionId);
        try
        {
            await AttachSeedAsync(
                request.RepositoryPath,
                target,
                storageRepoId,
                seed.Solution,
                ct).ConfigureAwait(false);
            var forked = await _workspaceManager.ForkWorkspaceAsync(
                storageRepoId,
                seed.Solution.WorkspaceId,
                seed.Solution.OverlayRevision,
                workspace,
                ct).ConfigureAwait(false);
            if (forked.IsFailure)
                throw new InvalidOperationException(forked.Error.Message);

            IReadOnlyList<FileChange> changes =
                seed.Solution.IndexedCommit == snapshot.HeadCommit &&
                string.Equals(
                    seed.Generation.WorkingTreeFingerprint,
                    snapshot.WorkingTreeFingerprint,
                    StringComparison.Ordinal)
                    ? []
                    : await GetChangesFromSeedAsync(
                        request.RepositoryPath,
                        seed.Solution,
                        targetInputs,
                        ct).ConfigureAwait(false);
            var impact = impactMap.Analyze(changes);

            if (changes.Count == 0 ||
                (!impact.IsAffected && request.SkipUnaffectedSolutions))
            {
                timer.Stop();
                return CreateResult(
                    snapshot,
                    target,
                    workspace,
                    seed.Solution.BaselineCommit,
                    seed.Solution.OverlayRevision,
                    RollingUpdateStrategy.Reused,
                    affected: false,
                    changedFiles: changes.Count,
                    symbols: 0,
                    timer.Elapsed,
                    targetInputs);
            }

            if (changes.Count > request.FullRebuildChangeThreshold)
                throw new IncrementalCompilationException(
                    $"Change threshold exceeded ({changes.Count} files).");

            Publish(key, CreateTransientStatus(
                snapshot,
                target,
                workspace,
                RollingIndexState.Updating));
            var refreshed = await _workspaceManager.RefreshSeededOverlayAsync(
                storageRepoId,
                workspace,
                changes,
                seed.Solution.WorkspaceId,
                ct).ConfigureAwait(false);
            if (refreshed.IsFailure)
                throw new IncrementalCompilationException(refreshed.Error.Message);

            timer.Stop();
            _logger.LogInformation(
                "{Solution}: seeded incremental update, similarity {Similarity:P1}, {Files} changed files, {Symbols} symbols",
                target.RelativePath,
                seed.Similarity,
                changes.Count,
                refreshed.Value.SymbolsUpdated);
            return CreateResult(
                snapshot,
                target,
                workspace,
                seed.Solution.BaselineCommit,
                refreshed.Value.NewOverlayRevision,
                RollingUpdateStrategy.Incremental,
                affected: true,
                changedFiles: changes.Count,
                symbols: refreshed.Value.SymbolsUpdated,
                timer.Elapsed,
                targetInputs);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _workspaceManager.DeleteWorkspaceAsync(
                storageRepoId,
                workspace,
                ct).ConfigureAwait(false);
            _logger.LogWarning(
                ex,
                "{Solution}: seed update failed; rebuilding only this solution",
                target.RelativePath);
            return await FullBuildAsync(
                request,
                snapshot,
                target,
                workspace,
                impactMap,
                targetInputs,
                "strict incremental fallback",
                timer,
                ct).ConfigureAwait(false);
        }
    }

    private async Task<BranchSeedDecision> SelectSeedAsync(
        RollingRepositoryRequest request,
        RepositorySnapshot targetSnapshot,
        IndexCompatibilityFingerprint compatibility,
        IReadOnlyList<RepositoryIndexGeneration> history,
        RollingSolutionTarget target,
        SolutionImpactMap impactMap,
        IReadOnlyList<RelevantInputFingerprint> targetInputs,
        CancellationToken ct)
    {
        if (!string.Equals(
                request.BranchSeedMode,
                "closestCompatible",
                StringComparison.OrdinalIgnoreCase))
        {
            return new BranchSeedDecision(
                target.SolutionId,
                null,
                false,
                "branch seeding is disabled");
        }

        var compatible = history
            .Where(generation => generation.Compatibility == compatibility)
            .Select(generation => (
                Generation: generation,
                Solution: generation.Solutions.FirstOrDefault(
                    solution => solution.SolutionId == target.SolutionId)))
            .Where(candidate => candidate.Solution is not null)
            .Select(candidate => (
                candidate.Generation,
                Solution: candidate.Solution!))
            .ToList();

        var selected = await FindExactSeedAsync(
            targetSnapshot,
            compatibility,
            history,
            target,
            ct).ConfigureAwait(false);
        if (selected is not null)
        {
            return new BranchSeedDecision(
                target.SolutionId,
                selected,
                true,
                "exact target seed");
        }

        var candidates = new List<BranchSeedCandidate>();
        foreach (var candidate in compatible
                     .OrderByDescending(candidate => candidate.Generation.PublishedAt)
                     .Take(Math.Max(1, request.BranchSeedCandidateCount)))
        {
            if (!await SeedOverlayExistsAsync(
                    targetSnapshot.RepoId,
                    target.SolutionId,
                    candidate.Solution.WorkspaceId,
                    ct).ConfigureAwait(false))
                continue;
            var similarity = BranchSeedScoring.WeightedSimilarity(
                targetInputs,
                candidate.Solution.RelevantInputs);
            var relationship = await GetRelationshipAsync(
                request.RepositoryPath,
                candidate.Solution.IndexedCommit,
                targetSnapshot.HeadCommit,
                ct).ConfigureAwait(false);
            var changes = await _git.GetChangedFilesAsync(
                request.RepositoryPath,
                candidate.Solution.IndexedCommit,
                ct).ConfigureAwait(false);
            candidates.Add(new BranchSeedCandidate(
                candidate.Generation,
                candidate.Solution,
                relationship,
                similarity,
                impactMap.CountChangedProjects(changes)));
        }

        var best = BranchSeedScoring.SelectBest(candidates);
        if (best is null)
            return new BranchSeedDecision(
                target.SolutionId,
                null,
                false,
                "no compatible seed");
        if (best.Similarity < request.BranchSeedMinimumSimilarity)
            return new BranchSeedDecision(
                target.SolutionId,
                best,
                false,
                $"best seed similarity {best.Similarity:F3} is below threshold");
        return new BranchSeedDecision(
            target.SolutionId,
            best,
            true,
            $"compatible seed similarity {best.Similarity:F3}");
    }

    private async Task<BranchSeedCandidate?> FindExactSeedAsync(
        RepositorySnapshot targetSnapshot,
        IndexCompatibilityFingerprint compatibility,
        IReadOnlyList<RepositoryIndexGeneration> history,
        RollingSolutionTarget target,
        CancellationToken ct)
    {
        var exact = history
            .Where(generation =>
                generation.Compatibility == compatibility &&
                generation.HeadCommit == targetSnapshot.HeadCommit &&
                string.Equals(
                    generation.WorkingTreeFingerprint,
                    targetSnapshot.WorkingTreeFingerprint,
                    StringComparison.Ordinal))
            .Select(generation => (
                Generation: generation,
                Solution: generation.Solutions.FirstOrDefault(
                    solution => solution.SolutionId == target.SolutionId)))
            .Where(candidate => candidate.Solution is not null)
            .OrderByDescending(candidate => candidate.Generation.PublishedAt);
        foreach (var candidate in exact)
        {
            if (!await SeedOverlayExistsAsync(
                    targetSnapshot.RepoId,
                    target.SolutionId,
                    candidate.Solution!.WorkspaceId,
                    ct).ConfigureAwait(false))
                continue;
            return new BranchSeedCandidate(
                candidate.Generation,
                candidate.Solution!,
                BranchSeedRelationship.Identical,
                1,
                0);
        }

        return null;
    }

    private async Task<SolutionBuildResult> FullBuildAsync(
        RollingRepositoryRequest request,
        RepositorySnapshot snapshot,
        RollingSolutionTarget target,
        WorkspaceId workspace,
        SolutionImpactMap impactMap,
        IReadOnlyList<RelevantInputFingerprint> targetInputs,
        string reason,
        Stopwatch timer,
        CancellationToken ct)
    {
        _logger.LogInformation(
            "Full rebuild for {Solution}: {Reason}",
            target.RelativePath,
            reason);
        var result = await _indexHandler.HandleAsync(new JsonObject
        {
            ["repo_path"] = request.RepositoryPath,
            ["solution_path"] = target.SolutionPath,
            ["commit_sha"] = BaselineFor(snapshot).Value,
        }, ct).ConfigureAwait(false);
        if (result.IsError)
            throw new InvalidOperationException(
                "Full rebuild failed: " +
                Sanitize(result.Content, request.RepositoryPath));

        var storageRepoId = SolutionScope.ToStorageRepoId(
            snapshot.RepoId,
            target.SolutionId);
        var created = await _workspaceManager.CreateWorkspaceAsync(
            storageRepoId,
            workspace,
            BaselineFor(snapshot),
            target.SolutionPath,
            request.RepositoryPath,
            ct).ConfigureAwait(false);
        if (created.IsFailure)
            throw new InvalidOperationException(created.Error.Message);

        timer.Stop();
        return CreateResult(
            snapshot,
            target,
            workspace,
            BaselineFor(snapshot),
            created.Value.CurrentRevision,
            RollingUpdateStrategy.FullRebuild,
            affected: true,
            changedFiles: 0,
            symbols: 0,
            timer.Elapsed,
            targetInputs);
    }

    private async Task AttachSeedAsync(
        string repoPath,
        RollingSolutionTarget target,
        RepoId storageRepoId,
        SolutionGenerationBinding seed,
        CancellationToken ct)
    {
        var existing = _workspaceManager.GetWorkspaceInfo(
            storageRepoId,
            seed.WorkspaceId);
        if (existing is not null)
        {
            if (existing.CurrentRevision != seed.OverlayRevision)
                throw new InvalidOperationException(
                    "Seed overlay revision no longer matches its generation.");
            return;
        }

        var attached = await _workspaceManager.CreateWorkspaceAsync(
            storageRepoId,
            seed.WorkspaceId,
            seed.BaselineCommit,
            target.SolutionPath,
            repoPath,
            ct).ConfigureAwait(false);
        if (attached.IsFailure)
            throw new InvalidOperationException(attached.Error.Message);
        if (attached.Value.CurrentRevision != seed.OverlayRevision)
            throw new InvalidOperationException(
                "Seed overlay revision no longer matches its generation.");
    }

    private async Task<bool> SeedOverlayExistsAsync(
        RepoId repoId,
        SolutionId solutionId,
        WorkspaceId workspaceId,
        CancellationToken ct) =>
        await _overlayStore.OverlayExistsAsync(
            SolutionScope.ToStorageRepoId(repoId, solutionId),
            workspaceId,
            ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<RelevantInputFingerprint>> CaptureInputsAsync(
        string repoPath,
        RepositorySnapshot snapshot,
        SolutionImpactMap impactMap,
        CancellationToken ct)
    {
        var weighted = impactMap.GetWeightedInputs();
        var hashes = await _git.GetInputFingerprintsAsync(
            repoPath,
            snapshot,
            weighted.Keys.ToList(),
            ct).ConfigureAwait(false);
        return weighted
            .OrderBy(input => input.Key, StringComparer.OrdinalIgnoreCase)
            .Select(input => new RelevantInputFingerprint(
                input.Key,
                hashes.GetValueOrDefault(input.Key, "missing"),
                input.Value))
            .ToList();
    }

    private async Task<IReadOnlyList<FileChange>> GetChangesFromSeedAsync(
        string repoPath,
        SolutionGenerationBinding seed,
        IReadOnlyList<RelevantInputFingerprint> targetInputs,
        CancellationToken ct)
    {
        var changes = (await _git.GetChangedFilesAsync(
                repoPath,
                seed.IndexedCommit,
                ct).ConfigureAwait(false))
            .ToDictionary(
                change => change.FilePath.Value,
                StringComparer.OrdinalIgnoreCase);
        var before = seed.RelevantInputs.ToDictionary(
            input => input.Path,
            StringComparer.OrdinalIgnoreCase);
        var after = targetInputs.ToDictionary(
            input => input.Path,
            StringComparer.OrdinalIgnoreCase);
        foreach (var path in before.Keys
                     .Concat(after.Keys)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            before.TryGetValue(path, out var oldInput);
            after.TryGetValue(path, out var newInput);
            if (oldInput is not null &&
                newInput is not null &&
                string.Equals(
                    oldInput.ContentHash,
                    newInput.ContentHash,
                    StringComparison.Ordinal))
                continue;

            var kind = oldInput is null || oldInput.ContentHash == "missing"
                ? FileChangeKind.Added
                : newInput is null || newInput.ContentHash == "missing"
                    ? FileChangeKind.Deleted
                    : FileChangeKind.Modified;
            changes[path] = new FileChange(FilePath.From(path), kind);
        }
        return changes.Values.ToList();
    }

    private async Task<BranchSeedRelationship> GetRelationshipAsync(
        string repoPath,
        CommitSha candidate,
        CommitSha target,
        CancellationToken ct)
    {
        if (candidate == target)
            return BranchSeedRelationship.Identical;
        if (await _git.IsAncestorAsync(repoPath, candidate, target, ct)
                .ConfigureAwait(false))
            return BranchSeedRelationship.Ancestor;
        return await _git.FindMergeBaseAsync(repoPath, candidate, target, ct)
                   .ConfigureAwait(false) is not null
            ? BranchSeedRelationship.MergeBase
            : BranchSeedRelationship.Divergent;
    }

    private IndexCompatibilityFingerprint GetCompatibilityFingerprint()
    {
        MsBuildInitializer.EnsureRegistered(_runtime.MsBuildPath);
        var instance = MsBuildInitializer.SelectedInstance;
        var msBuildSource = instance is null
            ? "host-registered"
            : $"{instance.Version}|{instance.Kind}|{Normalize(instance.MSBuildPath)}";
        var msBuildHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(msBuildSource)));
        return new IndexCompatibilityFingerprint(
            "engine-2.0",
            typeof(IncrementalCompiler).Assembly.GetName().Version?.ToString() ?? "unknown",
            msBuildHash);
    }

    private static SolutionBuildResult CreateResult(
        RepositorySnapshot snapshot,
        RollingSolutionTarget target,
        WorkspaceId workspace,
        CommitSha baseline,
        int revision,
        RollingUpdateStrategy strategy,
        bool affected,
        int changedFiles,
        int symbols,
        TimeSpan elapsed,
        IReadOnlyList<RelevantInputFingerprint> inputs)
    {
        var binding = new SolutionGenerationBinding(
            target.SolutionId,
            target.RelativePath,
            workspace,
            snapshot.HeadCommit,
            baseline,
            revision,
            strategy,
            inputs);
        var status = new RollingSolutionStatus(
            snapshot.RepoId,
            target.SolutionId,
            target.RelativePath,
            snapshot.Branch,
            snapshot.HeadCommit,
            snapshot.HeadCommit,
            baseline,
            workspace,
            revision,
            RollingIndexState.UpToDate,
            false,
            affected,
            changedFiles,
            symbols,
            strategy,
            0,
            elapsed.TotalMilliseconds,
            DateTimeOffset.UtcNow,
            null);
        return new SolutionBuildResult(target, binding, status);
    }

    private static RollingSolutionStatus CreateTransientStatus(
        RepositorySnapshot snapshot,
        RollingSolutionTarget target,
        WorkspaceId workspace,
        RollingIndexState state) =>
        new(
            snapshot.RepoId,
            target.SolutionId,
            target.RelativePath,
            snapshot.Branch,
            snapshot.HeadCommit,
            snapshot.HeadCommit,
            snapshot.HeadCommit,
            workspace,
            0,
            state,
            true,
            null,
            0,
            0,
            RollingUpdateStrategy.Reused,
            0,
            0,
            DateTimeOffset.UtcNow,
            null);

    private async Task RecoverIncompleteStagingAsync(
        RepoId repoId,
        string repositoryPath,
        CancellationToken ct)
    {
        var incomplete = _generations.LoadStaging(repoId, repositoryPath);
        if (incomplete is null) return;

        var active = _generations.GetActive(repositoryPath);
        if (!string.Equals(
                active?.GenerationId,
                incomplete.GenerationId,
                StringComparison.Ordinal))
        {
            var cleanupComplete = true;
            foreach (var binding in incomplete.Workspaces)
            {
                try
                {
                    await _workspaceManager.DeleteWorkspaceAsync(
                        SolutionScope.ToStorageRepoId(repoId, binding.SolutionId),
                        binding.WorkspaceId,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    cleanupComplete = false;
                    _logger.LogWarning(
                        ex,
                        "Could not clean workspace from incomplete generation {Generation}",
                        incomplete.GenerationId);
                }
            }

            if (!cleanupComplete)
                throw new IOException(
                    "An incomplete generation could not be cleaned safely.");
        }

        _generations.CompleteStaging(
            repoId,
            repositoryPath,
            incomplete.GenerationId);
    }

    private async Task<bool> DeleteStagingAsync(
        IEnumerable<SolutionBuildResult> staged,
        CancellationToken ct)
    {
        var cleanupComplete = true;
        foreach (var result in staged)
        {
            try
            {
                await _workspaceManager.DeleteWorkspaceAsync(
                    SolutionScope.ToStorageRepoId(
                        result.Status.RepoId,
                        result.Status.SolutionId),
                    result.Status.WorkspaceId,
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cleanupComplete = false;
                _logger.LogWarning(
                    ex,
                    "Could not clean incomplete workspace {Workspace}",
                    result.Status.WorkspaceId.Value);
            }
        }
        return cleanupComplete;
    }

    private async Task ApplyRetentionAsync(
        RepoId repoId,
        int retentionDays,
        int maxBranches,
        CancellationToken ct)
    {
        var removedStates = _stateStore.ApplyRetention(
            repoId,
            retentionDays,
            maxBranches);
        var retained = _stateStore.LoadAll(repoId)
            .Select(state => state.SolutionId.Value + "|" + state.WorkspaceId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var removed in removedStates)
        {
            var key = removed.SolutionId.Value + "|" + removed.WorkspaceId.Value;
            if (retained.Contains(key)) continue;
            await _overlayStore.DeleteOverlayAsync(
                SolutionScope.ToStorageRepoId(repoId, removed.SolutionId),
                removed.WorkspaceId,
                ct).ConfigureAwait(false);
        }
    }

    private void Publish(string key, RollingSolutionStatus state)
    {
        var statuses = _statuses.GetOrAdd(
            key,
            _ => new ConcurrentDictionary<string, RollingSolutionStatus>(
                StringComparer.OrdinalIgnoreCase));
        statuses[state.SolutionId.Value] = state;
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private void RequestPriority(string repoPath, SolutionId solutionId)
    {
        // Query priority no longer changes publication order: independent solutions
        // already run concurrently and only a complete generation is visible.
    }

    private static string Short(CommitSha sha) =>
        sha.Value[..Math.Min(8, sha.Value.Length)];

    private static string SafeRepositoryLabel(string path) =>
        Path.GetFileName(
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar));

    private static string Sanitize(string message, string repoPath) =>
        message.Replace(
            Path.GetFullPath(repoPath),
            "<repository>",
            StringComparison.OrdinalIgnoreCase);

    private static WorkspaceId WorkspaceFor(
        RepoId repoId,
        string branch,
        SolutionId solutionId,
        string generationId) =>
        WorkspaceId.From(
            "rolling-" +
            RollingIndexStateStore.StableId(
                repoId.Value + "\n" +
                branch + "\n" +
                solutionId.Value + "\n" +
                generationId));

    private static CommitSha BaselineFor(RepositorySnapshot snapshot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            snapshot.HeadCommit.Value + "\n" + snapshot.WorkingTreeFingerprint));
        return CommitSha.From(Convert.ToHexStringLower(bytes)[..40]);
    }

    private sealed class QueueState
    {
        public object Gate { get; } = new();
        public RollingRepositoryRequest? Pending { get; set; }
        public Task? Runner { get; set; }
    }

    private sealed record SolutionBuildResult(
        RollingSolutionTarget Target,
        SolutionGenerationBinding Binding,
        RollingSolutionStatus Status);
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
    int FullRebuildChangeThreshold,
    RepositorySnapshot? Snapshot = null,
    string BranchSeedMode = "closestCompatible",
    int BranchSeedCandidateCount = 3,
    double BranchSeedMinimumSimilarity = 0.60,
    bool StrictGenerationPublish = true,
    bool ServePreviousIndexWhileUpdating = false);

public sealed record RollingSolutionTarget(
    string SolutionPath,
    string RelativePath,
    SolutionId SolutionId,
    bool IsDefault);
