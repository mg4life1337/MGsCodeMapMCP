namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>
/// A stable, internally consistent observation of a local Git repository.
/// The working-tree fingerprint includes both index and worktree state.
/// </summary>
public sealed record RepositorySnapshot(
    RepoId RepoId,
    string Branch,
    CommitSha HeadCommit,
    string WorkingTreeFingerprint,
    DateTimeOffset CapturedAt,
    string GenerationId)
{
    public bool HasSameTarget(RepositorySnapshot other) =>
        RepoId == other.RepoId &&
        string.Equals(Branch, other.Branch, StringComparison.Ordinal) &&
        HeadCommit == other.HeadCommit &&
        string.Equals(
            WorkingTreeFingerprint,
            other.WorkingTreeFingerprint,
            StringComparison.Ordinal);
}

/// <summary>Versions that must match before an index may seed another generation.</summary>
public sealed record IndexCompatibilityFingerprint(
    string StorageSchema,
    string ExtractorVersion,
    string MsBuildFingerprint);

/// <summary>A content identity and weight for one solution-relevant repository input.</summary>
public sealed record RelevantInputFingerprint(
    string Path,
    string ContentHash,
    int Weight);

/// <summary>One solution binding inside an atomically published repository generation.</summary>
public sealed record SolutionGenerationBinding(
    SolutionId SolutionId,
    string SolutionPath,
    WorkspaceId WorkspaceId,
    CommitSha IndexedCommit,
    CommitSha BaselineCommit,
    int OverlayRevision,
    RollingUpdateStrategy Strategy,
    IReadOnlyList<RelevantInputFingerprint> RelevantInputs);

/// <summary>
/// Complete query-visible state for a repository target. A generation is published only
/// after every solution binding is complete and the target snapshot has been revalidated.
/// </summary>
public sealed record RepositoryIndexGeneration(
    string GenerationId,
    RepoId RepoId,
    string Branch,
    CommitSha HeadCommit,
    string WorkingTreeFingerprint,
    IndexCompatibilityFingerprint Compatibility,
    IReadOnlyList<SolutionGenerationBinding> Solutions,
    DateTimeOffset PublishedAt);

public enum BranchSeedRelationship
{
    Identical,
    Ancestor,
    MergeBase,
    Divergent,
}

/// <summary>A compatible, queryable source considered for one target solution.</summary>
public sealed record BranchSeedCandidate(
    RepositoryIndexGeneration Generation,
    SolutionGenerationBinding Solution,
    BranchSeedRelationship Relationship,
    double Similarity,
    int ChangedProjectCount);

/// <summary>Auditable result of per-solution seed selection.</summary>
public sealed record BranchSeedDecision(
    SolutionId SolutionId,
    BranchSeedCandidate? Selected,
    bool UseIncremental,
    string Reason);

public enum RollingGenerationAvailability
{
    NotManaged,
    Ready,
    Updating,
    NotReady,
}

/// <summary>Result used by query routing when no explicit workspace was supplied.</summary>
public sealed record RollingGenerationResolution(
    RollingGenerationAvailability Availability,
    WorkspaceId? WorkspaceId,
    string? GenerationId,
    bool ServingPrevious);

/// <summary>
/// Separates atomically published rolling generations from session-scoped manual
/// workspace defaults.
/// </summary>
public interface IRollingGenerationRegistry
{
    void BeginUpdate(
        string repoPath,
        RepositorySnapshot target,
        bool servePreviousIndexWhileUpdating);

    void Activate(string repoPath, RepositoryIndexGeneration generation);

    void Fail(string repoPath, RepositorySnapshot target);

    RepositoryIndexGeneration? GetActive(string repoPath);

    RollingGenerationResolution Resolve(string repoPath, SolutionId solutionId);
}

public sealed class NullRollingGenerationRegistry : IRollingGenerationRegistry
{
    public void BeginUpdate(
        string repoPath,
        RepositorySnapshot target,
        bool servePreviousIndexWhileUpdating) { }

    public void Activate(string repoPath, RepositoryIndexGeneration generation) { }

    public void Fail(string repoPath, RepositorySnapshot target) { }

    public RepositoryIndexGeneration? GetActive(string repoPath) => null;

    public RollingGenerationResolution Resolve(string repoPath, SolutionId solutionId) =>
        new(RollingGenerationAvailability.NotManaged, null, null, false);
}
