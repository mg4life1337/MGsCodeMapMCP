namespace CodeMap.Core.Models;

using CodeMap.Core.Types;

/// <summary>Lifecycle states exposed for a rolling solution index.</summary>
public enum RollingIndexState
{
    UpToDate,
    Checking,
    Updating,
    Stale,
    Failed,
    FullRebuildRequired,
}

/// <summary>How the current logical index state was produced.</summary>
public enum RollingUpdateStrategy
{
    Reused,
    Incremental,
    FullRebuild,
}

/// <summary>
/// Persisted and observable state for one repository, branch, and solution.
/// Paths are repository-relative so the state never stores a machine-specific root.
/// </summary>
public sealed record RollingSolutionStatus(
    RepoId RepoId,
    SolutionId SolutionId,
    string SolutionPath,
    string Branch,
    CommitSha HeadCommit,
    CommitSha IndexedCommit,
    CommitSha BaselineCommit,
    WorkspaceId WorkspaceId,
    int OverlayRevision,
    RollingIndexState IndexState,
    bool PossiblyStale,
    bool? Affected,
    int ChangedFileCount,
    int UpdatedSymbolCount,
    RollingUpdateStrategy Strategy,
    double LastCheckDurationMilliseconds,
    double LastUpdateDurationMilliseconds,
    DateTimeOffset LastUpdatedAt,
    string? LastError = null);

/// <summary>Read-only status surface used by MCP handlers.</summary>
public interface IRollingIndexStatusProvider
{
    IReadOnlyList<RollingSolutionStatus> GetStatuses(string repoPath);
}

/// <summary>No-op provider used when rolling indexing is not configured.</summary>
public sealed class NullRollingIndexStatusProvider : IRollingIndexStatusProvider
{
    public IReadOnlyList<RollingSolutionStatus> GetStatuses(string repoPath) => [];
}
