namespace CodeMap.Mcp.Context;

using System.Collections.Concurrent;
using CodeMap.Core.Models;
using CodeMap.Core.Types;

/// <summary>
/// Thread-safe default <see cref="IWorkspaceStickyRegistry"/> backed by a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>. Keys normalize repo paths the
/// same way <see cref="RepoRegistry"/> does so both registries agree on identity.
/// </summary>
public sealed class WorkspaceStickyRegistry : IWorkspaceStickyRegistry
{
    private readonly IMcpSessionContext? _sessionContext;
    private readonly IRollingGenerationRegistry _rollingGenerations;
    private readonly ConcurrentDictionary<string, string> _sticky =
        new(StringComparer.OrdinalIgnoreCase);
    private Action<string, SolutionId>? _solutionRequested;

    public WorkspaceStickyRegistry(IMcpSessionContext? sessionContext = null)
        : this(new NullRollingGenerationRegistry(), sessionContext) { }

    public WorkspaceStickyRegistry(
        IRollingGenerationRegistry rollingGenerations,
        IMcpSessionContext? sessionContext = null)
    {
        _rollingGenerations = rollingGenerations;
        _sessionContext = sessionContext;
    }

    /// <inheritdoc/>
    public void Set(string repoPath, string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(workspaceId)) return;
        _sticky[SessionKey(repoPath)] = workspaceId;
    }

    /// <inheritdoc/>
    public void Clear(string repoPath, string workspaceId)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || string.IsNullOrWhiteSpace(workspaceId)) return;
        var key = SessionKey(repoPath);
        // Conditional remove: only clear if the sticky currently matches the deleted workspace.
        if (_sticky.TryGetValue(key, out var current) && string.Equals(current, workspaceId, StringComparison.Ordinal))
            _sticky.TryRemove(new KeyValuePair<string, string>(key, current));
    }

    /// <inheritdoc/>
    public string? Get(string repoPath)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;
        return _sticky.TryGetValue(SessionKey(repoPath), out var ws) ? ws : null;
    }

    /// <inheritdoc/>
    public string? Get(string repoPath, SolutionId solutionId)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) return null;
        return Resolve(repoPath, solutionId).WorkspaceId?.Value ?? Get(repoPath);
    }

    /// <inheritdoc/>
    public RollingGenerationResolution Resolve(string repoPath, SolutionId solutionId)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            return new(RollingGenerationAvailability.NotReady, null, null, false);
        Volatile.Read(ref _solutionRequested)?.Invoke(repoPath, solutionId);
        return _rollingGenerations.Resolve(repoPath, solutionId);
    }

    /// <inheritdoc/>
    public void SetSolutionRequestedCallback(Action<string, SolutionId>? callback) =>
        Volatile.Write(ref _solutionRequested, callback);

    private static string Normalize(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/');

    private string SessionKey(string path) =>
        (_sessionContext?.CurrentSessionId ?? "stdio") + "|" + Normalize(path);

}
