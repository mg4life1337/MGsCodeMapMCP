namespace CodeMap.Roslyn.Extraction;

using CodeMap.Core.Types;
using Microsoft.CodeAnalysis;

internal static class ExtractionScope
{
    public static bool Includes(SyntaxTree? tree, IReadOnlySet<string>? absolutePaths)
    {
        if (absolutePaths is null) return true;
        if (tree is null || string.IsNullOrWhiteSpace(tree.FilePath)) return false;
        string path = Path.GetFullPath(tree.FilePath).Replace('\\', '/');
        return absolutePaths.Contains(path);
    }

    public static FilePath? ToRepositoryPath(string repositoryRoot, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (string.IsNullOrWhiteSpace(repositoryRoot) && !Path.IsPathRooted(path))
            return FilePath.From(path);
        return RepositoryPath.TryCreate(repositoryRoot, path, out var result) ? result : null;
    }
}
