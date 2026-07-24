namespace CodeMap.Mcp.Tests.Handlers;

using System.Text.Json.Nodes;
using CodeMap.Core.Errors;
using CodeMap.Core.Models;
using CodeMap.Core.Types;
using CodeMap.Mcp.Context;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using NSubstitute;

public sealed class RollingGenerationRoutingTests
{
    [Fact]
    public void ResolveWorkspaceId_UsesActiveGeneration()
    {
        var (path, solution, repositories) = RegisteredSolution();
        var generations = Substitute.For<IRollingGenerationRegistry>();
        generations.Resolve(path, solution).Returns(new RollingGenerationResolution(
            RollingGenerationAvailability.Ready,
            WorkspaceId.From("rolling-ready"),
            "generation",
            false));
        var sticky = new WorkspaceStickyRegistry(generations);

        var workspace = HandlerHelpers.ResolveWorkspaceId(
            new JsonObject { ["solution_id"] = solution.Value },
            path,
            sticky,
            repositories);

        workspace.Should().Be("rolling-ready");
    }

    [Theory]
    [InlineData(RollingGenerationAvailability.Updating, ErrorCodes.IndexUpdating)]
    [InlineData(RollingGenerationAvailability.NotReady, ErrorCodes.IndexNotReady)]
    public void ResolveWorkspaceId_RejectsIncompleteCurrentGeneration(
        RollingGenerationAvailability availability,
        string expectedCode)
    {
        var (path, solution, repositories) = RegisteredSolution();
        var generations = Substitute.For<IRollingGenerationRegistry>();
        generations.Resolve(path, solution).Returns(new RollingGenerationResolution(
            availability,
            null,
            "generation",
            false));
        var sticky = new WorkspaceStickyRegistry(generations);

        var act = () => HandlerHelpers.ResolveWorkspaceId(
            new JsonObject { ["solution_id"] = solution.Value },
            path,
            sticky,
            repositories);

        act.Should().Throw<RollingGenerationUnavailableException>()
            .Which.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void ResolveWorkspaceId_ExplicitWorkspaceAlwaysWins()
    {
        var (path, solution, repositories) = RegisteredSolution();
        var generations = Substitute.For<IRollingGenerationRegistry>();
        generations.Resolve(path, solution).Returns(new RollingGenerationResolution(
            RollingGenerationAvailability.Updating,
            null,
            "generation",
            false));
        var sticky = new WorkspaceStickyRegistry(generations);

        var workspace = HandlerHelpers.ResolveWorkspaceId(
            new JsonObject
            {
                ["solution_id"] = solution.Value,
                ["workspace_id"] = "manual-explicit",
            },
            path,
            sticky,
            repositories);

        workspace.Should().Be("manual-explicit");
    }

    private static (string Path, SolutionId Solution, RepoRegistry Repositories)
        RegisteredSolution()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "codemap-routing-" + Guid.NewGuid().ToString("N"));
        var solution = SolutionId.From("solution");
        var repositories = new RepoRegistry();
        repositories.Register(path);
        repositories.RegisterSolution(
            path,
            new SolutionRegistration(solution, "src/App.sln", "src/App.sln"));
        repositories.SetDefaultSolution(path, solution);
        return (path, solution, repositories);
    }
}
