using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// Layouts (columns, equal rows, grid), the scrubber's label, each section's default view, and saved searches.
/// </summary>
public partial class MainWindow
{
    private List<ViewState> _saved = new();

    private void InitViews()
    {
        _loading = true;
        GridLayoutBtn.IsChecked = _settings.GridLayout; FitBtn.IsChecked = _settings.GridFit;
        _loading = false;
        ApplyLayoutToView();
        View.HoverDetails = i => _settings.HoverDetails && RowOfTile(i) is int r and >= 0 && _model is { } m
            ? $"{m.W[r]} x {m.H[r]}  ·  {Ginkgo.App.BrowsePage.SizeText(m.Size[r])}" : null;
        // the scrubber: dragging the scroll bar shows where you are in the list
        Scroll.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => { _scrubbing = true; UpdateScrub(); }));
        Scroll.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler((_, _) => UpdateScrub()));
        Scroll.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => { _scrubbing = false; ScrubBubble.Visibility = Visibility.Collapsed; }));
        View.ScrollChanged += () => { if (_scrubbing) UpdateScrub(); };
        _saved = SavedSearches.Load();
        RebuildSavedList();
    }

    // ---- layouts ----

    /// <summary>"Masonry" (columns of equal width), "Rows" (rows of equal height) or "Grid" (square cells).</summary>
    private string CurrentLayout => _settings.GridLayout ? "Grid" : _settings.RowsLayout ? "Rows" : "Masonry";

    private void SetLayout(string layout)
    {
        _settings.GridLayout = layout == "Grid";
        _settings.RowsLayout = layout == "Rows";
        ApplyLayoutToView();
        if (_settings.GridLayout != _cropApplied) BuildView(true);           // cropping is set up when the view is built
    }

    private bool _cropApplied;

    private void ApplyLayoutToView()
    {
        bool was = _loading; _loading = true;
        RowsBtn.IsChecked = _settings.RowsLayout && !_settings.GridLayout;
        GridLayoutBtn.IsChecked = _settings.GridLayout;
        FitBtn.IsChecked = _settings.GridFit;
        FitBtn.Visibility = _settings.GridLayout ? Visibility.Visible : Visibility.Collapsed;
        _loading = was;
        View.GridFit = _settings.GridFit;
        View.GridMode = _settings.GridLayout;
        View.RowsMode = _settings.RowsLayout && !_settings.GridLayout;
        View.Gap = _settings.Spacing;
    }

    private void OnGridLayout(object s, RoutedEventArgs e) { if (!_loading) SetLayout(GridLayoutBtn.IsChecked == true ? "Grid" : _settings.RowsLayout ? "Rows" : "Masonry"); }
    private void OnGridFit(object s, RoutedEventArgs e) { if (_loading) return; _settings.GridFit = FitBtn.IsChecked == true; ApplyLayoutToView(); BuildView(true); }

    /// <summary>Grid without Fit: a file's picture cropped from the middle to fill its square cell (called from the loading threads).</summary>
    private static ThumbLoader.Crop? GridCrop(int i, int[] order, LibraryModel m, int firstFile, double tilePx)
    {
        if (i < firstFile || i >= order.Length || order[i] < 0) return null;
        int row = order[i];
        float aspect = Ginkgo.Data.ThumbMaker.ClampAspect(m.W[row], m.H[row]);
        // how much wider than the cell the picture is decoded, so the crop stays sharp (the stored thumbnail when that is enough)
        float factor = Math.Max(1f, aspect);
        if (tilePx * factor <= 480) factor = (float)Math.Min(factor, 320 / Math.Max(1, tilePx));
        return new ThumbLoader.Crop(1f, 0.5f, 0.5f, Math.Max(1f, factor));
    }

    /// <summary>Changes the space between tiles (from Settings).</summary>
    public void ApplySpacing() { View.Gap = _settings.Spacing; }

    // ---- the scrubber ----

    private bool _scrubbing;

    private void UpdateScrub()
    {
        if (!_scrubbing || _model is not { } m || _order.Length == 0) { ScrubBubble.Visibility = Visibility.Collapsed; return; }
        int i = View.TileAtContent(View.Offset + View.ActualHeight / 2);
        if (i < 0) return;
        string where = $"{Math.Max(0, i - _firstFile) + 1:N0} of {Math.Max(0, _order.Length - _firstFile):N0}";
        string extra = "";
        if (i < _order.Length && _order[i] >= 0)
        {
            int r = _order[i];
            string sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
            extra = sort switch
            {
                "Name" => "  ·  " + System.IO.Path.GetFileName(m.Path[r]),
                "Folder" => "  ·  " + (m.Display[r].Length == 0 ? "(library)" : m.Display[r]),
                "Size" => "  ·  " + BrowsePage.SizeText(m.Size[r]),
                "Width" or "Height" or "Megapixels" => $"  ·  {m.W[r]} x {m.H[r]}",
                "Duration" => m.Kind[r] == (byte)MediaKind.Video ? "  ·  " + Clock(m.Duration[r]) : "",
                "Random" => "",
                _ => "  ·  " + DateTimeOffset.FromUnixTimeSeconds(m.DateOf(r, sort)).ToLocalTime().ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            };
        }
        ScrubText.Text = where + extra;
        ScrubBubble.Visibility = Visibility.Visible;
        // beside the scroll bar's thumb
        double frac = Scroll.Maximum > 0 ? Scroll.Value / Scroll.Maximum : 0;
        double track = Math.Max(0, Scroll.ActualHeight - 40);
        ScrubBubble.Margin = new Thickness(0, 20 + frac * track - 12, Scroll.ActualWidth + 10, 0);
    }

    // ---- defaults ----

    private string DefaultKey => ViewState.DefaultKey(_section, _folder);
    private static string KeyName(string key) => key == "Folder" ? "inside a folder" : key;

    /// <summary>How this section looks and sorts now (a default keeps no folder choices or content switches).</summary>
    private ViewState CaptureLook()
    {
        bool inFolder = InBrowse && _folder.Length > 0;
        var v = new ViewState
        {
            Section = _section, Folder = _folder,
            Images = ImagesBtn.IsChecked == true, Gifs = GifsBtn.IsChecked == true, Videos = VideosBtn.IsChecked == true,
            Search = SearchBox.Text, Filters = _settings.Filters.Copy(),
            Layout = CurrentLayout, GridFit = _settings.GridFit, TileSize = View.TileTarget, Spacing = _settings.Spacing,
        };
        if (!InBrowse || inFolder)
        {
            v.Sort = (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
            v.Reverse = SortRevBtn.IsChecked == true;
            v.RandomSeed = _randomSeed;
        }
        if (InBrowse)
        {
            var p = Prefs(_folder);
            v.FolderSort = p.FolderSort; v.FolderReverse = p.FolderReverse;
            v.BrowseDetails = _settings.BrowseDetails.ToList(); v.CoverGrid = _settings.BrowseCoverGrid; v.OwnOnly = _settings.BrowseOwnOnly;
        }
        return v;
    }

    /// <summary>Everything about the view, for a saved search: the look plus folder choices and content switches.</summary>
    private ViewState CaptureAll(string name)
    {
        var v = CaptureLook();
        v.Name = name;
        v.General = GeneralBtn.IsChecked == true; v.Nsfw = NsfwBtn.IsChecked == true; v.Very = VeryBtn.IsChecked == true;
        v.OffFolders = _settings.OffFolders.ToList(); v.NsfwOff = _settings.NsfwOff.ToList(); v.VeryOff = _settings.VeryOff.ToList();
        v.Standalone = _settings.Standalone.ToList(); v.RarelyUsed = _settings.RarelyUsed.ToList();
        v.Sort ??= (SortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "FirstSeen";
        v.Reverse ??= SortRevBtn.IsChecked == true;
        v.RandomSeed ??= _randomSeed;
        return v;
    }

    /// <summary>Puts a view back: what it says, and leaves alone what it doesn't. Goes to its section and folder when asked.</summary>
    private async Task ApplyView(ViewState v, bool go)
    {
        bool was = _loading; _loading = true;
        try
        {
            if (v.General is bool g) { GeneralBtn.IsChecked = g; _settings.ShowGeneral = g; }
            if (v.Nsfw is bool n) NsfwBtn.IsChecked = n;
            if (v.Very is bool vy) VeryBtn.IsChecked = vy;
            if (v.Images is bool im) { ImagesBtn.IsChecked = im; _settings.ShowImages = im; }
            if (v.Gifs is bool gf) { GifsBtn.IsChecked = gf; _settings.ShowGifs = gf; }
            if (v.Videos is bool vd) { VideosBtn.IsChecked = vd; _settings.ShowVideos = vd; }
            if (v.OffFolders is { } off) _settings.OffFolders = off.ToList();
            if (v.NsfwOff is { } no) _settings.NsfwOff = no.ToList();
            if (v.VeryOff is { } vo) _settings.VeryOff = vo.ToList();
            if (v.Standalone is { } sa) _settings.Standalone = sa.ToList();
            if (v.RarelyUsed is { } ru) _settings.RarelyUsed = ru.ToList();
            if (v.Filters is { } f) _settings.Filters = f.Copy();
            if (v.Layout is { } lay) { _settings.GridLayout = lay == "Grid"; _settings.RowsLayout = lay == "Rows"; }
            if (v.GridFit is bool gfit) _settings.GridFit = gfit;
            if (v.Spacing is float sp) _settings.Spacing = sp;
            if (v.TileSize is float ts && ts > 0) { _settings.TileSize = ts; View.TileTarget = ts; }
            if (v.RandomSeed is int seed) _randomSeed = seed;
            if (v.BrowseDetails is { } bd) _settings.BrowseDetails = bd.ToList();
            if (v.CoverGrid is bool cg) { _settings.BrowseCoverGrid = cg; GridBtn.IsChecked = cg; }
            if (v.OwnOnly is bool oo) { _settings.BrowseOwnOnly = oo; OwnOnlyBtn.IsChecked = oo; }
            string section = go ? v.Section : _section, folder = go ? v.Folder : _folder;
            if (section == "Browse")
            {
                var p = PrefsForEdit(folder);
                if (v.FolderSort is { } fs) p.FolderSort = fs;
                if (v.FolderReverse is bool fr) p.FolderReverse = fr;
                if (folder.Length > 0) { if (v.Sort is { } so) p.FileSort = so; if (v.Reverse is bool re) p.FileReverse = re; }
            }
            else { if (v.Sort is { } so) _settings.Sort = so; if (v.Reverse is bool re) _settings.SortReverse = re; }
            if (v.Search is { } q && !go) SearchBox.Text = q;
            _filters.Load(_settings.Filters, (_model ?? _libModel)?.Formats() ?? new());
        }
        finally { _loading = was; }
        ApplyLayoutToView();
        UpdateChips(); UpdateLevelLabels(); UpdateFoldersLabel();
        ShuffleBtn.Visibility = (v.Sort ?? _settings.Sort) == "Random" ? Visibility.Visible : Visibility.Collapsed;
        if (go && (v.Section != _section || v.Folder != _folder))
        {
            await Go(v.Section, v.Folder);
            if (!string.IsNullOrEmpty(v.Search)) { SearchBox.Text = v.Search; }
        }
        else { ApplyFolderPrefs(); await BuildViewAsync(false, 0); }
        SaveSettings();
    }

    private async void OnReturnToDefault(object s, RoutedEventArgs e)
    {
        ((ToggleButton)s).IsChecked = false;
        SetSidebar(false);
        string key = DefaultKey;
        var v = _settings.Defaults.TryGetValue(key, out var d) ? d.Copy() : ViewState.BuiltIn(key);
        await ApplyView(v, go: false);
        StatusText.Text = _settings.Defaults.ContainsKey(key) ? $"Back to your default for {KeyName(key)}." : $"Back to Ginkgo's default for {KeyName(key)}.";
    }

    private void OnSaveAsDefault(object s, RoutedEventArgs e)
    {
        ((ToggleButton)s).IsChecked = false;
        string key = DefaultKey;
        var v = CaptureLook();
        v.Search = "";                                   // a default starts with nothing typed
        _settings.Defaults[key] = v;
        SaveSettings();
        SetSidebar(false);
        StatusText.Text = $"Saved as the default for {KeyName(key)}. Return to default brings it back.";
    }

    // ---- saved searches ----

    private void OnSaveSearch(object s, RoutedEventArgs e)
    {
        string suggested = _section == "Browse" && _folder.Length > 0 ? FolderPaths.Name(_folder) : _section;
        if (SearchBox.Text.Trim().Length > 0) suggested += " · " + SearchBox.Text.Trim();
        var name = AskName("Save this view", "Name for this saved search:", suggested);
        if (name is null) return;
        _saved.Add(CaptureAll(name));
        SavedSearches.Save(_saved);
        RebuildSavedList();
        StatusText.Text = $"Saved “{name}”.";
    }

    private void RebuildSavedList()
    {
        SavedList.Children.Clear();
        SavedEmpty.Visibility = _saved.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int k = 0; k < _saved.Count; k++)
        {
            var v = _saved[k]; int at = k;
            var item = new ToggleButton { Style = (Style)FindResource("SideItem"), ToolTip = Describe(v) };
            var dp = new DockPanel();
            dp.Children.Add(new TextBlock { Text = "", FontFamily = ThemeKit.IconFamily, Width = 30, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0xB8, 0xC2)) });
            dp.Children.Add(new TextBlock { Text = v.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            item.Content = dp;
            item.Click += async (_, _) => { item.IsChecked = false; SetSidebar(false); await ApplyView(_saved[at].Copy(), go: true); StatusText.Text = $"“{_saved[at].Name}”"; };
            item.MouseRightButtonUp += (_, ev) => { SavedMenu(at); ev.Handled = true; };
            SavedList.Children.Add(item);
        }
    }

    private static string Describe(ViewState v)
    {
        var parts = new List<string> { v.Section == "Browse" ? (v.Folder.Length == 0 ? "Browse" : "Browse: " + v.Folder) : v.Section };
        if (!string.IsNullOrEmpty(v.Search)) parts.Add($"search “{v.Search}”");
        if (v.Filters is { } f) parts.AddRange(FilterPanel.Chips(f).Select(c => c.text));
        if (v.Sort is { } s) parts.Add("by " + s + (v.Reverse == true ? " (reversed)" : ""));
        if (v.Layout is { } l) parts.Add(l == "Masonry" ? "columns" : l == "Rows" ? "equal rows" : "grid");
        return string.Join("  ·  ", parts) + "\nRight-click to update, rename, move or delete.";
    }

    private void SavedMenu(int at)
    {
        var menu = new ContextMenu();
        void Item(string text, Action act, bool enabled = true) { var it = new MenuItem { Header = text, IsEnabled = enabled }; it.Click += (_, _) => { act(); SavedSearches.Save(_saved); RebuildSavedList(); }; menu.Items.Add(it); }
        Item("Update with this view", () => { string name = _saved[at].Name; _saved[at] = CaptureAll(name); StatusText.Text = $"“{name}” updated."; });
        Item("Rename…", () => { if (AskName("Rename", "New name:", _saved[at].Name) is { } n) _saved[at].Name = n; });
        Item("Move up", () => (_saved[at - 1], _saved[at]) = (_saved[at], _saved[at - 1]), at > 0);
        Item("Move down", () => (_saved[at + 1], _saved[at]) = (_saved[at], _saved[at + 1]), at < _saved.Count - 1);
        menu.Items.Add(new Separator());
        Item("Delete", () => { string name = _saved[at].Name; _saved.RemoveAt(at); StatusText.Text = $"“{name}” deleted."; });
        menu.IsOpen = true;
    }

    /// <summary>A small box asking for a name (null when cancelled or left empty).</summary>
    private string? AskName(string title, string prompt, string start)
    {
        var box = new TextBox { Text = start, Margin = new Thickness(0, 6, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = prompt, Foreground = (Brush)FindResource("Text") });
        panel.Children.Add(box); panel.Children.Add(buttons);
        var w = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = ThemeKit.Brush("Bar"), ShowInTaskbar = false,
        };
        Theme.DarkTitleBar(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (w.ShowDialog() != true) return null;
        string t = box.Text.Trim();
        return t.Length == 0 ? null : t;
    }
}
