using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Data;
using SkiaSharp;

namespace Ginkgo.Prototype;

/// <summary>
/// Supplies thumbnails to the grid. The UI thread owns the cache (a byte-budgeted LRU); background threads
/// read from the thumbnail packs and decode. Requests for tiles that have scrolled away are dropped.
/// </summary>
public sealed class ThumbLoader : IDisposable
{
    private readonly record struct Req(int Index, int WidthPx);
    private readonly record struct Done(int Index, BitmapSource? Bitmap, long Bytes);
    private sealed record Entry(int Index, BitmapSource Bitmap, long Bytes);

    private readonly ThumbStore _store;
    private readonly int[] _pack; private readonly long[] _off; private readonly int[] _len;
    private readonly Dispatcher _ui;
    private readonly Action _onReady;
    private readonly long _budget;

    private readonly Dictionary<int, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private readonly HashSet<int> _pending = new();
    private readonly ConcurrentStack<Req> _requests = new();
    private readonly ConcurrentQueue<Done> _done = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private long _bytes;
    private int _lo, _hi = int.MaxValue;
    private int _drainQueued;

    public long CachedBytes => _bytes;
    public int CachedCount => _cache.Count;

    public ThumbLoader(ThumbStore store, int[] pack, long[] off, int[] len, Dispatcher ui, Action onReady, long budgetBytes, int threads = 3)
    {
        _store = store; _pack = pack; _off = off; _len = len; _ui = ui; _onReady = onReady; _budget = budgetBytes;
        for (int i = 0; i < threads; i++) new Thread(Work) { IsBackground = true, Name = "thumb-decode" }.Start();
    }

    /// <summary>Which item indexes are worth loading right now.</summary>
    public void SetWanted(int lo, int hi) { _lo = lo; _hi = hi; }

    /// <summary>UI thread only. Returns the thumbnail if it's ready, otherwise asks for it and returns null.</summary>
    public BitmapSource? Get(int index, int widthPx)
    {
        if (_cache.TryGetValue(index, out var node))
        {
            _lru.Remove(node); _lru.AddFirst(node);
            return node.Value.Bitmap;
        }
        if (_pending.Add(index)) { _requests.Push(new Req(index, widthPx)); _signal.Release(); }
        return null;
    }

    private void Work()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { _signal.Wait(_cts.Token); } catch (OperationCanceledException) { return; }
            if (!_requests.TryPop(out var req)) continue; // most recently requested = what's on screen now
            BitmapSource? bmp = null; long bytes = 0;
            if (req.Index >= _lo && req.Index <= _hi && _len[req.Index] > 0)
            {
                try
                {
                    var data = _store.Read(_pack[req.Index], _off[req.Index], _len[req.Index]);
                    if (data is not null) (bmp, bytes) = Decode(data, req.WidthPx);
                }
                catch { bmp = null; }
            }
            _done.Enqueue(new Done(req.Index, bmp, bytes));
            if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Render, Drain);
        }
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
            if (d.Bitmap is null || _cache.ContainsKey(d.Index)) continue;
            var node = _lru.AddFirst(new Entry(d.Index, d.Bitmap, d.Bytes));
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
