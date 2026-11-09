using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// Appearance: built-in themes and your own, every colour, the font with its size and density, the icon style, and the viewer's
/// background (a colour, a gradient or a pattern). Every change shows at once; your themes can be saved, updated, restored,
/// renamed, deleted, exported and imported.
/// </summary>
public sealed class AppearanceWindow : Window
{
    private readonly AppSettings _s;
    private readonly Action _changed;
    private AppTheme _t;
    private readonly ListBox _list = new() { Width = 210, Margin = new Thickness(0, 0, 14, 0) };
    private readonly StackPanel _editor = new();
    private readonly List<Action> _syncs = new();
    private bool _syncing;

    public AppearanceWindow(AppSettings s, Action changed)
    {
        _s = s; _changed = changed; _t = s.Theme.Copy();
        Title = "Appearance"; Width = 860; Height = 760; WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "Bar");
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(TextElement.ForegroundProperty, "Text");
        Theme.DarkTitleBar(this);

        var left = new DockPanel();
        var listButtons = new WrapPanel { Margin = new Thickness(0, 8, 14, 0) };
        Button B(string text, Action act, string tip = "") { var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip.Length > 0 ? tip : null }; b.Click += (_, _) => act(); listButtons.Children.Add(b); return b; }
        B("Save as new…", SaveNew, "Keep these colours and settings as a theme of your own");
        B("Update", UpdateOwn, "Save the changes into the selected theme of your own");
        B("Restore", Restore, "Undo your changes: back to the selected theme as it was saved");
        B("Rename…", Rename);
        B("Delete", Delete);
        B("Export…", Export, "Save the theme as a file to share or keep");
        B("Import…", Import, "Load a theme file");
        DockPanel.SetDock(listButtons, Dock.Bottom);
        left.Children.Add(listButtons);
        left.Children.Add(_list);
        _list.SelectionChanged += (_, _) =>
        {
            if (_syncing || _list.SelectedItem is not ListBoxItem { Tag: AppTheme t }) return;
            _t = t.Copy(); Push();
        };

        var scroll = new ScrollViewer { Content = _editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var grid = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(left, Dock.Left);
        grid.Children.Add(left); grid.Children.Add(scroll);
        Content = grid;
        BuildEditor();
        FillList();
        Sync();
    }

    private void FillList()
    {
        _syncing = true;
        _list.Items.Clear();
        void Head(string t) => _list.Items.Add(new ListBoxItem { Content = new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Opacity = 0.7 }, IsEnabled = false });
        Head("Built in");
        foreach (var t in ThemeKit.BuiltIn) _list.Items.Add(Item(t));
        var own = ThemeKit.LoadOwn();
        if (own.Count > 0) { Head("Yours"); foreach (var t in own) _list.Items.Add(Item(t, own: true)); }
        foreach (ListBoxItem it in _list.Items) if (it.Tag is AppTheme t && t.Name == _t.Name) _list.SelectedItem = it;
        _syncing = false;
    }

    private static ListBoxItem Item(AppTheme t, bool own = false)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in new[] { t.Window, t.Surface, t.Accent })
            sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(ButtonLook.C(c)), Margin = new Thickness(0, 0, 3, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) });
        sp.Children.Add(new TextBlock { Text = t.Name + (t.Light ? "  (light)" : ""), Margin = new Thickness(6, 0, 0, 0) });
        return new ListBoxItem { Content = sp, Tag = t, ToolTip = own ? "One of yours" : null };
    }

    /// <summary>Shows the current settings in effect, and keeps them.</summary>
    private void Push()
    {
        _s.Theme = _t.Copy();
        ThemeKit.Apply(_s.Theme, _s.Look);
        _changed();
        Sync();
    }

    private void Sync() { _syncing = true; try { foreach (var a in _syncs) a(); } finally { _syncing = false; } }

    private void BuildEditor()
    {
        void Title(string t) => _editor.Children.Add(new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 8) });
        Title("Colours");
        Colour("Behind the pictures", () => _t.Window, v => _t.Window = v);
        Colour("Bars and panels", () => _t.Surface, v => _t.Surface = v);
        Colour("Boxes inside panels", () => _t.Inner, v => _t.Inner = v);
        Colour("Fields", () => _t.Field, v => _t.Field = v);
        Colour("Edges", () => _t.Edge, v => _t.Edge = v);
        Colour("Text", () => _t.Text, v => _t.Text = v);
        Colour("Dim text", () => _t.Dim, v => _t.Dim = v);
        Colour("Accent", () => _t.Accent, v => _t.Accent = v);
        var light = Check("Light theme (buttons take the colours below)", () => _t.Light, v => _t.Light = v);
        Colour("Buttons (top)", () => _t.ButtonA, v => _t.ButtonA = v);
        Colour("Buttons (bottom)", () => _t.ButtonB, v => _t.ButtonB = v);
        Colour("Button text", () => _t.ButtonText, v => _t.ButtonText = v);

        Title("Text and spacing");
        var font = new ComboBox { Width = 240, IsEditable = false };
        foreach (var f in ThemeKit.Fonts) font.Items.Add(new ComboBoxItem { Content = f, FontFamily = new FontFamily(f), Tag = f });
        font.SelectionChanged += (_, _) => { if (_syncing || font.SelectedItem is not ComboBoxItem { Tag: string f }) return; _t.Font = f; Push(); };
        _syncs.Add(() => { font.SelectedItem = null; foreach (ComboBoxItem it in font.Items) if ((string)it.Tag == _t.Font) font.SelectedItem = it; });
        Row("Font", font);
        Slide("Text size", 10, 16, 0.5, () => _t.FontSize, v => _t.FontSize = v, v => $"{v:0.#}");
        Slide("Density", 0.8, 1.3, 0.05, () => _t.Density, v => _t.Density = v, v => v < 0.95 ? "compact" : v > 1.05 ? "roomy" : "normal");
        Choice("Icons", new[] { ("Fluent (Windows 11)", "Fluent"), ("Classic (Windows 10)", "Classic") }, () => _t.Icons, v => _t.Icons = v);

        Title("Viewer background");
        Choice("Kind", new[] { ("Colour", "Solid"), ("Gradient", "Gradient"), ("Gradient and pattern", "Pattern") }, () => _t.Backdrop, v => _t.Backdrop = v);
        Colour("Colour (gradient start)", () => _t.BackA, v => _t.BackA = v);
        Colour("Gradient end", () => _t.BackB, v => _t.BackB = v);
        Slide("Gradient angle", 0, 360, 15, () => _t.BackAngle, v => _t.BackAngle = v, v => $"{v:0}°");
        Choice("Pattern", new[] { ("Dots", "Dots"), ("Grid", "Grid"), ("Diagonal lines", "Diagonal"), ("Checks", "Checks") }, () => _t.Pattern, v => _t.Pattern = v);
        Colour("Pattern colour", () => _t.PatternColour, v => _t.PatternColour = v);
        Slide("Pattern strength", 0, 0.4, 0.01, () => _t.PatternOpacity, v => _t.PatternOpacity = v, v => $"{v * 100:0}%");
        Slide("Pattern size", 6, 80, 1, () => _t.PatternSize, v => _t.PatternSize = v, v => $"{v:0} px");
        _editor.Children.Add(new TextBlock { Text = "The buttons' shape, glow and outlines are in Button look (side menu).", Opacity = 0.75, Margin = new Thickness(0, 14, 0, 0), TextWrapping = TextWrapping.Wrap });
    }

    private void Row(string label, FrameworkElement control)
    {
        var d = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        d.Children.Add(new TextBlock { Text = label, Width = 190, VerticalAlignment = VerticalAlignment.Center });
        d.Children.Add(control); control.HorizontalAlignment = HorizontalAlignment.Left;
        _editor.Children.Add(d);
    }

    private void Colour(string label, Func<string> get, Action<string> set)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var swatch = new Border { Width = 26, Height = 22, CornerRadius = new CornerRadius(4), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
        var box = new TextBox { Width = 90, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "#RRGGBB" };
        void Apply()
        {
            if (_syncing) return;
            string t = box.Text.Trim(); if (!t.StartsWith('#')) t = "#" + t;
            try { ColorConverter.ConvertFromString(t); } catch { Sync(); return; }
            if (string.Equals(t, get(), StringComparison.OrdinalIgnoreCase)) return;
            set(t.ToUpperInvariant()); Push();
        }
        box.LostFocus += (_, _) => Apply();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(); };
        // a quick pick: lighter / darker
        Button Nudge(string text, double by)
        {
            var b = new Button { Content = text, Width = 26, Margin = new Thickness(6, 0, 0, 0), ToolTip = by > 0 ? "Lighter" : "Darker" };
            b.Click += (_, _) =>
            {
                var c = ButtonLook.C(get());
                byte F(byte v) => (byte)Math.Clamp(by > 0 ? v + (255 - v) * by : v * (1 + by), 0, 255);
                set($"#{F(c.R):X2}{F(c.G):X2}{F(c.B):X2}"); Push();
            };
            return b;
        }
        sp.Children.Add(swatch); sp.Children.Add(box); sp.Children.Add(Nudge("−", -0.12)); sp.Children.Add(Nudge("+", 0.12));
        _syncs.Add(() => { swatch.Background = new SolidColorBrush(ButtonLook.C(get())); if (!box.IsKeyboardFocused) box.Text = get(); });
        Row(label, sp);
    }

    private CheckBox Check(string label, Func<bool> get, Action<bool> set)
    {
        var c = new CheckBox { Content = label, Margin = new Thickness(0, 4, 0, 8) };
        c.SetResourceReference(ForegroundProperty, "Text");
        c.Click += (_, _) => { set(c.IsChecked == true); Push(); };
        _syncs.Add(() => c.IsChecked = get());
        _editor.Children.Add(c);
        return c;
    }

    private void Slide(string label, double min, double max, double step, Func<double> get, Action<double> set, Func<double, string> fmt)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var sl = new Slider { Width = 220, Minimum = min, Maximum = max, TickFrequency = step, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var val = new TextBlock { Width = 70, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        sl.ValueChanged += (_, e) => { val.Text = fmt(e.NewValue); if (_syncing) return; set(e.NewValue); Push(); };
        _syncs.Add(() => { sl.Value = get(); val.Text = fmt(sl.Value); });
        sp.Children.Add(sl); sp.Children.Add(val);
        Row(label, sp);
    }

    private void Choice(string label, (string text, string value)[] items, Func<string> get, Action<string> set)
    {
        var sp = new WrapPanel();
        var btns = new List<(ToggleButton b, string v)>();
        foreach (var (text, value) in items)
        {
            var b = new ToggleButton { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2) };
            string v = value;
            b.Click += (_, _) => { set(v); Push(); };
            btns.Add((b, v)); sp.Children.Add(b);
        }
        _syncs.Add(() => { foreach (var (b, v) in btns) b.IsChecked = v == get(); });
        Row(label, sp);
    }

    // ---- your themes ----

    private bool IsBuiltIn(string name) => ThemeKit.BuiltIn.Any(b => b.Name == name);

    private string? Ask(string title, string start)
    {
        var box = new TextBox { Text = start, MinWidth = 300, Margin = new Thickness(0, 0, 0, 12) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok); row.Children.Add(cancel);
        var p = new StackPanel { Margin = new Thickness(16) }; p.Children.Add(box); p.Children.Add(row);
        var w = new Window { Title = title, Content = p, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        w.SetResourceReference(BackgroundProperty, "Bar");
        Theme.DarkTitleBar(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (w.ShowDialog() != true) return null;
        string t = box.Text.Trim();
        return t.Length == 0 ? null : t;
    }

    private void SaveNew()
    {
        if (Ask("Save as a new theme", IsBuiltIn(_t.Name) ? _t.Name + " (mine)" : _t.Name) is not { } name) return;
        if (IsBuiltIn(name)) { MessageBox.Show(this, "That name belongs to a built-in theme. Choose another.", "Appearance"); return; }
        _t.Name = name; ThemeKit.SaveOwn(_t); Push(); FillList();
    }

    private void UpdateOwn()
    {
        if (IsBuiltIn(_t.Name)) { SaveNew(); return; }       // built-in themes can't be changed: save as yours instead
        ThemeKit.SaveOwn(_t); Push(); FillList();
    }

    private void Restore()
    {
        var saved = ThemeKit.BuiltIn.FirstOrDefault(b => b.Name == _t.Name) ?? ThemeKit.LoadOwn().FirstOrDefault(o => o.Name == _t.Name);
        if (saved is null) return;
        _t = saved.Copy(); Push();
    }

    private void Rename()
    {
        if (IsBuiltIn(_t.Name)) { MessageBox.Show(this, "Built-in themes keep their names. Save it as your own first.", "Appearance"); return; }
        if (Ask("Rename the theme", _t.Name) is not { } name || name == _t.Name || IsBuiltIn(name)) return;
        ThemeKit.DeleteOwn(_t.Name); _t.Name = name; ThemeKit.SaveOwn(_t); Push(); FillList();
    }

    private void Delete()
    {
        if (IsBuiltIn(_t.Name)) return;
        if (MessageBox.Show(this, $"Delete the theme “{_t.Name}”?", "Appearance", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        ThemeKit.DeleteOwn(_t.Name); _t = ThemeKit.BuiltIn[0].Copy(); Push(); FillList();
    }

    private void Export()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export theme", FileName = _t.Name + ".ginkgo-theme.json", Filter = "Ginkgo theme|*.json" };
        if (dlg.ShowDialog(this) == true) { try { ThemeKit.Export(_t, dlg.FileName); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export"); } }
    }

    private void Import()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import theme", Filter = "Ginkgo theme|*.json|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        if (ThemeKit.Import(dlg.FileName) is not { } t) { MessageBox.Show(this, "That file isn't a Ginkgo theme.", "Import"); return; }
        if (IsBuiltIn(t.Name)) t.Name += " (imported)";
        ThemeKit.SaveOwn(t); _t = t; Push(); FillList();
    }
}
