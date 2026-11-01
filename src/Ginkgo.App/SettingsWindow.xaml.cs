using Ginkgo.Data;
using System.Windows;
using System.Windows.Controls;

namespace Ginkgo.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly Engine _engine;
    private CancellationTokenSource? _sharpCts;
    private bool _init = true;

    public SettingsWindow(MainWindow main, AppSettings s, Engine engine)
    {
        InitializeComponent();
        Theme.DarkTitleBar(this);
        _main = main; _s = s; _engine = engine;
        LibraryBox.Text = s.LibraryRoot;
        CacheLine.Text = "Small thumbnails: " + (string.IsNullOrEmpty(engine.ThumbDir) ? s.FastCacheDir : engine.ThumbDir) + "   |   sharp versions: " + s.CacheDir + "   |   index: " + AppSettings.IndexPath + (engine.FastCacheNote.Length > 0 ? "   |   " + engine.FastCacheNote : "");
        foreach (var p in AppSettings.CacheProfiles) CacheBox.Items.Add(p.name);
        CacheBox.SelectedIndex = Math.Clamp(s.CacheProfile, 0, AppSettings.CacheProfiles.Length - 1);
        foreach (ComboBoxItem it in LookBox.Items) if (int.Parse((string)it.Tag) == s.LookAheadWindows) LookBox.SelectedItem = it;
        if (LookBox.SelectedItem is null) LookBox.SelectedIndex = 1;
        FadeBox.IsChecked = s.FadeSharpening;
        RoundBox.IsChecked = s.CoverRounded;
        RadiusSlider.Value = s.CoverRadius; RadiusSlider.IsEnabled = s.CoverRounded; RadiusText.Text = $"{s.CoverRadius} px";
        WarmBox.IsChecked = s.WarmCache;
        BuildGpu();
        _init = false;
        OnMeasure(this, new RoutedEventArgs());
    }

    private void OnChooseLibrary(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your library folder", InitialDirectory = _s.LibraryRoot };
        if (dlg.ShowDialog(this) != true) return;
        _s.LibraryRoot = dlg.FolderName;
        LibraryBox.Text = _s.LibraryRoot;
        _main.ApplySettings();
        Result.Text = "Library folder changed. Restart Ginkgo to look through the new folder.";
    }

    private async void OnRescan(object s, RoutedEventArgs e) { Result.Text = "Looking for changes..."; await _engine.Rescan(); Result.Text = _engine.Status; }

    private void OnCacheChanged(object s, SelectionChangedEventArgs e) { if (_init) return; _s.CacheProfile = CacheBox.SelectedIndex; _main.ApplySettings(); }
    private void OnLookChanged(object s, SelectionChangedEventArgs e)
    {
        if (_init) return;
        _s.LookAheadWindows = int.Parse((string)((ComboBoxItem)LookBox.SelectedItem).Tag); _main.ApplySettings();
    }
    private void OnWarmChanged(object s, RoutedEventArgs e) { _s.WarmCache = WarmBox.IsChecked == true; _engine.Warmer.Enabled = _s.WarmCache; _main.ApplySettings(); Result.Text = "The limit and GIF settings are in Optimisations (next to Settings in the menu bar)."; }
    private void OnLook(object s, RoutedEventArgs e) => _main.OpenLook();
    private void OnRoundChanged(object s, RoutedEventArgs e) { _s.CoverRounded = RoundBox.IsChecked == true; RadiusSlider.IsEnabled = _s.CoverRounded; _main.ApplySettings(); }
    private void OnRadiusChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusText is null) return;
        RadiusText.Text = $"{(int)e.NewValue} px";
        if (_init) return;
        _s.CoverRadius = (int)e.NewValue; _main.ApplySettings();
    }
    private void OnFadeChanged(object s, RoutedEventArgs e) { _s.FadeSharpening = FadeBox.IsChecked == true; _main.ApplySettings(); }

    private async Task RunSharp(bool everything)
    {
        SharpBigBtn.IsEnabled = SharpAllBtn.IsEnabled = false; SharpStopBtn.IsEnabled = true;
        _sharpCts = new CancellationTokenSource();
        try { await _engine.PrepareSharp(everything, _sharpCts.Token); Result.Text = "Sharp versions are ready. Use Update view in the main window to use them."; }
        catch (OperationCanceledException) { Result.Text = "Stopped."; }
        catch (Exception ex) { Result.Text = "Problem: " + ex.Message; }
        SharpBigBtn.IsEnabled = SharpAllBtn.IsEnabled = true; SharpStopBtn.IsEnabled = false;
    }

    private void OnSharpBig(object s, RoutedEventArgs e) => _ = RunSharp(false);
    private void OnSharpAll(object s, RoutedEventArgs e) => _ = RunSharp(true);
    private void OnSharpStop(object s, RoutedEventArgs e) => _sharpCts?.Cancel();

    private void OnMeasure(object s, RoutedEventArgs e)
    {
        var usage = _engine.CacheUsage();
        UsageText.Text = usage.Count == 0 ? "(not ready yet)" : string.Join("\n", usage.Select(u =>
            $"{u.Name,-6} {u.OnDisk / 1048576.0,9:F0} MB on disk   {u.Wasted / 1048576.0,7:F0} MB unused"));
    }

    private async void OnClean(object s, RoutedEventArgs e)
    {
        CleanBtn.IsEnabled = false; Result.Text = "Cleaning up... (the grid is rebuilt afterwards)";
        CompactResult? r = null;
        try { await _main.WithViewPaused(async () => { r = await Task.Run(() => _engine.CleanUpCache()); }); }
        catch (Exception ex) { Result.Text = "Problem: " + ex.Message; }
        if (r is not null) Result.Text = $"Cache went from {r.BytesBefore / 1048576.0:F0} MB to {r.BytesAfter / 1048576.0:F0} MB.";
        OnMeasure(this, new RoutedEventArgs());
        CleanBtn.IsEnabled = true;
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();

    // ---- GPU grid ----

    private readonly ComboBox _drawBox = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _gpuStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#9AA0A6"), Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button _prefBtn = new() { Content = "Use the NVIDIA GPU for Ginkgo", Visibility = Visibility.Collapsed };
    private readonly Button _restartBtn = new() { Content = "Restart Ginkgo", Visibility = Visibility.Collapsed };
    private readonly StackPanel _gifPanel = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _gifOff = new() { Text = "These apply only while the GPU grid is drawing.", Foreground = Brush("#9AA0A6"), Margin = new Thickness(0, 0, 0, 6) };

    private static System.Windows.Media.Brush Brush(string hex) => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

    private static DockPanel Row(string label, UIElement control, string? valueText = null, TextBlock? value = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (value is not null) { value.Width = 52; value.TextAlignment = TextAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(value, Dock.Right); d.Children.Add(value); }
        DockPanel.SetDock(control, Dock.Right); d.Children.Add(control);
        d.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        return d;
    }

    private void BuildGpu()
    {
        _drawBox.Items.Add(new ComboBoxItem { Content = "Current", Tag = "Current" });
        _drawBox.Items.Add(new ComboBoxItem { Content = "GPU grid", Tag = "GPU" });
        _drawBox.SelectedIndex = _s.Renderer == "GPU" ? 1 : 0;
        _drawBox.SelectionChanged += (_, _) =>
        {
            if (_init) return;
            _main.SetRenderer((string)((ComboBoxItem)_drawBox.SelectedItem).Tag);
            if (_s.Renderer == "GPU") RunGpuCheck(); else ShowGpu();
        };
        GpuPanel.Children.Add(Row("Drawing", _drawBox));
        GpuPanel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Brush("#9AA0A6"),
            Text = "The GPU grid draws the thumbnails on the NVIDIA graphics card instead of the processor. It is being built in stages: this version draws still pictures (GIFs play the same way as before). Press F3 in the grid to compare the two."
        });
        GpuPanel.Children.Add(_gpuStatus);
        var buttons = new WrapPanel();
        var again = new Button { Content = "Check again" };
        again.Click += (_, _) => RunGpuCheck();
        _prefBtn.Click += (_, _) =>
        {
            if (GpuCheck.SetPreference()) { _gpuStatus.Text = "Windows will now run Ginkgo on the high-performance GPU. Restart Ginkgo for it to take effect."; _restartBtn.Visibility = Visibility.Visible; _prefBtn.Visibility = Visibility.Collapsed; }
            else _gpuStatus.Text = "Couldn't save the Windows graphics preference. You can set it in Windows Settings > System > Display > Graphics.";
        };
        _restartBtn.Click += (_, _) => { Close(); _main.Restart(); };
        buttons.Children.Add(again); buttons.Children.Add(_prefBtn); buttons.Children.Add(_restartBtn);
        GpuPanel.Children.Add(buttons);

        // GIFs (GPU grid only)
        _gifPanel.Children.Add(_gifOff);
        var animate = new CheckBox { Content = "Animate GIFs in the grid (off: still covers; they play on hover)", Foreground = Brush("#E4E4E8"), IsChecked = _s.GifAnimate, Margin = new Thickness(0, 0, 0, 6) };
        animate.Click += (_, _) => { _s.GifAnimate = animate.IsChecked == true; _main.ApplySettings(); };
        _gifPanel.Children.Add(animate);

        var lenText = new TextBlock();
        var len = new Slider { Width = 160, Minimum = 1, Maximum = 15, IsSnapToTickEnabled = true, TickFrequency = 1, Value = _s.GifLengthSec, VerticalAlignment = VerticalAlignment.Center };
        lenText.Text = $"{_s.GifLengthSec:0} s";
        len.ValueChanged += (_, e) => { lenText.Text = $"{e.NewValue:0} s"; if (_init) return; _s.GifLengthSec = e.NewValue; _main.ApplySettings(); };
        _gifPanel.Children.Add(Row("GIF length in the grid (longer GIFs loop this much; full length on hover)", len, null, lenText));

        var fadeText = new TextBlock();
        var fadeLen = new Slider { Width = 120, Minimum = 0.1, Maximum = 1.0, IsSnapToTickEnabled = true, TickFrequency = 0.1, Value = _s.GifCrossFadeSec, VerticalAlignment = VerticalAlignment.Center, IsEnabled = _s.GifCrossFade };
        fadeText.Text = $"{_s.GifCrossFadeSec:0.0} s";
        fadeLen.ValueChanged += (_, e) => { fadeText.Text = $"{e.NewValue:0.0} s"; if (_init) return; _s.GifCrossFadeSec = Math.Round(e.NewValue, 1); _main.ApplySettings(); };
        var fade = new CheckBox { Content = "Cross-fade at the loop (GIFs cut to the length above)", Foreground = Brush("#E4E4E8"), IsChecked = _s.GifCrossFade, VerticalAlignment = VerticalAlignment.Center };
        fade.Click += (_, _) => { _s.GifCrossFade = fade.IsChecked == true; fadeLen.IsEnabled = _s.GifCrossFade; _main.ApplySettings(); };
        var fadeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        fadeText.Width = 52; fadeText.TextAlignment = TextAlignment.Right; fadeText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(fadeText, Dock.Right); fadeRow.Children.Add(fadeText);
        DockPanel.SetDock(fadeLen, Dock.Right); fadeRow.Children.Add(fadeLen);
        fadeRow.Children.Add(fade);
        _gifPanel.Children.Add(fadeRow);

        var fps = new ComboBox { MinWidth = 120 };
        foreach (var (label, v) in new[] { ("Automatic", 0), ("8 fps", 8), ("10 fps", 10), ("15 fps", 15), ("20 fps", 20) })
        {
            var it = new ComboBoxItem { Content = label, Tag = v };
            fps.Items.Add(it);
            if (v == _s.GifFps) fps.SelectedItem = it;
        }
        if (fps.SelectedItem is null) fps.SelectedIndex = 0;
        fps.SelectionChanged += (_, _) => { if (_init) return; _s.GifFps = (int)((ComboBoxItem)fps.SelectedItem).Tag; _main.ApplySettings(); };
        _gifPanel.Children.Add(Row("GIF frame rate (Automatic lowers it when many GIFs are on screen)", fps));
        _gifStat.Foreground = Brush("#9AA0A6"); _gifStat.TextWrapping = TextWrapping.Wrap; _gifStat.Margin = new Thickness(0, 4, 0, 0);
        _gifPanel.Children.Add(_gifStat);
        GpuPanel.Children.Add(_gifPanel);
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => GifStat();
        Loaded += (_, _) => tick.Start(); Closed += (_, _) => tick.Stop();
        GifStat();

        if (_main.Gpu is null && !_main.GpuChecking) RunGpuCheck(); else ShowGpu();
    }

    private readonly TextBlock _gifStat = new();

    private void GifStat()
    {
        var gm = _engine.GifMaker;
        if (gm is null || !gm.Stats.Running) { _gifStat.Text = "GIF versions for the GPU grid are made once the GPU grid is drawing."; return; }
        var st = gm.Stats;
        long animated = Math.Max(0, st.Animated - st.Stills);
        double avg = st.Made > 0 ? st.Bytes / (double)st.Made : 0;
        string est = st.Made >= 20 && st.Pending > 0 ? $" · about {(st.Bytes + avg * st.Pending) / 1073741824.0:0.0} GB when all are made" : "";
        _gifStat.Text = $"GIF versions: {st.Made:N0} of {animated:N0} animated GIFs made ({st.Stills:N0} are still pictures) · {st.Bytes / 1048576.0:N0} MB on {_s.CacheDir}{est}"
                      + (st.Paused ? " · paused while you scroll" : st.Pending > 0 ? " · making them in the background" : "")
                      + (st.Failed > 0 ? $" · {st.Failed} couldn't be read ({st.LastError})" : "")
                      + ". Changing the length remakes them in the background; the old ones are used until then.";
    }

    private void RunGpuCheck()
    {
        _gpuStatus.Text = "Checking the GPU...";
        _main.CheckGpu(() => { if (IsLoaded) ShowGpu(); });
    }

    private void ShowGpu()
    {
        var g = _main.Gpu;
        if (g is null) { _gpuStatus.Text = _main.GpuChecking ? "Checking the GPU..." : "Not checked yet."; }
        else if (g.Ok) _gpuStatus.Text = $"{g.Name} · {g.VideoMemory / 1073741824.0:0} GB · {g.Status}" + (_main.GpuDrawing ? " · drawing the grid" : "");
        else _gpuStatus.Text = $"{(g.Name.Length > 0 ? g.Name + " · " : "")}can't be used: {g.FailedStep}: {g.Status}." + (_s.Renderer == "GPU" ? " Current is drawing." : "");
        if (_main.GpuFallback.Length > 0 && (g is null || g.Ok)) _gpuStatus.Text += "\n" + _main.GpuFallback + ".";
        bool needPref = g is not null && !g.OnNvidia && g.Name.Length > 0 && !GpuCheck.PreferenceIsSet();
        _prefBtn.Visibility = needPref ? Visibility.Visible : Visibility.Collapsed;
        if (g is not null && !g.OnNvidia && GpuCheck.PreferenceIsSet() && g.Name.Length > 0) _restartBtn.Visibility = Visibility.Visible;
        bool gpuOn = _s.Renderer == "GPU" && g is { Ok: true };
        foreach (UIElement c in _gifPanel.Children) if (c != _gifOff) c.IsEnabled = gpuOn;
        _gifPanel.Opacity = gpuOn ? 1 : 0.55;
        _gifOff.Text = gpuOn ? "GIF settings for the GPU grid. Playing them in the grid comes in the next build; this one makes the GIF versions." : "These apply only while the GPU grid is drawing.";
    }
}
