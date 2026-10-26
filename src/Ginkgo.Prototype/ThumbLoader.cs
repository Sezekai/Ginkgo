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
/// Supplies pictures to the grid.
///  - Each tile is decoded at exactly its on-screen pixel size (no resampling while scrolling = no shimmer).
///  - What is on screen is loaded first. A "plan" of what to warm up next (ahead of the scroll direction first, then behind,
///    nearest first) is replaced every few tenths of a second, so work that is no longer useful is simply dropped.
///  - Compressed thumbnails sit in a second cache; one disk read fetches a whole stretch of neighbours.
///  - A better version of a tile fades in over the old one.
/// </summary>
public sealed class ThumbLoader : IDisposable
{
    public sealed record TierData(ThumbStore Store, int[] Pack, long[] Off, int[] Len);   // 0 micro, 1 small, 2 full, 3 large

    public sealed class Entry
    {
        public int Index, Tier, PixelWidth;
        public BitmapSource Bitmap = null!;
        public BitmapSource? Prev;      // what was shown before, while fading to this one
        public long Bytes, FadeStart;
        public double FadeSecs;         // 0 = no fade
        public bool Drawn;
    }

    public readonly record struct PlanItem(int Index, int WidthPx, int HeightPx, int Tier, bool EncodedOnly, bool MakeOnly = false);

    private sealed class Plan { public PlanItem[] Items = Array.Empty<PlanItem>(); public int Next; }
    private readonly record struct Req(int Index, int WidthPx, int HeightPx, int Tier, bool Urgent, bool EncodedOnly, bool MakeOnly = false);
    private readonly record struct Done(int Index, int Tier, BitmapSource? Bitmap, long Bytes, bool Urgent);

    private static readonly int[] Nominal = { ThumbMaker.MicroW, ThumbMaker.SmallW, ThumbMaker.MaxW, ThumbMaker.LargeW };

    private readonly TierData[] _tiers;
    private readonly string[] _paths; private readonly int[] _origW;
    private readonly ThumbStore _large; private readonly long _largeCap;
    private readonly Action<int, Loc> _onLargeMade;
    private readonly ConcurrentDictionary<int, byte> _largeFailed = new();
    private readonly Dispatcher _ui;
    private readonly Action _onReady;
    private long _budget, _encBudget;

    /// <summary>Change the memory limits while running (takes effect as the caches next trim themselves).</summary>
    public void SetBudgets(long decodedBytes, long encodedBytes) { Volatile.Write(ref _budget, decodedBytes); Volatile.Write(ref _encBudget, encodedBytes); }

    // UI-thread-owned
    private readonly Dictionary<int, LinkedListNode<Entry>> _cache = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _bytes;

    // shared with workers
    private readonly ConcurrentDictionary<long, byte> _inflight = new();   // key = index*4+tier: queued or being worked on
    private readonly ConcurrentStack<Req> _urgent = new();        // quick work for tiles on screen (reading a thumbnail): newest request first
    private readonly ConcurrentStack<Req> _urgentLarge = new();   // slow work for tiles on screen (reading an original to make a sharp version)
    private int _quickActive;                                     // quick jobs being worked on right now
    private long _lastQuick;                                      // when quick work was last asked for or finished
    private int _laActive;                                        // look-ahead jobs running right now
    private long _lastScroll;                                     // when the view last moved
    private readonly ConcurrentDictionary<int, byte> _laMade = new();   // sharp versions made by look-ahead and not yet seen on screen
    public long LookAheadMade, LookAheadUsed;
    public string LookAheadSummary => $"look-ahead: made {Volatile.Read(ref LookAheadMade):N0}, already ready when scrolled to {Volatile.Read(ref LookAheadUsed):N0}, running {Volatile.Read(ref _laActive)}";
    private int _largeActive;                                     // how many workers are making sharp versions right now
    private readonly ConcurrentQueue<Done> _done = new();
    private Plan _plan = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _cts = new();
    private int _lo, _hi = int.MaxValue;
    private int _drainQueued;

