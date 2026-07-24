namespace CodeMap.Core.Models;

using System.Diagnostics;
using System.Runtime;
using CodeMap.Core.Interfaces;
using Microsoft.Extensions.Logging;

/// <summary>
/// Debounces one explicit managed-memory reclaim after a full-index batch becomes idle.
/// A scheduled reclaim is replaced by the next full-index completion, so a repository
/// containing many solutions is collected once rather than once per solution.
/// </summary>
public sealed class FullIndexMemoryReclaimer : IDisposable
{
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DefaultRequestQuietPeriod = TimeSpan.FromSeconds(120);
    private readonly IndexingResourceConfig _config;
    private readonly IndexingResourceGate _indexing;
    private readonly RuntimeActivityTracker _activity;
    private readonly IStorageReaderCache? _readers;
    private readonly ILogger<FullIndexMemoryReclaimer> _logger;
    private readonly TimeSpan _debounceDelay;
    private readonly TimeSpan _requestQuietPeriod;
    private readonly object _gate = new();
    private CancellationTokenSource? _scheduled;
    private bool _disposed;

    public FullIndexMemoryReclaimer(
        IndexingResourceConfig config,
        IndexingResourceGate indexing,
        RuntimeActivityTracker activity,
        ILogger<FullIndexMemoryReclaimer> logger,
        IStorageReaderCache? readers = null,
        TimeSpan? debounceDelay = null,
        TimeSpan? requestQuietPeriod = null)
    {
        _config = config;
        _indexing = indexing;
        _activity = activity;
        _logger = logger;
        _readers = readers;
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        _requestQuietPeriod = requestQuietPeriod ?? DefaultRequestQuietPeriod;
    }

    /// <summary>Schedules (or reschedules) reclamation for the current full-index batch.</summary>
    public void Schedule()
    {
        if (!_config.ReleaseMemoryAfterFullIndex) return;

        CancellationTokenSource current;
        lock (_gate)
        {
            if (_disposed) return;
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            current = new CancellationTokenSource();
            _scheduled = current;
        }

        _ = Task.Run(() => RunScheduledAsync(current), CancellationToken.None);
    }

    private async Task RunScheduledAsync(CancellationTokenSource scheduled)
    {
        try
        {
            // Let the storage idle timeout expire first so reader lookup tables and
            // memory-mapped views are closed before compacting the managed heap.
            var readerSettleDelay = TimeSpan.FromSeconds(_config.StorageReaderIdleSeconds);
            var initialDelay = _debounceDelay >= readerSettleDelay
                ? _debounceDelay
                : readerSettleDelay;
            await Task.Delay(initialDelay, scheduled.Token).ConfigureAwait(false);
            while (!scheduled.IsCancellationRequested)
            {
                var quietFor = DateTimeOffset.UtcNow - _activity.LastRequestActivityUtc;
                var idle = _indexing.ActiveIndexes == 0 &&
                    _activity.ActivePublications == 0 &&
                    _activity.ActiveIncrementalUpdates == 0 &&
                    _activity.ActiveRequests == 0;
                if (idle && quietFor >= _requestQuietPeriod)
                    break;

                var remainingQuiet = _requestQuietPeriod - quietFor;
                var delay = remainingQuiet > TimeSpan.Zero && remainingQuiet < TimeSpan.FromSeconds(5)
                    ? remainingQuiet
                    : TimeSpan.FromSeconds(5);
                await Task.Delay(delay, scheduled.Token).ConfigureAwait(false);
            }

            await ReclaimIfEligibleAsync(scheduled.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (scheduled.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Full-index memory reclaim failed");
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_scheduled, scheduled))
                {
                    _scheduled = null;
                    scheduled.Dispose();
                }
            }
        }
    }

    internal Task<bool> ReclaimIfEligibleAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_indexing.ActiveIndexes != 0 ||
            _activity.ActivePublications != 0 ||
            _activity.ActiveIncrementalUpdates != 0 ||
            _activity.ActiveRequests != 0)
            return Task.FromResult(false);

        _readers?.TrimIdleReaders();
        var before = MemorySnapshot.Capture();
        var thresholdBytes = _config.MemoryReclaimMinimumManagedHeapMb * 1024L * 1024L;
        if (before.ManagedHeapBytes < thresholdBytes)
        {
            _logger.LogInformation(
                "MEMORY_RECLAIM skipped=true reason=below_threshold working_set_mb={WorkingSetMb:F1} private_mb={PrivateMb:F1} managed_heap_mb={ManagedHeapMb:F1} fragmented_mb={FragmentedMb:F1} threshold_mb={ThresholdMb}",
                ToMb(before.WorkingSetBytes),
                ToMb(before.PrivateMemoryBytes),
                ToMb(before.ManagedHeapBytes),
                ToMb(before.FragmentedBytes),
                _config.MemoryReclaimMinimumManagedHeapMb);
            return Task.FromResult(false);
        }

        _logger.LogInformation(
            "MEMORY_RECLAIM phase=before working_set_mb={WorkingSetMb:F1} private_mb={PrivateMb:F1} managed_heap_mb={ManagedHeapMb:F1} fragmented_mb={FragmentedMb:F1}",
            ToMb(before.WorkingSetBytes),
            ToMb(before.PrivateMemoryBytes),
            ToMb(before.ManagedHeapBytes),
            ToMb(before.FragmentedBytes));

        var watch = Stopwatch.StartNew();
        // Drain finalizers before the one explicit collection so objects that
        // were already awaiting finalization can be reclaimed by that collection.
        GC.WaitForPendingFinalizers();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        // Aggressive is intentionally reserved for this fully idle boundary. In
        // addition to compacting, it asks the runtime to decommit unused GC
        // segments instead of retaining several gigabytes after large solutions.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        watch.Stop();

        var after = MemorySnapshot.Capture();
        _logger.LogInformation(
            "MEMORY_RECLAIM phase=after working_set_mb={WorkingSetMb:F1} private_mb={PrivateMb:F1} managed_heap_mb={ManagedHeapMb:F1} fragmented_mb={FragmentedMb:F1} duration_ms={DurationMs:F1}",
            ToMb(after.WorkingSetBytes),
            ToMb(after.PrivateMemoryBytes),
            ToMb(after.ManagedHeapBytes),
            ToMb(after.FragmentedBytes),
            watch.Elapsed.TotalMilliseconds);
        return Task.FromResult(true);
    }

    private static double ToMb(long bytes) => bytes / 1048576d;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _scheduled?.Cancel();
            _scheduled?.Dispose();
            _scheduled = null;
        }
    }

    private readonly record struct MemorySnapshot(
        long WorkingSetBytes,
        long PrivateMemoryBytes,
        long ManagedHeapBytes,
        long FragmentedBytes)
    {
        public static MemorySnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            var gc = GC.GetGCMemoryInfo();
            return new MemorySnapshot(
                process.WorkingSet64,
                process.PrivateMemorySize64,
                gc.HeapSizeBytes,
                gc.FragmentedBytes);
        }
    }
}
