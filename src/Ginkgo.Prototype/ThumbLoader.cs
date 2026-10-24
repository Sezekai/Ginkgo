using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Core;
using Ginkgo.Data;
using SkiaSharp;

namespace Ginkgo.Prototype;

/// <summary>
/// Supplies thumbnails to the grid.
///  - Decoded pictures (ready to draw) live in a byte-budgeted LRU owned by the UI thread.
///  - Compressed thumbnails live in a second, larger LRU, so scrolling back over recent tiles needs no disk read.
///  - One disk read fetches a whole stretch of neighbouring thumbnails (hard drives are slow at jumping around).
///  - What's on screen is loaded first; tiles ahead in the scroll direction are loaded quietly in the background.
/// </summary>
public sealed class ThumbLoader : IDisposable
{
    /// <summary>Where one size of thumbnail lives, per item. Index 0 = micro, 1 = small, 2 = full.</summary>
    public sealed record TierData(ThumbStore Store, int[] Pack, long[] Off, int[] Len);

    private readonly record struct Req(int Index, int WidthPx, int Tier);
    private readonly record struct Done(int Index, int Tier, BitmapSource? Bitmap, long Bytes);
    private sealed record Entry(int Index, int Tier, BitmapSource Bitmap, long Bytes);

    private readonly TierData[] _tiers;           // 0 micro, 1 small, 2 full, 3 large (filled in on demand)
    private readonly string[] _paths; private readonly int[] _origW;
    private readonly ThumbStore _large; private readonly long _largeCap;
    private readonly Action<int, Loc> _onLargeMade;
    private readonly ConcurrentDictionary<int, byte> _largeFailed = new();
    public long LargeMade;
    private readonly Dispatcher _ui;
    private readonly Action _onReady;
    private readonly long _budget, _encBudget;

    // UI-thread-owned
    private readonly Dictionary<int, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<long, bool> _pending = new(); // key = index*4+size; value: has been asked for as urgent
    private long _bytes;

    // shared with workers
    private readonly ConcurrentStack<Req> _urgent = new();
    private readonly ConcurrentQueue<Req> _prefetch = new();
    private readonly ConcurrentQueue<Done> _done = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private int _lo, _hi = int.MaxValue;
    private int _drainQueued;

    private readonly object _encLock = new();
    private readonly Dictionary<long, LinkedListNode<(long key, byte[] data)>> _enc = new();
    private readonly LinkedList<(long key, byte[] data)> _encLru = new();
    private long _encBytes;

    private long _reads, _readTicks, _decodes, _decodeTicks, _blockThumbs;

    public long CachedBytes => _bytes;
    public int CachedCount => _cache.Count;
    public int PendingCount => _pending.Count;
    public long EncodedBytes => Volatile.Read(ref _encBytes);
    public double AvgReadMs => Volatile.Read(ref _reads) == 0 ? 0 : Volatile.Read(ref _readTicks) * 1000.0 / Stopwatch.Frequency / _reads;
    public double AvgDecodeMs => Volatile.Read(ref _decodes) == 0 ? 0 : Volatile.Read(ref _decodeTicks) * 1000.0 / Stopwatch.Frequency / _decodes;
    public double ThumbsPerRead => Volatile.Read(ref _reads) == 0 ? 0 : (double)Volatile.Read(ref _blockThumbs) / _reads;

    public ThumbLoader(TierData[] tiers, string[] paths, int[] originalWidths, ThumbStore largeStore, long largeCapBytes,
        Action<int, Loc> onLargeMade, Dispatcher ui, Action onReady,
        long decodedBudgetBytes, long encodedBudgetBytes, int threads = 4)
    {
        _tiers = tiers; _paths = paths; _origW = originalWidths; _large = largeStore; _largeCap = largeCapBytes; _onLargeMade = onLargeMade;
        _ui = ui; _onReady = onReady;
        _budget = decodedBudgetBytes; _encBudget = encodedBudgetBytes;
        for (int i = 0; i < threads; i++) new Thread(Work) { IsBackground = true, Name = "thumb-decode" }.Start();
    }

    public void SetWanted(int lo, int hi) { _lo = lo; _hi = hi; }

