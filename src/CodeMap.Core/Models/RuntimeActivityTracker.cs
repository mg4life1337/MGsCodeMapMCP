namespace CodeMap.Core.Models;

/// <summary>
/// Process-wide activity counters used to keep reader eviction and explicit memory
/// reclamation away from active MCP requests, incremental updates, and publications.
/// </summary>
public sealed class RuntimeActivityTracker
{
    private int _activeRequests;
    private int _activeIncrementalUpdates;
    private int _activePublications;
    private long _lastRequestActivityUtcTicks = DateTimeOffset.UtcNow.UtcTicks;

    public int ActiveRequests => Volatile.Read(ref _activeRequests);
    public int ActiveIncrementalUpdates => Volatile.Read(ref _activeIncrementalUpdates);
    public int ActivePublications => Volatile.Read(ref _activePublications);
    public DateTimeOffset LastRequestActivityUtc =>
        new(Interlocked.Read(ref _lastRequestActivityUtcTicks), TimeSpan.Zero);

    public IDisposable BeginRequest()
    {
        Interlocked.Exchange(ref _lastRequestActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Increment(ref _activeRequests);
        return new ActivityLease(
            () =>
            {
                Interlocked.Decrement(ref _activeRequests);
                Interlocked.Exchange(ref _lastRequestActivityUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
            });
    }

    public IDisposable BeginIncrementalUpdate()
    {
        Interlocked.Increment(ref _activeIncrementalUpdates);
        return new ActivityLease(() => Interlocked.Decrement(ref _activeIncrementalUpdates));
    }

    public IDisposable BeginPublication()
    {
        Interlocked.Increment(ref _activePublications);
        return new ActivityLease(() => Interlocked.Decrement(ref _activePublications));
    }

    private sealed class ActivityLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
