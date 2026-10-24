using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Ginkgo.Data;

/// <summary>
/// Thumbnails packed end to end into a few big files (pack-00000.bin, ...). A hard drive reads one big file far
/// faster than hundreds of thousands of tiny ones. Where each thumbnail sits is recorded in the index.
/// </summary>
public sealed class ThumbStore : IDisposable
{
    private const long PackLimit = 512L * 1024 * 1024;
    private readonly string _dir;
    private readonly string _prefix;
    private readonly object _lock = new();
    private long _size = -1;
    /// <summary>Total bytes in this store's files (kept up to date as thumbnails are added).</summary>
    public long Size { get { if (Volatile.Read(ref _size) < 0) Interlocked.CompareExchange(ref _size, TotalBytes(), -1); return Volatile.Read(ref _size); } }
    private FileStream? _writer;
    private int _writerPack = -1;
    private readonly ConcurrentDictionary<int, SafeFileHandle> _readers = new();

    public ThumbStore(string directory, string prefix = "pack")
    {
        _dir = directory; _prefix = prefix;
        Directory.CreateDirectory(_dir);
    }

    private string PackPath(int pack) => Path.Combine(_dir, $"{_prefix}-{pack:D5}.bin");

    private int LatestPack()
    {
        int best = 0;
        foreach (var f in Directory.EnumerateFiles(_dir, _prefix + "-*.bin"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(_prefix.Length + 1), out int n) && n > best) best = n;
        return best;
    }

    public (int pack, long offset, int length) Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            if (_writer is null)
            {
                _writerPack = LatestPack();
                _writer = new FileStream(PackPath(_writerPack), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
                _writer.Seek(0, SeekOrigin.End);
            }
            else if (_writer.Position >= PackLimit)
            {
                _writer.Flush(); _writer.Dispose();
                _writerPack++;
                _writer = new FileStream(PackPath(_writerPack), FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            }
            long off = _writer.Position;
            _writer.Write(data);
            if (Volatile.Read(ref _size) >= 0) Interlocked.Add(ref _size, data.Length);
            return (_writerPack, off, data.Length);
        }
    }

    /// <summary>Makes sure everything appended so far is really in the file. Call before recording locations in the index.</summary>
    public void Flush() { lock (_lock) _writer?.Flush(); }

    public byte[]? Read(int pack, long offset, int length)
    {
        if (length <= 0) return null;
        var handle = _readers.GetOrAdd(pack, p =>
            File.OpenHandle(PackPath(p), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var buf = new byte[length];
        int got = RandomAccess.Read(handle, buf, offset);
        return got == length ? buf : null;
    }

    public long TotalBytes()
    {
        long t = 0;
        foreach (var f in Directory.EnumerateFiles(_dir, _prefix + "-*.bin")) t += new FileInfo(f).Length;
        return t;
    }

    public void Dispose()
    {
        lock (_lock) { _writer?.Flush(); _writer?.Dispose(); _writer = null; }
        foreach (var h in _readers.Values) h.Dispose();
        _readers.Clear();
    }
}

/// <summary>The three thumbnail sizes, each packed into its own set of files.</summary>
public sealed class ThumbStores : IDisposable
{
    public ThumbStore Micro { get; }   // ~112 px wide: for the smallest grid sizes
    public ThumbStore Small { get; }   // ~192 px wide
    public ThumbStore Full { get; }    // ~320 px wide
    public ThumbStore Large { get; }   // up to 960 px wide, made on demand when a tile is shown big

    public ThumbStores(string directory)
    {
        Micro = new ThumbStore(directory, "micro");
        Small = new ThumbStore(directory, "small");
        Full = new ThumbStore(directory, "pack");
        Large = new ThumbStore(directory, "large");
    }

    public void Flush() { Micro.Flush(); Small.Flush(); Full.Flush(); Large.Flush(); }
    public long TotalBytes() => Micro.TotalBytes() + Small.TotalBytes() + Full.TotalBytes() + Large.TotalBytes();
    public void Dispose() { Micro.Dispose(); Small.Dispose(); Full.Dispose(); Large.Dispose(); }
}
