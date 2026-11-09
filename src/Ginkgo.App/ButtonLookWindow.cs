using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// Dialling in how the buttons look. Every change shows at once, here in the preview and in the main window; Save keeps it,
/// Cancel puts back what was there before.
/// </summary>
public sealed class ButtonLookWindow : Window
{
    private readonly ButtonLook _look;
    private readonly ButtonLook _before;
    private readonly Action _changed;
    private readonly StackPanel _form = new();
    private bool _building;

    public ButtonLookWindow(ButtonLook look, Action changed)
    {
        _look = look; _before = look.Copy(); _changed = changed;
        Title = "Button look"; Width = 640; Height = 820; MinWidth = 460; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = ThemeKit.Brush("Bar");
        Foreground = (Brush)FindResource("Text");
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(18) };
        // preview
        var preview = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        preview.Children.Add(new Button { Content = "Button", Height = 0 });
        var other = new ToggleButton { Content = "Images", IsChecked = true };
        var off = new ToggleButton { Content = "GIFs" };
        var content = new ToggleButton { Content = "General", IsChecked = true, Style = (Style)FindResource("ContentToggle") };
        var icon = new Button { Content = "", Style = (Style)FindResource("IconButton") };
        preview.Children.Clear();
        foreach (FrameworkElement el in new FrameworkElement[] { new Button { Content = "Select folders" }, other, off, content, icon })
        {
            el.Margin = new Thickness(0, 0, 10, 0);
            el.SetResourceReference(HeightProperty, "KeyHeight");
            preview.Children.Add(el);
        }
        var previewBox = new Border { Background = ThemeKit.Brush("Bar"), BorderBrush = (Brush)FindResource("Edge"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 14, 16, 14), Child = preview };
        DockPanel.SetDock(previewBox, Dock.Top);
        root.Children.Add(previewBox);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = "Save", Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => { DialogResult = true; };
        var cancel = new Button { Content = "Cancel", Height = 30 };
        cancel.Click += (_, _) => { Restore(); DialogResult = false; };
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        root.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _form });
        Content = root;
        Closing += (_, _) => { if (DialogResult != true) Restore(); };
        Build();
    }

    private void Restore()
    {
        foreach (var p in typeof(ButtonLook).GetProperties()) p.SetValue(_look, p.GetValue(_before));
        _changed();
    }

    private void Changed() { if (!_building) _changed(); }

    // ---- the form ----

    private void Build()
    {
        _building = true;
        _form.Children.Clear();
        Head("Start from a preset");
        var presets = new WrapPanel();
        foreach (var (name, set) in ButtonLook.Presets)
        {
            var b = new Button { Content = name, Height = 28, Margin = new Thickness(0, 0, 8, 8) };
            b.Click += (_, _) => { set(_look); _changed(); Build(); };
            presets.Children.Add(b);
        }
        _form.Children.Add(presets);

        Head("Shape and size");
        Slider("Roundness (square to pill)", 0, 20, () => Math.Min(_look.Radius, 20), v => _look.Radius = v >= 19.5 ? 99 : v, v => v >= 19.5 ? "pill" : $"{v:F0}");
        Slider("Height", 22, 38, () => _look.Height, v => _look.Height = v, v => $"{v:F0} px");
        Slider("Icon buttons: roundness", 0, 20, () => Math.Min(_look.IconRadius, 20), v => _look.IconRadius = v >= 19.5 ? 99 : v, v => v >= 19.5 ? "circle" : $"{v:F0}");
        Slider("Icon buttons: width", 24, 44, () => _look.IconSize, v => _look.IconSize = v, v => $"{v:F0} px");
        Slider("Icon size", 9, 18, () => _look.IconGlyph, v => _look.IconGlyph = v, v => $"{v:F0} px");
        Colour("Icon colour", () => _look.IconColour, v => _look.IconColour = v);

        Head("Buttons that are off");
        Choice("Fill", new[] { ("Solid", "Solid"), ("Vertical", "Gradient, top to bottom"), ("Horizontal", "Gradient, centre to sides"), ("BottomGlow", "Glow from the bottom") },
            () => _look.OffStyle, v => _look.OffStyle = v);
        Colour("Colour 1", () => _look.OffA, v => _look.OffA = v);
        Colour("Colour 2", () => _look.OffB, v => _look.OffB = v);
        Colour("Text", () => _look.Text, v => _look.Text = v);

        Head("Switches that are on");
        Choice("Fill", new[] { ("Solid", "Solid"), ("Vertical", "Gradient, top to bottom"), ("Horizontal", "Gradient, centre to sides"), ("BottomGlow", "Glow from the bottom") },
            () => _look.OnStyle, v => _look.OnStyle = v);
        Colour("General / NSFW / Very: colour 1", () => _look.ContentA, v => _look.ContentA = v);
        Colour("General / NSFW / Very: colour 2", () => _look.ContentB, v => _look.ContentB = v);
        Colour("General / NSFW / Very: text", () => _look.ContentText, v => _look.ContentText = v);
        Colour("Other switches: colour 1", () => _look.OtherA, v => _look.OtherA = v);
        Colour("Other switches: colour 2", () => _look.OtherB, v => _look.OtherB = v);
        Colour("Other switches: text", () => _look.OtherText, v => _look.OtherText = v);

        Head("Outline, light, glow and shadow");
        Choice("Outline", new[] { ("None", "None"), ("Solid", "Solid colour"), ("Lighter", "A lighter shade of the button"), ("Gradient", "Colour gradient (blue, purple, orange)") },
            () => _look.Outline, v => _look.Outline = v);
        Colour("Outline colour (solid)", () => _look.OutlineColour, v => _look.OutlineColour = v);
        Slider("Outline thickness", 0.5, 3, () => _look.OutlineWidth, v => _look.OutlineWidth = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:F1} px");
        Slider("Light along the top edge", 0, 1, () => _look.Shine, v => _look.Shine = v, v => $"{v * 100:F0}%");
        Slider("Glow round switches that are on", 0, 1, () => _look.Glow, v => _look.Glow = v, v => $"{v * 100:F0}%");
        Slider("Shadow under buttons", 0, 1, () => _look.Shadow, v => _look.Shadow = v, v => $"{v * 100:F0}%");
        _building = false;
    }

    private void Head(string text) =>
        _form.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, _form.Children.Count == 0 ? 0 : 16, 0, 8) });

    private DockPanel Row(string label, UIElement right)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Text") });
        _form.Children.Add(row);
        return row;
    }

    private void Slider(string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> show)
    {
        var value = new TextBlock { Width = 56, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)FindResource("Dim") };
        var s = new Slider { Minimum = min, Maximum = max, Width = 200, Value = Math.Clamp(get(), min, max), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 8, 0) };
        value.Text = show(s.Value);
        s.ValueChanged += (_, e) => { set(e.NewValue); value.Text = show(e.NewValue); Changed(); };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(s); right.Children.Add(value);
        Row(label, right);
    }

    private void Choice(string label, (string key, string name)[] options, Func<string> get, Action<string> set)
    {
        var box = new ComboBox { Width = 264, Height = 28 };
        foreach (var (key, name) in options) box.Items.Add(new ComboBoxItem { Content = name, Tag = key });
        foreach (ComboBoxItem it in box.Items) if ((string)it.Tag == get()) box.SelectedItem = it;
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem it) { set((string)it.Tag); Changed(); } };
        Row(label, box);
    }

    private void Colour(string label, Func<string> get, Action<string> set)
    {
        var pick = new ColourButton(get(), v => { set(v); Changed(); });
        Row(label, pick);
    }
}

