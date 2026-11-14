using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Ginkgo.App;

/// <summary>
/// The card a folder pop-out sits in: a title, a pointer up to the button that opened it, and its own background, edge, corners,
/// glow and shadow (Appearance &gt; Folder pop-outs).
/// </summary>
public sealed class PopCard : Grid
{
    private static PanelLook _look = new();
    private static readonly List<WeakReference<PopCard>> All = new();

    private readonly Border _shadow = new();
    private readonly Border _card = new();
    private readonly Path _notchFill = new() { Data = Geometry.Parse("M0,10 L10,0 L20,10 Z"), Width = 20, Height = 10, Stretch = Stretch.None };
    private readonly Path _notchEdge = new() { Data = Geometry.Parse("M0,10 L10,0 L20,10"), Width = 20, Height = 10, Stretch = Stretch.None };
    private readonly Canvas _notch = new() { Height = 10, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TextBlock _title = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(14, 10, 14, 0) };
    private double _notchX = 30;
    private static FontFamily? _font;
    private static double _size;

    /// <summary>The pop-outs' font and text size (Appearance → Text); open ones change at once.</summary>
    public static void SetFonts(FontFamily font, double size)
    {
        _font = font; _size = size;
        foreach (var w in All) if (w.TryGetTarget(out var c)) { TextElement.SetFontFamily(c, font); TextElement.SetFontSize(c, size); }
    }

    public PopCard(UIElement content, string title)
    {
        Margin = new Thickness(6, 0, 20, 20);
        _title.Text = title;
        _title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        TextElement.SetFontFamily(this, _font ?? (FontFamily)Application.Current.Resources["AppFont"]);
        if (_size > 0) TextElement.SetFontSize(this, _size);
        var stack = new DockPanel();
        DockPanel.SetDock(_title, Dock.Top);
        stack.Children.Add(_title); stack.Children.Add(content);
        _card.Child = stack;
        _shadow.Child = _card;
        Children.Add(_shadow);
        _notch.Children.Add(_notchFill); _notch.Children.Add(_notchEdge);
        Children.Add(_notch);
        All.Add(new WeakReference<PopCard>(this));
        Restyle();
    }

    /// <summary>Where the pointer sits, from the card's left edge (the middle of the button that opened it).</summary>
    public double NotchX { get => _notchX; set { _notchX = Math.Max(14, value); Place(); } }

    private void Place()
    {
        Canvas.SetLeft(_notchFill, _notchX - 10); Canvas.SetLeft(_notchEdge, _notchX - 10);
        Canvas.SetTop(_notchFill, 1); Canvas.SetTop(_notchEdge, 0);
    }

    /// <summary>A new look for every card.</summary>
    public static void SetLook(PanelLook l)
    {
        _look = l.Copy();
        All.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var w in All) if (w.TryGetTarget(out var c)) c.Restyle();
    }

    public void Restyle()
    {
        var l = _look;
        Color accent = (Application.Current.TryFindResource("Accent") as SolidColorBrush)?.Color ?? Colors.MediumPurple;
        Color bar = (Application.Current.TryFindResource("Bar") as SolidColorBrush)?.Color ?? Color.FromRgb(28, 28, 32);
        Color bg = l.Background.Length > 0 ? ButtonLook.C(l.Background) : bar;
        bg.A = (byte)(Math.Clamp(l.Opacity, 0.3, 1) * 255);
        var bgBrush = new SolidColorBrush(bg); bgBrush.Freeze();
        Color e1 = l.Border.Length > 0 ? ButtonLook.C(l.Border) : accent;
        Brush edge = l.Border2.Length > 0
            ? new LinearGradientBrush(e1, ButtonLook.C(l.Border2), 0)
            : new SolidColorBrush(e1);
        edge.Freeze();
        double bw = Math.Clamp(l.BorderWidth, 0, 6);
        Resources["ScrollEdge"] = edge;           // its scroll bar is outlined like the card
        _card.Background = bgBrush; _card.BorderBrush = edge; _card.BorderThickness = new Thickness(bw);
        _card.CornerRadius = new CornerRadius(Math.Clamp(l.Radius, 0, 30));
        _shadow.CornerRadius = _card.CornerRadius;
        _card.Effect = l.Glow > 0.01 ? new DropShadowEffect { Color = e1, ShadowDepth = 0, BlurRadius = 6 + 26 * l.Glow, Opacity = Math.Min(1, 0.25 + 0.7 * l.Glow) } : null;
        _shadow.Effect = l.Shadow > 0.01 ? new DropShadowEffect { Color = Colors.Black, ShadowDepth = 3 + 4 * l.Shadow, BlurRadius = 10 + 20 * l.Shadow, Opacity = Math.Min(1, 0.3 + 0.6 * l.Shadow) } : null;
        _notch.Visibility = l.Notch ? Visibility.Visible : Visibility.Collapsed;
        _shadow.Margin = new Thickness(0, l.Notch ? 9 : 0, 0, 0);
        _notchFill.Fill = bgBrush;
        _notchEdge.Stroke = edge; _notchEdge.StrokeThickness = Math.Max(1, bw);
        _title.Visibility = l.Title ? Visibility.Visible : Visibility.Collapsed;
        Place();
    }
}
