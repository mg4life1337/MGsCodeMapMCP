namespace CodeMap.Daemon;

using CodeMap.Core.Models;

internal static class BranchSeedScoring
{
    public static double WeightedSimilarity(
        IReadOnlyList<RelevantInputFingerprint> target,
        IReadOnlyList<RelevantInputFingerprint> candidate)
    {
        var left = target.ToDictionary(
            input => input.Path,
            StringComparer.OrdinalIgnoreCase);
        var right = candidate.ToDictionary(
            input => input.Path,
            StringComparer.OrdinalIgnoreCase);
        var paths = left.Keys
            .Concat(right.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        long denominator = 0;
        long numerator = 0;
        foreach (var path in paths)
        {
            left.TryGetValue(path, out var leftInput);
            right.TryGetValue(path, out var rightInput);
            var weight = Math.Max(leftInput?.Weight ?? 0, rightInput?.Weight ?? 0);
            denominator += weight;
            if (leftInput is not null &&
                rightInput is not null &&
                string.Equals(
                    leftInput.ContentHash,
                    rightInput.ContentHash,
                    StringComparison.Ordinal))
                numerator += weight;
        }
        return denominator == 0 ? 0 : (double)numerator / denominator;
    }

    public static BranchSeedCandidate? SelectBest(
        IEnumerable<BranchSeedCandidate> candidates) =>
        candidates
            .OrderByDescending(candidate => candidate.Similarity)
            .ThenBy(candidate => candidate.Relationship)
            .ThenBy(candidate => candidate.ChangedProjectCount)
            .ThenByDescending(candidate => candidate.Generation.PublishedAt)
            .FirstOrDefault();
}
