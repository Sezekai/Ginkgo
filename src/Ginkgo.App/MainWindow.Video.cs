using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// Video playback. A click plays a video in its tile (several at once if Quick toggles allows), resting the mouse on a video
/// plays a preview straight away (even while others play), and the viewer plays with full controls. Each has its own player, so
/// one never waits for another. Where each video was left, which were watched, and the volume are remembered between sessions.
/// </summary>
public partial class MainWindow
{
    private readonly VideoPositions _vpos = new();
    private sealed class GridVid { public int Index; public long Id; public VideoEngine E = null!; public long Order; }
    private readonly List<GridVid> _gridVids = new();
    private readonly Stack<VideoEngine> _spare = new();
    private readonly List<VideoEngine> _allEngines = new();
    private long _vidOrder;
    private VideoEngine? _preview;
    private int _previewIdx = -1, _previewWanted = -1;
    private DispatcherTimer? _previewTimer;
    private readonly HashSet<long> _cantPlay = new();
    private long _clickPausedAt; private int _clickPausedIdx = -1;     // the first click of a double-click pauses: the viewer treats it as still playing
    private long _videoTick;

    // the viewer
    private VideoEngine? _vEngine;
    private bool _viewerVideo;               // the viewer is showing a video (playing or paused)
    private WriteableBitmap? _vBmp;
    private byte[]? _vFrame;
    private int _vSeq = -1;
    private bool _viewerMuted;               // for this session
    private float _viewerRate = 1f;
    private static readonly float[] Speeds = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f };
    private int _viewerFirstIndex = -1;      // the video the viewer was opened on: the only one that starts where it was left
    private bool _viewerLeftFirst;
    private double _viewerLastPos;
    private GridVid? _resumeGrid;            // playing in the grid when the viewer opened on it: it carries on there if you come straight back
    // the bar and the info box show while the mouse moves (and briefly when a video comes up)
    private long _ctrlFlashUntil, _infoFlashUntil, _lastMouseMove;
    private Point _lastMousePos = new(double.NaN, double.NaN);
    private bool _infoPinnedVideo;           // H on a video: the info box stays (this session)
    private bool _speedMenuOpen;
    private long _lastRenderTick;
    private bool _vDownValid; private Point _vDownPos;

    private bool IsVideoTile(int i) => _model is not null && i >= _firstFile && i < _order.Length && _order[i] >= 0 && _model.Kind[_order[i]] == (byte)MediaKind.Video;
    private long TileId(int i) => _model is not null && i >= 0 && i < _order.Length && _order[i] >= 0 ? _model.Id[_order[i]] : -1;
    private static long Ms(double s) => (long)(s * Stopwatch.Frequency);

    private void InitVideo()
    {
        ThreadPool.QueueUserWorkItem(_ => VideoEngine.Warm());          // load the player early, so the first video starts sooner
        View.VideoMuteClicked += i => { var g = _gridVids.Find(x => x.Index == i); if (g is not null) { g.E.Muted = !g.E.Muted; if (!g.E.Muted) g.E.Volume = _vpos.Volume; UpdateVideoButtons(); View.Refresh(); } };
        View.VideoSeek += (i, f, final) =>
        {
            var g = _gridVids.Find(x => x.Index == i);
            if (g is not null && g.E.LengthSec > 0) g.E.Seek(f * g.E.LengthSec);
            View.Refresh();
        };
        View.VideoMark = i =>
        {
            long id = TileId(i);
            if (id < 0) return 0;
            return (_settings.WatchedRule == "Started" ? _vpos.Started(id) : _vpos.Watched(id)) ? 1 : 0;
        };
        ApplyVideoLook();
        View.HoverChanged += OnVideoHover;
        View.MouseLeave += (_, _) => OnVideoHover(-1);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); StartPreview(); };
        CompositionTarget.Rendering += OnVideoRendering;
        VVolume.Value = _vpos.Volume; VVolText.Text = _vpos.Volume.ToString();
        VTimeline.Source = () => (_vEngine is { } e && e.LengthSec > 0 ? e.SmoothPositionSec / e.LengthSec : 0, _vEngine?.LengthSec ?? 0);
        VTimeline.SeekTo += (f, final) => { if (_vEngine is { } e && e.LengthSec > 0) e.Seek(f * e.LengthSec); };
        ViewerRoot.PreviewMouseMove += (_, e) =>
        {
            var p = e.GetPosition(ViewerRoot);
            if (double.IsNaN(_lastMousePos.X) || Math.Abs(p.X - _lastMousePos.X) + Math.Abs(p.Y - _lastMousePos.Y) > 1.5) _lastMouseMove = Stopwatch.GetTimestamp();
            _lastMousePos = p;
        };
        VideoBar.SizeChanged += (_, _) => ClipBar();
        Closing += (_, _) =>
        {
            foreach (var g in _gridVids) SaveVideoPosition(g.E);
            if (_viewerVideo && _vEngine is not null) SaveVideoPosition(_vEngine);
            _vpos.Save();
            foreach (var e in _allEngines) { try { e.Dispose(); } catch { } }
        };
    }

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

    private void SetVolume(int v)
    {
        v = Math.Clamp(v, 0, 100);
        _vpos.Volume = v;
        foreach (var e in _allEngines) e.Volume = v;
        if (Math.Abs(VVolume.Value - v) > 0.5) VVolume.Value = v;
        VVolText.Text = v.ToString();
    }

    /// <summary>Remembers where a video is (not for a preview, and not for one that was only passed by).</summary>
    private void SaveVideoPosition(VideoEngine e)
    {
        if (e.FileId < 0 || e.Preview || e.FramesShown < 10) return;
        _vpos.Set(e.FileId, e.SmoothPositionSec, e.LengthSec);
    }

    // ---- the grid ----

    /// <summary>A click on a video tile: plays it there, or pauses / carries on if it's playing there already.</summary>
    private void ClickVideoTile(int i)
    {
        long id = TileId(i);
        if (id < 0) return;
        if (_cantPlay.Contains(id)) { StatusText.Text = "This video can't be played here. Right-click it and choose Show in Explorer to open it in another player."; return; }
        var g = _gridVids.Find(x => x.Index == i);
        if (g is not null)
        {
            if (g.E.Playing) { g.E.SetPaused(true); SaveVideoPosition(g.E); _clickPausedAt = Stopwatch.GetTimestamp(); _clickPausedIdx = i; }
            else { MakeRoom(g); g.Order = ++_vidOrder; g.E.TogglePause(); }
            UpdateVideoButtons(); View.Refresh();
            return;
        }
        double saved = _vpos.Get(id);
        VideoEngine e;
        if (_previewIdx == i && _preview is not null)
        {
            // the preview carries on as the real thing (no restart): from where it was left if it was left somewhere
            e = _preview; _preview = null; _previewIdx = -1;
            e.Preview = false;
            e.Muted = _settings.GridStartMuted;
            if (saved > 1) e.Seek(saved);
        }
        else
        {
            if (_previewIdx >= 0) StopPreview();
            e = NewEngine();
            e.Loop = true;
            if (!e.Play(id, _model!.Path[_order[i]], saved, _settings.GridStartMuted, 1280, 1280, preview: false)) { OnVideoFailed(e, id); return; }
        }
        g = new GridVid { Index = i, Id = id, E = e, Order = ++_vidOrder };
        MakeRoom(g);
        _gridVids.Add(g);
        View.SetVideo(i, e, controls: true);
        UpdateVideoButtons();
    }

    /// <summary>At most the chosen number play at once: the one started longest ago pauses. Paused ones beyond a few let go of their player.</summary>
    private void MakeRoom(GridVid keep)
    {
        int n = Math.Clamp(_settings.VideosAtOnce, 1, 5);
        var playing = _gridVids.Where(x => x != keep && x.E.Playing).OrderBy(x => x.Order).ToList();
        for (int k = 0; k < playing.Count - (n - 1); k++) { playing[k].E.SetPaused(true); SaveVideoPosition(playing[k].E); }
        var paused = _gridVids.Where(x => x != keep && !x.E.Playing).OrderBy(x => x.Order).ToList();
        int over = _gridVids.Count + 1 - (n + 4);
        for (int k = 0; k < over && k < paused.Count; k++) ReleaseGrid(paused[k]);
    }

    private void ReleaseGrid(GridVid g)
    {
        SaveVideoPosition(g.E);
        g.E.Stop();
        _spare.Push(g.E);
        _gridVids.Remove(g);
        View.SetVideo(g.Index, null, false);
    }

    /// <summary>Stops every video in the grid (the tiles go back to their pictures).</summary>
    private void StopGridVideos()
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) StopPreview();
        foreach (var g in _gridVids.ToList()) ReleaseGrid(g);
        _resumeGrid = null;
        View.ClearVideos();
        UpdateVideoButtons();
    }

    /// <summary>The grid's items changed (a refresh, another sort or folder): playing videos follow their tiles; a preview stops.</summary>
    private void VideoItemsChanged()
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) StopPreview();
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
        _previewWanted = -1;
        if (i < 0 || !IsVideoTile(i) || i == _previewIdx || _gridVids.Exists(x => x.Index == i)) return;
        if (_cantPlay.Contains(TileId(i))) return;
        _previewWanted = i;
        _previewTimer?.Start();          // a moment's grace, so sweeping across tiles doesn't open each one
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
        if (!_preview.Play(id, _model!.Path[_order[i]], 0, muted: !_settings.PreviewSound, 960, 960, preview: true)) return;
        _previewIdx = i;
        View.SetVideo(i, _preview, controls: false);
    }

    private void StopPreview()
    {
        _preview?.Stop();
        if (_previewIdx >= 0) View.SetVideo(_previewIdx, null, false);
        _previewIdx = -1;
    }

    private void OnPauseAll(object s, RoutedEventArgs e)
    {
        foreach (var g in _gridVids) if (g.E.Playing) { g.E.SetPaused(true); SaveVideoPosition(g.E); }
        if (_previewIdx >= 0) StopPreview();
        UpdateVideoButtons(); View.Refresh();
    }

    private void OnMuteAll(object s, RoutedEventArgs e)
    {
        bool anyLoud = _gridVids.Exists(g => g.E.Playing && !g.E.Muted);
        foreach (var g in _gridVids) if (g.E.Playing || anyLoud) { g.E.Muted = anyLoud; if (!anyLoud) g.E.Volume = _vpos.Volume; }
        UpdateVideoButtons(); View.Refresh();
    }

    /// <summary>The pause-all and mute-all buttons show only while a video plays in the grid.</summary>
    private void UpdateVideoButtons()
    {
        bool any = _gridVids.Exists(g => g.E.Playing) && !ViewerOpen;
        var vis = any ? Visibility.Visible : Visibility.Collapsed;
        if (PauseAllBtn.Visibility != vis) { PauseAllBtn.Visibility = vis; MuteAllBtn.Visibility = vis; BarRow.InvalidateMeasure(); }
        if (!any) return;
        bool anyLoud = _gridVids.Exists(g => g.E.Playing && !g.E.Muted);
        string glyph = anyLoud ? "" : "";
        if ((string)MuteAllBtn.Content != glyph)
        {
            MuteAllBtn.Content = glyph;
            MuteAllBtn.ToolTip = anyLoud ? "Mute every playing video" : "Sound on for every playing video";
        }
    }

    private void OnVideoFailed(VideoEngine e, long id)
    {
        if (e.FileId != id) return;                  // it has moved on to another file since
        _cantPlay.Add(id);
        int at = -1;
        for (int k = _firstFile; k < _order.Length; k++) if (TileId(k) == id) { at = k; break; }
        if (at >= 0 && View.BadgeText is { } bt && at < bt.Length) bt[at] = "can't play";
        var g = _gridVids.Find(x => x.E == e);
        if (g is not null) ReleaseGrid(g);
        if (e == _preview && _previewIdx >= 0) StopPreview();
        string msg = "This video can't be played here.\nRight-click its tile and choose Show in Explorer to open it in another player.";
        if (e == _vEngine && ViewerOpen && _viewerVideo) { ViewerNote.Text = msg; VideoBar.Visibility = Visibility.Collapsed; }
        else if (at >= 0) StatusText.Text = msg.Replace("\n", " ");
        UpdateVideoButtons();
        View.Refresh();
    }

    // ---- the viewer ----

    /// <summary>The viewer opens: videos in the grid pause (the one it opened on carries on there if you come straight back).</summary>
    private void VideoViewerOpening(int index)
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) StopPreview();
        _resumeGrid = null;
        _viewerFirstIndex = index; _viewerLeftFirst = false;
        bool justPaused = _clickPausedIdx == index && (Stopwatch.GetTimestamp() - _clickPausedAt) * 1000 < 700 * Stopwatch.Frequency;
        foreach (var g in _gridVids)
        {
            if (g.Index == index && (g.E.Playing || justPaused)) _resumeGrid = g;
            if (g.E.Playing) g.E.SetPaused(true);
            SaveVideoPosition(g.E);                 // the viewer starts the opened one from here
        }
        UpdateVideoButtons();
    }

    /// <summary>The viewer shows another picture or closes: the video there stops (its place is kept).</summary>
    private void LeaveViewerVideo()
    {
        if (!_viewerVideo || _vEngine is null) return;
        _viewerLastPos = _vEngine.SmoothPositionSec;
        SaveVideoPosition(_vEngine);
        _vEngine.Stop();
        _viewerVideo = false;
        _vBmp = null; _vSeq = -1;
        VideoBar.Visibility = Visibility.Collapsed;
        InfoBox.Opacity = 1;
    }

    private void VideoViewerClosed()
    {
        LeaveViewerVideo();
        // straight back from the video it opened on, which was playing in the grid: it carries on there, from where the viewer got to
        var g = _resumeGrid; _resumeGrid = null;
        if (g is not null && !_viewerLeftFirst && _gridVids.Contains(g))
        {
            double at = _viewerLastPos;
            if (at > 0.2) g.E.Seek(at);
            g.Order = ++_vidOrder;
            g.E.SetPaused(false);
        }
        UpdateVideoButtons();
        View.Refresh();
    }

    /// <summary>LoadPicture for a video: plays it at the size of the screen. Only the video the viewer opened on starts where it was left.</summary>
    private async Task LoadVideo(int index, int token)
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
        double start = first ? _vpos.Get(id) : 0;
        var (fw, fh) = FitPixels(row);
        _vEngine ??= NewEngine();
        _vEngine.Loop = VLoopBtn.IsChecked == true;
        _vEngine.SetRate(_viewerRate);
        _vEngine.Volume = _vpos.Volume;
        _viewerVideo = true; _vSeq = -1; _vBmp = null;
        if (!_vEngine.Play(id, path, start, _viewerMuted, fw, fh, preview: false)) { OnVideoFailed(_vEngine, id); return; }
        VideoBar.Visibility = Visibility.Visible;
        long now = Stopwatch.GetTimestamp();
        _ctrlFlashUntil = now + Ms(0.3); _infoFlashUntil = now + Ms(0.3);
        VideoBar.Opacity = 1; InfoBox.Opacity = 1;
        SyncVideoBar();
        Dispatcher.BeginInvoke(PlaceInfo, DispatcherPriority.Loaded);
        // the tile's picture until the first frame arrives (the player has started meanwhile)
        var vl = _loader;
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
            // watched: 90% played
            foreach (var g in _gridVids) MarkIfWatched(g.E);
            if (_viewerVideo && _vEngine is not null) MarkIfWatched(_vEngine);
        }
        if (!ViewerOpen) return;
        if (!_viewerVideo || _vEngine is null || _vEngine.FileId < 0)
        {
            if (InfoBox.Opacity != 1) InfoBox.Opacity = 1;
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

        // the bar shows while the mouse moves or is on it (and briefly when a video comes up); the info box only briefly, or when H pins it
        bool show = now < _ctrlFlashUntil || (now - _lastMouseMove) < Ms(0.15) || VideoBar.IsMouseOver || VTimeline.Dragging || _speedMenuOpen;
        Fade(VideoBar, show, dt);
        Fade(InfoBox, _infoPinnedVideo || now < _infoFlashUntil, dt);
    }

    private static void Fade(UIElement el, bool show, double dt)
    {
        double target = show ? 1 : 0;
        double o = el.Opacity;
        if (Math.Abs(o - target) > 0.001) el.Opacity = show ? Math.Min(1, o + dt / 0.08) : Math.Max(0, o - dt / 0.12);
        bool hit = el.Opacity > 0.05;
        if (el is FrameworkElement fe && fe.Name == "VideoBar" && el.IsHitTestVisible != hit) el.IsHitTestVisible = hit;
    }

    private void MarkIfWatched(VideoEngine e)
    {
        if (e.FileId < 0 || e.Preview) return;
        double len = e.LengthSec;
        if (len > 0 && e.SmoothPositionSec >= len * 0.9 && !_vpos.Watched(e.FileId)) { _vpos.MarkWatched(e.FileId); View.Refresh(); }
    }

    private void SyncVideoBar()
    {
        if (!_viewerVideo || _vEngine is null) return;
        double len = _vEngine.LengthSec;
        double pos = VTimeline.Dragging && len > 0 ? _vEngine.SmoothPositionSec : _vEngine.SmoothPositionSec;
        string t = TimelineArt.Clock(pos) + " / " + TimelineArt.Clock(len);
        if (VTime.Text != t) VTime.Text = t;
        string play = _vEngine.Playing ? "" : "";
        if ((string)VPlayBtn.Content != play) VPlayBtn.Content = play;
        string mute = _vEngine.Muted ? "" : "";
        if ((string)VMuteBtn.Content != mute) VMuteBtn.Content = mute;
        VTimeline.InvalidateVisual();
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
    private void VideoToggleMute() { if (_vEngine is null) return; _vEngine.Muted = !_vEngine.Muted; _viewerMuted = _vEngine.Muted; SyncVideoBar(); }
    private void OnVLoop(object s, RoutedEventArgs e) { if (_vEngine is not null) _vEngine.Loop = VLoopBtn.IsChecked == true; ViewerRoot.Focus(); }

    private void OnVSpeedBtn(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = VSpeedBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        foreach (var r in Speeds)
        {
            float rr = r;
            var it = new MenuItem { Header = $"{r:0.##}x", IsCheckable = true, IsChecked = Math.Abs(r - _viewerRate) < 0.001f };
            it.Click += (_, _) => SetSpeed(rr);
            menu.Items.Add(it);
        }
        _speedMenuOpen = true;
        menu.Closed += (_, _) => { _speedMenuOpen = false; ViewerRoot.Focus(); };
        menu.IsOpen = true;
    }

    private void SetSpeed(float r)
    {
        _viewerRate = r;
        VSpeedBtn.Content = $"{r:0.##}x";
        _vEngine?.SetRate(r);
        Flash(0.8);
    }

    private void StepSpeed(int d)
    {
        int k = Array.FindIndex(Speeds, x => Math.Abs(x - _viewerRate) < 0.001f);
        if (k < 0) k = 3;
        SetSpeed(Speeds[Math.Clamp(k + d, 0, Speeds.Length - 1)]);
    }

    /// <summary>Shows the bar for a moment (a key changed something on it).</summary>
    private void Flash(double sec) { long u = Stopwatch.GetTimestamp() + Ms(sec); if (u > _ctrlFlashUntil) _ctrlFlashUntil = u; }

    private void OnVVolume(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        int v = (int)Math.Round(e.NewValue);
        if (VVolText is not null) VVolText.Text = v.ToString();
        if (!IsLoaded) return;
        if (v != _vpos.Volume) SetVolume(v);
        if (_viewerVideo && _vEngine is { Muted: true } en && v > 0 && Mouse.LeftButton == MouseButtonState.Pressed) { en.Muted = false; _viewerMuted = false; }
    }

    private void SeekBy(double sec)
    {
        if (_vEngine is null) return;
        _vEngine.Seek(_vEngine.SmoothPositionSec + sec);
        Flash(0.8);
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
            case Key.OemComma: _vEngine.PrevFrame(); Flash(0.8); SyncVideoBar(); return true;
            case Key.OemPeriod: _vEngine.NextFrame(); Flash(0.8); SyncVideoBar(); return true;
            case Key.M: VideoToggleMute(); Flash(0.8); return true;
            case Key.OemOpenBrackets: StepSpeed(-1); return true;
            case Key.OemCloseBrackets: StepSpeed(1); return true;
            case Key.L: VLoopBtn.IsChecked = VLoopBtn.IsChecked != true; _vEngine.Loop = VLoopBtn.IsChecked == true; Flash(0.8); return true;
            case Key.D9: case Key.NumPad9: SeekBy(-3); return true;
            case Key.D0: case Key.NumPad0: SeekBy(3); return true;
            case Key.Left when shift: SeekBy(-5); return true;
            case Key.Right when shift: SeekBy(5); return true;
            case Key.Up:
            case Key.Down:
                {
                    // 3% a press (holding the key repeats)
                    int v = _vpos.Volume + (e.Key == Key.Up ? 3 : -3);
                    SetVolume(v);
                    if (e.Key == Key.Up && _vEngine.Muted) { _vEngine.Muted = false; _viewerMuted = false; }
                    Flash(0.8);
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
