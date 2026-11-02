using System.Collections.Concurrent;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Core;
using Ginkgo.Data;
using SkiaSharp;

namespace Ginkgo.App;

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
    /// <summary>A tile's pixels (premultiplied BGRA), already the size of the tile on screen. SrcW is the width of the thumbnail it was made from.</summary>
    public sealed class Pix { public byte[] Data = Array.Empty<byte>(); public int W, H, SrcW; }

    /// <summary>
    /// Re-uses the memory of tile pictures. Making a fresh array for every decoded thumbnail (hundreds a second while scrolling)
    /// kept the garbage collector busy, and its pauses were the frame-rate dips. Arrays are handed back when a picture leaves the cache.
    /// </summary>
    public static class PixPool
    {
        private static readonly object L = new();
        private static readonly Dictionary<int, Stack<byte[]>> Buckets = new();
        private static long _held;
        private const long Cap = 64L << 20;
        /// <summary>Rounds up to 1, 1.25, 1.5 or 1.75 times a power of two (at most a quarter wasted), so similar sizes share arrays.</summary>
        private static int Bucket(int n)
        {
            if (n <= 4096) return 4096;
            int p = 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)n));
            if (p == n) return n;
            int q = p >> 2;
            return p + ((n - p + q - 1) / q) * q;
        }
        public static byte[] Rent(int n)
        {
            int b = Bucket(n);
            lock (L) { if (Buckets.TryGetValue(b, out var st) && st.Count > 0) { _held -= b; return st.Pop(); } }
            return GC.AllocateUninitializedArray<byte>(b);
        }
        public static void Return(Pix? p)
        {
            var a = p?.Data;
            if (a is null || a.Length < 4096) return;
            p!.Data = Array.Empty<byte>();
            lock (L)
            {
                if (_held + a.Length > Cap) return;
                if (!Buckets.TryGetValue(a.Length, out var st)) Buckets[a.Length] = st = new Stack<byte[]>();
                st.Push(a); _held += a.Length;
            }
        }
    }

    public sealed record TierData(ThumbStore Store, int[] Pack, long[] Off, int[] Len);   // 0 micro, 1 small, 2 full, 3 large

    public sealed class Entry
    {
        public int Index, Tier, PixelWidth;
        public Pix Pix = null!;
        public Pix? Prev;               // what was shown before, while fading to this one
        public long Bytes, FadeStart;
        public double FadeSecs;         // 0 = no fade
        public bool Drawn;
        public long Touched;            // when the screen last asked for it (ms)
    }

    /// <summary>
    /// How a picture fills a tile of a different shape (folder covers): Zoom 1 = fill the tile (cropping the overflow), smaller shows
    /// more of the picture (with dark bars), larger zooms in. Cx, Cy = the point of the picture at the middle of the tile (0..1).
    /// Factor = how much wider than the tile the picture needs to be for the crop to stay sharp.
    /// </summary>
    public readonly record struct Crop(float Zoom, float Cx, float Cy, float Factor, float Radius = 0);

    /// <summary>Set for views with covers: the crop for a tile, or null for a plain picture.</summary>
    public Func<int, Crop?>? CropFor;
    private int Eff(int index, int widthPx) => CropFor?.Invoke(index) is { } c ? (int)Math.Ceiling(widthPx * Math.Max(0.2f, c.Factor)) : widthPx;

    public readonly record struct PlanItem(int Index, int WidthPx, int HeightPx, int Tier, bool EncodedOnly);

    private sealed class Plan { public PlanItem[] Items = Array.Empty<PlanItem>(); public int Next; }
    private readonly record struct Req(int Index, int WidthPx, int HeightPx, int Tier, bool Urgent, bool EncodedOnly);
    private readonly record struct Done(int Index, int Tier, Pix? Pixels, long Bytes, bool Urgent);

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
    private readonly SweepQueue _urgent;                  // quick work for tiles on screen (reading a thumbnail): taken in the order the data lies on the drive
    private readonly ConcurrentStack<Req> _urgentLarge = new();   // slow work for tiles on screen (reading an original to make a sharp version)
    private int _quickActive;                                     // quick jobs being worked on right now
    private long _lastQuick;                                      // when quick work was last asked for or finished
    // ---- look-ahead: its own two quiet threads, separate from the warm-up plan ----
    private int[] _la = Array.Empty<int>();                       // sharp versions worth preparing, nearest first (set by the view)
    private readonly ConcurrentDictionary<int, byte> _laBusy = new();
    private readonly ConcurrentDictionary<int, byte> _laMade = new();
    private long _lastScroll;
    private int _laActive;
    public long LookAheadMade, LookAheadUsed;
    public string LookAheadWhy = "off";
    public string LookAheadSummary => $"look-ahead: {Volatile.Read(ref _la).Length} wanted | made {Volatile.Read(ref LookAheadMade):N0} | already ready when scrolled to {Volatile.Read(ref LookAheadUsed):N0} | running {Volatile.Read(ref _laActive)} | {LookAheadWhy}";
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
        _urgent = new SweepQueue(i => i >= _scrLo && i <= _scrHi);
        _tiers = tiers; _paths = paths; _origW = originalWidths; _large = largeStore; _largeCap = largeCapBytes; _onLargeMade = onLargeMade;
        _ui = ui; _onReady = onReady;
        _budget = decodedBudgetBytes; _encBudget = encodedBudgetBytes;
        for (int i = 0; i < 2; i++) new Thread(LookAheadLoop) { IsBackground = true, Name = "look-ahead", Priority = ThreadPriority.Lowest }.Start();
        for (int i = 0; i < threads; i++) new Thread(Work) { IsBackground = true, Name = "thumb-decode", Priority = ThreadPriority.Normal }.Start();
    }

    public void SetWanted(int lo, int hi) { _lo = lo; _hi = hi; }

    private int _scrLo, _scrHi = int.MaxValue;
    /// <summary>The tiles actually on screen (the rest of the wanted range is just beyond the edges): these are read first.</summary>
    public void SetScreen(int lo, int hi) { _scrLo = lo; _scrHi = hi; }

    /// <summary>Called whenever the view moves.</summary>
    public void NoteScroll() => Volatile.Write(ref _lastScroll, Stopwatch.GetTimestamp());

    /// <summary>UI thread. Does this tile still need its sharp version made?</summary>
    public bool NeedsSharp(int index, int widthPx) => Resolve(index, Eff(index, widthPx), out _, out bool large) && large;

    public void SetLookAhead(int[] indices) => Volatile.Write(ref _la, indices);

    private void LookAheadLoop()
    {
        long ms = Stopwatch.Frequency / 1000;
        while (!_cts.IsCancellationRequested)
        {
            Thread.Sleep(120);
            var list = Volatile.Read(ref _la);
            if (list.Length == 0) { LookAheadWhy = "nothing to prepare"; continue; }
            long now = Stopwatch.GetTimestamp();
            if (now - Volatile.Read(ref _lastScroll) < 400 * ms) { LookAheadWhy = "waiting: scrolling"; continue; }
            if (Volatile.Read(ref _quickActive) > 0 || !_urgent.IsEmpty || !_urgentLarge.IsEmpty || Volatile.Read(ref _largeActive) > 0
                || now - Volatile.Read(ref _lastQuick) < 400 * ms) { LookAheadWhy = "waiting: busy loading"; continue; }
            if (_large.Size > _largeCap) { LookAheadWhy = "stopped: size limit"; continue; }
            int pick = -1;
            foreach (int i in list)
            {
                if (Volatile.Read(ref _tiers[3].Len[i]) > 0 || _largeFailed.ContainsKey(i)) continue;
                if (_laBusy.TryAdd(i, 0)) { pick = i; break; }
            }
            if (pick < 0) { LookAheadWhy = "all prepared"; continue; }
            LookAheadWhy = "working";
            Interlocked.Increment(ref _laActive);
            try { MakeLargeOnly(pick); }
            finally { Interlocked.Decrement(ref _laActive); _laBusy.TryRemove(pick, out _); }
        }
    }

    private void MakeLargeOnly(int index)
    {
        try
        {
            if (Volatile.Read(ref _tiers[3].Len[index]) > 0) return;
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

    /// <summary>Removes requests for tiles that have scrolled away, so they neither count as waiting nor sit in the way.</summary>
    public void PurgeStale()
    {
        int lo = _lo, hi = _hi;
        _urgent.RemoveWhere(r => r.Index < lo || r.Index > hi, r => _inflight.TryRemove((long)r.Index * 4 + r.Tier, out _));
        foreach (var stack in new[] { _urgentLarge })
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
    public void SetPlan(PlanItem[] items) { Volatile.Write(ref _plan, new Plan { Items = items }); WakeWorkers(4); }

    private void WakeWorkers(int n) { for (int k = 0; k < n && _signal.CurrentCount < 8; k++) _signal.Release(); }

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
    private bool NeedsRefresh(Entry e, int show, int widthPx, int heightPx)
    {
        if (e.Tier < show) return true;
        if (widthPx > 16 && heightPx > 0 && (e.Pix.W != widthPx || e.Pix.H != heightPx)) return true;     // the tile is a different size now: make the picture that size
        int need = Math.Min(Math.Min(Eff(e.Index, widthPx), Nominal[e.Tier]), Math.Max(1, _origW[e.Index]));
        int w = e.PixelWidth;
        return w > need * 1.15 || w < need * 0.9;
    }

    /// <summary>Where a thumbnail lies on the drive (pack number, then offset), so waiting reads can be done in one sweep instead of jumping back and forth.</summary>
    private long PositionOf(Req r)
    {
        var t = _tiers[r.Tier];
        return ((long)t.Pack[r.Index] << 34) + t.Off[r.Index];
    }

    /// <summary>
    /// Waiting reads for what is on screen. A hard drive is slow at jumping about and fast when it moves in one direction, so the
    /// next read taken is the one just past the previous one (per size), wrapping round at the end.
    /// </summary>
    private sealed class SweepQueue
    {
        private readonly object _l = new();
        private readonly List<(long key, Req r)> _items = new();
        private readonly long[] _head = new long[4];
        private readonly Func<int, bool> _onScreen;
        private int _count;
        public SweepQueue(Func<int, bool> onScreen) { _onScreen = onScreen; }
        public bool IsEmpty => Volatile.Read(ref _count) == 0;
        public void Push(long key, Req r) { lock (_l) { _items.Add((key, r)); _count = _items.Count; } }
        public bool TryTake(out Req r)
        {
            lock (_l)
            {
                if (_items.Count == 0) { r = default; return false; }
                int best = 0; long bestScore = long.MaxValue;
                for (int i = 0; i < _items.Count; i++)
                {
                    var (k, q) = _items[i];
                    long score = (_onScreen(q.Index) ? 0 : 1L << 58) + ((long)q.Tier << 52) + (k >= _head[q.Tier] ? k : k + (1L << 50));
                    if (score < bestScore) { bestScore = score; best = i; }
                }
                var (bk, br) = _items[best];
                _head[br.Tier] = bk;
                _items[best] = _items[^1]; _items.RemoveAt(_items.Count - 1); _count = _items.Count;
                r = br; return true;
            }
        }
        public void RemoveWhere(Func<Req, bool> drop, Action<Req> onDropped)
        {
            lock (_l)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                    if (drop(_items[i].r)) { onDropped(_items[i].r); _items[i] = _items[^1]; _items.RemoveAt(_items.Count - 1); }
                _count = _items.Count;
            }
        }
    }

    private bool TryQueue(int index, int w, int h, int tier)
    {
        long key = (long)index * 4 + tier;
        if (!_inflight.TryAdd(key, 0)) return false;
        var req = new Req(index, w, h, tier, true, false);
        if (tier == 3 && Volatile.Read(ref _tiers[3].Len[index]) == 0) _urgentLarge.Push(req);
        else { Volatile.Write(ref _lastQuick, Stopwatch.GetTimestamp()); _urgent.Push(PositionOf(req), req); }
        WakeWorkers(1);
        return true;
    }

    /// <summary>UI thread only. The picture for a tile if there is one (asks for it / a better one if needed).</summary>
    public Entry? Get(int index, int widthPx, int heightPx)
    {
        if (!Resolve(index, Eff(index, widthPx), out int show, out bool large)) return null;
        if (show == 3 && !_laMade.IsEmpty && _laMade.TryRemove(index, out _)) Interlocked.Increment(ref LookAheadUsed);
        Entry? e = null;
        if (_cache.TryGetValue(index, out var node)) { _lru.Remove(node); _lru.AddFirst(node); e = node.Value; e.Drawn = true; e.Touched = Environment.TickCount64; }
        if (show >= 0 && (e is null || NeedsRefresh(e, show, widthPx, heightPx))) TryQueue(index, widthPx, heightPx, show);
        if (large && (e is null || e.Tier < 3)) TryQueue(index, widthPx, heightPx, 3);
        return e;
    }

    /// <summary>Any thread. The best stored picture of a tile (the 1280 px one if it exists, else 320 px), for the viewer to show at once while the real file loads.</summary>
    public BitmapSource? QuickPicture(int index, bool small = false)
    {
        try
        {
            for (int step = 0; step < 2; step++)
            {
                int tier = small ? 2 + step : 3 - step;
                if (index < 0 || index >= _paths.Length || Volatile.Read(ref _tiers[tier].Len[index]) <= 0) continue;
                var data = GetEncoded(index, tier);
                if (data is null) continue;
                return DecodeBitmap(data);
            }
        }
        catch { }
        return null;
    }

    /// <summary>UI thread only. Adds what this tile still needs to a warm-up list.</summary>
    public void AppendPlan(int index, int widthPx, int heightPx, List<PlanItem> list, bool decodeToo, bool largeToo)
    {
        if (!Resolve(index, Eff(index, widthPx), out int show, out bool large)) return;
        _cache.TryGetValue(index, out var node);
        var e = node?.Value;
        if (show >= 0 && (e is null || NeedsRefresh(e, show, widthPx, heightPx)) && !_inflight.ContainsKey((long)index * 4 + show))
        {
            list.Add(new PlanItem(index, widthPx, heightPx, show, !decodeToo));
        }
        if (decodeToo && largeToo && large && (e is null || e.Tier < 3) && !_inflight.ContainsKey((long)index * 4 + 3))
            list.Add(new PlanItem(index, widthPx, heightPx, 3, false));
    }

    // ---- workers --------------------------------------------------------------------------------------------

    /// <summary>
    /// Making a sharp version reads an original from the hard drive, which slows everything else down. So it only starts when
    /// no quick work is waiting or running and nothing quick was asked for in the last 100 ms (i.e. scrolling has settled).
    /// </summary>
    private bool SharpMayStart()
    {
        if (Volatile.Read(ref _largeActive) >= 3 || Volatile.Read(ref _quickActive) > 0 || !_urgent.IsEmpty) return false;
        return (Stopwatch.GetTimestamp() - Volatile.Read(ref _lastQuick)) * 1000 / Stopwatch.Frequency > 100;
    }

    private void Work()
    {
        while (!_cts.IsCancellationRequested)
        {
            Req req = default; bool have = false; bool makingLarge = false, quick = false;
            if (_urgent.TryTake(out req)) { have = true; quick = true; Interlocked.Increment(ref _quickActive); }   // thumbnails for what's on screen: always first
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
                    if (slow && (!_urgentLarge.IsEmpty || !SharpMayStart())) continue;   // sharp versions wait until the screen is settled
                    if (!_inflight.TryAdd((long)it.Index * 4 + it.Tier, 0)) continue;
                    req = new Req(it.Index, it.WidthPx, it.HeightPx, it.Tier, false, it.EncodedOnly);
                    have = true; break;
                }
            }
            if (!have) { _signal.Wait(40); continue; }     // nothing to do: sleep (a wake-up arrives when work is queued)
            if (have && req.Tier == 3 && Volatile.Read(ref _tiers[3].Len[req.Index]) == 0) { makingLarge = true; Interlocked.Increment(ref _largeActive); }
            Pix? bmp = null; long bytes = 0;
            bool stale = req.Urgent && (req.Index < _lo || req.Index > _hi);
            if (!stale)
            {
                try
                {
                    if (makingLarge && !req.EncodedOnly)
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
                        (bmp, bytes) = Decode(data, req.WidthPx, req.HeightPx, CropFor?.Invoke(req.Index));
                        Interlocked.Add(ref _decodeTicks, Stopwatch.GetTimestamp() - t0); Interlocked.Increment(ref _decodes);
                    }
                    }
                }
                catch { bmp = null; }
            }
            if (makingLarge) Interlocked.Decrement(ref _largeActive);
            if (quick) { Interlocked.Decrement(ref _quickActive); Volatile.Write(ref _lastQuick, Stopwatch.GetTimestamp()); }
            _done.Enqueue(new Done(req.Index, req.Tier, bmp, bytes, req.Urgent));
            if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
                _ui.BeginInvoke(DispatcherPriority.Input, Drain);
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
        var plan = t.Store.IsInMemory(t.Pack[index], (int)(t.Off[index] / ThumbStore.SegSize)) ? ReadPlanner.Make(t.Pack, t.Off, t.Len, index, 0, 0)      // in memory: no point fetching neighbours
                 : tier == 0 ? ReadPlanner.Make(t.Pack, t.Off, t.Len, index, 64, 256)
                 : tier == 1 ? ReadPlanner.Make(t.Pack, t.Off, t.Len, index, 16, 64)
                 : ReadPlanner.Make(t.Pack, t.Off, t.Len, index);
        long t0 = Stopwatch.GetTimestamp();
        var block = t.Store.Read(plan.Pack, plan.Start, plan.Length);   // short-lived: sliced below
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
    private (Pix?, long) MakeLargeAndPresent(Req req)
    {
        try
        {
            long t0 = Stopwatch.GetTimestamp();
            using var full = ThumbMaker.DecodeLarge(_paths[req.Index]);
            long t1 = Stopwatch.GetTimestamp();
            var result = Present(full, req.WidthPx, req.HeightPx, CropFor?.Invoke(req.Index));
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

    /// <summary>Decodes a thumbnail into pixels of exactly the tile's size on screen.</summary>
    private static (Pix?, long) Decode(byte[] data, int widthPx, int heightPx, Crop? crop = null)
    {
        using var src = SKBitmap.Decode(data);
        return src is null ? (null, 0) : Present(src, widthPx, heightPx, crop);
    }

    /// <summary>
    /// Turns a decoded bitmap into pixels at exactly the tile's size (smaller or larger than the bitmap), so the grid only has to copy them.
    /// SrcW keeps the width the picture really has (never more than the thumbnail's own), which is what the refresh logic compares.
    /// </summary>
    private static readonly SKColor CropBack = new(24, 24, 28), GridBack = new(18, 18, 20);

    private static (Pix?, long) Present(SKBitmap src, int widthPx, int heightPx, Crop? crop = null)
    {
        if (crop is { } c && widthPx > 4 && heightPx > 4) return PresentCropped(src, widthPx, heightPx, c);
        bool sized = widthPx > 16;
        int srcEff = sized && src.Width > widthPx * 1.05 ? widthPx : src.Width;
        int tw = sized ? widthPx : src.Width;
        int th = sized ? (heightPx > 0 ? heightPx : Math.Max(1, (int)Math.Round(src.Height * (widthPx / (double)src.Width)))) : src.Height;
        SKBitmap use = src; SKBitmap? temp = null;
        if (tw != src.Width || th != src.Height)
        {
            temp = src.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
            if (temp is not null) use = temp;
        }
        if (use.ColorType != SKColorType.Bgra8888 || use.AlphaType != SKAlphaType.Premul)
        {
            var conv = use.Copy(SKColorType.Bgra8888);
            temp?.Dispose();
            use = conv; temp = conv;
        }
        int w = use.Width, h = use.Height, rowBytes = use.RowBytes;
        var data = PixPool.Rent(w * h * 4);
        var ptr = use.GetPixels();
        if (rowBytes == w * 4) System.Runtime.InteropServices.Marshal.Copy(ptr, data, 0, w * h * 4);
        else for (int y = 0; y < h; y++) System.Runtime.InteropServices.Marshal.Copy(ptr + y * rowBytes, data, y * w * 4, w * 4);
        temp?.Dispose();
        return (new Pix { Data = data, W = w, H = h, SrcW = srcEff }, (long)w * h * 4);
    }

    /// <summary>A cover: the part of the picture chosen by the crop, scaled to exactly the tile (bars where the picture does not reach).</summary>
    private static (Pix?, long) PresentCropped(SKBitmap src, int tw, int th, Crop c)
    {
        float sw = src.Width, sh = src.Height;
        float s = Math.Clamp(c.Zoom, 0.1f, 10f) * Math.Max(tw / sw, th / sh);
        float vw = tw / s, vh = th / s;                       // how much of the picture the tile shows
        float left = c.Cx * sw - vw / 2, top = c.Cy * sh - vh / 2;
        left = vw <= sw ? Math.Clamp(left, 0, sw - vw) : (sw - vw) / 2;
        top = vh <= sh ? Math.Clamp(top, 0, sh - vh) : (sh - vh) / 2;
        var srcRect = SKRect.Create(left, top, vw, vh);
        var inside = SKRect.Intersect(srcRect, SKRect.Create(0, 0, sw, sh));
        var dstRect = new SKRect((inside.Left - left) * s, (inside.Top - top) * s, (inside.Right - left) * s, (inside.Bottom - top) * s);
        using var dst = new SKBitmap(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(dst))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium, IsAntialias = true })
        {
            if (c.Radius > 0.5f)
            {
                canvas.Clear(GridBack);                                    // the corners show the grid's background
                using var clip = new SKRoundRect(SKRect.Create(0, 0, tw, th), c.Radius, c.Radius);
                canvas.ClipRoundRect(clip, SKClipOperation.Intersect, true);
            }
            canvas.Clear(CropBack);
            if (!inside.IsEmpty) canvas.DrawBitmap(src, inside, dstRect, paint);
        }
        int rowBytes = dst.RowBytes;
        var data = PixPool.Rent(tw * th * 4);
        var ptr = dst.GetPixels();
        if (rowBytes == tw * 4) System.Runtime.InteropServices.Marshal.Copy(ptr, data, 0, tw * th * 4);
        else for (int y = 0; y < th; y++) System.Runtime.InteropServices.Marshal.Copy(ptr + y * rowBytes, data, y * tw * 4, tw * 4);
        int asked = (int)Math.Ceiling(tw * Math.Max(0.2f, c.Factor));        // same as Eff(): what the tile asked for, so it is satisfied
        return (new Pix { Data = data, W = tw, H = th, SrcW = Math.Min(src.Width, asked) }, (long)tw * th * 4);
    }

    /// <summary>For the picture viewer: a stored thumbnail as it is, as a picture WPF can show.</summary>
    private static BitmapSource? DecodeBitmap(byte[] data)
    {
        using var src = SKBitmap.Decode(data);
        if (src is null) return null;
        SKBitmap use = src; SKBitmap? temp = null;
        if (use.ColorType != SKColorType.Bgra8888 || use.AlphaType != SKAlphaType.Premul)
        {
            var conv = use.Copy(SKColorType.Bgra8888);
            use = conv; temp = conv;
        }
        var bmp = BitmapSource.Create(use.Width, use.Height, 96, 96, PixelFormats.Pbgra32, null, use.GetPixels(), use.ByteCount, use.RowBytes);
        bmp.Freeze();
        temp?.Dispose();
        return bmp;
    }

    // ---- UI thread ------------------------------------------------------------------------------------------

    private void Drain()
    {
        Volatile.Write(ref _drainQueued, 0);
        bool any = false;
        long now = Stopwatch.GetTimestamp();
        while ((Stopwatch.GetTimestamp() - now) * 1000 < 2 * Stopwatch.Frequency && _done.TryDequeue(out var d))      // at most ~2 ms at a time, so the screen and the mouse are never kept waiting
        {
            _inflight.TryRemove((long)d.Index * 4 + d.Tier, out _);
            if (d.Pixels is null) continue;
            var ne = new Entry { Index = d.Index, Tier = d.Tier, Pix = d.Pixels, PixelWidth = d.Pixels.SrcW, Bytes = d.Bytes, FadeStart = now };
            if (_cache.TryGetValue(d.Index, out var old))
            {
                var o = old.Value;
                bool sameLook = d.Tier <= o.Tier && Math.Abs(ne.PixelWidth - o.PixelWidth) <= o.PixelWidth * 0.08 && d.Pixels.W == o.Pix.W && d.Pixels.H == o.Pix.H;
                if (sameLook) { PixPool.Return(d.Pixels); continue; }     // nothing gained
                if (d.Tier > o.Tier && o.Drawn && FadeEnabled) { ne.Prev = o.Pix; ne.FadeSecs = 0.15; PixPool.Return(o.Prev); }  // sharper version: fade over the old one
                else { PixPool.Return(o.Pix); PixPool.Return(o.Prev); }
                ne.Drawn = o.Drawn; ne.Touched = o.Touched;
                _lru.Remove(old); _cache.Remove(d.Index); _bytes -= o.Bytes;
            }
            _cache[d.Index] = _lru.AddFirst(ne);
            _bytes += d.Bytes; any = true;
        }
        while (_bytes > _budget && _lru.Last is { } last && Environment.TickCount64 - last.Value.Touched > 400)   // never throw out what is on screen (it would only be loaded again)
        {
            _lru.RemoveLast(); _cache.Remove(last.Value.Index); _bytes -= last.Value.Bytes;
            PixPool.Return(last.Value.Pix); PixPool.Return(last.Value.Prev);
        }
        if (any) _onReady();
        long spent = Stopwatch.GetTimestamp() - now; Perf.Add(spent); Perf.DrainMax = Math.Max(Perf.DrainMax, Perf.Ms(spent));
        if (!_done.IsEmpty && Interlocked.Exchange(ref _drainQueued, 1) == 0) _ui.BeginInvoke(DispatcherPriority.Input, Drain);
    }

    // ---- the GPU grid keeps the decoded pictures on the card: memory holds only what is waiting to be uploaded ----

    /// <summary>Pictures whose pixels have been handed back because they're on the graphics card (the entry stays, so nothing is decoded again).</summary>
    public int CardOnlyCount { get; private set; }

    /// <summary>
    /// UI thread. Hands back the memory of every picture the card already has (not ones in the middle of a fade). The entries stay
    /// in the cache, so the grid keeps drawing them from the card. Returns the bytes freed.
    /// </summary>
    public long ReleaseToCard(Func<Pix, bool> onCard)
    {
        long freed = 0; int cardOnly = 0;
        foreach (var e in _lru)
        {
            if (e.Pix.Data.Length == 0) { cardOnly++; continue; }
            if (e.FadeSecs > 0 || e.Prev is not null || !onCard(e.Pix)) continue;
            freed += e.Bytes; _bytes -= e.Bytes; e.Bytes = 0;
            PixPool.Return(e.Pix);
            cardOnly++;
        }
        CardOnlyCount = cardOnly;
        // entries cost almost nothing now, but don't let their number run away
        while (_cache.Count > 40000 && _lru.Last is { } last && last.Value.Pix.Data.Length == 0 && Environment.TickCount64 - last.Value.Touched > 400)
        { _lru.RemoveLast(); _cache.Remove(last.Value.Index); }
        return freed;
    }

    /// <summary>UI thread. The card let go of these pictures: entries that only had them there are dropped, so they're loaded again.</summary>
    public void Forget(HashSet<Pix> gone)
    {
        if (gone.Count == 0) return;
        var drop = new List<LinkedListNode<Entry>>();
        for (var n = _lru.First; n is not null; n = n.Next)
            if (n.Value.Pix.Data.Length == 0 && gone.Contains(n.Value.Pix)) drop.Add(n);
        foreach (var n in drop) { _lru.Remove(n); _cache.Remove(n.Value.Index); }
    }

    /// <summary>UI thread. The GPU grid stopped: every picture that was only on the card is loaded again.</summary>
    public void ForgetCardOnly()
    {
        var drop = new List<LinkedListNode<Entry>>();
        for (var n = _lru.First; n is not null; n = n.Next) if (n.Value.Pix.Data.Length == 0) drop.Add(n);
        foreach (var n in drop) { _lru.Remove(n); _cache.Remove(n.Value.Index); }
        CardOnlyCount = 0;
    }

    /// <summary>UI thread. A fade has finished: the picture faded from is no longer needed.</summary>
    public void EndFade(Entry e) { e.FadeSecs = 0; var p = e.Prev; e.Prev = null; PixPool.Return(p); }

    public void Dispose() { _cts.Cancel(); }
}
