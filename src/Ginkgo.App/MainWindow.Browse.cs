using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>Browse: folders as tiles, opening a folder, breadcrumbs, back and forward, covers.</summary>
public partial class MainWindow
{
    private string _section = "Home";
    private string _folder = "";
    private int _firstFile;                 // tiles before this index are folders
    private BrowsePage? _page;
    private readonly CoverStore _covers = CoverStore.Load();
    private long _navAt;
    /// <summary>A place to go back to: a section and folder with its scroll position, and the picture open in the viewer (-1 = none).</summary>
    private sealed record NavState(string Section, string Folder, double Offset, long ViewerId = -1);
    private readonly List<NavState> _back = new(), _fwd = new();

    private bool InBrowse => _section == "Browse";
    private bool ViewerOpen => ViewerRoot.Visibility == Visibility.Visible;
    private bool IsFolderTile(int index) => index >= 0 && index < _firstFile;

    private void InitBrowse()
    {
        OwnOnlyBtn.IsChecked = _settings.BrowseOwnOnly;
        GridBtn.IsChecked = _settings.BrowseCoverGrid;
        View.ItemClicked += i =>
        {
            if (RecentlyNavigated()) return;
            if (CollapsedSetAt(i) is not null) { ToggleSet(i); return; }        // a stacked set opens with a click
            if (IsFolderTile(i)) OpenFolder(_page!.Tiles[i].Path);
            else if (IsVideoTile(i)) ClickVideoTile(i);          // a video plays in its tile
            else if (InBrowse && i >= 0) OpenViewer(i);           // inside a folder one click opens a picture
        };
        View.ItemContextRequested += ShowTileMenu;
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { NavBack(); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { NavForward(); e.Handled = true; }
        };
        ApplyFolderPrefs();
        UpdateNavUi();
    }

    /// <summary>The second click of a double-click lands on whatever replaced the tile: ignore it.</summary>
    private bool RecentlyNavigated() => (Stopwatch.GetTimestamp() - _navAt) * 1000 < 450 * Stopwatch.Frequency;

    // ---- remembered choices per folder ----

    private BrowsePrefs Prefs(string folder) => _settings.BrowseFolders.TryGetValue(folder, out var p) ? p : new BrowsePrefs();
    private BrowsePrefs PrefsForEdit(string folder)
    {
        if (!_settings.BrowseFolders.TryGetValue(folder, out var p)) _settings.BrowseFolders[folder] = p = new BrowsePrefs();
        return p;
    }

    /// <summary>Puts the sort boxes to what this place uses: per folder in Browse (name A to Z unless changed), the Home choice in Home.</summary>
    private void ApplyFolderPrefs()
    {
        bool was = _loading; _loading = true;
        try
        {
            string fileSort = _settings.Sort; bool fileRev = _settings.SortReverse;
            if (InBrowse)
            {
                var p = Prefs(_folder);
                fileSort = p.FileSort; fileRev = p.FileReverse;
                foreach (ComboBoxItem it in FolderSortBox.Items) if ((string)it.Tag == p.FolderSort) FolderSortBox.SelectedItem = it;
                if (FolderSortBox.SelectedItem is null) FolderSortBox.SelectedIndex = 0;
                FolderRevBtn.IsChecked = p.FolderReverse;
            }
            SortBox.SelectedItem = null;
            foreach (ComboBoxItem it in SortBox.Items) if ((string)it.Tag == fileSort) SortBox.SelectedItem = it;
            if (SortBox.SelectedItem is null) SortBox.SelectedIndex = 0;
            SortRevBtn.IsChecked = fileRev;
            UpdateSortTip();
            ShuffleBtn.Visibility = fileSort == "Random" ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _loading = was; }
    }

    // ---- moving around ----

    private NavState Here() => new(_section, _folder, View.Offset, ViewerOpen && _vIndex >= 0 && _vIndex < _order.Length && _model is not null ? _model.Id[_order[_vIndex]] : -1);

