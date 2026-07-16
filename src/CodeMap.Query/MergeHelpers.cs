namespace CodeMap.Query;

using CodeMap.Core.Interfaces;
using CodeMap.Core.Types;

/// <summary>
/// Pure static helpers for merging baseline + overlay query results.
/// All methods are side-effect-free for easy unit testing.
/// </summary>
public static class MergeHelpers
{
    /// <summary>
    /// Merges baseline and overlay search hits using the overlay-wins strategy.
    ///
    /// Overlay hits appear first (higher priority — recently modified symbols).
    /// Baseline hits are included only if:
    ///   (a) their symbol_id is not in <paramref name="deletedIds"/>, AND
    ///   (b) their file_path is not in <paramref name="overlayFiles"/>
    ///       (any file that was reindexed in the overlay is fully superseded).
    /// </summary>
    public static MergedSearchResult MergeSearchResults(
        IReadOnlyList<SymbolSearchHit> baselineHits,
        IReadOnlyList<SymbolSearchHit> overlayHits,
        IReadOnlySet<SymbolId> deletedIds,
        IReadOnlySet<FilePath> overlayFiles,
        int limit)
    {
        var deleted = deletedIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var replacedFiles = overlayFiles
            .Select(path => path.Value)
            .ToHashSet(RepositoryPath.StringComparer);
        var seenSymbolIds = new ScopedIdentitySet();
        var seenStableIds = new ScopedIdentitySet();
        var combined = new List<SymbolSearchHit>(Math.Min(limit + 1, baselineHits.Count + overlayHits.Count));

        void AddUnique(SymbolSearchHit hit)
        {
            if (combined.Count > limit) return;
            if (!seenSymbolIds.Add(hit.SymbolId.Value, hit.ProjectName)) return;
            if (hit.StableId is { IsEmpty: false } stable &&
                !seenStableIds.Add(stable.Value, hit.ProjectName))
                return;
            combined.Add(hit);
        }

        // Overlay is authoritative and is deduplicated before the result limit is applied.
        foreach (var hit in overlayHits)
            AddUnique(hit);

        foreach (var hit in baselineHits)
        {
            if (deleted.Contains(hit.SymbolId.Value) || replacedFiles.Contains(hit.FilePath.Value))
                continue;
            AddUnique(hit);
        }

        var truncated = combined.Count > limit;
        var hits = combined.Take(limit).ToList();
        var totalCount = truncated ? limit + 1 : hits.Count;

        return new MergedSearchResult(hits, totalCount, truncated);
    }

    /// <summary>
    /// Keeps equal symbol/stable IDs separate when their owning projects are known to differ.
    /// A missing project is treated as legacy unscoped identity and therefore matches every
    /// occurrence of that ID, preserving deduplication for older baseline formats.
    /// </summary>
    private sealed class ScopedIdentitySet
    {
        private const string Unscoped = "\0";
        private readonly Dictionary<string, HashSet<string>> _projects =
            new(StringComparer.Ordinal);

        public bool Add(string identity, string? projectName)
        {
            string project = string.IsNullOrWhiteSpace(projectName) ? Unscoped : projectName;
            if (!_projects.TryGetValue(identity, out var projects))
            {
                _projects[identity] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project };
                return true;
            }

            if (projects.Contains(Unscoped) || project == Unscoped)
                return false;

            return projects.Add(project);
        }
    }
}

/// <summary>Result of merging baseline and overlay search hits.</summary>
public record MergedSearchResult(
    IReadOnlyList<SymbolSearchHit> Hits,
    int TotalCount,
    bool Truncated);