    /// <summary>Which size is wanted for a tile this many pixels wide: tiny ones need only the micro size, big ones the large size.</summary>
    private static int WantedTier(int widthPx) => widthPx <= 120 ? 0 : widthPx <= 200 ? 1 : widthPx <= 330 ? 2 : 3;

    /// <summary>The wanted size if the item has it, otherwise the next larger size that exists (-1 if none).</summary>
    private int Effective(int index, int wanted)
    {
        for (int k = wanted; k < _tiers.Length; k++) if (Volatile.Read(ref _tiers[k].Len[index]) > 0) return k;
        return -1;
    }

    /// <summary>
    /// Decides what to show now (<paramref name="show"/>) and whether a large version should be made/loaded (<paramref name="large"/>).
    /// </summary>
    private bool Resolve(int index, int widthPx, out int show, out bool large)
    {
        int want = WantedTier(widthPx);
        large = false;
        if (want == 3 && (_origW[index] <= 340 || _largeFailed.ContainsKey(index))) want = 2;
        if (want == 3)
        {
            if (Volatile.Read(ref _tiers[3].Len[index]) > 0) { show = 3; return true; }
            large = true;                      // make it; show the 320 px one meanwhile
            show = Effective(index, 2);
            return show >= 0 || _origW[index] > 0;
        }
        show = Effective(index, want);
        return show >= 0;
    }

    private void Push(int index, int widthPx, int tier, bool urgent)
    {
        long key = (long)index * 4 + tier;
        if (_pending.TryGetValue(key, out bool u)) { if (!urgent || u) return; }
        _pending[key] = urgent;
        var req = new Req(index, widthPx, tier);
        if (urgent) _urgent.Push(req); else _prefetch.Enqueue(req);
        _signal.Release();
    }

    /// <summary>UI thread only. Returns the picture if it's ready, otherwise asks for it right now and returns null.</summary>
    public BitmapSource? Get(int index, int widthPx)
    {
        if (!Resolve(index, widthPx, out int show, out bool large)) return null;
        BitmapSource? have = null; int haveTier = -1;
        if (_cache.TryGetValue(index, out var node))
        {
            _lru.Remove(node); _lru.AddFirst(node);
            have = node.Value.Bitmap; haveTier = node.Value.Tier;
        }
        if (show >= 0 && haveTier < show) Push(index, widthPx, show, true);
        if (large && haveTier < 3) Push(index, widthPx, 3, true);
        return have;
    }

    /// <summary>UI thread only. Quietly gets a tile ready before it is needed.</summary>
    public void Prefetch(int index, int widthPx)
    {
        if (!Resolve(index, widthPx, out int show, out bool large)) return;
        int haveTier = _cache.TryGetValue(index, out var node) ? node.Value.Tier : -1;
        if (show >= 0 && haveTier < show) Push(index, widthPx, show, false);
        if (large && haveTier < 3) Push(index, widthPx, 3, false);
    }

