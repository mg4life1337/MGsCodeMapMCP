namespace CodeMap.Git;

using System.Security.Cryptography;
using System.Text;
using CodeMap.Core.Interfaces;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

/// <summary>
/// Read-only Git repository state provider backed by LibGit2Sharp.
/// Each method opens and disposes its own Repository instance (stateless).
/// </summary>
public sealed class GitService : IGitService
{
    private const int SnapshotCaptureAttempts = 3;
    private const string MissingInputHash = "missing";
    private readonly ILogger<GitService> _logger;

    public GitService(ILogger<GitService> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<RepositorySnapshot> GetRepositorySnapshotAsync(
        string repoPath,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        for (var attempt = 1; attempt <= SnapshotCaptureAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var repo = new Repository(validatedPath);
            if (repo.Head.Tip is null)
                throw new InvalidOperationException("Repository has no commits (unborn HEAD).");

            var repoId = DeriveRepoId(repo);
            var branchBefore = GetBranch(repo);
            var headBefore = repo.Head.Tip.Sha;
            var fingerprintBefore = ComputeWorkingTreeFingerprint(repo, ct);
            var branchAfter = GetBranch(repo);
            var headAfter = repo.Head.Tip?.Sha;
            var fingerprintAfter = ComputeWorkingTreeFingerprint(repo, ct);

            if (string.Equals(branchBefore, branchAfter, StringComparison.Ordinal) &&
                string.Equals(headBefore, headAfter, StringComparison.Ordinal) &&
                string.Equals(fingerprintBefore, fingerprintAfter, StringComparison.Ordinal))
            {
                var generationSource = string.Join(
                    '\n',
                    repoId.Value,
                    branchBefore,
                    headBefore,
                    fingerprintBefore,
                    Guid.NewGuid().ToString("N"));
                return Task.FromResult(new RepositorySnapshot(
                    repoId,
                    branchBefore,
                    CommitSha.From(headBefore),
                    fingerprintBefore,
                    DateTimeOffset.UtcNow,
                    Sha256Hex(generationSource)[..32]));
            }
        }

        throw new InvalidOperationException(
            "Repository state changed repeatedly while capturing a consistent snapshot.");
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, string>> GetInputFingerprintsAsync(
        string repoPath,
        RepositorySnapshot snapshot,
        IReadOnlyCollection<string> repositoryRelativePaths,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        using (var repo = new Repository(validatedPath))
        {
            EnsureSnapshotHead(repo, snapshot);
            var changedPaths = repo.RetrieveStatus(new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                })
                .Select(entry => NormalizeGitPath(entry.FilePath))
                .ToHashSet(StringComparer.Ordinal);

            foreach (var requestedPath in repositoryRelativePaths
                         .Select(NormalizeGitPath)
                         .Distinct(StringComparer.Ordinal)
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                ct.ThrowIfCancellationRequested();
                var worktreePath = ResolveRepositoryFile(validatedPath, requestedPath);
                if (changedPaths.Contains(requestedPath))
                {
                    result[requestedPath] = File.Exists(worktreePath)
                        ? HashFile(worktreePath, ct)
                        : MissingInputHash;
                    continue;
                }

                var entry = repo.Head.Tip?[requestedPath];
                result[requestedPath] = entry?.Target is Blob blob
                    ? blob.Id.Sha
                    : MissingInputHash;
            }
        }

        var revalidated = await GetRepositorySnapshotAsync(repoPath, ct).ConfigureAwait(false);
        if (!snapshot.HasSameTarget(revalidated))
            throw new InvalidOperationException(
                "Repository state changed while reading solution input fingerprints.");

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// RepoId derivation: SHA-256 of normalized remote origin URL (first 16 hex chars).
    /// If no remote exists, uses SHA-256 of the normalized absolute repo root path,
    /// prefixed with "local-" to distinguish local-only repos.
    /// URL normalization: lowercase, strip trailing ".git" and "/".
    /// </remarks>
    public Task<RepoId> GetRepoIdentityAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);
        RepoId repoId = DeriveRepoId(repo);

        _logger.LogInformation("Resolved repo identity {RepoId} for {RepoPath}", repoId, repoPath);
        return Task.FromResult(repoId);
    }

    /// <inheritdoc/>
    /// <remarks>Throws <see cref="InvalidOperationException"/> if HEAD is unborn (no commits yet).</remarks>
    public Task<CommitSha> GetCurrentCommitAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        if (repo.Head.Tip is null)
            throw new InvalidOperationException("Repository has no commits (unborn HEAD).");

