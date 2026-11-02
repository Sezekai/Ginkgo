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
/// Video playback. One player for the whole app: a click plays a video in its tile (muted, with a speaker to turn sound on and
/// a progress line to jump along), resting the mouse on a video plays a silent preview from the start, and the viewer plays
/// with sound and full controls. Where each video was left is remembered between sessions (previews never change it).
/// </summary>
public partial class MainWindow
{
    private readonly VideoEngine _video = new();
    private readonly VideoPositions _vpos = new();
    private int _gridVideo = -1;            // tile playing in the grid (a click, not a preview)
    private int _previewIdx = -1;           // tile showing a hover preview
    private int _previewWanted = -1;
    private DispatcherTimer? _previewTimer;
    private readonly HashSet<long> _cantPlay = new();
    private long _clickPausedAt;            // the first click of a double-click pauses: the viewer treats it as still playing

    // the viewer
    private bool _viewerVideo;               // the viewer is showing a video (playing or paused)
    private WriteableBitmap? _vBmp;
    private byte[]? _vFrame;
    private int _vSeq = -1;
    private long _vUiTick;
    private bool _vSyncing, _vSeeking;
    private bool _viewerMuted;
    private float _viewerRate = 1f;
    // what played in the grid when the viewer opened (it carries on there when the viewer closes)
    private int _resumeGrid = -1;
    private bool _resumeMuted = true;

    private bool IsVideoTile(int i) => _model is not null && i >= _firstFile && i < _order.Length && _order[i] >= 0 && _model.Kind[_order[i]] == (byte)MediaKind.Video;
    private long TileId(int i) => _model is not null && i >= 0 && i < _order.Length && _order[i] >= 0 ? _model.Id[_order[i]] : -1;

