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
    private volatile bool _dirty;   // something was written but may still be sitting in the write buffer
    private readonly ConcurrentDictionary<int, SafeFileHandle> _readers = new();

    public ThumbStore(string directory, string prefix = "pack")
    {
        _dir = directory; _prefix = prefix;
        System.IO.Directory.CreateDirectory(_dir);
    }

    /// <summary>Starts writing into a fresh pack file; returns its number. Everything appended from now on lands in packs with this number or higher.</summary>
    public int RollToNewPack()
    {
        lock (_lock)
        {
            int next = (_writer is null ? LatestPack() : _writerPack) + 1;
            if (_writer is not null) { _writer.Flush(); _writer.Dispose(); _dirty = false; }
            _writerPack = next;
            _writer = new FileStream(PackPath(_writerPack), FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            Interlocked.Exchange(ref _size, -1);
            return next;
        }
    }

    /// <summary>Deletes pack files numbered below <paramref name="firstKept"/>. Only call once the index no longer points into them.</summary>
    public void DeletePacksBefore(int firstKept)
    {
        lock (_lock)
        {
            foreach (var f in System.IO.Directory.EnumerateFiles(_dir, _prefix + "-*.bin"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(_prefix.Length + 1), out int n) || n >= firstKept) continue;
                if (_readers.TryRemove(n, out var h)) h.Dispose();
                DropPack(n);
                try { File.Delete(f); } catch { }
            }
            Interlocked.Exchange(ref _size, -1);
        }
    }

    private string PackPath(int pack) => Path.Combine(_dir, $"{_prefix}-{pack:D5}.bin");

    private int LatestPack()
    {
        int best = 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, _prefix + "-*.bin"))
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
                _writer.Flush(); _writer.Dispose(); _dirty = false;
                _writerPack++;
                _writer = new FileStream(PackPath(_writerPack), FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 16);
            }
            long off = _writer.Position;
            _writer.Write(data);
            _dirty = true;
            if (Volatile.Read(ref _size) >= 0) Interlocked.Add(ref _size, data.Length);
            return (_writerPack, off, data.Length);
        }
    }

    /// <summary>Makes sure everything appended so far is really in the file. Call before recording locations in the index.</summary>
    public void Flush() { lock (_lock) { _writer?.Flush(); _dirty = false; } }

    // ---- whole pack files held in memory: lookups then never touch the drive ----
    private readonly ConcurrentDictionary<int, byte[]> _mem = new();
    private long _memBytes;
    /// <summary>Bytes of this store currently held in memory.</summary>
    public long MemoryBytes => Interlocked.Read(ref _memBytes);

    /// <summary>The pack files of this store, oldest first, with their sizes.</summary>
    public List<(int pack, long size)> Packs()
    {
        var list = new List<(int, long)>();
        if (!System.IO.Directory.Exists(_dir)) return list;
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, _prefix + "-*.bin"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(_prefix.Length + 1), out int n)) list.Add((n, new FileInfo(f).Length));
        list.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return list;
    }

    public bool IsInMemory(int pack) => _mem.ContainsKey(pack);

    /// <summary>Reads a whole pack file into memory in one long sequential read (fast even on a hard drive).</summary>
    public bool LoadPack(int pack, Action<long>? progress, CancellationToken ct)
    {
        if (_mem.ContainsKey(pack)) return true;
        if (_dirty) Flush();
        string path = PackPath(pack);
        if (!File.Exists(path)) return false;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        long size = fs.Length;
        if (size <= 0 || size > int.MaxValue - 64) return false;
        var data = GC.AllocateUninitializedArray<byte>((int)size);
        int pos = 0;
        while (pos < size)
        {
            ct.ThrowIfCancellationRequested();
            int n = fs.Read(data, pos, (int)Math.Min(8 << 20, size - pos));
            if (n <= 0) break;
            pos += n; progress?.Invoke(n);
        }
        if (pos < size) return false;
        if (_mem.TryAdd(pack, data)) Interlocked.Add(ref _memBytes, size);
        return true;
    }

    /// <summary>Lets go of a pack held in memory (lookups go back to the drive).</summary>
    public void DropPack(int pack) { if (_mem.TryRemove(pack, out var a)) Interlocked.Add(ref _memBytes, -a.Length); }
    public void DropAll() { foreach (var k in _mem.Keys.ToList()) DropPack(k); }

    public byte[]? Read(int pack, long offset, int length)
    {
        if (length <= 0) return null;
        if (_mem.TryGetValue(pack, out var m) && offset >= 0 && offset + length <= m.Length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(m, (int)offset, copy, 0, length);
            return copy;
        }
        if (_dirty) Flush(); // a thumbnail written a moment ago may not be in the file yet
        var handle = _readers.GetOrAdd(pack, p =>
            File.OpenHandle(PackPath(p), FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        var buf = new byte[length];
        int got = RandomAccess.Read(handle, buf, offset);
        return got == length ? buf : null;
    }

    public long TotalBytes()
    {
        long t = 0;
        foreach (var f in System.IO.Directory.EnumerateFiles(_dir, _prefix + "-*.bin")) t += new FileInfo(f).Length;
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

    /// <param name="directory">Where the micro, small and full thumbnails are kept (best on a fast drive).</param>
    /// <param name="largeDirectory">Where the big sharp versions are kept (null = same place).</param>
    public ThumbStores(string directory, string? largeDirectory = null)
    {
        Micro = new ThumbStore(directory, "micro");
        Small = new ThumbStore(directory, "small");
        Full = new ThumbStore(directory, "pack");
        Large = new ThumbStore(largeDirectory ?? directory, "large");
    }

    public void Flush() { Micro.Flush(); Small.Flush(); Full.Flush(); Large.Flush(); }
    public long TotalBytes() => Micro.TotalBytes() + Small.TotalBytes() + Full.TotalBytes() + Large.TotalBytes();
    public void Dispose() { Micro.Dispose(); Small.Dispose(); Full.Dispose(); Large.Dispose(); }
}
