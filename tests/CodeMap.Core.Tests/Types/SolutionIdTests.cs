namespace CodeMap.Core.Tests.Types;

using CodeMap.Core.Types;
using FluentAssertions;

public sealed class SolutionIdTests
{
    [Fact]
    public void FromPath_IsStableAcrossCaseAndSlashVariants()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeMapSolutionIdRepo");
        var first = SolutionId.FromPath(root, Path.Combine(root, "src", "App.sln"));
        var second = SolutionId.FromPath(root.ToUpperInvariant(),
            Path.Combine(root, "SRC", "APP.SLN").Replace('\\', '/'));

        second.Should().Be(first);
        first.Value.Should().MatchRegex("^sln_[0-9a-f]{24}_[0-9a-f]{24}$");
    }

    [Fact]
    public void FromPath_ChangesWhenRepositoryRelativePathChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeMapSolutionIdRepo");

        SolutionId.FromPath(root, Path.Combine(root, "one", "App.sln"))
            .Should().NotBe(SolutionId.FromPath(root, Path.Combine(root, "two", "App.sln")));
    }

    [Fact]
    public void FromPath_ChangesWhenSameSolutionLivesInDifferentRepositoryFolder()
    {
        var firstRoot = Path.Combine(Path.GetTempPath(), "CodeMapSolutionCloneA");
        var secondRoot = Path.Combine(Path.GetTempPath(), "CodeMapSolutionCloneB");

        SolutionId.FromPath(firstRoot, Path.Combine(firstRoot, "src", "App.sln"))
            .Should().NotBe(SolutionId.FromPath(secondRoot, Path.Combine(secondRoot, "src", "App.sln")));
    }

    [Fact]
    public void LegacyId_CanBeRecoveredForExistingIndexes()
    {
        var root = Path.Combine(Path.GetTempPath(), "CodeMapSolutionIdRepo");
        var solution = Path.Combine(root, "src", "App.sln");
        var current = SolutionId.FromPath(root, solution);

        current.TryGetLegacyId(out var legacy).Should().BeTrue();
        legacy.Should().Be(SolutionId.LegacyFromPath(root, solution));
        legacy.Value.Should().MatchRegex("^sln_[0-9a-f]{24}$");
    }

    [Fact]
    public void FromPath_RejectsPathOutsideRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "repo", "nested");
        var outside = Path.Combine(Path.GetTempPath(), "other", "App.sln");

        var act = () => SolutionId.FromPath(root, outside);

        act.Should().Throw<ArgumentException>().WithMessage("*outside repository root*");
    }
}
