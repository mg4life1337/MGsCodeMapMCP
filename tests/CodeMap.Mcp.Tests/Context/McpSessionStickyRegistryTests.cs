namespace CodeMap.Mcp.Tests.Context;

using CodeMap.Mcp.Context;
using FluentAssertions;

public sealed class McpSessionStickyRegistryTests
{
    [Fact]
    public void StickyWorkspace_IsIsolatedPerSession()
    {
        var sessions = new McpSessionContext();
        var registry = new WorkspaceStickyRegistry(sessions);
        var repo = Path.Combine(Path.GetTempPath(), "session-repo");

        using (sessions.Enter("first")) registry.Set(repo, "workspace-one");
        using (sessions.Enter("second")) registry.Set(repo, "workspace-two");

        using (sessions.Enter("first")) registry.Get(repo).Should().Be("workspace-one");
        using (sessions.Enter("second")) registry.Get(repo).Should().Be("workspace-two");
    }
}
