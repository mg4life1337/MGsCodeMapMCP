namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class RollingIndexStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-state-{Guid.NewGuid():N}");

    public RollingIndexStateStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void StableId_DoesNotExposeBranchNameOrPathSeparators()
    {
        var id = RollingIndexStateStore.StableId("feature/sample-name");

        id.Should().MatchRegex("^[0-9a-f]{32}$");
        id.Should().NotContain("feature");
        id.Should().NotContain("/");
    }

    [Fact]
    public void SaveAndLoad_RestoresPersistedBranchState()
    {
        var store = new RollingIndexStateStore(_root);
        var state = CreateState("main", Sha('a'));

        store.Save(state);
        var restored = store.Load(state.RepoId, state.SolutionId, state.Branch);

        restored.Should().BeEquivalentTo(state);
    }

    [Fact]
    public void FindAtCommit_AllowsNewBranchToReuseKnownState()
    {
        var store = new RollingIndexStateStore(_root);
        var commit = Sha('b');
        var state = CreateState("main", commit);
        store.Save(state);

        var reusable = store.FindAtCommit(state.RepoId, state.SolutionId, commit);

        reusable.Should().NotBeNull();
        reusable!.WorkspaceId.Should().Be(state.WorkspaceId);
    }

    [Fact]
    public void Save_NewWorkspace_KeepsPreviousStateAsReusableHistory()
    {
        var store = new RollingIndexStateStore(_root);
        var previous = CreateState("main", Sha('b'));
        var current = previous with
        {
            HeadCommit = Sha('c'),
            IndexedCommit = Sha('c'),
            WorkspaceId = WorkspaceId.From("rolling-current-workspace"),
            LastUpdatedAt = previous.LastUpdatedAt.AddMinutes(1),
        };

        store.Save(previous);
        store.Save(current);

        store.FindAtCommit(previous.RepoId, previous.SolutionId, previous.IndexedCommit)!
            .WorkspaceId.Should().Be(previous.WorkspaceId);
        store.Load(previous.RepoId, previous.SolutionId, "main")!
            .WorkspaceId.Should().Be(current.WorkspaceId);
    }

    [Fact]
    public void Retention_RemovesExpiredHistoryButKeepsActiveState()
    {
        var store = new RollingIndexStateStore(_root);
        var previous = CreateState("main", Sha('b')) with
        {
            LastUpdatedAt = DateTimeOffset.UtcNow.AddDays(-3),
        };
        var current = previous with
        {
            HeadCommit = Sha('c'),
            IndexedCommit = Sha('c'),
            WorkspaceId = WorkspaceId.From("rolling-current-workspace"),
            LastUpdatedAt = DateTimeOffset.UtcNow,
        };
        store.Save(previous);
        store.Save(current);

        var removed = store.ApplyRetention(previous.RepoId, retentionDays: 1, maxBranches: 8);

        removed.Should().ContainSingle(state => state.WorkspaceId == previous.WorkspaceId);
        store.LoadAll(previous.RepoId).Should().ContainSingle(state => state.WorkspaceId == current.WorkspaceId);
    }

    private static RollingSolutionStatus CreateState(string branch, CommitSha commit) =>
        new(
            RepoId.From("0123456789abcdef"),
            SolutionId.From("solution-0123456789abcdef"),
            "src/Primary.sln",
            branch,
            commit,
            commit,
            commit,
            WorkspaceId.From("rolling-0123456789abcdef"),
            2,
            RollingIndexState.UpToDate,
            false,
            false,
            0,
            0,
            RollingUpdateStrategy.Reused,
            1,
            2,
            DateTimeOffset.UtcNow,
            null);

    private static CommitSha Sha(char value) => CommitSha.From(new string(value, 40));
}
