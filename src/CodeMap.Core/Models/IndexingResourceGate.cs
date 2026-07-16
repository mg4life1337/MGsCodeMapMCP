namespace CodeMap.Core.Models;

/// <summary>Process-wide gate that bounds concurrent full baseline builds.</summary>
public sealed class IndexingResourceGate : IDisposable
{
    private readonly SemaphoreSlim _gate;
    private int _active;
    private int _peakActive;

    public IndexingResourceGate(int maxConcurrentIndexes = 1)
    {
        if (maxConcurrentIndexes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentIndexes));
        MaxConcurrentIndexes = maxConcurrentIndexes;
        _gate = new SemaphoreSlim(maxConcurrentIndexes, maxConcurrentIndexes);
    }

    public int MaxConcurrentIndexes { get; }

    internal int PeakActive => Volatile.Read(ref _peakActive);

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var active = Interlocked.Increment(ref _active);
        UpdatePeak(active);
        return new Lease(this);
    }

    private void UpdatePeak(int active)
    {
        var current = Volatile.Read(ref _peakActive);
        while (active > current)
        {
            var observed = Interlocked.CompareExchange(ref _peakActive, active, current);
            if (observed == current) return;
            current = observed;
        }
    }

    private void Release()
    {
        Interlocked.Decrement(ref _active);
        _gate.Release();
    }

    public void Dispose() => _gate.Dispose();

    private sealed class Lease(IndexingResourceGate owner) : IDisposable
    {
        private IndexingResourceGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
