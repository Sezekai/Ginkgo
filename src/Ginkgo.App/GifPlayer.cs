using System.Collections.Concurrent;
using System.Diagnostics;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// Plays GIFs in the GPU grid from their renditions (build brief, "Playback rules"). One shared clock; only GIFs on screen play;
/// covers show during a fast scroll and for 150 ms after it stops; everything pauses while Ginkgo isn't the active window. The
/// governor picks the frame rate and size from how many GIFs are on screen and steps down when frames come late.
/// </summary>
public sealed class GifPlayer
{
    private readonly GifRenditionMaker _maker;
    private readonly Dictionary<(long id, int tier), GifClip> _clips = new();
    private readonly object _lock = new();
    private readonly LinkedList<(long id, int tier)> _lru = new();
    private long _ramBytes;
    /// <summary>Renditions kept in memory (their frames, ready to upload).</summary>
    public long MaxRamBytes = 256L << 20;
    private Dictionary<(long, int), bool> _have = new();       // which renditions exist (refreshed in the background)
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastHaveRefresh = -100_000;

    // settings
    public bool Animate = true;
    public bool CrossFade = true;
    public double CrossFadeSec = 0.3;
    public int FixedFps;         // 0 = the governor decides
    public int Limit = 20;       // GIFs playing at once (0 = all)

    // the governor
    private int _row;            // 0..3: rows of the table, stepped down when frames come late
    private long _calmSince;
    public int OnScreen { get; private set; }
    public int Playing { get; private set; }
    public int Fps { get; private set; } = 15;
    public string TierNote { get; private set; } = "";
    public bool Paused { get; private set; }

    public long NowMs => _clock.ElapsedMilliseconds;

