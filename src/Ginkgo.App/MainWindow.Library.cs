using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// The Filters pop-out and its chips, the NSFW and Very folder-by-folder pop-outs, Connected Art sets (opening and closing),
/// copies of the same file shown once, and the background details job's progress.
/// </summary>
public partial class MainWindow
{
    private FilterPanel _filters = null!;
    private Popup _filterPopup = null!, _nsfwPopup = null!, _veryPopup = null!;
    private FolderPanel _nsfwPanel = null!, _veryPanel = null!;
    private long _filterClosedAt, _nsfwClosedAt, _veryClosedAt;
    private readonly DispatcherTimerLite _filterDelay = new(300);
    private List<SetBlock> _sets = new();
    private Dictionary<int, List<int>> _copies = new();
    private readonly HashSet<string> _openSets = new(StringComparer.OrdinalIgnoreCase);

    private void InitLibraryTools()
    {
        _filters = new FilterPanel(_settings.Filters);
        _filters.Changed += () => { UpdateChips(); _filterDelay.Run(() => BuildView(false)); };
        _filterPopup = MakePopup(FiltersBtn, _filters, () => { _filterClosedAt = Stopwatch.GetTimestamp(); FiltersBtn.Tag = false; });
        _filterPopup.Opened += (_, _) => FiltersBtn.Tag = true;

        Popup Level(Button anchor, ContentLevel level, Action closed, out FolderPanel panel)
        {
            panel = new FolderPanel(_settings.FolderSort, _settings.FolderSortReverse)
            {
                NoteText = level == ContentLevel.Nsfw
                    ? "The folders that hold an NSFW folder. Switched off here, that folder's NSFW files stay hidden while NSFW is on. The NSFW button still switches them all."
                    : "The folders that hold a Very folder (inside their NSFW folder). Switched off here, that folder's Very files stay hidden while Very is on.",
            };
            var p = panel;
            p.Changed += off =>
            {
                if (level == ContentLevel.Nsfw) _settings.NsfwOff = off.ToList(); else _settings.VeryOff = off.ToList();
                UpdateLevelLabels();
                _filterDelay.Run(() => BuildView(false));
            };
            p.PlacesChanged += (alone, rare) => { _settings.Standalone = alone.ToList(); _settings.RarelyUsed = rare.ToList(); SaveSettings(); };
            var card = new PopCard(p, level == ContentLevel.Nsfw ? "NSFW folders" : "Very folders");
            var pop = MakePopup(anchor, card, closed);
            pop.Opened += (_, _) => card.NotchX = anchor.ActualWidth / 2 - pop.HorizontalOffset - 6;
            return pop;
        }
        _nsfwPopup = Level(NsfwMoreBtn, ContentLevel.Nsfw, () => _nsfwClosedAt = Stopwatch.GetTimestamp(), out _nsfwPanel);
        _veryPopup = Level(VeryMoreBtn, ContentLevel.Very, () => _veryClosedAt = Stopwatch.GetTimestamp(), out _veryPanel);

        View.SetLabelClicked += ToggleSet;
        View.CopiesOf = i => i >= _firstFile && i < _order.Length && _order[i] >= 0 && _copies.TryGetValue(_order[i], out var l) ? l.Count : 0;
        UpdateChips();
        UpdateLevelLabels();
    }

