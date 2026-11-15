using Ginkgo.Data;
using System.Windows;
using System.Windows.Controls;

namespace Ginkgo.App;

public partial class SettingsPage : UserControl
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly Engine _engine;
    private CancellationTokenSource? _sharpCts;
    private bool _init = true;

    public SettingsPage(MainWindow main, AppSettings s, Engine engine)
    {
        InitializeComponent();
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
        BuildSets();
        BuildView();
        BuildResets();
        Reg(CacheBox, nameof(AppSettings.CacheProfile));
        Reg(LookBox, nameof(AppSettings.LookAheadWindows));
        Reg(WarmBox, nameof(AppSettings.WarmCache));
        Reg(RoundBox, nameof(AppSettings.CoverRounded));
        Reg(RadiusSlider, nameof(AppSettings.CoverRadius));
        Reg(FadeBox, nameof(AppSettings.FadeSharpening));
        _init = false;
        WireResets();
        // the slow parts (adding up the cache from the index, listing the library's folders on its drive) run in the
        // background each time the page is shown, so it opens at once
        Loaded += (_, _) => { OnMeasure(this, new RoutedEventArgs()); _ = BuildFolderTreeAsync(); };
    }

    private void OnChooseLibrary(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your library folder", InitialDirectory = _s.LibraryRoot };
        if (dlg.ShowDialog(Host) != true) return;
        string old = _s.LibraryRoot;
        _s.LibraryRoot = dlg.FolderName;
        LibraryBox.Text = _s.LibraryRoot;
        // the same library somewhere new (another drive letter): the index's paths move, so nothing is made again
        if (MessageBox.Show(Host, $"Is this the same library, moved from {old}?\n\nYes: Ginkgo matches files by their place inside the library and keeps all thumbnails.\nNo: it is treated as a different library.", "Library folder", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
            _s.PendingRelocateFrom = old;
        _main.ApplySettings();
        if (MessageBox.Show(Host, "Restart Ginkgo now to use the new folder?", "Library folder", MessageBoxButton.YesNo) == MessageBoxResult.Yes) App.Restart();
        else Result.Text = "Library folder changed. It is used from the next start.";
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
    private void OnAppearance(object s, RoutedEventArgs e) => _main.OpenAppearance();

    // ---- library folders: untick to remove from Ginkgo ----

    private readonly HashSet<string> _excl = new(StringComparer.OrdinalIgnoreCase);

    private bool _treeBuilt;

    /// <summary>The library's folders, read from the drive in the background (a big library on a hard drive takes a moment).</summary>
    private async Task BuildFolderTreeAsync()
    {
        if (_treeBuilt) return;
        _treeBuilt = true;
        _excl.Clear();
        foreach (var e in _s.ExcludedFolders) _excl.Add(e.Replace('/', '\\'));
        FolderTree.Items.Clear();
        FolderTree.Items.Add(new TreeViewItem { Header = "Reading the library's folders...", Foreground = ThemeKit.Brush("Dim") });
        string root = _s.LibraryRoot;
        var tops = await Task.Run(() =>
        {
            if (!System.IO.Directory.Exists(root)) return null;
            return Kids(root).Select(d => (d, HasKids(d))).ToList();
        });
        FolderTree.Items.Clear();
        if (tops is null) { FolderTree.Items.Add(new TreeViewItem { Header = "(the library folder can't be found right now)" }); return; }
        foreach (var (d, kids) in tops) FolderTree.Items.Add(Node(d, kids));
    }

    private static bool HasKids(string dir)
    {
        try { return System.IO.Directory.EnumerateDirectories(dir).Any(); } catch { return false; }
    }

    private static IEnumerable<string> Kids(string dir)
    {
        try { return System.IO.Directory.GetDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private TreeViewItem Node(string full, bool hasKids)
    {
        string rel = System.IO.Path.GetRelativePath(_s.LibraryRoot, full);
        var cb = new CheckBox { Content = System.IO.Path.GetFileName(full), IsChecked = !_excl.Contains(rel), Foreground = ThemeKit.Brush("Text") };
        var item = new TreeViewItem { Header = cb, Tag = full };
        cb.Click += (_, _) =>
        {
            if (cb.IsChecked == true) _excl.Remove(rel); else _excl.Add(rel);
            ApplyFoldersBtn.IsEnabled = true;
            FoldersNote.Text = $"{_excl.Count} folder{(_excl.Count == 1 ? "" : "s")} left out";
        };
        if (hasKids)
        {
            item.Items.Add("…");                                    // filled when opened, from the drive in the background
            item.Expanded += async (_, _) =>
            {
                if (item.Items.Count == 1 && item.Items[0] is string)
                {
                    var kids = await Task.Run(() => Kids(full).Select(k => (k, HasKids(k))).ToList());
                    item.Items.Clear();
                    foreach (var (k, more) in kids) item.Items.Add(Node(k, more));
                }
            };
        }
        return item;
    }

    private async void OnApplyFolders(object s, RoutedEventArgs e)
    {
        var before = new HashSet<string>(_s.ExcludedFolders.Select(x => x.Replace('/', '\\')), StringComparer.OrdinalIgnoreCase);
        var removed = _excl.Where(x => !before.Contains(x)).ToList();
        bool restored = before.Any(x => !_excl.Contains(x));
        _s.ExcludedFolders = _excl.ToList();
        _main.ApplySettings();
        ApplyFoldersBtn.IsEnabled = false;
        Result.Text = "Applying the folder changes...";
        await _engine.ExcludedChanged(removed, restored);
        await _main.ReloadAfterFolders();
        Result.Text = $"Done: {removed.Count} folder{(removed.Count == 1 ? "" : "s")} removed{(restored ? ", and the ones brought back were looked through again" : "")}.";
    }

    // ---- moving the cache, and resets ----

    private void OnMoveCache(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Move the cache to", InitialDirectory = _s.CacheDir };
        if (dlg.ShowDialog(Host) != true) return;
        if (string.Equals(System.IO.Path.GetFullPath(dlg.FolderName).TrimEnd('\\'), System.IO.Path.GetFullPath(_s.CacheDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
        if (MessageBox.Show(Host, $"Move the cache from\n{_s.CacheDir}\nto\n{dlg.FolderName}?\n\nGinkgo restarts and moves it before opening, with progress. Nothing has to be made again.", "Move cache", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _s.PendingCacheMove = dlg.FolderName;
        _main.ApplySettings();
        App.Restart();
    }

    private void BuildResets()
    {
        void Reset(string label, string question, Action act, bool restart = false)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) =>
            {
                if (MessageBox.Show(Host, question + (restart ? "\n\nGinkgo restarts to do it." : ""), label, MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
                act();
                _main.ApplySettings();
                if (restart) App.Restart(); else Result.Text = label + ": done.";
            };
            ResetPanel.Children.Add(b);
        }
        Reset("Reset settings", "Put every setting back to how Ginkgo starts out? (The library and cache folders, saved searches and themes stay.)", () => _main.ResetSettings(), restart: true);
        Reset("Reset themes", "Delete your own themes and go back to the default theme?", () => { ThemeKit.ResetOwn(); _s.Theme = new AppTheme(); ThemeKit.Apply(_s.Theme, _s.Look); _main.ThemeApplied(); });
        Reset("Clear saved searches", "Delete every saved search?", () => _main.ClearSavedSearches());
        Reset("Clear defaults", "Forget the defaults you saved for each section (Ginkgo's own come back)?", () => _s.Defaults.Clear());
        Reset("Rebuild the index", "Make the index again from scratch? Ginkgo looks through the whole library again and makes thumbnails again (this takes a while).", () => { if (!_s.PendingResets.Contains("index")) _s.PendingResets.Add("index"); }, restart: true);
        Reset("Clear thumbnails", "Delete every thumbnail and make them again in the background?", () => { if (!_s.PendingResets.Contains("thumbnails")) _s.PendingResets.Add("thumbnails"); }, restart: true);
        Reset("Clear video positions", "Forget where every video was paused, which were watched, and each video's volume?", () => _main.ClearVideoPositions());
        Reset("Clear folder covers", "Forget every folder cover you picked or adjusted (they go back to random)?", () => _main.ClearCovers());
    }
    private void OnOutlines(object s, RoutedEventArgs e) => new OutlineWindow(_s, _main.OutlinesChanged) { Owner = Host }.ShowDialog();
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

    private async void OnMeasure(object s, RoutedEventArgs e)
    {
        if (UsageText.Text.Length == 0) UsageText.Text = "Measuring...";
        IReadOnlyList<TierUsage> usage;
        try { usage = await Task.Run(() => _engine.CacheUsage()); }
        catch (Exception ex) { UsageText.Text = "Couldn't measure: " + ex.Message; return; }
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

    private void OnClose(object s, RoutedEventArgs e) => _main.LeavePage();
    private Window Host => Window.GetWindow(this) ?? Application.Current.MainWindow!;

    // ---- GPU grid ----

    private readonly ComboBox _drawBox = new() { MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock _gpuStatus = new() { TextWrapping = TextWrapping.Wrap, Foreground = ThemeKit.Brush("Dim"), Margin = new Thickness(0, 6, 0, 6) };
    private readonly Button _prefBtn = new() { Content = "Use the NVIDIA GPU for Ginkgo", Visibility = Visibility.Collapsed };
    private readonly Button _restartBtn = new() { Content = "Restart Ginkgo", Visibility = Visibility.Collapsed };
    private readonly StackPanel _gifPanel = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _gifOff = new() { Text = "These apply only while the GPU grid is drawing.", Foreground = ThemeKit.Brush("Dim"), Margin = new Thickness(0, 0, 0, 6) };

    private static System.Windows.Media.Brush Brush(string hex) => (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;

    private static DockPanel Row(string label, UIElement control, string? valueText = null, TextBlock? value = null)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (value is not null) { value.Width = 52; value.TextAlignment = TextAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center; DockPanel.SetDock(value, Dock.Right); d.Children.Add(value); }
        DockPanel.SetDock(control, Dock.Right); d.Children.Add(control);
        d.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), TextWrapping = TextWrapping.Wrap });
        return d;
    }

    /// <summary>Space between tiles, details on the tile under the mouse, and the slideshow's timing and order.</summary>
    private void BuildView()
    {
        var gap = new Slider { Width = 160, Minimum = 0, Maximum = 20, IsSnapToTickEnabled = true, TickFrequency = 1, Value = Math.Clamp(_s.Spacing, 0, 20), VerticalAlignment = VerticalAlignment.Center };
        var gapText = new TextBlock { Text = $"{_s.Spacing:0} px" };
        gap.ValueChanged += (_, e) => { gapText.Text = $"{e.NewValue:0} px"; if (_init) return; _s.Spacing = (float)e.NewValue; _main.ApplySpacing(); _main.ApplySettings(); };
        ViewPanel.Children.Add(Row("Space between pictures", gap, null, gapText)); Reg(gap, nameof(AppSettings.Spacing));
        var hover = new CheckBox { Content = "Show size and dimensions in the corner of the picture under the mouse", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.HoverDetails, Margin = new Thickness(0, 0, 0, 10) };
        hover.Click += (_, _) => { _s.HoverDetails = hover.IsChecked == true; _main.ApplySettings(); };
        ViewPanel.Children.Add(hover); Reg(hover, nameof(AppSettings.HoverDetails));
        var secs = new Slider { Width = 160, Minimum = 1, Maximum = 60, IsSnapToTickEnabled = true, TickFrequency = 1, Value = Math.Clamp(_s.SlideSeconds, 1, 60), VerticalAlignment = VerticalAlignment.Center };
        var secsText = new TextBlock { Text = $"{_s.SlideSeconds:0} s" };
        secs.ValueChanged += (_, e) => { secsText.Text = $"{e.NewValue:0} s"; if (_init) return; _s.SlideSeconds = e.NewValue; _main.ApplySettings(); };
        ViewPanel.Children.Add(Row("Slideshow: each picture shows for", secs, null, secsText)); Reg(secs, nameof(AppSettings.SlideSeconds));
        CheckBox Box(string text, bool on, Action<bool> set)
        {
            var b = new CheckBox { Content = text, Foreground = ThemeKit.Brush("Text"), IsChecked = on, Margin = new Thickness(0, 0, 0, 6) };
            b.Click += (_, _) => { set(b.IsChecked == true); _main.ApplySettings(); };
            ViewPanel.Children.Add(b);
            return b;
        }
        Reg(Box("Shuffle (otherwise the view's own order)", _s.SlideShuffle, v => _s.SlideShuffle = v), nameof(AppSettings.SlideShuffle));
        Reg(Box("Start again from the beginning at the end", _s.SlideLoop, v => _s.SlideLoop = v), nameof(AppSettings.SlideLoop));
        Reg(Box("Play each video to its end before moving on (otherwise videos get the same time as pictures)", _s.SlideVideosToEnd, v => _s.SlideVideosToEnd = v), nameof(AppSettings.SlideVideosToEnd));
    }

    /// <summary>How sets show (a block up to a size, a stacked tile above it), how they sort, and whether copies of a file show once.</summary>
    private void BuildSets()
    {
        var limit = new Slider { Width = 160, Minimum = 1, Maximum = 20, IsSnapToTickEnabled = true, TickFrequency = 1, Value = Math.Clamp(_s.SetLimit, 1, 20), VerticalAlignment = VerticalAlignment.Center };
        var limitText = new TextBlock { Text = $"{_s.SetLimit}" };
        limit.ValueChanged += (_, e) => { limitText.Text = $"{(int)e.NewValue}"; if (_init) return; _s.SetLimit = (int)e.NewValue; _main.ApplySettings(); _main.RebuildView(); };
        SetsPanel.Children.Add(Row("Sets with up to this many images show as one block; bigger ones as a stacked tile that opens with a click", limit, null, limitText)); Reg(limit, nameof(AppSettings.SetLimit));
        var first = new CheckBox { Content = "Sort every set by its first image (otherwise: name by the set's folder, size by its total, dates by its newest file)", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.SetsByFirst, Margin = new Thickness(0, 0, 0, 6) };
        first.Click += (_, _) => { _s.SetsByFirst = first.IsChecked == true; _main.ApplySettings(); _main.RebuildView(); };
        SetsPanel.Children.Add(first); Reg(first, nameof(AppSettings.SetsByFirst));
        var copies = new CheckBox { Content = "Show a file once when the exact same file is in several folders (a +1 in its corner; right-click lists the other places)", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.HideCopies, Margin = new Thickness(0, 0, 0, 6) };
        copies.Click += (_, _) => { _s.HideCopies = copies.IsChecked == true; _main.ApplySettings(); _main.RebuildView(); };
        SetsPanel.Children.Add(copies); Reg(copies, nameof(AppSettings.HideCopies));
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
        GpuPanel.Children.Add(Row("Drawing", _drawBox)); Reg(_drawBox, nameof(AppSettings.Renderer));
        GpuPanel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = ThemeKit.Brush("Dim"),
            Text = "The GPU grid draws the thumbnails and plays the GIFs on the NVIDIA graphics card instead of the processor. Press F3 in the grid to compare it with Current."
        });
        GpuPanel.Children.Add(_gpuStatus);
        var cardOnly = new CheckBox { Content = "Keep decoded pictures only on the graphics card (frees the memory they took; F3 shows how much)", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.GpuCardOnly, Margin = new Thickness(0, 0, 0, 6) };
        cardOnly.Click += (_, _) => { _s.GpuCardOnly = cardOnly.IsChecked == true; _main.ApplySettings(); };
        GpuPanel.Children.Add(cardOnly); Reg(cardOnly, nameof(AppSettings.GpuCardOnly));
        var buttons = new WrapPanel();
        var again = new Button { Content = "Check again" };
        again.Click += (_, _) => RunGpuCheck();
        _prefBtn.Click += (_, _) =>
        {
            if (GpuCheck.SetPreference()) { _gpuStatus.Text = "Windows will now run Ginkgo on the high-performance GPU. Restart Ginkgo for it to take effect."; _restartBtn.Visibility = Visibility.Visible; _prefBtn.Visibility = Visibility.Collapsed; }
            else _gpuStatus.Text = "Couldn't save the Windows graphics preference. You can set it in Windows Settings > System > Display > Graphics.";
        };
        _restartBtn.Click += (_, _) => _main.Restart();
        buttons.Children.Add(again); buttons.Children.Add(_prefBtn); buttons.Children.Add(_restartBtn);
        GpuPanel.Children.Add(buttons);

        // GIFs (GPU grid only)
        _gifPanel.Children.Add(_gifOff);
        var animate = new CheckBox { Content = "Animate GIFs in the grid (off: still covers; they play on hover)", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.GifAnimate, Margin = new Thickness(0, 0, 0, 6) };
        animate.Click += (_, _) => { _s.GifAnimate = animate.IsChecked == true; _main.ApplySettings(); };
        _gifPanel.Children.Add(animate); Reg(animate, nameof(AppSettings.GifAnimate));

        var lenText = new TextBlock();
        var len = new Slider { Width = 160, Minimum = 1, Maximum = 15, IsSnapToTickEnabled = true, TickFrequency = 1, Value = _s.GifLengthSec, VerticalAlignment = VerticalAlignment.Center };
        lenText.Text = $"{_s.GifLengthSec:0} s";
        len.ValueChanged += (_, e) => { lenText.Text = $"{e.NewValue:0} s"; if (_init) return; _s.GifLengthSec = e.NewValue; _main.ApplySettings(); };
        _gifPanel.Children.Add(Row("GIF length in the grid (longer GIFs loop this much; full length on hover)", len, null, lenText)); Reg(len, nameof(AppSettings.GifLengthSec));

        var fadeText = new TextBlock();
        var fadeLen = new Slider { Width = 120, Minimum = 0.1, Maximum = 1.0, IsSnapToTickEnabled = true, TickFrequency = 0.1, Value = _s.GifCrossFadeSec, VerticalAlignment = VerticalAlignment.Center, IsEnabled = _s.GifCrossFade };
        fadeText.Text = $"{_s.GifCrossFadeSec:0.0} s";
        fadeLen.ValueChanged += (_, e) => { fadeText.Text = $"{e.NewValue:0.0} s"; if (_init) return; _s.GifCrossFadeSec = Math.Round(e.NewValue, 1); _main.ApplySettings(); };
        var fade = new CheckBox { Content = "Cross-fade at the loop (GIFs cut to the length above)", Foreground = ThemeKit.Brush("Text"), IsChecked = _s.GifCrossFade, VerticalAlignment = VerticalAlignment.Center };
        fade.Click += (_, _) => { _s.GifCrossFade = fade.IsChecked == true; fadeLen.IsEnabled = _s.GifCrossFade; _main.ApplySettings(); };
        var fadeRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        fadeText.Width = 52; fadeText.TextAlignment = TextAlignment.Right; fadeText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(fadeText, Dock.Right); fadeRow.Children.Add(fadeText);
        DockPanel.SetDock(fadeLen, Dock.Right); fadeRow.Children.Add(fadeLen);
        fadeRow.Children.Add(fade);
        _gifPanel.Children.Add(fadeRow); Reg(fade, nameof(AppSettings.GifCrossFade)); Reg(fadeLen, nameof(AppSettings.GifCrossFadeSec));

        var fps = new ComboBox { MinWidth = 120 };
        foreach (var (label, v) in new[] { ("Automatic", 0), ("8 fps", 8), ("10 fps", 10), ("15 fps", 15), ("20 fps", 20) })
        {
            var it = new ComboBoxItem { Content = label, Tag = v };
            fps.Items.Add(it);
            if (v == _s.GifFps) fps.SelectedItem = it;
        }
        if (fps.SelectedItem is null) fps.SelectedIndex = 0;
        fps.SelectionChanged += (_, _) => { if (_init) return; _s.GifFps = (int)((ComboBoxItem)fps.SelectedItem).Tag; _main.ApplySettings(); };
        _gifPanel.Children.Add(Row("GIF frame rate (Automatic lowers it when many GIFs are on screen)", fps)); Reg(fps, nameof(AppSettings.GifFps));
        _gifStat.Foreground = ThemeKit.Brush("Dim"); _gifStat.TextWrapping = TextWrapping.Wrap; _gifStat.Margin = new Thickness(0, 4, 0, 0);
        _gifPanel.Children.Add(_gifStat);

        // extra-large versions on the drive: their size, a limit, and a way to clear them
        var bigCap = new ComboBox { MinWidth = 120 };
        foreach (var (label, gb) in new[] { ("No limit", 0.0), ("1 GB", 1.0), ("2 GB", 2.0), ("5 GB", 5.0), ("10 GB", 10.0), ("20 GB", 20.0), ("50 GB", 50.0) })
        {
            var it = new ComboBoxItem { Content = label, Tag = gb };
            bigCap.Items.Add(it);
            if (Math.Abs(gb - _s.GifBigCapGB) < 0.01) bigCap.SelectedItem = it;
        }
        if (bigCap.SelectedItem is null) bigCap.SelectedIndex = 0;
        bigCap.SelectionChanged += (_, _) =>
        {
            if (_init) return;
            _s.GifBigCapGB = (double)((ComboBoxItem)bigCap.SelectedItem).Tag;
            if (_engine.GifMaker is { } gm) { gm.BigCapBytes = (long)(_s.GifBigCapGB * 1073741824.0); Task.Run(() => { try { gm.EnforceBigCap(); } catch { } }); }
            _main.ApplySettings();
        };
        _gifPanel.Children.Add(Row("Extra-large GIF versions on the drive (for big tiles): most they may take", bigCap));
        var clearBig = new Button { Content = "Delete the extra-large versions", HorizontalAlignment = HorizontalAlignment.Left };
        clearBig.Click += (_, _) =>
        {
            if (_engine.GifMaker is not { } gm) return;
            Task.Run(() => { try { gm.ClearBig(); } catch { } }).ContinueWith(_ => Dispatcher.BeginInvoke(GifStat));
        };
        _gifPanel.Children.Add(clearBig);
        _bigStat.Foreground = ThemeKit.Brush("Dim"); _bigStat.TextWrapping = TextWrapping.Wrap; _bigStat.Margin = new Thickness(0, 4, 0, 0);
        _gifPanel.Children.Add(_bigStat);
        GpuPanel.Children.Add(_gifPanel);
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => GifStat();
        Loaded += (_, _) => tick.Start(); Unloaded += (_, _) => tick.Stop();
        GifStat();

        if (_main.Gpu is null && !_main.GpuChecking) RunGpuCheck(); else ShowGpu();
    }

    private readonly TextBlock _gifStat = new();
    private readonly TextBlock _bigStat = new();

    private void GifStat()
    {
        var gm = _engine.GifMaker;
        if (gm is not null)
            _bigStat.Text = $"Extra-large versions: {gm.Stats.Big:N0} GIFs, {gm.BigBytes / 1073741824.0:0.00} GB on {_s.CacheDir}"
                          + (_s.GifBigCapGB > 0 ? $" (limit {_s.GifBigCapGB:0} GB: the oldest are deleted first, a few hundred MB at a time, and made again if needed)" : " (no limit: it keeps growing)") + ".";
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
        _gifOff.Text = gpuOn ? "GIFs play from their GPU versions as they're made (until then, the first frame shows). Hovering a GIF plays the original at full length." : "These apply only while the GPU grid is drawing.";
    }

    // ---- back to the default: each setting has its own reset, and each group a Reset for everything in it ----

    private static readonly AppSettings Defaults = new();
    private readonly List<(FrameworkElement control, System.Reflection.PropertyInfo prop)> _resettable = new();
    private readonly List<Action> _marks = new();

    private void Reg(FrameworkElement control, string prop)
    {
        var pi = typeof(AppSettings).GetProperty(prop);
        if (pi is not null) _resettable.Add((control, pi));
    }

    private bool AtDefault(System.Reflection.PropertyInfo pi)
    {
        object? now = pi.GetValue(_s), def = pi.GetValue(Defaults);
        if (now is double a && def is double b) return Math.Abs(a - b) < 1e-6;
        if (now is float fa && def is float fb) return Math.Abs(fa - fb) < 1e-4;
        return Equals(now, def);
    }

    /// <summary>Puts one setting back through its own control, so everything that control does on a change happens too.</summary>
    private bool Reset(FrameworkElement control, System.Reflection.PropertyInfo pi)
    {
        if (AtDefault(pi)) return false;
        object? def = pi.GetValue(Defaults);
        switch (control)
        {
            case Slider sl: sl.Value = Convert.ToDouble(def); break;
            case CheckBox cb:
                cb.IsChecked = def is true;
                cb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, cb));
                break;
            case ComboBox co:
                var match = co.Items.OfType<ComboBoxItem>().FirstOrDefault(it => it.Tag is not null && it.Tag.ToString() == def?.ToString());
                if (match is not null) co.SelectedItem = match; else if (def is int n && n >= 0 && n < co.Items.Count) co.SelectedIndex = n;
                break;
        }
        if (!AtDefault(pi)) pi.SetValue(_s, def);      // (if the control kept the setting from changing, set it directly)
        _main.ApplySettings();
        return true;
    }

    private void MarkAll() { foreach (var m in _marks) m(); }

    private void WireResets()
    {
        var groups = new Dictionary<TextBlock, List<(FrameworkElement, System.Reflection.PropertyInfo)>>();
        foreach (var (control, pi) in _resettable)
        {
            // the reset sits beside the control: the control is wrapped together with it, in the control's place
            var owner = LogicalTreeHelper.GetParent(control);
            if (owner is not Panel and not Decorator) continue;
            var wrap = new DockPanel { Margin = control.Margin, VerticalAlignment = control.VerticalAlignment, HorizontalAlignment = control.HorizontalAlignment };
            DockPanel.SetDock(wrap, DockPanel.GetDock(control));
            control.Margin = new Thickness(0);
            int at = -1;
            if (owner is Panel parent) { at = parent.Children.IndexOf(control); parent.Children.RemoveAt(at); }
            else ((Decorator)owner).Child = null;
            var b = new Button { Content = "\uE7A7", Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, FontSize = 11, Margin = new Thickness(6, 0, 0, 0),
                                 VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Back to the default ({Show(pi.GetValue(Defaults))})" };
            var c = control; var p = pi;
            b.Click += (_, _) => { Reset(c, p); MarkAll(); };
            void Mark() => b.Opacity = AtDefault(p) ? 0.3 : 1;
            _marks.Add(Mark);
            DockPanel.SetDock(b, Dock.Right);
            wrap.Children.Add(b);
            wrap.Children.Add(control);
            if (owner is Panel pp) pp.Children.Insert(at, wrap); else ((Decorator)owner).Child = wrap;
            switch (control)
            {
                case Slider sl: sl.ValueChanged += (_, _) => MarkAll(); break;
                case CheckBox cb: cb.Click += (_, _) => MarkAll(); break;
                case ComboBox co: co.SelectionChanged += (_, _) => MarkAll(); break;
            }
            if (GroupOf(wrap) is { } g) { if (!groups.TryGetValue(g, out var list)) groups[g] = list = new(); list.Add((control, pi)); }
        }
        // each group heading gets a Reset for the settings under it
        foreach (var (heading, list) in groups)
        {
            if (LogicalTreeHelper.GetParent(heading) is not Panel col) continue;
            int at = col.Children.IndexOf(heading);
            col.Children.RemoveAt(at);
            var head = new DockPanel { Margin = heading.Margin };
            heading.Margin = new Thickness(0);
            var reset = new Button { Content = "Reset", Height = 24, Padding = new Thickness(8, 0, 8, 0), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                                     ToolTip = $"Put every setting in {heading.Text} back to its default (the other groups stay as they are)" };
            reset.Click += (_, _) => { foreach (var (c, p) in list) Reset(c, p); MarkAll(); Result.Text = $"{heading.Text}: back to the defaults."; };
            DockPanel.SetDock(reset, Dock.Right);
            head.Children.Add(reset);
            head.Children.Add(heading);
            col.Children.Insert(at, head);
        }
        MarkAll();
    }

    /// <summary>The group heading above a setting: the nearest heading before the row it sits in.</summary>
    private TextBlock? GroupOf(DependencyObject d)
    {
        var style = (Style)FindResource("Group");
        for (var e = d; e is not null; e = LogicalTreeHelper.GetParent(e))
        {
            if (LogicalTreeHelper.GetParent(e) is not Panel col) continue;
            int at = col.Children.IndexOf((UIElement)e);
            for (int i = at - 1; i >= 0; i--)
                if (col.Children[i] is TextBlock { Style: var st } tb && st == style) return tb;
        }
        return null;
    }

    private static string Show(object? v) => v switch { bool b => b ? "on" : "off", double x => x.ToString("0.##"), float f => f.ToString("0.##"), null => "none", _ => v.ToString() ?? "" };
}
