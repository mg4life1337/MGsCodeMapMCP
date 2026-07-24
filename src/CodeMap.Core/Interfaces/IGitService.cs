namespace CodeMap.Core.Interfaces;

using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Provides read-only access to Git repository state.
/// Implementation: CodeMap.Git.
/// </summary>
public interface IGitService
{
    /// <summary>
    /// Captures branch, HEAD and local index/worktree state as one consistency boundary.
    /// Implementations must not invoke an external Git process or perform network I/O.
    /// </summary>
    async Task<RepositorySnapshot> GetRepositorySnapshotAsync(
        string repoPath,
        CancellationToken ct = default)
    {
        var repoId = await GetRepoIdentityAsync(repoPath, ct).ConfigureAwait(false);
        var branch = await GetCurrentBranchAsync(repoPath, ct).ConfigureAwait(false);
        var head = await GetCurrentCommitAsync(repoPath, ct).ConfigureAwait(false);
        return new RepositorySnapshot(
            repoId,
            branch,
            head,
            await IsCleanAsync(repoPath, ct).ConfigureAwait(false) ? "clean" : "dirty",
            DateTimeOffset.UtcNow,
            Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Gets content identities for repository-relative paths at an already captured target.
    /// Missing inputs are represented by a stable deletion marker.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> GetInputFingerprintsAsync(
        string repoPath,
        RepositorySnapshot snapshot,
        IReadOnlyCollection<string> repositoryRelativePaths,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>
    /// Gets a stable identifier for the repository.
    /// Derived from the first remote URL, or a hash of the absolute path if no remote.
    /// </summary>
    Task<RepoId> GetRepoIdentityAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Gets the SHA of the current HEAD commit.</summary>
    Task<CommitSha> GetCurrentCommitAsync(string repoPath, CancellationToken ct = default);

    /// <summary>Gets the current branch name (e.g., "main", "feature/xyz").</summary>
    Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Gets the list of files changed between the given baseline commit and
    /// the current working tree.
    /// </summary>
    Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha baseline,
        CancellationToken ct = default);

    /// <summary>Gets committed changes between two explicit commits.</summary>
    Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha fromCommit,
        CommitSha toCommit,
        CancellationToken ct = default) =>
        GetChangedFilesAsync(repoPath, fromCommit, ct);

    /// <summary>Returns whether one commit is an ancestor of another.</summary>
    Task<bool> IsAncestorAsync(
        string repoPath,
        CommitSha ancestor,
        CommitSha descendant,
        CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Finds the merge base of two commits, or null when none exists.</summary>
    Task<CommitSha?> FindMergeBaseAsync(
        string repoPath,
        CommitSha first,
        CommitSha second,
        CancellationToken ct = default) => Task.FromResult<CommitSha?>(null);

    /// <summary>
    /// Returns true if the working tree has no uncommitted changes.
    /// </summary>
    Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Resolves a commitish (short SHA, branch name, tag, etc.) to a full 40-char commit SHA.
    /// Returns null if the commitish cannot be resolved.
    /// </summary>
    Task<CommitSha?> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct = default);
}
