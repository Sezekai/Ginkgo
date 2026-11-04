using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>Settings &gt; Video outlines: the outlines round paused and watched videos in the grid, everything about them adjustable, live.</summary>
public sealed class OutlineWindow : Window
{
    public OutlineWindow(AppSettings s, Action changed)
    {
        Title = "Video outlines"; Width = 470; Height = 760; Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x20));
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        var ed = new OutlineEditor(s);
        ed.Changed += changed;
        Content = new ScrollViewer { Content = ed, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);
    }
}

public sealed class OutlineEditor : StackPanel
{
    public event Action? Changed;
    private readonly AppSettings _s;
    private readonly List<Action> _syncs = new();
    private static readonly Brush Dim = Frozen("#B5BAC0"), Txt = Frozen("#E4E4E8"), Line = Frozen("#2E2E36");
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly string[] Swatches = { "#FFB000", "#FF8A00", "#FFD84A", "#FF4D4D", "#FF5FA2", "#B07CFF", "#4DA3FF", "#3FD0D0", "#5FAF6B", "#9BE07A", "#FFFFFF" };

    public OutlineEditor(AppSettings s)
    {
        _s = s;
        Margin = new Thickness(18, 14, 18, 18);
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI")); TextElement.SetFontSize(this, 12);
        var stack = this;
        stack.Children.Add(new TextBlock { Text = "Video outlines", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Txt, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(Note("Watched comes first: a watched video you pause keeps the watched outline. A video playing has no outline."));
        Sep(stack);

        stack.Children.Add(Title("Which videos"));
        stack.Children.Add(Note("This session: videos paused or watched since Ginkgo opened (they start fresh each time). Always: remembered between sessions."));
        var mem = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var mems = new List<(ToggleButton b, string m)>();
        foreach (var (label, m) in new[] { ("This session", "Session"), ("Always", "Always") })
        {
            var b = Choice(label); string mm = m;
            b.Click += (_, _) => { _s.OutlineMemory = mm; Sync(); Changed?.Invoke(); };
            mems.Add((b, m)); mem.Children.Add(b);
        }
        _syncs.Add(() => { foreach (var (b, m) in mems) b.IsChecked = m == _s.OutlineMemory; });
        stack.Children.Add(mem);
        Sep(stack);

        OutlineSection(stack, "Paused outline", "Round a video you've paused in the grid.", () => _s.PausedOutline, v => _s.PausedOutline = v, OutlineStyle.PausedDefault());
        Sep(stack);
        OutlineSection(stack, "Watched outline", "Round videos you've watched.", () => _s.WatchedOutline, v => _s.WatchedOutline = v, OutlineStyle.WatchedDefault());
        stack.Children.Add(new TextBlock { Text = "Counts as watched", Foreground = Dim, Margin = new Thickness(0, 10, 0, 6) });
        var rule = new WrapPanel();
        var rules = new List<(ToggleButton b, string r)>();
        foreach (var (label, r, tip) in new[] { ("Played to the end", "End", "Played to 90% or more (a hover preview counts too)"), ("Started", "Started", "Any of it played") })
        {
            var b = Choice(label); b.ToolTip = tip; string rr = r;
            b.Click += (_, _) => { _s.WatchedRule = rr; Sync(); Changed?.Invoke(); };
            rules.Add((b, r)); rule.Children.Add(b);
        }
        _syncs.Add(() => { foreach (var (b, r) in rules) b.IsChecked = r == _s.WatchedRule; });
        stack.Children.Add(rule);
        Sep(stack);

        var copy = new Button { Content = "Copy my outline settings", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left };
        var copied = new TextBlock { Foreground = Dim, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        copy.Click += (_, _) => { try { Clipboard.SetText(Describe()); copied.Text = "Copied: paste it to Claude."; } catch { copied.Text = "Couldn't copy."; } };
        var cp = new StackPanel { Orientation = Orientation.Horizontal };
        cp.Children.Add(copy); cp.Children.Add(copied);
        stack.Children.Add(cp);
        Sync();
    }

    public string Describe()
    {
        static string O(OutlineStyle o) => $"on {o.On}, colour {o.Colour}, thickness {o.Width:0.#}, opacity {o.Opacity:0.00}, glow {o.Glow:0.#}, glow strength {o.GlowStrength:0.00}, pulse {o.Pulse}";
        return $"Ginkgo outlines\nPaused: {O(_s.PausedOutline)}\nWatched: {O(_s.WatchedOutline)}, counts as watched: {_s.WatchedRule}, which videos: {_s.OutlineMemory}";
    }

    public void Sync() { foreach (var a in _syncs) a(); }

    private void OutlineSection(StackPanel stack, string title, string note, Func<OutlineStyle> get, Action<OutlineStyle> set, OutlineStyle defaults)
    {
        stack.Children.Add(Head(title, OnOff(() => get().On, v => get().On = v)));
        stack.Children.Add(Note(note));

        // colour: swatches and a box for any colour (#RRGGBB)
        stack.Children.Add(new TextBlock { Text = "Colour", Foreground = Dim, Margin = new Thickness(0, 10, 0, 6) });
        var sw = new WrapPanel();
        var hex = new TextBox { Width = 84, Margin = new Thickness(0, 0, 6, 6), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Any colour, like #FFB000" };
        var marks = new List<(Border b, string c)>();
        foreach (var c in Swatches)
        {
            var col = (Color)ColorConverter.ConvertFromString(c);
            var b = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = new SolidColorBrush(col), Margin = new Thickness(0, 0, 6, 6), Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(2), ToolTip = c };
            string cc = c;
            b.MouseLeftButtonUp += (_, _) => { get().Colour = cc; Sync(); Changed?.Invoke(); };
            marks.Add((b, c)); sw.Children.Add(b);
        }
        sw.Children.Add(hex);
        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) ApplyHex(); };
        void ApplyHex()
        {
            var t = hex.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
            try { ColorConverter.ConvertFromString(t); get().Colour = t.ToUpperInvariant(); Changed?.Invoke(); } catch { }
            Sync();
        }
        _syncs.Add(() =>
        {
            foreach (var (b, c) in marks) b.BorderBrush = string.Equals(c, get().Colour, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent;
            if (!hex.IsKeyboardFocused) hex.Text = get().Colour;
        });
        stack.Children.Add(sw);

        Slide(stack, "Thickness", 0.5, 8, 0.5, () => get().Width, v => get().Width = v, v => $"{v:0.#} px");
        Slide(stack, "Strength", 0.1, 1, 0.05, () => get().Opacity, v => get().Opacity = v, v => $"{v * 100:0}%");
        Slide(stack, "Glow size", 0, 30, 1, () => get().Glow, v => get().Glow = v, v => v < 0.5 ? "off" : $"{v:0} px");
        Slide(stack, "Glow strength", 0, 1, 0.05, () => get().GlowStrength, v => get().GlowStrength = v, v => $"{v * 100:0}%");
        var pulse = new CheckBox { Content = "Pulse slowly", Foreground = Txt, Margin = new Thickness(0, 6, 0, 0) };
        pulse.Click += (_, _) => { get().Pulse = pulse.IsChecked == true; Changed?.Invoke(); };
        _syncs.Add(() => pulse.IsChecked = get().Pulse);
        stack.Children.Add(pulse);
        var reset = new Button { Content = "Default look", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        reset.Click += (_, _) => { set(defaults.Copy()); Sync(); Changed?.Invoke(); };
        stack.Children.Add(reset);
    }

    private void Slide(StackPanel stack, string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var dp = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var name = new TextBlock { Text = label, Foreground = Dim, Width = 92, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Foreground = Txt, Width = 50, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var sl = new Slider { Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 4, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(name, Dock.Left); DockPanel.SetDock(val, Dock.Right);
        dp.Children.Add(name); dp.Children.Add(val); dp.Children.Add(sl);
        bool syncing = false;
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); if (syncing) return; set(e.NewValue); Changed?.Invoke(); };
        _syncs.Add(() => { syncing = true; sl.Value = get(); val.Text = fmt(sl.Value); syncing = false; });
        stack.Children.Add(dp);
    }

    private ToggleButton OnOff(Func<bool> get, Action<bool> set)
    {
        var b = new ToggleButton { MinWidth = 64 };
        b.Click += (_, _) => { set(b.IsChecked == true); b.Content = b.IsChecked == true ? "On" : "Off"; Changed?.Invoke(); };
        _syncs.Add(() => { b.IsChecked = get(); b.Content = get() ? "On" : "Off"; });
        return b;
    }

    private static DockPanel Head(string title, FrameworkElement right)
    {
        var d = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(right, Dock.Right);
        d.Children.Add(right); d.Children.Add(Title(title));
        return d;
    }
    private static void Sep(StackPanel s) => s.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 14, 0, 14) });
    private static TextBlock Title(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Txt, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Note(string t) => new() { Text = t, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private static ToggleButton Choice(string t) => new() { Content = t, MinWidth = 44, Margin = new Thickness(0, 0, 6, 6) };
}
