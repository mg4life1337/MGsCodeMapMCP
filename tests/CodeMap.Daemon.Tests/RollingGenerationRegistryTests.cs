namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class RollingGenerationRegistryTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "codemap-generation-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Activate_PublishesAllSolutionsTogether()
    {
        var registry = CreateRegistry();
        var snapshot = Snapshot("main", 'a', "work-a", "generation-a");
        var first = SolutionId.From("solution-a");
        var second = SolutionId.From("solution-b");

        registry.BeginUpdate(_tempDir, snapshot, false);
        registry.Resolve(_tempDir, first).Availability
            .Should().Be(RollingGenerationAvailability.Updating);

        registry.Activate(_tempDir, Generation(snapshot, first, second));

        registry.Resolve(_tempDir, first).Availability
            .Should().Be(RollingGenerationAvailability.Ready);
        registry.Resolve(_tempDir, second).Availability
            .Should().Be(RollingGenerationAvailability.Ready);
        registry.GetActive(_tempDir)!.Solutions.Should().HaveCount(2);
    }

    [Fact]
    public void NewTarget_DoesNotServePreviousGenerationByDefault()
    {
        var registry = CreateRegistry();
        var first = Snapshot("main", 'a', "work-a", "generation-a");
        var solution = SolutionId.From("solution-a");
        registry.Activate(_tempDir, Generation(first, solution));

        var next = Snapshot("feature", 'b', "work-b", "generation-b");
        registry.BeginUpdate(_tempDir, next, false);

        var resolution = registry.Resolve(_tempDir, solution);
        resolution.Availability.Should().Be(RollingGenerationAvailability.Updating);
        resolution.WorkspaceId.Should().BeNull();
    }

    [Fact]
    public void ExplicitPreviousServing_IsReportedAsStale()
    {
        var registry = CreateRegistry();
        var first = Snapshot("main", 'a', "work-a", "generation-a");
        var solution = SolutionId.From("solution-a");
        registry.Activate(_tempDir, Generation(first, solution));

        registry.BeginUpdate(
            _tempDir,
            Snapshot("feature", 'b', "work-b", "generation-b"),
            true);

        var resolution = registry.Resolve(_tempDir, solution);
        resolution.Availability.Should().Be(RollingGenerationAvailability.Ready);
        resolution.ServingPrevious.Should().BeTrue();
    }

    [Fact]
    public void ActiveGeneration_IsRecoveredFromDurablePointer()
    {
        var snapshot = Snapshot("main", 'a', "work-a", "generation-a");
        var solution = SolutionId.From("solution-a");
        CreateRegistry().Activate(_tempDir, Generation(snapshot, solution));

        var restarted = CreateRegistry();
        restarted.BeginUpdate(_tempDir, snapshot, false);

        restarted.Resolve(_tempDir, solution).Availability
            .Should().Be(RollingGenerationAvailability.Ready);
    }

    [Fact]
    public void SameRemoteIdentity_InDifferentFolders_HasIndependentGenerationPointers()
    {
        var firstPath = Path.Combine(_tempDir, "clone-a");
        var secondPath = Path.Combine(_tempDir, "clone-b");
        Directory.CreateDirectory(firstPath);
        Directory.CreateDirectory(secondPath);
        var snapshot = Snapshot("main", 'a', "work-a", "generation-a");
        var firstSolution = SolutionId.From("solution-clone-a");
        var secondSolution = SolutionId.From("solution-clone-b");
        var registry = CreateRegistry();

        registry.Activate(firstPath, Generation(snapshot, firstSolution));
        registry.Activate(secondPath, Generation(
            snapshot with { GenerationId = "generation-b" },
            secondSolution));

        var restarted = CreateRegistry();
        restarted.BeginUpdate(firstPath, snapshot, false);
        restarted.BeginUpdate(
            secondPath,
            snapshot with { GenerationId = "target-b" },
            false);

        restarted.Resolve(firstPath, firstSolution).Availability
            .Should().Be(RollingGenerationAvailability.Ready);
        restarted.Resolve(secondPath, secondSolution).Availability
            .Should().Be(RollingGenerationAvailability.Ready);
        restarted.GetActive(firstPath)!.GenerationId.Should().Be("generation-a");
        restarted.GetActive(secondPath)!.GenerationId.Should().Be("generation-b");
    }

    [Fact]
    public void SolutionStateRetention_DoesNotDeleteGenerationHistory()
    {
        var snapshot = Snapshot("main", 'a', "work-a", "generation-a");
        var registry = CreateRegistry();
        registry.Activate(
            _tempDir,
            Generation(snapshot, SolutionId.From("solution-a")));

        var stateStore = new RollingIndexStateStore(_tempDir);
        stateStore.ApplyRetention(snapshot.RepoId, retentionDays: 1, maxBranches: 1);

        registry.LoadHistory(snapshot.RepoId, _tempDir).Should().ContainSingle();
    }

    [Fact]
    public void IncompleteStagingGeneration_SurvivesRestartUntilMatchingCleanup()
    {
        var snapshot = Snapshot("main", 'a', "work-a", "generation-a");
        var solution = SolutionId.From("solution-a");
        var registry = CreateRegistry();
        registry.BeginStaging(
            _tempDir,
            new StagingRepositoryGeneration(
                snapshot.GenerationId,
                snapshot.RepoId,
                [new StagingWorkspaceBinding(
                    solution,
                    WorkspaceId.From("staging-workspace"))]));

        var restarted = CreateRegistry();
        var recovered = restarted.LoadStaging(snapshot.RepoId, _tempDir);
        recovered.Should().NotBeNull();
        recovered!.Workspaces.Should().ContainSingle();

        restarted.CompleteStaging(
            snapshot.RepoId,
            _tempDir,
            "different-generation");
        restarted.LoadStaging(snapshot.RepoId, _tempDir).Should().NotBeNull();

        restarted.CompleteStaging(
            snapshot.RepoId,
            _tempDir,
            snapshot.GenerationId);
        restarted.LoadStaging(snapshot.RepoId, _tempDir).Should().BeNull();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException) { }
    }

    private RollingGenerationRegistry CreateRegistry()
    {
        Directory.CreateDirectory(_tempDir);
        var runtime = new RuntimeConfiguration(
            new CodeMapConfig(),
            _tempDir,
            Path.Combine(_tempDir, "codemap.json"),
            _tempDir,
            Path.Combine(_tempDir, "logs"),
            null);
        return new RollingGenerationRegistry(runtime);
    }

    private static RepositorySnapshot Snapshot(
        string branch,
        char sha,
        string working,
        string generation) =>
        new(
            RepoId.From("repo"),
            branch,
            CommitSha.From(new string(sha, 40)),
            working,
            DateTimeOffset.UtcNow,
            generation);

    private static RepositoryIndexGeneration Generation(
        RepositorySnapshot snapshot,
        params SolutionId[] solutions) =>
        new(
            snapshot.GenerationId,
            snapshot.RepoId,
            snapshot.Branch,
            snapshot.HeadCommit,
            snapshot.WorkingTreeFingerprint,
            new IndexCompatibilityFingerprint("schema", "extractor", "msbuild"),
            solutions.Select((solution, index) => new SolutionGenerationBinding(
                solution,
                $"src/app-{index}.sln",
                WorkspaceId.From($"workspace-{index}"),
                snapshot.HeadCommit,
                snapshot.HeadCommit,
                0,
                RollingUpdateStrategy.FullRebuild,
                [])).ToList(),
            DateTimeOffset.UtcNow);
}
