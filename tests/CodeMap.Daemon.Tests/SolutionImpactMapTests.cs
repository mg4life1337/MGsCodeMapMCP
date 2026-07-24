namespace CodeMap.Daemon.Tests;

using CodeMap.Core.Models;
using CodeMap.Core.Types;
using FluentAssertions;

public sealed class SolutionImpactMapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"codemap-impact-{Guid.NewGuid():N}");
    private readonly string _solution;

    public SolutionImpactMapTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "src", "Primary"));
        Directory.CreateDirectory(Path.Combine(_root, "src", "Shared"));
        File.WriteAllText(Path.Combine(_root, "Directory.Build.props"), "<Project />");
        File.WriteAllText(Path.Combine(_root, "src", "Primary", "Primary.vbproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\Shared\\Shared.vbproj\" /></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_root, "src", "Shared", "Shared.vbproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(_root, "src", "Primary", "Service.vb"), "Public Class Service\nEnd Class");
        File.WriteAllText(Path.Combine(_root, "src", "Shared", "Model.vb"), "Public Class Model\nEnd Class");
        File.WriteAllText(Path.Combine(_root, "README.MD"), "documentation");
        _solution = Path.Combine(_root, "Primary.sln");
        File.WriteAllText(_solution,
            "Project(\"{00000000-0000-0000-0000-000000000000}\") = \"Primary\", \"src\\Primary\\Primary.vbproj\", \"{10000000-0000-0000-0000-000000000000}\"\nEndProject\n" +
            "Project(\"{00000000-0000-0000-0000-000000000000}\") = \"Shared\", \"src\\Shared\\Shared.vbproj\", \"{20000000-0000-0000-0000-000000000000}\"\nEndProject\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Analyze_DocumentationChange_SkipsSolution()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("README.MD")]);

        result.IsAffected.Should().BeFalse();
        result.Reason.Should().Be("0 changed inputs");
    }

    [Fact]
    public void Analyze_SourceChange_AffectsContainingSolution()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("src/Primary/Service.vb")]);

        result.IsAffected.Should().BeTrue();
        result.ChangedInputCount.Should().Be(1);
    }

    [Fact]
    public void Analyze_ReferencedProjectChange_ExpandsToDependentProject()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("src/Shared/Shared.vbproj")]);

        result.IsAffected.Should().BeTrue();
        result.Reason.Should().Contain("2 affected project");
        result.RebuildMap.Should().BeTrue();
    }

    [Fact]
    public void Analyze_GlobalBuildInput_AffectsProjectsAndRebuildsMap()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("Directory.Build.props")]);

        result.IsAffected.Should().BeTrue();
        result.RebuildMap.Should().BeTrue();
    }

    [Fact]
    public void Analyze_NewSubtreeBuildInput_AffectsOnlyProjectsBelowIt()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("src/Directory.Build.targets")]);

        result.IsAffected.Should().BeTrue();
        result.Reason.Should().Contain("2 affected project");
        result.RebuildMap.Should().BeTrue();
    }

    [Fact]
    public void Analyze_SolutionMembershipChange_RebuildsMap()
    {
        var map = SolutionImpactMap.Build(_root, _solution);

        var result = map.Analyze([Changed("Primary.sln")]);

        result.IsAffected.Should().BeTrue();
        result.RebuildMap.Should().BeTrue();
    }

    [Fact]
    public void Build_ExternalProjectReference_IsExcludedFromRepositoryInputs()
    {
        var external = _root + "-external";
        Directory.CreateDirectory(external);
        try
        {
            var project = Path.Combine(external, "External.csproj");
            File.WriteAllText(project, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.AppendAllText(
                _solution,
                $"Project(\"{{00000000-0000-0000-0000-000000000000}}\") = \"External\", \"{project}\", \"{{30000000-0000-0000-0000-000000000000}}\"\nEndProject\n");

            var map = SolutionImpactMap.Build(_root, _solution);

            map.Projects.Should().HaveCount(2);
            map.GetWeightedInputs().Keys.Should().NotContain(path =>
                path == ".." || path.StartsWith("../", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(external, recursive: true);
        }
    }

    private static FileChange Changed(string path) =>
        new(FilePath.From(path), FileChangeKind.Modified);
}
