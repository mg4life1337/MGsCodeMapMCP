namespace CodeMap.Daemon;

using System.Collections.Concurrent;

/// <summary>Tracks transport sessions without coupling their lifetime to the daemon.</summary>
public sealed class McpSessionRegistry
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _sessions = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            PruneIdleSessions();
            return _sessions.Count;
        }
    }

    public string Touch(string? sessionId)
    {
        sessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString("N") : sessionId;
        _sessions[sessionId] = DateTimeOffset.UtcNow;
        return sessionId;
    }

    public bool Remove(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _sessions.TryRemove(sessionId, out _);

    private void PruneIdleSessions()
    {
        var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
        foreach (var session in _sessions)
            if (session.Value < cutoff) _sessions.TryRemove(session.Key, out _);
    }
}
