namespace CodeMap.Storage.Engine;

using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Mutable string dictionary for baseline construction.
/// Interns strings and assigns 1-based StringIds. StringId 0 = null/empty.
/// Call <see cref="Build"/> to serialize to dictionary.seg and get a reader.
/// </summary>
internal sealed class DictionaryBuilder : IDictionaryBuilder
{
    private readonly Dictionary<string, int> _map = new(StringComparer.Ordinal);
    private readonly List<string> _values = [];
    private bool _built;

    /// <inheritdoc />
    public int Count => _map.Count;

    /// <inheritdoc />
    public int Intern(string value)
    {
        ObjectDisposedException.ThrowIf(_built, this);

        if (string.IsNullOrEmpty(value))
            return 0;

        if (_map.TryGetValue(value, out var existing))
            return existing;

        var id = _map.Count + 1; // 1-based
        _map[value] = id;
        _values.Add(value);
        return id;
    }

    /// <inheritdoc />
    public int Intern(ReadOnlySpan<byte> utf8Value)
    {
        ObjectDisposedException.ThrowIf(_built, this);

        if (utf8Value.IsEmpty)
            return 0;

        var str = Encoding.UTF8.GetString(utf8Value);
        return Intern(str);
    }

    /// <inheritdoc />
    public IDictionaryReader Build(string targetPath)
    {
        ObjectDisposedException.ThrowIf(_built, this);
        _built = true;

        var count = _map.Count;

        // Compute offset table (Count + 1 entries of uint32)
        var offsets = new uint[count + 1];
        uint runningOffset = 0;
        for (var i = 0; i < count; i++)
        {
            offsets[i] = runningOffset;
            runningOffset += (uint)Encoding.UTF8.GetByteCount(_values[i]);
        }
        offsets[count] = runningOffset; // sentinel = total blob size

        // Write file: [SegmentFileHeader 16B][OffsetTable uint32×(Count+1)][DataBlob]
        // File must be fully closed before DictionaryReader can mmap it.
        using (var fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: false))
        {
            // SegmentFileHeader
            bw.Write(StorageConstants.SegmentMagic);
            bw.Write((ushort)StorageConstants.FormatMajor);
            bw.Write((ushort)StorageConstants.FormatMinor);
            bw.Write((uint)count);
            bw.Write(0u); // Reserved

            // OffsetTable
            foreach (var offset in offsets)
                bw.Write(offset);

            // DataBlob
            foreach (var value in _values)
            {
                var byteCount = Encoding.UTF8.GetByteCount(value);
                var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    var written = Encoding.UTF8.GetBytes(value, buffer);
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

        _map.Clear();
        _values.Clear();
        return new DictionaryReader(targetPath);
    }

    public void Dispose()
    {
        // No unmanaged resources; just mark as unusable.
        _built = true;
        _map.Clear();
        _values.Clear();
    }
}
