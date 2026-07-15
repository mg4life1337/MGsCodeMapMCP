namespace CodeMap.Core.Types;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// Stable solution identifier derived from the normalized repository-relative solution path.
/// Windows path casing and slash style do not affect the identifier.
/// </summary>
public readonly record struct SolutionId
{
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
        var canonical = relative.ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return new SolutionId($"sln_{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}");
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
}