    /// <summary>Keeps where we are now, to come back to.</summary>
    private void Remember()
    {
        _back.Add(Here());
        if (_back.Count > 500) _back.RemoveAt(0);
        _fwd.Clear();
        UpdateNavButtons();
    }

    private Task Go(string section, string folder, bool remember = true, double? offset = null)
    {
        if (remember && (section != _section || folder != _folder)) Remember();
        bool sectionChanged = section != _section;
        if (ViewerOpen) CloseViewer(fromUser: false);
        _section = section; _folder = folder; _navAt = Stopwatch.GetTimestamp();
        _loading = true; SearchBox.Text = ""; _loading = false;     // a search belongs to the place it was typed in
        ApplyFolderPrefs();
        UpdateNavUi();
        _barAuto = false; ApplyBar();                               // a new place starts at the top, with the bar showing
        if (sectionChanged) RevealBar();
        return BuildViewAsync(false, offset ?? 0);
    }

    private void OpenFolder(string path) => _ = Go("Browse", path);

    /// <summary>Back and forward stay within the section you are in (Browse does not step back into Home).</summary>
    private void GoBack()
    {
        if (_back.Count == 0 || _back[^1].Section != _section) return;
        var s = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _fwd.Add(Here());
        _ = Restore(s);
    }

    private void GoForward()
    {
        if (_fwd.Count == 0 || _fwd[^1].Section != _section) return;
        var s = _fwd[^1]; _fwd.RemoveAt(_fwd.Count - 1);
        _back.Add(Here());
        _ = Restore(s);
    }

    /// <summary>Returns to a remembered place: the folder (rebuilt if it is another one), its scroll position, and the picture if one was open.</summary>
    private async Task Restore(NavState s)
    {
        if (s.Section != _section || s.Folder != _folder) await Go(s.Section, s.Folder, remember: false, offset: s.Offset);
        if (s.ViewerId >= 0)
        {
            int idx = IndexOfId(s.ViewerId);
            if (idx >= _firstFile) { if (ViewerOpen) ShowIndex(idx, remember: false); else OpenViewer(idx, remember: false); }
        }
        else
        {
            if (ViewerOpen) CloseViewer(fromUser: false);
            View.Offset = s.Offset;
        }
        UpdateNavButtons();
    }

    private int IndexOfId(long id)
    {
        if (_model is null) return -1;
        for (int k = _firstFile; k < _order.Length; k++) if (_order[k] >= 0 && _model.Id[_order[k]] == id) return k;
        return -1;
    }

    /// <summary>Closing the viewer yourself: the steps through pictures are dropped, so Back goes to the folder you came from.</summary>
    private void ForgetViewerSteps()
    {
        while (_back.Count > 0 && _back[^1].Section == _section && _back[^1].Folder == _folder) _back.RemoveAt(_back.Count - 1);
        UpdateNavButtons();
    }

    private void GoUp()
    {
        if (!InBrowse || _folder.Length == 0) return;
        _ = Go("Browse", FolderPaths.Parent(_folder));
    }

    private void UpdateNavButtons()
    {
        BackBtn.IsEnabled = _back.Count > 0 && _back[^1].Section == _section;
        FwdBtn.IsEnabled = _fwd.Count > 0 && _fwd[^1].Section == _section;
    }

    private void OnBack(object s, RoutedEventArgs e) => GoBack();
    private void OnForward(object s, RoutedEventArgs e) => GoForward();
    private void OnUp(object s, RoutedEventArgs e) => GoUp();