    private readonly object _encLock = new();
    private readonly Dictionary<long, LinkedListNode<(long key, byte[] data)>> _enc = new();
    private readonly LinkedList<(long key, byte[] data)> _encLru = new();
    private long _encBytes;

    private long _sharpJobs, _sharpDecodeTicks, _sharpPresentTicks, _sharpSaveTicks;
    /// <summary>How long making sharp versions takes: reading+decoding the original / fitting it to the tile / saving it (milliseconds, averaged).</summary>
    public string SharpSummary
    {
        get
        {
            long n = Volatile.Read(ref _sharpJobs);
            if (n == 0) return "sharp: none made yet";
            double f = 1000.0 / Stopwatch.Frequency / n;
            return $"sharp ({n:N0} made): read+decode {Volatile.Read(ref _sharpDecodeTicks) * f:F0} ms, fit {Volatile.Read(ref _sharpPresentTicks) * f:F0} ms, save {Volatile.Read(ref _sharpSaveTicks) * f:F0} ms";
        }
    }

    private long _reads, _readTicks, _decodes, _decodeTicks, _blockThumbs;
    public long LargeMade;
    /// <summary>Crossfade when a sharper version replaces the one shown. (Tiles appearing for the first time never fade.)</summary>
    public volatile bool FadeEnabled = true;

