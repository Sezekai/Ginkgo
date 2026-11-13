using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Ginkgo.App;

/// <summary>
/// How the buttons look: shape, size, fills, outlines, glow and shadow. Everything is turned into app-wide brushes and effects
/// that the button templates use, so a change shows at once everywhere.
/// </summary>
public sealed class ButtonLook
{
    // shape and size
    public double Radius { get; set; } = 3;            // 0 = square ... Height / 2 = pill
    public double Height { get; set; } = 26;
    public double IconRadius { get; set; } = 99;
    public double IconSize { get; set; } = 32;
    public double IconGlyph { get; set; } = 13;
    public string IconColour { get; set; } = "#C4C4CC";

    // buttons that are off
    public string OffStyle { get; set; } = "Solid";   // Solid, Vertical, Horizontal, BottomGlow
    public string OffA { get; set; } = "#0E0E12";
    public string OffB { get; set; } = "#19191D";
    public string Text { get; set; } = "#E4E4E8";

    // switches that are on: General / NSFW / Very, and the others
    public string OnStyle { get; set; } = "BottomGlow";
    public string ContentA { get; set; } = "#2E8B4A";
    public string ContentB { get; set; } = "#8FE6A8";
    public string ContentText { get; set; } = "#FFFFFF";
    public string OtherA { get; set; } = "#6A3CF0";
    public string OtherB { get; set; } = "#C9A8FF";
    public string OtherText { get; set; } = "#FFFFFF";

    // edges and light
    public string Outline { get; set; } = "Lighter";        // None, Solid, Lighter, Gradient
    public string OutlineColour { get; set; } = "#0E0E12";
    public double OutlineWidth { get; set; } = 0.5;
    /// <summary>The gradient outline's colours (left, middle, right) and its direction in degrees.</summary>
    public string OutlineA { get; set; } = "#4F6BFF";
    public string OutlineB { get; set; } = "#A24BE0";
    public string OutlineC { get; set; } = "#F28A5B";
    public double OutlineAngle { get; set; }
    public double Shine { get; set; } = 0.35;              // light along the top edge, 0..1
    public double Glow { get; set; } = 0.75;              // glow round switches that are on, 0..1
    public double Shadow { get; set; } = 0;               // shadow under every button, 0..1

    public ButtonLook Copy() => (ButtonLook)MemberwiseClone();

    public static readonly (string name, Action<ButtonLook> set)[] Presets =
    {
        ("Your look (default)", l => { var d = new ButtonLook(); foreach (var p in typeof(ButtonLook).GetProperties()) p.SetValue(l, p.GetValue(d)); }),
        ("Flat", l => { l.OffStyle = "Solid"; l.OffA = "#222228"; l.OnStyle = "Solid"; l.Outline = "None"; l.Shine = 0; l.Glow = 0; l.Shadow = 0; l.Radius = 6; }),
        ("Pill", l => { l.Radius = 99; l.IconRadius = 99; l.OffStyle = "Vertical"; l.Outline = "Lighter"; l.Shine = 0.15; l.Glow = 0.35; }),
        ("Neon outline", l => { l.OffStyle = "Solid"; l.OffA = "#0E0E12"; l.OnStyle = "Solid"; l.Outline = "Gradient"; l.OutlineWidth = 1.5; l.Shine = 0; l.Glow = 0.6; l.Radius = 9;
                                 l.OtherA = "#1A1530"; l.ContentA = "#14261A"; }),
        ("Glow pill", l => { l.Radius = 99; l.OnStyle = "BottomGlow"; l.OtherA = "#6A3CF0"; l.OtherB = "#C9A8FF"; l.ContentA = "#2E8B4A"; l.ContentB = "#8FE6A8";
                              l.Outline = "Lighter"; l.Shine = 0.35; l.Glow = 0.75; }),
        ("Soft gradient", l => { l.Radius = 99; l.OnStyle = "Horizontal"; l.OtherA = "#B9A5FF"; l.OtherB = "#DCD4FF"; l.OtherText = "#141418";
                                  l.ContentA = "#C6F36B"; l.ContentB = "#E6FBC0"; l.ContentText = "#141418"; l.Outline = "None"; l.Glow = 0.2; l.Shine = 0; }),
        ("Bottom glow", l => { l.Radius = 12; l.OnStyle = "BottomGlow"; l.OtherA = "#FF5A12"; l.OtherB = "#FFC21A"; l.ContentA = "#1F7A4D"; l.ContentB = "#3FD6A0";
                                l.Outline = "None"; l.Shine = 0.25; l.Glow = 0.5; }),
    };

