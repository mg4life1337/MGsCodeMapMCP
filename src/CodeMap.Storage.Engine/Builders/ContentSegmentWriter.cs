namespace CodeMap.Storage.Engine;

using System.Buffers;
using System.Text;
using CodeMap.Core.Interfaces;

/// <summary>
/// Writes file body text to content.seg per STORAGE-FORMAT.MD §4A.
/// Layout: [SegmentFileHeader][OffsetTable: uint64 × (Count+1)][ContentBlob: packed UTF-8].
/// ContentIds are 1-based; ContentId 0 = no content.
/// </summary>
internal static class ContentSegmentWriter
{
    /// <summary>
    /// Writes content entries to the specified path. Entries must be in ContentId order (1-based).
    /// </summary>
    public static void Write(string path, IReadOnlyList<byte[]> utf8Contents)
    {
        var count = utf8Contents.Count;

        // Compute uint64 offset table
        var offsets = new ulong[count + 1];
        ulong running = 0;
        for (var i = 0; i < count; i++)
        {
            offsets[i] = running;
            running += (ulong)utf8Contents[i].Length;
        }
        offsets[count] = running; // sentinel

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);

        // SegmentFileHeader (16 bytes)
        bw.Write(StorageConstants.SegmentMagic);
        bw.Write((ushort)StorageConstants.FormatMajor);
        bw.Write((ushort)StorageConstants.FormatMinor);
        bw.Write((uint)count);
        bw.Write(0u); // Reserved

        // OffsetTable (uint64 × (count + 1))
        foreach (var offset in offsets)
            bw.Write(offset);

        // ContentBlob
        foreach (var content in utf8Contents)
            bw.Write(content);

        bw.Flush();
        fs.Flush(true);
    }

    /// <summary>
    /// Writes source text directly from extracted file records. UTF-8 buffers are rented one
    /// at a time instead of retaining a second byte-array copy of every source file.
    /// </summary>
    public static void WriteFiles(string path, IReadOnlyList<ExtractedFile> files)
    {
        var contentCount = files.Count(file => file.Content is not null);
        var offsets = new ulong[contentCount + 1];
        ulong running = 0;
        var contentIndex = 0;
        foreach (var file in files)
        {
            if (file.Content is null) continue;
            offsets[contentIndex++] = running;
            running += (ulong)Encoding.UTF8.GetByteCount(file.Content);
        }
        offsets[contentCount] = running;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false);
        bw.Write(StorageConstants.SegmentMagic);
        bw.Write((ushort)StorageConstants.FormatMajor);
        bw.Write((ushort)StorageConstants.FormatMinor);
        bw.Write((uint)contentCount);
        bw.Write(0u);
        foreach (var offset in offsets) bw.Write(offset);

        foreach (var file in files)
        {
            if (file.Content is null) continue;
            var content = file.Content;
            var byteCount = Encoding.UTF8.GetByteCount(content);
            var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                var written = Encoding.UTF8.GetBytes(content, buffer);
                bw.Write(buffer, 0, written);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        bw.Flush();
        fs.Flush(true);
    }
}
