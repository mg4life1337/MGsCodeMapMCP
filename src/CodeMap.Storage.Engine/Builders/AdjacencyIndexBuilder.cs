namespace CodeMap.Storage.Engine;

using System.Runtime.InteropServices;

/// <summary>
/// Builds adjacency-out.idx and adjacency-in.idx per STORAGE-FORMAT.MD §11.
/// Layout: [SegmentFileHeader][HeaderTable: uint32 × (MaxSymbolId+2)][PostingsRegion].
/// HeaderTable[i] = byte offset into PostingsRegion for symbol i (0 = no edges).
/// Each postings block: [uint32 Count][int32 × Count: sorted EdgeIntIds].
/// </summary>
internal static class AdjacencyIndexBuilder
{
    /// <summary>
    /// Builds both adjacency index files from the edge records.
    /// </summary>
    public static void Build(string outPath, string inPath, ReadOnlySpan<EdgeRecord> edges, int maxSymbolId)
    {
        // Build and release one direction at a time. Keeping both posting maps alive
        // duplicates the largest adjacency intermediate on reference-heavy solutions.
        BuildDirection(outPath, edges, maxSymbolId, outgoing: true);
        BuildDirection(inPath, edges, maxSymbolId, outgoing: false);
    }

    /// <summary>Array overload for convenience.</summary>
    public static void Build(string outPath, string inPath, EdgeRecord[] edges, int maxSymbolId)
        => Build(outPath, inPath, (ReadOnlySpan<EdgeRecord>)edges, maxSymbolId);

    private static void BuildDirection(
        string path,
        ReadOnlySpan<EdgeRecord> edges,
        int maxSymbolId,
        bool outgoing)
    {
        var adjacency = new Dictionary<int, List<int>>();
        foreach (ref readonly var edge in edges)
        {
            var symbolId = outgoing ? edge.FromSymbolIntId : edge.ToSymbolIntId;
            if (symbolId <= 0) continue;
            if (!adjacency.TryGetValue(symbolId, out var list))
            {
                list = [];
                adjacency[symbolId] = list;
            }
            list.Add(edge.EdgeIntId);
        }
        WriteIndex(path, adjacency, maxSymbolId);
    }

    private static void WriteIndex(string path, Dictionary<int, List<int>> adjacency, int maxSymbolId)
    {
        // Sort each posting list
        foreach (var list in adjacency.Values)
            list.Sort();

        var headerTable = new uint[maxSymbolId + 2]; // indices [0..maxSymbolId+1]
        var recordCount = adjacency.Count;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var postingsStart = StorageConstants.SegFileHeaderSize +
            (long)headerTable.Length * sizeof(uint);
        fs.Position = postingsStart;
        Span<byte> intBuf = stackalloc byte[4];

        for (var symbolId = 1; symbolId <= maxSymbolId; symbolId++)
        {
            if (!adjacency.TryGetValue(symbolId, out var edgeIds))
            {
                headerTable[symbolId] = 0; // no edges
                continue;
            }

            // Block offset relative to PostingsRegion start
            // +1 because offset 0 means "no edges", so we use 1-based offsets
            headerTable[symbolId] = checked((uint)(fs.Position - postingsStart)) + 1;

            // Write [Count][EdgeIntIds...]
            BitConverter.TryWriteBytes(intBuf, edgeIds.Count);
            fs.Write(intBuf);

            foreach (var edgeId in edgeIds)
            {
                BitConverter.TryWriteBytes(intBuf, edgeId);
                fs.Write(intBuf);
            }
            adjacency.Remove(symbolId);
        }

        // Sentinel: total postings region size + 1
        headerTable[maxSymbolId + 1] = checked((uint)(fs.Position - postingsStart)) + 1;

        fs.Position = 0;
        Span<byte> header = stackalloc byte[StorageConstants.SegFileHeaderSize];
        BitConverter.TryWriteBytes(header, StorageConstants.SegmentMagic);
        BitConverter.TryWriteBytes(header[4..], (ushort)StorageConstants.FormatMajor);
        BitConverter.TryWriteBytes(header[6..], (ushort)StorageConstants.FormatMinor);
        BitConverter.TryWriteBytes(header[8..], (uint)recordCount);
        BitConverter.TryWriteBytes(header[12..], 0u);
        fs.Write(header);

        fs.Write(MemoryMarshal.AsBytes(headerTable.AsSpan()));
        fs.Flush(true);
    }

    /// <summary>
    /// Reads edge IDs for a given symbol from an adjacency index file.
    /// Returns empty array if symbol has no edges.
    /// </summary>
    public static int[] ReadEdgeIds(byte[] fileBytes, int symbolIntId, int maxSymbolId)
    {
        var headerTableStart = StorageConstants.SegFileHeaderSize;
        var headerTable = MemoryMarshal.Cast<byte, uint>(
            fileBytes.AsSpan(headerTableStart, (maxSymbolId + 2) * sizeof(uint)));

        var blockOffset = headerTable[symbolIntId];
        if (blockOffset == 0) return [];

        var postingsBase = headerTableStart + (maxSymbolId + 2) * sizeof(uint);
        var pos = postingsBase + (int)(blockOffset - 1); // -1 because 1-based offset

        var count = BitConverter.ToInt32(fileBytes.AsSpan(pos));
        pos += 4;

        var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = BitConverter.ToInt32(fileBytes.AsSpan(pos));
            pos += 4;
        }
        return result;
    }
}
