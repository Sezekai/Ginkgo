using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;

namespace Ginkgo.Data;

/// <summary>
/// Thumbnails packed end to end into a few big files (pack-00000.bin, ...). A hard drive reads one big file far
/// faster than hundreds of thousands of tiny ones. Where each thumbnail sits is recorded in the index.
/// </summary>
/// <summary>
/// A second place to keep pieces of the pack files when memory runs short (the graphics card's own memory). Slower to read from
/// than memory, far faster than the drive. Keys are the store's piece keys.
/// </summary>
public interface ISecondHome
{
    bool Has(ThumbStore store, long key);
    /// <summary>Copies <paramref name="dst"/>.Length bytes starting <paramref name="inSeg"/> bytes into the piece.</summary>
    bool Read(ThumbStore store, long key, int inSeg, Span<byte> dst);
    /// <summary>Keeps a copy of a piece (false: no room, or not available).</summary>
    bool Put(ThumbStore store, long key, IntPtr data, int length);
    void Remove(ThumbStore store, long key);
    void RemovePack(ThumbStore store, int pack);
}

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
                Second?.RemovePack(this, n);
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

    // ---- pieces of the pack files held in memory: lookups there never touch the drive. Pieces (32 MB) rather than whole
    // files, so whatever memory is free can be used, and the pieces holding what is being looked at can be chosen. ----
    public const int SegSize = 32 << 20;
    // Pieces live outside the garbage-collected heap, so letting one go hands the memory straight back to Windows.
    private sealed class Seg { public IntPtr P; public int Len; }
    private readonly ConcurrentDictionary<long, Seg> _mem = new();
    private readonly ReaderWriterLockSlim _memLock = new(LockRecursionPolicy.NoRecursion);
    private readonly ConcurrentDictionary<long, int> _hits = new();
    private long _memBytes;
    public static long Key(int pack, int seg) => ((long)pack << 20) | (uint)seg;
    /// <summary>Where pieces go when memory is short (null: nowhere; they are read from the drive again).</summary>
    public ISecondHome? Second { get; set; }
    public bool InSecond(int pack, int seg) => Second?.Has(this, Key(pack, seg)) ?? false;
    public void DropSecond(int pack, int seg) => Second?.Remove(this, Key(pack, seg));

    /// <summary>Moves a piece held in memory to the second home (copied there first, then let go here).</summary>
    public bool MoveToSecond(int pack, int seg)
    {
        var sec = Second; long key = Key(pack, seg);
        if (sec is null) return false;
        bool ok = sec.Has(this, key);
        if (!ok)
        {
            _memLock.EnterReadLock();
            try { if (_mem.TryGetValue(key, out var m)) ok = sec.Put(this, key, m.P, m.Len); }
            finally { _memLock.ExitReadLock(); }
        }
        if (ok) DropKey(key);
        return ok;
    }

    /// <summary>Reads one piece from the drive straight into the second home (not into memory).</summary>
    public bool LoadToSecond(int pack, int seg, CancellationToken ct)
    {
        var sec = Second; long key = Key(pack, seg);
        if (sec is null) return false;
        if (sec.Has(this, key)) return true;
        var (ptr, size) = ReadPiece(pack, seg, ct);
        if (ptr == IntPtr.Zero) return false;
        try { return sec.Put(this, key, ptr, size); }
        finally { Free(ptr); }
    }

    /// <summary>One piece of a pack read into native memory (one sequential read), or zero.</summary>
    private (IntPtr ptr, int size) ReadPiece(int pack, int seg, CancellationToken ct)
    {
        if (_dirty) Flush();
        string path = PackPath(pack);
        if (!File.Exists(path)) return (IntPtr.Zero, 0);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan);
        long start = (long)seg * SegSize;
        int size = (int)Math.Min(SegSize, fs.Length - start);
        if (size <= 0) return (IntPtr.Zero, 0);
        IntPtr ptr;
        unsafe { ptr = (IntPtr)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)size); }
        try
        {
            fs.Position = start;
            int pos = 0;
            while (pos < size)
            {
                ct.ThrowIfCancellationRequested();
                int n;
                unsafe { n = fs.Read(new Span<byte>((byte*)ptr + pos, Math.Min(4 << 20, size - pos))); }
                if (n <= 0) break;
                pos += n;
            }
            if (pos < size) { Free(ptr); return (IntPtr.Zero, 0); }
        }
        catch { Free(ptr); throw; }
        return (ptr, size);
    }
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

    /// <summary>Every 32 MB piece of every pack: (pack, piece, size).</summary>
    public List<(int pack, int seg, int size)> Segments()
    {
        var list = new List<(int, int, int)>();
        foreach (var (pack, size) in Packs())
            for (long o = 0; o < size; o += SegSize) list.Add((pack, (int)(o / SegSize), (int)Math.Min(SegSize, size - o)));
        return list;
    }

    public bool IsInMemory(int pack, int seg) => _mem.ContainsKey(Key(pack, seg));
    public bool IsInMemory(int pack) => _mem.Keys.Any(k => (int)(k >> 20) == pack);
    /// <summary>How often a piece was asked for lately (decays when <see cref="DecayHits"/> is called).</summary>
    public int Hits(int pack, int seg) => _hits.TryGetValue(Key(pack, seg), out int h) ? h : 0;
    public void DecayHits() { foreach (var k in _hits.Keys) _hits.AddOrUpdate(k, 0, (_, v) => v / 2); }

    /// <summary>Reads one piece of a pack into memory (a single sequential read: fast even on a hard drive).</summary>
    public bool LoadSegment(int pack, int seg, CancellationToken ct)
    {
        long key = Key(pack, seg);
        if (_mem.ContainsKey(key)) return true;
        var (ptr, size) = ReadPiece(pack, seg, ct);
        if (ptr == IntPtr.Zero) return false;
        if (_mem.TryAdd(key, new Seg { P = ptr, Len = size })) Interlocked.Add(ref _memBytes, size); else Free(ptr);
        return true;
    }

    /// <summary>Lets go of a piece held in memory (lookups there go back to the drive).</summary>
    public void DropSegment(int pack, int seg) => DropKey(Key(pack, seg));
    public void DropPack(int pack) { foreach (var k in _mem.Keys.Where(k => (int)(k >> 20) == pack).ToList()) DropKey(k); }
    public void DropAll() { foreach (var k in _mem.Keys.ToList()) DropKey(k); }
    private void DropKey(long k)
    {
        _memLock.EnterWriteLock();      // nobody is copying out of it while it goes
        try { if (_mem.TryRemove(k, out var a)) { Interlocked.Add(ref _memBytes, -a.Len); Free(a.P); } }
        finally { _memLock.ExitWriteLock(); }
    }
    private static unsafe void Free(IntPtr p) => System.Runtime.InteropServices.NativeMemory.Free((void*)p);

    /// <summary>Test helper: loads every piece of one pack.</summary>
    public bool LoadPack(int pack, Action<long>? progress, CancellationToken ct)
    {
        foreach (var (p, seg, size) in Segments()) if (p == pack) { if (!LoadSegment(p, seg, ct)) return false; progress?.Invoke(size); }
        return true;
    }

    public byte[]? Read(int pack, long offset, int length)
    {
        if (length <= 0) return null;
        if (offset >= 0)
        {
            int seg = (int)(offset / SegSize), segEnd = (int)((offset + length - 1) / SegSize);
            _hits.AddOrUpdate(Key(pack, seg), 1, (_, v) => v + 1);
            if (_mem.ContainsKey(Key(pack, seg)))
            {
                _memLock.EnterReadLock();
                try
                {
                    if (_mem.TryGetValue(Key(pack, seg), out var m))
                    {
                        int inSeg = (int)(offset - (long)seg * SegSize);
                        if (segEnd == seg && inSeg + length <= m.Len)
                        {
                            var copy = new byte[length];
                            unsafe { new ReadOnlySpan<byte>((byte*)m.P + inSeg, length).CopyTo(copy); }
                            return copy;
                        }
                        if (segEnd == seg + 1 && _mem.TryGetValue(Key(pack, segEnd), out var m2))
                        {
                            int first = m.Len - inSeg;
                            if (first > 0 && length - first <= m2.Len)
                            {
                                var copy = new byte[length];
                                unsafe
                                {
                                    new ReadOnlySpan<byte>((byte*)m.P + inSeg, first).CopyTo(copy);
                                    new ReadOnlySpan<byte>((byte*)m2.P, length - first).CopyTo(copy.AsSpan(first));
                                }
                                return copy;
                            }
                        }
                    }
                }
                finally { _memLock.ExitReadLock(); }
            }
        }
        if (offset >= 0 && Second is { } sec)
        {
            int seg = (int)(offset / SegSize);
            long key = Key(pack, seg);
            int inSeg = (int)(offset - (long)seg * SegSize);
            if (inSeg + length <= SegSize && sec.Has(this, key))
            {
                var buf2 = new byte[length];
                if (sec.Read(this, key, inSeg, buf2)) return buf2;
            }
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
        DropAll();
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
