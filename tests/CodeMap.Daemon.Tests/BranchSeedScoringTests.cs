namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class BranchSeedScoringTests
{
    [Fact]
    public void WeightedSimilarity_GivesBuildInputsTheirConfiguredWeight()
    {
        var target = Inputs(
            ("Repo.sln", "same", 8),
            ("src/App/App.csproj", "same", 5),
            ("src/App/A.cs", "changed", 1),
            ("src/App/B.cs", "same", 1));
        var candidate = Inputs(
            ("Repo.sln", "same", 8),
            ("src/App/App.csproj", "same", 5),
            ("src/App/A.cs", "old", 1),
            ("src/App/B.cs", "same", 1));

        BranchSeedScoring.WeightedSimilarity(target, candidate)
            .Should().BeApproximately(14d / 15d, 0.000001);
    }

    [Fact]
    public void WeightedSimilarity_UsesUnionForAddedAndDeletedInputs()
    {
        var target = Inputs(("Repo.sln", "same", 8), ("new.cs", "new", 1));
        var candidate = Inputs(("Repo.sln", "same", 8), ("old.cs", "old", 1));

        BranchSeedScoring.WeightedSimilarity(target, candidate)
            .Should().BeApproximately(8d / 10d, 0.000001);
    }

    [Fact]
    public void SelectBest_AppliesRelationshipProjectCountAndAgeTieBreakers()
    {
        var older = Candidate(
            BranchSeedRelationship.Ancestor,
            changedProjects: 2,
            publishedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var fewerProjects = Candidate(
            BranchSeedRelationship.Ancestor,
            changedProjects: 1,
            publishedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        var divergentAndNew = Candidate(
            BranchSeedRelationship.Divergent,
            changedProjects: 0,
            publishedAt: DateTimeOffset.UtcNow);

        BranchSeedScoring.SelectBest([older, divergentAndNew, fewerProjects])
            .Should().BeSameAs(fewerProjects);
    }

    private static IReadOnlyList<RelevantInputFingerprint> Inputs(
        params (string Path, string Hash, int Weight)[] values) =>
        values.Select(value => new RelevantInputFingerprint(
            value.Path,
            value.Hash,
            value.Weight)).ToList();

    private static BranchSeedCandidate Candidate(
        BranchSeedRelationship relationship,
        int changedProjects,
        DateTimeOffset publishedAt)
    {
        var commit = CommitSha.From(new string('a', 40));
        var solution = SolutionId.From(Guid.NewGuid().ToString("N"));
        var binding = new SolutionGenerationBinding(
            solution,
            "Repo.sln",
            WorkspaceId.From("workspace-" + Guid.NewGuid().ToString("N")),
            commit,
            commit,
            0,
            RollingUpdateStrategy.Incremental,
            []);
        var generation = new RepositoryIndexGeneration(
            Guid.NewGuid().ToString("N"),
            RepoId.From("repo"),
            "branch",
            commit,
            "working",
            new IndexCompatibilityFingerprint("schema", "extractor", "msbuild"),
            [binding],
            publishedAt);
        return new BranchSeedCandidate(
            generation,
            binding,
            relationship,
            Similarity: 0.75,
            changedProjects);
    }
}