    private void Work()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { _signal.Wait(_cts.Token); } catch (OperationCanceledException) { return; }
            if (!_urgent.TryPop(out var req) && !_prefetch.TryDequeue(out req)) continue;
            BitmapSource? bmp = null; long bytes = 0;
            if (req.Index >= _lo && req.Index <= _hi)
            {
                try
                {
                    var data = GetEncoded(req.Index, req.Tier);
                    if (data is not null)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        (bmp, bytes) = Decode(data, req.WidthPx);
                        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _decodes);
                    }
                }
                catch { bmp = null; }
            }
            _done.Enqueue(new Done(req.Index, req.Tier, bmp, bytes));
            if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Render, Drain);
        }
    }

    private byte[]? GetEncoded(int index, int tier)
    {
        long key = (long)index * 4 + tier;
        lock (_encLock)
        {
            if (_enc.TryGetValue(key, out var n)) { _encLru.Remove(n); _encLru.AddFirst(n); return n.Value.data; }
        }
        var t = _tiers[tier];
        if (tier == 3 && Volatile.Read(ref t.Len[index]) == 0) return MakeLarge(index);
        // tiny thumbnails are cheap to carry, so grab many neighbours per read
        var plan = tier == 0 ? ReadPlanner.Make(t.Pack, t.Off, t.Len, index, 64, 256)
                 : tier == 1 ? ReadPlanner.Make(t.Pack, t.Off, t.Len, index, 16, 64)
                 : ReadPlanner.Make(t.Pack, t.Off, t.Len, index);
        long t0 = Stopwatch.GetTimestamp();
        var block = t.Store.Read(plan.Pack, plan.Start, plan.Length);
        Interlocked.Add(ref _readTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _reads);
        if (block is null) return null;
        byte[]? mine = null;
        lock (_encLock)
        {
            for (int j = plan.FirstIndex; j <= plan.LastIndex; j++)
            {
                if (!ReadPlanner.Covers(plan, t.Pack, t.Off, t.Len, j)) continue;
                var slice = new byte[t.Len[j]];
                Buffer.BlockCopy(block, (int)(t.Off[j] - plan.Start), slice, 0, slice.Length);
                long k = (long)j * 4 + tier;
                if (j == index) mine = slice;
                if (_enc.ContainsKey(k)) continue;
                _enc[k] = _encLru.AddFirst((k, slice)); _encBytes += slice.Length;
                Interlocked.Increment(ref _blockThumbs);
            }
            while (_encBytes > _encBudget && _encLru.Last is { } last)
            {
                _encLru.RemoveLast(); _enc.Remove(last.Value.key); _encBytes -= last.Value.data.Length;
            }
        }
        return mine;
    }

    private byte[]? MakeLarge(int index)
    {
        if (_large.Size > _largeCap) { _largeFailed.TryAdd(index, 0); return null; } // size limit reached: keep showing 320 px
        try
        {
            var bytes = ThumbMaker.MakeLarge(_paths[index]);
            var loc = _large.Append(bytes);
            var t = _tiers[3];
            t.Pack[index] = loc.pack; t.Off[index] = loc.offset;
            Volatile.Write(ref t.Len[index], loc.length);   // last: readers only look once the length is set
            Interlocked.Increment(ref LargeMade);
            _onLargeMade(index, new Loc(loc.pack, loc.offset, loc.length));
            return bytes;
        }
        catch { _largeFailed.TryAdd(index, 0); return null; }
    }

    private static (BitmapSource?, long) Decode(byte[] data, int widthPx)
    {
        using var src = SKBitmap.Decode(data);
        if (src is null) return (null, 0);
        SKBitmap use = src; SKBitmap? resized = null;
        if (widthPx > 16 && src.Width > widthPx * 1.1)
        {
            int h = Math.Max(1, (int)Math.Round(src.Height * (widthPx / (double)src.Width)));
            resized = src.Resize(new SKImageInfo(widthPx, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
            if (resized is not null) use = resized;
        }
        if (use.ColorType != SKColorType.Bgra8888 || use.AlphaType != SKAlphaType.Premul)
        {
            var conv = use.Copy(SKColorType.Bgra8888);
            if (!ReferenceEquals(use, src)) use.Dispose();
            use = conv; resized = conv;
        }
        var bmp = BitmapSource.Create(use.Width, use.Height, 96, 96, PixelFormats.Pbgra32, null, use.GetPixels(), use.ByteCount, use.RowBytes);
        bmp.Freeze();
        long bytes = use.ByteCount;
        resized?.Dispose();
        return (bmp, bytes);
    }

    private void Drain()
    {
        Volatile.Write(ref _drainQueued, 0);
        bool any = false;
        while (_done.TryDequeue(out var d))
        {
            _pending.Remove((long)d.Index * 4 + d.Tier);
            if (d.Bitmap is null) continue;
            if (_cache.TryGetValue(d.Index, out var old))
            {
                if (old.Value.Tier >= d.Tier) continue;      // already have an equal or sharper one
                _lru.Remove(old); _cache.Remove(d.Index); _bytes -= old.Value.Bytes;
            }
            var node = _lru.AddFirst(new Entry(d.Index, d.Tier, d.Bitmap, d.Bytes));
            _cache[d.Index] = node; _bytes += d.Bytes; any = true;
        }
        while (_bytes > _budget && _lru.Last is { } last)
        {
            _lru.RemoveLast(); _cache.Remove(last.Value.Index); _bytes -= last.Value.Bytes;
        }
        if (any) _onReady();
    }

    public void Dispose() { _cts.Cancel(); }
}
