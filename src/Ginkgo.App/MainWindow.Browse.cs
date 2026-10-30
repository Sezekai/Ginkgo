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
    private sealed record NavState(string Section, string Folder, double Offset);
    private readonly List<NavState> _back = new(), _fwd = new();

    private bool InBrowse => _section == "Browse";
    private bool IsFolderTile(int index) => index >= 0 && index < _firstFile;

    private void InitBrowse()
    {
        OwnOnlyBtn.IsChecked = _settings.BrowseOwnOnly;
        FolderRevBtn.IsChecked = _settings.BrowseFolderReverse;
        foreach (ComboBoxItem it in FolderSortBox.Items) if ((string)it.Tag == _settings.BrowseFolderSort) FolderSortBox.SelectedItem = it;
        if (FolderSortBox.SelectedItem is null) FolderSortBox.SelectedIndex = 0;
        View.ItemClicked += i => { if (IsFolderTile(i) && !RecentlyNavigated()) OpenFolder(_page!.Tiles[i].Path); };
        View.ItemContextRequested += ShowTileMenu;
        PreviewMouseDown += (_, e) =>
        {
            if (ViewerRoot.Visibility == Visibility.Visible) return;
            if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
            else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
        };
        UpdateNavUi();
    }

    /// <summary>The second click of a double-click lands on whatever replaced the tile: ignore it.</summary>
    private bool RecentlyNavigated() => (Stopwatch.GetTimestamp() - _navAt) * 1000 < 450 * Stopwatch.Frequency;

    // ---- moving around ----

    private void Go(string section, string folder, bool remember = true, double? offset = null)
    {
        if (remember && (section != _section || folder != _folder)) { _back.Add(new NavState(_section, _folder, View.Offset)); _fwd.Clear(); }
        bool sectionChanged = section != _section;
        _section = section; _folder = folder; _navAt = Stopwatch.GetTimestamp();
        _loading = true; SearchBox.Text = ""; _loading = false;     // a search belongs to the place it was typed in
        UpdateNavUi();
        _ = BuildViewAsync(false, offset ?? 0);
        if (sectionChanged) RevealBar();
    }

    private void OpenFolder(string path) => Go("Browse", path);

    private void GoBack()
    {
        if (_back.Count == 0) return;
        var s = _back[^1]; _back.RemoveAt(_back.Count - 1);
        _fwd.Add(new NavState(_section, _folder, View.Offset));
        Go(s.Section, s.Folder, remember: false, offset: s.Offset);
    }

    private void GoForward()
    {
        if (_fwd.Count == 0) return;
        var s = _fwd[^1]; _fwd.RemoveAt(_fwd.Count - 1);
        _back.Add(new NavState(_section, _folder, View.Offset));
        Go(s.Section, s.Folder, remember: false, offset: s.Offset);
    }

    private void GoUp()
    {
        if (!InBrowse || _folder.Length == 0) return;
        Go("Browse", FolderPaths.Parent(_folder));
    }

    private void OnBack(object s, RoutedEventArgs e) => GoBack();
    private void OnForward(object s, RoutedEventArgs e) => GoForward();
    private void OnUp(object s, RoutedEventArgs e) => GoUp();

    private void UpdateNavUi()
    {
        NavHome.IsChecked = _section == "Home"; NavBrowse.IsChecked = InBrowse; NavFav.IsChecked = NavTools.IsChecked = false;
        BrowseBar.Visibility = InBrowse ? Visibility.Visible : Visibility.Collapsed;
        FoldersBtn.Visibility = InBrowse ? Visibility.Collapsed : Visibility.Visible;     // Select folders shapes Home; Browse shows every folder
        BackBtn.IsEnabled = _back.Count > 0; FwdBtn.IsEnabled = _fwd.Count > 0; UpBtn.IsEnabled = InBrowse && _folder.Length > 0;
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
        var b = new Button { Content = text, Height = 28, Margin = new Thickness(0), Padding = new Thickness(8, 2, 8, 2), FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal };
        if (current) b.Background = System.Windows.Media.Brushes.Transparent;
        b.Click += (_, _) => { if (!current) Go("Browse", path); };
        Crumbs.Children.Add(b);
    }

    // ---- what is shown ----

    private HashSet<string> BrowseDetails() => new(_settings.BrowseDetails, StringComparer.OrdinalIgnoreCase);

    private void OnOwnOnly(object s, RoutedEventArgs e) { _settings.BrowseOwnOnly = OwnOnlyBtn.IsChecked == true; BuildView(false); }

    private void OnFolderSortChanged(object s, RoutedEventArgs e)
    {
        if (_loading || FolderSortBox is null || FolderRevBtn is null) return;
        _settings.BrowseFolderSort = (FolderSortBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Name";
        _settings.BrowseFolderReverse = FolderRevBtn.IsChecked == true;
        if (InBrowse) BuildView(false);
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
                Item("Open its folder in Browse", () => OpenFolder(_model.Display[row]));
                Item("Show in Explorer", () => ShowInExplorer(_model.Path[row], folder: false));
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
