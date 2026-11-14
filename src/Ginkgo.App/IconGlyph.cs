using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// An icon (or a few letters) drawn exactly in the middle of its space by the shape of what is drawn, not by the font's line
/// box. Text in an icon font sits a little high or low in a TextBlock because the font's ascent and descent are uneven; this
/// measures the drawn shape and centres that.
/// </summary>
public sealed class IconGlyph : FrameworkElement
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(object), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((IconGlyph)d)._geo = null));
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(nameof(Size), typeof(double), typeof(IconGlyph),
        new FrameworkPropertyMetadata(14.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((IconGlyph)d)._geo = null));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(IconGlyph),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(IconGlyph),
        new FrameworkPropertyMetadata(ThemeKit.IconFamily, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((IconGlyph)d)._geo = null));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(nameof(Background), typeof(Brush), typeof(IconGlyph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>Fills the whole space behind the icon (also makes all of it clickable).</summary>
    public Brush? Background { get => (Brush?)GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }

    public object? Glyph { get => GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    private Geometry? _geo;
    private Rect _ink;

    private Geometry? Shape()
    {
        if (_geo is not null) return _geo;
        string text = Glyph?.ToString() ?? "";
        if (text.Length == 0) return null;
        double dpi = 1;
        try { dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip; } catch { }
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(OwnFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), Math.Max(1, Size), Brushes.White, dpi);
        _geo = ft.BuildGeometry(new Point(0, 0));
        _geo.Freeze();
        _ink = _geo.Bounds.IsEmpty ? new Rect(0, 0, ft.Width, ft.Height) : _geo.Bounds;
        return _geo;
    }

    /// <summary>The icon font, unless a font was given to this icon itself (not just passed down from what it sits in).</summary>
    private FontFamily OwnFamily()
    {
        var src = DependencyPropertyHelper.GetValueSource(this, FontFamilyProperty).BaseValueSource;
        return src is BaseValueSource.Default or BaseValueSource.Inherited || FontFamily is null ? ThemeKit.IconFamily : FontFamily;
    }

    protected override Size MeasureOverride(Size available)
    {
        if (Shape() is null) return new Size(0, 0);
        // as wide as the drawn shape (at least the font size, so icons in a row line up), as tall as the font size
        return new Size(Math.Max(Size, Math.Ceiling(_ink.Width)), Math.Max(Size, Math.Ceiling(_ink.Height)));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (Background is not null) dc.DrawRectangle(Background, null, new Rect(RenderSize));
        var g = Shape();
        if (g is null) return;
        double x = (ActualWidth - _ink.Width) / 2 - _ink.X, y = (ActualHeight - _ink.Height) / 2 - _ink.Y;
        // whole device pixels, so straight lines in the icon stay sharp
        x = Math.Round(x); y = Math.Round(y);
        dc.PushTransform(new TranslateTransform(x, y));
        dc.DrawGeometry(Foreground, null, g);
        dc.Pop();
    }
}
