namespace CodeMap.Core.Types;

/// <summary>
/// Encodes a solution scope at the existing storage interface boundary while preserving
/// the public repository identity. Storage implementations decode it into nested directories.
/// </summary>
public static class SolutionScope
{
    private const string Separator = "::solution::";

    public static RepoId ToStorageRepoId(RepoId repoId, SolutionId solutionId) =>
        RepoId.From(repoId.Value + Separator + solutionId.Value);

    public static bool TryParse(RepoId storageRepoId, out RepoId repoId, out SolutionId solutionId)
    {
        var index = storageRepoId.Value.IndexOf(Separator, StringComparison.Ordinal);
        if (index <= 0 || index + Separator.Length >= storageRepoId.Value.Length)
        {
            repoId = storageRepoId;
            solutionId = default;
            return false;
        }

        repoId = RepoId.From(storageRepoId.Value[..index]);
        solutionId = SolutionId.From(storageRepoId.Value[(index + Separator.Length)..]);
        return true;
    }

    public static RepoId PublicRepoId(RepoId storageRepoId) =>
        TryParse(storageRepoId, out var repoId, out _) ? repoId : storageRepoId;
}
