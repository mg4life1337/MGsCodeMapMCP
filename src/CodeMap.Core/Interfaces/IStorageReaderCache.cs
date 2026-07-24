namespace CodeMap.Core.Interfaces;

/// <summary>Observability and safe idle trimming for disk-backed storage readers.</summary>
public interface IStorageReaderCache
{
    int OpenBaselineReaderCount { get; }
    int OpenOverlayReaderCount { get; }

    /// <summary>
    /// Closes unleased readers that are idle or above their configured LRU limits.
    /// Files, overlay snapshots, and WAL data are retained.
    /// </summary>
    void TrimIdleReaders();
}
