using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Ginkgo.App;

/// <summary>
/// The section switch in the top bar: a pill with an icon for each section and a knob that slides to the one you are in.
/// Ctrl+B or M puts the keyboard on it; then 1-6 or the arrows move between sections, and Enter, Esc, Ctrl+B or M go back to
/// the pictures.
/// </summary>
public sealed class SectionSwitch : Border
{
    public static readonly (string key, string name, string glyph)[] Sections =
    {
        ("Home", "Home", "\uE80F"), ("Browse", "Browse", "\uE8B7"), ("Favourites", "Favourites", "\uE734"), ("Tools", "Tools", "\uE90F"),
        ("Appearance", "Appearance", "\uE790"), ("Settings", "Settings", "\uE713"),
    };

    /// <summary>A section was picked (by click or key).</summary>
    public event Action<string>? Picked;
    /// <summary>Enter, Esc, Ctrl+B or M: the keyboard goes back to the pictures.</summary>
    public event Action? Done;

    private readonly Grid _grid = new();
    private readonly Border _knob = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform _shift = new();
    private readonly TextBlock[] _icons = new TextBlock[Sections.Length];
    private readonly Border[] _slots = new Border[Sections.Length];
    private int _at;
    private double Slot = 38;

    public SectionSwitch()
    {
        Focusable = true; FocusVisualStyle = null;
        Padding = new Thickness(2);
        BorderThickness = new Thickness(1);
        SetResourceReference(BackgroundProperty, "Field");
        SetResourceReference(BorderBrushProperty, "Edge");
        _knob.Width = Slot;
        _knob.SetResourceReference(BackgroundProperty, "KeyOnFill");
        _knob.SetResourceReference(BorderBrushProperty, "KeyOnBorder");
        _knob.SetResourceReference(EffectProperty, "KeyOnGlow");
        _knob.BorderThickness = new Thickness(1);
        _knob.RenderTransform = _shift;
        _grid.Children.Add(_knob);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < Sections.Length; i++)
        {
            int at = i;
            var icon = new TextBlock { Text = Sections[i].glyph, FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            _icons[i] = icon;
            var slot = new Border { Width = Slot, Background = Brushes.Transparent, Child = icon, Cursor = Cursors.Hand, ToolTip = $"{Sections[i].name}  (Ctrl+{i + 1}, or Ctrl+B / M then {i + 1})" };
            // on press: the bar under the switch would otherwise take the mouse to drag the window
            slot.MouseLeftButtonDown += (_, e) => { Pick(at, fromKey: false); e.Handled = true; };
            slot.MouseEnter += (_, _) => Paint();
            slot.MouseLeave += (_, _) => Paint();
            _slots[i] = slot;
            row.Children.Add(slot);
        }
        _grid.Children.Add(row);
        Child = _grid;
        GotKeyboardFocus += (_, _) => Paint();
        LostKeyboardFocus += (_, _) => Paint();
        Loaded += (_, _) => { Shape(); Paint(); };
        SizeChanged += (_, _) => Shape();
    }

    private void Shape()
    {
        double h = Math.Max(10, ActualHeight - Padding.Top - Padding.Bottom - BorderThickness.Top - BorderThickness.Bottom);
        CornerRadius = new CornerRadius(ActualHeight / 2);
        // the slots are as wide as the switch is tall, the icons in proportion
        double slot = Math.Max(30, Math.Round(h * 1.08));
        if (Math.Abs(slot - Slot) > 0.5)
        {
            Slot = slot; _knob.Width = slot;
            for (int i = 0; i < _slots.Length; i++) { _slots[i].Width = slot; _icons[i].FontSize = Math.Round(h * 0.46); }
        }
        _knob.CornerRadius = new CornerRadius(h / 2);
        _knob.Height = h;
        _shift.X = _at * Slot;
    }

    /// <summary>Shows a section as the current one (no event).</summary>
    public void Show(string key)
    {
        int i = Array.FindIndex(Sections, s => s.key == key);
        if (i < 0 || i == _at) { Paint(); return; }
        _at = i;
        Slide();
        Paint();
    }

    public string Current => Sections[_at].key;

    private void Slide()
    {
        var anim = new DoubleAnimation(_at * Slot, TimeSpan.FromMilliseconds(190)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void Paint()
    {
        for (int i = 0; i < _icons.Length; i++)
        {
            string key = i == _at ? "KeyOnText" : _slots[i].IsMouseOver ? "Text" : "Dim";
            _icons[i].SetResourceReference(TextBlock.ForegroundProperty, key);
        }
        // the keyboard is here: an accent ring
        if (IsKeyboardFocused) { SetResourceReference(BorderBrushProperty, "Accent"); BorderThickness = new Thickness(2); Padding = new Thickness(1); }
        else { SetResourceReference(BorderBrushProperty, "Edge"); BorderThickness = new Thickness(1); Padding = new Thickness(2); }
    }

    private void Pick(int i, bool fromKey)
    {
        i = Math.Clamp(i, 0, Sections.Length - 1);
        bool changed = i != _at;
        _at = i; Slide(); Paint();
        if (changed || !fromKey) Picked?.Invoke(Sections[i].key);
    }

    /// <summary>Keys while the switch has the keyboard. True when handled.</summary>
    public bool HandleKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (key)
        {
            case Key.D1: case Key.NumPad1: Pick(0, true); return true;
            case Key.D2: case Key.NumPad2: Pick(1, true); return true;
            case Key.D3: case Key.NumPad3: Pick(2, true); return true;
            case Key.D4: case Key.NumPad4: Pick(3, true); return true;
            case Key.D5: case Key.NumPad5: Pick(4, true); return true;
            case Key.D6: case Key.NumPad6: Pick(5, true); return true;
            case Key.Left: case Key.Up: Pick(_at - 1, true); return true;
            case Key.Right: case Key.Down: case Key.Tab: Pick(_at + 1 >= Sections.Length && key == Key.Tab ? 0 : _at + 1, true); return true;
            case Key.Home: Pick(0, true); return true;
            case Key.End: Pick(Sections.Length - 1, true); return true;
            case Key.Enter: case Key.Escape: case Key.Space: Done?.Invoke(); return true;
            case Key.M: Done?.Invoke(); return true;
            case Key.B when ctrl: Done?.Invoke(); return true;
        }
        return false;
    }
}
