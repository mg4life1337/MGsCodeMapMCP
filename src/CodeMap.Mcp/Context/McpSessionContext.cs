namespace CodeMap.Mcp.Context;

/// <summary>Async-flow-local MCP session context shared by singleton handlers.</summary>
public sealed class McpSessionContext : IMcpSessionContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string? CurrentSessionId => _current.Value;

    public IDisposable Enter(string sessionId)
    {
        var previous = _current.Value;
        _current.Value = sessionId;
        return new Scope(this, previous);
    }

    private sealed class Scope(McpSessionContext owner, string? previous) : IDisposable
    {
        private McpSessionContext? _owner = owner;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is not null) current._current.Value = previous;
        }
    }
}
