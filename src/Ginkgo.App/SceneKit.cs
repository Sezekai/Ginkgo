using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ginkgo.App;

/// <summary>
/// Draws the backgrounds of Appearance &gt; Backgrounds: gradients and night-sky scenes (a starfield with nebulae in the accent
/// colours, a night sky over a glowing horizon, the Milky Way, grey nebula clouds, an aurora, deep space, dusk). Each is drawn
/// once at the size it is shown and kept until the size or the settings change. The same settings always draw the same picture.
/// </summary>
public static class SceneKit
{
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    /// <summary>The scene as a picture <paramref name="w"/> x <paramref name="h"/> pixels (null for "Theme": the theme's own colour).</summary>
    public static BitmapSource? Render(Scene s, int w, int h)
    {
        if (s.Kind == "Theme") return null;
        w = Math.Clamp(w, 64, 3840); h = Math.Clamp(h, 64, 2400);
        // drawn in steps of 64 px so small size changes reuse the picture
        int W = (w + 63) / 64 * 64, H = (h + 63) / 64 * 64;
        string key = $"{s.Kind}|{s.A}|{s.B}|{s.C}|{s.Angle:0}|{s.Strength:0.00}|{s.Stars:0.00}|{W}x{H}";
        if (Cache.TryGetValue(key, out var hit)) return hit;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) Draw(dc, s, W, H);
        var bmp = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        bmp.Freeze();
        if (Cache.Count > 8) Cache.Clear();
        Cache[key] = bmp;
        return bmp;
    }

    /// <summary>A small preview of a scene (for the Appearance window).</summary>
    public static BitmapSource? Preview(Scene s, int w = 160, int h = 90) => Render(s, w, h);

    private static Color C(string hex) => ButtonLook.C(hex);
    private static Color A(Color c, double a) => Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B);

    private static void Glow(DrawingContext dc, double x, double y, double rx, double ry, Color c, double a, double W, double H)
    {
        var g = new RadialGradientBrush { Center = new Point(x / W, y / H), GradientOrigin = new Point(x / W, y / H), RadiusX = rx / W, RadiusY = ry / H };
        g.GradientStops.Add(new GradientStop(A(c, a), 0));
        g.GradientStops.Add(new GradientStop(A(c, a * 0.45), 0.45));
        g.GradientStops.Add(new GradientStop(A(c, 0), 1));
        g.Freeze();
        dc.DrawRectangle(g, null, new Rect(0, 0, W, H));
    }

    private static void Stars(DrawingContext dc, Random rnd, double W, double H, double density, Func<double, double, double>? weight = null, double bright = 1)
    {
        int n = (int)(W * H / 820 * density);
        var white = Color.FromRgb(255, 255, 255); var lav = Color.FromRgb(0xBE, 0xB4, 0xFF); var aqua = Color.FromRgb(0xA0, 0xFF, 0xEB); var warm = Color.FromRgb(0xFF, 0xE2, 0xB8);
        for (int i = 0; i < n; i++)
        {
            double x = rnd.NextDouble() * W, y = rnd.NextDouble() * H;
            if (weight is not null && rnd.NextDouble() > weight(x / W, y / H)) continue;
            bool big = rnd.NextDouble() > 0.965;
            double r = big ? 1.0 + rnd.NextDouble() * 0.7 : 0.3 + rnd.NextDouble() * 0.5;
            double k = rnd.NextDouble();
            var col = k > 0.88 ? lav : k > 0.8 ? aqua : k > 0.74 ? warm : white;
            double a = Math.Clamp((0.35 + rnd.NextDouble() * 0.6) * bright, 0, 1);
            dc.DrawEllipse(new SolidColorBrush(A(col, a)), null, new Point(x, y), r, r);
            if (big && rnd.NextDouble() > 0.5) dc.DrawEllipse(new SolidColorBrush(A(col, 0.12 * bright)), null, new Point(x, y), r * 3, r * 3);
        }
    }

    private static LinearGradientBrush Linear(double angle, params (Color c, double at)[] stops)
    {
        double rad = angle * Math.PI / 180;
        var g = new LinearGradientBrush { StartPoint = new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2), EndPoint = new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2) };
        foreach (var (c, at) in stops) g.GradientStops.Add(new GradientStop(c, at));
        g.Freeze();
        return g;
    }

    private static void Draw(DrawingContext dc, Scene s, double W, double H)
    {
        var rnd = new Random(7);
        double k = Math.Clamp(s.Strength, 0.1, 2), stars = Math.Clamp(s.Stars, 0, 3);
        Color a = C(s.A), b = C(s.B), c = C(s.C);
        var all = new Rect(0, 0, W, H);
        double M = Math.Max(W, H);
        switch (s.Kind)
        {
            case "Gradient":
                dc.DrawRectangle(Linear(s.Angle, (a, 0), (b, 1)), null, all);
                break;

            case "Starfield":
                dc.DrawRectangle(new SolidColorBrush(a), null, all);
                Glow(dc, W * 0.82, H * 0.14, M * 0.42, M * 0.42, c, 0.22 * k, W, H);
                Glow(dc, W * 0.18, H * 0.88, M * 0.45, M * 0.45, b, 0.24 * k, W, H);
                Glow(dc, W * 0.53, H * 0.49, M * 0.30, M * 0.30, Color.FromRgb(0xFF, 0x6F, 0xB1), 0.05 * k, W, H);
                Stars(dc, rnd, W, H, stars);
                break;

            case "Night sky":
                dc.DrawRectangle(Linear(90, (a, 0), (A(b, 1), 0.75), (Color.FromArgb(255, (byte)((b.R + c.R) / 2), (byte)((b.G + c.G) / 2), (byte)((b.B + c.B) / 2)), 1)), null, all);
                Glow(dc, W * 0.5, H * 1.05, W * 0.9, H * 0.45, c, 0.35 * k, W, H);
                Stars(dc, rnd, W, H, stars * 1.2, (x, y) => 1.15 - y, Math.Min(1.4, k));
                break;

            case "Milky Way":
            {
                dc.DrawRectangle(new SolidColorBrush(a), null, all);
                // the band runs corner to corner
                double ang = (s.Angle - 90) * Math.PI / 180, ux = Math.Cos(ang), uy = Math.Sin(ang);
                double cx = W / 2, cy = H / 2, spread = M * 0.12;
                double Dist(double x, double y) => Math.Abs((x - cx) * -uy + (y - cy) * ux);
                // a soft glow along the band, a warm core and a blue halo
                for (int i = -6; i <= 6; i++)
                {
                    double t = i / 6.0;
                    Glow(dc, cx + ux * t * M * 0.6, cy + uy * t * M * 0.6, M * 0.22, M * 0.22, b, 0.10 * k * (1 - Math.Abs(t) * 0.5), W, H);
                }
                Glow(dc, cx + ux * M * 0.08, cy + uy * M * 0.08, M * 0.16, M * 0.11, Color.FromRgb(0xF2, 0xA6, 0x5A), 0.28 * k, W, H);
                Glow(dc, cx - ux * M * 0.15, cy - uy * M * 0.15, M * 0.25, M * 0.2, c, 0.16 * k, W, H);
                // dense small stars along the band
                int n = (int)(W * H / 260 * stars);
                for (int i = 0; i < n; i++)
                {
                    double t = (rnd.NextDouble() - 0.5) * 1.4, g = (rnd.NextDouble() + rnd.NextDouble() + rnd.NextDouble() - 1.5) * spread * 1.3;
                    double x = cx + ux * t * M - uy * g, y = cy + uy * t * M + ux * g;
                    if (x < 0 || y < 0 || x > W || y > H) continue;
                    double r = 0.25 + rnd.NextDouble() * 0.5;
                    dc.DrawEllipse(new SolidColorBrush(A(Colors.White, (0.25 + rnd.NextDouble() * 0.6) * Math.Min(1.3, k))), null, new Point(x, y), r, r);
                }
                // dark dust lanes
                for (int i = 0; i < 60; i++)
                {
                    double t = (rnd.NextDouble() - 0.5) * 1.2, g = (rnd.NextDouble() - 0.5) * spread * 0.8;
                    double x = cx + ux * t * M - uy * g, y = cy + uy * t * M + ux * g;
                    Glow(dc, x, y, spread * (0.25 + rnd.NextDouble() * 0.5), spread * (0.15 + rnd.NextDouble() * 0.3), a, 0.35, W, H);
                }
                Stars(dc, rnd, W, H, stars * 0.7, (x, y) => 0.6 + 0.4 * (1 - Math.Min(1, Dist(x * W, y * H) / (spread * 3))));
                break;
            }

            case "Nebula":
                dc.DrawRectangle(new SolidColorBrush(a), null, all);
                for (int i = 0; i < 14; i++)
                {
                    double x = W * (0.45 + (rnd.NextDouble() - 0.5) * 0.5), y = H * (0.35 + (rnd.NextDouble() - 0.5) * 0.5);
                    Glow(dc, x, y, M * (0.08 + rnd.NextDouble() * 0.2), M * (0.06 + rnd.NextDouble() * 0.14), i % 3 == 0 ? c : b, (0.06 + rnd.NextDouble() * 0.1) * k, W, H);
                }
                for (int i = 0; i < 18; i++)
                {
                    double x = W * (0.45 + (rnd.NextDouble() - 0.5) * 0.45), y = H * (0.38 + (rnd.NextDouble() - 0.5) * 0.45);
                    Glow(dc, x, y, M * (0.03 + rnd.NextDouble() * 0.07), M * (0.02 + rnd.NextDouble() * 0.06), a, 0.5, W, H);
                }
                Glow(dc, W * 0.85, H * 0.12, M * 0.4, M * 0.35, Color.FromRgb(0xC8, 0xCC, 0xD6), 0.08 * k, W, H);
                Stars(dc, rnd, W, H, stars * 1.3);
                break;

            case "Aurora":
            {
                dc.DrawRectangle(Linear(90, (a, 0), (b, 1)), null, all);
                Stars(dc, rnd, W, H, stars * 0.8, (x, y) => 1.1 - y);
                // curtains: thin vertical strokes along a few slow waves, bright at the bottom edge and fading upward
                for (int wave = 0; wave < 3; wave++)
                {
                    double baseY = H * (0.35 + wave * 0.12), amp = H * (0.06 + 0.04 * wave), freq = 1.3 + wave * 0.7, phase = rnd.NextDouble() * 6;
                    var col = wave == 1 ? b : c;
                    for (double x = 0; x < W; x += 2)
                    {
                        double y0 = baseY + Math.Sin(x / W * Math.PI * 2 * freq + phase) * amp;
                        double tall = H * (0.18 + 0.12 * (0.5 + 0.5 * Math.Sin(x / W * 17 + phase * 3)));
                        double alpha = (0.05 + 0.07 * (0.5 + 0.5 * Math.Sin(x / W * 31 + wave))) * k;
                        var g = new LinearGradientBrush(A(col, 0), A(col, alpha), 90);
                        dc.DrawRectangle(g, null, new Rect(x, y0 - tall, 2.2, tall));
                    }
                    Glow(dc, W * 0.5, baseY, W * 0.7, H * 0.08, col, 0.10 * k, W, H);
                }
                break;
            }

            case "Deep space":
                dc.DrawRectangle(new SolidColorBrush(a), null, all);
                Glow(dc, W * 0.5, H * 0.5, M * 0.8, M * 0.8, b, 0.18 * k, W, H);
                Stars(dc, rnd, W, H, stars * 0.45, null, 0.9);
                break;

            case "Dusk":
                dc.DrawRectangle(Linear(90, (a, 0), (b, 0.55), (c, 1)), null, all);
                Glow(dc, W * 0.5, H * 1.1, W * 0.8, H * 0.35, c, 0.35 * k, W, H);
                Stars(dc, rnd, W, H, stars * 0.6, (x, y) => Math.Max(0, 0.9 - y * 1.4));
                break;

            default:
                dc.DrawRectangle(new SolidColorBrush(a), null, all);
                break;
        }
    }

    /// <summary>Built-in starting points (colours chosen for each scene).</summary>
    public static Scene Preset(string kind) => kind switch
    {
        "Gradient" => new Scene { Kind = kind, A = "#0F0C29", B = "#302B63", Angle = 135 },
        "Starfield" => new Scene { Kind = kind, A = "#05060B", B = "#2FD6B4", C = "#8B7BFF" },
        "Night sky" => new Scene { Kind = kind, A = "#02040C", B = "#0B1E3F", C = "#2B6C8F" },
        "Milky Way" => new Scene { Kind = kind, A = "#05060C", B = "#5868A8", C = "#2F7FD8", Angle = 120 },
        "Nebula" => new Scene { Kind = kind, A = "#0B0B0D", B = "#6E7480", C = "#8090A8" },
        "Aurora" => new Scene { Kind = kind, A = "#020611", B = "#7B5CFF", C = "#3DFFB0" },
        "Deep space" => new Scene { Kind = kind, A = "#030305", B = "#141A2E", C = "#2B2F45" },
        "Dusk" => new Scene { Kind = kind, A = "#0B1030", B = "#5A2D6B", C = "#F08A5D" },
        _ => new Scene { Kind = "Theme" },
    };
}
