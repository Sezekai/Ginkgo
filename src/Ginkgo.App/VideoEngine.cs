using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace Ginkgo.App;

/// <summary>
/// The one video player in the app (grid, viewer and slideshow share it, so starting a video stops any other). LibVLC plays every
/// format in the library (MP4, WebM, MKV, FLV, AVI, WMV, HEVC...). Frames are decoded into memory rather than into a window of their
/// own, so the grid and viewer can draw them like any picture, with the bar and controls on top.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    private LibVLC? _vlc;
    private MediaPlayer? _mp;
    private Media? _media;
    private readonly object _lock = new();      // the frame buffers (also taken by LibVLC's threads)
    private readonly object _ctl = new();       // play / stop (never held while LibVLC needs _lock, or Stop would wait forever)

    // two frame buffers: the player writes one while the screen reads the other
    private IntPtr[] _bufs = { IntPtr.Zero, IntPtr.Zero };
    private int _bufBytes, _write, _ready = -1;
    private int _w, _h;
    private int _maxW = 1920, _maxH = 1080;
    private volatile int _frameSeq;
    public int FrameSeq => _frameSeq;
    public int FrameWidth => _w;
    public int FrameHeight => _h;

    /// <summary>What is playing: the index entry (file) id, -1 when nothing.</summary>
    public long FileId { get; private set; } = -1;
    public string Path { get; private set; } = "";
    /// <summary>A hover preview: silent, from the start, never changes the saved position.</summary>
    public bool Preview { get; private set; }
    public bool Loop { get; set; } = true;
    public volatile bool Failed;
    public string Error = "";
    public event Action<long>? FailedToPlay;
    public event Action? Ended;

    // keep the delegates alive while LibVLC holds them
    private readonly MediaPlayer.LibVLCVideoFormatCb _format;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanup;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _display;

    public VideoEngine()
    {
        _format = OnFormat; _cleanup = OnCleanup; _lockCb = OnLock; _display = OnDisplay;
    }

    private bool Ensure()
    {
        if (_mp is not null) return true;
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
            _vlc = new LibVLC("--quiet", "--no-osd", "--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any");
            _mp = new MediaPlayer(_vlc);
            _mp.SetVideoFormatCallbacks(_format, _cleanup);
            _mp.SetVideoCallbacks(_lockCb, null, _display);
            _mp.EndReached += (_, _) => ThreadPool.QueueUserWorkItem(_ => OnEnded());
            _mp.EncounteredError += (_, _) => { Failed = true; Error = "the player couldn't open it"; var id = FileId; ThreadPool.QueueUserWorkItem(_ => FailedToPlay?.Invoke(id)); };
            return true;
        }
        catch (Exception ex) { Error = "video playback isn't available: " + ex.Message; Failed = true; return false; }
    }

    /// <summary>
    /// Plays a file from <paramref name="startSec"/>, decoding at most <paramref name="maxW"/> x <paramref name="maxH"/> (a tile
    /// doesn't need full HD frames). Stops whatever was playing.
    /// </summary>
    public bool Play(long fileId, string path, double startSec, bool muted, int maxW, int maxH, bool preview)
    {
        if (!Ensure()) return false;
        lock (_ctl)
        {
            Stop();
            _maxW = Math.Max(64, maxW); _maxH = Math.Max(64, maxH);
            FileId = fileId; Path = path; Preview = preview; Failed = false; Error = "";
            lock (_lock) _ready = -1;
            _media = new Media(_vlc!, path, FromType.FromPath);
            if (startSec > 0.5) _media.AddOption($":start-time={startSec.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");
            _mp!.Mute = muted;
            bool ok = _mp.Play(_media);
            _mp.Volume = _volume;
            if (Math.Abs(Rate - 1f) > 0.01f) _mp.SetRate(Rate);
            return ok;
        }
    }

    public void Stop()
    {
        lock (_ctl)
        {
            if (_mp is null) return;
            if (FileId >= 0) { try { _mp.Stop(); } catch { } }
            _media?.Dispose(); _media = null;
            FileId = -1; Path = ""; Preview = false;
            lock (_lock) _ready = -1;
        }
    }

    public bool Playing => _mp is { IsPlaying: true };
    public bool Paused => FileId >= 0 && _mp is not null && !_mp.IsPlaying;
    public void SetPaused(bool pause) { if (_mp is not null && FileId >= 0) _mp.SetPause(pause); }
    public void TogglePause()
    {
        if (AtEnd) { lock (_ctl) { if (_mp is null || _media is null) return; _mp.Stop(); _media.AddOption(":start-time=0"); _mp.Play(_media); } return; }
        SetPaused(Playing);
    }

    public double PositionSec { get => _mp is null || FileId < 0 ? 0 : Math.Max(0, _mp.Time / 1000.0); set { if (_mp is not null && FileId >= 0) _mp.Time = (long)(value * 1000); } }
    public double LengthSec => _mp is null || FileId < 0 ? 0 : Math.Max(0, _mp.Length / 1000.0);
    public bool Muted { get => _mp?.Mute ?? true; set { if (_mp is not null) _mp.Mute = value; } }
    private int _volume = 80;
    public int Volume { get => _volume; set { _volume = Math.Clamp(value, 0, 100); if (_mp is not null) _mp.Volume = _volume; } }
    /// <summary>Played to the end (and not looping): Play starts it again from the beginning.</summary>
    public bool AtEnd => _mp is not null && FileId >= 0 && _mp.State == VLCState.Ended;
    public float Rate { get; private set; } = 1f;
    public void SetRate(float r) { Rate = r; _mp?.SetRate(r); }
    public void NextFrame() { if (_mp is not null && FileId >= 0) _mp.NextFrame(); }

    /// <summary>A frame back (pauses, then steps back by about one frame at 30 fps).</summary>
    public void PrevFrame() { if (_mp is null || FileId < 0) return; _mp.SetPause(true); _mp.Time = Math.Max(0, _mp.Time - 34); }

    private void OnEnded()
    {
        lock (_ctl)
        {
            if (_mp is null || _media is null) return;
            if (Loop || Preview)
            {
                // replaying from the start: the player can't be restarted from inside its own event
                _mp.Stop();
                _media.AddOption(":start-time=0");
                _mp.Play(_media);
                return;
            }
        }
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
            FreeBufs();
            _bufBytes = (int)(w * h * 4);
            _bufs[0] = Marshal.AllocHGlobal(_bufBytes);
            _bufs[1] = Marshal.AllocHGlobal(_bufBytes);
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
        lock (_lock) { _ready = _write; _write ^= 1; }
        _frameSeq++;
    }

    /// <summary>UI thread. Copies the newest frame into <paramref name="dst"/> (made or resized as needed). False when there's none yet.</summary>
    public bool CopyFrame(ref byte[]? dst, out int w, out int h)
    {
        lock (_lock)
        {
            w = _w; h = _h;
            if (_ready < 0 || _bufs[_ready] == IntPtr.Zero || FileId < 0) return false;
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
        try { _mp?.Stop(); } catch { }
        _media?.Dispose(); _mp?.Dispose(); _vlc?.Dispose();
        lock (_lock) FreeBufs();
    }
}

