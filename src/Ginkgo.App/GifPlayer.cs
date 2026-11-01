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
    private readonly ConcurrentQueue<(long id, int tier)> _load = new();
    private readonly HashSet<(long id, int tier)> _loading = new();
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

    /// <summary>The governor's table: how many GIFs on screen → frame rate and size.</summary>
    private static (int fps, int tier) Table(int row) => row switch { 0 => (15, 1), 1 => (12, -1), 2 => (10, 0), _ => (8, 0) };
    private static int RowFor(int count) => count <= 20 ? 0 : count <= 100 ? 1 : count <= 300 ? 2 : 3;

    /// <summary>Once a second: how many frames came late in the last second (the governor steps down, and back up after 3 calm seconds).</summary>
    public void Tick(int slowFrames)
    {
        long now = NowMs;
        if (slowFrames > 3) { _row = Math.Min(3, _row + 1); _calmSince = now; }
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
    {
        var result = new Dictionary<int, (GifClip, int, int, float)>();
        OnScreen = gifs.Count;
        Paused = !active;
        int row = Math.Max(RowFor(gifs.Count), _row);
        var (fps, tierRule) = Table(row);
        if (FixedFps > 0) fps = FixedFps;
        Fps = fps;
        TierNote = tierRule == 1 ? "large" : tierRule == 0 ? "small" : "by tile size";
        if (!Animate || scrolling || gifs.Count == 0) { Playing = 0; return result; }
        long now = NowMs;
        // while inactive the clock stands still: the GIFs show the frame they were on
        long t = active ? now : _pausedAt;
        if (active) _pausedAt = now;
        gifs.Sort((p, q) => p.dist.CompareTo(q.dist));
        int n = 0;
        foreach (var g in gifs)
        {
            if (Limit > 0 && n >= Limit) break;
            int tier = tierRule >= 0 ? tierRule : Math.Max(g.w, g.h) <= 200 ? 0 : 1;
            var clip = Clip(g.id, tier) ?? Clip(g.id, 1 - tier);         // the other size will do while this one loads
            if (clip is null) continue;
            n++;
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
        return result;
    }
    private long _pausedAt;

    /// <summary>The rendition if it's in memory; otherwise asks for it (from the drive, or to be made) and returns null.</summary>
    private GifClip? Clip(long id, int tier)
    {
        var key = (id, tier);
        lock (_lock)
        {
            if (_clips.TryGetValue(key, out var c))
            {
                _lru.Remove(key); _lru.AddFirst(key);
                return c;
            }
            if (_loading.Contains(key)) return null;
            if (!_have.ContainsKey(key)) { if (tier == 0) _maker.Want(new[] { id }); return null; }
            _loading.Add(key);
        }
        _load.Enqueue(key);
        _signal.Release();
        return null;
    }

    private readonly SemaphoreSlim _signal = new(0);
    /// <summary>Called when a rendition has been read from the drive (redraw).</summary>
    public event Action? Loaded;

    private void LoadLoop()
    {
        while (true)
        {
            _signal.Wait();
            if (!_load.TryDequeue(out var key)) continue;
            GifRendition? r = null;
            try { r = _maker.Load(key.id, key.tier); } catch { }
            lock (_lock)
            {
                _loading.Remove(key);
                if (r is null) continue;
                _clips[key] = new GifClip { Id = key.id, Tier = key.tier, R = r };
                _lru.AddFirst(key);
                _ramBytes += r.Blocks.Length;
                while (_ramBytes > MaxRamBytes && _lru.Count > 1)
                {
                    var old = _lru.Last!.Value; _lru.RemoveLast();
                    if (_clips.Remove(old, out var oc)) _ramBytes -= oc.R.Blocks.Length;
                }
            }
            Loaded?.Invoke();
        }
    }

    public long RamBytes { get { lock (_lock) return _ramBytes; } }
}
