using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Ginkgo.App;

/// <summary>
/// One video player. Ginkgo keeps a few (grid tiles, the hover preview, the viewer); they share one LibVLC. LibVLC plays every
/// format in the library (MP4, WebM, MKV, FLV, AVI, WMV, HEVC...). Frames are decoded into memory rather than into a window of
/// their own, so the grid and viewer can draw them like any picture, with the bar and controls on top.
///
/// Starting and stopping a file can take LibVLC a tenth of a second or more, so it happens on the player's own thread: the
/// screen never waits. Only the newest request counts (moving the mouse across ten videos opens one, not ten).
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private static LibVLC? s_vlc;
    private static readonly object s_init = new();
    private static string s_error = "";

    private MediaPlayer? _mp;
    private Media? _media;
    private readonly object _lock = new();      // the frame buffers (also taken by LibVLC's threads)

    // the player's own thread: play / stop / seek requests, newest wins
    private readonly BlockingCollection<Action> _work = new();
    private Thread? _thread;
    private volatile int _gen;                   // bumped by every Play / Stop on the UI thread
    private volatile int _runningGen = -1;       // the request whose file LibVLC is playing now
    private volatile bool _disposed;

    // two frame buffers: the player writes one while the screen reads the other
    private IntPtr[] _bufs = { IntPtr.Zero, IntPtr.Zero };
    private int _bufBytes, _write, _ready = -1, _bufGen = -1;
    private int _w, _h;
    private int _maxW = 1920, _maxH = 1080;
    private volatile int _frameSeq;
    public int FrameSeq => _frameSeq;

    /// <summary>What is playing: the index entry (file) id, -1 when nothing.</summary>
    public long FileId { get; private set; } = -1;
    public string Path { get; private set; } = "";
    /// <summary>A hover preview: from the start, never changes the saved position.</summary>
    public bool Preview { get; set; }
    public bool Loop { get; set; } = true;
    public double StartSec { get; private set; }
    public volatile bool Failed;
    public string Error = "";
    public event Action<long>? FailedToPlay;
    /// <summary>Played to the end without looping.</summary>
    public event Action? Ended;
    /// <summary>Frames shown since the file started (to tell "watched a bit" from "passed by").</summary>
    public int FramesShown => _frameSeq - _seqAtStart;
    private int _seqAtStart;

    // keep the delegates alive while LibVLC holds them
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    public VideoEngine()
    {
        _format = OnFormat; _cleanup = OnCleanup; _lockCb = OnLock; _display = OnDisplay;
    }

    /// <summary>Loads LibVLC (once for the app). Safe to call early, from any thread, so the first video starts sooner.</summary>
    public static bool Warm()
    {
        lock (s_init)
        {
            if (s_vlc is not null) return true;
            if (s_error.Length > 0) return false;
            try
            {
                LibVLCSharp.Shared.Core.Initialize();
                s_vlc = new LibVLC("--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any", "--file-caching=150");
                return true;
            }
            catch (Exception ex) { s_error = "video playback isn't available: " + ex.Message; return false; }
        }
    }

    private bool Ensure()
    {
        if (_mp is not null) return true;
        if (!Warm()) { Error = s_error; Failed = true; return false; }
        _mp = new MediaPlayer(s_vlc!);
        _mp.SetVideoFormatCallbacks(_format, _cleanup);
        _mp.SetVideoCallbacks(_lockCb, null, _display);
        _mp.Volume = _volume;
        _mp.EndReached += (_, _) => { int g = _gen; Post(() => OnEnded(g)); };
        _mp.EncounteredError += (_, _) =>
        {
            int g = _gen;
            if (g != _runningGen) return;
            Failed = true; Error = "the player couldn't open it";
            var id = FileId;
            ThreadPool.QueueUserWorkItem(_ => FailedToPlay?.Invoke(id));
        };
        _thread = new Thread(Run) { IsBackground = true, Name = "Ginkgo video", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    private void Post(Action a) { if (!_disposed) try { _work.Add(a); } catch { } }

    private void Run()
    {
        foreach (var a in _work.GetConsumingEnumerable())
        {
            try { a(); } catch { }
        }
    }

    /// <summary>
    /// Plays a file from <paramref name="startSec"/>, decoding at most <paramref name="maxW"/> x <paramref name="maxH"/> (a tile
    /// doesn't need full HD frames). Replaces whatever this player was playing. Returns at once; the file opens on the player's thread.
    /// </summary>
    public bool Play(long fileId, string path, double startSec, bool muted, int maxW, int maxH, bool preview)
    {
        if (!Ensure()) return false;
        int g = ++_gen;
        _maxW = Math.Max(64, maxW); _maxH = Math.Max(64, maxH);
        FileId = fileId; Path = path; Preview = preview; Failed = false; Error = "";
        StartSec = Math.Max(0, startSec);
        _muted = muted;
        _pausedWanted = false; _ended = false;
        ResetClock(StartSec, false);
        _seqAtStart = _frameSeq;
        Post(() =>
        {
            if (g != _gen) return;                       // something newer was asked for meanwhile
            var mp = _mp!;
            try { mp.Stop(); } catch { }
            _media?.Dispose();
            _media = new Media(s_vlc!, path, FromType.FromPath);
            if (StartSec > 0.5) _media.AddOption($":start-time={StartSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            mp.Mute = _muted;
            mp.Volume = _volume;
            lock (_lock) _ready = -1;
            _runningGen = g;
            _seqAtStart = _frameSeq;
            mp.Play(_media);
            if (Math.Abs(Rate - 1f) > 0.01f) mp.SetRate(Rate);
            if (_pausedWanted && g == _gen) mp.SetPause(true);
        });
        return true;
    }

    /// <summary>Stops (returns at once). The tile or viewer goes back to its still picture.</summary>
    public void Stop()
    {
        if (_mp is null) return;
        int g = ++_gen;
        FileId = -1; Path = ""; Preview = false;
        _pausedWanted = false; _ended = false;
        lock (_lock) _ready = -1;
        Post(() =>
        {
            if (g != _gen) return;
            _runningGen = -1;
            try { _mp!.Stop(); } catch { }
            _media?.Dispose(); _media = null;
        });
    }

    private bool Live => _mp is not null && FileId >= 0 && _runningGen == _gen;

    // ---- pause, position ----

    private volatile bool _pausedWanted;
    public bool Playing => FileId >= 0 && !_pausedWanted && !AtEnd;
    public bool Paused => FileId >= 0 && (_pausedWanted || AtEnd);
    public void SetPaused(bool pause)
    {
        if (_mp is null || FileId < 0) return;
        double now = SmoothPositionSec;
        _pausedWanted = pause;
        ResetClock(now, pause);
        int g = _gen;
        Post(() => { if (g == _gen && _runningGen == g) _mp!.SetPause(pause); });
    }
    public void TogglePause()
    {
        if (AtEnd) { Restart(); return; }
        SetPaused(!_pausedWanted);
    }

    /// <summary>Played to the end (and not looping): Play starts it again from the beginning.</summary>
    public bool AtEnd => Live && _ended;
    private volatile bool _ended;

    private void Restart()
    {
        int g = _gen;
        _ended = false; _pausedWanted = false;
        ResetClock(0, false);
        Post(() =>
        {
            if (g != _gen || _media is null) return;
            _mp!.Stop();
            _media.AddOption(":start-time=0");
            _mp.Play(_media);
        });
    }

    // the position as LibVLC reports it moves in steps (a few times a second): between reports it's worked out from the clock,
    // so bars move smoothly
    private double _clockBase;                 // seconds at _clockTick
    private long _clockTick;
    private bool _clockStill;
    private long _lastVlcMs = long.MinValue;

    private void ResetClock(double sec, bool still) { _clockBase = sec; _clockTick = Stopwatch.GetTimestamp(); _clockStill = still; _lastVlcMs = long.MinValue; }

    /// <summary>The position, smooth from one screen refresh to the next.</summary>
    public double SmoothPositionSec
    {
        get
        {
            if (FileId < 0) return 0;
            long now = Stopwatch.GetTimestamp();
            if (!Live) return _clockBase;
            var st = _mp!.State;
            if (!_ended && st is VLCState.Playing or VLCState.Paused)
            {
                long t = _mp.Time;
                if (t >= 0 && t != _lastVlcMs)
                {
                    // a new report: follow it, unless it's only slightly behind where the clock already is (no jumping back)
                    double est = Estimate(now);
                    double rep = t / 1000.0;
                    if (_lastVlcMs == long.MinValue || Math.Abs(rep - est) > 0.25 || _clockStill || _pausedWanted) { _clockBase = rep; _clockTick = now; }
                    _lastVlcMs = t;
                }
            }
            double len = LengthSec;
            double p = _pausedWanted || _clockStill || _ended ? _clockBase : Estimate(now);
            return len > 0 ? Math.Clamp(p, 0, len) : Math.Max(0, p);
        }
    }
    private double Estimate(long now) => _clockBase + (now - _clockTick) / (double)Stopwatch.Frequency * (_mp?.IsPlaying == true ? Rate : 0);

    public double PositionSec => SmoothPositionSec;

    /// <summary>Jumps to a time. When paused, the new frame is shown too (LibVLC wouldn't, until playing again).</summary>
    public void Seek(double sec)
    {
        if (_mp is null || FileId < 0) return;
        double len = LengthSec;
        sec = len > 0 ? Math.Clamp(sec, 0, Math.Max(0, len - 0.05)) : Math.Max(0, sec);
        bool paused = _pausedWanted;
        _ended = false;
        ResetClock(sec, paused);
        _seekTo = sec;
        if (Interlocked.Exchange(ref _seekQueued, 1) == 1) return;           // one already waiting: it'll use the newest time
        int g = _gen;
        Post(() =>
        {
            Interlocked.Exchange(ref _seekQueued, 0);
            if (g != _gen || _runningGen != g) return;
            var mp = _mp!;
            if (mp.State == VLCState.Ended) { mp.Stop(); _media?.AddOption($":start-time={_seekTo.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}"); mp.Play(_media!); if (_pausedWanted) mp.SetPause(true); return; }
            mp.Time = (long)(_seekTo * 1000);
            if (_pausedWanted) { mp.NextFrame(); }
        });
    }
    private double _seekTo;
    private int _seekQueued;

    public double LengthSec => Live ? Math.Max(0, _mp!.Length / 1000.0) : 0;
    private volatile bool _muted;
    public bool Muted { get => _muted; set { _muted = value; int g = _gen; Post(() => { if (_mp is not null) _mp.Mute = value; }); } }
    private volatile int _volume = 80;
    public int Volume { get => _volume; set { int v = Math.Clamp(value, 0, 100); if (v == _volume) return; _volume = v; Post(() => { if (_mp is not null) _mp.Volume = v; }); } }
    public float Rate { get; private set; } = 1f;
    public void SetRate(float r)
    {
        if (Math.Abs(r - Rate) < 0.001f) return;
        double now = SmoothPositionSec;
        Rate = r; ResetClock(now, _pausedWanted);
        int g = _gen;
        Post(() => { if (g == _gen && _runningGen == g) _mp?.SetRate(r); });
    }

    public void NextFrame()
    {
        if (!Live) return;
        _pausedWanted = true;
        double fps = 30;
        ResetClock(SmoothPositionSec + 1 / fps, true);
        Post(() => _mp?.NextFrame());
    }

    /// <summary>A frame back (pauses, then steps back by about one frame at 30 fps).</summary>
    public void PrevFrame()
    {
        if (!Live) return;
        if (!_pausedWanted) SetPaused(true);
        Seek(Math.Max(0, SmoothPositionSec - 0.034));
    }

    private void OnEnded(int g)
    {
        if (g != _gen || _media is null || _mp is null) return;
        if (Loop || Preview)
        {
            // again from the start: the player can't be restarted from inside its own event, so it's done here, on its thread
            ResetClock(0, false);
            _mp.Stop();
            _media.AddOption(":start-time=0");
            _mp.Play(_media);
            if (Math.Abs(Rate - 1f) > 0.01f) _mp.SetRate(Rate);
            return;
        }
        _ended = true;
        ResetClock(LengthSec, true);
        Ended?.Invoke();
    }

    // ---- frames ----

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // RV32 = BGRA in memory; decode at a size that fits what it's shown in
        var rv32 = new byte[] { (byte)'R', (byte)'V', (byte)'3', (byte)'2' };
        Marshal.Copy(rv32, 0, chroma, 4);
        double s = Math.Min(1.0, Math.Min(_maxW / (double)Math.Max(1, width), _maxH / (double)Math.Max(1, height)));
        uint w = Math.Max(2, (uint)(width * s)) & ~1u, h = Math.Max(2, (uint)(height * s)) & ~1u;
        width = w; height = h;
        pitches = w * 4; lines = h;
        lock (_lock)
        {
            int bytes = (int)(w * h * 4);
            if (bytes != _bufBytes || _bufs[0] == IntPtr.Zero)
            {
                FreeBufs();
                _bufBytes = bytes;
                _bufs[0] = Marshal.AllocHGlobal(_bufBytes);
                _bufs[1] = Marshal.AllocHGlobal(_bufBytes);
            }
            _w = (int)w; _h = (int)h; _write = 0; _ready = -1;
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque) { }

    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _bufs[_write]);
        return IntPtr.Zero;
    }

    private void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_lock) { _ready = _write; _write ^= 1; _bufGen = _runningGen; }
        _frameSeq++;
    }

    /// <summary>UI thread. Copies the newest frame into <paramref name="dst"/> (made or resized as needed). False when there's none yet.</summary>
    public bool CopyFrame(ref byte[]? dst, out int w, out int h)
    {
        lock (_lock)
        {
            w = _w; h = _h;
            // only frames of the file asked for most recently (not the one before, still finishing on the player's thread)
            if (_ready < 0 || _bufs[_ready] == IntPtr.Zero || FileId < 0 || _bufGen != _gen) return false;
            if (dst is null || dst.Length != _bufBytes) dst = new byte[_bufBytes];
            Marshal.Copy(_bufs[_ready], dst, 0, _bufBytes);
            // RV32 leaves the alpha byte undefined: make every pixel opaque
            var span = dst.AsSpan();
            for (int i = 3; i < span.Length; i += 4) span[i] = 255;
            return true;
        }
    }

    private void FreeBufs()
    {
        for (int k = 0; k < 2; k++) if (_bufs[k] != IntPtr.Zero) { Marshal.FreeHGlobal(_bufs[k]); _bufs[k] = IntPtr.Zero; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _gen++;
        Post(() =>
        {
            try { _mp?.Stop(); } catch { }
            _media?.Dispose(); _mp?.Dispose();
            lock (_lock) FreeBufs();
        });
        _disposed = true;
        _work.CompleteAdding();
    }
}

