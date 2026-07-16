namespace CodeMap.Daemon;

/// <summary>Small process-status surface used by health and shutdown management.</summary>
public sealed class DaemonRuntimeState
{
    private int _stopping;
    private long _requests;

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public bool IsStopping => Volatile.Read(ref _stopping) != 0;
    public long RequestCount => Interlocked.Read(ref _requests);

    public bool BeginStopping() => Interlocked.Exchange(ref _stopping, 1) == 0;
    public void RequestObserved() => Interlocked.Increment(ref _requests);
}
