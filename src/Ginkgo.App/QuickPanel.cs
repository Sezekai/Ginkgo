using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The Quick toggles pop-out (in the menu bar, like Optimisations): how videos behave in the grid. Every change takes effect straight away.
/// </summary>
public sealed class QuickPanel : Border
{
    public event Action? Changed;
    private readonly AppSettings _s;
    private readonly List<Action> _syncs = new();

    private static readonly Brush Dim = Frozen("#B5BAC0"), Txt = Frozen("#E4E4E8"), Line = Frozen("#2E2E36");
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }
    private static readonly string[] Swatches = { "#FFB000", "#FF8A00", "#FFD84A", "#FF4D4D", "#FF5FA2", "#B07CFF", "#4DA3FF", "#3FD0D0", "#5FAF6B", "#9BE07A", "#FFFFFF" };

    public QuickPanel(AppSettings s)
    {
        _s = s;
        Background = Frozen("#1C1C20"); BorderBrush = Frozen("#3A3A42"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(10);
        Width = 420;
        TextElement.SetFontFamily(this, new FontFamily("Segoe UI")); TextElement.SetFontSize(this, 12);
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 };

        var stack = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        Child = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640 };
        stack.Children.Add(new TextBlock { Text = "Quick toggles", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Txt, Margin = new Thickness(0, 0, 0, 12) });

        // ---- previews ----
        stack.Children.Add(Head("Hover previews", OnOff(() => _s.HoverPreviews, v => _s.HoverPreviews = v)));
        stack.Children.Add(Note("Resting the mouse on a video plays it in its tile."));
        Sep(stack);
        stack.Children.Add(Head("Sound in hover previews", OnOff(() => _s.PreviewSound, v => _s.PreviewSound = v)));
        stack.Children.Add(Note("Resting the mouse on a video plays it without sound unless this is on (at the volume you last set)."));
        Sep(stack);

        // ---- grid videos start muted ----
        stack.Children.Add(Head("Grid videos start muted", OnOff(() => _s.GridStartMuted, v => _s.GridStartMuted = v)));
        stack.Children.Add(Note("Off: a video you click plays with sound at the volume you last set. The speaker in the tile's corner turns it on or off."));
        Sep(stack);

        // ---- how many at once ----
        stack.Children.Add(Title("Videos playing at once"));
        stack.Children.Add(Note("Starting one more pauses the one you started longest ago."));
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var nums = new List<(ToggleButton b, int n)>();
        for (int n = 1; n <= 5; n++)
        {
            var b = Choice(n.ToString()); int k = n;
            b.Click += (_, _) => { _s.VideosAtOnce = k; Sync(); Changed?.Invoke(); };
            nums.Add((b, n)); row.Children.Add(b);
        }
        _syncs.Add(() => { foreach (var (b, n) in nums) b.IsChecked = n == Math.Clamp(_s.VideosAtOnce, 1, 5); });
        stack.Children.Add(row);
        Sep(stack);

        Sep(stack);
        stack.Children.Add(Head("Carry on where you left off", OnOff(() => _s.ResumeVideos, v => _s.ResumeVideos = v)));
        stack.Children.Add(Note("Videos start from where they were paused or left. Off: always from the beginning."));
        Sep(stack);
        stack.Children.Add(Head("Paused outline", OnOff(() => _s.PausedOutline.On, v => _s.PausedOutline.On = v)));
        Sep(stack);
        stack.Children.Add(Head("Watched outline", OnOff(() => _s.WatchedOutline.On, v => _s.WatchedOutline.On = v)));
        stack.Children.Add(Note("Their colours, glow and the rest are in Settings, under Video outlines."));
        Sync();
    }

    public string Describe()
    {
        static string O(OutlineStyle o) => $"on {o.On}, colour {o.Colour}, thickness {o.Width:0.#}, opacity {o.Opacity:0.00}, glow {o.Glow:0.#}, glow strength {o.GlowStrength:0.00}, pulse {o.Pulse}";
        return $"Ginkgo outlines\nPaused: {O(_s.PausedOutline)}\nWatched: {O(_s.WatchedOutline)}, counts as watched: {_s.WatchedRule}";
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