/// <summary>
/// Where each video was left (by its index entry, so it follows the file when it's moved), which ones were watched, and the
/// volume. Saved between sessions.
/// </summary>
public sealed class VideoPositions
{
    private readonly string _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "video-positions.json");
    private Dictionary<long, double> _pos = new();
    private HashSet<long> _watched = new();
    private bool _dirty;
    private int _volume = 80;
    public int Volume { get => _volume; set { if (_volume != value) { _volume = value; _dirty = true; } } }

    public VideoPositions()
    {
        try
        {
            if (System.IO.File.Exists(_path))
            {
                var d = System.Text.Json.JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(_path));
                if (d is not null) { _pos = d.Positions ?? new(); _volume = d.Volume; _watched = d.Watched is null ? new() : new HashSet<long>(d.Watched); }
            }
        }
        catch { }
    }

    private sealed class Saved
    {
        public Dictionary<long, double>? Positions { get; set; }
        public int Volume { get; set; } = 80;
        public List<long>? Watched { get; set; }
    }

    public double Get(long id) => _pos.TryGetValue(id, out var s) ? s : 0;
    public bool Started(long id) => _pos.ContainsKey(id) || _watched.Contains(id);
    public bool Watched(long id) => _watched.Contains(id);
    public void MarkWatched(long id) { if (id >= 0 && _watched.Add(id)) _dirty = true; }

    /// <summary>Remembers where a video is; near the end counts as finished (it starts from the beginning next time).</summary>
    public void Set(long id, double sec, double length)
    {
        if (id < 0) return;
        if (length > 0 && sec >= length * 0.9) MarkWatched(id);
        if (sec < 1 || (length > 0 && sec > length - 2)) _pos.Remove(id); else _pos[id] = sec;
        _dirty = true;
    }

    public void Clear() { _pos.Clear(); _watched.Clear(); _dirty = true; Save(); }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(new Saved { Positions = _pos, Volume = _volume, Watched = _watched.ToList() }));
            _dirty = false;
        }
        catch { }
    }
}
