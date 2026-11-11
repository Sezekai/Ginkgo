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
    private GifAnimator _gifs = null!;
    private readonly Engine _engine;
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
    private readonly PopCard _folderCard;
    private System.Windows.Controls.Primitives.Popup _optPopup = null!;
    private OptimisePanel _opt = null!;
    private long _optClosedAt;
    private readonly FolderPanel _folders;
    private long _popupClosedAt;

    // top bar
    private bool _barManual, _barAuto, _barShown = true;
    private double _barAccum, _lastOffset;
    private long _barShownAt, _ignoreOverUntil;

    // side menu
    private bool _diag;
    private long _lastFrame; private double _worstGap; private TimeSpan _lastRenderTime; private int _slowFrames, _slowGc, _slowOurs, _slowOther; private long _busyAtFrame; private TimeSpan _gcAtFrame, _gcAtTick;

    private const float TileMin = 60, TileMax = 1800;
    private static double SizeToSlider(float size) => Math.Log(Math.Clamp(size, TileMin, TileMax) / TileMin) / Math.Log(TileMax / TileMin);
    private static float SliderToSize(double t) => (float)(TileMin * Math.Pow(TileMax / TileMin, Math.Clamp(t, 0, 1)));

    public MainWindow()
    {
        ThemeKit.Apply(_settings.Theme, _settings.Look);           // the theme and button look, before anything is drawn
        InitializeComponent();
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        _engine = new Engine(_settings);

        // restore what the person last chose (NSFW and Very always start off)
        GeneralBtn.IsChecked = _settings.ShowGeneral; NsfwBtn.IsChecked = false; VeryBtn.IsChecked = false;
        ImagesBtn.IsChecked = _settings.ShowImages; GifsBtn.IsChecked = _settings.ShowGifs; VideosBtn.IsChecked = _settings.ShowVideos;
        RowsBtn.IsChecked = _settings.RowsLayout; View.RowsMode = _settings.RowsLayout;
        View.Gap = _settings.Spacing;
        SizeSlider.Value = SizeToSlider(_settings.TileSize); View.TileTarget = _settings.TileSize;
        View.TileTargetChanged += size =>
        {
            _settings.TileSize = size;
            _syncingSize = true; SizeSlider.Value = SizeToSlider(size); _syncingSize = false;
        };
        foreach (ComboBoxItem it in SortBox.Items) if ((string)it.Tag == _settings.Sort) SortBox.SelectedItem = it;
        if (SortBox.SelectedItem is null) SortBox.SelectedIndex = 0;
        SortRevBtn.IsChecked = _settings.SortReverse;
        ShuffleBtn.Visibility = _settings.Sort == "Random" ? Visibility.Visible : Visibility.Collapsed;
        View.LookAheadWindows = _settings.LookAheadWindows;
        _barManual = _settings.BarHidden;
        if (_barManual && _settings.StatusHidden) StatusBar.Visibility = Visibility.Collapsed;
        UpdateFoldersLabel();

        _folders = new FolderPanel(_settings.FolderSort, _settings.FolderSortReverse);
        _folders.Changed += off =>
        {
            _settings.OffFolders = off.ToList();
            UpdateFoldersLabel();
            _folderDelay.Stop(); _folderDelay.Start();
        };
        _folders.SortChanged += (key, rev) => { _settings.FolderSort = key; _settings.FolderSortReverse = rev; };
        _folders.PlacesChanged += (alone, rare) => { _settings.Standalone = alone.ToList(); _settings.RarelyUsed = rare.ToList(); SaveSettings(); };
        _folderCard = new PopCard(_folders, "Select folders");
        _folderPopup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = FoldersBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
            AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 2,
            Child = _folderCard
        };
        _folderPopup.Opened += (_, _) => FoldersBtn.Tag = true;
        _folderPopup.Closed += (_, _) => { _popupClosedAt = Stopwatch.GetTimestamp(); FoldersBtn.Tag = false; ApplyBar(); };
        Sticky(_folderPopup);
        _loading = false;

        View.ScrollChanged += SyncBar;
        View.ScrollChanged += BarScroll;
        View.ScrollChanged += () => Interlocked.Exchange(ref _engine.Pipe.LastUiActivity, Stopwatch.GetTimestamp());
        View.HoverChanged += i => StatusText.Text = IsFolderTile(i) ? FolderHoverText(i) : i >= 0 && i < _paths.Length ? HoverText(i) : StatusLine();
        View.ItemActivated += i => { if (RecentlyNavigated() || (Stopwatch.GetTimestamp() - _viewerClosedAt) * 1000 < 500 * Stopwatch.Frequency) return; if (IsFolderTile(i)) OpenFolder(_page!.Tiles[i].Path); else OpenViewer(i); };
        InitBrowse();
        InitVideo();
        InitFavourites();
        InitLibraryTools();
        InitViews();
        InitSlideshow();
        InitTools();
        InitSections();
        Sticky(SavedPopup);
        SavedPopup.Opened += (_, _) => ViewsBtn.Tag = true;
        SavedPopup.Closed += (_, _) => { ViewsBtn.Tag = false; ApplyBar(); };
        StartSections();
        ApplyLooks();
        _gifs = new GifAnimator(i => i >= 0 && i < _paths.Length ? _paths[i] : null, () => Dispatcher.BeginInvoke(View.Refresh));
        View.Gifs = _gifs;
        _gifs.Limit = _settings.GifLimit;
        BarRow.MoreButton = MoreBtn;
        // a click anywhere else closes the drop-down; it also closes when everything fits again
        PreviewMouseDown += (_, e) =>
        {
            if (!BarRow.Expanded) return;
            for (var d = e.OriginalSource as DependencyObject; d != null; d = ParentOf(d)) if (d == BarRow) return;
            if (e.OriginalSource is DependencyObject o && IsInPopup(o)) return;      // a sort list opened from the drop-down
            BarRow.Expanded = false;
        };
        BarRow.LayoutUpdated += (_, _) => { if (BarRow.Expanded && !BarRow.HasOverflow) BarRow.Expanded = false; };
        SourceInitialized += (_, _) => { Theme.RoundedFrame(this, 0x4A, 0x4A, 0x54); OnStateChanged(this, EventArgs.Empty); StartGpu(); };
        SizeChanged += (_, _) => Dispatcher.BeginInvoke(UpdateEdges, DispatcherPriority.Loaded);
        LocationChanged += (_, _) => Dispatcher.BeginInvoke(UpdateEdges, DispatcherPriority.Loaded);
        CompositionTarget.Rendering += (_, e) =>
        {
            // the event can come more than once per screen refresh: count each refresh once
            if (e is RenderingEventArgs re) { if (re.RenderingTime == _lastRenderTime) return; _lastRenderTime = re.RenderingTime; }
            _frames++;
            long t = Stopwatch.GetTimestamp();
            long busy = Perf.BusyTicks - _busyAtFrame; _busyAtFrame = Perf.BusyTicks;
            var gcNow = GC.GetTotalPauseDuration(); double gcMs = (gcNow - _gcAtFrame).TotalMilliseconds; _gcAtFrame = gcNow;
            if (_lastFrame != 0 && (t - _lastFrame) * 1000 > 25 * Stopwatch.Frequency)
            {
                _slowFrames++;
                if (gcMs > 8) _slowGc++; else if (Perf.Ms(busy) > 12) _slowOurs++; else _slowOther++;
            }
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
        // while Ginkgo is the window in use, it comes first; in the background it is an ordinary program again
        Activated += (_, _) => { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { } View.Refresh(); };
        Deactivated += (_, _) => { try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.Normal; } catch { } };
        Closing += (_, _) =>
        {
            _settings.BarHidden = _barManual;
            KeepHomeForNextTime();
            CleanGpuExit();
            StopGrid();
            _engine.Warmer.Vram = null;        // the process is ending: the card's memory goes with it
            SaveSettings();
            _covers.Save();
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
        string favRoot = _settings.FavRoot;
        var m = await Task.Run(() => LibraryModel.Load(db, favRoot, favourites: false));
        var fm = await Task.Run(() => LibraryModel.Load(db, favRoot, favourites: true));
        _libModel = m; _favModel = fm;
        _dupResult = ForModel(_dupResult, m);                      // the duplicate finder's groups, found again in the new list
        MigrateFolderRules(m);
        _fav.RequestSync();
        _engine.ViewRefreshed();
        UpdateChangeIndicator();
        await BuildViewAsync(keepPosition);
        StatusText.Text = $"library loaded in {sw.Elapsed.TotalSeconds:F1}s";
    }

    /// <summary>Old settings hid a folder together with everything inside it; each folder now stands alone, so spell the old rule out.</summary>
    private void MigrateFolderRules(LibraryModel m)
    {
        if (_settings.FolderRulesVersion == 2) MigrateFolderRules3();
        if (_settings.FolderRulesVersion >= 2) return;
        var all = new HashSet<string>(m.Folder.Take(m.Count), StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in all)
            foreach (var e in _settings.OffFolders)
                if (f.Equals(e, StringComparison.OrdinalIgnoreCase) || f.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase) || f.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase)) { off.Add(f); break; }
        _settings.OffFolders = off.ToList();
        _settings.FolderRulesVersion = 2;
        MigrateFolderRules3();
        UpdateFoldersLabel();
    }

    /// <summary>Folders are now switched as Browse shows them; a switched-off NSFW or Very folder becomes that folder's NSFW / Very switch.</summary>
    private void MigrateFolderRules3()
    {
        var off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nsfw = new HashSet<string>(_settings.NsfwOff, StringComparer.OrdinalIgnoreCase);
        var very = new HashSet<string>(_settings.VeryOff, StringComparer.OrdinalIgnoreCase);
        foreach (var f in _settings.OffFolders)
        {
            var level = ContentClassifier.ClassifyRelativeFolder(f);
            if (level != ContentLevel.General && LevelOwner.Of(f) is { } owner) (level == ContentLevel.Very ? very : nsfw).Add(owner);
            else off.Add(FolderPaths.Display(f));
        }
        _settings.OffFolders = off.ToList(); _settings.NsfwOff = nsfw.ToList(); _settings.VeryOff = very.ToList();
        _settings.FolderRulesVersion = 3;
    }

    private ViewQuery CurrentQuery() => new()
    {
        General = GeneralBtn.IsChecked == true, Nsfw = NsfwBtn.IsChecked == true, Very = VeryBtn.IsChecked == true,
        Images = ImagesBtn.IsChecked == true, Gifs = GifsBtn.IsChecked == true, Videos = VideosBtn.IsChecked == true,
        OffFolders = new HashSet<string>(_settings.OffFolders, StringComparer.OrdinalIgnoreCase),
        NsfwOff = new HashSet<string>(_settings.NsfwOff, StringComparer.OrdinalIgnoreCase),
        VeryOff = new HashSet<string>(_settings.VeryOff, StringComparer.OrdinalIgnoreCase),
        Search = SearchBox.Text, Sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen", Reverse = SortRevBtn.IsChecked == true,
        RandomSeed = _randomSeed, F = _settings.Filters.Copy(), IsFavourite = _fav.IsStarred,
        SetLimit = Math.Max(1, _settings.SetLimit), OpenSets = new HashSet<string>(_openSets, StringComparer.OrdinalIgnoreCase),
        SetsByFirst = _settings.SetsByFirst, HideCopies = _settings.HideCopies,
    };

    private async void BuildView(bool keepPosition) => await BuildViewAsync(keepPosition);

    private async Task BuildViewAsync(bool keepPosition, double? offset = null)
    {
        var model = TargetModel; var stores = _engine.Stores;
        if (model is null || stores is null) return;
        int version = ++_buildVersion;
        var q = CurrentQuery();
        bool browse = InBrowse;
        if (InFavourites) q.OffFolders.Clear();                   // Select folders shapes Home; Favourites shows the whole folder
        string folder = _folder; bool own = _settings.BrowseOwnOnly;
        var prefs = Prefs(_folder);
        string fsort = prefs.FolderSort; bool frev = prefs.FolderReverse;
        bool grid = _settings.BrowseCoverGrid;
        var details = BrowseDetails();
        q.RowFilter = VideoRowFilter();
        bool tools = InTools; string tool = _settings.Tool; var dupResult = tools && tool == "Duplicates" ? (_dupResult = ForModel(_dupResult, model)) : _dupResult;
        if (tools && tool == "LowRes") { var low = LowResFilter(model); var vf0 = q.RowFilter; q.RowFilter = vf0 is null ? low : r => low(r) && vf0(r); q.HideCopies = false; }
        var result = await Task.Run(() =>
        {
            BrowsePage? page = null;
            int[] order; List<SetBlock> sets; Dictionary<int, List<int>> copies;
            if (tools && tool == "Duplicates") { order = dupResult?.Order ?? Array.Empty<int>(); sets = dupResult?.Blocks ?? new(); copies = new(); }
            else if (browse)
            {
                q.OffFolders.Clear();                               // Browse shows every folder; Select folders shapes Home
                q.PlainSets = folder.Length > 0 && FolderPaths.IsUnder(folder, Ginkgo.Core.ConnectedArt.FolderName);   // inside Connected Art every image just shows
                page = BrowsePage.Build(model, q, folder, own, fsort, frev, _covers, details);
                order = page.Order; sets = page.Sets; copies = page.Copies;
            }
            else { var a = model.Select(q); order = a.Order; sets = a.Sets; copies = a.Copies; }
            var slice = model.Slice(order, stores);
            // Connected Art sets: a small set is one block; a large one is a stacked tile (its other images take no space) until opened
            foreach (var b in sets)
            {
                if (b.Collapsed) { for (int k = b.First + 1; k < b.First + b.Count; k++) slice.specs[k] = slice.specs[k] with { Hidden = true }; }
                else if (b.Count > 1) slice.specs[b.First] = slice.specs[b.First] with { Block = b.Count, Wide = b.Count > q.SetLimit };
            }
            return (order, page, slice, sets, copies);
        });
        if (version != _buildVersion) return;                     // something newer was asked for meanwhile
        _page = result.page;
        _firstFile = result.page?.FirstFile ?? 0;
        if (result.page is not null && result.page.Folder != _folder) { _folder = result.page.Folder; UpdateNavUi(); }   // the folder has gone (or is hidden): its nearest parent instead
        for (int k = 0; k < _firstFile; k++) result.slice.badges[k] = 0;
        _order = result.order;
        _shownModel = model;
        _paths = result.slice.paths;
        _sets = result.sets; _copies = result.copies;
        View.Sets = _sets;
        var ids = result.slice.ids;
        DetachLoader();
        var (decoded, encoded) = CacheBudgets();
        _loader = new ThumbLoader(result.slice.tiers, _paths, result.slice.origW, stores.Large, Engine.NoCap,
            (i, loc) => _engine.QueueLarge(ids[i], loc.Pack, loc.Off, loc.Len), Dispatcher, () => View.Refresh(), decoded, encoded)
        { FadeEnabled = _settings.FadeSharpening };
        ThumbLoader.Crop?[] covers = Array.Empty<ThumbLoader.Crop?>();
        if (result.page is { } cp && grid && cp.FirstFile > 0)
        {
            covers = new ThumbLoader.Crop?[cp.FirstFile];
            for (int k = 0; k < covers.Length; k++) covers[k] = CropForTile(cp, k, result.order);
        }
        // the Grid layout without Fit crops every picture to its square cell
        _cropApplied = _settings.GridLayout && !_settings.GridFit;
        var gridOrder = result.order; var gridModel = model; int gridFirst = _firstFile; bool gridCrop = _cropApplied;
        double gridTilePx = View.TileTarget * VisualTreeHelper.GetDpi(View).PixelsPerDip;
        if (covers.Length > 0 || gridCrop)
            _loader.CropFor = i => i < covers.Length ? covers[i] : gridCrop ? GridCrop(i, gridOrder, gridModel, gridFirst, gridTilePx) : null;
        View.PrefetchBudgetBytes = decoded * 36 / 100;
        _gifs.Clear();
        View.Badges = result.slice.badges;
        View.BadgeText = BadgeTexts(result.order, result.slice.badges);
        View.Thumbs = _loader;
        View.CoverRadius = _settings.CoverRounded ? _settings.CoverRadius : 0;
        if (!keepPosition) View.SelectedIndex = -1;
        View.SetItems(result.slice.specs, result.page?.Labels, keepPosition, grid && result.page is not null ? _firstFile : 0);
        VideoItemsChanged();
        if (offset is double o) View.Offset = o;
        _covers.Save();
        EmptyNote.Visibility = _order.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_order.Length == 0)
            EmptyNote.Text = InFavourites && model.Count == 0
                ? "No favourites yet.\nClick the star in a picture's top-left corner (or in the viewer) to copy it into your Favourites folder."
                : model.Count == 0
                ? "Nothing to show yet.\nGinkgo is looking through your library and making thumbnails. Pictures appear here as they become ready."
                : "No files match the current filters.";
        CountText.Text = result.page is { } pg
            ? $"{pg.Tiles.Count:N0} folder{(pg.Tiles.Count == 1 ? "" : "s")}, {_order.Length - pg.Tiles.Count:N0} files"
            : $"{_order.Length:N0} shown of {model.Count:N0}";
        if (result.page is { } pp && _order.Length == 0) EmptyNote.Text = "Nothing in this folder matches the current switches and search.";
        if (InTools && _order.Length == 0)
            EmptyNote.Text = _settings.Tool == "Duplicates"
                ? (_dupResult is null ? "Press Find to look for copies of the same file in each top-level folder." : "No copies found with these options.")
                : "Nothing falls below these minimums in the folders chosen in Select folders.";
        if (InTools && _settings.Tool == "Duplicates" && _dupResult is { } dr) CountText.Text = $"{dr.Blocks.Count:N0} groups, {_order.Length:N0} files";
    }

    /// <summary>The length shown on video tiles.</summary>
    private string?[] BadgeTexts(int[] order, byte[] badges)
    {
        var m = _model!;
        var t = new string?[order.Length];
        for (int k = 0; k < order.Length; k++) if (badges[k] == 2 && order[k] >= 0) t[k] = Clock(m.Duration[order[k]]);
        return t;
    }

    private void DetachLoader() { View.Thumbs = null; _loader?.Dispose(); _loader = null; }

    private (long decoded, long encoded) CacheBudgets()
    {
        var p = AppSettings.CacheProfiles[Math.Clamp(_settings.CacheProfile, 0, AppSettings.CacheProfiles.Length - 1)];
        if (p.decoded < 0)
        {
            // automatic: a share of the memory that is free right now (pictures are the size of the tiles, so this is many screens' worth)
            long free = CacheWarmer.AvailableBytes();
            long decoded = Math.Clamp(free / 6, 160L << 20, 1536L << 20);
            return (decoded, 96L << 20);
        }
        return (p.decoded * 1024L * 1024, p.encoded * 1024L * 1024);
    }

    // =========================================================================================================
    // toolbar
    // =========================================================================================================

    private void OnFilterChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        _settings.ShowGeneral = GeneralBtn.IsChecked == true;
        _settings.ShowImages = ImagesBtn.IsChecked == true; _settings.ShowGifs = GifsBtn.IsChecked == true; _settings.ShowVideos = VideosBtn.IsChecked == true;
        BuildView(false);
    }

    private void OnLayoutChanged(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        SetLayout(RowsBtn.IsChecked == true ? "Rows" : "Masonry");
    }

    private void OnSearchChanged(object s, TextChangedEventArgs e) { if (_loading) return; _searchDelay.Stop(); _searchDelay.Start(); }

    private void OnSortChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string tag = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
        if (InBrowse) PrefsForEdit(_folder).FileSort = tag;          // each folder keeps its own order
        else _settings.Sort = tag;
        UpdateSortTip();
        ShuffleBtn.Visibility = tag == "Random" ? Visibility.Visible : Visibility.Collapsed;
        BuildView(false);
    }

    private void OnSortRev(object s, RoutedEventArgs e)
    {
        if (_loading) return;
        bool rev = SortRevBtn.IsChecked == true;
        if (InBrowse) PrefsForEdit(_folder).FileReverse = rev; else _settings.SortReverse = rev;
        UpdateSortTip();
        BuildView(false);
    }

    /// <summary>The reverse button says which way the order runs now.</summary>
    private void UpdateSortTip()
    {
        string tag = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
        bool desc = LibraryModel.Descending(tag) != (SortRevBtn.IsChecked == true);
        SortRevBtn.ToolTip = tag switch
        {
            "Name" or "Folder" => desc ? "Z to A (click for A to Z)" : "A to Z (click for Z to A)",
            "Size" or "Width" or "Height" or "Megapixels" => desc ? "Largest first (click for smallest first)" : "Smallest first (click for largest first)",
            "Duration" => desc ? "Longest first (click for shortest first)" : "Shortest first (click for longest first)",
            "Random" => "Reverse the shuffled order",
            _ => desc ? "Newest first (click for oldest first)" : "Oldest first (click for newest first)",
        };
    }

    private void OnReshuffle(object s, RoutedEventArgs e) { _randomSeed = Environment.TickCount; BuildView(false); }

    private void OnSizeChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _syncingSize) return;
        View.TileTarget = SliderToSize(e.NewValue);
    }

    private void OnFolders(object s, RoutedEventArgs e)
    {
        if (_libModel is null) return;
        if (_folderPopup.IsOpen) { _folderPopup.IsOpen = false; return; }      // its own button closes it
        _folders.Width = Math.Min(WindowState == WindowState.Maximized ? 1152 : 768, Math.Max(300, ActualWidth - 48));
        // keep the pop-out inside the window: slide it left if it would reach past the right edge
        double x0 = FoldersBtn.TranslatePoint(new Point(0, 0), this).X;
        _folderPopup.HorizontalOffset = Math.Min(0, ActualWidth - 10 - (_folders.Width + 26) - x0);
        _folderCard.NotchX = FoldersBtn.ActualWidth / 2 - _folderPopup.HorizontalOffset - 6;
        _folders.Load(_libModel.FolderStats(CurrentQuery()), _settings.OffFolders, _settings.Standalone, _settings.RarelyUsed);
        _folderPopup.IsOpen = true;
    }

    private void UpdateFoldersLabel()
    {
        int n = _settings.OffFolders.Count;
        FoldersBtn.Content = n == 0 ? "Select folders" : $"Select folders ({n:N0} off)";
    }

    private void OnSettings(object s, RoutedEventArgs e) => OpenSettings();

    /// <summary>The Optimisations pop-out: thumbnails in memory (on/off and a limit) and how many GIFs play at once.</summary>
    private void OnOptimise(object s, RoutedEventArgs e)
    {
        if (_opt is null)
        {
            _opt = new OptimisePanel(_settings, _engine.Warmer);
            _opt.Changed += () => { _gifs.Limit = _settings.GifLimit; ApplyGifSettings(); SaveSettings(); };
            _optPopup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = OptimiseBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 2,
                Child = new Border { Margin = new Thickness(6, 0, 20, 20), Child = _opt }
            };
            _optPopup.Closed += (_, _) => { _optClosedAt = Stopwatch.GetTimestamp(); ApplyBar(); };
            Sticky(_optPopup);
        }
        if (_optPopup.IsOpen) { _optPopup.IsOpen = false; return; }
        _opt.Width = Math.Min(420, Math.Max(280, ActualWidth - 48));
        // keep it inside the window
        var btn = OptimiseBtn.IsVisible ? OptimiseBtn : (FrameworkElement)MoreBtn;
        _optPopup.PlacementTarget = btn;
        double x0 = btn.TranslatePoint(new Point(0, 0), this).X;
        _optPopup.HorizontalOffset = Math.Min(0, ActualWidth - 10 - (_opt.Width + 26) - x0);
        _optPopup.IsOpen = true;
        ApplyBar();
    }
    private System.Windows.Controls.Primitives.Popup? _quickPopup;
    private QuickPanel? _quick;
    private long _quickClosedAt;

    /// <summary>The Quick toggles pop-out: how videos behave in the grid, and the outlines round paused and watched ones.</summary>
    private void OnQuick(object s, RoutedEventArgs e)
    {
        if (_quick is null)
        {
            _quick = new QuickPanel(_settings);
            _quick.Changed += () => { ApplyVideoLook(); SaveSettings(); };
            _quickPopup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = QuickBtn, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false,
                AllowsTransparency = true, PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.Fade, VerticalOffset = 2,
                Child = new Border { Margin = new Thickness(6, 0, 20, 20), Child = _quick }
            };
            _quickPopup.Closed += (_, _) => { _quickClosedAt = Stopwatch.GetTimestamp(); ApplyBar(); };
            Sticky(_quickPopup);
        }
        if (_quickPopup!.IsOpen) { _quickPopup.IsOpen = false; return; }
        _quick.Sync();
        _quick.Width = Math.Min(420, Math.Max(280, ActualWidth - 48));
        _quick.MaxHeight = Math.Max(300, ActualHeight - 120);
        var btn = QuickBtn.IsVisible ? QuickBtn : (FrameworkElement)MoreBtn;
        _quickPopup.PlacementTarget = btn;
        double x0 = btn.TranslatePoint(new Point(0, 0), this).X;
        _quickPopup.HorizontalOffset = Math.Min(0, ActualWidth - 10 - (_quick.Width + 26) - x0);
        _quickPopup.IsOpen = true;
        ApplyBar();
    }

    private void OpenSettings() => new SettingsWindow(this, _settings, _engine) { Owner = this }.ShowDialog();

    private void OnUpdateView(object s, RoutedEventArgs e) => _ = ReloadModel(true);

    private void SaveSettings() { if (!_skipSave) _settings.Save(); }

    // called by the settings window
    /// <summary>Lays the view out again (after a setting that changes what shows or its order).</summary>
    public void RebuildView() => BuildView(true);

    public void ApplySettings()
    {
        View.LookAheadWindows = _settings.LookAheadWindows;
        var (d, c) = CacheBudgets();
        _loader?.SetBudgets(d, c);
        View.PrefetchBudgetBytes = d * 36 / 100;
        if (_loader is not null) _loader.FadeEnabled = _settings.FadeSharpening;
        ApplyGifSettings();
        SaveSettings();
        double r = _settings.CoverRounded ? _settings.CoverRadius : 0;
        if (InBrowse && Math.Abs(View.CoverRadius - r) > 0.1) BuildView(true);     // covers are cut with their corners: remake them
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
        string len = m.Kind[row] == (byte)MediaKind.Video ? "   " + Clock(m.Duration[row]) : "";
        return $"{m.Path[row]}   {m.W[row]} x {m.H[row]}{len}   {m.Size[row] / 1048576.0:F1} MB{CopiesText(row)}";
    }

    /// <summary>A video length as 0:42, 3:07 or 1:02:09.</summary>
    public static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, Math.Round(seconds)));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private string StatusLine()
    {
        var p = _engine.Pipe;
        string fav = FavStatus();
        if (fav.Length > 0) return fav;
        if (_engine.IdsRunning && _engine.IdsTotal > 0)
            return $"Reading file IDs from the drive (this is the disk noise, it only happens once): {_engine.IdsDone:N0} of {_engine.IdsTotal:N0}";
        string pipe = p.Running || p.Total > 0 && p.Done < p.Total
            ? $"  |  thumbnails {p.Done:N0} of {p.Total:N0}{(p.Failed > 0 ? $" ({p.Failed:N0} could not be read)" : "")}"
            : "";
        string back = p.BackfillTotal > 0 && p.BackfillDone < p.BackfillTotal ? $"  |  small sizes {p.BackfillDone:N0} of {p.BackfillTotal:N0}" : "";
        string sharp = p.SharpTotal > 0 && p.SharpDone < p.SharpTotal ? $"  |  sharp versions {p.SharpDone:N0} of {p.SharpTotal:N0}" : "";
        string warm = _engine.Warmer.Running && _engine.Warmer.Total > 0 ? $"  |  loading thumbnails into memory {_engine.Warmer.Done * 100 / _engine.Warmer.Total}%" : "";
        pipe += warm;
        bool busy = pipe.Length > 0 || back.Length > 0 || sharp.Length > 0 || p.Running || _engine.IdsRunning || _engine.Details.Running;
        string idle = !busy && _engine.IdsFinished ? "  |  file IDs done, nothing running in the background" : "";
        return _engine.Status + pipe + back + sharp + DetailsStatus() + idle + (_engine.FastCacheNote.Length > 0 ? "  |  " + _engine.FastCacheNote : "");
    }

    private void UpdateChangeIndicator()
    {
        int n = _engine.ChangesSinceView;
        UpdateBtn.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateBtn.Content = n > 0 ? $"Update view ({n:N0} changes)" : "Update view";
    }

    private int _slowAtLastTick;
    private long _moveStarted;

    private void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        _fps = _frames / ((now - _lastTick) / (double)Stopwatch.Frequency); _frames = 0; _lastTick = now;
        if (_syncPending) SyncBar();
        if (_engine.Warmer.Vram is { Alive: false }) ApplyVramTier();
        // the governor goes by frames that came late because of Ginkgo's own work (not Windows' copy to the other chip), and the card's time
        _player?.Tick(_slowOurs - _slowAtLastTick, View.Gpu?.GpuMs ?? 0);
        _slowAtLastTick = _diag ? 0 : _slowOurs;
        if (_engine.GifMaker is { } gm && gm.Stats.Running && _model is not null)
        {
            gm.CapMs = (int)(_settings.GifLengthSec * 1000);
            var gifIds = View.VisibleGifs().Where(i => i < _order.Length && _order[i] >= 0 && _order[i] < _model.Id.Length).Select(i => _model.Id[_order[i]]).ToList();
            if (gifIds.Count > 0) gm.Want(gifIds);
            // read the GIFs just off screen into memory, so they play as soon as they scroll in
            if (_player is { } pl && View.UsingGpu && View.Badges is { } bd && bd.Length > 0)
            {
                var vis = View.VisibleRange();
                int tier = pl.TierFor(View.TileTarget * VisualTreeHelper.GetDpi(View).PixelsPerDip);
                var ahead = new List<(long, int)>();
                void Add(int i) { if (i >= 0 && i < bd.Length && bd[i] == 1 && i < _order.Length && _order[i] >= 0 && _order[i] < _model.Id.Length) ahead.Add((_model.Id[_order[i]], tier)); }
                for (int i = vis.last + 1; i < Math.Min(bd.Length, vis.last + 400) && ahead.Count < 150; i++) Add(i);
                for (int i = vis.first - 1; i >= Math.Max(0, vis.first - 150) && ahead.Count < 200; i--) Add(i);
                pl.Prefetch(ahead);
            }
        }
        double tilePx = View.TileTarget * VisualTreeHelper.GetDpi(View).PixelsPerDip;
        _engine.Warmer.Preferred = tilePx <= 120 ? 0 : tilePx <= 200 ? 1 : 2;
        TickFavourites();
        TickDetails();
        if (View.HoverIndex < 0 && Stopwatch.GetTimestamp() > _favMessageUntil) StatusText.Text = StatusLine();
        if (_diag)
        {
            var (mr, _) = View.TakeWorst();
            DiagText.Text = $"{_fps:F0} fps | worst gap {_worstGap:F0} ms | slow {_slowFrames} (gc {_slowGc}, ours {_slowOurs}, other {_slowOther}) | gc pause {(GC.GetTotalPauseDuration() - _gcAtTick).TotalMilliseconds:F0} ms | draw {mr:F1} (prep {Perf.PrepMax:F1}, paint {Perf.PaintMax:F1}/{Perf.PaintedBands}) drain {Perf.DrainMax:F1} | {View.LastVisibleCount} tiles, cached {_loader?.CachedCount ?? 0}, pending {_loader?.PendingCount ?? 0} | GC {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} | read {_loader?.AvgReadMs ?? 0:F1} ms, decode {_loader?.AvgDecodeMs ?? 0:F1} ms{(_engine.FastCacheNote.Length > 0 ? " | " + _engine.FastCacheNote : "")} | thumbs in memory {_engine.Warmer.InMemory / 1073741824.0:F1} GB ({(_engine.Warmer.Total > 0 ? _engine.Warmer.Done * 100 / _engine.Warmer.Total : 0)}%{(_engine.Warmer.Running ? ", loading" : "")}) {_engine.Warmer.Note} | free RAM {CacheWarmer.AvailableBytes() / 1073741824.0:F1} GB" + GpuDiag() + VideoDiag() + FavDiag() + DetailsDiag();
            if (DiagFull.Visibility == Visibility.Visible) FillFullStatus();
            _slowAtLastTick = 0;
            _worstGap = 0; _slowFrames = _slowGc = _slowOurs = _slowOther = 0; _gcAtTick = GC.GetTotalPauseDuration(); Perf.Reset();
        }
        bool moving = _engine.Moving;
        MoveLayer.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;
        if (moving)
        {
            if (_moveStarted == 0) _moveStarted = Stopwatch.GetTimestamp();
            long total = Math.Max(1, _engine.MoveTotal);
            double f = _engine.MoveDone / (double)total, secs = (Stopwatch.GetTimestamp() - _moveStarted) / (double)Stopwatch.Frequency;
            MoveBar.Value = f;
            string left = f > 0.01 && secs > 1 ? $", about {Clock(secs / f - secs)} left" : "";
            MoveText.Text = $"{f * 100:F0}%   {_engine.MoveDone / 1073741824.0:F2} of {total / 1073741824.0:F2} GB   {Clock(secs)} so far{left}";
            if (_engine.MovingWhat == "cache")
            {
                MoveTitle.Text = "Moving the cache to " + _settings.PendingCacheMove;
                MoveNote.Text = "Asked for in Settings. Every thumbnail file is copied to the new folder and checked; the old ones are removed only after all of them arrived. Ginkgo opens when it is done.";
            }
        }
        else _moveStarted = 0;
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
        long sNow = Stopwatch.GetTimestamp();
        if (!Scroll.IsMouseOver && !Scroll.IsMouseCaptureWithin && (sNow - _lastSync) * 1000 < 120 * Stopwatch.Frequency) { _syncPending = true; return; }   // the bar is hidden: no need to move it every frame
        _lastSync = sNow; _syncPending = false;
        _syncing = true; View.Offset = e.NewValue; _syncing = false;
    }

    private long _lastSync; private bool _syncPending;
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
        if (Sections.IsKeyboardFocused && ViewerRoot.Visibility != Visibility.Visible)
        {
            if (Sections.HandleKey(e)) e.Handled = true;
            return;
        }
        if (KeysLayer.Visibility == Visibility.Visible)
        {
            if (e.Key is Key.Escape or Key.K or Key.OemQuestion) { KeysLayer.Visibility = Visibility.Collapsed; e.Handled = true; }
            return;
        }
        {
            var k0 = e.Key == Key.System ? e.SystemKey : e.Key;
            bool alt0 = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
            if (alt0 && k0 == Key.Left) { NavBack(); e.Handled = true; return; }
            if (alt0 && k0 == Key.Right) { NavForward(); e.Handled = true; return; }
            if (ViewerRoot.Visibility == Visibility.Visible && e.Key == Key.Back) { NavBack(); e.Handled = true; return; }
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && e.Key == Key.W) { Close(); e.Handled = true; return; }
        }
        if (ViewerRoot.Visibility == Visibility.Visible) { ViewerKey(e); return; }
        bool typing = Keyboard.FocusedElement is TextBox;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape)
        {
            if (CloseSticky()) { e.Handled = true; }
            else if (_fullscreen && !typing) { ToggleFullscreen(); e.Handled = true; }
            else if (typing) { SearchBox.Text = ""; View.Focus(); e.Handled = true; }
            return;
        }
        if (ctrl && e.Key == Key.B) { FocusSections(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.W) { Close(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.S) { OpenSettings(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.F) { RevealBar(); SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; return; }
        if (ctrl && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
        {
            Navigate(e.Key switch { Key.D1 => "Home", Key.D2 => "Browse", Key.D3 => "Favourites", _ => "Tools" });
            e.Handled = true; return;
        }
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (alt && key == Key.Left) { GoBack(); e.Handled = true; return; }
        if (alt && key == Key.Right) { GoForward(); e.Handled = true; return; }
        if (alt && key == Key.Up) { GoUp(); e.Handled = true; return; }
        if (typing) return;
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None) { GoBack(); e.Handled = true; return; }
        if (Keyboard.Modifiers != ModifierKeys.None && !ctrl) return;
        switch (e.Key)
        {
            case Key.Home: View.Offset = 0; e.Handled = true; break;
            case Key.End: View.Offset = double.MaxValue / 4; e.Handled = true; break;
            case Key.PageDown: View.ScrollBy(View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.PageUp: View.ScrollBy(-View.ActualHeight * 0.9); e.Handled = true; break;
            case Key.Enter:
            {
                int at = View.SelectedIndex >= 0 && View.SelectedIndex < _order.Length ? View.SelectedIndex : View.HoverIndex;
                if (IsFolderTile(at)) { OpenFolder(_page!.Tiles[at].Path); e.Handled = true; } else if (at >= 0) { OpenViewer(at); e.Handled = true; }
                break;
            }
            case Key.Left: MoveSelection(-1, 0); e.Handled = true; break;
            case Key.Right: MoveSelection(1, 0); e.Handled = true; break;
            case Key.Up: MoveSelection(0, -1); e.Handled = true; break;
            case Key.Down: MoveSelection(0, 1); e.Handled = true; break;
            case Key.Space:
                if (View.SelectedIndex >= 0 && IsVideoTile(View.SelectedIndex)) { ClickVideoTile(View.SelectedIndex); e.Handled = true; }
                break;
            case Key.OemQuestion: RevealBar(); SearchBox.Focus(); e.Handled = true; break;
            case Key.K: ShowKeys(false); e.Handled = true; break;
            case Key.S: OpenSettings(); e.Handled = true; break;
            case Key.T: OpenHoveredOrFirst(); e.Handled = true; break;
            case Key.O: RevealBar(); OnOptimise(this, e); e.Handled = true; break;
            case Key.Q: RevealBar(); OnQuick(this, e); e.Handled = true; break;
            case Key.F: case Key.F11: ToggleFullscreen(); e.Handled = true; break;
            case Key.F3: StepDiag(); e.Handled = true; break;
            case Key.M: FocusSections(); e.Handled = true; break;
            case Key.H: OnHideBar(this, e); e.Handled = true; break;
            case Key.G: GeneralBtn.IsChecked = GeneralBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.N: NsfwBtn.IsChecked = NsfwBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.V: VeryBtn.IsChecked = VeryBtn.IsChecked != true; OnFilterChanged(this, e); e.Handled = true; break;
            case Key.L: RowsBtn.IsChecked = RowsBtn.IsChecked != true; OnLayoutChanged(this, e); e.Handled = true; break;
            case Key.R: if (((SortBox.SelectedItem as ComboBoxItem)?.Tag as string) == "Random") { OnReshuffle(this, e); e.Handled = true; } break;
            case Key.Add: case Key.OemPlus: View.TileTarget *= 1.15f; e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: View.TileTarget /= 1.15f; e.Handled = true; break;
        }
    }

    // =========================================================================================================
    // window bar, side menu, shortcut list
    // =========================================================================================================

    private static bool IsInPopup(DependencyObject d)
    {
        for (var x = d; x != null; x = ParentOf(x)) if (x is System.Windows.Controls.Primitives.Popup || x.GetType().Name == "PopupRoot") return true;
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject d) =>
        d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private Point _titleDown; private bool _titleArmed;
    private FrameworkElement? _titleFrom;

    /// <summary>The title bar and the menu bar move the window: drag from anywhere that isn't a control (also out of maximised); double-click maximises.</summary>
    private void OnTitleDown(object s, MouseButtonEventArgs e)
    {
        var host = (FrameworkElement)s;
        for (var d = e.OriginalSource as DependencyObject; d != null && d != host; d = ParentOf(d))
            if (d is System.Windows.Controls.Primitives.ButtonBase or TextBox or Slider or ComboBox or System.Windows.Controls.Primitives.Thumb) return;
        if (e.ClickCount == 2) { OnMaximize(this, e); e.Handled = true; return; }
        _titleArmed = true; _titleDown = e.GetPosition(this); _titleFrom = host;
        host.CaptureMouse();
        e.Handled = true;
    }

    private void OnTitleMove(object s, MouseEventArgs e)
    {
        if (!_titleArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { OnTitleUp(s, null!); return; }
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _titleDown.X) < 4 && Math.Abs(p.Y - _titleDown.Y) < 4) return;
        _titleArmed = false;
        _titleFrom?.ReleaseMouseCapture();
        if (WindowState == WindowState.Maximized)
        {
            // come out of maximised with the window under the mouse, like other apps
            double frac = Math.Clamp(_titleDown.X / Math.Max(1, ActualWidth), 0, 1);
            var src = PresentationSource.FromVisual(this);
            var m2d = src?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            var screen = m2d.Transform(PointToScreen(_titleDown));
            WindowState = WindowState.Normal;
            Left = screen.X - Width * frac; Top = Math.Max(0, screen.Y - Math.Min(_titleDown.Y, 14));
        }
        try { DragMove(); } catch { }
    }

    // ---- controls that did not fit in the bar: the more button opens them in rows under the bar ----
    private void OnMoreBar(object s, RoutedEventArgs e) => BarRow.Expanded = !BarRow.Expanded;

    private void OnTitleUp(object s, MouseButtonEventArgs e) { _titleArmed = false; if (_titleFrom?.IsMouseCaptured == true) _titleFrom.ReleaseMouseCapture(); }

    private void OnMinimize(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object s, RoutedEventArgs e) => Close();

    private void OnStateChanged(object? s, EventArgs e)
    {
        bool max = WindowState == WindowState.Maximized;
        MaxBtn.Content = max ? "\uE923" : "\uE922";
        UpdateEdges();
        // windowed: Windows draws the outline and rounds the corners (Windows 11); maximised or full screen: no outline
        WindowEdge.BorderThickness = !max && !_fullscreen && !Theme.HasRoundedFrame ? new Thickness(1) : new Thickness(0);
    }

    // ---- a maximised or snapped window reaches past the visible screen by its (invisible) resize border: keep the content inside ----

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectI rect);
    private bool _edgeRecheck;

    /// <summary>How far a maximised window reaches past the monitor's work area on each side (in WPF units), or null if unknown.</summary>
    private Thickness? MaxOverhang(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var wr)) return null;
        var mi = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi)) return null;
        var src = PresentationSource.FromVisual(this)?.CompositionTarget;
        if (src is null) return null;
        var m = src.TransformFromDevice;
        double sx = m.M11, sy = m.M22;
        var w = mi.Work;
        double l = Math.Max(0, w.L - wr.L) * sx, t = Math.Max(0, w.T - wr.T) * sy, r = Math.Max(0, wr.R - w.R) * sx, b = Math.Max(0, wr.B - w.B) * sy;
        if (l > 40 || t > 40 || r > 40 || b > 40) return null;          // not what a maximised window looks like
        return new Thickness(l, t, r, b);
    }

    private void UpdateEdges()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        bool max = WindowState == WindowState.Maximized;
        // snapped to half or a quarter of the screen: Windows reports the window as normal, but it is not at its normal size
        var rb = RestoreBounds;
        bool snapped = !max && WindowState == WindowState.Normal && !rb.IsEmpty &&
                       (Math.Abs(rb.Width - ActualWidth) > 2 || Math.Abs(rb.Height - ActualHeight) > 2);
        Thickness pad = new(0);
        if (max && hwnd != IntPtr.Zero && !_fullscreen && MaxOverhang(hwnd) is { } over)
        {
            // maximised: keep the content exactly inside the screen's work area, measured from where the window really is
            // (the border sizes from the system don't match on every monitor and scale: that left a gap under the bottom bar)
            WindowEdge.Margin = over;
            if (!_edgeRecheck) { _edgeRecheck = true; Dispatcher.BeginInvoke(() => { _edgeRecheck = false; if (WindowState == WindowState.Maximized && MaxOverhang(hwnd) is { } o2) WindowEdge.Margin = o2; }, DispatcherPriority.Background); }
            return;
        }
        if ((max || snapped) && hwnd != IntPtr.Zero && !_fullscreen)
        {
            uint dpi = GetDpiForWindow(hwnd);
            if (dpi == 0) dpi = 96;
            double scale = dpi / 96.0;
            double bx = (GetSystemMetricsForDpi(32, dpi) + GetSystemMetricsForDpi(92, dpi)) / scale;    // frame + padded border
            double by = (GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi)) / scale;
            pad = max ? new Thickness(bx, by, bx, by) : new Thickness(bx, 0, bx, by);
        }
        WindowEdge.Margin = pad;
    }

    // ---- top bar: slides away on scroll down, back on scroll up; H hides it for good until shown again ----

    private bool BarPinned =>
        _folderPopup.IsOpen || (_optPopup?.IsOpen ?? false) || (_quickPopup?.IsOpen ?? false) || (_vfPopup?.IsOpen ?? false) || LibraryPopupOpen() || (_slidePopup?.IsOpen ?? false) || SavedPopup.IsOpen || Sections.IsKeyboardFocused || BarRow.Expanded || Banner.Visibility == Visibility.Visible || SearchBox.IsKeyboardFocused ||
        (TopBar.IsMouseOver && Stopwatch.GetTimestamp() > _ignoreOverUntil) || HoverStrip.IsMouseOver;

    private void ApplyBar()
    {
        bool want = BarPinned || (!_barManual && (!_barAuto || View.Offset < 30));      // at the top of the page the bar always shows
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
        if (off < 30 && _barAuto) { _barAuto = false; _barAccum = 0; ApplyBar(); return; }
        if (ViewerRoot.Visibility == Visibility.Visible || View.InRelayout) return;     // zooming or resizing moves the offset too, but isn't scrolling
        bool before = _barAuto;
        if (off < 30) { _barAuto = false; _barAccum = 0; }
        else if (d > 0) { _barAccum = Math.Max(0, _barAccum) + d; if (_barAccum > 90) _barAuto = true; }
        else if (d < 0) { _barAccum = Math.Min(0, _barAccum) + d; if (_barAccum < -50) _barAuto = false; }
        if (before != _barAuto) ApplyBar();
    }

    private void UpdateInset() => View.TopInset = _barManual ? 0 : (float)BarHost.ActualHeight;

    /// <summary>Hide, in steps: first the top bar, then the bottom bar too; the next press brings both back.</summary>
    private void OnHideBar(object s, RoutedEventArgs e)
    {
        if (!_barManual)
        {
            _barManual = true;
            _ignoreOverUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency;      // the mouse is still on the bar for a moment
            UpdateInset(); ApplyBar();
        }
        else if (StatusBar.Visibility == Visibility.Visible) SetBottomBar(false);
        else OnShowBar(s, e);
    }

    private void OnShowBar(object s, RoutedEventArgs e)
    {
        _barManual = false; _barAuto = false;
        SetBottomBar(true);
        UpdateInset(); ApplyBar();
    }

    private void SetBottomBar(bool show)
    {
        StatusBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _settings.StatusHidden = !show;
    }

    private void OnDiagMore(object s, RoutedEventArgs e)
    {
        bool open = DiagFull.Visibility != Visibility.Visible;
        DiagFull.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) FillFullStatus();
    }

    /// <summary>F3: once for the status line, twice for the full status, a third time to close both.</summary>
    private void StepDiag()
    {
        if (!_diag) { _diag = true; DiagFull.Visibility = Visibility.Collapsed; }
        else if (DiagFull.Visibility != Visibility.Visible) { DiagFull.Visibility = Visibility.Visible; FillFullStatus(); }
        else { _diag = false; DiagFull.Visibility = Visibility.Collapsed; }
        DiagText.Visibility = DiagMoreBtn.Visibility = _diag ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly (string[] words, Brush colour)[] DiagColours =
    {
        (new[] { "fps", "worst gap", "slow", "gc pause", "draw", "GC " }, Frozen("#E0B060")),                    // frames and timing
        (new[] { "tiles", "read", "pending" }, Frozen("#7FC8F8")),                                                // loading
        (new[] { "thumbs", "free RAM", "moved", "gave" }, Frozen("#8FE6A8")),                                     // memory
        (new[] { "gifs", "gif renditions" }, Frozen("#FF9EC7")),                                                  // GIFs
        (new[] { "sound", "videos" }, Frozen("#FFD08A")),                                                          // video and sound
        (new[] { "renderer", "vram", "uploads", "gpu", "device" }, Frozen("#C9A8FF")),                            // the graphics card
    };
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly Brush DiagSep = Frozen("#5A5A66"), DiagOther = Frozen("#C8C8D0");

    /// <summary>The full status as flowing text: items one after another with a separator, coloured by kind.</summary>
    private void FillFullStatus()
    {
        DiagFullText.Inlines.Clear();
        var items = DiagText.Text.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int k = 0; k < items.Length; k++)
        {
            if (k > 0) DiagFullText.Inlines.Add(new System.Windows.Documents.Run("  ·  ") { Foreground = DiagSep });
            var it = items[k];
            var colour = DiagColours.FirstOrDefault(c => c.words.Any(w => it.StartsWith(w, StringComparison.OrdinalIgnoreCase) || it.Contains(" " + w + " "))).colour ?? DiagOther;
            DiagFullText.Inlines.Add(new System.Windows.Documents.Run(it) { Foreground = colour });
        }
    }

    private void OnDiagCopy(object s, RoutedEventArgs e) { try { Clipboard.SetText(DiagText.Text); StatusText.Text = "Status copied."; } catch { } }

    private void RevealBar() { if (_barManual || _barAuto) { _barManual = false; _barAuto = false; UpdateInset(); } ApplyBar(); }

    // ---- the section switch ----

    /// <summary>Ctrl+B or M: the keyboard goes to the section switch (1-4 or the arrows move; Enter, Esc, Ctrl+B or M come back).</summary>
    private void FocusSections()
    {
        RevealBar();
        if (!Sections.IsVisible) return;
        Keyboard.Focus(Sections);
    }

    private void InitSections()
    {
        Sections.Picked += Navigate;
        Sections.Done += () => View.Focus();
    }

    private void OnNav(object s, RoutedEventArgs e) => Navigate((string)((FrameworkElement)s).Tag);

    private void Navigate(string where)
    {
        if (where is "Home" or "Browse" or "Favourites" or "Tools")
        {
            if (where != _section) _ = GoSection(where);                       // back where it was left
            else if (where == "Browse" && _folder.Length > 0) _ = Go(where, ""); // Browse again: the top
            else UpdateNavUi();
            return;
        }
        UpdateNavUi();
        StatusText.Text = $"{where} is coming in a later build.";
    }

    /// <summary>The arrow keys move a highlight through the tiles (starting at the one under the mouse, or the first on screen).</summary>
    private void MoveSelection(int dx, int dy)
    {
        if (_order.Length == 0) return;
        int cur = View.SelectedIndex;
        double cover = BarHost.Visibility == Visibility.Visible ? Math.Max(0, BarHost.ActualHeight + BarShift.Y) : 0;
        if (cur < 0 || cur >= _order.Length) { cur = View.HoverIndex >= 0 ? View.HoverIndex : Math.Max(0, View.VisibleRange().first); View.SelectedIndex = cur; View.RevealIndex(cur, cover); return; }
        int next = View.Neighbour(cur, dx, dy);
        if (next < 0) return;
        View.SelectedIndex = next;
        View.RevealIndex(next, cover);
        StatusText.Text = IsFolderTile(next) ? FolderHoverText(next) : next < _paths.Length ? HoverText(next) : StatusText.Text;
    }

    private void OnAppearanceBtn(object s, RoutedEventArgs e) => OpenAppearance();

    /// <summary>The button look window: changes show at once; Save keeps them.</summary>
    /// <summary>Appearance: themes, colours, fonts, icons and backgrounds.</summary>
    private AppearanceWindow? _appearance;
    public void OpenAppearance() => OpenAppearance(null);

    /// <summary>The Appearance window (one at a time; it stays open beside the main window), at a page if asked.</summary>
    public void OpenAppearance(string? page)
    {
        if (_appearance is null || !_appearance.IsLoaded)
        {
            _appearance = new AppearanceWindow(_settings, ThemeApplied, LooksApplied) { Owner = this };
            _appearance.Closed += (_, _) => { _appearance = null; SaveSettings(); };
            _appearance.Show();
        }
        else { if (_appearance.WindowState == WindowState.Minimized) _appearance.WindowState = WindowState.Normal; _appearance.Activate(); }
        if (page is not null) _appearance.ShowPage(page);
    }

    /// <summary>The theme changed: the grid repaints, and the window's own frame follows.</summary>
    public void ThemeApplied()
    {
        ApplyLooks();                 // (the backgrounds and the logo follow the theme's colours)
        SaveSettings();
    }

    public void ResetSettings()
    {
        var fresh = new AppSettings
        {
            LibraryRoot = _settings.LibraryRoot, CacheDir = _settings.CacheDir, FastCacheDir = _settings.FastCacheDir, UseFastCache = _settings.UseFastCache,
            ExcludedFolders = _settings.ExcludedFolders, Theme = _settings.Theme, FolderRulesVersion = 3,
        };
        fresh.Save();
        _skipSave = true;                                          // closing must not write the old settings back
    }
    private bool _skipSave;

    public void ClearSavedSearches() { _saved.Clear(); SavedSearches.Save(_saved); RebuildSavedList(); }
    public void ClearCovers() { _covers.Clear(); _covers.Save(); if (InBrowse) BuildView(true); }
    public void ClearVideoPositions() { ResetVideoMemory(); View.Refresh(); }
    public Task ReloadAfterFolders() => ReloadModel(true);

    public void OpenLook() => OpenAppearance("Buttons");

    public void OpenLookOld()
    {
        var w = new ButtonLookWindow(_settings.Look, () => ThemeKit.Apply(_settings.Theme, _settings.Look)) { Owner = this };
        if (w.ShowDialog() == true) SaveSettings();
    }

    // ---- keyboard shortcut list ----

    private static readonly (string keys, string what)[] GridKeys =
    {
        ("Ctrl+B  or  M", "Open or close the menu"), ("S  or  Ctrl+S", "Settings"), ("T", "Open the picture under the mouse (or the first one)"), ("O", "Optimisations (memory, GIFs)"), ("Q", "Quick toggles (videos)"), ("F  or  F11", "Full screen (Esc to leave)"), ("Ctrl+W", "Close Ginkgo"), ("H", "Hide or show the top bar"), ("/  or  Ctrl+F", "Search"),
        ("G   N   V", "Show General / NSFW / Very files"), ("L", "Equal columns  or  equal rows"), ("+   -   or  Ctrl + wheel", "Picture size"),
        ("R", "Reshuffle (when sorted randomly)"), ("Home   End", "Top / bottom"), ("Page Up   Page Down", "Scroll one screen"),
        ("Enter  or  click", "Open the picture under the mouse"), ("Middle button", "Scroll by moving the mouse"),
        ("Ctrl+1 ... 4", "Home, Browse, Favourites, Tools"), ("Alt+Left  or  Backspace", "Back (Browse)"), ("Alt+Right   Alt+Up", "Forward / up one folder"), ("K", "This list"), ("Esc", "Close / clear the search"),
    };
    private static readonly (string keys, string what)[] ViewerKeys =
    {
        ("Left   Right   (Space)", "Previous / next picture"), ("Home   End", "First / last picture"), ("Mouse wheel   Up   Down", "Zoom in and out (Up and Down: volume on a video)"),
        ("Drag", "Move a zoomed picture"), ("1", "Fit to window"), ("2", "Actual size (100%)"), ("I", "More or less information"),
        ("Click off the picture", "Close the viewer"), ("H", "Show or hide the information box (stays until you press it again)"), ("T  or  mouse back", "Close the viewer"), ("F  or  F11", "Full screen"), ("K", "This list"), ("Esc  or  double-click", "Leave full screen, then back to the grid"),
        ("Space  or  click the video", "Video: play / pause"), (",   .", "Video: back / forward 3 seconds"), ("Shift + Left   Right", "Video: back / forward 5 seconds"),
        ("9   0", "Video: back / forward a frame"), ("Up   Down", "Video: volume (3% a press, hold to keep going)"), ("M", "Video: sound on / off"),
        ("[   ]", "Video: slower / faster"), ("L", "Video: loop on / off"),
        ("Space   Left   Right   Esc", "Slideshow: pause or carry on, step, stop (start it with the button beside the star)"),
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
    private WindowState _prevState; private WindowStyle _prevStyle; private ResizeMode _prevResize;
    private readonly Dictionary<string, BitmapSource> _vCache = new();
    private readonly Queue<string> _vCacheOrder = new();

    private long _viewerOpenedAt, _viewerClosedAt;

    private void OpenViewer(int index) => OpenViewer(index, remember: true);

    /// <summary>T: the picture under the mouse in the viewer, or the first one when the mouse isn't over a picture.</summary>
    private void OpenHoveredOrFirst()
    {
        int i = View.HoverIndex;
        if (i < _firstFile || i >= _paths.Length || IsFolderTile(i)) i = _firstFile;
        if (i >= _firstFile && i < _paths.Length) OpenViewer(i);
    }

    /// <summary>Mouse back (and Alt+Left): in the viewer it closes it and returns to where you were, however many pictures you went through.</summary>
    private void NavBack() { if (ViewerOpen) { CloseViewer(); UpdateNavButtons(); return; } GoBack(); }
    /// <summary>Mouse forward (and Alt+Right): does nothing in the viewer, and never reopens a closed viewer.</summary>
    private void NavForward() { if (ViewerOpen) return; GoForward(); }

    private void OpenViewer(int index, bool remember)
    {
        if (index < _firstFile || index >= _paths.Length) return;
        if (remember) Remember();                 // Back returns to the grid
        ViewerRoot.Visibility = Visibility.Visible;
        ViewerRoot.Focus();
        _viewerOpenedAt = Stopwatch.GetTimestamp();
        VideoViewerOpening(index);
        ShowIndex(index, remember: false);
    }

    private void CloseViewer() => CloseViewer(fromUser: true);

    private void CloseViewer(bool fromUser)
    {
        StopSlideshow();
        if (fromUser) ForgetViewerSteps();
        _viewerClosedAt = Stopwatch.GetTimestamp();
        _vToken++;
        ViewerRoot.Visibility = Visibility.Collapsed;
        StopViewerGif();
        Pic.Source = null;
        View.ScrollToIndex(_vIndex);
        View.Focus();
        VideoViewerClosed();
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
        string count = $"{_vIndex - _firstFile + 1:N0} / {_paths.Length - _firstFile:N0}";
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
        bool hidden = _infoHidden && !_viewerVideo;          // a video's box comes and goes by itself (see the video bar)
        InfoBox.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        if (hidden || _model is null || _vIndex >= _order.Length) return;
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
        if (_viewerVideo)
        {
            // bottom left as always, beside the video bar: narrower and taller when there's little room; above the bar when there's none
            double barLeft = VideoBar.IsVisible && VideoBar.ActualWidth > 0 ? VideoBar.TranslatePoint(new Point(0, 0), ViewerRoot).X : HW;
            double room = barLeft - 12 - 10;
            if (room >= 110)
            {
                s = TryMeasure(0, Math.Min(cap + 20, room));
                if (s.Width > room || s.Height > HH - 30) s = TryMeasure(2, room);
                Put(12, 18);
            }
            else { TryMeasure(0, Math.Min(cap + 20, HW - 24)); Put(12, 18 + Math.Max(48, VideoBar.ActualHeight) + 8); }
            return;
        }
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

    private void ShowIndex(int index) => ShowIndex(index, remember: true);

    private void ShowIndex(int index, bool remember)
    {
        index = Math.Clamp(index, _firstFile, _paths.Length - 1);
        // (pictures you step through aren't remembered: Back closes the viewer)
        if (index != _viewerFirstIndex) _viewerLeftFirst = true;      // other videos start from the beginning from now on
        _vIndex = index;
        ResetZoom();
        int row = _order[_vIndex];
        UpdateInfo(row);
        UpdateViewerStar();
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
        if (m.Kind[row] == (byte)MediaKind.Video)
        {
            if (full && _viewerVideo) return;                 // zooming a video: nothing to reload
            StopViewerGif();
            LeaveViewerVideo();
            await LoadVideo(index, token, fast);
            return;
        }
        LeaveViewerVideo();
        if (m.Kind[row] != (byte)MediaKind.Gif || _vGifIndex != index) StopViewerGif();
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
        if (m.Kind[row] == (byte)MediaKind.Gif) StartViewerGif(index, row); else StopViewerGif();
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
        if (index < _firstFile || index >= _paths.Length) return;
        int row = _order[index];
        var m = _model!;
        if (m.Kind[row] == (byte)MediaKind.Video) return;
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
        // a video zooms into the middle of the window; a picture into where the mouse is
        var at = _viewerVideo ? new Point(PicHost.ActualWidth / 2, PicHost.ActualHeight / 2) : e.GetPosition(PicHost);
        ZoomAt(at, _zoom * Math.Pow(1.18, e.Delta / 120.0));
        e.Handled = true;
    }

    /// <summary>Right-click in the viewer: the same menu as on the tile (favourites, folders, Explorer, copy path).</summary>
    private void OnViewerRightClick(object s, MouseButtonEventArgs e)
    {
        if (_vIndex < _firstFile || _vIndex >= _order.Length) return;
        ShowTileMenu(_vIndex, inViewer: true);
        e.Handled = true;
    }

    private void OnViewerDown(object s, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            // the second click of the double-click that opened the viewer must not close it again
            _vDownValid = false;
            if ((Stopwatch.GetTimestamp() - _viewerOpenedAt) * 1000 > 600 * Stopwatch.Frequency) CloseViewer();
            e.Handled = true; return;
        }
        ViewerClickDown(e);
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

    private void OnViewerUp(object s, MouseButtonEventArgs e) { _dragging = false; PicHost.ReleaseMouseCapture(); ViewerClickUp(e); }

    private void ViewerKey(KeyEventArgs e)
    {
        if (SlideKey(e)) { e.Handled = true; return; }
        if (VideoKey(e)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.Escape: if (_fullscreen) ToggleFullscreen(); else CloseViewer(); break;
            case Key.Right: case Key.PageDown: case Key.Space: if (_vIndex + 1 < _paths.Length) ShowIndex(_vIndex + 1); break;
            case Key.Left: case Key.PageUp: if (_vIndex > _firstFile) ShowIndex(_vIndex - 1); break;
            case Key.Up: ZoomAt(new Point(PicHost.ActualWidth / 2, PicHost.ActualHeight / 2), _zoom * 1.18); break;
            case Key.Down: ZoomAt(new Point(PicHost.ActualWidth / 2, PicHost.ActualHeight / 2), _zoom / 1.18); break;
            case Key.Home: ShowIndex(_firstFile); break;
            case Key.End: ShowIndex(_paths.Length - 1); break;
            case Key.F: case Key.F11: ToggleFullscreen(); break;
            case Key.K: case Key.OemQuestion: ShowKeys(true); break;
            case Key.H:
                if (_viewerVideo) { _infoPinnedVideo = !_infoPinnedVideo; if (!_infoPinnedVideo) _infoFlashUntil = 0; else InfoBox.Opacity = 1; }
                else { _infoHidden = !_infoHidden; UpdateInfo(_order[_vIndex]); }
                break;
            case Key.I: _infoMore = !_infoMore; if (_viewerVideo) { _infoPinnedVideo = true; InfoBox.Opacity = 1; } else _infoHidden = false; UpdateInfo(_order[_vIndex]); break;
            case Key.T: CloseViewer(); break;
            case Key.D1: case Key.NumPad1: ResetZoom(); break;
            case Key.D2: case Key.NumPad2:
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RectI { public int L, T, R, B; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public RectI Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr mon, ref MonitorInfo info);
    private Rect _prevBounds;

    /// <summary>
    /// Full screen covers the whole monitor, taskbar included. A maximised window can't do that here (the custom frame keeps a
    /// maximised window inside the work area), so the window is made a plain, frameless window exactly the size of the monitor.
    /// </summary>
    private void ToggleFullscreen()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!_fullscreen)
        {
            _prevState = WindowState; _prevStyle = WindowStyle; _prevResize = ResizeMode;
            _prevBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            var mi = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi);
            _fullscreen = true;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var tl = m.Transform(new Point(mi.Monitor.L, mi.Monitor.T));
            var br = m.Transform(new Point(mi.Monitor.R, mi.Monitor.B));
            Left = tl.X; Top = tl.Y; Width = br.X - tl.X; Height = br.Y - tl.Y;
            Theme.FullScreenFrame(this, true, 0x4A, 0x4A, 0x54);
            Activate();
        }
        else
        {
            _fullscreen = false;
            WindowStyle = _prevStyle; ResizeMode = _prevResize;
            Left = _prevBounds.Left; Top = _prevBounds.Top; Width = _prevBounds.Width; Height = _prevBounds.Height;
            WindowState = _prevState;
            Theme.FullScreenFrame(this, false, 0x4A, 0x4A, 0x54);
        }
        TitleBar.Visibility = _fullscreen ? Visibility.Collapsed : Visibility.Visible;
        WindowEdge.BorderThickness = new Thickness(0);
        UpdateEdges();
        if (!_fullscreen) OnStateChanged(null, EventArgs.Empty);
    }
}
