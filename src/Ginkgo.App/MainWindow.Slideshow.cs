using System.Diagnostics;
using System.Globalization;
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
/// The slideshow: the current view in its order (or shuffled), one picture every few seconds, videos and GIFs to their end if
/// asked. Its settings drop down from the slideshow button (with Start); in the viewer the button starts and stops it and its
/// arrow opens the same settings. Space pauses and resumes, the arrows step, Esc stops. Also: GIFs play in the viewer.
/// </summary>
public partial class MainWindow
{
    private bool _slide, _slidePaused;
    private int[] _slideOrder = Array.Empty<int>();
    private int _slidePos;
    private long _slideNextAt, _slideArrived;
    private readonly DispatcherTimer _slideTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitSlideshow() => _slideTimer.Tick += (_, _) => SlideTick();

    /// <summary>The slideshow button in the bar opens its settings (with Start); in the viewer it starts and stops the slideshow.</summary>
    private void OnSlideshow(object s, RoutedEventArgs e)
    {
        if (_slide) { StopSlideshow(); return; }
        if (!ViewerOpen) { ToggleSlidePanel(SlideshowBtn.IsVisible ? SlideshowBtn : MoreBtn); return; }
        StartSlideshow();
    }

    /// <summary>The arrow beside the viewer's slideshow button: its settings.</summary>
    private void OnSlideMore(object s, RoutedEventArgs e) => ToggleSlidePanel(SlideMoreBtn);

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

