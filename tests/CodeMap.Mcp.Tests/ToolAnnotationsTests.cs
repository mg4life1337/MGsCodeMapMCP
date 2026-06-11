namespace CodeMap.Mcp.Tests;

using System.Text;
using System.Text.Json.Nodes;
using CodeMap.Mcp.Handlers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies the MCP 2025-03-26 <c>annotations</c> field emitted on <c>tools/list</c>:
/// the four <c>*Hint</c> sub-fields are present when a tool registers a
/// <see cref="ToolAnnotations"/>, the key is omitted entirely when it doesn't
/// (back-compat), and the values reflect the annotation as passed.
///
/// Goes through the real <see cref="McpServer.RunAsync"/> stdio path so the
/// JSON serialization is what an actual client would see. The "every shipped
/// tool has annotations" guard lives in
/// <c>CodeMap.Daemon.Tests/ToolAnnotationsCoverageTests.cs</c> — that test
/// project has full DI graph visibility, this one doesn't.
/// </summary>
public sealed class ToolAnnotationsTests
{
    [Fact]
    public async Task ToolsList_EmitsAnnotationsWhenSet_AndOmitsWhenNot()
    {
        var registry = new ToolRegistry();
        registry.Register(new ToolDefinition(
            "test.read", "read-only test", new JsonObject(),
            (_, _) => Task.FromResult(new ToolCallResult("{}")),
            HandlerHelpers.AnnotReadOnly));
        registry.Register(new ToolDefinition(
            "test.write", "write test", new JsonObject(),
            (_, _) => Task.FromResult(new ToolCallResult("{}")),
            HandlerHelpers.AnnotWriteIdempotent));
        registry.Register(new ToolDefinition(
            "test.destruct", "destruct test", new JsonObject(),
            (_, _) => Task.FromResult(new ToolCallResult("{}")),
            HandlerHelpers.AnnotDestructIdempotent));
        registry.Register(new ToolDefinition(
            "test.bare", "no annotations", new JsonObject(),
            (_, _) => Task.FromResult(new ToolCallResult("{}"))));

        var server = new McpServer(registry, NullLogger<McpServer>.Instance);
        var tools = await InvokeToolsListAsync(server);

        tools.Should().HaveCount(4);
        var byName = tools
            .Select(t => t!.AsObject())
            .ToDictionary(t => t["name"]!.GetValue<string>(), t => t);

        // ── Read-only preset ──────────────────────────────────────────────────
        var ro = byName["test.read"]["annotations"]!.AsObject();
        ro["readOnlyHint"]!.GetValue<bool>().Should().BeTrue();
        ro["destructiveHint"]!.GetValue<bool>().Should().BeFalse();
        ro["idempotentHint"]!.GetValue<bool>().Should().BeTrue(
            "read-only operations are trivially idempotent");
        ro["openWorldHint"]!.GetValue<bool>().Should().BeFalse(
            "CodeMap is fully closed-world (local index, no network)");

        // ── Write preset (idempotent, NOT destructive) ────────────────────────
        var w = byName["test.write"]["annotations"]!.AsObject();
        w["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
        w["destructiveHint"]!.GetValue<bool>().Should().BeFalse();
        w["idempotentHint"]!.GetValue<bool>().Should().BeTrue(
            "every CodeMap write is idempotent — clients must be able to retry transient failures");
        w["openWorldHint"]!.GetValue<bool>().Should().BeFalse();

        // ── Destruct preset (idempotent — resource is gone the second time) ──
        var d = byName["test.destruct"]["annotations"]!.AsObject();
        d["readOnlyHint"]!.GetValue<bool>().Should().BeFalse();
        d["destructiveHint"]!.GetValue<bool>().Should().BeTrue();
        d["idempotentHint"]!.GetValue<bool>().Should().BeTrue(
            "destruct is idempotent in CodeMap: cleanup/remove/delete twice = first call already removed it");
        d["openWorldHint"]!.GetValue<bool>().Should().BeFalse();

        // ── Bare tool: no annotations key (back-compat for older clients) ────
        byName["test.bare"].ContainsKey("annotations").Should().BeFalse(
            "absent annotations must not emit a key — clients that don't understand annotations see no spurious field");
    }

    /// <summary>
    /// Drives <see cref="McpServer.RunAsync"/> with a single newline-delimited
    /// <c>tools/list</c> request and returns the parsed <c>result.tools</c> array.
    /// </summary>
    private static async Task<JsonArray> InvokeToolsListAsync(McpServer server)
    {
        var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}\n";
        var input = new MemoryStream(Encoding.UTF8.GetBytes(request));
        var output = new MemoryStream();
        await server.RunAsync(input, output, CancellationToken.None);
        var responseJson = Encoding.UTF8.GetString(output.ToArray()).TrimEnd();
        var response = JsonNode.Parse(responseJson)!.AsObject();
        return response["result"]!["tools"]!.AsArray();
    }
}
