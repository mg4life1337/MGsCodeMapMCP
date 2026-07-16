namespace CodeMap.Mcp.Context;

/// <summary>Associates handler execution with one MCP transport session.</summary>
public interface IMcpSessionContext
{
    string? CurrentSessionId { get; }
    IDisposable Enter(string sessionId);
}