    // ---- turning the settings into brushes ----

    public static Color C(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return Colors.Gray; }
    }

    private static Color Shift(Color c, double by)
    {
        byte F(byte v) => (byte)Math.Clamp(by >= 0 ? v + (255 - v) * by : v * (1 + by), 0, 255);
        return Color.FromArgb(c.A, F(c.R), F(c.G), F(c.B));
    }

    private static Brush Fill(string style, Color a, Color b)
    {
        Brush br = style switch
        {
            "Solid" => new SolidColorBrush(a),
            "Horizontal" => Stops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) }, (b, 0), (a, 0.5), (b, 1)),
            "BottomGlow" => Stops(new RadialGradientBrush { Center = new Point(0.5, 1.15), GradientOrigin = new Point(0.5, 1.15), RadiusX = 0.75, RadiusY = 1.1 }, (b, 0), (a, 0.75)),
            _ => Stops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) }, (a, 0), (b, 1)),
        };
        br.Freeze();
        return br;
    }

    private static T Stops<T>(T g, params (Color c, double at)[] stops) where T : GradientBrush
    {
        foreach (var (c, at) in stops) g.GradientStops.Add(new GradientStop(c, at));
        return g;
    }

    private Brush Edge(Color fillA)
    {
        Brush b = Outline switch
        {
            "None" => Brushes.Transparent,
            "Lighter" => new SolidColorBrush(Shift(fillA, 0.25)),
            "Gradient" => Stops(new LinearGradientBrush(Colors.Black, Colors.Black, OutlineAngle) { GradientStops = new GradientStopCollection() },
                (C(OutlineA), 0), (C(OutlineB), 0.5), (C(OutlineC), 1)),
            _ => new SolidColorBrush(C(OutlineColour)),
        };
        if (!b.IsFrozen) b.Freeze();
        return b;
    }

    private Effect? GlowOf(Color c)
    {
        if (Glow <= 0.01) return null;
        var e = new DropShadowEffect { Color = c, BlurRadius = 4 + 26 * Glow, ShadowDepth = 0, Opacity = Math.Min(1, 0.2 + 0.7 * Glow) };
        e.Freeze(); return e;
    }

    public void Apply(ResourceDictionary r)
    {
        Color offA = C(OffA), offB = C(OffB);
        double rad = Math.Min(Radius, Height / 2);
        r["KeyRadius"] = new CornerRadius(rad);
        r["KeyRadiusL"] = new CornerRadius(rad, 0, 0, rad);      // split buttons: the switch, then its drop-down arrow
        r["KeyRadiusR"] = new CornerRadius(0, rad, rad, 0);
        r["KeyHeight"] = Height;
        r["KeyFill"] = Fill(OffStyle, offA, offB);
        r["KeyHover"] = Fill(OffStyle, Shift(offA, 0.08), Shift(offB, 0.08));
        r["KeyPress"] = Fill(OffStyle, Shift(offA, -0.15), Shift(offB, -0.15));
        r["KeyText"] = Frozen(C(Text));
        r["KeyBorder"] = Edge(offA);
        double ow = Outline == "None" ? 0 : OutlineWidth;
        r["KeyBorderWidth"] = new Thickness(ow);
        r["KeyBorderWidthL"] = new Thickness(ow, ow, 0, ow);
        r["KeyBorderWidthR"] = new Thickness(0, ow, ow, ow);
        var shine = Stops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) },
            (Color.FromArgb((byte)(Shine * 160), 255, 255, 255), 0), (Color.FromArgb(0, 255, 255, 255), 0.55));
        shine.Freeze();
        r["KeyShine"] = shine;
        if (Shadow > 0.01)
        {
            var sh = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4 + 12 * Shadow, ShadowDepth = 1 + 3 * Shadow, Direction = 270, Opacity = Math.Min(1, 0.3 + 0.6 * Shadow) };
            sh.Freeze(); r["KeyShadow"] = sh;
        }
        else r["KeyShadow"] = null;

        foreach (var (suffix, a, b, text) in new[] { ("", OtherA, OtherB, OtherText), ("C", ContentA, ContentB, ContentText) })
        {
            Color ca = C(a), cb = C(b);
            r["KeyOnFill" + suffix] = Fill(OnStyle, ca, cb);
            r["KeyOnHover" + suffix] = Fill(OnStyle, Shift(ca, 0.1), Shift(cb, 0.1));
            r["KeyOnBorder" + suffix] = Outline == "Gradient" ? Edge(ca) : Frozen(Shift(OnStyle == "BottomGlow" ? cb : ca, 0.3));
            r["KeyOnGlow" + suffix] = GlowOf(OnStyle == "BottomGlow" ? cb : ca);
            r["KeyOnText" + suffix] = Frozen(C(text));
        }

        r["IconRadius"] = new CornerRadius(Math.Min(IconRadius, IconSize / 2));
        r["IconSize"] = IconSize;
        r["IconGlyph"] = IconGlyph;
        r["IconText"] = Frozen(C(IconColour));
    }

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    /// <summary>Brushes for a switch drawn by code (the folder switches): 0 = off, 1 = on ("other" colours), 2 = partly on ("content" colours).</summary>
    public sealed record Kit(Brush Fill, Brush Hover, Brush Border, Thickness BorderWidth, Effect? Glow, Brush Text, Brush Shine, CornerRadius Radius, Effect? Shadow);

    public Kit KitFor(int state)
    {
        double ow = Outline == "None" ? 0 : OutlineWidth;
        var shine = Stops(new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) },
            (Color.FromArgb((byte)(Shine * 160), 255, 255, 255), 0), (Color.FromArgb(0, 255, 255, 255), 0.55));
        shine.Freeze();
        Effect? shadow = null;
        if (Shadow > 0.01) { var sh = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4 + 12 * Shadow, ShadowDepth = 1 + 3 * Shadow, Direction = 270, Opacity = Math.Min(1, 0.3 + 0.6 * Shadow) }; sh.Freeze(); shadow = sh; }
        var radius = new CornerRadius(Math.Max(0, Radius));
        if (state == 0)
        {
            Color a = C(OffA), b = C(OffB);
            return new Kit(Fill(OffStyle, a, b), Fill(OffStyle, Shift(a, 0.08), Shift(b, 0.08)), Edge(a), new Thickness(ow), null, Frozen(C(Text)), shine, radius, shadow);
        }
        var (ca, cb, text) = state == 1 ? (C(OtherA), C(OtherB), OtherText) : (C(ContentA), C(ContentB), ContentText);
        var border = Outline == "Gradient" ? Edge(ca) : Outline == "None" ? Brushes.Transparent : Frozen(Shift(OnStyle == "BottomGlow" ? cb : ca, 0.3));
        return new Kit(Fill(OnStyle, ca, cb), Fill(OnStyle, Shift(ca, 0.1), Shift(cb, 0.1)), border, new Thickness(ow), GlowOf(OnStyle == "BottomGlow" ? cb : ca),
            Frozen(C(text)), shine, radius, shadow);
    }
}
