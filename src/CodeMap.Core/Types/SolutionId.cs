namespace CodeMap.Core.Types;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Stable solution identifier derived from both the normalized repository instance path
/// and the repository-relative solution path. Two clones of the same remote therefore
/// receive independent indexes even when their solution paths and commits are identical.
/// </summary>
public readonly record struct SolutionId
{
    private const int HashBytes = 12;
    public string Value { get; }

    private SolutionId(string value) => Value = value;

    public static SolutionId From(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new SolutionId(value);
    }

    public static SolutionId FromPath(string repoRootPath, string solutionPath)
    {
        var relative = GetRepositoryRelativePath(repoRootPath, solutionPath);
        var legacy = Hash(relative.ToLowerInvariant());
        var root = Path.GetFullPath(repoRootPath)
            .Replace('\\', '/')
            .TrimEnd('/')
            .ToLowerInvariant();
        var instance = Hash(root);
        return new SolutionId($"sln_{legacy}_{instance}");
    }

    /// <summary>
    /// Returns the pre-2.8.0-mgs.7 relative-path-only ID so existing on-disk baselines
    /// can be read without migration or reindexing.
    /// </summary>
    public static SolutionId LegacyFromPath(string repoRootPath, string solutionPath)
    {
        var relative = GetRepositoryRelativePath(repoRootPath, solutionPath);
        return new SolutionId($"sln_{Hash(relative.ToLowerInvariant())}");
    }

    public bool TryGetLegacyId(out SolutionId legacyId)
    {
        const int legacyLength = 4 + HashBytes * 2;
        if (Value.Length == legacyLength + 1 + HashBytes * 2 &&
            Value.StartsWith("sln_", StringComparison.Ordinal) &&
            Value[legacyLength] == '_')
        {
            legacyId = new SolutionId(Value[..legacyLength]);
            return true;
        }
        legacyId = default;
        return false;
    }

    public static string GetRepositoryRelativePath(string repoRootPath, string solutionPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);

        var root = Path.GetFullPath(repoRootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.IsPathRooted(solutionPath)
            ? solutionPath
            : Path.Combine(root, solutionPath));
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        if (relative == ".." || relative.StartsWith("../", StringComparison.Ordinal))
            throw new ArgumentException($"Solution path '{solutionPath}' is outside repository root '{repoRootPath}'.");
        return relative;
    }

    public override string ToString() => Value;
    public static implicit operator string(SolutionId id) => id.Value;

    private static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash.AsSpan(0, HashBytes)).ToLowerInvariant();
    }
}
