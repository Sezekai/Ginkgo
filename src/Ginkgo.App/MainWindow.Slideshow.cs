using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// The slideshow: the current view in its order (or shuffled), one picture every few seconds, videos to their end if Settings says
/// so. Space pauses and resumes, the arrows step, Esc stops. Videos carry on where they were left, as everywhere else.
/// </summary>
public partial class MainWindow
{
    private bool _slide, _slidePaused;
    private int[] _slideOrder = Array.Empty<int>();
    private int _slidePos;
    private long _slideNextAt, _slideArrived;
    private readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitSlideshow() => _slideTimer.Tick += (_, _) => SlideTick();

    private void OnSlideshow(object s, RoutedEventArgs e)
    {
        if (_slide) { StopSlideshow(); return; }
        StartSlideshow();
    }

    private void StartSlideshow()
    {
        int files = _paths.Length - _firstFile;
        if (files <= 0) { StatusText.Text = "Nothing to show in a slideshow here."; return; }
        // from the open picture, or the one under the mouse, or the first one on screen
        int start = ViewerOpen ? _vIndex : View.HoverIndex >= _firstFile ? View.HoverIndex : Math.Max(_firstFile, View.VisibleRange().first);
        start = Math.Clamp(start, _firstFile, _paths.Length - 1);
        var order = Enumerable.Range(_firstFile, files).ToList();
        if (_settings.SlideShuffle)
        {
            var rnd = new Random();
            for (int k = order.Count - 1; k > 0; k--) { int j = rnd.Next(k + 1); (order[k], order[j]) = (order[j], order[k]); }
            order.Remove(start); order.Insert(0, start);
        }
        _slideOrder = order.ToArray();
        _slidePos = Array.IndexOf(_slideOrder, start);
        _slide = true; _slidePaused = false; _slideArrived = Stopwatch.GetTimestamp();
        if (!ViewerOpen) OpenViewer(start); else ShowIndex(start);
        ScheduleNext();
        _slideTimer.Start();
        SlideBtn.Content = ""; SlideBtn.ToolTip = "Stop the slideshow (Esc)";
        Osd("Slideshow" + (_settings.SlideShuffle ? ", shuffled" : ""));
    }

    private void StopSlideshow()
    {
        if (!_slide) return;
        _slide = false; _slideTimer.Stop();
        if (_vEngine is not null) _vEngine.Loop = VLoopBtn.IsChecked == true;
        SlideBtn.Content = ""; SlideBtn.ToolTip = "Slideshow from here (Space pauses, arrows step, Esc stops)";
        if (ViewerOpen) Osd("Slideshow stopped");
    }

    private void ScheduleNext() => _slideNextAt = Stopwatch.GetTimestamp() + (long)(Math.Clamp(_settings.SlideSeconds, 1, 600) * Stopwatch.Frequency);

    private bool SlideOnVideo => _viewerVideo && _vEngine is not null;

    private void SlideTick()
    {
        if (!_slide) return;
        if (!ViewerOpen) { StopSlideshow(); return; }
        if (_slidePaused) { ScheduleNext(); return; }
        if (SlideOnVideo && _settings.SlideVideosToEnd)
        {
            var e = _vEngine!;
            e.Loop = false;                                        // so the end can be seen
            if (e.AtEnd) SlideStep(1);
            // a video that won't play doesn't hold the slideshow up
            else if (e.LengthSec <= 0 && Stopwatch.GetTimestamp() - _slideArrived > Stopwatch.Frequency * Math.Max(4, _settings.SlideSeconds)) SlideStep(1);
            return;
        }
        if (Stopwatch.GetTimestamp() >= _slideNextAt) SlideStep(1);
    }

    /// <summary>The next (or previous) one in the slideshow's order; at the end it starts again or stops.</summary>
    private void SlideStep(int dir)
    {
        if (_slideOrder.Length == 0) return;
        int pos = _slidePos + dir;
        if (pos >= _slideOrder.Length)
        {
            if (!_settings.SlideLoop) { StopSlideshow(); Osd("Slideshow finished"); return; }
            pos = 0;
        }
        if (pos < 0) pos = _settings.SlideLoop ? _slideOrder.Length - 1 : 0;
        _slidePos = pos;
        int idx = _slideOrder[pos];
        if (idx >= _firstFile && idx < _paths.Length) ShowIndex(idx);
        _slideArrived = Stopwatch.GetTimestamp();
        ScheduleNext();
    }

    /// <summary>Keys while the slideshow runs: Space pauses and resumes it (and the video), the arrows step, Esc stops. True when handled.</summary>
    private bool SlideKey(KeyEventArgs e)
    {
        if (!_slide) return false;
        switch (e.Key)
        {
            case Key.Escape: StopSlideshow(); return true;
            case Key.Space:
                _slidePaused = !_slidePaused;
                if (SlideOnVideo && _vEngine!.Playing == _slidePaused) VideoTogglePause();
                ScheduleNext();
                Osd(_slidePaused ? "Slideshow paused" : "Slideshow");
                return true;
            case Key.Right: case Key.PageDown: SlideStep(1); return true;
            case Key.Left: case Key.PageUp: SlideStep(-1); return true;
        }
        return false;
    }
}
