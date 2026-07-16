namespace CodeMap.Core.Types;

/// <summary>
/// Canonical path rules for repository-relative paths.
/// </summary>
public static class RepositoryPath
{
    /// <summary>
    /// Case-insensitive comparer used at repository path merge boundaries.
    /// The canonical representation itself preserves the file-system casing.
    /// </summary>
    public static StringComparer StringComparer { get; } = StringComparer.OrdinalIgnoreCase;

    /// <summary>Case-insensitive comparer for strongly typed repository paths.</summary>
    public static IEqualityComparer<FilePath> FilePathComparer { get; } = new CanonicalFilePathComparer();

    /// <summary>
    /// Converts slash variants and dot segments to one repo-relative representation.
    /// Absolute paths and paths that escape the repository are rejected.
    /// </summary>
    public static string CanonicalizeRelative(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        string normalized = value.Trim().Replace('\\', '/');
        if (IsRooted(normalized))
            throw new ArgumentException("Path must be repository-relative.", nameof(value));

        var segments = new List<string>();
        foreach (string segment in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count == 0)
                    throw new ArgumentException("Path must not escape the repository.", nameof(value));

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
            throw new ArgumentException("Path must identify a file inside the repository.", nameof(value));

        return string.Join('/', segments);
    }

    /// <summary>
    /// Resolves an absolute or relative candidate against a repository root and returns
    /// its canonical repository-relative path. Candidates outside the root are rejected.
    /// </summary>
    public static bool TryCreate(string repositoryRoot, string candidate, out FilePath filePath)
    {
        filePath = default;
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(candidate))
            return false;

        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
            string absolute = Path.GetFullPath(
                Path.IsPathRooted(candidate) ? candidate : Path.Combine(root, candidate));
            string relative = Path.GetRelativePath(root, absolute).Replace('\\', '/');

            if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || IsRooted(relative))
                return false;

            filePath = FilePath.From(relative);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool IsRooted(string path) =>
        (path.Length > 0 && path[0] == '/') ||
        (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':');

    private sealed class CanonicalFilePathComparer : IEqualityComparer<FilePath>
    {
        public bool Equals(FilePath x, FilePath y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Value, y.Value);

        public int GetHashCode(FilePath obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value);
    }
}