/// <summary>Where each video was left (by its index entry, so it follows the file when it's moved). Saved between sessions.</summary>
public sealed class VideoPositions
{
    private readonly string _path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "video-positions.json");
    private Dictionary<long, double> _pos = new();
    private bool _dirty;
    public int Volume { get; set; } = 80;

    public VideoPositions()
    {
        try
        {
            if (System.IO.File.Exists(_path))
            {
                var d = System.Text.Json.JsonSerializer.Deserialize<Saved>(System.IO.File.ReadAllText(_path));
                if (d is not null) { _pos = d.Positions ?? new(); Volume = d.Volume; }
            }
        }
        catch { }
    }

    private sealed class Saved { public Dictionary<long, double>? Positions { get; set; } public int Volume { get; set; } = 80; }

    public double Get(long id) => _pos.TryGetValue(id, out var s) ? s : 0;

    /// <summary>Remembers where a video is; near the end counts as finished (it starts from the beginning next time).</summary>
    public void Set(long id, double sec, double length)
    {
        if (id < 0) return;
        if (sec < 1 || (length > 0 && sec > length - 2)) _pos.Remove(id); else _pos[id] = sec;
        _dirty = true;
    }

    public void Clear() { _pos.Clear(); _dirty = true; Save(); }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            System.IO.File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(new Saved { Positions = _pos, Volume = Volume }));
            _dirty = false;
        }
        catch { }
    }
}