    private void InitVideo()
    {
        _video.Volume = _vpos.Volume;
        _video.FailedToPlay += id => Dispatcher.BeginInvoke(() => OnVideoFailed(id));
        _video.Ended += () => Dispatcher.BeginInvoke(() => { SaveVideoPosition(); SyncVideoBar(true); });
        View.Video = _video;
        View.VideoMuteClicked += () => { _video.Muted = !_video.Muted; View.Refresh(); };
        View.VideoSeekClicked += f => { if (_video.LengthSec > 0) { _video.PositionSec = f * _video.LengthSec; View.Refresh(); } };
        View.HoverChanged += OnVideoHover;
        View.MouseLeave += (_, _) => OnVideoHover(-1);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); StartPreview(); };
        CompositionTarget.Rendering += OnVideoRendering;
        VVolume.Value = _vpos.Volume;
        Closing += (_, _) =>
        {
            SaveVideoPosition();
            _vpos.Volume = _video.Volume;
            _vpos.Save();
            try { _video.Dispose(); } catch { }
        };
    }

    /// <summary>Remembers where the playing video is (not for a preview).</summary>
    private void SaveVideoPosition()
    {
        if (_video.FileId < 0 || _video.Preview) return;
        _vpos.Set(_video.FileId, _video.PositionSec, _video.LengthSec);
    }

    /// <summary>The grid's items are about to change (another folder, sort or filter): tile numbers mean something else now.</summary>
    private void StopGridVideo()
    {
        _previewTimer?.Stop();
        if (ViewerOpen && _viewerVideo) return;
        if (_gridVideo >= 0 || _previewIdx >= 0) { SaveVideoPosition(); _video.Stop(); }
        _gridVideo = _previewIdx = _previewWanted = -1;
        _resumeGrid = -1;
        View.SetVideo(-1, false);
    }

    /// <summary>The grid's items changed (a refresh, another sort or folder): the playing video follows its tile, a preview stops.</summary>
    private void VideoItemsChanged()
    {
        long id = _video.FileId;
        int at = -1;
        bool marks = _cantPlay.Count > 0 && View.BadgeText is not null;
        if ((id >= 0 && _gridVideo >= 0) || marks)
            for (int k = _firstFile; k < _order.Length; k++)
            {
                long t = TileId(k);
                if (t == id && id >= 0) at = k;
                if (marks && _cantPlay.Contains(t) && View.BadgeText is { } bt && k < bt.Length) bt[k] = "can't play";
            }
        _resumeGrid = -1;
        if (ViewerOpen && _viewerVideo) return;
        if (_previewIdx >= 0 || _gridVideo < 0 || at < 0) { StopGridVideo(); return; }
        _gridVideo = at;
        View.SetVideo(at, controls: true);
    }

    // ---- the grid ----

    /// <summary>A click on a video tile: plays it there (muted), or pauses / carries on if it's the one playing.</summary>
    private void ClickVideoTile(int i)
    {
        long id = TileId(i);
        if (_gridVideo == i && _video.FileId == id && !_video.Preview)
        {
            bool pausing = _video.Playing;
            _video.TogglePause();
            if (pausing) { SaveVideoPosition(); _clickPausedAt = Stopwatch.GetTimestamp(); }
            View.Refresh();
            return;
        }
        PlayInGrid(i, _vpos.Get(id), muted: _gridVideo >= 0 ? _video.Muted : true);
    }

    private void PlayInGrid(int i, double start, bool muted)
    {
        long id = TileId(i);
        if (id < 0) return;
        if (_cantPlay.Contains(id)) { StatusText.Text = "This video can't be played here. Right-click it and choose Show in Explorer to open it in another player."; return; }
        _previewTimer?.Stop();
        SaveVideoPosition();
        _video.Loop = true;
        _video.SetRate(1f);
        if (!_video.Play(id, _model!.Path[_order[i]], start, muted, 1280, 1280, preview: false)) { OnVideoFailed(id); return; }
        _gridVideo = i; _previewIdx = -1;
        View.SetVideo(i, controls: true);
    }

    private void OnVideoHover(int i)
    {
        if (ViewerOpen) return;
        _previewTimer?.Stop();
        if (_previewIdx >= 0 && i != _previewIdx)
        {
            // moved off the preview: it stops (and the paused video, if any, keeps its place: it was saved when it paused)
            _video.Stop();
            View.SetVideo(-1, false);
            _previewIdx = -1;
        }
        _previewWanted = -1;
        if (i < 0 || !IsVideoTile(i) || i == _gridVideo || i == _previewIdx) return;
        if (_gridVideo >= 0 && _video.Playing && !_video.Preview) return;     // no previews while a video plays
        if (_cantPlay.Contains(TileId(i))) return;
        _previewWanted = i;
        _previewTimer?.Start();
    }

    private void StartPreview()
    {
        int i = _previewWanted;
        _previewWanted = -1;
        if (ViewerOpen || i < 0 || i != View.HoverIndex || !IsVideoTile(i)) return;
        if (_gridVideo >= 0 && _video.Playing && !_video.Preview) return;
        if (_gridVideo >= 0) { SaveVideoPosition(); _gridVideo = -1; }       // the paused one gives way (its place is kept)
        long id = TileId(i);
        _video.Loop = true;
        _video.SetRate(1f);
        if (!_video.Play(id, _model!.Path[_order[i]], 0, muted: true, 960, 960, preview: true)) return;
        _previewIdx = i;
        View.SetVideo(i, controls: false);
    }

    private void OnVideoFailed(long id)
    {
        _cantPlay.Add(id);
        int at = -1;
        for (int k = _firstFile; k < _order.Length; k++) if (TileId(k) == id) { at = k; break; }
        if (at >= 0 && View.BadgeText is { } bt && at < bt.Length) { bt[at] = "can't play"; }
        if (_gridVideo == at || _previewIdx == at) { _gridVideo = _previewIdx = -1; View.SetVideo(-1, false); }
        if (ViewerOpen && _viewerVideo && TileId(_vIndex) == id)
        {
            ViewerNote.Text = "This video can't be played here.\nRight-click its tile and choose Show in Explorer to open it in another player.";
            VideoBar.Visibility = Visibility.Collapsed;
        }
        else if (at >= 0 && at == View.HoverIndex) StatusText.Text = "This video can't be played here. Right-click it and choose Show in Explorer to open it in another player.";
        View.Refresh();
    }

    // ---- the viewer ----

    /// <summary>The viewer opens: a video playing in the grid stops there (and carries on there when the viewer closes).</summary>
    private void VideoViewerOpening(int index)
    {
        _previewTimer?.Stop();
        if (_previewIdx >= 0) { _video.Stop(); _previewIdx = -1; View.SetVideo(-1, false); }
        _resumeGrid = -1;
        if (_gridVideo >= 0 && _video.FileId >= 0)
        {
            bool playing = _video.Playing || (Stopwatch.GetTimestamp() - _clickPausedAt) * 1000 < 700 * Stopwatch.Frequency;
            _resumeMuted = _video.Muted;
            SaveVideoPosition();
            if (playing) _resumeGrid = _gridVideo;
            // the same video carries on in the viewer from where it is (LoadPicture picks it up from the saved place)
            if (_gridVideo != index) { _video.Stop(); }
            View.SetVideo(-1, false);
            _gridVideo = -1;
        }
    }

    /// <summary>The viewer shows another picture or closes: the video there stops (its place is kept).</summary>
    private void LeaveViewerVideo()
    {
        if (!_viewerVideo) return;
        SaveVideoPosition();
        _video.Stop();
        _viewerVideo = false;
        _vBmp = null; _vSeq = -1;
        VideoBar.Visibility = Visibility.Collapsed;
    }

    private void VideoViewerClosed()
    {
        LeaveViewerVideo();
        int i = _resumeGrid; _resumeGrid = -1;
        if (i >= 0 && IsVideoTile(i)) PlayInGrid(i, _vpos.Get(TileId(i)), _resumeMuted);
    }

    /// <summary>LoadPicture for a video: plays it at the size of the screen, from where it was left.</summary>
    private async Task LoadVideo(int index, int token)
    {
        int row = _order[index];
        var m = _model!;
        long id = m.Id[row];
        string path = m.Path[row];
        _vFull = true;                                       // zooming never reloads a video
        ViewerNote.Text = "";
        // the tile's picture until the first frame arrives
        var vl = _loader;
        var poster = vl is null ? null : await Task.Run(() => vl.QuickPicture(index, false));
        if (token != _vToken) return;
        Pic.Source = poster;
        if (_cantPlay.Contains(id))
        {
            ViewerNote.Text = "This video can't be played here.\nRight-click its tile and choose Show in Explorer to open it in another player.";
            return;
        }
        var (fw, fh) = FitPixels(row);
        _video.Loop = VLoopBtn.IsChecked == true;
        _video.SetRate(_viewerRate);
        _viewerVideo = true; _vSeq = -1; _vBmp = null;
        if (!_video.Play(id, path, _vpos.Get(id), _viewerMuted, fw, fh, preview: false)) { OnVideoFailed(id); return; }
        VideoBar.Visibility = Visibility.Visible;
        SyncVideoBar(true);
        PlaceInfo();
    }

    private void OnVideoRendering(object? s, EventArgs e)
    {
        if (!_viewerVideo || !ViewerOpen || _video.FileId < 0) return;
        if (_video.FrameSeq != _vSeq && _video.CopyFrame(ref _vFrame, out int w, out int h))
        {
            _vSeq = _video.FrameSeq;
            if (_vBmp is null || _vBmp.PixelWidth != w || _vBmp.PixelHeight != h)
            {
                _vBmp = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                Pic.Source = _vBmp;
            }
            _vBmp.WritePixels(new Int32Rect(0, 0, w, h), _vFrame!, w * 4, 0);
            if (!ReferenceEquals(Pic.Source, _vBmp)) Pic.Source = _vBmp;
        }
        long now = Stopwatch.GetTimestamp();
        if ((now - _vUiTick) * 4 >= Stopwatch.Frequency) { _vUiTick = now; SyncVideoBar(false); }
    }

    private static string VClock(double sec)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, sec));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private void SyncVideoBar(bool all)
    {
        if (!_viewerVideo) return;
        double len = _video.LengthSec, pos = _video.PositionSec;
        VTime.Text = VClock(pos) + " / " + VClock(len);
        if (!_vSeeking && len > 0) { _vSyncing = true; VSeek.Value = Math.Clamp(pos / len, 0, 1); _vSyncing = false; }
        VPlayBtn.Content = _video.Playing ? "" : "";
        VMuteBtn.Content = _video.Muted ? "" : "";
    }

    private void OnVPlay(object s, RoutedEventArgs e) { VideoTogglePause(); ViewerRoot.Focus(); }
    private void VideoTogglePause()
    {
        bool pausing = _video.Playing;
        _video.TogglePause();
        if (pausing) SaveVideoPosition();
        SyncVideoBar(true);
    }
    private void OnVPrevFrame(object s, RoutedEventArgs e) { _video.PrevFrame(); SyncVideoBar(true); ViewerRoot.Focus(); }
    private void OnVNextFrame(object s, RoutedEventArgs e) { _video.NextFrame(); SyncVideoBar(true); ViewerRoot.Focus(); }
    private void OnVMute(object s, RoutedEventArgs e) { VideoToggleMute(); ViewerRoot.Focus(); }
    private void VideoToggleMute() { _video.Muted = !_video.Muted; _viewerMuted = _video.Muted; SyncVideoBar(true); }
    private void OnVLoop(object s, RoutedEventArgs e) { _video.Loop = VLoopBtn.IsChecked == true; ViewerRoot.Focus(); }

    private void OnVSpeed(object s, SelectionChangedEventArgs e)
    {
        if (VSpeed?.SelectedItem is ComboBoxItem { Tag: string t } && float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var r))
        {
            _viewerRate = r;
            if (_viewerVideo) _video.SetRate(r);
        }
    }

    private void StepSpeed(int d)
    {
        int k = Math.Clamp(VSpeed.SelectedIndex + d, 0, VSpeed.Items.Count - 1);
        VSpeed.SelectedIndex = k;
    }

    private void OnVVolume(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        _video.Volume = (int)Math.Round(e.NewValue);
        _vpos.Volume = _video.Volume;
        if (_viewerVideo && _video.Muted && e.NewValue > 0 && IsLoaded) { _video.Muted = false; _viewerMuted = false; SyncVideoBar(true); }
    }

    private void OnVSeekDown(object s, MouseButtonEventArgs e) => _vSeeking = true;
    private void OnVSeekUp(object s, MouseButtonEventArgs e)
    {
        _vSeeking = false;
        if (_video.LengthSec > 0) _video.PositionSec = VSeek.Value * _video.LengthSec;
        ViewerRoot.Focus();
    }
    private void OnVSeekChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vSyncing || !_viewerVideo) return;
        if (_video.LengthSec > 0) _video.PositionSec = e.NewValue * _video.LengthSec;
        VTime.Text = VClock(e.NewValue * _video.LengthSec) + " / " + VClock(_video.LengthSec);
    }

    /// <summary>Keys for a video in the viewer. True when the key was used.</summary>
    private bool VideoKey(KeyEventArgs e)
    {
        if (!_viewerVideo || _video.FileId < 0) return false;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case Key.Space: VideoTogglePause(); return true;
            case Key.OemComma: _video.PrevFrame(); SyncVideoBar(true); return true;
            case Key.OemPeriod: _video.NextFrame(); SyncVideoBar(true); return true;
            case Key.M: VideoToggleMute(); return true;
            case Key.OemOpenBrackets: StepSpeed(-1); return true;
            case Key.OemCloseBrackets: StepSpeed(1); return true;
            case Key.L: VLoopBtn.IsChecked = VLoopBtn.IsChecked != true; _video.Loop = VLoopBtn.IsChecked == true; return true;
            case Key.Left when shift: _video.PositionSec = Math.Max(0, _video.PositionSec - 5); SyncVideoBar(true); return true;
            case Key.Right when shift: _video.PositionSec = Math.Min(Math.Max(0, _video.LengthSec - 0.5), _video.PositionSec + 5); SyncVideoBar(true); return true;
            case Key.Up when shift: VVolume.Value = Math.Min(100, VVolume.Value + 5); return true;
            case Key.Down when shift: VVolume.Value = Math.Max(0, VVolume.Value - 5); return true;
        }
        return false;
    }
}