    private Popup MakePopup(FrameworkElement anchor, UIElement child, Action closed)
    {
        var p = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = child, VerticalOffset = 2, PopupAnimation = PopupAnimation.Fade };
        p.Closed += (_, _) => { closed(); ApplyBar(); };
        return Sticky(p);
    }

    private bool LibraryPopupOpen() => (_filterPopup?.IsOpen ?? false) || (_nsfwPopup?.IsOpen ?? false) || (_veryPopup?.IsOpen ?? false);

    private static bool JustClosed(long at) => false;     // (sticky pop-outs: the button's own click toggles them)

    // ---- pop-outs that the button opening them also closes ----

    private readonly List<Popup> _sticky = new();

    /// <summary>A pop-out that stays open until a click lands outside it (not on its own button), Esc, or the window moves.
    /// Its button opens and closes it, like a drop-down.</summary>
    private Popup Sticky(Popup p)
    {
        p.StaysOpen = true;
        if (!_sticky.Contains(p)) _sticky.Add(p);
        if (_sticky.Count == 1)
        {
            PreviewMouseDown += (_, e) =>
            {
                foreach (var q in _sticky)
                {
                    if (!q.IsOpen) continue;
                    if (q.PlacementTarget is UIElement anchor && e.OriginalSource is DependencyObject d && IsInside(d, anchor)) continue;
                    q.IsOpen = false;
                }
            };
            Deactivated += (_, _) => CloseSticky();
            LocationChanged += (_, _) => CloseSticky();
            SizeChanged += (_, _) => CloseSticky();
        }
        return p;
    }

    private bool CloseSticky()
    {
        bool any = false;
        foreach (var q in _sticky) if (q.IsOpen) { q.IsOpen = false; any = true; }
        return any;
    }

    private static bool IsInside(DependencyObject d, DependencyObject outer)
    {
        for (var x = d; x != null; x = x is Visual || x is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
            if (x == outer) return true;
        return false;
    }

    private void Toggle(Popup p, long closedAt, Action beforeOpen)
    {
        if (p.IsOpen) { p.IsOpen = false; return; }
        if (JustClosed(closedAt)) return;
        beforeOpen();
        // keep it inside the window
        if (p.PlacementTarget is FrameworkElement t && p.Child is FrameworkElement c)
        {
            c.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double x0 = t.TranslatePoint(new Point(0, 0), this).X;
            p.HorizontalOffset = Math.Min(0, ActualWidth - 10 - (c.DesiredSize.Width + 10) - x0);
        }
        p.IsOpen = true;
        ApplyBar();
    }

    private void OnFilters(object s, RoutedEventArgs e) =>
        Toggle(_filterPopup, _filterClosedAt, () => _filters.Load(_settings.Filters, (_model ?? _libModel)?.Formats() ?? new()));

    private void OnNsfwMore(object s, RoutedEventArgs e) => Toggle(_nsfwPopup, _nsfwClosedAt, () =>
    {
        if (_libModel is { } m) _nsfwPanel.Load(m.LevelStats(CurrentQuery(), ContentLevel.Nsfw), _settings.NsfwOff, _settings.Standalone, _settings.RarelyUsed);
        _nsfwPanel.Width = Math.Min(768, Math.Max(300, ActualWidth - 48));
    });

    private void OnVeryMore(object s, RoutedEventArgs e) => Toggle(_veryPopup, _veryClosedAt, () =>
    {
        if (_libModel is { } m) _veryPanel.Load(m.LevelStats(CurrentQuery(), ContentLevel.Very), _settings.VeryOff, _settings.Standalone, _settings.RarelyUsed);
        _veryPanel.Width = Math.Min(768, Math.Max(300, ActualWidth - 48));
    });

    private void UpdateLevelLabels()
    {
        NsfwMoreBtn.ToolTip = _settings.NsfwOff.Count == 0 ? "NSFW folder by folder" : $"NSFW folder by folder ({_settings.NsfwOff.Count:N0} off)";
        VeryMoreBtn.ToolTip = _settings.VeryOff.Count == 0 ? "Very folder by folder" : $"Very folder by folder ({_settings.VeryOff.Count:N0} off)";
    }

    /// <summary>The filters in use as chips under the bar, each with an x, and Clear all.</summary>
    private void UpdateChips()
    {
        Chips.Children.Clear();
        var list = FilterPanel.Chips(_settings.Filters);
        ChipBar.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FiltersBtn.Content = list.Count > 0 ? $"Filters ({list.Count})" : "Filters";
        foreach (var (text, clear) in list)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock { Text = text, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center });
            var x = new IconGlyph { Glyph = "", Size = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Dim") };
            row.Children.Add(x);
            var chip = new Border
            {
                Child = row, CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 3, 9, 3), Margin = new Thickness(0, 0, 6, 0), Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(0x2E, 0x26, 0x44)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x5C, 0xF5)), BorderThickness = new Thickness(1),
                ToolTip = "Remove this filter",
            };
            var c = clear;
            chip.MouseLeftButtonUp += (_, _) => { c(); FiltersChanged(); };
            Chips.Children.Add(chip);
        }
        if (list.Count > 1)
        {
            var all = new Button { Content = "Clear all", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 0, 0, 0) };
            all.Click += (_, _) => { FilterPanel.Clear(_settings.Filters); FiltersChanged(); };
            Chips.Children.Add(all);
        }
        UpdateInset();
    }

    private void FiltersChanged()
    {
        _filters.Sync();
        UpdateChips();
        BuildView(false);
    }

    // ---- Connected Art sets ----

    private SetBlock? CollapsedSetAt(int i) => _sets.FirstOrDefault(b => b.Collapsed && b.First == i);

    /// <summary>Opens a stacked set into its block, or closes an open large one.</summary>
    private void ToggleSet(int first)
    {
        var b = _sets.FirstOrDefault(x => x.First == first);
        if (b is null) return;
        if (!_openSets.Remove(b.Key)) _openSets.Add(b.Key);
        BuildView(true);
    }

    /// <summary>"also in: ..." for a file shown once although it is in other folders too.</summary>
    private string CopiesText(int row)
    {
        if (!_copies.TryGetValue(row, out var l) || _model is not { } m) return "";
        return "   also in: " + string.Join(", ", l.Select(r => m.Display[r].Length == 0 ? "(library)" : m.Display[r]).Distinct());
    }

    /// <summary>Home: switch off the file's folder (or its top-level folder), send it to Rarely used, and the same for its
    /// NSFW and Very files when it is one of them.</summary>
    private void AddFolderOptions(ContextMenu menu, int row)
    {
        if (_section != "Home" || _model is not { } m || row < 0 || row >= m.Count) return;
        string folder = m.Display[row];
        string name = folder.Length == 0 ? "the library's own files" : FolderPaths.Name(folder);
        var level = (ContentLevel)m.Level[row];
        void Item(string text, Action act) { var it = new MenuItem { Header = text }; it.Click += (_, _) => { act(); AfterFolderChange(); }; menu.Items.Add(it); }
        menu.Items.Add(new Separator());
        Item($"Hide the folder “{name}”", () => HideFolderTree(folder));
        int cut = folder.IndexOf('\\');
        if (cut > 0) { string top = folder[..cut]; Item($"Hide “{top}” (all of it)", () => HideFolderTree(top)); }
        if (folder.Length > 0) Item($"Send “{name}” to Rarely used", () => { Add(_settings.RarelyUsed, folder); HideFolderTree(folder); });
        if (level >= ContentLevel.Nsfw && folder.Length > 0)
        {
            menu.Items.Add(new Separator());
            Item($"Hide the NSFW files of “{name}”", () => Add(_settings.NsfwOff, folder));
            Item($"Send “{name}” to Rarely used in NSFW", () => { Add(_settings.RarelyUsed, folder); Add(_settings.NsfwOff, folder); });
        }
        if (level >= ContentLevel.Very && folder.Length > 0)
        {
            Item($"Hide the Very files of “{name}”", () => Add(_settings.VeryOff, folder));
            Item($"Send “{name}” to Rarely used in Very", () => { Add(_settings.RarelyUsed, folder); Add(_settings.VeryOff, folder); });
        }
        static void Add(List<string> list, string v) { if (!list.Contains(v, StringComparer.OrdinalIgnoreCase)) list.Add(v); }
    }

    /// <summary>A folder and everything inside it, switched off in Select folders.</summary>
    private void HideFolderTree(string folder)
    {
        if (_libModel is not { } m) return;
        var off = new HashSet<string>(_settings.OffFolders, StringComparer.OrdinalIgnoreCase) { folder };
        for (int i = 0; i < m.Count; i++) if (FolderPaths.IsUnder(m.Display[i], folder)) off.Add(m.Display[i]);
        _settings.OffFolders = off.ToList();
    }

    private void AfterFolderChange()
    {
        UpdateFoldersLabel(); UpdateLevelLabels();
        SaveSettings();
        if (ViewerOpen) CloseViewer(fromUser: false);
        BuildView(true);
    }

    private void AddCopiesMenu(ContextMenu menu, int row)
    {
        if (!_copies.TryGetValue(row, out var l) || _model is not { } m) return;
        var parent = new MenuItem { Header = $"Also in {l.Count} other place{(l.Count == 1 ? "" : "s")}" };
        foreach (int r in l)
        {
            var it = new MenuItem { Header = "Show in Explorer: " + m.Path[r] };
            string path = m.Path[r];
            it.Click += (_, _) => ShowInExplorer(path, folder: false);
            parent.Items.Add(it);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(parent);
    }

    // ---- the details job (photo dates, copies of the same file) ----

    private string DetailsStatus()
    {
        var p = _engine.Details;
        if (!p.Running) return "";
        string left = p.Remaining is { } r ? $", about {Clock(r.TotalSeconds)} left" : "";
        long bt = Interlocked.Read(ref p.BytesTotal);
        string amount = bt > 0 ? $"{Interlocked.Read(ref p.BytesDone) / 1048576.0:N0} of {bt / 1048576.0:N0} MB" : $"{Interlocked.Read(ref p.FilesDone):N0} of {Interlocked.Read(ref p.FilesTotal):N0} files";
        string file = p.Current.Length > 0 ? "  (" + System.IO.Path.GetFileName(p.Current) + ")" : "";
        return $"  |  {p.What}: {p.Fraction * 100:F0}%, {amount}, {Clock(p.Elapsed.TotalSeconds)} so far{left}{file}";
    }

    private void TickDetails()
    {
        var p = _engine.Details;
        DetailsBar.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        if (p.Running) DetailsBar.Value = p.Fraction;
    }

    private string DetailsDiag() => _engine.DetailsProblem.Length > 0 ? " | details problem: " + _engine.DetailsProblem : "";
}

/// <summary>Runs an action once, a moment after the last request (typing in a filter box, clicking several switches).</summary>
public sealed class DispatcherTimerLite
{
    private readonly System.Windows.Threading.DispatcherTimer _t;
    private Action? _act;
    public DispatcherTimerLite(int ms)
    {
        _t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        _t.Tick += (_, _) => { _t.Stop(); _act?.Invoke(); };
    }
    public void Run(Action a) { _act = a; _t.Stop(); _t.Start(); }
}
