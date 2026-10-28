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
    private bool _syncing, _loading = true, _syncingSize;
    private int _buildVersion;
    private int _randomSeed = Environment.TickCount;
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _folderDelay = new() { Interval = TimeSpan.FromMilliseconds(140) };
    private readonly DispatcherTimer _barTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _frames; private long _lastTick = Stopwatch.GetTimestamp(); private double _fps;
    private string _bannerKind = "";
    private long _lastGc = Stopwatch.GetTimestamp();

    // folder pop-out
    private readonly System.Windows.Controls.Primitives.Popup _folderPopup;
    private readonly FolderPanel _folders;
    private long _popupClosedAt;

    // top bar
    private bool _barManual, _barAuto, _barShown = true;
    private double _barAccum, _lastOffset;
    private long _barShownAt, _ignoreOverUntil;

    // side menu
    private bool _sideOpen;
    private bool _diag;
    private long _lastFrame; private double _worstGap;

    private const float TileMin = 60, TileMax = 1800;
    private static double SizeToSlider(float size) => Math.Log(Math.Clamp(size, TileMin, TileMax) / TileMin) / Math.Log(TileMax / TileMin);
    private static float SliderToSize(double t) => (float)(TileMin * Math.Pow(TileMax / TileMin, Math.Clamp(t, 0, 1)));

    public MainWindow()
    {
        InitializeComponent();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        _engine = new Engine(_settings);

        // restore what the person last chose (NSFW and Very always start off)
        GeneralBtn.IsChecked = _settings.ShowGeneral; NsfwBtn.IsChecked = false; VeryBtn.IsChecked = false;
        ImagesBtn.IsChecked = _settings.ShowImages; GifsBtn.IsChecked = _settings.ShowGifs;
        RowsBtn.IsChecked = _settings.RowsLayout; View.RowsMode = _settings.RowsLayout;
        SizeSlider.Value = SizeToSlider(_settings.TileSize); View.TileTarget = _settings.TileSize;
        View.TileTargetChanged += size =>
        {
            _settings.TileSize = size;
            _syncingSize = true; SizeSlider.Value = SizeToSlider(size); _syncingSize = false;
        };
        foreach (ComboBoxItem it in SortBox.Items) if ((string)it.Tag == _settings.Sort) SortBox.SelectedItem = it;
        if (SortBox.SelectedItem is null) SortBox.SelectedIndex = 0;
        ShuffleBtn.Visibility = _settings.Sort == "Random" ? Visibility.Visible : Visibility.Collapsed;
        View.LookAheadWindows = _settings.LookAheadWindows;
        _barManual = _settings.BarHidden;
        UpdateFoldersLabel();

        _folders = new FolderPanel(_settings.FolderSort, _settings.FolderSortReverse);
        _folders.Changed += off =>
        {
            _settings.OffFolders = off.ToList();
            UpdateFoldersLabel();
            _folderDelay.Stop(); _folderDelay.Start();
        };
        _folders.SortChanged += (key, rev) => { _settings.FolderSort = key; _settings.FolderSortReverse = rev; };
        _folders.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 };
        _folderPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = FoldersBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 2,
            Child = new Border { Margin = new Thickness(6, 0, 20, 20), Child = _folders }
        };
        _folderPopup.Closed += (_, _) => { _popupClosedAt = Stopwatch.GetTimestamp(); ApplyBar(); };
        _loading = false;

        View.ScrollChanged += SyncBar;
        View.ScrollChanged += BarScroll;
        View.ScrollChanged += () => Interlocked.Exchange(ref _engine.Pipe.LastUiActivity, Stopwatch.GetTimestamp());
        View.HoverChanged += i => StatusText.Text = i >= 0 && i < _paths.Length ? HoverText(i) : StatusLine();
        View.ItemActivated += OpenViewer;
        CompositionTarget.Rendering += (_, _) =>
        {
            _frames++;
            long t = Stopwatch.GetTimestamp();
            if (_lastFrame != 0) _worstGap = Math.Max(_worstGap, (t - _lastFrame) * 1000.0 / Stopwatch.Frequency);
            _lastFrame = t;
        };
        _searchDelay.Tick += (_, _) => { _searchDelay.Stop(); BuildView(false); };
        _folderDelay.Tick += (_, _) => { _folderDelay.Stop(); BuildView(false); };

        _engine.Opened += () => Dispatcher.InvokeAsync(async () => await ReloadModel(false));
        _engine.DataChanged += () => Dispatcher.BeginInvoke(UpdateChangeIndicator);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        timer.Tick += (_, _) => Tick();
        timer.Start();
        _barTimer.Tick += (_, _) => ApplyBar();
        _barTimer.Start();
        BarHost.SizeChanged += (_, _) => UpdateInset();
        SearchBox.GotKeyboardFocus += (_, _) => ApplyBar();
        SearchBox.LostKeyboardFocus += (_, _) => ApplyBar();
        TopBar.MouseLeave += (_, _) => _ignoreOverUntil = 0;
        Loaded += (_, _) => { _engine.Start(); UpdateInset(); ApplyBar(); };
        Closing += (_, _) =>
        {
            _settings.BarHidden = _barManual;
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
        MigrateFolderRules(m);
        _engine.ViewRefreshed();
        UpdateChangeIndicator();
        await BuildViewAsync(keepPosition);
        StatusText.Text = $"library loaded in {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>Old settings hid a folder together with everything inside it; each folder now stands alone, so spell the old rule out.</summary>
    private void MigrateFolderRules(LibraryModel m)
    {
        if (_settings.FolderRulesVersion >= 2) return;
        var all = new HashSet<string>(m.Folder.Take(m.Count), StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all)
            foreach (var e in _settings.OffFolders)
                if (f.Equals(e, StringComparison.OrdinalIgnoreCase) || f.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase) || f.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase)) { off.Add(f); break; }
        _settings.OffFolders = off.ToList();
        _settings.FolderRulesVersion = 2;
        UpdateFoldersLabel();
    }

    private ViewQuery CurrentQuery() => new()
    {
        General = GeneralBtn.IsChecked == true, Nsfw = NsfwBtn.IsChecked == true, Very = VeryBtn.IsChecked == true,
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
        _settings.ShowGeneral = GeneralBtn.IsChecked == true;
        _settings.ShowImages = ImagesBtn.IsChecked == true; _settings.ShowGifs = GifsBtn.IsChecked == true;
        BuildView(false);
    }

    private void OnLayoutChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.RowsLayout = RowsBtn.IsChecked == true;
        View.RowsMode = _settings.RowsLayout;
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
        if (_loading || _syncingSize) return;
        View.TileTarget = SliderToSize(e.NewValue);
    }

    private void OnFolders(object s, RoutedEventArgs e)
    {
        if (_model is null) return;
        if (_folderPopup.IsOpen) { _folderPopup.IsOpen = false; return; }
        // clicking the button while the pop-out is open closes it (the pop-out closes itself on that same click)
        if ((Stopwatch.GetTimestamp() - _popupClosedAt) / (double)Stopwatch.Frequency < 0.25) return;
        _folders.Width = Math.Min(WindowState == WindowState.Maximized ? 960 : 640, Math.Max(360, ActualWidth - 40));
        _folders.Load(_model.FolderStats(CurrentQuery()), _settings.OffFolders);
        _folderPopup.IsOpen = true;
    }

    private void UpdateFoldersLabel()
    {
        int n = _settings.OffFolders.Count;
        FoldersBtn.Content = n == 0 ? "Select folders" : $"Select folders ({n:N0} off)";
    }

    private void OnSettings(object s, RoutedEventArgs e) => OpenSettings();
    private void OpenSettings() => new SettingsWindow(this, _settings, _engine) { Owner = this }.ShowDialog();

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
        if (_engine.IdsRunning && _engine.IdsTotal > 0)
            return $"Reading file IDs from the drive (this is the disk noise, it only happens once): {_engine.IdsDone:N0} of {_engine.IdsTotal:N0}";
        string pipe = p.Running || p.Total > 0 && p.Done < p.Total
            ? $"  |  thumbnails {p.Done:N0} of {p.Total:N0}{(p.Failed > 0 ? $" ({p.Failed:N0} could not be read)" : "")}"
            : "";
        string back = p.BackfillTotal > 0 && p.BackfillDone < p.BackfillTotal ? $"  |  small sizes {p.BackfillDone:N0} of {p.BackfillTotal:N0}" : "";
        string sharp = p.SharpTotal > 0 && p.SharpDone < p.SharpTotal ? $"  |  sharp versions {p.SharpDone:N0} of {p.SharpTotal:N0}" : "";
        bool busy = pipe.Length > 0 || back.Length > 0 || sharp.Length > 0 || p.Running || _engine.IdsRunning;
        string idle = !busy && _engine.IdsFinished ? "  |  file IDs done, nothing running in the background" : "";
        return _engine.Status + pipe + back + sharp + idle;
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
        if (_diag)
        {
            var (mr, mp) = View.TakeWorst();
            DiagText.Text = $"{_fps:F0} fps | worst frame gap {_worstGap:F0} ms | draw max {mr:F1} ms | plan max {mp:F1} ms | {View.LastVisibleCount} tiles | cached {_loader?.CachedCount ?? 0} | pending {_loader?.PendingCount ?? 0} | GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}";
            _worstGap = 0;
        }
        bool ids = _engine.IdsRunning && _engine.IdsTotal > 0;
        IdBar.Visibility = ids ? Visibility.Visible : Visibility.Collapsed;
        if (ids) IdBar.Value = _engine.IdsDone / (double)_engine.IdsTotal;
        UpdateChangeIndicator();
        UpdateBanner();
        if (ViewerRoot.Visibility == Visibility.Visible) Interlocked.Exchange(ref _engine.Pipe.LastUiActivity, Stopwatch.GetTimestamp());
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
            choose.Click += (_, _) => OpenSettings();
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
        Scroll.Maximum = max; Scroll.ViewportSize = View.ActualHeight;
        Scroll.LargeChange = View.ActualHeight * 0.9; Scroll.SmallChange = 60;
        Scroll.Value = View.Offset;
        _syncing = false;
    }

    // =========================================================================================================
    // keyboard
    // =========================================================================================================

    private void OnKey(object s, KeyEventArgs e)
    {
        if (KeysLayer.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Escape or Key.K or Key.OemQuestion) { KeysLayer.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        if (ViewerRoot.Visibility == Visibility.Visible) { ViewerKey(e); return; }
        bool typing = Keyboard.FocusedElement is TextBox;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            if (_sideOpen) { SetSidebar(false); e.Handled = true; }
            else if (typing) { SearchBox.Text = ""; View.Focus(); e.Handled = true; }
            return;
        }
        if (ctrl && e.Key == Key.B) { SetSidebar(!_sideOpen); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { RevealBar(); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
        {
            Navigate(e.Key switch { Key.D1 => "Home", Key.D2 => "Browse", Key.D3 => "Favourites", _ => "Tools" });
            e.Handled = true; return;
        }
        if (typing) return;
        if (Keyboard.Modifiers != ModifierKeys.None && !ctrl) return;
        switch (e.Key)
        {
            case Key.Home: View.Offset = 0; e.Handled = true; break;
            case Key.End: View.Offset = double.MaxValue / 4; e.Handled = true; break;
            case Key.PageDown: View.ScrollBy(View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.PageUp: View.ScrollBy(-View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.Enter: if (View.HoverIndex >= 0) { OpenViewer(View.HoverIndex); e.Handled = true; } break;
            case Key.OemQuestion: RevealBar(); SearchBox.Focus(); e.Handled = true; break;
            case Key.K: ShowKeys(false); e.Handled = true; break;
            case Key.F3: _diag = !_diag; DiagText.Visibility = _diag ? Visibility.Visible : Visibility.Collapsed; e.Handled = true; break;
            case Key.M: SetSidebar(!_sideOpen); e.Handled = true; break;
            case Key.H: if (_barManual) OnShowBar(this, e); else OnHideBar(this, e); e.Handled = true; break;
            case Key.G: GeneralBtn.IsChecked = GeneralBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.N: NsfwBtn.IsChecked = NsfwBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.V: VeryBtn.IsChecked = VeryBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.L: RowsBtn.IsChecked = RowsBtn.IsChecked != true; OnLayoutChanged(this, e); e.Handled = true; break;
            case Key.R: if (_settings.Sort == "Random") { OnReshuffle(this, e); e.Handled = true; } break;
            case Key.Add: case Key.OemPlus: View.TileTarget *= 1.15f; e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: View.TileTarget /= 1.15f; e.Handled = true; break;
        }
    }

    // =========================================================================================================
    // window bar, side menu, shortcut list
    // =========================================================================================================

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private Point _titleDown; private bool _titleArmed;

    /// <summary>The bar is also the title bar: drag it from anywhere that isn't a control to move the window (also out of maximised); double-click it to maximise.</summary>
    private void OnTitleDown(object s, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && d != TopBar; d = ParentOf(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider or ComboBox or System.Windows.Controls.Primitives.Thumb) return;
        if (e.ClickCount == 2) { OnMaximize(this, e); e.Handled = true; return; }
        _titleArmed = true; _titleDown = e.GetPosition(this);
        TopBar.CaptureMouse();
        e.Handled = true;
    }

    private void OnTitleMove(object s, MouseEventArgs e)
    {
        if (!_titleArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { OnTitleUp(s, null!); return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _titleDown.X) < 4 && Math.Abs(p.Y - _titleDown.Y) < 4) return;
        _titleArmed = false;
        TopBar.ReleaseMouseCapture();
        if (WindowState == WindowState.Maximized)
        {
            // come out of maximised with the window under the mouse, like other apps
            double frac = Math.Clamp(_titleDown.X / Math.Max(1, ActualWidth), 0, 1);
            var src = PresentationSource.FromVisual(this);
            var m2d = src?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var screen = m2d.Transform(PointToScreen(_titleDown));
            WindowState = WindowState.Normal;
            Left = screen.X - Width * frac; Top = Math.Max(0, screen.Y - Math.Min(_titleDown.Y, 24));
        }
        try { DragMove(); } catch { }
    }

    private void OnTitleUp(object s, MouseButtonEventArgs e) { _titleArmed = false; if (TopBar.IsMouseCaptured) TopBar.ReleaseMouseCapture(); }

    private void OnMinimize(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object s, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? s, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        WindowEdge.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);     // a maximised window overhangs the screen by its resize border
        WindowEdge.BorderThickness = max ? new Thickness(0) : new Thickness(1);                         // outline round the whole window when it isn't maximised
        TopBar.BorderThickness = max ? new Thickness(1) : new Thickness(0, 0, 0, 1);                      // maximised: the bar gets its own outline
    }

    // ---- top bar: slides away on scroll down, back on scroll up; H hides it for good until shown again ----

    private bool BarPinned =>
        _folderPopup.IsOpen || _sideOpen || Banner.Visibility == Visibility.Visible || SearchBox.IsKeyboardFocused ||
        (TopBar.IsMouseOver && Stopwatch.GetTimestamp() > _ignoreOverUntil) || HoverStrip.IsMouseOver;

    private void ApplyBar()
    {
        bool want = BarPinned || (!_barManual && !_barAuto);
        if (want == _barShown) return;
        if (!want && (Stopwatch.GetTimestamp() - _barShownAt) / (double)Stopwatch.Frequency < 0.45) return;   // don't flicker straight after showing
        _barShown = want;
        if (want) _barShownAt = Stopwatch.GetTimestamp();
        double h = BarHost.ActualHeight + 4;
        var anim = new System.Windows.Media.Animation.DoubleAnimation(want ? 0 : -h, TimeSpan.FromMilliseconds(want ? 140 : 190))
        { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } };
        BarShift.BeginAnimation(TranslateTransform.YProperty, anim);
        BarHost.IsHitTestVisible = want;
        HoverStrip.Visibility = want ? Visibility.Collapsed : Visibility.Visible;
        ShowBarBtn.Visibility = !want && _barManual ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BarScroll()
    {
        double off = View.Offset, d = off - _lastOffset;
        _lastOffset = off;
        if (ViewerRoot.Visibility == Visibility.Visible || View.InRelayout) return;     // zooming or resizing moves the offset too, but isn't scrolling
        bool before = _barAuto;
        if (off < 30) { _barAuto = false; _barAccum = 0; }
        else if (d > 0) { _barAccum = Math.Max(0, _barAccum) + d; if (_barAccum > 90) _barAuto = true; }
        else if (d < 0) { _barAccum = Math.Min(0, _barAccum) + d; if (_barAccum < -50) _barAuto = false; }
        if (before != _barAuto) ApplyBar();
    }

    private void UpdateInset() => View.TopInset = _barManual ? 0 : (float)BarHost.ActualHeight;

    private void OnHideBar(object s, RoutedEventArgs e)
    {
        _barManual = true;
        _ignoreOverUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency;      // the mouse is still on the bar for a moment
        UpdateInset(); ApplyBar();
    }

    private void OnShowBar(object s, RoutedEventArgs e)
    {
        _barManual = false; _barAuto = false;
        UpdateInset(); ApplyBar();
    }

    private void RevealBar() { if (_barManual || _barAuto) { _barManual = false; _barAuto = false; UpdateInset(); } ApplyBar(); }

    // ---- side menu ----

    private void OnSidebarToggle(object s, RoutedEventArgs e) => SetSidebar(!_sideOpen);
    private void OnScrimDown(object s, MouseButtonEventArgs e) => SetSidebar(false);

    private void SetSidebar(bool open)
    {
        if (open == _sideOpen) return;
        _sideOpen = open;
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        if (open)
        {
            SideLayer.Visibility = Visibility.Visible;
            SideShift.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
            Scrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)));
        }
        else
        {
            var slide = new System.Windows.Media.Animation.DoubleAnimation(-260, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
            slide.Completed += (_, _) => { if (!_sideOpen) SideLayer.Visibility = Visibility.Collapsed; };
            SideShift.BeginAnimation(TranslateTransform.XProperty, slide);
            Scrim.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        }
        ApplyBar();
    }

    private void OnNav(object s, RoutedEventArgs e) => Navigate((string)((FrameworkElement)s).Tag);

    private void Navigate(string where)
    {
        NavHome.IsChecked = true; NavBrowse.IsChecked = NavFav.IsChecked = NavTools.IsChecked = false;
        if (where != "Home") StatusText.Text = $"{where} is coming in a later build.";
        SetSidebar(false);
    }

    private void OnSideSettings(object s, RoutedEventArgs e) { ((System.Windows.Controls.Primitives.ToggleButton)s).IsChecked = false; SetSidebar(false); OpenSettings(); }
    private void OnSideKeys(object s, RoutedEventArgs e) { ((System.Windows.Controls.Primitives.ToggleButton)s).IsChecked = false; SetSidebar(false); ShowKeys(false); }

    // ---- keyboard shortcut list ----

    private static readonly (string keys, string what)[] GridKeys =
    {
        ("Ctrl+B  or  M", "Open or close the side menu"), ("H", "Hide or show the top bar"), ("/  or  Ctrl+F", "Search"),
        ("G   N   V", "Show General / NSFW / Very files"), ("L", "Equal columns  or  equal rows"), ("+   -   or  Ctrl + wheel", "Picture size"),
        ("R", "Reshuffle (when sorted randomly)"), ("Home   End", "Top / bottom"), ("Page Up   Page Down", "Scroll one screen"),
        ("Enter  or  double-click", "Open the picture under the mouse"), ("Middle button", "Scroll by moving the mouse"),
        ("Ctrl+1 ... 4", "Home, Browse, Favourites, Tools"), ("K", "This list"), ("Esc", "Close / clear the search"),
    };
    private static readonly (string keys, string what)[] ViewerKeys =
    {
        ("Left   Right   (Space)", "Previous / next picture"), ("Home   End", "First / last picture"), ("Mouse wheel", "Zoom in and out"),
        ("Drag", "Move a zoomed picture"), ("0", "Fit to window"), ("1", "Actual size (100%)"), ("I", "More or less information"),
        ("H", "Hide or show the information box"), ("F  or  F11", "Full screen"), ("K", "This list"), ("Esc  or  double-click", "Back to the grid"),
    };

    private void OnKeysClick(object s, RoutedEventArgs e) => ShowKeys(false);
    private void OnKeysLayerDown(object s, MouseButtonEventArgs e) => KeysLayer.Visibility = Visibility.Collapsed;
    private void OnKeysPanelDown(object s, MouseButtonEventArgs e) => e.Handled = true;

    private void ShowKeys(bool viewer)
    {
        viewer = viewer || ViewerRoot.Visibility == Visibility.Visible;
        KeysTitle.Text = viewer ? "Keyboard shortcuts: picture viewer" : "Keyboard shortcuts";
        KeysGrid.Children.Clear(); KeysGrid.RowDefinitions.Clear(); KeysGrid.ColumnDefinitions.Clear();
        KeysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        KeysGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var list = viewer ? ViewerKeys : GridKeys;
        for (int i = 0; i < list.Length; i++)
        {
            KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var chip = new Border
            {
                Background = (Brush)FindResource("Field"), BorderBrush = (Brush)FindResource("Edge"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 3, 18, 3), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = list[i].keys, Foreground = (Brush)FindResource("Text"), FontFamily = new FontFamily("Consolas"), FontSize = 12.5 }
            };
            var what = new TextBlock { Text = list[i].what, Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(chip, i); Grid.SetRow(what, i); Grid.SetColumn(what, 1);
            KeysGrid.Children.Add(chip); KeysGrid.Children.Add(what);
        }
        KeysLayer.Visibility = Visibility.Visible;
    }

    // =========================================================================================================
    // viewer
    // =========================================================================================================

    private int _vIndex;
    private int _vToken;
    private double _zoom = 1, _panX, _panY;
    private bool _vFull, _dragging, _fullscreen;
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

    private bool _infoMore, _infoHidden;
    private long _lastNav;
    private readonly SemaphoreSlim _decodeGate = new(2);      // never more than two full-size decodes at once (holding an arrow key must not pile them up)

    private string[] _infoLines = Array.Empty<string>();      // block layout
    private string _infoOneLine = "";                         // the same details on a single line
    private string[] _infoStack = Array.Empty<string>();      // one detail per line, for a narrow strip beside the picture
    private double _charW60;

    private static string TrimName(string name, int max)
    {
        if (name.Length <= max) return name;
        string ext = System.IO.Path.GetExtension(name);
        if (ext.Length > 8) ext = "";
        int keep = Math.Max(4, max - ext.Length - 1);
        return name.Substring(0, Math.Min(keep, name.Length - ext.Length)) + "…" + ext;
    }

    private void UpdateInfo(int row)
    {
        var m = _model!;
        string name = System.IO.Path.GetFileName(m.Path[row]);
        string size = m.Size[row] >= 1048576 ? $"{m.Size[row] / 1048576.0:F1} MB" : $"{m.Size[row] / 1024.0:F0} KB";
        string kind = m.Kind[row] == (byte)MediaKind.Gif ? "   GIF" : "";
        string mod = DateTimeOffset.FromUnixTimeSeconds(m.Mtime[row]).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        string count = $"{_vIndex + 1:N0} / {_paths.Length:N0}";
        string facts = $"{m.W[row]} x {m.H[row]}    {size}{kind}    {mod}";
        if (_infoMore)
        {
            string dir = System.IO.Path.GetDirectoryName(m.Path[row]) ?? "";
            _infoLines = new[]
            {
                $"{count}    {name}",
                dir,
                $"{m.W[row]} x {m.H[row]}    {m.W[row] * (long)m.H[row] / 1e6:F1} megapixels    {size} ({m.Size[row]:N0} bytes){kind}    {(ContentLevel)m.Level[row]}",
                $"Modified {mod}    Added to Ginkgo {DateTimeOffset.FromUnixTimeSeconds(m.FirstSeen[row]).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)}",
            };
        }
        else
            _infoLines = new[] { $"{count}    {TrimName(name, Math.Max(12, 60 - count.Length - 4))}", facts };
        _infoOneLine = $"{count}    {TrimName(name, 36)}    {facts}";
        string md = $"{m.W[row]} x {m.H[row]}";
        _infoStack = _infoMore
            ? new[] { count, name, System.IO.Path.GetDirectoryName(m.Path[row]) ?? "", md, $"{m.W[row] * (long)m.H[row] / 1e6:F1} megapixels", $"{size} ({m.Size[row]:N0} bytes)", (kind.Trim() + " " + (ContentLevel)m.Level[row]).Trim(), "Modified " + mod, "Added " + DateTimeOffset.FromUnixTimeSeconds(m.FirstSeen[row]).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture) }
            : new[] { count, TrimName(name, 40), md, size + kind, mod };
        PlaceInfo();
    }

    private void OnViewerSize(object s, SizeChangedEventArgs e) { if (ViewerRoot.Visibility == Visibility.Visible && _model is not null && _vIndex < _order.Length) PlaceInfo(); }

    /// <summary>
    /// The information box always sits at the bottom left. Where exactly depends on the picture: in the empty strip below a wide
    /// one, in the empty strip beside a tall one (made narrow and tall so the lines wrap), or on a single line when only a thin
    /// strip is free. If nothing is free it sits over the picture's bottom-left corner.
    /// </summary>
    private void PlaceInfo()
    {
        InfoBox.Visibility = _infoHidden ? Visibility.Collapsed : Visibility.Visible;
        if (_infoHidden || _model is null || _vIndex >= _order.Length) return;
        double HW = PicHost.ActualWidth, HH = PicHost.ActualHeight;
        if (HW < 50 || HH < 50) { HW = ActualWidth; HH = ActualHeight; }
        if (_charW60 == 0)
            _charW60 = new FormattedText(new string('0', 60), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(InfoText.FontFamily, InfoText.FontStyle, InfoText.FontWeight, InfoText.FontStretch), InfoText.FontSize, Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip).Width;
        int row = _order[_vIndex];
        double iw = _model.W[row], ih = _model.H[row];
        // where the picture is on screen
        double f = iw > 0 && ih > 0 ? Math.Min(HW / iw, HH / ih) : 1;
        double dw = iw * f * _zoom, dh = ih * f * _zoom, cx = HW / 2 + _panX, cy = HH / 2 + _panY;
        double left = Math.Max(0, cx - dw / 2), bottom = Math.Min(HH, cy + dh / 2);
        double leftFree = left, bottomFree = HH - bottom;
        double cap = _infoMore ? Math.Max(_charW60, HW * 0.5) : _charW60;     // normally about 60 characters wide; more info may grow

        // kind: 0 = block of lines, 1 = one line, 2 = one detail per line (narrow)
        Size TryMeasure(int kind, double maxBox)
        {
            InfoBox.Padding = kind == 2 ? new Thickness(7, 5, 7, 5) : new Thickness(10, 6, 10, 6);
            double padX = InfoBox.Padding.Left + InfoBox.Padding.Right;
            InfoText.TextWrapping = kind == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
            InfoText.Text = kind == 1 ? _infoOneLine : string.Join("\n", kind == 2 ? _infoStack : _infoLines);
            InfoText.MaxWidth = kind == 1 ? double.PositiveInfinity : Math.Max(40, maxBox - padX);
            InfoBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return InfoBox.DesiredSize;
        }
        void Put(double x, double y) { InfoBox.HorizontalAlignment = HorizontalAlignment.Left; InfoBox.VerticalAlignment = VerticalAlignment.Bottom; InfoBox.Margin = new Thickness(x, 0, 0, y); }

        // 1. below the picture, as a block
        var s = TryMeasure(0, Math.Min(cap + 20, HW - 24));
        if (bottomFree > 0 && s.Height + 24 <= bottomFree) { Put(12, 12); return; }
        // 2. beside the picture on the left: narrow and tall
        if (leftFree >= 150)
        {
            // wide strip: the normal block (2 lines normally) if it comes out short enough
            s = TryMeasure(0, Math.Min(cap + 20, leftFree - 12));
            double lineH = 17;
            if (s.Width + 12 <= leftFree && s.Height <= (_infoMore ? 9 : 3) * lineH + 14 && s.Height + 12 <= HH) { Put(6, 6); return; }
        }
        if (leftFree >= 56)
        {
            s = TryMeasure(2, leftFree - 10);
            if (s.Width + 6 <= leftFree && s.Height + 12 <= HH) { Put(5, 6); return; }
        }
        // 3. below the picture, on one line
        s = TryMeasure(1, 0);
        if (bottomFree > 0 && s.Width + 24 <= HW && s.Height + 12 <= bottomFree) { Put(12, Math.Max(4, (bottomFree - s.Height) / 2)); return; }
        // 4. nothing free: over the bottom-left corner of the picture
        TryMeasure(0, Math.Min(cap + 20, HW - 24));
        Put(12, 12);
    }

    private void ShowIndex(int index)
    {
        _vIndex = Math.Clamp(index, 0, _paths.Length - 1);
        ResetZoom();
        int row = _order[_vIndex];
        UpdateInfo(row);
        Interlocked.Exchange(ref _engine.Pipe.LastUiActivity, Stopwatch.GetTimestamp());      // keep the drive free for the picture
        _ = LoadPicture(_vIndex, full: false);
    }

    private (int w, int h) FitPixels(int row)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double w = PicHost.ActualWidth, h = PicHost.ActualHeight;
        if (w < 50 || h < 50) { w = ActualWidth; h = ActualHeight; }       // the viewer has only just appeared and isn't measured yet
        return ((int)Math.Max(400, w * dpi), (int)Math.Max(300, h * dpi));
    }

    private static BitmapSource? Decode(string path, int decodeW, int decodeH)
    {
        try
        {
            // read through a stream (not a Uri), so names containing # or % work
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeW > 0) bi.DecodePixelWidth = decodeW; else if (decodeH > 0) bi.DecodePixelHeight = decodeH;
            bi.StreamSource = fs;
            bi.EndInit();
            bi.Freeze();
            return bi;
        }
        catch { return null; }
    }

    private async Task LoadPicture(int index, bool full)
    {
        int token = ++_vToken;
        long nowTick = Stopwatch.GetTimestamp();
        bool fast = (nowTick - _lastNav) / (double)Stopwatch.Frequency < 0.3;       // keys are being held or tapped quickly
        _lastNav = nowTick;
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
            var loader = _loader;
            if (!full && loader is not null)
            {
                // something is on screen at once: the stored picture (a small one while keys are held, to keep up)
                var quick = await Task.Run(() => token != _vToken ? null : loader.QuickPicture(index, fast));
                if (token != _vToken) return;
                if (quick is not null) { Pic.Source = quick; _vFull = false; }
            }
            if (fast && !full) { await Task.Delay(170); if (token != _vToken) return; }     // still moving: wait until it settles before the real file
            bmp = await Task.Run(() =>
            {
                _decodeGate.Wait();
                try { return token != _vToken ? null : Decode(path, decodeW, decodeH); }
                finally { _decodeGate.Release(); }
            });
            if (bmp is not null && !full) lock (_vCache) CacheAdd(key, bmp);
        }
        if (token != _vToken) return;                                 // moved on to another picture meanwhile
        _vFull = full;
        if (bmp is null) { if (Pic.Source is null) ViewerNote.Text = "This file can't be shown."; return; }
        Pic.Source = bmp;
        ViewerNote.Text = "";
        if (!full)
        {
            _ = Preload(index + 1); _ = Preload(index + 2); _ = Preload(index - 1);
        }
    }

    private void CacheAdd(string key, BitmapSource bmp)
    {
        if (_vCache.ContainsKey(key)) return;
        _vCache[key] = bmp; _vCacheOrder.Enqueue(key);
        while (_vCacheOrder.Count > 6) _vCache.Remove(_vCacheOrder.Dequeue());
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
        int token0 = _vToken;
        var bmp = await Task.Run(() =>
        {
            _decodeGate.Wait();
            try { return token0 != _vToken ? null : Decode(path, decodeW, decodeH); }
            finally { _decodeGate.Release(); }
        });
        if (bmp is not null) lock (_vCache) CacheAdd(key, bmp);
    }

    private void ResetZoom() { _zoom = 1; _panX = _panY = 0; ApplyTransform(); }

    private void ApplyTransform()
    {
        PicScale.ScaleX = PicScale.ScaleY = _zoom;
        PicMove.X = _panX; PicMove.Y = _panY;
        if (ViewerRoot.Visibility == Visibility.Visible && _model is not null && _vIndex < _order.Length) PlaceInfo();
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
            case Key.K: case Key.OemQuestion: ShowKeys(true); break;
            case Key.H: _infoHidden = !_infoHidden; UpdateInfo(_order[_vIndex]); break;
            case Key.I: _infoMore = !_infoMore; _infoHidden = false; UpdateInfo(_order[_vIndex]); break;
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
