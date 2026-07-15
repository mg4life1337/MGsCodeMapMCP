namespace CodeMap.Daemon.Tests;

using FluentAssertions;

public sealed class RepositorySupervisorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-discovery-{Guid.NewGuid():N}");

    public RepositorySupervisorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void DiscoverGitRepositories_FindsDirectoryAndWorktreeGitMarkers()
    {
        var standard = Path.Combine(_root, "team", "standard");
        var worktree = Path.Combine(_root, "team", "worktree");
        Directory.CreateDirectory(Path.Combine(standard, ".git"));
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: C:/repo/.git/worktrees/test");

        var results = RepositorySupervisor.DiscoverGitRepositories(_root);

        results.Should().BeEquivalentTo(standard, worktree);
    }

    [Fact]
    public void DiscoverGitRepositories_PrunesGeneratedAndToolDirectories()
    {
        foreach (var excluded in new[] { "bin", "obj", ".vs", "packages", ".codemap", ".codex" })
            Directory.CreateDirectory(Path.Combine(_root, excluded, "hidden", ".git"));
        var visible = Path.Combine(_root, "visible");
        Directory.CreateDirectory(Path.Combine(visible, ".git"));

        RepositorySupervisor.DiscoverGitRepositories(_root).Should().ContainSingle().Which.Should().Be(visible);
    }

    [Fact]
    public void ResolveAndValidateDefault_AcceptsDiscoveredRelativeSolution()
    {
        var solution = Path.Combine(_root, "src", "Primary.sln");
        Directory.CreateDirectory(Path.GetDirectoryName(solution)!);
        File.WriteAllText(solution, "");

        var result = RepositorySupervisor.ResolveAndValidateDefault(
            _root, "src\\Primary.sln", [solution]);

        result.Should().Be(solution);
    }

    [Fact]
    public void ResolveAndValidateDefault_RejectsUnknownSolutionClearly()
    {
        var act = () => RepositorySupervisor.ResolveAndValidateDefault(
            _root, "src\\Missing.sln", []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*defaultSolution*discovered solutions*");
    }
}
