using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The Filters pop-out: format, size, dimensions, megapixels, shape, video length, dates, favourites and sets. Filters combine (a file
/// must pass them all); each one in use also shows as a chip under the bar, where it can be removed.
/// </summary>
public sealed class FilterPanel : Border
{
    public event Action? Changed;
    private FilterSet _f;
    private readonly List<Action> _syncs = new();
    private readonly WrapPanel _formats = new();
    private List<(string ext, int count)> _formatList = new();
    private bool _syncing;

    private static Brush Dim => ThemeKit.Brush("Dim");
    private static Brush Txt => ThemeKit.Brush("Text");
    private static Brush Line => ThemeKit.Brush("Edge");
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public FilterPanel(FilterSet f)
    {
        _f = f;
        Background = ThemeKit.Brush("Bar"); BorderBrush = ThemeKit.Brush("Edge"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(10);
        Width = 460;
        TextElement.SetFontFamily(this, (FontFamily)Application.Current.Resources["AppFont"]); TextElement.SetFontSize(this, 12);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 };
        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 700 };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var clear = new Button { Content = "Clear all", Padding = new Thickness(8, 2, 8, 2) };
        clear.Click += (_, _) => { Clear(_f); Sync(); Changed?.Invoke(); };
        DockPanel.SetDock(clear, Dock.Right); head.Children.Add(clear);
        head.Children.Add(new TextBlock { Text = "Filters", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Txt });
        stack.Children.Add(head);
        stack.Children.Add(Note("A file has to pass every filter in use. Each one also shows under the bar, where its × removes it."));

        Title(stack, "Format");
        stack.Children.Add(_formats);
        _syncs.Add(BuildFormats);

        Title(stack, "File size (MB)");
        stack.Children.Add(Range(v => _f.MinSize = (long)(v * 1048576), v => _f.MaxSize = (long)(v * 1048576), () => _f.MinSize / 1048576.0, () => _f.MaxSize / 1048576.0, "0.#"));
        Title(stack, "Width (pixels)");
        stack.Children.Add(Range(v => _f.MinW = (int)v, v => _f.MaxW = (int)v, () => _f.MinW, () => _f.MaxW, "0"));
        Title(stack, "Height (pixels)");
        stack.Children.Add(Range(v => _f.MinH = (int)v, v => _f.MaxH = (int)v, () => _f.MinH, () => _f.MaxH, "0"));
        Title(stack, "Megapixels");
        stack.Children.Add(Range(v => _f.MinMP = v, v => _f.MaxMP = v, () => _f.MinMP, () => _f.MaxMP, "0.##"));

        Title(stack, "Shape");
        var shapes = Choices(new[] { ("Any", ""), ("Portrait", "Portrait"), ("Landscape", "Landscape"), ("Square", "Square"), ("Width ÷ height", "Ratio") },
            () => _f.Orientation, v => _f.Orientation = v);
        stack.Children.Add(shapes);
        var ratio = Range(v => _f.MinRatio = v, v => _f.MaxRatio = v, () => _f.MinRatio, () => _f.MaxRatio, "0.##");
        stack.Children.Add(ratio);
        _syncs.Add(() => ratio.Visibility = _f.Orientation == "Ratio" ? Visibility.Visible : Visibility.Collapsed);

        Title(stack, "Video length (seconds, or minutes:seconds)");
        stack.Children.Add(Range(v => _f.MinDur = v, v => _f.MaxDur = v, () => _f.MinDur, () => _f.MaxDur, "clock"));
        stack.Children.Add(Note("Setting a length shows only videos."));

        Title(stack, "Date");
        var field = Choices(new[] { ("Modified", "Modified"), ("Created", "Created"), ("Taken", "Taken"), ("First seen", "FirstSeen") }, () => _f.DateField, v => _f.DateField = v);
        stack.Children.Add(field);
        var when = Choices(new[] { ("Any time", "0"), ("Last 7 days", "7"), ("Last 30 days", "30"), ("Last 90 days", "90"), ("Between", "range") },
            () => _f.LastDays > 0 ? _f.LastDays.ToString() : _f.DateFrom > 0 || _f.DateTo > 0 || _rangeOpen ? "range" : "0",
            v =>
            {
                _rangeOpen = v == "range";
                if (v == "range") { _f.LastDays = 0; }
                else { _f.LastDays = int.Parse(v); _f.DateFrom = _f.DateTo = 0; }
            });
        stack.Children.Add(when);
        var from = new DatePicker { Width = 140, Margin = new Thickness(0, 6, 8, 0) };
        var to = new DatePicker { Width = 140, Margin = new Thickness(0, 6, 0, 0) };
        from.SelectedDateChanged += (_, _) => { if (_syncing) return; _f.DateFrom = from.SelectedDate is { } d ? new DateTimeOffset(d.Date).ToUnixTimeSeconds() : 0; _f.LastDays = 0; Changed?.Invoke(); };
        to.SelectedDateChanged += (_, _) => { if (_syncing) return; _f.DateTo = to.SelectedDate is { } d ? new DateTimeOffset(d.Date.AddDays(1).AddSeconds(-1)).ToUnixTimeSeconds() : 0; _f.LastDays = 0; Changed?.Invoke(); };
        var dates = new WrapPanel();
        dates.Children.Add(new TextBlock { Text = "From", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 6, 0) });
        dates.Children.Add(from);
        dates.Children.Add(new TextBlock { Text = "to", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 6, 0) });
        dates.Children.Add(to);
        stack.Children.Add(dates);
        _syncs.Add(() =>
        {
            dates.Visibility = _f.LastDays == 0 && (_rangeOpen || _f.DateFrom > 0 || _f.DateTo > 0) ? Visibility.Visible : Visibility.Collapsed;
            from.SelectedDate = _f.DateFrom > 0 ? DateTimeOffset.FromUnixTimeSeconds(_f.DateFrom).LocalDateTime.Date : null;
            to.SelectedDate = _f.DateTo > 0 ? DateTimeOffset.FromUnixTimeSeconds(_f.DateTo).LocalDateTime.Date : null;
        });
        stack.Children.Add(Note("Taken is the date in a photo's camera details; files without one use their modified date. Created and taken dates fill in as Ginkgo reads them in the background."));

        Title(stack, "Favourites");
        stack.Children.Add(Choices(new[] { ("All", "0"), ("Favourites only", "1"), ("Hide favourites", "2") }, () => _f.Favs.ToString(), v => _f.Favs = int.Parse(v)));
        Title(stack, "Connected Art sets");
        stack.Children.Add(Choices(new[] { ("All", "0"), ("Only sets", "1"), ("Hide sets", "2") }, () => _f.Sets.ToString(), v => _f.Sets = int.Parse(v)));
        Sync();
    }

    private bool _rangeOpen;

    /// <summary>Shows another filter set (and the formats there are to choose from).</summary>
    public void Load(FilterSet f, List<(string ext, int count)> formats) { _f = f; _formatList = formats; _rangeOpen = false; Sync(); }

    public void Sync() { _syncing = true; try { foreach (var a in _syncs) a(); } finally { _syncing = false; } }

    public static void Clear(FilterSet f)
    {
        f.Formats.Clear(); f.MinSize = f.MaxSize = 0; f.MinW = f.MaxW = f.MinH = f.MaxH = 0; f.MinMP = f.MaxMP = 0;
        f.Orientation = ""; f.MinRatio = f.MaxRatio = 0; f.MinDur = f.MaxDur = 0; f.LastDays = 0; f.DateFrom = f.DateTo = 0; f.Favs = f.Sets = 0;
    }

    private void BuildFormats()
    {
        _formats.Children.Clear();
        var list = _formatList.Select(x => x.ext).ToList();
        foreach (var e in _f.Formats) if (!list.Contains(e, StringComparer.OrdinalIgnoreCase)) list.Add(e);
        foreach (var ext in list)
        {
            int n = _formatList.FirstOrDefault(x => x.ext == ext).count;
            var b = new ToggleButton { Content = n > 0 ? $"{ext}  {n:N0}" : ext, Margin = new Thickness(0, 0, 6, 6), MinWidth = 44, IsChecked = _f.Formats.Contains(ext) };
            string k = ext;
            b.Click += (_, _) => { if (b.IsChecked == true) _f.Formats.Add(k); else _f.Formats.Remove(k); Changed?.Invoke(); };
            _formats.Children.Add(b);
        }
        if (list.Count == 0) _formats.Children.Add(Note("(the library is still loading)"));
    }

    private FrameworkElement Range(Action<double> setMin, Action<double> setMax, Func<double> getMin, Func<double> getMax, string fmt)
    {
        var p = new WrapPanel();
        TextBox Box(Action<double> set, Func<double> get, string hint)
        {
            var t = new TextBox { Width = 90, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = hint };
            void Apply()
            {
                if (_syncing) return;
                double v = ParseNumber(t.Text, fmt == "clock");
                if (Math.Abs(v - get()) < 1e-9) return;
                set(Math.Max(0, v)); Changed?.Invoke();
            }
            t.LostFocus += (_, _) => Apply();
            t.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
            _syncs.Add(() => { if (!t.IsKeyboardFocused) t.Text = get() > 0 ? Show(get(), fmt) : ""; });
            return t;
        }
        p.Children.Add(new TextBlock { Text = "at least", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        p.Children.Add(Box(setMin, getMin, "Leave empty for no lower limit"));
        p.Children.Add(new TextBlock { Text = "at most", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        p.Children.Add(Box(setMax, getMax, "Leave empty for no upper limit"));
        return p;
    }

    public static double ParseNumber(string text, bool clock)
    {
        text = text.Trim();
        if (text.Length == 0) return 0;
        if (clock && text.Contains(':'))
        {
            double total = 0;
            foreach (var part in text.Split(':')) total = total * 60 + (double.TryParse(part, NumberStyles.Float, CultureInfo.CurrentCulture, out var v) ? v : 0);
            return total;
        }
        return double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var d) || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0;
    }

    private static string Show(double v, string fmt) => fmt == "clock" ? MainWindow.Clock(v) : v.ToString(fmt, CultureInfo.CurrentCulture);

    private FrameworkElement Choices((string label, string value)[] items, Func<string> get, Action<string> set)
    {
        var p = new WrapPanel();
        var btns = new List<(ToggleButton b, string v)>();
        foreach (var (label, value) in items)
        {
            var b = new ToggleButton { Content = label, Margin = new Thickness(0, 0, 6, 6), MinWidth = 44 };
            string v = value;
            b.Click += (_, _) => { set(v); Sync(); Changed?.Invoke(); };
            btns.Add((b, v)); p.Children.Add(b);
        }
        _syncs.Add(() => { string cur = get(); foreach (var (b, v) in btns) b.IsChecked = v == cur; });
        return p;
    }

    private static void Title(StackPanel s, string t)
    {
        s.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 12, 0, 10) });
        s.Children.Add(new TextBlock { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Txt, Margin = new Thickness(0, 0, 0, 6) });
    }
    private static TextBlock Note(string t) => new() { Text = t, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>Each filter in use, as a chip's words and what its × does.</summary>
    public static List<(string text, Action clear)> Chips(FilterSet f)
    {
        var l = new List<(string, Action)>();
        string Mb(long b) => (b / 1048576.0).ToString("0.#", CultureInfo.CurrentCulture) + " MB";
        string Between(string what, string lo, string hi, bool hasLo, bool hasHi) =>
            hasLo && hasHi ? $"{what} {lo} to {hi}" : hasLo ? $"{what} at least {lo}" : $"{what} at most {hi}";
        if (f.Formats.Count > 0) l.Add((string.Join(", ", f.Formats.OrderBy(x => x)), () => f.Formats.Clear()));
        if (f.MinSize > 0 || f.MaxSize > 0) l.Add((Between("Size", Mb(f.MinSize), Mb(f.MaxSize), f.MinSize > 0, f.MaxSize > 0), () => f.MinSize = f.MaxSize = 0));
        if (f.MinW > 0 || f.MaxW > 0) l.Add((Between("Width", $"{f.MinW} px", $"{f.MaxW} px", f.MinW > 0, f.MaxW > 0), () => f.MinW = f.MaxW = 0));
        if (f.MinH > 0 || f.MaxH > 0) l.Add((Between("Height", $"{f.MinH} px", $"{f.MaxH} px", f.MinH > 0, f.MaxH > 0), () => f.MinH = f.MaxH = 0));
        if (f.MinMP > 0 || f.MaxMP > 0) l.Add((Between("Megapixels", f.MinMP.ToString("0.##"), f.MaxMP.ToString("0.##"), f.MinMP > 0, f.MaxMP > 0), () => f.MinMP = f.MaxMP = 0));
        if (f.Orientation.Length > 0)
            l.Add((f.Orientation == "Ratio" ? Between("Width ÷ height", f.MinRatio.ToString("0.##"), f.MaxRatio.ToString("0.##"), f.MinRatio > 0, f.MaxRatio > 0) : f.Orientation,
                () => { f.Orientation = ""; f.MinRatio = f.MaxRatio = 0; }));
        if (f.MinDur > 0 || f.MaxDur > 0) l.Add((Between("Length", MainWindow.Clock(f.MinDur), MainWindow.Clock(f.MaxDur), f.MinDur > 0, f.MaxDur > 0), () => f.MinDur = f.MaxDur = 0));
        string field = f.DateField == "FirstSeen" ? "First seen" : f.DateField;
        string D(long t) => DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        if (f.LastDays > 0) l.Add(($"{field} in the last {f.LastDays} days", () => f.LastDays = 0));
        else if (f.DateFrom > 0 || f.DateTo > 0) l.Add((Between(field, D(f.DateFrom), D(f.DateTo), f.DateFrom > 0, f.DateTo > 0).Replace(" at least ", " from ").Replace(" at most ", " until "), () => f.DateFrom = f.DateTo = 0));
        if (f.Favs != 0) l.Add((f.Favs == 1 ? "Favourites only" : "No favourites", () => f.Favs = 0));
        if (f.Sets != 0) l.Add((f.Sets == 1 ? "Only sets" : "No sets", () => f.Sets = 0));
        return l;
    }
}
