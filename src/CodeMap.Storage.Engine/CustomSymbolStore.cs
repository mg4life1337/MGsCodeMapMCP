namespace CodeMap.Storage.Engine;

using CodeMap.Core.Enums;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// ISymbolStore adapter for the v2 custom storage engine.
/// Phase 3: CreateBaselineAsync + BaselineExistsAsync.
/// Phase 4: All read-only query methods implemented.
/// Registered in DI when CODEMAP_ENGINE=custom.
/// </summary>
public sealed class CustomSymbolStore :
    ISymbolStore,
    IStorageReaderCache,
    ILegacyBaselineAliasRegistry,
    IDisposable
{
    private readonly EngineBaselineBuilder _builder;
    private readonly string _storeBaseDir;
    private readonly IndexingResourceConfig _resources;
    private readonly Dictionary<string, BaselineCacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OverlayCacheEntry> _overlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _legacyBaselineAliases = new(StringComparer.Ordinal);
    private readonly object _cacheLock = new();
    private readonly Timer _readerSweep;
    private bool _disposed;

    public int OpenBaselineCount { get { lock (_cacheLock) return _cache.Count; } }
    public int OpenOverlayCount { get { lock (_cacheLock) return _overlays.Count; } }
    public int OpenBaselineReaderCount => OpenBaselineCount;
    public int OpenOverlayReaderCount => OpenOverlayCount;

    public void RegisterLegacyBaselineAlias(RepoId storageRepoId, RepoId legacyStorageRepoId)
    {
        lock (_cacheLock)
            _legacyBaselineAliases[storageRepoId.Value] = legacyStorageRepoId.Value;
    }

    public bool TryGetLegacyBaselineAlias(
        RepoId storageRepoId,
        out RepoId legacyStorageRepoId)
    {
        lock (_cacheLock)
        {
            if (_legacyBaselineAliases.TryGetValue(storageRepoId.Value, out var value))
            {
                legacyStorageRepoId = RepoId.From(value);
                return true;
            }
        }
        legacyStorageRepoId = default;
        return false;
    }

    public CustomSymbolStore(string storeBaseDir, IndexingResourceConfig? resources = null)
    {
        _storeBaseDir = storeBaseDir;
        _resources = resources ?? new IndexingResourceConfig();
        _builder = new EngineBaselineBuilder(storeBaseDir);
        var sweepSeconds = Math.Max(1, Math.Min(
            _resources.StorageReaderIdleSeconds,
            Math.Max(1, _resources.StorageReaderIdleSeconds / 2)));
        _readerSweep = new Timer(
            static state => ((CustomSymbolStore)state!).TrimIdleReaders(),
            this,
            TimeSpan.FromSeconds(sweepSeconds),
            TimeSpan.FromSeconds(sweepSeconds));
    }

    // ── Baseline lifecycle ───────────────────────────────────────────────────

    public async Task CreateBaselineAsync(RepoId repoId, CommitSha commitSha, CompilationResult data, string repoRootPath = "", CancellationToken ct = default)
    {
        var hasSolution = SolutionScope.TryParse(repoId, out _, out var solutionId);
        var solutionPath = hasSolution && data.SourcePath is not null && !string.IsNullOrWhiteSpace(repoRootPath)
            ? SolutionId.GetRepositoryRelativePath(repoRootPath, data.SourcePath)
            : null;
        var input = new BaselineBuildInput(
            CommitSha: commitSha.Value,
            RepoRootPath: repoRootPath,
            Symbols: data.Symbols,
            Files: data.Files,
            References: data.References,
            Facts: data.Facts ?? [],
            TypeRelations: data.TypeRelations ?? [],
            ProjectDiagnostics: data.Stats.ProjectDiagnostics,
            SolutionId: hasSolution ? solutionId.Value : null,
            SolutionPath: solutionPath);

        // Builder places baselines at <storeDir>/baselines/<commitSha>/
        // We scope storeDir by repoId so final path = <baseDir>/<repoId>/baselines/<commitSha>/
        var result = await new EngineBaselineBuilder(RepoStoreDir(repoId.Value)).BuildAsync(input, ct);
        if (!result.Success)
            throw new StorageWriteException($"Baseline build failed: {result.ErrorMessage}");

        // Invalidate cache for this commit
        var cacheKey = CacheKey(repoId.Value, commitSha.Value);
        lock (_cacheLock)
        {
            if (_cache.Remove(cacheKey, out var old))
                RetireBaselineUnsafe(old);
        }
    }

    public Task<bool> BaselineExistsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = ResolveBaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var exists = File.Exists(manifestPath) && ManifestWriter.Read(manifestPath) != null;
        return Task.FromResult(exists);
    }

    // ── Symbol queries ───────────────────────────────────────────────────────

    public Task<SymbolCard?> GetSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        SymbolRecord? rec;

        // sym_ prefix → StableId lookup
        if (symbolId.Value.StartsWith("sym_", StringComparison.Ordinal))
            rec = merged.GetSymbolByStableId(symbolId.Value);
        else
            rec = merged.GetSymbolByFqn(symbolId.Value);

        if (rec == null) return Task.FromResult<SymbolCard?>(null);
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader));
    }

    public Task<SymbolCard?> GetSymbolByStableIdAsync(RepoId repoId, CommitSha commitSha, StableId stableId, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var rec = merged.GetSymbolByStableId(stableId.Value);
        if (rec == null) return Task.FromResult<SymbolCard?>(null);
        return Task.FromResult<SymbolCard?>(RecordToCoreMappings.ToSymbolCard(rec.Value, reader));
    }

    public Task<IReadOnlyList<SymbolSearchHit>> SearchSymbolsAsync(RepoId repoId, CommitSha commitSha, string query, SymbolSearchFilters? filters, int limit, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);

        // Single-kind filter is forwarded into the engine fast path. Multi-kind is
        // post-filtered below (the engine filter struct holds only one kind).
        var filter = new SymbolSearchFilter(
            Kind: filters?.Kinds?.Count == 1 ? RecordMappers.MapSymbolKind(filters.Kinds[0]) : null,
            NamespacePrefix: string.IsNullOrEmpty(filters?.Namespace) ? null : filters.Namespace,
            FilePathPrefix: string.IsNullOrEmpty(filters?.FilePath) ? null : filters.FilePath,
            ProjectName: string.IsNullOrEmpty(filters?.ProjectName) ? null : filters.ProjectName,
            Limit: limit);

        var results = merged.SearchSymbols(query, filter);
        var hits = new List<SymbolSearchHit>(results.Length);
        foreach (var r in results)
        {
            if (filters?.Kinds is { Count: > 1 } multiKinds)
            {
                var symKind = RecordToCoreMappings.ReverseSymbolKind(r.Symbol.Kind);
                if (!multiKinds.Contains(symKind)) continue;
            }
            hits.Add(RecordToCoreMappings.ToSearchHit(r.Symbol, reader, r.Score));
        }

        return Task.FromResult<IReadOnlyList<SymbolSearchHit>>(hits);
    }

    public Task<IReadOnlyList<SymbolSearchHit>> GetSymbolsByKindsAsync(
        RepoId repoId, CommitSha commitSha, IReadOnlyList<SymbolKind>? kinds, int limit,
        CancellationToken ct = default, SymbolSearchFilters? filters = null)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var kindFilter = kinds?.Count == 1 ? RecordMappers.MapSymbolKind(kinds[0]) : (short?)null;
        var symbols = merged.EnumerateSymbols(kindFilter);

        // BUG-1 fix: apply namespace / file_path / project_name filters that
        // pre-fix were dropped on the floor for the no-query browse path.
        // Predicates mirror SearchIndexReader.cs:106-128 so behavior matches
        // the with-query path. Kinds is taken from the explicit `kinds` param,
        // not filters.Kinds.
        var nsFilter = filters?.Namespace;
        var filePathFilter = filters?.FilePath;
        var projectNameFilter = filters?.ProjectName;

        var hits = new List<SymbolSearchHit>();
        foreach (var sym in symbols)
        {
            if (kinds != null && kinds.Count > 1)
            {
                var symKind = RecordToCoreMappings.ReverseSymbolKind(sym.Kind);
                if (!kinds.Contains(symKind)) continue;
            }

            if (!string.IsNullOrEmpty(nsFilter))
            {
                var ns = sym.NamespaceStringId > 0 ? reader.ResolveString(sym.NamespaceStringId) : "";
                if (!ns.StartsWith(nsFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }

            if (!string.IsNullOrEmpty(filePathFilter))
            {
                if (sym.FileIntId < 1 || sym.FileIntId > reader.FileCount) continue;
                ref readonly var file = ref reader.GetFileByIntId(sym.FileIntId);
                var path = file.PathStringId > 0 ? reader.ResolveString(file.PathStringId) : "";
                if (!path.StartsWith(filePathFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }

            if (!string.IsNullOrEmpty(projectNameFilter))
            {
                if (sym.ProjectIntId < 1 || sym.ProjectIntId > reader.ProjectCount) continue;
                ref readonly var proj = ref reader.GetProjectByIntId(sym.ProjectIntId);
                var name = proj.NameStringId > 0 ? reader.ResolveString(proj.NameStringId) : "";
                if (!string.Equals(name, projectNameFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }

            hits.Add(RecordToCoreMappings.ToSearchHit(sym, reader, 0));
            if (hits.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<SymbolSearchHit>>(hits);
    }

    public Task<IReadOnlyList<SymbolCard>> GetSymbolsByFileAsync(RepoId repoId, CommitSha commitSha, FilePath filePath, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var records = merged.GetSymbolsByFile(filePath.Value);
        var cards = new List<SymbolCard>(records.Count);
        foreach (var r in records)
            cards.Add(RecordToCoreMappings.ToSymbolCard(r, reader));
        return Task.FromResult<IReadOnlyList<SymbolCard>>(cards);
    }

    // ── Reference queries ────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredReference>> GetReferencesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default, ResolutionState? resolutionState = null)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredReference>>([]);

        var filter = new EdgeFilter(
            EdgeKind: kind.HasValue ? RecordMappers.MapEdgeKind(kind.Value) : null,
            ResolvedOnly: resolutionState == ResolutionState.Resolved);

        var edges = merged.GetIncomingEdges(intId, filter);
        var refs = new List<StoredReference>(Math.Min(edges.Count, limit));
        foreach (var e in edges)
        {
            if (resolutionState == ResolutionState.Unresolved && e.ResolutionState != 1) continue;
            refs.Add(RecordToCoreMappings.ToStoredReference(e, reader));
            if (refs.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<StoredReference>>(refs);
    }

    public Task<IReadOnlyList<StoredOutgoingReference>> GetOutgoingReferencesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, RefKind? kind, int limit, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>([]);

        var filter = new EdgeFilter(EdgeKind: kind.HasValue ? RecordMappers.MapEdgeKind(kind.Value) : null);
        var edges = merged.GetOutgoingEdges(intId, filter);
        var refs = new List<StoredOutgoingReference>(Math.Min(edges.Count, limit));
        foreach (var e in edges)
        {
            refs.Add(RecordToCoreMappings.ToOutgoingReference(e, reader));
            if (refs.Count >= limit) break;
        }

        return Task.FromResult<IReadOnlyList<StoredOutgoingReference>>(refs);
    }

    // ── Type hierarchy ───────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredTypeRelation>> GetTypeRelationsAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

        // Outgoing Inherits(4) + Implements(5) edges = base types
        var edges = merged.GetOutgoingEdges(intId);
        var relations = new List<StoredTypeRelation>();
        foreach (var e in edges)
        {
            if (e.EdgeKind is not (4 or 5)) continue;

            string targetFqn;
            string targetDisplay;
            if (e.ToSymbolIntId > 0 && e.ToSymbolIntId <= reader.SymbolCount)
            {
                // Target is in the symbols table (local type)
                ref readonly var target = ref reader.GetSymbolByIntId(e.ToSymbolIntId);
                targetFqn = reader.ResolveString(target.FqnStringId);
                targetDisplay = reader.ResolveString(target.DisplayNameStringId);
            }
            else if (e.ToNameStringId > 0)
            {
                // Target is external (framework/DLL type) — stored as doc-comment FQN
                var storedName = reader.ResolveString(e.ToNameStringId);
                targetFqn = storedName;
                // Extract display name from FQN: "T:System.Windows.Forms.Form" → "Form"
                var lastDot = storedName.LastIndexOf('.');
                targetDisplay = lastDot >= 0 ? storedName[(lastDot + 1)..] : storedName;
                if (targetDisplay.Length > 2 && targetDisplay[1] == ':')
                    targetDisplay = targetDisplay[2..];
            }
            else
            {
                continue; // No target info at all
            }

            var relKind = e.EdgeKind == 4 ? TypeRelationKind.BaseType : TypeRelationKind.Interface;
            relations.Add(new StoredTypeRelation(symbolId, SymbolId.From(targetFqn), relKind, targetDisplay));
        }

        return Task.FromResult<IReadOnlyList<StoredTypeRelation>>(relations);
    }

    public Task<IReadOnlyList<StoredTypeRelation>> GetDerivedTypesAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredTypeRelation>>([]);

        // Incoming Inherits(4) + Implements(5) edges = derived types
        var edges = merged.GetIncomingEdges(intId);
        var relations = new List<StoredTypeRelation>();
        foreach (var e in edges)
        {
            if (e.EdgeKind is not (4 or 5)) continue;
            if (e.FromSymbolIntId <= 0 || e.FromSymbolIntId > reader.SymbolCount) continue;
            ref readonly var source = ref reader.GetSymbolByIntId(e.FromSymbolIntId);
            var sourceFqn = reader.ResolveString(source.FqnStringId);
            var sourceDisplay = reader.ResolveString(source.DisplayNameStringId);
            var relKind = e.EdgeKind == 4 ? TypeRelationKind.BaseType : TypeRelationKind.Interface;
            relations.Add(new StoredTypeRelation(SymbolId.From(sourceFqn), symbolId, relKind, sourceDisplay));
        }

        return Task.FromResult<IReadOnlyList<StoredTypeRelation>>(relations);
    }

    // ── Fact queries ─────────────────────────────────────────────────────────

    public Task<IReadOnlyList<StoredFact>> GetFactsByKindAsync(RepoId repoId, CommitSha commitSha, FactKind kind, int limit, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var records = merged.GetFactsByKind((int)kind);

        // Convert all matching facts, then sort by (FilePath, LineStart) for deterministic
        // ordering that matches SQLite's rowid-based insertion order.
        var allFacts = new List<StoredFact>(records.Count);
        foreach (var r in records)
            allFacts.Add(RecordToCoreMappings.ToStoredFact(r, reader));

        // Sort by Value to match SQLite's ORDER BY f.value
        allFacts.Sort((a, b) => string.Compare(a.Value, b.Value, StringComparison.Ordinal));

        IReadOnlyList<StoredFact> result = allFacts.Count <= limit ? allFacts : allFacts.GetRange(0, limit);
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<StoredFact>> GetFactsForSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var intId = ResolveSymbolIntId(merged, symbolId.Value);
        if (intId == 0) return Task.FromResult<IReadOnlyList<StoredFact>>([]);

        var records = merged.GetFactsBySymbol(intId);
        var facts = new List<StoredFact>(records.Count);
        foreach (var r in records)
            facts.Add(RecordToCoreMappings.ToStoredFact(r, reader));
        return Task.FromResult<IReadOnlyList<StoredFact>>(facts);
    }

    // ── File queries ─────────────────────────────────────────────────────────

    public Task<FileSpan?> GetFileSpanAsync(RepoId repoId, CommitSha commitSha, FilePath filePath, int startLine, int endLine, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var file = merged.GetFileByPath(filePath.Value);
        if (file == null) return Task.FromResult<FileSpan?>(null);

        string content;
        if (file.Value.ContentId > 0)
            content = reader.ResolveContent(file.Value.ContentId);
        else
            return Task.FromResult<FileSpan?>(null); // No content stored, no disk fallback in Phase 4

        var lines = content.Split('\n');
        var totalLines = lines.Length;
        var start = Math.Max(0, Math.Min(startLine - 1, totalLines));
        var end = Math.Max(start, Math.Min(totalLines, endLine));
        var spanLines = lines[start..end];
        var spanContent = string.Join('\n', spanLines);

        return Task.FromResult<FileSpan?>(new FileSpan(filePath, startLine, endLine, totalLines, spanContent, end < endLine));
    }

    public Task<IReadOnlyList<FilePath>> GetAllFilePathsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var paths = new List<FilePath>();
        foreach (var p in merged.EnumerateFilePaths())
            paths.Add(FilePath.From(p));
        return Task.FromResult<IReadOnlyList<FilePath>>(paths);
    }

    public Task<IReadOnlyList<(FilePath Path, string? Content)>> GetAllFileContentsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var result = new List<(FilePath, string?)>();
        foreach (var file in merged.EnumerateFiles())
        {
            var path = reader.ResolveString(file.PathStringId);
            var content = file.ContentId > 0 ? reader.ResolveContent(file.ContentId) : null;
            result.Add((FilePath.From(path), content));
        }
        // Sort by path for deterministic file iteration order (text search results
        // depend on which files are scanned first when a limit is applied).
        result.Sort((a, b) => string.Compare(a.Item1.Value, b.Item1.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult<IReadOnlyList<(FilePath Path, string? Content)>>(result);
    }

    public Task<string?> GetFileContentAsync(
        RepoId repoId,
        CommitSha commitSha,
        FilePath filePath,
        CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var file = merged.GetFileByPath(filePath.Value);
        string? content = file is { ContentId: > 0 }
            ? reader.ResolveContent(file.Value.ContentId)
            : null;
        return Task.FromResult(content);
    }

    // ── Stats/metadata queries ───────────────────────────────────────────────

    public Task<SemanticLevel?> GetSemanticLevelAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = ResolveBaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        if (manifest?.ProjectDiagnostics is not { Count: > 0 } diags)
            return Task.FromResult<SemanticLevel?>(null);

        var compiledCount = diags.Count(d => d.Compiled);
        SemanticLevel level;
        if (compiledCount == diags.Count)
            level = SemanticLevel.Full;
        else if (compiledCount > 0)
            level = SemanticLevel.Partial;
        else
            level = SemanticLevel.SyntaxOnly;

        return Task.FromResult<SemanticLevel?>(level);
    }

    public Task<IReadOnlyList<SymbolSummary>> GetAllSymbolSummariesAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var summaries = new List<SymbolSummary>();
        foreach (var sym in merged.EnumerateSymbols())
            summaries.Add(RecordToCoreMappings.ToSummary(sym, reader));
        return Task.FromResult<IReadOnlyList<SymbolSummary>>(summaries);
    }

    public Task<string?> GetRepoRootAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        var baselineDir = ResolveBaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        return Task.FromResult(manifest?.RepoRootPath);
    }

    public Task<IReadOnlyList<ProjectDiagnostic>> GetProjectDiagnosticsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
    {
        // Read authoritative ProjectDiagnostics stored at build time (matches SQLite behavior).
        // These come from CompilationResult.Stats.ProjectDiagnostics, which Roslyn populates
        // with exact per-project counts during compilation.
        var baselineDir = ResolveBaselineDir(repoId.Value, commitSha.Value);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = ManifestWriter.Read(manifestPath);
        IReadOnlyList<ProjectDiagnostic> diags = manifest?.ProjectDiagnostics ?? [];
        return Task.FromResult(diags);
    }

    public Task<IReadOnlyList<UnresolvedEdge>> GetUnresolvedEdgesAsync(RepoId repoId, CommitSha commitSha, IReadOnlyList<FilePath> filePaths, CancellationToken ct = default)
    {
        using var lease = AcquireBaseline(repoId.Value, commitSha.Value);
        var (reader, merged) = (lease.Reader, lease.Merged);
        var fileIntIds = new HashSet<int>();
        foreach (var fp in filePaths)
        {
            var file = merged.GetFileByPath(fp.Value);
            if (file != null) fileIntIds.Add(file.Value.FileIntId);
        }

        var result = new List<UnresolvedEdge>();
        foreach (var edge in reader.EnumerateEdges())
        {
            if (edge.ResolutionState != 1) continue; // Only unresolved
            if (!fileIntIds.Contains(edge.FileIntId)) continue;

            var fromFqn = edge.FromSymbolIntId > 0 && edge.FromSymbolIntId <= reader.SymbolCount
                ? reader.ResolveString(reader.GetSymbolByIntId(edge.FromSymbolIntId).FqnStringId) : "";
            var toName = edge.ToNameStringId > 0 ? reader.ResolveString(edge.ToNameStringId) : null;
            var filePath = edge.FileIntId > 0 && edge.FileIntId <= reader.FileCount
                ? reader.ResolveString(reader.GetFileByIntId(edge.FileIntId).PathStringId) : "";
            var refKind = RecordToCoreMappings.ReverseEdgeKind(edge.EdgeKind).ToString();

            result.Add(new UnresolvedEdge(fromFqn, toName, null, refKind, filePath, edge.SpanStart, edge.SpanEnd));
        }

        return Task.FromResult<IReadOnlyList<UnresolvedEdge>>(result);
    }

    public Task<string?> GetDllFingerprintAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
        => Task.FromResult<string?>(null); // Not stored in v2 manifest yet

    // ── Overlay-write methods ──────────────────────────────────────────────────

    public async Task UpgradeEdgeAsync(RepoId repoId, CommitSha commitSha, EdgeUpgrade upgrade, CancellationToken ct = default)
    {
        using var lease = AcquireOverlay(commitSha.Value, repoId.Value, commitSha.Value);
        var (reader, merged, overlay) = (lease.Reader, lease.Merged, lease.Overlay);
        var fromIntId = ResolveSymbolIntId(merged, upgrade.FromSymbolId);
        var toIntId = ResolveSymbolIntId(merged, upgrade.ResolvedToSymbolId.Value);
        var file = merged.GetFileByPath(upgrade.FileId);
        var fileIntId = file?.FileIntId ?? 0;

        using var batch = overlay.BeginBatch();
        batch.ResolveEdge(fromIntId, fileIntId, upgrade.LocStart, toIntId);
        await batch.CommitAsync(ct);
    }

    public async Task<int> InsertMetadataStubsAsync(RepoId repoId, CommitSha commitSha, IReadOnlyList<SymbolCard> stubs, IReadOnlyList<ExtractedTypeRelation>? typeRelations = null, CancellationToken ct = default)
    {
        if (stubs.Count == 0) return 0;
        using var lease = AcquireOverlay(commitSha.Value, repoId.Value, commitSha.Value);
        var (reader, overlay) = (lease.Reader, lease.Overlay);

        using var batch = overlay.BeginBatch();
        foreach (var stub in stubs)
        {
            var fqn = stub.SymbolId.Value;
            var stableId = RecordMappers.ComputeDegradedStableId(stub.Kind, fqn, null);
            var stableIdSid = batch.InternString(stableId);
            var fqnSid = batch.InternString(fqn);
            var displayName = stub.FullyQualifiedName.Split('.')[^1];
            var displaySid = batch.InternString(displayName);
            var nsSid = batch.InternString(stub.Namespace ?? "");
            var tokenStr = string.Join(' ', SearchIndexBuilder.Tokenize(fqn, displayName, stub.Namespace));
            var tokensSid = batch.InternString(tokenStr);
            var tokens = tokenStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            var intId = overlay.NextOverlaySymbolIntId--;
            var record = new SymbolRecord(intId, stableIdSid, fqnSid, displaySid, nsSid,
                0, 0, 0, RecordMappers.MapSymbolKind(stub.Kind), RecordMappers.MapAccessibility(stub.Visibility),
                1 << 7, // IsDecompiled flag
                stub.SpanStart, stub.SpanEnd, tokensSid, 0);
            batch.UpsertSymbol(record, tokens);
        }
        await batch.CommitAsync(ct);
        return stubs.Count;
    }

    public Task RebuildFtsAsync(RepoId repoId, CommitSha commitSha, CancellationToken ct = default)
        => Task.CompletedTask; // No FTS5 in v2 — search index is immutable

    public async Task InsertVirtualFileAsync(RepoId repoId, CommitSha commitSha, string virtualPath, string content, IReadOnlyList<ExtractedReference>? decompiledRefs = null, CancellationToken ct = default)
    {
        using var lease = AcquireOverlay(commitSha.Value, repoId.Value, commitSha.Value);
        var overlay = lease.Overlay;

        using var batch = overlay.BeginBatch();
        var pathSid = batch.InternString(virtualPath);
        var normalizedSid = batch.InternString(virtualPath.ToLowerInvariant());
        var fileRecord = new FileRecord(overlay.NextOverlayFileIntId--, pathSid, normalizedSid,
            0, 0, 0, RecordMappers.DetectLanguage(virtualPath), 0, 0);
        batch.UpsertFile(fileRecord);
        await batch.CommitAsync(ct);
    }

    public async Task UpgradeDecompiledSymbolAsync(RepoId repoId, CommitSha commitSha, SymbolId symbolId, string virtualFilePath, CancellationToken ct = default)
    {
        using var lease = AcquireOverlay(commitSha.Value, repoId.Value, commitSha.Value);
        var (reader, merged, overlay) = (lease.Reader, lease.Merged, lease.Overlay);
        var rec = symbolId.Value.StartsWith("sym_", StringComparison.Ordinal)
            ? merged.GetSymbolByStableId(symbolId.Value)
            : merged.GetSymbolByFqn(symbolId.Value);
        if (rec == null) return;

        var sym = rec.Value;
        // Clear IsDecompiled flag (bit 7)
        var updatedFlags = sym.Flags & ~(1 << 7);
        var updated = new SymbolRecord(sym.SymbolIntId, sym.StableIdStringId, sym.FqnStringId,
            sym.DisplayNameStringId, sym.NamespaceStringId, sym.ContainerIntId, sym.FileIntId,
            sym.ProjectIntId, sym.Kind, sym.Accessibility, updatedFlags,
            sym.SpanStart, sym.SpanEnd, sym.NameTokensStringId, sym.SignatureHash);

        var stableId = merged.ResolveString(sym.StableIdStringId);
        var nameTokens = sym.NameTokensStringId > 0
            ? merged.ResolveString(sym.NameTokensStringId).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            : [];

        using var batch = overlay.BeginBatch();
        batch.UpsertSymbol(updated, nameTokens);
        await batch.CommitAsync(ct);
    }

    // ── Internal helpers ─────────────────────────────────────────────────────

    internal BaselineReaderLease AcquireBaseline(string repoId, string commitSha)
    {
        lock (_cacheLock)
        {
            ThrowIfDisposed();
            var entry = AcquireBaselineUnsafe(repoId, commitSha);
            TrimReadersUnsafe(DateTimeOffset.UtcNow);
            return new BaselineReaderLease(this, entry);
        }
    }

    internal OverlayReaderLease AcquireOverlay(string workspaceId, string repoId, string commitSha)
    {
        lock (_cacheLock)
        {
            ThrowIfDisposed();
            if (_overlays.TryGetValue(workspaceId, out var existing))
            {
                // A delete that raced an already active query is completed after
                // the final lease. Reuse that same object for any query which
                // already resolved the workspace instead of opening the same WAL
                // directory a second time.
                existing.LeaseCount++;
                existing.LastAccessUtc = DateTimeOffset.UtcNow;
                existing.Baseline.LastAccessUtc = existing.LastAccessUtc;
                return new OverlayReaderLease(this, existing);
            }

            var baseline = AcquireBaselineUnsafe(repoId, commitSha);
            try
            {
                var overlayDir = Path.Combine(_storeBaseDir, "overlays", workspaceId);
                var overlay = new EngineOverlay(overlayDir, workspaceId, baseline.Reader);
                var entry = new OverlayCacheEntry(workspaceId, overlay, baseline)
                {
                    LeaseCount = 1,
                    LastAccessUtc = DateTimeOffset.UtcNow,
                };
                _overlays[workspaceId] = entry;
                TrimReadersUnsafe(DateTimeOffset.UtcNow);
                return new OverlayReaderLease(this, entry);
            }
            catch
            {
                ReleaseBaselineUnsafe(baseline);
                throw;
            }
        }
    }

    /// <summary>
    /// Compatibility helper for engine tests. Production query paths use a lease for the
    /// complete operation and never retain the returned reader after releasing it.
    /// </summary>
    internal (EngineBaselineReader Reader, EngineMergedReader Merged) GetOrOpenBaseline(
        string repoId,
        string commitSha)
    {
        using var lease = AcquireBaseline(repoId, commitSha);
        return (lease.Reader, lease.Merged);
    }

    private static int ResolveSymbolIntId(EngineMergedReader merged, string symbolId)
    {
        if (symbolId.StartsWith("sym_", StringComparison.Ordinal))
        {
            var rec = merged.GetSymbolByStableId(symbolId);
            return rec?.SymbolIntId ?? 0;
        }
        else
        {
            var rec = merged.GetSymbolByFqn(symbolId);
            return rec?.SymbolIntId ?? 0;
        }
    }

    internal EngineOverlay GetOrCreateOverlay(string workspaceId, EngineBaselineReader reader)
    {
        lock (_cacheLock)
        {
            if (_overlays.TryGetValue(workspaceId, out var existing))
                return existing.Overlay;

            var overlayDir = Path.Combine(_storeBaseDir, "overlays", workspaceId);
            var overlay = new EngineOverlay(overlayDir, workspaceId, reader);
            var baseline = _cache.Values.FirstOrDefault(entry => ReferenceEquals(entry.Reader, reader))
                ?? throw new InvalidOperationException("The baseline reader must be leased before opening an overlay.");
            baseline.LeaseCount++;
            _overlays[workspaceId] = new OverlayCacheEntry(workspaceId, overlay, baseline)
            {
                LastAccessUtc = DateTimeOffset.UtcNow,
            };
            return overlay;
        }
    }

    internal bool OverlayExists(string workspaceId)
    {
        lock (_cacheLock)
        {
            if (_overlays.ContainsKey(workspaceId)) return true;
        }
        var manifestPath = Path.Combine(_storeBaseDir, "overlays", workspaceId, "manifest.json");
        return File.Exists(manifestPath);
    }

    internal EngineOverlay? TryGetOverlay(string workspaceId)
    {
        lock (_cacheLock)
        {
            return _overlays.TryGetValue(workspaceId, out var entry) ? entry.Overlay : null;
        }
    }

    internal void DeleteOverlay(string workspaceId)
    {
        lock (_cacheLock)
        {
            if (_overlays.TryGetValue(workspaceId, out var entry))
            {
                entry.DeletePending = true;
                if (entry.LeaseCount == 0)
                    RemoveOverlayUnsafe(entry, deleteFiles: true);
                return;
            }
            DeleteOverlayFilesUnsafe(workspaceId);
        }
    }

    public void TrimIdleReaders()
    {
        lock (_cacheLock)
        {
            if (_disposed) return;
            TrimReadersUnsafe(DateTimeOffset.UtcNow);
        }
    }

    private BaselineCacheEntry AcquireBaselineUnsafe(string repoId, string commitSha)
    {
        var cacheKey = CacheKey(repoId, commitSha);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            cached.LeaseCount++;
            cached.LastAccessUtc = DateTimeOffset.UtcNow;
            return cached;
        }

        var baselineDir = ResolveBaselineDir(repoId, commitSha);
        if (!File.Exists(Path.Combine(baselineDir, "manifest.json")))
            throw new StorageFormatException($"No baseline found for repo {repoId} commit {commitSha}");

        var reader = new EngineBaselineReader(baselineDir);
        try
        {
            var searchPath = Path.Combine(baselineDir, "search.idx");
            if (File.Exists(searchPath))
                reader.InitSearch(new SearchIndexReader(reader, searchPath));

            var adjOutPath = Path.Combine(baselineDir, "adjacency-out.idx");
            var adjInPath = Path.Combine(baselineDir, "adjacency-in.idx");
            if (File.Exists(adjOutPath) && File.Exists(adjInPath))
                reader.InitAdjacency(new AdjacencyIndexReader(adjOutPath, adjInPath, reader.SymbolCount));

            var entry = new BaselineCacheEntry(
                cacheKey,
                reader,
                new EngineMergedReader(reader))
            {
                LeaseCount = 1,
                LastAccessUtc = DateTimeOffset.UtcNow,
            };
            _cache[cacheKey] = entry;
            return entry;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void ReleaseBaseline(BaselineCacheEntry entry)
    {
        lock (_cacheLock)
        {
            ReleaseBaselineUnsafe(entry);
            TrimReadersUnsafe(DateTimeOffset.UtcNow);
        }
    }

    private void ReleaseBaselineUnsafe(BaselineCacheEntry entry, bool touch = true)
    {
        if (entry.LeaseCount <= 0) return;
        entry.LeaseCount--;
        if (touch)
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
        if (entry.Retired && entry.LeaseCount == 0)
            entry.Reader.Dispose();
    }

    private void ReleaseOverlay(OverlayCacheEntry entry)
    {
        lock (_cacheLock)
        {
            if (entry.LeaseCount <= 0) return;
            entry.LeaseCount--;
            entry.LastAccessUtc = DateTimeOffset.UtcNow;
            entry.Baseline.LastAccessUtc = entry.LastAccessUtc;
            if (entry.DeletePending && entry.LeaseCount == 0)
                RemoveOverlayUnsafe(entry, deleteFiles: true);
            else
                TrimReadersUnsafe(DateTimeOffset.UtcNow);
        }
    }

    private void RetireBaselineUnsafe(BaselineCacheEntry entry)
    {
        entry.Retired = true;
        if (entry.LeaseCount == 0)
            entry.Reader.Dispose();
    }

    private void TrimReadersUnsafe(DateTimeOffset now)
    {
        var idleBefore = now - TimeSpan.FromSeconds(_resources.StorageReaderIdleSeconds);

        foreach (var entry in _overlays.Values
            .Where(entry => entry.LeaseCount == 0 && entry.LastAccessUtc <= idleBefore)
            .OrderBy(entry => entry.LastAccessUtc)
            .ToList())
            RemoveOverlayUnsafe(entry, deleteFiles: false);

        while (_overlays.Count > _resources.MaxOpenOverlayReaders)
        {
            var candidate = _overlays.Values
                .Where(entry => entry.LeaseCount == 0)
                .OrderBy(entry => entry.LastAccessUtc)
                .FirstOrDefault();
            if (candidate is null) break;
            RemoveOverlayUnsafe(candidate, deleteFiles: false);
        }

        foreach (var entry in _cache.Values
            .Where(entry => entry.LeaseCount == 0 && entry.LastAccessUtc <= idleBefore)
            .OrderBy(entry => entry.LastAccessUtc)
            .ToList())
        {
            if (_cache.Remove(entry.CacheKey))
                RetireBaselineUnsafe(entry);
        }

        while (_cache.Count > _resources.MaxOpenBaselineReaders)
        {
            var candidate = _cache.Values
                .Where(entry => entry.LeaseCount == 0)
                .OrderBy(entry => entry.LastAccessUtc)
                .FirstOrDefault();
            if (candidate is null) break;
            _cache.Remove(candidate.CacheKey);
            RetireBaselineUnsafe(candidate);
        }
    }

    private void RemoveOverlayUnsafe(OverlayCacheEntry entry, bool deleteFiles)
    {
        if (!_overlays.Remove(entry.WorkspaceId)) return;
        entry.Overlay.Dispose();
        ReleaseBaselineUnsafe(entry.Baseline, touch: false);
        if (deleteFiles)
            DeleteOverlayFilesUnsafe(entry.WorkspaceId);
    }

    private void DeleteOverlayFilesUnsafe(string workspaceId)
    {
        var overlayDir = Path.Combine(_storeBaseDir, "overlays", workspaceId);
        try
        {
            if (Directory.Exists(overlayDir))
                Directory.Delete(overlayDir, recursive: true);
        }
        catch (IOException) { /* Best effort cleanup */ }
        catch (UnauthorizedAccessException) { /* Best effort cleanup */ }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    // ── Path helpers ─────────────────────────────────────────────────────────

    private string RepoStoreDir(string repoId)
    {
        var storageRepoId = RepoId.From(repoId);
        if (SolutionScope.TryParse(storageRepoId, out var publicRepoId, out var solutionId))
        {
            return Path.Combine(
                _storeBaseDir,
                SanitizeRepoId(publicRepoId.Value),
                "solutions",
                SanitizeRepoId(solutionId.Value));
        }
        return Path.Combine(_storeBaseDir, SanitizeRepoId(repoId));
    }

    private string BaselineDir(string repoId, string commitSha)
        => Path.Combine(RepoStoreDir(repoId), "baselines", commitSha);

    private string ResolveBaselineDir(string repoId, string commitSha)
    {
        var primary = BaselineDir(repoId, commitSha);
        if (File.Exists(Path.Combine(primary, "manifest.json")))
            return primary;

        string? legacyRepoId;
        lock (_cacheLock)
            _legacyBaselineAliases.TryGetValue(repoId, out legacyRepoId);
        if (legacyRepoId is not null)
        {
            var legacy = BaselineDir(legacyRepoId, commitSha);
            if (File.Exists(Path.Combine(legacy, "manifest.json")))
                return legacy;
        }
        return primary;
    }

    private static string CacheKey(string repoId, string commitSha)
        => $"{repoId}|{commitSha}";

    private static string SanitizeRepoId(string repoId)
    {
        // Same sanitization as BaselineDbFactory: non-alphanumeric chars (except -, _) → _
        var chars = repoId.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                chars[i] = '_';
        }
        return new string(chars);
    }

    // ── Dispose ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _readerSweep.Dispose();
        lock (_cacheLock)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var overlay in _overlays.Values.ToList())
                RemoveOverlayUnsafe(overlay, deleteFiles: false);
            foreach (var entry in _cache.Values)
            {
                entry.Retired = true;
                entry.Reader.Dispose();
            }
            _cache.Clear();
            _legacyBaselineAliases.Clear();
        }
    }

    internal sealed class BaselineReaderLease : IDisposable
    {
        private CustomSymbolStore? _owner;
        private readonly BaselineCacheEntry _entry;

        internal BaselineReaderLease(CustomSymbolStore owner, BaselineCacheEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public EngineBaselineReader Reader => _entry.Reader;
        public EngineMergedReader Merged => _entry.Merged;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseBaseline(_entry);
    }

    internal sealed class OverlayReaderLease : IDisposable
    {
        private CustomSymbolStore? _owner;
        private readonly OverlayCacheEntry _entry;

        internal OverlayReaderLease(CustomSymbolStore owner, OverlayCacheEntry entry)
        {
            _owner = owner;
            _entry = entry;
        }

        public EngineOverlay Overlay => _entry.Overlay;
        public EngineBaselineReader Reader => _entry.Baseline.Reader;
        public EngineMergedReader Merged => _entry.Baseline.Merged;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseOverlay(_entry);
    }

    internal sealed class BaselineCacheEntry(
        string cacheKey,
        EngineBaselineReader reader,
        EngineMergedReader merged)
    {
        public string CacheKey { get; } = cacheKey;
        public EngineBaselineReader Reader { get; } = reader;
        public EngineMergedReader Merged { get; } = merged;
        public int LeaseCount { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public bool Retired { get; set; }
    }

    internal sealed class OverlayCacheEntry(
        string workspaceId,
        EngineOverlay overlay,
        BaselineCacheEntry baseline)
    {
        public string WorkspaceId { get; } = workspaceId;
        public EngineOverlay Overlay { get; } = overlay;
        public BaselineCacheEntry Baseline { get; } = baseline;
        public int LeaseCount { get; set; }
        public DateTimeOffset LastAccessUtc { get; set; }
        public bool DeletePending { get; set; }
    }
}
