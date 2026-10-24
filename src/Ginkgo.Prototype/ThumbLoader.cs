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

    private readonly TierData[] _tiers;
    private readonly Dispatcher _ui;
    private readonly Action _onReady;
    private readonly long _budget, _encBudget;

    // UI-thread-owned
    private readonly Dictionary<int, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly Dictionary<int, bool> _pending = new(); // value: has been asked for as urgent
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

    public ThumbLoader(TierData[] tiers, Dispatcher ui, Action onReady,
        long decodedBudgetBytes, long encodedBudgetBytes, int threads = 3)
    {
        _tiers = tiers; _ui = ui; _onReady = onReady;
        _budget = decodedBudgetBytes; _encBudget = encodedBudgetBytes;
        for (int i = 0; i < threads; i++) new Thread(Work) { IsBackground = true, Name = "thumb-decode" }.Start();
    }

    public void SetWanted(int lo, int hi) { _lo = lo; _hi = hi; }

    /// <summary>Which size is wanted for a tile this many pixels wide: tiny ones need only the micro size.</summary>
    private static int WantedTier(int widthPx) => widthPx <= 120 ? 0 : widthPx <= 200 ? 1 : 2;

    /// <summary>The wanted size if the item has it, otherwise the next larger size that exists (-1 if none).</summary>
    private int Effective(int index, int wanted)
    {
        for (int k = wanted; k < _tiers.Length; k++) if (_tiers[k].Len[index] > 0) return k;
        return -1;
    }

    /// <summary>UI thread only. Returns the picture if it's ready, otherwise asks for it right now and returns null.</summary>
    public BitmapSource? Get(int index, int widthPx)
    {
        int tier = Effective(index, WantedTier(widthPx));
        if (tier < 0) return null;
        BitmapSource? have = null;
        if (_cache.TryGetValue(index, out var node))
        {
            _lru.Remove(node); _lru.AddFirst(node);
            have = node.Value.Bitmap;
            if (node.Value.Tier >= tier) return have; // already sharp enough
        }
        // not here yet, or here at a size that is now too small (zoomed in): ask for the right one, show what we have meanwhile
        if (!_pending.TryGetValue(index, out bool urgent) || !urgent)
        {
            _pending[index] = true; _urgent.Push(new Req(index, widthPx, tier)); _signal.Release();
        }
        return have;
    }

    /// <summary>UI thread only. Quietly gets a tile ready before it is needed.</summary>
    public void Prefetch(int index, int widthPx)
    {
        int tier = Effective(index, WantedTier(widthPx));
        if (tier < 0 || _pending.ContainsKey(index)) return;
        if (_cache.TryGetValue(index, out var node) && node.Value.Tier >= tier) return;
        _pending[index] = false;
        _prefetch.Enqueue(new Req(index, widthPx, tier));
        _signal.Release();
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
            _pending.Remove(d.Index);
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
