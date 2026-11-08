using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// Video playback. A click plays a video in its tile (several at once if Quick toggles allows), resting the mouse on a video
/// plays a preview straight away (even while others play; on a paused one it carries on from where it was), and the viewer plays
/// with full controls. Each has its own player, so one never waits for another.
/// </summary>
public partial class MainWindow
{
    private readonly VideoPositions _vpos = new();
    private sealed class GridVid { public int Index; public long Id; public VideoEngine E = null!; public long Order; public bool HoverPlay; }
    private readonly List<GridVid> _gridVids = new();
    private readonly Stack<VideoEngine> _spare = new();
    private readonly List<VideoEngine> _allEngines = new();
    private long _vidOrder;
    private VideoEngine? _preview;
    private int _previewIdx = -1, _previewWanted = -1;
    private long _previewId = -1;
    private bool _previewSoundTouched;      // you changed the preview's sound yourself (it keeps it if you click to play)
    private GridVid? _hoverPlay;             // a paused grid video playing while the mouse is on it
    private DispatcherTimer? _previewTimer;
    private readonly HashSet<long> _cantPlay = new();
    private long _clickPausedAt; private int _clickPausedIdx = -1;
    private long _videoTick;

    // this session: what was paused (and where), watched, started (for the outlines, the Paused label and the Videos filter)
    private readonly Dictionary<long, double> _sessPaused = new();
    private readonly HashSet<long> _sessWatched = new(), _sessStarted = new();
    // the last frame of paused videos whose player was let go: they keep showing it rather than going back to the thumbnail
    private readonly Dictionary<long, ThumbLoader.Pix> _frozen = new();
    private readonly LinkedList<long> _frozenOrder = new();
    private const int FrozenMax = 40;
    // the Videos filter (hover the Videos switch): which videos show
    private bool _vfWatched = true, _vfPaused = true, _vfUnwatched = true;
    private Popup? _vfPopup;

    // the viewer
    private VideoEngine? _vEngine;
    private bool _viewerVideo;
    private WriteableBitmap? _vBmp;
    private byte[]? _vFrame;
    private int _vSeq = -1;
    private bool _viewerMuted;               // for this session
    private float _viewerRate = 1f;
    private static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
    private int _viewerFirstIndex = -1;      // the video the viewer was opened on: the only one that starts where it was left
    private bool _viewerLeftFirst;
    private long _ctrlFlashUntil, _infoFlashUntil, _lastMouseMove, _osdUntil;
    private bool _osdTime;
    private Point _lastMousePos = new(double.NaN, double.NaN);
    private bool _infoPinnedVideo;           // H on a video: the info box stays (this session)
    private bool _speedMenuOpen;
    private long _lastRenderTick;
    private bool _vDownValid; private Point _vDownPos;
    private bool _vSyncVol;

    private bool IsVideoTile(int i) => _model is not null && i >= _firstFile && i < _order.Length && _order[i] >= 0 && _model.Kind[_order[i]] == (byte)MediaKind.Video;
    private long TileId(int i) => _model is not null && i >= 0 && i < _order.Length && _order[i] >= 0 ? _model.Id[_order[i]] : -1;
    private static long Ms(double s) => (long)(s * Stopwatch.Frequency);
    private bool Always => _settings.OutlineMemory == "Always";

    private bool IsWatched(long id) => _settings.WatchedRule == "Started"
        ? (Always ? _vpos.Started(id) : _sessStarted.Contains(id) || _sessWatched.Contains(id))
        : (Always ? _vpos.Watched(id) : _sessWatched.Contains(id));
    private double PausedAt(long id) => !_settings.ResumeVideos ? -1 : _sessPaused.TryGetValue(id, out var p) ? p : Always && _vpos.Get(id) > 0 ? _vpos.Get(id) : -1;