    public GifPlayer(GifRenditionMaker maker)
    {
        _maker = maker;
        var t = new Thread(LoadLoop) { IsBackground = true, Name = "gif-load", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    /// <summary>Which size the GIFs on screen would use at this tile size (for reading ahead).</summary>
    public int TierFor(double tilePx) { var (_, rule) = Table(_lastRow); return TierForTile(rule, (int)tilePx, (int)tilePx, _lastRow); }
    private int _lastRow;

    /// <summary>The governor's table: how many GIFs on screen → frame rate and size.</summary>
    private static (int fps, int tier) Table(int row) => row switch { 0 => (15, 1), 1 => (12, -1), 2 => (10, 0), _ => (8, 0) };
    private static int RowFor(int count) => count <= 20 ? 0 : count <= 100 ? 1 : count <= 300 ? 2 : 3;

    /// <summary>
    /// Once a second: frames that came late because of Ginkgo's own work in the last second, and the card's drawing time. The
    /// governor steps down when either is high, and back up after 3 calm seconds. (Late frames from elsewhere, like Windows copying
    /// each frame to the other graphics chip, happen with or without GIFs, so they don't count.)
    /// </summary>
    public void Tick(int ourSlowFrames, double gpuMs)
    {
        long now = NowMs;
        if (Playing > 0 && (ourSlowFrames > 2 || gpuMs > 8)) { _row = Math.Min(3, _row + 1); _calmSince = now; }
        else if (now - _calmSince > 3000 && _row > 0) { _row--; _calmSince = now; }
        if (now - _lastHaveRefresh > 5000) { _lastHaveRefresh = now; ThreadPool.QueueUserWorkItem(_ => RefreshHave()); }
    }

    private void RefreshHave()
    {
        try
        {
            var have = new Dictionary<(long, int), bool>();
            foreach (var (id, tier) in _maker.Made()) have[(id, tier)] = true;
            _have = have;
        }
        catch { }
    }

    /// <summary>
    /// Before a frame: which GIF tiles play and with which frames. <paramref name="gifs"/> are the GIF tiles on screen (tile index,
    /// file id, tile width, distance from the middle). Returns, per tile index, the clip and frames to draw (missing: show the cover).
    /// </summary>
    public Dictionary<int, (GifClip clip, int a, int b, float w)> Frame(List<(int index, long id, int w, int h, double dist)> gifs, bool scrolling, bool active)
        => Frame(gifs, new List<(long id, int w, int h)>(), scrolling, active);

    /// <summary>GIFs a screen above and below: read, put on the card and started, so they're already playing when they scroll in.</summary>
    public List<GifClip> Ahead { get; } = new();
    private readonly HashSet<GifClip> _live = new(), _liveNext = new();

    /// <summary>The size to play a GIF at in a tile this big: the extra-large version for big tiles (few on screen), else by the governor.</summary>
    private int TierForTile(int tierRule, int w, int h, int row)
    {
        int edge = Math.Max(w, h);
        if (row <= 1 && edge > 360) return 2;                         // big tiles: the 960 px version (made when first needed)
        return tierRule >= 0 ? tierRule : edge <= 200 ? 0 : 1;
    }

    private GifClip? Best(long id, int tier)
    {
        // the size wanted; while it isn't ready, whichever other size is
        if (Clip(id, tier) is { } c) return c;
        for (int t = tier - 1; t >= 0; t--) if (Peek(id, t) is { } d) return d;
        for (int t = tier + 1; t < 3; t++) if (Peek(id, t) is { } e) return e;
        return tier > 0 ? Clip(id, tier - 1) : null;
    }

    /// <summary>The clip if it can be drawn now, without asking for anything.</summary>
    private GifClip? Peek(long id, int tier)
    {
        lock (_lock) return _clips.TryGetValue((id, tier), out var c) && (c.OnCard || c.R.Blocks.Length > 0) ? c : null;
    }

    public Dictionary<int, (GifClip clip, int a, int b, float w)> Frame(List<(int index, long id, int w, int h, double dist)> gifs, List<(long id, int w, int h)> near, bool scrolling, bool active)
    {
        Ahead.Clear();
        var result = new Dictionary<int, (GifClip, int, int, float)>();
        OnScreen = gifs.Count;
        Paused = !active;
        int row = Math.Max(RowFor(gifs.Count), _row);
        _lastRow = row;
        var (fps, tierRule) = Table(row);
        if (FixedFps > 0) fps = FixedFps;
        Fps = fps;
        TierNote = tierRule == 1 ? "large" : tierRule == 0 ? "small" : "by tile size";
        if (!Animate || scrolling || gifs.Count == 0 && near.Count == 0) { Playing = 0; return result; }
        long now = NowMs;
        // while inactive the clock stands still: the GIFs show the frame they were on
        long t = active ? now : _pausedAt;
        if (active) _pausedAt = now;
        gifs.Sort((p, q) => p.dist.CompareTo(q.dist));
        // the GIF under the mouse: its big version is asked for first, even when more GIFs are on screen than play at once
        foreach (var hg in gifs)
            if (hg.id == HoverId && TierForTile(tierRule, hg.w, hg.h, row) == 2) { _wantClass = 0; Clip(hg.id, 2); break; }
        int n = 0;
        foreach (var g in gifs)
        {
            if (Limit > 0 && n >= Limit) break;
            int tier = TierForTile(tierRule, g.w, g.h, row);
            _wantClass = g.id == HoverId ? 0 : 1;
            var clip = Best(g.id, tier);
            if (clip is null) continue;
            n++;
            _liveNext.Add(clip);
            var r = clip.R;
            if (clip.Start == 0) clip.Start = Math.Max(1, t);
            long len = Math.Max(1, r.LengthMs);
            double step = 1000.0 / fps;
            double at = Math.Floor(((t - clip.Start) % len) / step) * step;             // the shared clock, at the grid's frame rate
            int a = Math.Min(r.Frames - 1, (int)(at * r.Fps / 1000));
            int b = a; float w = 0;
            double fade = CrossFadeSec * 1000;
            if (CrossFade && r.Cut && fade > 0 && at > len - fade)
            {
                double into = at - (len - fade);
                w = (float)(into / fade);
                b = Math.Min(r.Frames - 1, (int)(into * r.Fps / 1000));
            }
            result[g.index] = (clip, a, b, w);
        }
        Playing = n;
        // the next screen down and the one above: start them now (their clock runs, and the grid puts them on the card)
        _wantClass = 2;
        foreach (var g in near)
        {
            if (Limit > 0 && n >= Limit * 2) break;
            var clip = Best(g.id, TierForTile(tierRule, g.w, g.h, row));
            if (clip is null) continue;
            if (clip.Start == 0) clip.Start = Math.Max(1, t);
            _liveNext.Add(clip); Ahead.Add(clip); n++;
        }
        _wantClass = 1;
        // GIFs that are neither on screen nor near stop: they start from the beginning when they come back
        foreach (var c in _live) if (!_liveNext.Contains(c)) c.Start = 0;
        _live.Clear(); foreach (var c in _liveNext) _live.Add(c); _liveNext.Clear();
        DropUploaded();
        return result;
    }
    private long _pausedAt;

    /// <summary>
    /// The rendition if it can be drawn now (its frames are on the card, or in memory ready to upload). Otherwise asks for it (read from
    /// the drive, or made first) and returns null.
    /// </summary>
    private GifClip? Clip(long id, int tier)
    {
        var key = (id, tier);
        lock (_lock)
        {
            if (_clips.TryGetValue(key, out var c) && (c.OnCard || c.R.Blocks.Length > 0))
            {
                if (c.R.Blocks.Length > 0) { _lru.Remove(key); _lru.AddFirst(key); }
                return c;
            }
            if (!_have.ContainsKey(key))
            {
                if (tier == 0) _maker.Want(new[] { id });
                else if (tier == 2) _maker.WantBig(id, _wantClass);     // asked again every frame, so the order follows what's on screen
                return null;
            }
            _wantNow[key] = NowMs;          // on screen now: read before anything else
        }
        _wake.Set();
        return null;
    }

    /// <summary>GIFs just off screen (above and below): read ahead into memory so they play as soon as they scroll in.</summary>
    public void Prefetch(List<(long id, int tier)> ahead)
    {
        lock (_lock) { _ahead.Clear(); foreach (var k in ahead) if (_have.ContainsKey(k)) _ahead.Add(k); }
        _wake.Set();
    }

    /// <summary>Once the grid has a GIF's frames on the card, the copy in memory isn't needed (it's read again if the card lets go of it).</summary>
    public void DropUploaded()
    {
        lock (_lock)
        {
            foreach (var key in _lru.ToList())
                if (_clips.TryGetValue(key, out var c) && c.OnCard && c.R.Blocks.Length > 0)
                {
                    _ramBytes -= c.R.Blocks.Length; c.R.Blocks = Array.Empty<byte>(); _lru.Remove(key);
                }
        }
    }

    private readonly Dictionary<(long, int), long> _wantNow = new();
    private readonly HashSet<long> _askedBig = new();
    /// <summary>How urgent a big version asked for right now is: 0 under the mouse, 1 on screen, 2 a screen away.</summary>
    private int _wantClass = 1;
    /// <summary>The GIF under the mouse (its big version is made first).</summary>
    public long HoverId = -1;
    /// <summary>A big version was just made: refresh the list of what exists.</summary>
    /// <summary>Extra-large versions were deleted: forget the ones not on the card, and ask again when needed.</summary>
    public void ForgetBig()
    {
        lock (_lock)
        {
            foreach (var k in _clips.Keys.Where(k => k.Item2 == 2).ToList()) if (!_clips[k].OnCard) { var c = _clips[k]; if (c.R.Blocks.Length > 0) { _ramBytes -= c.R.Blocks.Length; _lru.Remove(k); } _clips.Remove(k); }
            _askedBig.Clear();
            _have = _have.Where(p => p.Key.Item2 != 2).ToDictionary(p => p.Key, p => p.Value);
        }
    }

    public void Made(long id, int tier) { lock (_lock) { var h = new Dictionary<(long, int), bool>(_have) { [(id, tier)] = true }; _have = h; } }
    private readonly List<(long, int)> _ahead = new();
    private readonly AutoResetEvent _wake = new(false);
    /// <summary>Called when a rendition has been read from the drive (redraw).</summary>
    public event Action? Loaded;
    public int Reads, Waiting;

    private (long, int)? Next()
    {
        lock (_lock)
        {
            long now = NowMs;
            // on screen: the most recently wanted first; ones scrolled past a while ago are dropped
            (long, int)? best = null; long bestAt = long.MinValue;
            foreach (var (k, at) in _wantNow.ToList())
            {
                if (now - at > 1500 || (_clips.TryGetValue(k, out var c0) && (c0.OnCard || c0.R.Blocks.Length > 0))) { _wantNow.Remove(k); continue; }
                if (at > bestAt) { best = k; bestAt = at; }
            }
            Waiting = _wantNow.Count;
            if (best is { } b) { _wantNow.Remove(b); return b; }
            // then the ones just off screen, while there's room in memory
            if (_ramBytes < MaxRamBytes * 0.8)
                foreach (var k in _ahead)
                    if (!_clips.TryGetValue(k, out var c1) || (!c1.OnCard && c1.R.Blocks.Length == 0)) { _ahead.Remove(k); return k; }
            return null;
        }
    }

    private void LoadLoop()
    {
        while (true)
        {
            var next = Next();
            if (next is not { } key) { _wake.WaitOne(500); continue; }
            GifRendition? r = null;
            try { r = _maker.Load(key.Item1, key.Item2); Reads++; } catch { }
            if (r is null) continue;
            lock (_lock)
            {
                if (_clips.TryGetValue(key, out var old)) { if (old.R.Blocks.Length > 0) _ramBytes -= old.R.Blocks.Length; old.R = r; }
                else _clips[key] = new GifClip { Id = key.Item1, Tier = key.Item2, R = r };
                _lru.Remove(key); _lru.AddFirst(key);
                _ramBytes += r.Blocks.Length;
                // over the memory limit: let go of the frames read longest ago (only frames still waiting for the card are here)
                while (_ramBytes > MaxRamBytes && _lru.Count > 1)
                {
                    var o = _lru.Last!.Value; _lru.RemoveLast();
                    if (_clips.TryGetValue(o, out var oc) && oc.R.Blocks.Length > 0) { _ramBytes -= oc.R.Blocks.Length; oc.R.Blocks = Array.Empty<byte>(); }
                }
            }
            Loaded?.Invoke();
        }
    }

    public long RamBytes { get { lock (_lock) return _ramBytes; } }
    public int Known { get { lock (_lock) return _clips.Count; } }
}