/// <summary>A colour swatch that opens a picker: colours from the reference buttons, hue / saturation / brightness sliders and a hex box.</summary>
public sealed class ColourButton : StackPanel
{
    private static readonly string[] Swatches =
    {
        "#0E0E12", "#19191D", "#24242A", "#34343E", "#4A4A54", "#C4C4CC", "#FFFFFF", "#141418",
        "#7B3FE4", "#8A5CF6", "#B9A5FF", "#E58BC7", "#FF5A12", "#FF8A3D", "#FFC21A", "#F3A35B",
        "#2F7BFF", "#5AB0FF", "#8FE3FF", "#1F7A4D", "#2AA06A", "#55A863", "#3FD6A0", "#C6F36B",
    };

    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Action<string> _set;
    private readonly Popup _popup;
    private readonly Slider _h, _s, _v;
    private bool _syncing;

    public ColourButton(string start, Action<string> set)
    {
        _set = set;
        Orientation = Orientation.Horizontal;
        _swatch = new Border { Width = 40, Height = 24, CornerRadius = new CornerRadius(6), BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 90)), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand };
        _hex = new TextBox { Width = 86, Height = 26, Margin = new Thickness(8, 0, 0, 0), VerticalContentAlignment = VerticalAlignment.Center };
        Children.Add(_swatch); Children.Add(_hex);

        var grid = new WrapPanel { Width = 8 * 30 };
        foreach (var sw in Swatches)
        {
            var b = new Border { Width = 24, Height = 24, Margin = new Thickness(3), CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(ButtonLook.C(sw)), Cursor = System.Windows.Input.Cursors.Hand, ToolTip = sw };
            b.MouseLeftButtonUp += (_, _) => Pick(ButtonLook.C(sw));
            grid.Children.Add(b);
        }
        _h = new Slider { Minimum = 0, Maximum = 360, Margin = new Thickness(0, 10, 0, 0) };
        _s = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
        _v = new Slider { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var sl in new[] { _h, _s, _v }) sl.ValueChanged += (_, _) => { if (!_syncing) Pick(FromHsv(_h.Value, _s.Value, _v.Value), keepSliders: true); };
        var panel = new StackPanel { Margin = new Thickness(10) };
        panel.Children.Add(grid);
        panel.Children.Add(Label("Hue")); panel.Children.Add(_h);
        panel.Children.Add(Label("Saturation")); panel.Children.Add(_s);
        panel.Children.Add(Label("Brightness")); panel.Children.Add(_v);
        _popup = new Popup
        {
            PlacementTarget = _swatch, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
            Child = new Border { Background = ThemeKit.Brush("Bar"), BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 66)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Child = panel, Margin = new Thickness(0, 4, 0, 0) }
        };
        _swatch.MouseLeftButtonUp += (_, _) => _popup.IsOpen = !_popup.IsOpen;
        _hex.LostKeyboardFocus += (_, _) => TryHex();
        _hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) TryHex(); };
        Pick(ButtonLook.C(start), notify: false);
    }

    private static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 8, 0, 0), Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0xA0, 0xA6)) };

    private void TryHex()
    {
        string t = _hex.Text.Trim();
        if (!t.StartsWith("#")) t = "#" + t;
        try { Pick((Color)ColorConverter.ConvertFromString(t)); } catch { }
    }

    private void Pick(Color c, bool keepSliders = false, bool notify = true)
    {
        _swatch.Background = new SolidColorBrush(c);
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _hex.Text = hex;
        if (!keepSliders)
        {
            _syncing = true;
            var (h, s, v) = ToHsv(c);
            _h.Value = h; _s.Value = s; _v.Value = v;
            _syncing = false;
        }
        if (notify) _set(hex);
    }

    private static (double h, double s, double v) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }
}
