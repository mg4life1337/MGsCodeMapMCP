namespace CodeMap.Roslyn;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Roslyn.Extraction;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

/// <summary>
/// Incrementally recompiles only the projects containing changed files
/// and diffs the new symbols against the baseline to produce an <see cref="OverlayDelta"/>.
///
/// Caches the loaded MSBuildWorkspace and Solution between calls.
/// On subsequent calls for the same solution, uses Solution.WithDocumentText to
/// apply file changes incrementally, avoiding the ~1.4s MSBuildWorkspace startup cost.
/// </summary>
public class IncrementalCompiler : IIncrementalCompiler, IDisposable
{
    private readonly SymbolDiffer _differ;
    private readonly ILogger<IncrementalCompiler> _logger;
    private readonly RuntimeActivityTracker? _activity;

    // Workspace caching — protects cached solutions from concurrent callers.
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly Dictionary<string, CacheEntry> _solutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly int _maxCachedSolutions;
    private readonly TimeSpan _cacheIdleTime;
    private readonly TimeProvider _timeProvider;
    private readonly Timer _cacheTrimTimer;
    private CacheEntry? _mostRecent;
    private int _disposed;
    private int _cachedSolutionCount;
    private long _cacheHits;
    private long _cacheMisses;
    private long _cacheEvictions;

    public int CachedSolutions => Volatile.Read(ref _cachedSolutionCount);
    public long CacheHits => Interlocked.Read(ref _cacheHits);
    public long CacheMisses => Interlocked.Read(ref _cacheMisses);
    public long CacheEvictions => Interlocked.Read(ref _cacheEvictions);

    public IncrementalCompiler(
        SymbolDiffer differ,
        ILogger<IncrementalCompiler> logger,
        IndexingResourceConfig? resources = null,
        TimeProvider? timeProvider = null,
        RuntimeActivityTracker? activity = null)
    {
        _differ = differ;
        _logger = logger;
        _activity = activity;
        var effectiveResources = resources ?? new IndexingResourceConfig();
        _maxCachedSolutions = effectiveResources.IncrementalSolutionCacheSize;
        _cacheIdleTime = TimeSpan.FromMinutes(effectiveResources.IncrementalSolutionCacheIdleMinutes);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheTrimTimer = new Timer(TrimIdleCache, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Acquires <c>SemaphoreSlim(1)</c> before entering the core compile path to prevent
    /// concurrent solution mutations on the cached workspace.
    /// Cold path (~1.4s): opens a fresh <c>MSBuildWorkspace</c> and loads the solution.
    /// Warm path (~63ms): reuses cached workspace, applies changed files via
    /// <c>Solution.WithDocumentText</c>, then recompiles only affected projects.
    /// </remarks>
    public async Task<OverlayDelta> ComputeDeltaAsync(
        string solutionPath,
        string repoRootPath,
        IReadOnlyList<FilePath> changedFiles,
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        int currentRevision,
        CancellationToken ct = default)
    {
        if (changedFiles.Count == 0)
        {
            _logger.LogDebug("No changed files — returning empty delta");
            return OverlayDelta.Empty(currentRevision + 1);
        }

        using var activityLease = _activity?.BeginIncrementalUpdate();
        MsBuildInitializer.EnsureRegistered();

        await _lock.WaitAsync(ct);
        try
        {
            return await ComputeDeltaCoreAsync(
                solutionPath, repoRootPath, changedFiles,
                baselineStore, repoId, commitSha, currentRevision, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<OverlayDelta> ComputeDeltaCoreAsync(
        string solutionPath,
        string repoRootPath,
        IReadOnlyList<FilePath> changedFiles,
        ISymbolStore baselineStore,
        RepoId repoId,
        CommitSha commitSha,
        int currentRevision,
        CancellationToken ct)
    {
        var totalTimer = Stopwatch.StartNew();
        TimeSpan solutionOpenTime = TimeSpan.Zero;
        TimeSpan syntaxDiffTime = TimeSpan.Zero;
        TimeSpan apiFingerprintTime = TimeSpan.Zero;
        TimeSpan compilationTime = TimeSpan.Zero;
        TimeSpan dependencyTime = TimeSpan.Zero;
        TimeSpan symbolTime = TimeSpan.Zero;
        TimeSpan referenceTime = TimeSpan.Zero;
        TimeSpan relationTime = TimeSpan.Zero;
        TimeSpan baselineDiffTime = TimeSpan.Zero;

        var canonicalChanges = changedFiles
            .Select(path => FilePath.From(path.Value))
            .Distinct(RepositoryPath.FilePathComparer)
            .ToList();
        var changedAbsolute = canonicalChanges
            .Select(path => NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, path.Value))))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string cacheKey = NormalizePath(Path.GetFullPath(solutionPath));
        bool warm = TryGetCached(cacheKey, out var cacheEntry);
        Solution oldSolution;
        Solution solution;
        bool structuralFallback = false;
        string? fallbackReason = null;

        if (warm)
        {
            oldSolution = cacheEntry!.Solution;
            var oldIndex = BuildDocumentPathIndex(oldSolution);
            structuralFallback = canonicalChanges.Any(path =>
            {
                string absolute = NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, path.Value)));
                string extension = Path.GetExtension(absolute).ToLowerInvariant();
                return IsBuildInput(extension) ||
                    (IsSourceFile(extension) && (!File.Exists(absolute) || !oldIndex.ContainsKey(absolute)));
            });

            if (structuralFallback)
            {
                fallbackReason = "solution-structure-change";
                var openTimer = Stopwatch.StartNew();
                cacheEntry = await OpenSolutionAsync(cacheKey, ct).ConfigureAwait(false);
                solutionOpenTime = openTimer.Elapsed;
                solution = cacheEntry.Solution;
            }
            else
            {
                solution = oldSolution;
                foreach (var changed in canonicalChanges)
                {
                    string absolute = NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, changed.Value)));
                    if (!File.Exists(absolute) || !oldIndex.TryGetValue(absolute, out var documentIds))
                        continue;

