namespace CodeMap.Mcp;

using Microsoft.Extensions.DependencyInjection;
using CodeMap.Mcp.Context;

/// <summary>
/// DI registration extension for the hand-rolled MCP server.
/// Call <see cref="AddMcpServer"/> in the Daemon's ServiceRegistration,
/// then resolve <see cref="McpServer"/> and call RunAsync to start.
/// </summary>
public static class McpServerSetup
{
    /// <summary>
    /// Registers <see cref="ToolRegistry"/> and <see cref="McpServer"/> as singletons.
    /// Tool handlers are registered separately via the returned <see cref="IServiceCollection"/>.
    /// </summary>
    public static IServiceCollection AddMcpServer(this IServiceCollection services)
    {
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<IMcpSessionContext, McpSessionContext>();
        services.AddSingleton<McpServer>();
        return services;
    }
}