    private void InitVideo()
    {
        ThreadPool.QueueUserWorkItem(_ => { VideoEngine.Warm(); AudioDevice.Start(); });   // the player and the sound output, ready early
        View.VideoMuteClicked += i => { if (i == _previewIdx) _previewSoundTouched = true; if (EngineAt(i) is { } e) { e.Muted = !e.Muted; if (!e.Muted && e.Volume == 0) e.Volume = _vpos.Volume; View.Refresh(); } };
        View.VideoVolume += (i, v) => { if (i == _previewIdx) _previewSoundTouched = true; if (EngineAt(i) is { } e) { e.Volume = v; if (e.Muted && v > 0) e.Muted = false; View.Refresh(); } };
        View.VideoSeek += (i, f, final) =>
        {
            if (EngineAt(i) is { } e && e.LengthSec > 0) e.Seek(f * e.LengthSec);
            View.Refresh();
        };
        View.VideoInfo = i =>
        {
            long id = TileId(i);
            return id < 0 ? (false, -1) : (IsWatched(id), PausedAt(id));
        };
        View.FrozenFrame = i =>
        {
            long id = TileId(i);
            return id >= 0 && _frozen.TryGetValue(id, out var f) && PausedAt(id) >= 0 ? f : null;
        };
        ApplyVideoLook();
        View.HoverChanged += OnVideoHover;
        View.MouseLeave += (_, _) => OnVideoHover(-1);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); StartPreview(); };
        CompositionTarget.Rendering += OnVideoRendering;
        SyncVolumeSlider();
        VTimeline.Source = () => (_vEngine is { } e && e.LengthSec > 0 ? e.SmoothPositionSec / e.LengthSec : 0, _vEngine?.LengthSec ?? 0);
        VTimeline.SeekTo += (f, final) => { if (_vEngine is { } e && e.LengthSec > 0) e.Seek(f * e.LengthSec); };
        ViewerRoot.PreviewMouseMove += (_, e) =>
        {
            var p = e.GetPosition(ViewerRoot);
            if (double.IsNaN(_lastMousePos.X) || Math.Abs(p.X - _lastMousePos.X) + Math.Abs(p.Y - _lastMousePos.Y) > 1.5) _lastMouseMove = Stopwatch.GetTimestamp();
            _lastMousePos = p;
        };
        VideoBar.SizeChanged += (_, _) => ClipBar();
        InitVideoFilter();
        Closing += (_, _) =>
        {
            foreach (var g in _gridVids) SaveVideoPosition(g.E);
            if (_viewerVideo && _vEngine is not null) SaveVideoPosition(_vEngine);
            _vpos.Save();
            foreach (var e in _allEngines) { try { e.Dispose(); } catch { } }
            AudioDevice.Stop();
        };
    }

    /// <summary>The player showing in tile i (a grid video or the preview).</summary>
    private VideoEngine? EngineAt(int i) => _gridVids.Find(x => x.Index == i)?.E ?? (_previewIdx == i ? _preview : null);

    /// <summary>The bar's buttons fill it edge to edge: clip them to its rounded corners.</summary>
    private void ClipBar()
    {
        double w = VideoBar.ActualWidth - 2, h = VideoBar.ActualHeight - 2;
        if (w > 0 && h > 0) VideoBarGrid.Clip = new RectangleGeometry(new Rect(0, 0, w, h), 9, 9);
    }

    private void ApplyVideoLook()
    {
        View.PausedOutline = _settings.PausedOutline;
        View.WatchedOutline = _settings.WatchedOutline;
        View.Refresh();
    }

    /// <summary>Settings &gt; Video outlines changed something.</summary>
    public void OutlinesChanged() { ApplyVideoLook(); SaveSettings(); }

    private VideoEngine NewEngine()
    {
        var e = _spare.Count > 0 ? _spare.Pop() : null;
        if (e is null)
        {
            e = new VideoEngine();
            var me = e;
            e.FailedToPlay += id => Dispatcher.BeginInvoke(() => OnVideoFailed(me, id));
            _allEngines.Add(e);
        }
        e.Volume = _vpos.Volume;
        e.SetRate(1f);
        return e;
    }

    /// <summary>The volume you set in the viewer: kept between videos and sessions, and what grid videos start at.</summary>
    private void SetVolume(int v)
    {
        v = Math.Clamp(v, 0, 100);
        _vpos.Volume = v;
        if (_vEngine is not null) _vEngine.Volume = v;
        SyncVolumeSlider();
    }

    /// <summary>The viewer's slider shows 0 while muted, and the volume again when sound comes back.</summary>
    private void SyncVolumeSlider()
    {
        int shown = _viewerVideo && _vEngine is { Muted: true } ? 0 : _vpos.Volume;
        _vSyncVol = true;
        if (Math.Abs(VVolume.Value - shown) > 0.5) VVolume.Value = shown;
        _vSyncVol = false;
        VVolText.Text = shown.ToString();
    }

    /// <summary>Remembers where a video is (not for one that was only passed by).</summary>
    private void SaveVideoPosition(VideoEngine e)
    {
        if (e.FileId < 0 || e.FramesShown < 10) return;
        _vpos.Set(e.FileId, e.SmoothPositionSec, e.LengthSec);
    }

    /// <summary>Keeps a paused video's frame for its tile after its player goes (the oldest are dropped past a limit).</summary>
    private void FreezeFrame(long id, ThumbLoader.Pix? f)
    {
        if (id < 0 || f is null) return;
        if (_frozen.ContainsKey(id)) _frozenOrder.Remove(id);
        _frozen[id] = f;
        _frozenOrder.AddLast(id);
        while (_frozenOrder.Count > FrozenMax) { _frozen.Remove(_frozenOrder.First!.Value); _frozenOrder.RemoveFirst(); }
    }

    private void PauseGrid(GridVid g)
    {
        // not carrying on where you left off: pausing stops it, and the tile is a thumbnail like the rest again
        if (!_settings.ResumeVideos) { ReleaseGrid(g); return; }
        if (g.E.Playing) g.E.SetPaused(true);
        g.HoverPlay = false;
        SaveVideoPosition(g.E);
        _sessPaused[g.Id] = g.E.SmoothPositionSec;
    }

    private void PlayGrid(GridVid g)
    {
        MakeRoom(g);
        g.Order = ++_vidOrder;
        g.HoverPlay = false;
        _sessPaused.Remove(g.Id);
        if (g.E.Paused) g.E.TogglePause();
    }

    // ---- the grid ----

    /// <summary>A click on a video tile: plays it there, or pauses / carries on if it's there already.</summary>
    private void ClickVideoTile(int i)
    {
        long id = TileId(i);
        if (id < 0) return;
        if (_cantPlay.Contains(id)) { StatusText.Text = "This video can't be played here. Right-click it and choose Show in Explorer to open it in another player."; return; }
        var g = _gridVids.Find(x => x.Index == i);
        if (g is not null)
        {
            if (g.HoverPlay) { if (_hoverPlay == g) _hoverPlay = null; PlayGrid(g); }          // playing because the mouse is on it: keep it playing
            else if (g.E.Playing) { PauseGrid(g); _clickPausedAt = Stopwatch.GetTimestamp(); _clickPausedIdx = i; }
            else PlayGrid(g);
            UpdateVideoButtons(); View.Refresh();
            return;
        }
        double saved = !_settings.ResumeVideos ? 0 : _sessPaused.TryGetValue(id, out var sp) ? sp : _vpos.Get(id);
        VideoEngine e;
        bool keepSound = false;
        if (_previewIdx == i && _preview is not null)
        {
            // the preview carries on as the real thing (no restart)
            e = _preview; _preview = null; _previewIdx = -1; _previewId = -1;
            e.Preview = false;
            keepSound = _previewSoundTouched;    // sound you set on the preview stays; otherwise the grid's setting
            if (!_sessPaused.ContainsKey(id) && saved > 1 && e.SmoothPositionSec < 1.5) e.Seek(saved);
        }
        else
        {
            if (_previewIdx >= 0) StopPreview();
            e = NewEngine();
            e.Loop = true;
            if (!e.Play(id, _model!.Path[_order[i]], saved, _settings.GridStartMuted, 1280, 1280, preview: false)) { OnVideoFailed(e, id); return; }
        }
        if (!keepSound) e.Muted = _settings.GridStartMuted;
        g = new GridVid { Index = i, Id = id, E = e, Order = ++_vidOrder };
        _sessPaused.Remove(id);
        MakeRoom(g);
        _gridVids.Add(g);
        View.SetVideo(i, e, controls: true);
        UpdateVideoButtons();
    }

    /// <summary>At most the chosen number play at once: the one started longest ago pauses. Paused ones beyond a few let go of their player.</summary>
    private void MakeRoom(GridVid keep)
    {
        int n = Math.Clamp(_settings.VideosAtOnce, 1, 5);
        var playing = _gridVids.Where(x => x != keep && x.E.Playing && !x.HoverPlay).OrderBy(x => x.Order).ToList();
        for (int k = 0; k < playing.Count - (n - 1); k++) PauseGrid(playing[k]);
        var paused = _gridVids.Where(x => x != keep && !x.E.Playing).OrderBy(x => x.Order).ToList();
        int over = _gridVids.Count + 1 - (n + 4);
        for (int k = 0; k < over && k < paused.Count; k++) ReleaseGrid(paused[k]);
    }

    private void ReleaseGrid(GridVid g)
    {
        SaveVideoPosition(g.E);
        if (_settings.ResumeVideos && (g.E.Paused || !g.E.Playing)) { _sessPaused[g.Id] = g.E.SmoothPositionSec; FreezeFrame(g.Id, View.FrameOf(g.Index)); }     // still shows as paused, where it was
        g.E.Stop();
        _spare.Push(g.E);
        _gridVids.Remove(g);
        if (_hoverPlay == g) _hoverPlay = null;
        View.SetVideo(g.Index, null, false);
    }

    /// <summary>The grid's items changed (a refresh, another sort or folder): playing videos follow their tiles; a preview stops.</summary>
    private void VideoItemsChanged()
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) StopPreview();
        EndHoverPlay();
        var where = new Dictionary<long, int>();
        bool marks = _cantPlay.Count > 0 && View.BadgeText is not null;
        if (_gridVids.Count > 0 || marks)
            for (int k = _firstFile; k < _order.Length; k++)
            {
                long t = TileId(k);
                if (_gridVids.Count > 0) where.TryAdd(t, k);
                if (marks && _cantPlay.Contains(t) && View.BadgeText is { } bt && k < bt.Length) bt[k] = "can't play";
            }
        View.ClearVideos();
        foreach (var g in _gridVids.ToList())
        {
            if (where.TryGetValue(g.Id, out int at)) { g.Index = at; View.SetVideo(at, g.E, true); }
            else ReleaseGrid(g);
        }
        UpdateVideoButtons();
    }

    private void OnVideoHover(int i)
    {
        if (ViewerOpen) return;
        _previewTimer?.Stop();
        if (_previewIdx >= 0 && i != _previewIdx) StopPreview();
        if (_hoverPlay is not null && _hoverPlay.Index != i) EndHoverPlay();
        _previewWanted = -1;
        if (i < 0 || !IsVideoTile(i) || i == _previewIdx || !_settings.HoverPreviews) return;
        var g = _gridVids.Find(x => x.Index == i);
        if (g is not null)
        {
            // a paused one carries on while the mouse is on it
            if (!g.E.Playing && !g.E.AtEnd) { g.E.SetPaused(false); g.HoverPlay = true; _hoverPlay = g; _sessPaused.Remove(g.Id); View.Refresh(); }
            return;
        }
        if (_cantPlay.Contains(TileId(i))) return;
        _previewWanted = i;
        _previewTimer?.Start();          // a moment's grace, so sweeping across tiles doesn't open each one
    }

    /// <summary>The mouse left a paused video it had set playing: paused again, here (the new paused time).</summary>
    private void EndHoverPlay()
    {
        var g = _hoverPlay; _hoverPlay = null;
        if (g is null || !g.HoverPlay || !_gridVids.Contains(g)) return;
        PauseGrid(g);
        View.Refresh();
    }

    private void StartPreview()
    {
        int i = _previewWanted;
        _previewWanted = -1;
        if (ViewerOpen || i < 0 || i != View.HoverIndex || !IsVideoTile(i) || _gridVids.Exists(x => x.Index == i)) return;
        long id = TileId(i);
        _preview ??= NewEngine();
        _preview.Loop = true;
        _preview.Volume = _vpos.Volume;
        // a video paused earlier carries on from where it was paused; others from the start
        double from = _settings.ResumeVideos && _sessPaused.TryGetValue(id, out var p) ? p : 0;
        if (!_preview.Play(id, _model!.Path[_order[i]], from, muted: !_settings.PreviewSound, 960, 960, preview: true)) return;
        _previewIdx = i; _previewId = id; _previewSoundTouched = false;
        View.SetVideo(i, _preview, controls: true);
    }

    private void StopPreview()
    {
        if (_preview is not null && _previewId >= 0 && _sessPaused.ContainsKey(_previewId) && _preview.FramesShown >= 5)
        {
            // it was a paused video: where the mouse left it is its new paused time
            _sessPaused[_previewId] = _preview.SmoothPositionSec;
            _vpos.Set(_previewId, _preview.SmoothPositionSec, _preview.LengthSec);
            if (_previewIdx >= 0) FreezeFrame(_previewId, View.FrameOf(_previewIdx));
        }
        _preview?.Stop();
        if (_previewIdx >= 0) View.SetVideo(_previewIdx, null, false);
        _previewIdx = -1; _previewId = -1;
    }

    private void OnPauseAll(object s, RoutedEventArgs e)
    {
        foreach (var g in _gridVids.ToList()) if (g.E.Playing) PauseGrid(g);
        _hoverPlay = null;
        if (_previewIdx >= 0) StopPreview();
        UpdateVideoButtons(); View.Refresh();
    }

    /// <summary>The pause-all button shows only while a video plays in the grid.</summary>
    private void UpdateVideoButtons()
    {
        bool any = _gridVids.Exists(g => g.E.Playing && !g.HoverPlay) && !ViewerOpen;
        var vis = any ? Visibility.Visible : Visibility.Collapsed;
        if (PauseAllBtn.Visibility != vis) { PauseAllBtn.Visibility = vis; BarRow.InvalidateMeasure(); }
    }

    private void OnVideoFailed(VideoEngine e, long id)
    {
        if (e.FileId != id) return;                  // it has moved on to another file since
        _cantPlay.Add(id);
        int at = -1;
        for (int k = _firstFile; k < _order.Length; k++) if (TileId(k) == id) { at = k; break; }
        if (at >= 0 && View.BadgeText is { } bt && at < bt.Length) bt[at] = "can't play";
        var g = _gridVids.Find(x => x.E == e);
        if (g is not null) { _sessPaused.Remove(g.Id); ReleaseGrid(g); _sessPaused.Remove(id); }
        if (e == _preview && _previewIdx >= 0) StopPreview();
        string msg = "This video can't be played here.\nRight-click its tile and choose Show in Explorer to open it in another player.";
        if (e == _vEngine && ViewerOpen && _viewerVideo) { ViewerNote.Text = msg; VideoBar.Visibility = Visibility.Collapsed; }
        else if (at >= 0) StatusText.Text = msg.Replace("\n", " ");
        UpdateVideoButtons();
        View.Refresh();
    }

    // ---- the Videos filter: the arrow beside Videos, for watched / paused / not watched yet ----

    private void InitVideoFilter()
    {
        var panel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        panel.Children.Add(new TextBlock { Text = "Videos to show", Foreground = (Brush)FindResource("Text"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, tip, get, set) in new (string, string, Func<bool>, Action<bool>)[]
        {
            ("Watched", "Played to the end (or started, if Settings > Video outlines says so)", () => _vfWatched, v => _vfWatched = v),
            ("Paused", "Paused part way", () => _vfPaused, v => _vfPaused = v),
            ("Not watched", "Not played yet", () => _vfUnwatched, v => _vfUnwatched = v),
        })
        {
            var b = new ToggleButton { Content = label, IsChecked = get(), ToolTip = tip, Margin = new Thickness(0, 0, 6, 0), Height = 28, MinWidth = 70 };
            b.Click += (_, _) => { set(b.IsChecked == true); BuildView(true); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = (Always ? "" : "This session's. ") + "Resets when Ginkgo closes.", Foreground = (Brush)FindResource("Dim"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0) });
        var card = new Border
        {
            Child = panel, Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(6, 0, 16, 16),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 },
        };
        System.Windows.Documents.TextElement.SetFontFamily(card, new FontFamily("Segoe UI"));
        _vfPopup = new Popup { PlacementTarget = VideosBtn, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = card, VerticalOffset = 2, PopupAnimation = PopupAnimation.Fade };
        _vfPopup.Closed += (_, _) => { _vfClosedAt = Stopwatch.GetTimestamp(); ApplyBar(); };
    }
    private long _vfClosedAt;

    /// <summary>The arrow beside Videos: which videos to show.</summary>
    private void OnVideosMore(object s, RoutedEventArgs e)
    {
        if (_vfPopup is null) return;
        if (_vfPopup.IsOpen) { _vfPopup.IsOpen = false; return; }
        if (Stopwatch.GetTimestamp() - _vfClosedAt < Ms(0.25)) return;     // the click that just closed it
        _vfPopup.IsOpen = true;
        ApplyBar();
    }

    /// <summary>Applied to the files of a new view: the videos the Videos filter leaves out are dropped. Null when it shows them all.</summary>
    private Func<int, bool>? VideoRowFilter()
    {
        if (_vfWatched && _vfPaused && _vfUnwatched || _model is null) return null;
        var m = _model;
        var watched = new HashSet<long>(); var paused = new HashSet<long>();
        for (int r = 0; r < m.Id.Length; r++)
        {
            if (m.Kind[r] != (byte)MediaKind.Video) continue;
            long id = m.Id[r];
            if (IsWatched(id)) watched.Add(id); else if (PausedAt(id) >= 0) paused.Add(id);
        }
        bool w = _vfWatched, p = _vfPaused, u = _vfUnwatched;
        return row =>
        {
            if (m.Kind[row] != (byte)MediaKind.Video) return true;
            long id = m.Id[row];
            return watched.Contains(id) ? w : paused.Contains(id) ? p : u;
        };
    }

    // ---- the viewer ----

    /// <summary>The viewer opens: videos in the grid pause.</summary>
    private void VideoViewerOpening(int index)
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) StopPreview();
        _hoverPlay = null;
        _viewerFirstIndex = index; _viewerLeftFirst = false;
        foreach (var g in _gridVids.ToList()) if (g.E.Playing) PauseGrid(g); else SaveVideoPosition(g.E);
        UpdateVideoButtons();
    }

    /// <summary>The viewer shows another picture or closes: the video there stops (its place is kept).</summary>
    private void LeaveViewerVideo()
    {
        if (!_viewerVideo || _vEngine is null) return;
        SaveVideoPosition(_vEngine);
        if (_vEngine.FramesShown >= 10)
        {
            // watched a bit in the viewer: it shows as paused there in the grid (unless it was watched through)
            long vid = _vEngine.FileId;
            double at = _vEngine.SmoothPositionSec, len = _vEngine.LengthSec;
            _sessStarted.Add(vid);
            if (_settings.ResumeVideos && (len <= 0 || at < len - 2)) _sessPaused[vid] = at; else _sessPaused.Remove(vid);
            _frozen.Remove(vid); _frozenOrder.Remove(vid);            // that frame is from earlier
        }
        _vEngine.Stop();
        _viewerVideo = false;
        _vBmp = null; _vSeq = -1;
        VideoBar.Visibility = Visibility.Collapsed;
        VBubble.Visibility = Visibility.Collapsed;
        ViewerOsd.Opacity = 0;
        InfoBox.Opacity = 1;
    }

    /// <summary>The viewer closes on a video: it waits, paused where you left it, in its tile.</summary>
    private void VideoViewerClosed()
    {
        int idx = _vIndex;
        bool wasVideo = _viewerVideo && _vEngine is not null && _vEngine.FileId >= 0;
        long id = wasVideo ? _vEngine!.FileId : -1;
        double at = wasVideo ? _vEngine!.SmoothPositionSec : 0;
        bool watchedSome = wasVideo && _vEngine!.FramesShown >= 10;
        LeaveViewerVideo();
        if (wasVideo && watchedSome && _settings.ResumeVideos && IsVideoTile(idx) && TileId(idx) == id)
        {
            var g = _gridVids.Find(x => x.Index == idx);
            if (g is not null) { if (g.E.Playing) g.E.SetPaused(true); g.E.Seek(at); }
            else
            {
                var e = NewEngine();
                e.Loop = true;
                if (e.Play(id, _model!.Path[_order[idx]], at, _settings.GridStartMuted, 1280, 1280, preview: false, startPaused: true))
                {
                    g = new GridVid { Index = idx, Id = id, E = e, Order = ++_vidOrder };
                    MakeRoom(g);
                    _gridVids.Add(g);
                    View.SetVideo(idx, e, controls: true);
                }
                else _spare.Push(e);
            }
            _sessPaused[id] = at;
        }
        UpdateVideoButtons();
        View.Refresh();
    }

    /// <summary>LoadPicture for a video: plays it at the size of the screen. Only the video the viewer opened on starts where it was left.</summary>
    private async Task LoadVideo(int index, int token, bool fast)
    {
        int row = _order[index];
        var m = _model!;
        long id = m.Id[row];
        string path = m.Path[row];
        _vFull = true;                                       // zooming never reloads a video
        ViewerNote.Text = "";
        if (_cantPlay.Contains(id))
        {
            var vl0 = _loader;
            Pic.Source = vl0 is null ? null : await Task.Run(() => vl0.QuickPicture(index, false));
            ViewerNote.Text = "This video can't be played here.\nRight-click its tile and choose Show in Explorer to open it in another player.";
            return;
        }
        bool first = index == _viewerFirstIndex && !_viewerLeftFirst;
        double start = first && _settings.ResumeVideos ? (_sessPaused.TryGetValue(id, out var sp) ? sp : _vpos.Get(id)) : 0;
        var (fw, fh) = FitPixels(row);
        _vEngine ??= NewEngine();
        _vEngine.Loop = VLoopBtn.IsChecked == true;
        _vEngine.SetRate(_viewerRate);
        _vEngine.Volume = _vpos.Volume;
        _viewerVideo = true; _vSeq = -1; _vBmp = null;
        if (!_vEngine.Play(id, path, start, _viewerMuted, fw, fh, preview: false)) { OnVideoFailed(_vEngine, id); return; }
        VideoBar.Visibility = Visibility.Visible;
        long now = Stopwatch.GetTimestamp();
        _ctrlFlashUntil = now + Ms(1); _infoFlashUntil = now + Ms(1);
        VideoBar.Opacity = 1; InfoBox.Opacity = 1;
        SyncVolumeSlider();
        SyncVideoBar();
        _ = Dispatcher.BeginInvoke(PlaceInfo, DispatcherPriority.Loaded);
        // the previous picture stays until the first frame (no flash of the small thumbnail); only when keys are held, or if the
        // video is slow to start, the thumbnail shows meanwhile
        var vl = _loader;
        if (!fast) await Task.Delay(700);
        if (token != _vToken || !_viewerVideo || _vBmp is not null) return;
        var poster = vl is null ? null : await Task.Run(() => vl.QuickPicture(index, false));
        if (token != _vToken || !_viewerVideo || _vBmp is not null) return;
        Pic.Source = poster;
    }

    private void OnVideoRendering(object? s, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = _lastRenderTick == 0 ? 0.016 : Math.Min(0.1, (now - _lastRenderTick) / (double)Stopwatch.Frequency);
        _lastRenderTick = now;
        if ((now - _videoTick) * 4 >= Stopwatch.Frequency)
        {
            _videoTick = now;
            UpdateVideoButtons();
            foreach (var g in _gridVids) MarkProgress(g.E);
            if (_preview is not null && _previewIdx >= 0) MarkProgress(_preview);
            if (_viewerVideo && _vEngine is not null) MarkProgress(_vEngine);
        }
        if (!ViewerOpen) return;
        Fade(ViewerOsd, now < _osdUntil, dt);
        if (!_viewerVideo || _vEngine is null || _vEngine.FileId < 0)
        {
            if (InfoBox.Opacity != 1) InfoBox.Opacity = 1;
            if (ViewerRoot.Cursor is not null) ViewerRoot.Cursor = null;
            return;
        }
        if (_vEngine.FrameSeq != _vSeq && _vEngine.CopyFrame(ref _vFrame, out int w, out int h))
        {
            _vSeq = _vEngine.FrameSeq;
            if (_vBmp is null || _vBmp.PixelWidth != w || _vBmp.PixelHeight != h) _vBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
            _vBmp.WritePixels(new Int32Rect(0, 0, w, h), _vFrame!, w * 4, 0);
            if (!ReferenceEquals(Pic.Source, _vBmp)) Pic.Source = _vBmp;
        }
        SyncVideoBar();
        if (_osdTime && now < _osdUntil) ViewerOsdText.Text = TimelineArt.Clock(_vEngine.SmoothPositionSec) + " / " + TimelineArt.Clock(_vEngine.LengthSec);

        // the bar: while the mouse moves (and half a second after), while it's on the bar, while paused, and for a second when a
        // video comes up. The info box: that first second, while paused, or when H pinned it.
        bool paused = _vEngine.Paused;
        bool show = paused || now < _ctrlFlashUntil || (now - _lastMouseMove) < Ms(0.5) || VideoBar.IsMouseOver || VTimeline.Dragging || _speedMenuOpen;
        Fade(VideoBar, show, dt);
        VideoBar.IsHitTestVisible = VideoBar.Opacity > 0.05;
        Fade(InfoBox, _infoPinnedVideo || paused || now < _infoFlashUntil, dt);
        // the pointer hides with the bar while the video plays
        var cur = !show && VideoBar.Opacity < 0.05 ? Cursors.None : null;
        if (!ReferenceEquals(ViewerRoot.Cursor, cur)) ViewerRoot.Cursor = cur;

        // the time under the mouse, just above the bar
        double hf = VTimeline.HoverFrac;
        if (!double.IsNaN(hf) && VideoBar.Opacity > 0.5 && _vEngine.LengthSec > 0)
        {
            VBubbleText.Text = TimelineArt.Clock(hf * _vEngine.LengthSec);
            VBubble.Visibility = Visibility.Visible;
            var at = VTimeline.TranslatePoint(new Point(VTimeline.XOf(hf), 0), ViewerRoot);
            double barTop = VideoBar.TranslatePoint(new Point(0, 0), ViewerRoot).Y;
            double bw = VBubble.ActualWidth > 0 ? VBubble.ActualWidth : 40;
            VBubble.Margin = new Thickness(Math.Max(4, at.X - bw / 2), Math.Max(0, barTop - 26), 0, 0);
        }
        else if (VBubble.Visibility == Visibility.Visible) VBubble.Visibility = Visibility.Collapsed;
    }

    private static void Fade(UIElement el, bool show, double dt)
    {
        double target = show ? 1 : 0;
        double o = el.Opacity;
        if (Math.Abs(o - target) > 0.001) el.Opacity = show ? Math.Min(1, o + dt / 0.08) : Math.Max(0, o - dt / 0.15);
    }

    /// <summary>Started (a little played) and watched (90%), for the outlines and the filter; previews count too.</summary>
    private void MarkProgress(VideoEngine e)
    {
        if (e.FileId < 0) return;
        if (e.FramesShown >= 10) _sessStarted.Add(e.FileId);
        double len = e.LengthSec;
        if (len > 0 && e.SmoothPositionSec >= len * 0.9 && !_sessWatched.Contains(e.FileId))
        {
            _sessWatched.Add(e.FileId);
            _vpos.MarkWatched(e.FileId);
            View.Refresh();
        }
    }

    private void SyncVideoBar()
    {
        if (!_viewerVideo || _vEngine is null) return;
        double len = _vEngine.LengthSec;
        string t = TimelineArt.Clock(_vEngine.SmoothPositionSec) + " / " + TimelineArt.Clock(len);
        if (VTime.Text != t) VTime.Text = t;
        string play = _vEngine.Playing ? "" : "";
        if ((string)VPlayBtn.Content != play) VPlayBtn.Content = play;
        string mute = _vEngine.Muted ? "" : "";
        if ((string)VMuteBtn.Content != mute) VMuteBtn.Content = mute;
        VTimeline.InvalidateVisual();
    }

    /// <summary>A note in the top-right corner for a moment: the volume, or (time = true) where the video is, kept up to date.</summary>
    private void Osd(string text, bool time = false)
    {
        _osdTime = time;
        ViewerOsdText.Text = time && _vEngine is not null ? TimelineArt.Clock(_vEngine.SmoothPositionSec) + " / " + TimelineArt.Clock(_vEngine.LengthSec) : text;
        _osdUntil = Stopwatch.GetTimestamp() + Ms(0.9);
        ViewerOsd.Opacity = 1;
    }

    private void OnVPlay(object s, RoutedEventArgs e) { VideoTogglePause(); ViewerRoot.Focus(); }
    private void VideoTogglePause()
    {
        if (_vEngine is null) return;
        bool pausing = _vEngine.Playing;
        _vEngine.TogglePause();
        if (pausing) SaveVideoPosition(_vEngine);
        SyncVideoBar();
    }
    private void OnVPrevFrame(object s, RoutedEventArgs e) { _vEngine?.PrevFrame(); SyncVideoBar(); ViewerRoot.Focus(); }
    private void OnVNextFrame(object s, RoutedEventArgs e) { _vEngine?.NextFrame(); SyncVideoBar(); ViewerRoot.Focus(); }
    private void OnVMute(object s, RoutedEventArgs e) { VideoToggleMute(); ViewerRoot.Focus(); }
    private void VideoToggleMute()
    {
        if (_vEngine is null) return;
        _vEngine.Muted = !_vEngine.Muted; _viewerMuted = _vEngine.Muted;
        SyncVolumeSlider();
        SyncVideoBar();
    }
    private void OnVLoop(object s, RoutedEventArgs e) { if (_vEngine is not null) _vEngine.Loop = VLoopBtn.IsChecked == true; ViewerRoot.Focus(); }

    private void OnVSpeedBtn(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = VSpeedBtn, Placement = PlacementMode.Top };
        foreach (var r in Speeds)
        {
            float rr = r;
            var it = new MenuItem { Header = $"{r:0.##}x", IsCheckable = true, IsChecked = Math.Abs(r - _viewerRate) < 0.001f };
            it.Click += (_, _) => SetSpeed(rr, false);
            menu.Items.Add(it);
        }
        _speedMenuOpen = true;
        menu.Closed += (_, _) => { _speedMenuOpen = false; ViewerRoot.Focus(); };
        menu.IsOpen = true;
    }

    private void SetSpeed(float r, bool fromKey)
    {
        _viewerRate = r;
        VSpeedBtn.Content = $"{r:0.##}x";
        _vEngine?.SetRate(r);
        if (fromKey) Osd($"Speed {r:0.##}x");
    }

    private void StepSpeed(int d)
    {
        int k = Array.FindIndex(Speeds, x => Math.Abs(x - _viewerRate) < 0.001f);
        if (k < 0) k = 3;
        SetSpeed(Speeds[Math.Clamp(k + d, 0, Speeds.Length - 1)], true);
    }

    private void OnVVolume(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        if (VVolText is not null) VVolText.Text = v.ToString();
        if (_vSyncVol || !IsLoaded) return;
        // dragging or clicking the slider sets the volume, and turns the sound back on if it was off
        if (_vEngine is { Muted: true } en) { en.Muted = false; _viewerMuted = false; }
        SetVolume(v);
        SyncVideoBar();
    }

    private void SeekBy(double sec)
    {
        if (_vEngine is null) return;
        _vEngine.Seek(_vEngine.SmoothPositionSec + sec);
        Osd("", time: true);
        SyncVideoBar();
    }

    /// <summary>Where the picture (or video) is on screen now, zoom and all.</summary>
    private Rect PicRect()
    {
        if (Pic.Source is null || Pic.ActualWidth < 1) return Rect.Empty;
        try { return Pic.TransformToAncestor(PicHost).TransformBounds(new Rect(0, 0, Pic.ActualWidth, Pic.ActualHeight)); }
        catch { return Rect.Empty; }
    }

    /// <summary>Keys for a video in the viewer. True when the key was used.</summary>
    private bool VideoKey(KeyEventArgs e)
    {
        if (!_viewerVideo || _vEngine is null || _vEngine.FileId < 0) return false;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space: VideoTogglePause(); return true;
            case Key.OemComma: SeekBy(-3); return true;
            case Key.OemPeriod: SeekBy(3); return true;
            case Key.D9: case Key.NumPad9: _vEngine.PrevFrame(); Osd("", time: true); SyncVideoBar(); return true;
            case Key.D0: case Key.NumPad0: _vEngine.NextFrame(); Osd("", time: true); SyncVideoBar(); return true;
            case Key.M: VideoToggleMute(); Osd(_vEngine.Muted ? "Muted" : $"Volume {_vpos.Volume}"); return true;
            case Key.OemOpenBrackets: StepSpeed(-1); return true;
            case Key.OemCloseBrackets: StepSpeed(1); return true;
            case Key.L: VLoopBtn.IsChecked = VLoopBtn.IsChecked != true; _vEngine.Loop = VLoopBtn.IsChecked == true; Osd(_vEngine.Loop ? "Loop on" : "Loop off"); return true;
            case Key.Left when shift: SeekBy(-5); return true;
            case Key.Right when shift: SeekBy(5); return true;
            case Key.Up:
            case Key.Down:
                {
                    // 3% a press (holding the key repeats); shown in the corner, not on the bar
                    if (_vEngine.Muted) { _vEngine.Muted = false; _viewerMuted = false; }
                    SetVolume(_vpos.Volume + (e.Key == Key.Up ? 3 : -3));
                    Osd($"Volume {_vpos.Volume}");
                    SyncVideoBar();
                    return true;
                }
        }
        return false;
    }

    // ---- clicks in the viewer: on a video it plays / pauses; anywhere off the picture closes the viewer ----

    private void ViewerClickDown(MouseButtonEventArgs e) { _vDownValid = true; _vDownPos = e.GetPosition(PicHost); }

    private void ViewerClickUp(MouseButtonEventArgs e)
    {
        if (!_vDownValid) return;                     // the press happened elsewhere (the double-click that opened the viewer)
        _vDownValid = false;
        var p = e.GetPosition(PicHost);
        if (Math.Abs(p.X - _vDownPos.X) + Math.Abs(p.Y - _vDownPos.Y) > 5) return;      // a drag
        var r = PicRect();
        if (!r.IsEmpty && r.Contains(p)) { if (_viewerVideo) VideoTogglePause(); }
        else CloseViewer();
    }
}
