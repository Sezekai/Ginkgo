using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ginkgo.Core;
using Ginkgo.Data;

namespace Ginkgo.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly Engine _engine;
    private LibraryModel? _model;
    private ThumbLoader? _loader;
    private int[] _order = Array.Empty<int>();       // model rows shown, in order
    private string[] _paths = Array.Empty<string>();
    private bool _syncing, _loading = true;
    private int _buildVersion;
    private int _randomSeed = Environment.TickCount;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _frames; private long _lastTick = Stopwatch.GetTimestamp(); private double _fps;
    private string _bannerKind = "";
    private long _lastGc = Stopwatch.GetTimestamp();

    public MainWindow()
    {
        InitializeComponent();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        _engine = new Engine(_settings);

        // restore what the person last chose
        NsfwBtn.IsChecked = _settings.ShowNsfw; VeryBtn.IsChecked = _settings.ShowVery;
        ImagesBtn.IsChecked = _settings.ShowImages; GifsBtn.IsChecked = _settings.ShowGifs;
        SizeSlider.Value = _settings.TileSize; View.TileTarget = _settings.TileSize;
        foreach (ComboBoxItem it in SortBox.Items) if ((string)it.Tag == _settings.Sort) SortBox.SelectedItem = it;
        if (SortBox.SelectedItem is null) SortBox.SelectedIndex = 0;
        ShuffleBtn.Visibility = _settings.Sort == "Random" ? Visibility.Visible : Visibility.Collapsed;
        View.LookAheadWindows = _settings.LookAheadWindows;
        _loading = false;

        View.ScrollChanged += SyncBar;
        View.ScrollChanged += () => Interlocked.Exchange(ref _engine.Pipe.LastUiActivity, Stopwatch.GetTimestamp());
        View.HoverChanged += i => StatusText.Text = i >= 0 && i < _paths.Length ? HoverText(i) : StatusLine();
        View.ItemActivated += OpenViewer;
        CompositionTarget.Rendering += (_, _) => _frames++;
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); BuildView(false); };

        _engine.Opened += () => Dispatcher.InvokeAsync(async () => await ReloadModel(false));
        _engine.DataChanged += () => Dispatcher.BeginInvoke(UpdateChangeIndicator);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        Loaded += (_, _) => _engine.Start();
        Closing += (_, _) =>
        {
            SaveSettings();
            _loader?.Dispose();
            _engine.Dispose();
        };
    }

    // =========================================================================================================
    // loading the library into the view
    // =========================================================================================================

    private async Task ReloadModel(bool keepPosition)
    {
        if (_engine.Db is null) return;
        var db = _engine.Db;
        _engine.SaveLargeBatch();
        StatusText.Text = "loading the library...";
        var sw = Stopwatch.StartNew();
        var m = await Task.Run(() => LibraryModel.Load(db));
        _model = m;
        _engine.ViewRefreshed();
        UpdateChangeIndicator();
        await BuildViewAsync(keepPosition);
        StatusText.Text = $"library loaded in {sw.Elapsed.TotalSeconds:F1}s";
    }

    private ViewQuery CurrentQuery() => new()
    {
        Nsfw = NsfwBtn.IsChecked == true, Very = VeryBtn.IsChecked == true,
        Images = ImagesBtn.IsChecked == true, Gifs = GifsBtn.IsChecked == true,
        OffFolders = new HashSet<string>(_settings.OffFolders, StringComparer.OrdinalIgnoreCase),
        Search = SearchBox.Text, Sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen", RandomSeed = _randomSeed,
    };

    private async void BuildView(bool keepPosition) => await BuildViewAsync(keepPosition);

    private async Task BuildViewAsync(bool keepPosition)
    {
        var model = _model; var stores = _engine.Stores;
        if (model is null || stores is null) return;
        int version = ++_buildVersion;
        var q = CurrentQuery();
        var result = await Task.Run(() =>
        {
            var order = model.Select(q);
            return (order, slice: model.Slice(order, stores));
        });
        if (version != _buildVersion) return;                     // something newer was asked for meanwhile
        _order = result.order;
        _paths = result.slice.paths;
        var ids = result.slice.ids;
        DetachLoader();
        var (decoded, encoded) = CacheBudgets();
        _loader = new ThumbLoader(result.slice.tiers, _paths, result.slice.origW, stores.Large, Engine.NoCap,
            (i, loc) => _engine.QueueLarge(ids[i], loc.Pack, loc.Off, loc.Len), Dispatcher, () => View.Refresh(), decoded, encoded)
        { FadeEnabled = _settings.FadeSharpening };
        View.PrefetchBudgetBytes = decoded * 36 / 100;
        View.Badges = result.slice.badges;
        View.Thumbs = _loader;
        View.SetItems(result.slice.specs, null, keepPosition);
        EmptyNote.Visibility = _order.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_order.Length == 0)
            EmptyNote.Text = model.Count == 0
                ? "Nothing to show yet.\nGinkgo is looking through your library and making thumbnails. Pictures appear here as they become ready."
                : "No files match the current filters.";
        CountText.Text = $"{_order.Length:N0} shown of {model.Count:N0}";
    }

    private void DetachLoader() { View.Thumbs = null; _loader?.Dispose(); _loader = null; }

    private (long decoded, long encoded) CacheBudgets()
    {
        var p = AppSettings.CacheProfiles[Math.Clamp(_settings.CacheProfile, 0, AppSettings.CacheProfiles.Length - 1)];
        return (p.decoded * 1024L * 1024, p.encoded * 1024L * 1024);
    }

    // =========================================================================================================
    // toolbar
    // =========================================================================================================

    private void OnFilterChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowNsfw = NsfwBtn.IsChecked == true; _settings.ShowVery = VeryBtn.IsChecked == true;
        _settings.ShowImages = ImagesBtn.IsChecked == true; _settings.ShowGifs = GifsBtn.IsChecked == true;
        BuildView(false);
    }

    private void OnSearchChanged(object s, TextChangedEventArgs e) { if (_loading) return; _searchDelay.Stop(); _searchDelay.Start(); }

    private void OnSortChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _settings.Sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
        ShuffleBtn.Visibility = _settings.Sort == "Random" ? Visibility.Visible : Visibility.Collapsed;
        BuildView(false);
    }

    private void OnReshuffle(object s, RoutedEventArgs e) { _randomSeed = Environment.TickCount; BuildView(false); }

    private void OnSizeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        _settings.TileSize = (float)e.NewValue;
        View.TileTarget = (float)e.NewValue;
    }

    private void OnFolders(object s, RoutedEventArgs e)
    {
        if (_model is null) return;
        var win = new SelectFoldersWindow(_model.FolderCounts(CurrentQuery()), _settings.OffFolders) { Owner = this };
        if (win.ShowDialog() == true)
        {
            _settings.OffFolders = win.OffFolders;
            FoldersBtn.Content = _settings.OffFolders.Count == 0 ? "Select folders" : $"Select folders ({_settings.OffFolders.Count} off)";
            BuildView(false);
        }
    }

    private void OnSettings(object s, RoutedEventArgs e) => new SettingsWindow(this, _settings, _engine) { Owner = this }.ShowDialog();

    private void OnUpdateView(object s, RoutedEventArgs e) => _ = ReloadModel(true);

    private void SaveSettings() { _settings.Save(); }

    // called by the settings window
    public void ApplySettings()
    {
        View.LookAheadWindows = _settings.LookAheadWindows;
        var (d, c) = CacheBudgets();
        _loader?.SetBudgets(d, c);
        View.PrefetchBudgetBytes = d * 36 / 100;
        if (_loader is not null) _loader.FadeEnabled = _settings.FadeSharpening;
        SaveSettings();
    }

    /// <summary>Gives up the picture and thumbnail files for a moment (the clean-up moves them), then rebuilds the view.</summary>
    public async Task WithViewPaused(Func<Task> work)
    {
        DetachLoader();
        try { await work(); }
        finally { await ReloadModel(true); }
    }

    // =========================================================================================================
    // status, banners, scroll bar
    // =========================================================================================================

    private string HoverText(int i)
    {
        int row = _order[i];
        var m = _model!;
        return $"{m.Path[row]}   {m.W[row]} x {m.H[row]}   {m.Size[row] / 1048576.0:F1} MB";
    }

    private string StatusLine()
    {
        var p = _engine.Pipe;
        string pipe = p.Running || p.Total > 0 && p.Done < p.Total
            ? $"  |  thumbnails {p.Done:N0} of {p.Total:N0}{(p.Failed > 0 ? $" ({p.Failed:N0} could not be read)" : "")}"
            : "";
        string back = p.BackfillTotal > 0 && p.BackfillDone < p.BackfillTotal ? $"  |  small sizes {p.BackfillDone:N0} of {p.BackfillTotal:N0}" : "";
        string sharp = p.SharpTotal > 0 && p.SharpDone < p.SharpTotal ? $"  |  sharp versions {p.SharpDone:N0} of {p.SharpTotal:N0}" : "";
        return _engine.Status + pipe + back + sharp;
    }

    private void UpdateChangeIndicator()
    {
        int n = _engine.ChangesSinceView;
        UpdateBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.Content = n > 0 ? $"Update view ({n:N0} changes)" : "Update view";
    }

    private void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        _fps = _frames / ((now - _lastTick) / (double)Stopwatch.Frequency); _frames = 0; _lastTick = now;
        if (View.HoverIndex < 0) StatusText.Text = StatusLine();
        UpdateChangeIndicator();
        UpdateBanner();
        IdleCleanup();
    }

    /// <summary>Picture memory freed by the app only goes back to Windows when .NET cleans up, so do that when scrolling has stopped.</summary>
    private void IdleCleanup()
    {
        long now = Stopwatch.GetTimestamp();
        double idle = (now - Volatile.Read(ref _engine.Pipe.LastUiActivity)) / (double)Stopwatch.Frequency;
        double since = (now - _lastGc) / (double)Stopwatch.Frequency;
        if (idle > 2.0 && since > 10 && Process.GetCurrentProcess().WorkingSet64 > 600L * 1024 * 1024)
        {
            _lastGc = now;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }

    private void UpdateBanner()
    {
        string kind = _engine.SafetyStop is not null ? "safety" : _engine.LibraryOffline ? "offline" : "";
        if (kind == _bannerKind) return;
        _bannerKind = kind;
        BannerButtons.Children.Clear();
        if (kind == "") { Banner.Visibility = Visibility.Collapsed; return; }
        Banner.Visibility = Visibility.Visible;
        if (kind == "safety")
        {
            var r = _engine.SafetyStop!;
            BannerText.Text = $"{r.WouldHide:N0} files suddenly can't be found, which is a large part of your library. Nothing was hidden. " +
                              "If you did remove them, you can hide them now (they are still kept for 30 days).";
            var hide = new Button { Content = "Hide them" }; hide.Click += async (_, _) => { _bannerKind = ""; await _engine.ConfirmLargeRemoval(); };
            var keep = new Button { Content = "Leave them" }; keep.Click += (_, _) => { _engine.DismissSafetyStop(); };
            BannerButtons.Children.Add(hide); BannerButtons.Children.Add(keep);
        }
        else
        {
            BannerText.Text = $"The library folder {_settings.LibraryRoot} can't be found (is the drive connected?). Showing what Ginkgo already knows.";
            var again = new Button { Content = "Try again" }; again.Click += async (_, _) => { _bannerKind = ""; await _engine.Rescan(); };
            var choose = new Button { Content = "Choose folder..." };
            choose.Click += (_, _) => { new SettingsWindow(this, _settings, _engine) { Owner = this }.ShowDialog(); };
            BannerButtons.Children.Add(again); BannerButtons.Children.Add(choose);
        }
    }

    private void OnBarChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _syncing = true; View.Offset = e.NewValue; _syncing = false;
    }

    private void SyncBar()
    {
        if (_syncing) return;
        _syncing = true;
        double max = Math.Max(0, View.TotalHeight - View.ActualHeight);
        Bar.Maximum = max; Bar.ViewportSize = View.ActualHeight;
        Bar.LargeChange = View.ActualHeight * 0.9; Bar.SmallChange = 60;
        Bar.Value = View.Offset;
        _syncing = false;
    }

    // =========================================================================================================
    // keyboard
    // =========================================================================================================

    private void OnKey(object s, KeyEventArgs e)
    {
        if (ViewerRoot.Visibility == Visibility.Visible) { ViewerKey(e); return; }
        bool typing = Keyboard.FocusedElement is TextBox;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            if (typing) { SearchBox.Text = ""; View.Focus(); e.Handled = true; }
            return;
        }
        if (ctrl && e.Key == Key.F) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (typing) return;
        if (Keyboard.Modifiers != ModifierKeys.None && !ctrl) return;
        switch (e.Key)
        {
            case Key.Home: View.Offset = 0; e.Handled = true; break;
            case Key.End: View.Offset = double.MaxValue / 4; e.Handled = true; break;
            case Key.PageDown: View.ScrollBy(View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.PageUp: View.ScrollBy(-View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.Enter: if (View.HoverIndex >= 0) { OpenViewer(View.HoverIndex); e.Handled = true; } break;
            case Key.OemQuestion: SearchBox.Focus(); e.Handled = true; break;
            case Key.N: NsfwBtn.IsChecked = NsfwBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.V: VeryBtn.IsChecked = VeryBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.R: if (_settings.Sort == "Random") { OnReshuffle(this, e); e.Handled = true; } break;
            case Key.Add: case Key.OemPlus: SizeSlider.Value = Math.Max(SizeSlider.Minimum, SizeSlider.Value / 1.15); e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: SizeSlider.Value = Math.Min(SizeSlider.Maximum, SizeSlider.Value * 1.15); e.Handled = true; break;
        }
    }

    // =========================================================================================================
    // viewer
    // =========================================================================================================

    private int _vIndex;
    private int _vToken;
    private double _zoom = 1, _panX, _panY;
    private bool _vFull, _dragging, _fullscreen, _uiHidden;
    private Point _dragStart; private double _dragPanX, _dragPanY;
    private WindowState _prevState; private WindowStyle _prevStyle;
    private readonly Dictionary<string, BitmapSource> _vCache = new();
    private readonly Queue<string> _vCacheOrder = new();

    private void OpenViewer(int index)
    {
        if (index < 0 || index >= _paths.Length) return;
        ViewerRoot.Visibility = Visibility.Visible;
        ViewerRoot.Focus();
        ShowIndex(index);
    }

    private void CloseViewer()
    {
        _vToken++;
        ViewerRoot.Visibility = Visibility.Collapsed;
        Pic.Source = null;
        if (_fullscreen) ToggleFullscreen();
        View.ScrollToIndex(_vIndex);
        View.Focus();
    }

    private void OnViewerClose(object s, RoutedEventArgs e) => CloseViewer();

    private void ShowIndex(int index)
    {
        _vIndex = Math.Clamp(index, 0, _paths.Length - 1);
        ResetZoom();
        int row = _order[_vIndex];
        var m = _model!;
        ViewerTitle.Text = $"{_vIndex + 1:N0} of {_paths.Length:N0}    {System.IO.Path.GetFileName(m.Path[row])}    {m.W[row]} x {m.H[row]}";
        InfoText.Text = string.Join("\n",
            "Path    " + m.Path[row],
            $"Size    {m.Size[row] / 1048576.0:F2} MB ({m.Size[row]:N0} bytes)",
            $"Pixels  {m.W[row]} x {m.H[row]}  ({m.W[row] * (long)m.H[row] / 1e6:F1} MP)",
            "Type    " + (m.Kind[row] == (byte)MediaKind.Gif ? "GIF" : "Image"),
            "Level   " + (ContentLevel)m.Level[row],
            "Changed " + DateTimeOffset.FromUnixTimeSeconds(m.Mtime[row]).ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
            "Seen    " + DateTimeOffset.FromUnixTimeSeconds(m.FirstSeen[row]).ToLocalTime().ToString("g", CultureInfo.CurrentCulture));
        _ = LoadPicture(_vIndex, full: false);
    }

    private (int w, int h) FitPixels(int row)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double hw = Math.Max(200, PicHost.ActualWidth * dpi), hh = Math.Max(200, PicHost.ActualHeight * dpi);
        return ((int)hw, (int)hh);
    }

    private static BitmapSource? Decode(string path, int decodeW, int decodeH)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeW > 0) bi.DecodePixelWidth = decodeW; else if (decodeH > 0) bi.DecodePixelHeight = decodeH;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private async Task LoadPicture(int index, bool full)
    {
        int token = ++_vToken;
        int row = _order[index];
        var m = _model!;
        string path = m.Path[row];
        int w = m.W[row], h = m.H[row];
        var (hw, hh) = FitPixels(row);
        int decodeW = 0, decodeH = 0;
        if (!full)
        {
            if ((double)w / Math.Max(1, h) >= (double)hw / hh) decodeW = Math.Min(w, hw); else decodeH = Math.Min(h, hh);
            if (decodeW >= w || decodeH >= h) { decodeW = 0; decodeH = 0; }
        }
        string key = path + "|" + (full ? "full" : decodeW + "x" + decodeH);
        BitmapSource? bmp;
        lock (_vCache) _vCache.TryGetValue(key, out bmp);
        if (bmp is null)
        {
            ViewerNote.Text = "";
            bmp = await Task.Run(() => Decode(path, decodeW, decodeH));
            if (bmp is not null && !full) lock (_vCache) CacheAdd(key, bmp);
        }
        if (token != _vToken) return;                                 // moved on to another picture meanwhile
        _vFull = full;
        if (bmp is null) { Pic.Source = null; ViewerNote.Text = "This file can't be shown."; return; }
        Pic.Source = bmp;
        ViewerNote.Text = "";
        if (!full)
        {
            if (m.Kind[row] == (byte)MediaKind.Gif) ViewerNote.Text = "";
            _ = Preload(index + 1); _ = Preload(index - 1);
        }
    }

    private void CacheAdd(string key, BitmapSource bmp)
    {
        if (_vCache.ContainsKey(key)) return;
        _vCache[key] = bmp; _vCacheOrder.Enqueue(key);
        while (_vCacheOrder.Count > 4) _vCache.Remove(_vCacheOrder.Dequeue());
    }

    private async Task Preload(int index)
    {
        if (index < 0 || index >= _paths.Length) return;
        int row = _order[index];
        var m = _model!;
        var (hw, hh) = FitPixels(row);
        int w = m.W[row], h = m.H[row], decodeW = 0, decodeH = 0;
        if ((double)w / Math.Max(1, h) >= (double)hw / hh) decodeW = Math.Min(w, hw); else decodeH = Math.Min(h, hh);
        if (decodeW >= w || decodeH >= h) { decodeW = 0; decodeH = 0; }
        string path = m.Path[row], key = path + "|" + decodeW + "x" + decodeH;
        lock (_vCache) if (_vCache.ContainsKey(key)) return;
        var bmp = await Task.Run(() => Decode(path, decodeW, decodeH));
        if (bmp is not null) lock (_vCache) CacheAdd(key, bmp);
    }

    private void ResetZoom() { _zoom = 1; _panX = _panY = 0; ApplyTransform(); }

    private void ApplyTransform()
    {
        PicScale.ScaleX = PicScale.ScaleY = _zoom;
        PicMove.X = _panX; PicMove.Y = _panY;
    }

    private void ZoomAt(Point p, double newZoom)
    {
        newZoom = Math.Clamp(newZoom, 0.1, 40);
        double cx = PicHost.ActualWidth / 2, cy = PicHost.ActualHeight / 2;
        double qx = (p.X - cx - _panX) / _zoom, qy = (p.Y - cy - _panY) / _zoom;
        _zoom = newZoom;
        _panX = p.X - cx - qx * _zoom; _panY = p.Y - cy - qy * _zoom;
        if (_zoom <= 1.001) { _panX = _panY = 0; }
        ApplyTransform();
        if (_zoom > 1.05 && !_vFull) _ = LoadPicture(_vIndex, full: true);       // zooming in: get the real pixels
    }

    private void OnViewerWheel(object s, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(PicHost), _zoom * Math.Pow(1.18, e.Delta / 120.0));
        e.Handled = true;
    }

    private void OnViewerDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { CloseViewer(); e.Handled = true; return; }
        _dragging = true; _dragStart = e.GetPosition(PicHost); _dragPanX = _panX; _dragPanY = _panY;
        PicHost.CaptureMouse();
    }

    private void OnViewerMove(object s, MouseEventArgs e)
    {
        if (!_dragging || _zoom <= 1.001) return;
        var p = e.GetPosition(PicHost);
        _panX = _dragPanX + (p.X - _dragStart.X); _panY = _dragPanY + (p.Y - _dragStart.Y);
        ApplyTransform();
    }

    private void OnViewerUp(object s, MouseButtonEventArgs e) { _dragging = false; PicHost.ReleaseMouseCapture(); }

    private void ViewerKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: CloseViewer(); break;
            case Key.Right: case Key.Down: case Key.PageDown: case Key.Space: if (_vIndex + 1 < _paths.Length) ShowIndex(_vIndex + 1); break;
            case Key.Left: case Key.Up: case Key.PageUp: if (_vIndex > 0) ShowIndex(_vIndex - 1); break;
            case Key.Home: ShowIndex(0); break;
            case Key.End: ShowIndex(_paths.Length - 1); break;
            case Key.F: case Key.F11: ToggleFullscreen(); break;
            case Key.H: _uiHidden = !_uiHidden; ViewerBar.Visibility = ViewerHint.Visibility = _uiHidden ? Visibility.Collapsed : Visibility.Visible; if (_uiHidden) InfoPanel.Visibility = Visibility.Collapsed; break;
            case Key.I: InfoPanel.Visibility = InfoPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; break;
            case Key.D0: case Key.NumPad0: ResetZoom(); break;
            case Key.D1: case Key.NumPad1:
                {
                    int row = _order[_vIndex];
                    double f = Math.Min(PicHost.ActualWidth / Math.Max(1, _model!.W[row]), PicHost.ActualHeight / Math.Max(1, _model.H[row]));
                    double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
                    ZoomAt(new Point(PicHost.ActualWidth / 2, PicHost.ActualHeight / 2), 1.0 / (f * dpi));
                    break;
                }
            default: return;
        }
        e.Handled = true;
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen) { _prevState = WindowState; _prevStyle = WindowStyle; WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized; _fullscreen = true; }
        else { WindowStyle = _prevStyle; WindowState = _prevState; _fullscreen = false; }
    }
}