                    var text = SourceText.From(
                        await File.ReadAllTextAsync(absolute, ct).ConfigureAwait(false), Encoding.UTF8);
                    foreach (var documentId in documentIds)
                        solution = solution.WithDocumentText(documentId, text);
                }
                cacheEntry!.Solution = solution;

                // Classification is always relative to the persisted baseline, not to
                // the previous refresh. Rolling workspaces are rebuilt from that baseline,
                // so comparing only with the prior HEAD could miss an accumulated API change.
                oldSolution = solution;
                var currentIndex = BuildDocumentPathIndex(solution);
                foreach (var changed in canonicalChanges)
                {
                    string absolute = NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, changed.Value)));
                    string extension = Path.GetExtension(absolute).ToLowerInvariant();
                    if (!IsSourceFile(extension) || !currentIndex.TryGetValue(absolute, out var documentIds))
                        continue;
                    string? baselineContent = await baselineStore
                        .GetFileContentAsync(repoId, commitSha, changed, ct)
                        .ConfigureAwait(false);
                    if (baselineContent is null)
                    {
                        structuralFallback = true;
                        fallbackReason = "baseline-content-unavailable";
                        break;
                    }
                    var baselineText = SourceText.From(baselineContent, Encoding.UTF8);
                    foreach (var documentId in documentIds)
                        oldSolution = oldSolution.WithDocumentText(documentId, baselineText);
                }
            }
        }
        else
        {
            var openTimer = Stopwatch.StartNew();
            cacheEntry = await OpenSolutionAsync(cacheKey, ct).ConfigureAwait(false);
            solutionOpenTime = openTimer.Elapsed;
            solution = cacheEntry.Solution;
            oldSolution = solution;

            var currentIndex = BuildDocumentPathIndex(solution);
            foreach (var changed in canonicalChanges)
            {
                string absolute = NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, changed.Value)));
                string extension = Path.GetExtension(absolute).ToLowerInvariant();
                if (!IsSourceFile(extension) || !File.Exists(absolute) ||
                    !currentIndex.TryGetValue(absolute, out var documentIds))
                {
                    structuralFallback = true;
                    fallbackReason = "cold-structural-or-unmapped-change";
                    continue;
                }

                string? baselineContent = await baselineStore
                    .GetFileContentAsync(repoId, commitSha, changed, ct)
                    .ConfigureAwait(false);
                if (baselineContent is null)
                {
                    structuralFallback = true;
                    fallbackReason = "baseline-content-unavailable";
                    continue;
                }

                var baselineText = SourceText.From(baselineContent, Encoding.UTF8);
                foreach (var documentId in documentIds)
                    oldSolution = oldSolution.WithDocumentText(documentId, baselineText);
            }
        }

        var currentDocumentIndex = BuildDocumentPathIndex(solution);
        var directProjectIds = FindDirectProjectIds(solution, changedAbsolute);
        var semanticChangedAbsolute = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int semanticNoOpFiles = 0;
        IncrementalChangeImpact greatestImpact = IncrementalChangeImpact.NoOp;

        var classificationTimer = Stopwatch.StartNew();
        if (!structuralFallback)
        {
            foreach (var changed in canonicalChanges)
            {
                string absolute = NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, changed.Value)));
                string extension = Path.GetExtension(absolute).ToLowerInvariant();
                if (!IsSourceFile(extension) || !currentDocumentIndex.TryGetValue(absolute, out var documentIds))
                {
                    structuralFallback = true;
                    fallbackReason = "unsupported-or-unmapped-change";
                    break;
                }

                IncrementalChangeImpact fileImpact = IncrementalChangeImpact.NoOp;
                foreach (var documentId in documentIds)
                {
                    var oldDocument = oldSolution.GetDocument(documentId);
                    var newDocument = solution.GetDocument(documentId);
                    if (oldDocument is null || newDocument is null)
                    {
                        fileImpact = IncrementalChangeImpact.Structural;
                        break;
                    }

                    var classification = await IncrementalChangeClassifier
                        .ClassifyAsync(oldDocument, newDocument, ct)
                        .ConfigureAwait(false);
                    syntaxDiffTime += classification.SyntaxSemanticDiff;
                    apiFingerprintTime += classification.ApiFingerprint;
                    if (classification.Impact > fileImpact)
                        fileImpact = classification.Impact;
                }

                if (fileImpact == IncrementalChangeImpact.NoOp)
                    semanticNoOpFiles++;
                else
                    semanticChangedAbsolute.Add(absolute);
                if (fileImpact > greatestImpact)
                    greatestImpact = fileImpact;
            }
        }
        classificationTimer.Stop();

        if (structuralFallback)
            greatestImpact = IncrementalChangeImpact.Structural;

        IncrementalUpdateMode mode = greatestImpact switch
        {
            IncrementalChangeImpact.NoOp => IncrementalUpdateMode.NoOp,
            IncrementalChangeImpact.Body => IncrementalUpdateMode.Document,
            IncrementalChangeImpact.ProjectApi => IncrementalUpdateMode.Project,
            _ => IncrementalUpdateMode.Dependency,
        };

        if (mode == IncrementalUpdateMode.NoOp)
        {
            totalTimer.Stop();
            var metrics = CreateMetrics(
                mode, fallbackReason, canonicalChanges.Count, semanticNoOpFiles, 0, 0, 0, 0, 0,
                solutionOpenTime, classificationTimer.Elapsed, syntaxDiffTime, apiFingerprintTime,
                compilationTime, dependencyTime, symbolTime, referenceTime, relationTime,
                baselineDiffTime, totalTimer.Elapsed);
            LogMetrics(metrics);
            return OverlayDelta.Empty(currentRevision + 1) with { Metrics = metrics };
        }

        var affectedProjectIds = structuralFallback
            ? solution.ProjectIds.ToHashSet()
            : directProjectIds;
        if (affectedProjectIds.Count == 0)
        {
            mode = IncrementalUpdateMode.Dependency;
            fallbackReason = "no-owning-project";
            affectedProjectIds = solution.ProjectIds.ToHashSet();
        }

        if (mode == IncrementalUpdateMode.Dependency)
        {
            var dependencyTimer = Stopwatch.StartNew();
            var graph = solution.GetProjectDependencyGraph();
            foreach (var directProject in affectedProjectIds.ToArray())
                affectedProjectIds.UnionWith(graph.GetProjectsThatTransitivelyDependOnThisProject(directProject));
            dependencyTime = dependencyTimer.Elapsed;
        }

        var reindexedAbsolute = mode == IncrementalUpdateMode.Document
            ? semanticChangedAbsolute
            : affectedProjectIds
                .Select(id => solution.GetProject(id))
                .Where(project => project is not null)
                .SelectMany(project => project!.Documents)
                .Where(document => document.FilePath is not null)
                .Select(document => NormalizePath(document.FilePath!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (structuralFallback)
            reindexedAbsolute.UnionWith(changedAbsolute);

        var affectedGroups = RoslynProjectGrouping.GroupByFilePath(
            affectedProjectIds.Select(id => solution.GetProject(id)!).Where(project => project is not null));
        _logger.LogInformation(
            "Incremental update mode {Mode}: {Projects} project(s), {Documents} document(s), {Changes} changed file(s)",
            mode, affectedProjectIds.Count, reindexedAbsolute.Count, canonicalChanges.Count);

        var baselineSymbols = await baselineStore.GetAllSymbolSummariesAsync(repoId, commitSha, ct)
            .ConfigureAwait(false);
        var allSymbolIds = new HashSet<string>(
            baselineSymbols.Select(symbol => symbol.SymbolId.Value), StringComparer.Ordinal);
        var newSymbols = new List<SymbolCard>();
        var newRefs = new List<ExtractedReference>();
        var newFiles = new List<ExtractedFile>();
        var newFacts = new List<ExtractedFact>();
        var newTypeRelations = new List<ExtractedTypeRelation>();
        var passes = new List<(
            RoslynProjectGrouping.ProjectGroup Group,
            Project Project,
            Compilation Compilation,
            IReadOnlyList<SymbolCard> Symbols,
            IReadOnlyDictionary<string, StableId> StableIds)>();

        foreach (var group in affectedGroups)
        {
            ct.ThrowIfCancellationRequested();
            var project = group.AllProjects[0];
            var compileTimer = Stopwatch.StartNew();
            var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
            compilationTime += compileTimer.Elapsed;
            if (compilation is null)
            {
                _logger.LogWarning("Compilation returned null for project {Project}", group.CanonicalName);
                continue;
            }

            var extractTimer = Stopwatch.StartNew();
            var (projectSymbols, stableIds) = SymbolExtractor.ExtractAllWithStableIds(
                compilation, group.CanonicalName, repoRootPath, reindexedAbsolute);
            symbolTime += extractTimer.Elapsed;
            foreach (var symbol in projectSymbols)
                allSymbolIds.Add(symbol.SymbolId.Value);
            passes.Add((group, project, compilation, projectSymbols, stableIds));
        }

        foreach (var pass in passes)
        {
            var referenceTimer = Stopwatch.StartNew();
            var projectRefs = ReferenceExtractor.ExtractAll(
                pass.Compilation, repoRootPath, pass.StableIds, allSymbolIds,
                includedAbsolutePaths: reindexedAbsolute);
            referenceTime += referenceTimer.Elapsed;

            var filteredSymbols = pass.Symbols.Where(symbol => IsSelected(symbol.FilePath)).ToList();
            var selectedSymbolIds = filteredSymbols
                .Select(symbol => symbol.SymbolId.Value)
                .ToHashSet(StringComparer.Ordinal);

            var relationTimer = Stopwatch.StartNew();
            var relations = TypeRelationExtractor.ExtractAll(
                    pass.Compilation, pass.StableIds, reindexedAbsolute)
                .Where(relation => selectedSymbolIds.Contains(relation.TypeSymbolId.Value));
            newTypeRelations.AddRange(relations);
            relationTime += relationTimer.Elapsed;

            bool isTestProject = pass.Group.CanonicalName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)
                || pass.Group.CanonicalName.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase)
                || pass.Group.CanonicalName.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase);
            var projectFacts = isTestProject
                ? []
                : EndpointExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute)
                    .Concat(ConfigKeyExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(DbTableExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(DiRegistrationExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(MiddlewareExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(RetryPolicyExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(ExceptionExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Concat(LogExtractor.ExtractAll(pass.Compilation, repoRootPath, pass.StableIds, reindexedAbsolute))
                    .Where(fact => IsSelected(fact.FilePath))
                    .ToList();

            newSymbols.AddRange(filteredSymbols);
            newRefs.AddRange(projectRefs.Where(reference => IsSelected(reference.FilePath)));
            newFacts.AddRange(projectFacts);

            foreach (var document in pass.Project.Documents)
            {
                if (document.FilePath is null || !reindexedAbsolute.Contains(NormalizePath(document.FilePath)))
                    continue;
                if (!RepositoryPath.TryCreate(repoRootPath, document.FilePath, out var relativePath))
                {
                    _logger.LogWarning("Ignoring document outside repository root: {Path}", document.FilePath);
                    continue;
                }

                string content = (await document.GetTextAsync(ct).ConfigureAwait(false)).ToString();
                string sha256 = ComputeSha256(content);
                newFiles.Add(new ExtractedFile(
                    sha256[..16], relativePath, sha256, pass.Group.CanonicalName, content));
            }
        }

        newFiles = newFiles
            .DistinctBy(file => file.Path, RepositoryPath.FilePathComparer)
            .ToList();
        var reindexedFilePaths = reindexedAbsolute
            .Select(path => RepositoryPath.TryCreate(repoRootPath, path, out var relative) ? relative : (FilePath?)null)
            .Where(path => path.HasValue)
            .Select(path => path!.Value)
            .Distinct(RepositoryPath.FilePathComparer)
            .ToList();

        var baselineDiffTimer = Stopwatch.StartNew();
        var delta = await _differ.ComputeDeltaAsync(
            baselineStore, repoId, commitSha,
            reindexedFilePaths, newSymbols, newRefs, newFiles,
            currentRevision, ct).ConfigureAwait(false);
        baselineDiffTime = baselineDiffTimer.Elapsed;
        totalTimer.Stop();

        var finalMetrics = CreateMetrics(
            mode, fallbackReason, canonicalChanges.Count, semanticNoOpFiles, reindexedFilePaths.Count,
            affectedProjectIds.Count, delta.AddedOrUpdatedSymbols.Count, delta.DeletedSymbolIds.Count,
            newTypeRelations.Count + newRefs.Count, solutionOpenTime, classificationTimer.Elapsed, syntaxDiffTime,
            apiFingerprintTime, compilationTime, dependencyTime, symbolTime, referenceTime,
            relationTime, baselineDiffTime, totalTimer.Elapsed);
        LogMetrics(finalMetrics);
        return delta with
        {
            Facts = newFacts,
            TypeRelations = newTypeRelations,
            Metrics = finalMetrics,
        };

        bool IsSelected(FilePath path) => reindexedAbsolute.Contains(
            NormalizePath(Path.GetFullPath(Path.Combine(repoRootPath, path.Value))));
    }

    private async Task<CacheEntry> OpenSolutionAsync(string solutionPath, CancellationToken ct)
    {
        _logger.LogInformation("Opening solution for incremental compile: {Path}", solutionPath);
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(args =>
            _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}",
                args.Diagnostic.Kind, args.Diagnostic.Message));
        try
        {
            var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct).ConfigureAwait(false);
            if (_solutionCache.Remove(solutionPath, out var previous))
            {
                _lru.Remove(previous.LruNode);
                previous.Workspace.Dispose();
            }

            var node = _lru.AddFirst(solutionPath);
            var entry = new CacheEntry(workspace, solution, node, _timeProvider.GetUtcNow());
            _solutionCache[solutionPath] = entry;
            Volatile.Write(ref _cachedSolutionCount, _solutionCache.Count);
            _mostRecent = entry;
            EvictOldestSolutions();
            return entry;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    private bool TryGetCached(string solutionPath, out CacheEntry? entry)
    {
        if (!_solutionCache.TryGetValue(solutionPath, out entry))
        {
            Interlocked.Increment(ref _cacheMisses);
            return false;
        }

        Interlocked.Increment(ref _cacheHits);

        _lru.Remove(entry.LruNode);
        _lru.AddFirst(entry.LruNode);
        entry.LastAccess = _timeProvider.GetUtcNow();
        _mostRecent = entry;
        return true;
    }

    private void EvictOldestSolutions()
    {
        while (_solutionCache.Count > _maxCachedSolutions && _lru.Last is { } last)
        {
            string path = last.Value;
            _lru.RemoveLast();
            if (!_solutionCache.Remove(path, out var entry))
                continue;
            entry.Workspace.Dispose();
            Interlocked.Increment(ref _cacheEvictions);
            Volatile.Write(ref _cachedSolutionCount, _solutionCache.Count);
            if (ReferenceEquals(_mostRecent, entry))
                _mostRecent = null;
        }
    }

    private void TrimIdleCache(object? state)
    {
        if (!_lock.Wait(0)) return;
        try
        {
            TrimIdleSolutions(_timeProvider.GetUtcNow());
        }
        finally
        {
            _lock.Release();
        }
    }

    internal int TrimIdleSolutions(DateTimeOffset now)
    {
        var removed = 0;
        while (_lru.Last is { } last)
        {
            if (!_solutionCache.TryGetValue(last.Value, out var entry))
            {
                _lru.RemoveLast();
                continue;
            }
            if (now - entry.LastAccess < _cacheIdleTime) break;

            _lru.RemoveLast();
            _solutionCache.Remove(last.Value);
            entry.Workspace.Dispose();
            Interlocked.Increment(ref _cacheEvictions);
            if (ReferenceEquals(_mostRecent, entry)) _mostRecent = null;
            removed++;
        }
        if (removed > 0)
            _logger.LogInformation("Released {Count} idle incremental solution workspace(s)", removed);
        Volatile.Write(ref _cachedSolutionCount, _solutionCache.Count);
        return removed;
    }

    internal int CachedSolutionCount => CachedSolutions;

    /// <summary>
    /// Builds a path → DocumentId index over the entire solution in O(D) time,
    /// so the warm-path can look up changed documents in O(1) rather than O(N×D).
    /// </summary>
    private static Dictionary<string, List<DocumentId>> BuildDocumentPathIndex(Solution solution)
    {
        var index = new Dictionary<string, List<DocumentId>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in solution.Projects)
            foreach (var document in project.Documents)
                if (document.FilePath is not null)
                {
                    string path = NormalizePath(document.FilePath);
                    if (!index.TryGetValue(path, out var ids))
                        index[path] = ids = [];
                    ids.Add(document.Id);
                }
        return index;
    }

    private static HashSet<ProjectId> FindDirectProjectIds(
        Solution solution,
        HashSet<string> changedAbsolute)
    {
        var result = new HashSet<ProjectId>();
        foreach (var project in solution.Projects)
        {
            bool matches = project.FilePath is not null &&
                changedAbsolute.Contains(NormalizePath(project.FilePath));
            matches |= project.Documents.Any(document => document.FilePath is not null &&
                changedAbsolute.Contains(NormalizePath(document.FilePath)));
            matches |= project.AdditionalDocuments.Any(document => document.FilePath is not null &&
                changedAbsolute.Contains(NormalizePath(document.FilePath)));
            matches |= project.AnalyzerConfigDocuments.Any(document => document.FilePath is not null &&
                changedAbsolute.Contains(NormalizePath(document.FilePath)));
            if (matches)
                result.Add(project.Id);
        }
        return result;
    }

    private static bool IsSourceFile(string extension) => extension is ".cs" or ".vb";

    private static bool IsBuildInput(string extension) => extension is
        ".sln" or ".slnx" or ".csproj" or ".vbproj" or ".fsproj" or ".props" or ".targets";

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static string ComputeSha256(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(bytes);
    }

    private static IncrementalUpdateMetrics CreateMetrics(
        IncrementalUpdateMode mode,
        string? fallbackReason,
        int changedFiles,
        int semanticNoOpFiles,
        int documentsReindexed,
        int affectedProjects,
        int symbolsWritten,
        int symbolsDeleted,
        int relationsUpdated,
        TimeSpan solutionOpen,
        TimeSpan changeClassification,
        TimeSpan syntaxSemanticDiff,
        TimeSpan apiFingerprint,
        TimeSpan directCompilation,
        TimeSpan dependencyResolution,
        TimeSpan symbolExtraction,
        TimeSpan referenceExtraction,
        TimeSpan typeRelations,
        TimeSpan baselineOverlayDiff,
        TimeSpan total) => new(
            mode,
            fallbackReason,
            changedFiles,
            semanticNoOpFiles,
            documentsReindexed,
            affectedProjects,
            symbolsWritten,
            symbolsDeleted,
            relationsUpdated,
            new IncrementalUpdateTimings(
                SolutionOpen: solutionOpen,
                ChangeClassification: changeClassification,
                SyntaxSemanticDiff: syntaxSemanticDiff,
                ApiFingerprint: apiFingerprint,
                DirectCompilation: directCompilation,
                DependencyResolution: dependencyResolution,
                SymbolExtraction: symbolExtraction,
                ReferenceExtraction: referenceExtraction,
                TypeRelations: typeRelations,
                BaselineOverlayDiff: baselineOverlayDiff,
                Total: total));

    private void LogMetrics(IncrementalUpdateMetrics metrics) => _logger.LogInformation(
        "Incremental metrics: mode={Mode}, changed={Changed}, noOp={NoOp}, documents={Documents}, " +
        "projects={Projects}, symbolsWritten={Written}, symbolsDeleted={Deleted}, relations={Relations}, " +
        "fallback={Fallback}, totalMs={TotalMs:F1}, compileMs={CompileMs:F1}, extractMs={ExtractMs:F1}",
        metrics.Mode,
        metrics.ChangedFiles,
        metrics.SemanticNoOpFiles,
        metrics.DocumentsReindexed,
        metrics.AffectedProjects,
        metrics.SymbolsWritten,
        metrics.SymbolsDeleted,
        metrics.RelationsUpdated,
        metrics.FallbackReason,
        metrics.Timings.Total.TotalMilliseconds,
        metrics.Timings.DirectCompilation.TotalMilliseconds,
        (metrics.Timings.SymbolExtraction + metrics.Timings.ReferenceExtraction).TotalMilliseconds);

    /// <summary>
    /// Returns the <see cref="Compilation"/> from the first successfully compiled
    /// project in the cached solution, or <c>null</c> if no solution has been
    /// loaded yet. Used by <see cref="MetadataResolver"/> to access DLL metadata
    /// without triggering a fresh MSBuildWorkspace open.
    /// </summary>
    internal async Task<Compilation?> GetMetadataCompilationAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entry = _mostRecent;
            var solution = entry?.Solution;
            if (solution is null) return null;
            entry!.LastAccess = _timeProvider.GetUtcNow();

            foreach (var project in solution.Projects)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var compilation = await project.GetCompilationAsync(ct).ConfigureAwait(false);
                    if (compilation is not null) return compilation;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GetCompilationAsync failed for project {Project} — trying next", project.Name);
                }
            }

            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Releases the <c>SemaphoreSlim</c> and disposes the cached <c>MSBuildWorkspace</c>.
    /// Must be called when the singleton is torn down (daemon shutdown).
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cacheTrimTimer.Dispose();
        _lock.Wait();
        try
        {
            foreach (var entry in _solutionCache.Values)
                entry.Workspace.Dispose();
            _solutionCache.Clear();
            Volatile.Write(ref _cachedSolutionCount, 0);
            _lru.Clear();
            _mostRecent = null;
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }

    private sealed class CacheEntry(
        MSBuildWorkspace workspace,
        Solution solution,
        LinkedListNode<string> lruNode,
        DateTimeOffset lastAccess)
    {
        public MSBuildWorkspace Workspace { get; } = workspace;
        public Solution Solution { get; set; } = solution;
        public LinkedListNode<string> LruNode { get; } = lruNode;
        public DateTimeOffset LastAccess { get; set; } = lastAccess;
    }
}