    public long CachedBytes => _bytes;
    public int CachedCount => _cache.Count;
    public int PendingCount => _inflight.Count;
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
        for (int i = 0; i < threads; i++) new Thread(Work) { IsBackground = true, Name = "thumb-decode", Priority = ThreadPriority.BelowNormal }.Start();
    }

    public void SetWanted(int lo, int hi) { _lo = lo; _hi = hi; }

    /// <summary>Called whenever the view moves; look-ahead only starts new work after the view has been still for a moment.</summary>
    public void NoteScroll() => Volatile.Write(ref _lastScroll, Stopwatch.GetTimestamp());

    /// <summary>Removes requests for tiles that have scrolled away, so they neither count as waiting nor sit in the way.</summary>
    public void PurgeStale()
    {
        int lo = _lo, hi = _hi;
        foreach (var stack in new[] { _urgent, _urgentLarge })
        {
            var items = new List<Req>();
            while (stack.TryPop(out var r)) items.Add(r);          // newest first
            for (int k = items.Count - 1; k >= 0; k--)             // push oldest first so the newest ends up on top again
            {
                var r = items[k];
                if (r.Index < lo || r.Index > hi) _inflight.TryRemove((long)r.Index * 4 + r.Tier, out _);
                else stack.Push(r);
            }
        }
    }

    /// <summary>Replaces the list of things to warm up. Anything not yet started from the previous list is dropped.</summary>
    public void SetPlan(PlanItem[] items) => Volatile.Write(ref _plan, new Plan { Items = items });

    // ---- which size, what to show ---------------------------------------------------------------------------

    private static int WantedTier(int widthPx) => widthPx <= 120 ? 0 : widthPx <= 200 ? 1 : widthPx <= 330 ? 2 : 3;

    private int Effective(int index, int wanted)
    {
        for (int k = wanted; k < _tiers.Length; k++) if (Volatile.Read(ref _tiers[k].Len[index]) > 0) return k;
        return -1;
    }

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
            return true;
        }
        show = Effective(index, want);
        return show >= 0;
    }

    /// <summary>True if a picture already in the cache is the wrong size for this tile (or too low a tier).</summary>
    private bool NeedsRefresh(Entry e, int show, int widthPx)
    {
        if (e.Tier < show) return true;
        int need = Math.Min(Math.Min(widthPx, Nominal[e.Tier]), Math.Max(1, _origW[e.Index]));
        int w = e.PixelWidth;
        return w > need * 1.15 || w < need * 0.9;
    }

    private bool TryQueue(int index, int w, int h, int tier)
    {
        long key = (long)index * 4 + tier;
        if (!_inflight.TryAdd(key, 0)) return false;
        var req = new Req(index, w, h, tier, true, false);
        if (tier == 3 && Volatile.Read(ref _tiers[3].Len[index]) == 0) _urgentLarge.Push(req);
        else { Volatile.Write(ref _lastQuick, Stopwatch.GetTimestamp()); _urgent.Push(req); }
        _signal.Release();
        return true;
    }

    /// <summary>UI thread only. The picture for a tile if there is one (asks for it / a better one if needed).</summary>
    public Entry? Get(int index, int widthPx, int heightPx)
    {
        if (!Resolve(index, widthPx, out int show, out bool large)) return null;
        if (show == 3 && !_laMade.IsEmpty && _laMade.TryRemove(index, out _)) Interlocked.Increment(ref LookAheadUsed);
        Entry? e = null;
        if (_cache.TryGetValue(index, out var node)) { _lru.Remove(node); _lru.AddFirst(node); e = node.Value; e.Drawn = true; }
        if (show >= 0 && (e is null || NeedsRefresh(e, show, widthPx))) TryQueue(index, widthPx, heightPx, show);
        if (large && (e is null || e.Tier < 3)) TryQueue(index, widthPx, heightPx, 3);
        return e;
    }

    /// <summary>UI thread only. Adds what this tile still needs to a warm-up list.</summary>
    public void AppendPlan(int index, int widthPx, int heightPx, List<PlanItem> list, bool decodeToo, bool largeToo)
    {
        if (!Resolve(index, widthPx, out int show, out bool large)) return;
        _cache.TryGetValue(index, out var node);
        var e = node?.Value;
        if (show >= 0 && (e is null || NeedsRefresh(e, show, widthPx)) && !_inflight.ContainsKey((long)index * 4 + show))
        {
            list.Add(new PlanItem(index, widthPx, heightPx, show, !decodeToo));
        }
        if (decodeToo && largeToo && large && (e is null || e.Tier < 3) && !_inflight.ContainsKey((long)index * 4 + 3))
            list.Add(new PlanItem(index, widthPx, heightPx, 3, false));
    }

    /// <summary>UI thread only. Look-ahead: just make and save the sharp version for a tile that will probably be needed (no picture is built).</summary>
    public void AppendMakeOnly(int index, int widthPx, int heightPx, List<PlanItem> list)
    {
        if (!Resolve(index, widthPx, out _, out bool large) || !large) return;      // no sharp version needed, or it already exists
        if (_inflight.ContainsKey((long)index * 4 + 3)) return;
        list.Add(new PlanItem(index, widthPx, heightPx, 3, false, true));
    }

    // ---- workers --------------------------------------------------------------------------------------------

    /// <summary>
    /// Making a sharp version reads an original from the hard drive, which slows everything else down. So it only starts when
    /// no quick work is waiting or running and nothing quick was asked for in the last 100 ms (i.e. scrolling has settled).
    /// </summary>
    private bool SharpMayStart()
    {
        if (Volatile.Read(ref _largeActive) >= 3 || Volatile.Read(ref _largeActive) + Volatile.Read(ref _laActive) >= 4 || Volatile.Read(ref _quickActive) > 0 || !_urgent.IsEmpty) return false;
        return (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastQuick)) * 1000 / Stopwatch.Frequency > 100;
    }

    /// <summary>
    /// Look-ahead is the most patient work: at most 2 jobs, leaving room for what's on screen, and only when nothing quick
    /// was asked for and the view hasn't moved for 300 ms.
    /// </summary>
    private bool LookAheadMayStart()
    {
        if (Volatile.Read(ref _laActive) >= 2 || Volatile.Read(ref _largeActive) + Volatile.Read(ref _laActive) >= 3
            || Volatile.Read(ref _quickActive) > 0 || !_urgent.IsEmpty) return false;
        long now = Stopwatch.GetTimestamp(), ms = Stopwatch.Frequency / 1000;
        return now - Volatile.Read(ref _lastQuick) > 300 * ms && now - Volatile.Read(ref _lastScroll) > 300 * ms;
    }

    private void Work()
    {
        while (!_cts.IsCancellationRequested)
        {
            Req req = default; bool have = false; bool makingLarge = false, quick = false, makeOnly = false;
            if (_urgent.TryPop(out req)) { have = true; quick = true; Interlocked.Increment(ref _quickActive); }   // thumbnails for what's on screen: always first
            else if (SharpMayStart() && _urgentLarge.TryPop(out req)) have = true;
            else
            {
                var plan = Volatile.Read(ref _plan);
                while (true)
                {
                    int k = Interlocked.Increment(ref plan.Next) - 1;
                    if (k >= plan.Items.Length) break;
                    var it = plan.Items[k];
                    bool slow = it.Tier == 3 && Volatile.Read(ref _tiers[3].Len[it.Index]) == 0;
                    if (it.MakeOnly) { if (!_urgentLarge.IsEmpty || !LookAheadMayStart()) continue; }   // look-ahead is the most patient work of all
                    else if (slow && (!_urgentLarge.IsEmpty || !SharpMayStart())) continue;               // sharp versions wait until the screen is settled
                    if (!_inflight.TryAdd((long)it.Index * 4 + it.Tier, 0)) continue;
                    req = new Req(it.Index, it.WidthPx, it.HeightPx, it.Tier, false, it.EncodedOnly, it.MakeOnly);
                    have = true; break;
                }
            }
            if (have && req.MakeOnly) { makeOnly = true; Interlocked.Increment(ref _laActive); }
            else if (have && req.Tier == 3 && Volatile.Read(ref _tiers[3].Len[req.Index]) == 0) { makingLarge = true; Interlocked.Increment(ref _largeActive); }
            BitmapSource? bmp = null; long bytes = 0;
            bool stale = req.Urgent && (req.Index < _lo || req.Index > _hi);
            if (!stale)
            {
                try
                {
                    if (req.MakeOnly) MakeLargeOnly(req.Index);
                    else if (makingLarge && !req.EncodedOnly)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        (bmp, bytes) = MakeLargeAndPresent(req);       // show it first, save it afterwards
                        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _decodes);
                    }
                    else
                    {
                    var data = GetEncoded(req.Index, req.Tier);
                    if (data is not null && !req.EncodedOnly)
                    {
                        long t0 = Stopwatch.GetTimestamp();
                        (bmp, bytes) = Decode(data, req.WidthPx, req.HeightPx);
                        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _decodes);
                    }
                    }
                }
                catch { bmp = null; }
            }
            if (makingLarge) Interlocked.Decrement(ref _largeActive);
            if (makeOnly) Interlocked.Decrement(ref _laActive);
            if (quick) { Interlocked.Decrement(ref _quickActive); Volatile.Write(ref _lastQuick, Stopwatch.GetTimestamp()); }
            _done.Enqueue(new Done(req.Index, req.Tier, bmp, bytes, req.Urgent));
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

    /// <summary>Reads the original, builds the picture for the tile right away, then saves the sharp version for next time.</summary>
    private (BitmapSource?, long) MakeLargeAndPresent(Req req)
    {
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            using var full = ThumbMaker.DecodeLarge(_paths[req.Index]);
            long t1 = Stopwatch.GetTimestamp();
            var result = Present(full, req.WidthPx, req.HeightPx);
            long t2 = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _sharpDecodeTicks, t1 - t0); Interlocked.Add(ref _sharpPresentTicks, t2 - t1); Interlocked.Increment(ref _sharpJobs);
            if (_large.Size > _largeCap) { _largeFailed.TryAdd(req.Index, 0); return result; }  // size limit reached: not saved
            // hand the picture to the screen first; saving comes after
            var enc = ThumbMaker.EncodeLarge(full);
            var loc = _large.Append(enc);
            var t = _tiers[3];
            t.Pack[req.Index] = loc.pack; t.Off[req.Index] = loc.offset;
            Volatile.Write(ref t.Len[req.Index], loc.length);   // last: readers only look once the length is set
            Interlocked.Increment(ref LargeMade);
            Interlocked.Add(ref _sharpSaveTicks, Stopwatch.GetTimestamp() - t2);
            _onLargeMade(req.Index, new Loc(loc.pack, loc.offset, loc.length));
            return result;
        }
        catch { _largeFailed.TryAdd(req.Index, 0); return (null, 0); }
    }

    /// <summary>Makes and saves the sharp version without building a picture for the screen.</summary>
    private void MakeLargeOnly(int index)
    {
        try
        {
            if (Volatile.Read(ref _tiers[3].Len[index]) > 0) return;     // already made meanwhile
            if (_large.Size > _largeCap) return;
            var bytes = ThumbMaker.MakeLarge(_paths[index]);
            var loc = _large.Append(bytes);
            var t = _tiers[3];
            t.Pack[index] = loc.pack; t.Off[index] = loc.offset;
            Volatile.Write(ref t.Len[index], loc.length);
            Interlocked.Increment(ref LargeMade); Interlocked.Increment(ref LookAheadMade);
            _laMade.TryAdd(index, 0);
            _onLargeMade(index, new Loc(loc.pack, loc.offset, loc.length));
        }
        catch { _largeFailed.TryAdd(index, 0); }
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

    /// <summary>Decodes a thumbnail at exactly the tile's pixel size (never bigger than the thumbnail itself).</summary>
    private static (BitmapSource?, long) Decode(byte[] data, int widthPx, int heightPx)
    {
        using var src = SKBitmap.Decode(data);
        return src is null ? (null, 0) : Present(src, widthPx, heightPx);
    }

    /// <summary>Turns a decoded bitmap into a picture at exactly the tile's pixel size (never bigger than the bitmap itself).</summary>
    private static (BitmapSource?, long) Present(SKBitmap src, int widthPx, int heightPx)
    {
        SKBitmap use = src; SKBitmap? temp = null;
        if (widthPx > 16 && src.Width > widthPx * 1.05)
        {
            int h = heightPx > 0 ? heightPx : Math.Max(1, (int)Math.Round(src.Height * (widthPx / (double)src.Width)));
            temp = src.Resize(new SKImageInfo(widthPx, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
            if (temp is not null) use = temp;
        }
        if (use.ColorType != SKColorType.Bgra8888 || use.AlphaType != SKAlphaType.Premul)
        {
            var conv = use.Copy(SKColorType.Bgra8888);
            temp?.Dispose();
            use = conv; temp = conv;
        }
        var bmp = BitmapSource.Create(use.Width, use.Height, 96, 96, PixelFormats.Pbgra32, null, use.GetPixels(), use.ByteCount, use.RowBytes);
        bmp.Freeze();
        long bytes = use.ByteCount;
        temp?.Dispose();
        return (bmp, bytes);
    }

    // ---- UI thread ------------------------------------------------------------------------------------------

    private void Drain()
    {
        Volatile.Write(ref _drainQueued, 0);
        bool any = false;
        long now = Stopwatch.GetTimestamp();
        while (_done.TryDequeue(out var d))
        {
            _inflight.TryRemove((long)d.Index * 4 + d.Tier, out _);
            if (d.Bitmap is null) continue;
            var ne = new Entry { Index = d.Index, Tier = d.Tier, Bitmap = d.Bitmap, PixelWidth = d.Bitmap.PixelWidth, Bytes = d.Bytes, FadeStart = now };
            if (_cache.TryGetValue(d.Index, out var old))
            {
                var o = old.Value;
                bool sameLook = d.Tier <= o.Tier && Math.Abs(ne.PixelWidth - o.PixelWidth) <= o.PixelWidth * 0.08;
                if (sameLook) continue;                                   // nothing gained
                if (d.Tier > o.Tier && o.Drawn && FadeEnabled) { ne.Prev = o.Bitmap; ne.FadeSecs = 0.15; }  // sharper version: fade over the old one
                ne.Drawn = o.Drawn;
                _lru.Remove(old); _cache.Remove(d.Index); _bytes -= o.Bytes;
            }
            _cache[d.Index] = _lru.AddFirst(ne);
            _bytes += d.Bytes; any = true;
        }
        while (_bytes > _budget && _lru.Last is { } last)
        {
            _lru.RemoveLast(); _cache.Remove(last.Value.Index); _bytes -= last.Value.Bytes;
        }
        if (any) _onReady();
    }

    public void Dispose() { _cts.Cancel(); }
}
