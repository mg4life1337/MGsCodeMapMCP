namespace CodeMap.Core.Models;

using System.Diagnostics;

/// <summary>
/// Lightweight ambient sampler for one indexing operation. The ambient scope flows through
/// async calls and Task.Run, allowing Roslyn and storage to report phase boundaries without
/// adding telemetry parameters to their public APIs.
/// </summary>
public sealed class IndexMemoryTelemetry : IAsyncDisposable
{
    private static readonly AsyncLocal<IndexMemoryTelemetry?> Ambient = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string, MemorySnapshot>? _phaseSink;
    private readonly Task _sampler;
    private readonly IndexMemoryTelemetry? _previous;
    private long _peakWorkingSet;
    private long _peakPrivateMemory;
    private long _peakManagedHeap;
    private int _completed;

    private IndexMemoryTelemetry(Action<string, MemorySnapshot>? phaseSink)
    {
        _phaseSink = phaseSink;
        _previous = Ambient.Value;
        Ambient.Value = this;
        Sample();
        _sampler = SampleUntilStoppedAsync(_stop.Token);
    }

    public static IndexMemoryTelemetry Start(Action<string, MemorySnapshot>? phaseSink = null) =>
        new(phaseSink);

    public static void MarkPhase(string phase) => Ambient.Value?.Mark(phase);

    public IndexResourceUsage Complete(int maxParallelProjects, int maxConcurrentIndexes)
    {
        Sample();
        Interlocked.Exchange(ref _completed, 1);
        return new IndexResourceUsage(
            Volatile.Read(ref _peakWorkingSet),
            Volatile.Read(ref _peakPrivateMemory),
            Volatile.Read(ref _peakManagedHeap),
            maxParallelProjects,
            maxConcurrentIndexes);
    }

    private void Mark(string phase)
    {
        var snapshot = Sample();
        _phaseSink?.Invoke(phase, snapshot);
    }

    private async Task SampleUntilStoppedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Sample();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private MemorySnapshot Sample()
    {
        using var process = Process.GetCurrentProcess();
        var snapshot = new MemorySnapshot(
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false));
        UpdateMaximum(ref _peakWorkingSet, snapshot.WorkingSetBytes);
        UpdateMaximum(ref _peakPrivateMemory, snapshot.PrivateMemoryBytes);
        UpdateMaximum(ref _peakManagedHeap, snapshot.ManagedHeapBytes);
        return snapshot;
    }

    private static void UpdateMaximum(ref long target, long value)
    {
        var current = Volatile.Read(ref target);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref target, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await _sampler.ConfigureAwait(false); }
        finally
        {
            _stop.Dispose();
            if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        }
    }

    public readonly record struct MemorySnapshot(
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        long ManagedHeapBytes);
}
