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
        _filterPopup = MakePopup(FiltersBtn, _filters, () => _filterClosedAt = Stopwatch.GetTimestamp());

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
            return MakePopup(anchor, new Border { Margin = new Thickness(6, 0, 20, 20), Child = p }, closed);
        }
        _nsfwPopup = Level(NsfwMoreBtn, ContentLevel.Nsfw, () => _nsfwClosedAt = Stopwatch.GetTimestamp(), out _nsfwPanel);
        _veryPopup = Level(VeryMoreBtn, ContentLevel.Very, () => _veryClosedAt = Stopwatch.GetTimestamp(), out _veryPanel);
        _nsfwPanel.Effect = _veryPanel.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 };

        View.SetLabelClicked += ToggleSet;
        View.CopiesOf = i => i >= _firstFile && i < _order.Length && _order[i] >= 0 && _copies.TryGetValue(_order[i], out var l) ? l.Count : 0;
        UpdateChips();
        UpdateLevelLabels();
    }

    private Popup MakePopup(FrameworkElement anchor, UIElement child, Action closed)
    {
        var p = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = child, VerticalOffset = 2, PopupAnimation = PopupAnimation.Fade };
        p.Closed += (_, _) => { closed(); ApplyBar(); };
        return p;
    }

    private bool LibraryPopupOpen() => (_filterPopup?.IsOpen ?? false) || (_nsfwPopup?.IsOpen ?? false) || (_veryPopup?.IsOpen ?? false);

    private static bool JustClosed(long at) => (Stopwatch.GetTimestamp() - at) * 4 < Stopwatch.Frequency;      // the click that closed it

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
        _nsfwPanel.Width = Math.Min(640, Math.Max(300, ActualWidth - 48));
    });

    private void OnVeryMore(object s, RoutedEventArgs e) => Toggle(_veryPopup, _veryClosedAt, () =>
    {
        if (_libModel is { } m) _veryPanel.Load(m.LevelStats(CurrentQuery(), ContentLevel.Very), _settings.VeryOff, _settings.Standalone, _settings.RarelyUsed);
        _veryPanel.Width = Math.Min(640, Math.Max(300, ActualWidth - 48));
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
            var x = new TextBlock { Text = "", FontFamily = ThemeKit.IconFamily, FontSize = 9, Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Dim") };
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