    private void ScheduleNext() => _slideNextAt = Stopwatch.GetTimestamp() + (long)(Math.Clamp(_settings.SlideSeconds, 0.5, 3600) * Stopwatch.Frequency);

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
        if (_settings.SlideGifsToEnd && _vGifIndex == _vIndex && _vGif is not null)
        {
            // a GIF plays at least once through (one that never loads doesn't hold the slideshow up)
            int loop = _vGif.LoopMs(_vIndex);
            long since = (Stopwatch.GetTimestamp() - _vGifStarted) * 1000 / Stopwatch.Frequency;
            if (loop > 0) { if (since >= loop && Stopwatch.GetTimestamp() >= _slideNextAt) SlideStep(1); return; }
            if (!_vGif.Settled(_vIndex) && Stopwatch.GetTimestamp() - _slideArrived < Stopwatch.Frequency * Math.Max(4, _settings.SlideSeconds)) return;
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

    // ---- the slideshow's settings ----

    private Popup? _slidePopup;
    private readonly List<Action> _slideSyncs = new();

    private void ToggleSlidePanel(FrameworkElement anchor)
    {
        if (_slidePopup is null)
        {
            _slidePopup = Sticky(new Popup
            {
                PlacementTarget = anchor, Placement = PlacementMode.Bottom, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade, VerticalOffset = 2,
                Child = new Border { Margin = new Thickness(6, 6, 20, 20), Child = SlidePanel() },
            });
            _slidePopup.Closed += (_, _) => ApplyBar();
        }
        if (_slidePopup.IsOpen) { bool same = ReferenceEquals(_slidePopup.PlacementTarget, anchor); _slidePopup.IsOpen = false; if (same) return; }
        _slidePopup.PlacementTarget = anchor;
        _slidePopup.Placement = ViewerOpen ? PlacementMode.Top : PlacementMode.Bottom;
        foreach (var a in _slideSyncs) a();
        double x0 = anchor.TranslatePoint(new Point(0, 0), this).X;
        _slidePopup.HorizontalOffset = Math.Min(0, ActualWidth - 10 - 390 - x0);
        _slidePopup.IsOpen = true;
        ApplyBar();
    }

    private FrameworkElement SlidePanel()
    {
        var card = new Border
        {
            Width = 360, Padding = new Thickness(16, 12, 16, 14), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Bar");
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        System.Windows.Documents.TextElement.SetFontFamily(card, (FontFamily)Application.Current.Resources["AppFont"]);
        var st = new StackPanel();
        TextBlock Head(string t)
        {
            var tb = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            return tb;
        }
        var title = Head("Slideshow"); title.FontSize = 14; title.Margin = new Thickness(0, 0, 0, 2);
        st.Children.Add(title);
        st.Children.Add(Head("Each picture shows for"));
        var secs = new WrapPanel();
        var custom = new TextBox { Width = 54, Height = 26, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), ToolTip = "Any number of seconds" };
        var choices = new List<(ToggleButton b, double v)>();
        foreach (double v in new double[] { 1, 2, 3, 5, 10 })
        {
            var b = new ToggleButton { Content = $"{v:0} s", Height = 26, Margin = new Thickness(0, 0, 4, 4), Padding = new Thickness(9, 0, 9, 0) };
            double val = v;
            b.Click += (_, _) => { _settings.SlideSeconds = val; SaveSettings(); foreach (var a in _slideSyncs) a(); };
            choices.Add((b, v)); secs.Children.Add(b);
        }
        void Custom()
        {
            string t = custom.Text.Trim().TrimEnd('s', 'S', ' ');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out double v) && v >= 0.5)
            { _settings.SlideSeconds = Math.Min(3600, v); SaveSettings(); }
            foreach (var a in _slideSyncs) a();
        }
        custom.LostFocus += (_, _) => Custom();
        custom.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Custom(); e.Handled = true; } };
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 4) };
        var or = new TextBlock { Text = "or", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 6, 0) };
        or.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var sec = new TextBlock { Text = "seconds", VerticalAlignment = VerticalAlignment.Center };
        sec.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        customRow.Children.Add(or); customRow.Children.Add(custom); customRow.Children.Add(sec);
        secs.Children.Add(customRow);
        st.Children.Add(secs);
        _slideSyncs.Add(() =>
        {
            bool any = false;
            foreach (var (b, v) in choices) { b.IsChecked = Math.Abs(_settings.SlideSeconds - v) < 0.01; any |= b.IsChecked == true; }
            if (!custom.IsKeyboardFocused) custom.Text = any ? "" : _settings.SlideSeconds.ToString("0.#", CultureInfo.CurrentCulture);
        });

        CheckBox Check(string text, string tip, Func<bool> get, Action<bool> set)
        {
            var c = new CheckBox { Content = text, ToolTip = tip, Margin = new Thickness(0, 0, 0, 6) };
            c.SetResourceReference(Control.ForegroundProperty, "Text");
            c.Click += (_, _) => { set(c.IsChecked == true); SaveSettings(); };
            _slideSyncs.Add(() => c.IsChecked = get());
            st.Children.Add(c);
            return c;
        }
        st.Children.Add(Head("Videos and GIFs"));
        Check("Play videos all the way through", "Off: a video shows for the same time as a picture", () => _settings.SlideVideosToEnd, v => _settings.SlideVideosToEnd = v);
        Check("Play GIFs all the way through", "Off: a GIF shows for the same time as a picture", () => _settings.SlideGifsToEnd, v => _settings.SlideGifsToEnd = v);
        st.Children.Add(Head("Order"));
        Check("Shuffle", "A random order (starting with the picture you are on)", () => _settings.SlideShuffle, v => _settings.SlideShuffle = v);
        Check("Start again at the end", "Off: the slideshow stops after the last one", () => _settings.SlideLoop, v => _settings.SlideLoop = v);

        var start = new Button { Content = "Start slideshow", Height = 30, Padding = new Thickness(16, 0, 16, 0), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        start.Click += (_, _) => { _slidePopup!.IsOpen = false; if (_slide) StopSlideshow(); StartSlideshow(); };
        _slideSyncs.Add(() => start.Content = _slide ? "Start again from here" : "Start slideshow");
        st.Children.Add(start);
        var keys = new TextBlock { Text = "While it runs: Space pauses, the arrows step, Esc stops.", FontSize = 11.5, Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        keys.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        st.Children.Add(keys);
        card.Child = st;
        return card;
    }

    // ---- GIFs play in the viewer ----

    private GifAnimator? _vGif;
    private int _vGifIndex = -1;
    private long _vGifStarted;
    private WriteableBitmap? _vGifBmp;
    private readonly DispatcherTimer _vGifTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
    private ThumbLoader.Pix? _vGifShown;
    private bool _vGifHooked;

    /// <summary>The open file is a GIF: its frames are decoded at the viewer's size in the background, then it plays.</summary>
    private void StartViewerGif(int index, int row)
    {
        if (_vGifIndex == index && _vGif is not null) return;
        StopViewerGif();
        if (_model is not { } m) return;
        _vGif ??= new GifAnimator(i => i == _vGifIndex && i >= 0 && i < _paths.Length ? _paths[i] : null, () => Dispatcher.BeginInvoke(() =>
        {
            if (_vGifIndex >= 0) { _vGifStarted = Stopwatch.GetTimestamp(); _vGifTimer.Start(); }
        })) { Limit = 1, BudgetPerGif = 192L << 20, MaxFrames = 600 };
        if (!_vGifHooked) { _vGifHooked = true; _vGifTimer.Tick += (_, _) => ViewerGifTick(); }
        _vGifIndex = index;
        var (fw, fh) = FitPixels(row);
        double k = Math.Min(1, Math.Min((double)fw / Math.Max(1, m.W[row]), (double)fh / Math.Max(1, m.H[row])));
        int w = Math.Max(24, (int)(m.W[row] * k)), h = Math.Max(24, (int)(m.H[row] * k));
        _vGifStarted = Stopwatch.GetTimestamp();
        _vGif.SetVisible(new List<(int index, int w, int h)> { (index, w, h) });
    }

    private void ViewerGifTick()
    {
        if (_vGif is null || _vGifIndex < 0 || _vGifIndex != _vIndex || !ViewerOpen) { StopViewerGif(); return; }
        if (_slide && _slidePaused) return;
        var f = _vGif.Frame(_vGifIndex);
        if (f is null || ReferenceEquals(f, _vGifShown)) return;
        _vGifShown = f;
        if (_vGifBmp is null || _vGifBmp.PixelWidth != f.W || _vGifBmp.PixelHeight != f.H)
            _vGifBmp = new WriteableBitmap(f.W, f.H, 96, 96, PixelFormats.Pbgra32, null);
        _vGifBmp.WritePixels(new Int32Rect(0, 0, f.W, f.H), f.Data, f.W * 4, 0);
        if (!ReferenceEquals(Pic.Source, _vGifBmp)) Pic.Source = _vGifBmp;
    }

    private void StopViewerGif()
    {
        _vGifTimer.Stop();
        _vGif?.Clear();
        _vGifIndex = -1; _vGifShown = null;
    }
}