        return Task.FromResult(CommitSha.From(repo.Head.Tip.Sha));
    }

    /// <inheritdoc/>
    /// <remarks>Returns "HEAD" when the repository is in detached HEAD state.</remarks>
    public Task<string> GetCurrentBranchAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        string branch = repo.Head.FriendlyName;
        if (branch == "(no branch)" || repo.Info.IsHeadDetached)
            branch = "HEAD";

        return Task.FromResult(branch);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Two-pass diff: (1) baseline tree vs HEAD tree (committed changes since baseline),
    /// (2) HEAD tree vs working directory (uncommitted index/working-copy changes).
    /// Working-directory changes override committed-change entries for the same path.
    /// </remarks>
    public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha baseline,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        var baselineCommit = repo.Lookup<Commit>(baseline.Value)
            ?? throw new InvalidOperationException(
                $"Commit {baseline.Value} not found in repository.");

        // Track changes: path → FileChangeKind, working directory overrides committed
        var changes = new Dictionary<string, FileChange>(StringComparer.Ordinal);

        // 1. Diff baseline tree vs HEAD tree (committed changes since baseline)
        if (repo.Head.Tip is not null)
        {
            var committedDiff = repo.Diff.Compare<TreeChanges>(
                baselineCommit.Tree,
                repo.Head.Tip.Tree);

            foreach (TreeEntryChanges entry in committedDiff)
            {
                changes[entry.Path] = ToFileChange(entry);
            }

            // 2. Diff HEAD tree vs working directory (uncommitted changes)
            var workingDiff = repo.Diff.Compare<TreeChanges>(
                repo.Head.Tip.Tree,
                DiffTargets.Index | DiffTargets.WorkingDirectory);

            foreach (TreeEntryChanges entry in workingDiff)
            {
                changes[entry.Path] = ToFileChange(entry);
            }
        }

        var result = changes.Values.ToList();

        return Task.FromResult<IReadOnlyList<FileChange>>(result);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<FileChange>> GetChangedFilesAsync(
        string repoPath,
        CommitSha fromCommit,
        CommitSha toCommit,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);
        using var repo = new Repository(validatedPath);
        var from = repo.Lookup<Commit>(fromCommit.Value)
            ?? throw new InvalidOperationException($"Commit {fromCommit.Value} not found in repository.");
        var to = repo.Lookup<Commit>(toCommit.Value)
            ?? throw new InvalidOperationException($"Commit {toCommit.Value} not found in repository.");
        var result = repo.Diff.Compare<TreeChanges>(from.Tree, to.Tree)
            .Select(ToFileChange)
            .ToList();
        return Task.FromResult<IReadOnlyList<FileChange>>(result);
    }

    /// <inheritdoc/>
    public Task<bool> IsAncestorAsync(
        string repoPath,
        CommitSha ancestor,
        CommitSha descendant,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);
        using var repo = new Repository(validatedPath);
        var ancestorCommit = repo.Lookup<Commit>(ancestor.Value);
        var descendantCommit = repo.Lookup<Commit>(descendant.Value);
        if (ancestorCommit is null || descendantCommit is null) return Task.FromResult(false);
        var divergence = repo.ObjectDatabase.CalculateHistoryDivergence(ancestorCommit, descendantCommit);
        return Task.FromResult(divergence?.AheadBy == 0);
    }

    /// <inheritdoc/>
    public Task<CommitSha?> FindMergeBaseAsync(
        string repoPath,
        CommitSha first,
        CommitSha second,
        CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);
        using var repo = new Repository(validatedPath);
        var firstCommit = repo.Lookup<Commit>(first.Value);
        var secondCommit = repo.Lookup<Commit>(second.Value);
        if (firstCommit is null || secondCommit is null)
            return Task.FromResult<CommitSha?>(null);
        var mergeBase = repo.ObjectDatabase.FindMergeBase(firstCommit, secondCommit);
        return Task.FromResult(mergeBase is null
            ? (CommitSha?)null
            : CommitSha.From(mergeBase.Sha));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Includes untracked files in the dirty check. An unborn repository (no commits) is treated as clean.
    /// </remarks>
    public Task<bool> IsCleanAsync(string repoPath, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        // Empty repo (no commits) with no files — consider clean
        if (repo.Head.Tip is null)
            return Task.FromResult(true);

        var status = repo.RetrieveStatus(new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = true
        });

        return Task.FromResult(!status.IsDirty);
    }

    /// <inheritdoc/>
    public Task<CommitSha?> ResolveCommitAsync(string repoPath, string commitish, CancellationToken ct = default)
    {
        string validatedPath = GitPathValidator.ValidateAndNormalize(repoPath);

        using var repo = new Repository(validatedPath);

        var obj = repo.Lookup(commitish);
        if (obj is null)
            return Task.FromResult<CommitSha?>(null);

        // Peel to commit (handles tags, etc.)
        var commit = obj.Peel<Commit>();
        if (commit is null)
            return Task.FromResult<CommitSha?>(null);

        return Task.FromResult<CommitSha?>(CommitSha.From(commit.Sha));
    }

    // --- Private helpers ---

    private static RepoId DeriveRepoId(Repository repo)
    {
        string? remoteUrl = repo.Network.Remotes["origin"]?.Url
                            ?? repo.Network.Remotes.FirstOrDefault()?.Url;

        if (!string.IsNullOrWhiteSpace(remoteUrl))
        {
            string normalized = NormalizeRemoteUrl(remoteUrl);
            string hash = Sha256Hex(normalized)[..16];
            return RepoId.From(hash);
        }

        string repoRoot = repo.Info.WorkingDirectory ?? repo.Info.Path;
        string normalizedPath = NormalizePath(repoRoot);
        string pathHash = Sha256Hex(normalizedPath)[..16];
        return RepoId.From($"local-{pathHash}");
    }

    private static string NormalizeRemoteUrl(string url)
    {
        url = url.Trim();
        if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            url = url[..^4];
        url = url.TrimEnd('/');
        return url.ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        path = Path.GetFullPath(path);
        path = path.Replace('\\', '/');
        path = path.TrimEnd('/');
        return path.ToLowerInvariant();
    }

    private static string Sha256Hex(string input)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GetBranch(Repository repo) =>
        repo.Info.IsHeadDetached || repo.Head.FriendlyName == "(no branch)"
            ? "HEAD"
            : repo.Head.FriendlyName;

    private static string ComputeWorkingTreeFingerprint(
        Repository repo,
        CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = true,
            })
            .OrderBy(entry => entry.FilePath, StringComparer.Ordinal)
            .ToList();

        foreach (var entry in status)
        {
            ct.ThrowIfCancellationRequested();
            var path = NormalizeGitPath(entry.FilePath);
            AppendHashText(hash, path);
            AppendHashText(hash, ((int)entry.State).ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendHashText(hash, repo.Index[path]?.Id.Sha ?? MissingInputHash);

            var fullPath = ResolveRepositoryFile(repo.Info.WorkingDirectory, path);
            AppendHashText(hash, File.Exists(fullPath) ? HashFile(fullPath, ct) : MissingInputHash);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void EnsureSnapshotHead(Repository repo, RepositorySnapshot snapshot)
    {
        if (repo.Head.Tip is null ||
            !string.Equals(repo.Head.Tip.Sha, snapshot.HeadCommit.Value, StringComparison.Ordinal) ||
            !string.Equals(GetBranch(repo), snapshot.Branch, StringComparison.Ordinal) ||
            DeriveRepoId(repo) != snapshot.RepoId)
        {
            throw new InvalidOperationException(
                "Repository branch or HEAD no longer matches the captured snapshot.");
        }
    }

    private static string NormalizeGitPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static string ResolveRepositoryFile(string repositoryRoot, string relativePath)
    {
        var root = Path.GetFullPath(repositoryRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(
            root,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Repository-relative input path escapes the repository root.");
        return fullPath;
    }

    private static string HashFile(string path, CancellationToken ct)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendHashText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static FileChangeKind MapChangeKind(ChangeKind kind) => kind switch
    {
        ChangeKind.Added => FileChangeKind.Added,
        ChangeKind.Modified => FileChangeKind.Modified,
        ChangeKind.Deleted => FileChangeKind.Deleted,
        ChangeKind.Renamed => FileChangeKind.Renamed,
        ChangeKind.Copied => FileChangeKind.Added,
        ChangeKind.TypeChanged => FileChangeKind.Modified,
        _ => FileChangeKind.Modified,
    };

    private static FileChange ToFileChange(TreeEntryChanges entry)
    {
        var oldPath = entry.Status == ChangeKind.Renamed && !string.IsNullOrWhiteSpace(entry.OldPath)
            ? GitPathValidator.ToRepoRelativePath(entry.OldPath)
            : (FilePath?)null;
        return new FileChange(
            GitPathValidator.ToRepoRelativePath(entry.Path),
            MapChangeKind(entry.Status),
            oldPath);
    }
}
