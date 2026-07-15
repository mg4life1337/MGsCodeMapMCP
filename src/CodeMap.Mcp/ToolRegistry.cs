namespace CodeMap.Mcp;

using System.Text.Json.Nodes;

/// <summary>
/// Registry of all MCP tools available on this server.
/// Handlers register tools here during DI setup.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _tools =
        new(StringComparer.Ordinal);

    /// <summary>Registers (or replaces) a tool definition.</summary>
    public void Register(ToolDefinition tool)
    {
        // Every repository-bound tool accepts a solution selector. Existing tool-specific
        // definitions (notably index.ensure_baseline/workspace.create) take precedence.
        if (tool.InputSchema["properties"] is JsonObject properties)
        {
            properties["solution_path"] ??= new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional repository-relative or absolute solution path. Required when multiple solutions are indexed for the same repository and commit.",
            };
            properties["solution_id"] ??= new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional stable solution identifier returned by index.ensure_baseline or index.list_baselines.",
            };
        }

        _tools[tool.Name] = tool;
    }

    /// <summary>Returns all registered tools in registration order.</summary>
    public IReadOnlyList<ToolDefinition> GetAll() => [.. _tools.Values];

    /// <summary>Finds a tool by name, or returns null if not found.</summary>
    public ToolDefinition? Find(string name) =>
        _tools.TryGetValue(name, out var t) ? t : null;

    /// <summary>Number of registered tools.</summary>
    public int Count => _tools.Count;
}