    private void UpdateNavUi()
    {
        NavHome.IsChecked = _section == "Home"; NavBrowse.IsChecked = InBrowse; NavFav.IsChecked = InFavourites; NavTools.IsChecked = InTools;
        ToolsBar.Visibility = InTools ? Visibility.Visible : Visibility.Collapsed;
        BrowseBar.Visibility = InBrowse ? Visibility.Visible : Visibility.Collapsed;
        FoldersBtn.Visibility = InBrowse || InFavourites ? Visibility.Collapsed : Visibility.Visible;     // Select folders shapes Home; Browse shows every folder
        UpdateNavButtons(); UpBtn.IsEnabled = InBrowse && _folder.Length > 0;
        OwnOnlyBtn.Visibility = InBrowse && _folder.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.ToolTip = InBrowse ? "Search this folder: folder and file names (/ or Ctrl+F)" : "Search file and folder names (/ or Ctrl+F)";
        Crumbs.Children.Clear();
        if (!InBrowse) return;
        AddCrumb("All folders", "", _folder.Length == 0);
        if (_folder.Length > 0)
        {
            var parts = _folder.Split('\\');
            for (int k = 0; k < parts.Length; k++)
            {
                Crumbs.Children.Add(new TextBlock { Text = "", FontFamily = new System.Windows.Media.FontFamily("Segoe MDL2 Assets"), FontSize = 9, Foreground = (System.Windows.Media.Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
                AddCrumb(parts[k], string.Join('\\', parts.Take(k + 1)), k == parts.Length - 1);
            }
        }
    }

    private void AddCrumb(string text, string path, bool current)
    {
        var b = new Button { Content = text, Style = (Style)FindResource("Crumb"), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal };
        b.Click += (_, _) => { if (!current) _ = Go("Browse", path); };
        Crumbs.Children.Add(b);
    }

    // ---- what is shown ----

    private HashSet<string> BrowseDetails() => new(_settings.BrowseDetails, StringComparer.OrdinalIgnoreCase);

    private void OnOwnOnly(object s, RoutedEventArgs e) { _settings.BrowseOwnOnly = OwnOnlyBtn.IsChecked == true; BuildView(false); }
    private void OnCoverGrid(object s, RoutedEventArgs e) { _settings.BrowseCoverGrid = GridBtn.IsChecked == true; BuildView(true); }

    /// <summary>The cover-grid crop for tile <paramref name="i"/> (null for files, or when folders are shown as plain tiles).</summary>
    private ThumbLoader.Crop? CropForTile(BrowsePage page, int i, int[] order)
    {
        if (i >= page.FirstFile || i >= page.Crops.Length || order[i] < 0 || _model is null) return null;
        var (z, cx, cy) = page.Crops[i];
        int row = order[i];
        float aspect = Ginkgo.Data.ThumbMaker.ClampAspect(_model.W[row], _model.H[row]);
        float radius = _settings.CoverRounded ? (float)(_settings.CoverRadius * System.Windows.Media.VisualTreeHelper.GetDpi(View).PixelsPerDip) : 0;
        // How much wider than the tile the picture should be for the crop to look sharp. Within 1.5 times of the stored 320-pixel
        // thumbnail it is used as it is: making a sharper version means reading and decoding the original file, which is slow work.
        float factor = z * Math.Max(1f, aspect / MasonryView.CoverAspect);
        double tilePx = Math.Clamp(View.TileTarget, 110, 420) * System.Windows.Media.VisualTreeHelper.GetDpi(View).PixelsPerDip;
        if (tilePx * factor <= 480) factor = (float)Math.Min(factor, 320 / Math.Max(1, tilePx));
        return new ThumbLoader.Crop(z, cx, cy, factor, radius);
    }

    private void AdjustCover(int index)
    {
        if (_page is null || _loader is null || _model is null || !IsFolderTile(index) || _order[index] < 0) return;
        var node = _page.Tiles[index];
        var pic = _loader.QuickPicture(index);
        if (pic is null) { StatusText.Text = "The cover picture could not be read."; return; }
        long id = _model.Id[_order[index]];
        var dlg = new CoverEditWindow(node.Name, pic, MasonryView.CoverAspect, _page.Crops[index]) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var (z, cx, cy) = dlg.Result;
        _covers.SetCrop(node.Path, id, z, cx, cy); _covers.Save();
        BuildView(true);
    }

    private void OnFolderSortChanged(object s, RoutedEventArgs e)
    {
        if (_loading || FolderSortBox is null || FolderRevBtn is null) return;
        if (!InBrowse) return;
        var p = PrefsForEdit(_folder);
        p.FolderSort = (FolderSortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Name";
        p.FolderReverse = FolderRevBtn.IsChecked == true;
        BuildView(true);
    }

    private void OnDetailsMenu(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = DetailsBtn, Placement = PlacementMode.Bottom };
        foreach (var (key, name) in BrowsePage.DetailKinds)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = _settings.BrowseDetails.Contains(key), StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                _settings.BrowseDetails.RemoveAll(k => k == key);
                if (item.IsChecked) _settings.BrowseDetails.Add(key);
                if (InBrowse) BuildView(true);
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private string FolderHoverText(int i)
    {
        var n = _page!.Tiles[i];
        string line = BrowsePage.DetailLine(n, new HashSet<string>(BrowsePage.DetailKinds.Select(d => d.key)));
        return $"{n.Path}    {line}    (click to open, right-click for the cover)";
    }

    // ---- right-click menu ----

    private void ShowTileMenu(int index)
    {
        if (_model is null || index < 0 || index >= _order.Length) return;
        var menu = new ContextMenu();
        MenuItem Item(string text, Action act, bool enabled = true) { var it = new MenuItem { Header = text, IsEnabled = enabled }; it.Click += (_, _) => act(); menu.Items.Add(it); return it; }
        if (IsFolderTile(index))
        {
            var node = _page!.Tiles[index];
            _page.Candidates.TryGetValue(node.Path, out var cand);
            bool has = cand is { Count: > 0 };
            Item("Open", () => OpenFolder(node.Path));
            menu.Items.Add(new Separator());
            if (_settings.BrowseCoverGrid) Item("Adjust cover (zoom, crop)…", () => AdjustCover(index), has && _order[index] >= 0);
            Item("New random cover", () => { _covers.Reroll(node.Path, cand!, _model); _covers.Save(); BuildView(true); }, has);
            Item("Reset cover to random", () => { _covers.Reset(node.Path); _covers.Save(); BuildView(true); }, _covers.IsPinned(node.Path));
            menu.Items.Add(new Separator());
            Item("Show in Explorer", () => ShowInExplorer(System.IO.Path.Combine(_settings.LibraryRoot, node.Path), folder: true));
        }
        else
        {
            int row = _order[index];
            Item("Open", () => OpenViewer(index));
            if (InBrowse && _folder.Length > 0 && row >= 0)
            {
                menu.Items.Add(new Separator());
                string here = FolderPaths.Name(_folder);
                Item($"Use as the cover of “{here}”", () => { _covers.Pin(_folder, _model.Id[row]); _covers.Save(); StatusText.Text = $"Cover of {here} set."; });
                string own = _model.Display[row];
                if (own.Length > 0 && !own.Equals(_folder, StringComparison.OrdinalIgnoreCase))
                    Item($"Use as the cover of “{FolderPaths.Name(own)}”", () => { _covers.Pin(own, _model.Id[row]); _covers.Save(); StatusText.Text = $"Cover of {FolderPaths.Name(own)} set."; });
            }
            if (row >= 0)
            {
                menu.Items.Add(new Separator());
                bool starred = _fav.IsStarred(_model.Id[row]);
                Item(starred ? "Remove from Favourites" : "Add to Favourites", () => ToggleFavourite(row), _fav.Ready);
                menu.Items.Add(new Separator());
                if (!InFavourites) Item("Open its folder in Browse", () => OpenFolder(_model.Display[row]));
                Item("Show in Explorer", () => ShowInExplorer(_model.Path[row], folder: false));
                Item("Copy path", () => { try { Clipboard.SetText(_model.Path[row]); } catch { } });
                AddCopiesMenu(menu, row);
                if (_sets.FirstOrDefault(b => index >= b.First && index < b.First + b.Count && b.Large) is { } sb)
                {
                    menu.Items.Add(new Separator());
                    Item(sb.Collapsed ? $"Open the set “{sb.Name}”" : $"Close the set “{sb.Name}”", () => ToggleSet(sb.First));
                }
            }
        }
        menu.IsOpen = true;
    }

    private static void ShowInExplorer(string path, bool folder)
    {
        try
        {
            if (folder) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
