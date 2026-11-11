using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ginkgo.App;

/// <summary>
/// The Ginkgo logo in the top bar, drawn from its outline so any fill, edge and light can go on it: its own colours, a colour, a
/// gradient, the button colours, iridescent, holographic or metal; an outline that can pulse or have a light run round it; a glow,
/// a shadow, and a shine that crosses it now and then (with sparkles). See <see cref="LogoLook"/>.
/// </summary>
public sealed class LogoView : Grid
{
    /// <summary>The logo's outline in a 100 x 100 box (traced from the logo picture).</summary>
    public const string Shape = "M88.8,11.5 L88.4,11.3 L87.3,11.5 L81.5,16.3 L80.9,16.6 L80.8,16.0 L82.7,10.5 L82.7,9.0 L82.5,8.5 L81.6,7.7 L78.9,6.7 L77.2,4.9 L76.7,4.7 L74.7,4.9 L73.6,4.4 L72.0,3.0 L71.2,2.4 L69.8,2.3 L69.3,2.5 L68.5,2.5 L67.0,1.7 L66.3,1.7 L65.7,2.1 L65.2,2.8 L64.3,5.0 L63.9,4.8 L63.1,3.2 L62.1,2.3 L61.2,2.2 L60.2,2.6 L59.6,2.6 L58.8,2.0 L58.0,2.0 L56.8,3.0 L55.0,2.6 L54.4,3.2 L53.8,4.1 L52.8,4.3 L52.3,4.7 L52.0,5.4 L52.1,6.9 L52.9,9.6 L54.2,12.4 L58.5,19.4 L60.2,23.0 L61.1,26.0 L61.6,29.7 L61.5,32.8 L60.6,35.9 L59.5,37.8 L55.6,42.7 L55.1,42.5 L53.4,41.0 L50.9,40.1 L49.1,38.4 L48.7,38.1 L46.6,37.9 L44.9,37.1 L44.0,37.1 L43.0,38.0 L39.1,46.5 L38.6,46.8 L38.1,45.2 L37.5,39.4 L36.9,37.7 L36.3,37.1 L35.2,36.7 L31.3,36.9 L29.1,35.9 L28.3,35.8 L25.8,37.1 L24.2,37.1 L21.6,36.3 L20.3,36.3 L19.4,36.7 L18.0,37.7 L15.8,37.6 L15.1,38.0 L14.5,39.2 L14.5,42.7 L14.1,42.6 L12.1,41.1 L11.5,40.9 L10.4,40.9 L9.6,41.3 L8.5,42.3 L7.9,42.4 L6.7,42.2 L6.3,42.4 L5.2,44.3 L3.2,44.9 L2.8,45.5 L2.7,46.8 L1.8,47.5 L1.3,48.2 L1.4,49.4 L2.3,51.0 L4.6,53.4 L6.6,55.1 L15.5,61.0 L19.2,64.0 L21.7,66.5 L23.0,68.3 L24.4,70.5 L25.7,73.1 L26.7,76.7 L26.7,80.2 L25.4,85.5 L24.8,90.3 L23.7,94.3 L22.5,97.2 L22.5,97.5 L22.8,97.8 L23.9,98.3 L24.2,98.3 L24.6,97.8 L26.8,92.7 L28.2,85.2 L29.2,82.1 L30.6,79.8 L31.6,78.7 L32.7,78.2 L34.9,78.1 L37.6,78.5 L42.7,80.3 L47.6,82.4 L51.9,84.5 L58.3,88.4 L61.6,90.0 L64.4,90.7 L65.8,90.6 L66.8,90.0 L67.4,88.7 L67.5,86.6 L67.8,85.9 L69.2,84.4 L69.3,84.1 L69.0,82.6 L69.3,80.6 L69.2,78.9 L68.5,77.3 L67.0,75.7 L67.0,75.5 L67.8,74.7 L67.9,72.0 L69.4,69.5 L69.7,68.6 L69.7,67.8 L69.2,66.0 L68.5,64.7 L68.4,63.5 L68.1,62.6 L66.4,60.1 L65.8,58.8 L65.7,58.0 L66.0,56.1 L66.0,54.2 L65.6,53.0 L65.1,52.2 L64.1,51.2 L62.1,50.1 L61.4,49.4 L60.8,48.1 L60.3,47.5 L59.0,46.9 L58.6,46.5 L58.3,45.7 L58.3,45.0 L57.8,43.7 L59.6,41.1 L61.7,39.2 L64.1,38.0 L65.1,38.0 L65.7,38.3 L68.6,40.0 L71.8,43.1 L75.9,47.9 L77.9,50.7 L81.0,55.3 L83.8,58.3 L85.4,59.0 L86.4,58.9 L87.1,58.4 L88.5,56.2 L90.3,55.3 L90.6,54.0 L91.6,52.7 L92.2,51.1 L92.2,50.1 L91.8,48.7 L91.8,48.1 L92.7,47.6 L93.3,46.2 L93.7,45.7 L95.9,44.2 L96.5,42.9 L96.6,40.5 L97.2,38.7 L96.7,35.5 L97.2,34.3 L98.5,32.1 L98.7,31.4 L98.7,30.2 L98.0,28.4 L96.9,27.0 L96.7,24.8 L95.8,23.6 L95.8,22.8 L96.1,22.0 L95.9,21.0 L94.8,19.5 L93.8,17.3 L92.4,16.0 L91.6,13.8 L90.2,13.0 Z";

    private static readonly Geometry Base = MakeBase();
    private static Geometry MakeBase() { var g = Geometry.Parse(Shape); g.Freeze(); return g; }
    private static readonly double Perimeter = Measure();
    private static BitmapImage? _png;
    private static BitmapImage Png => _png ??= Load();
    private static BitmapImage Load()
    {
        var b = new BitmapImage();
        b.BeginInit(); b.UriSource = new Uri("pack://application:,,,/Assets/ginkgo.png"); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
        return b;
    }

    private static double Measure()
    {
        var pg = PathGeometry.CreateFromGeometry(Base).GetFlattenedPathGeometry();
        double len = 0;
        foreach (var f in pg.Figures)
        {
            var p0 = f.StartPoint; var first = p0;
            foreach (var seg in f.Segments)
                if (seg is PolyLineSegment pl) foreach (var p in pl.Points) { len += (p - p0).Length; p0 = p; }
                else if (seg is LineSegment ls) { len += (ls.Point - p0).Length; p0 = ls.Point; }
            len += (first - p0).Length;
        }
        return Math.Max(100, len);
    }

    private readonly Border _tile = new() { IsHitTestVisible = false };
    private readonly Layer _shadow = new(), _glow = new(), _main = new();
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LogoLook _look = new();
    private Color _btnA = Color.FromRgb(0x6A, 0x3C, 0xF0), _btnB = Color.FromRgb(0xC9, 0xA8, 0xFF), _swA = Color.FromRgb(0x2E, 0x8B, 0x4A), _swB = Color.FromRgb(0x8F, 0xE6, 0xA8);

    public LogoView()
    {
        Children.Add(_tile); Children.Add(_shadow); Children.Add(_glow); Children.Add(_main);
        _shadow.Owner = _glow.Owner = _main.Owner = this;
        _shadow.Kind = 0; _glow.Kind = 1; _main.Kind = 2;
        _timer.Tick += (_, _) => { if (IsVisible) Tick(); };
        IsVisibleChanged += (_, _) => Restart();
        SnapsToDevicePixels = true;
        Apply(_look);
    }

    /// <summary>The button colours ("on" switches, and General/NSFW/Very), for the fills that match the buttons.</summary>
    public void SetButtonColours(ButtonLook l)
    {
        _btnA = ButtonLook.C(l.OtherA); _btnB = ButtonLook.C(l.OtherB); _swA = ButtonLook.C(l.ContentA); _swB = ButtonLook.C(l.ContentB);
        Redraw();
    }

    public void Apply(LogoLook l)
    {
        _look = l.Copy();
        double s = Math.Clamp(_look.Size, 16, 64);
        Width = s; Height = s;
        if (_look.Tile)
        {
            _tile.CornerRadius = new CornerRadius(s * 0.24);
            if (_look.TileColour.Length > 0) _tile.Background = new SolidColorBrush(ButtonLook.C(_look.TileColour)); else _tile.SetResourceReference(Border.BackgroundProperty, "Field");
            _tile.Margin = new Thickness(-s * 0.12);
            _tile.Visibility = Visibility.Visible;
        }
        else _tile.Visibility = Visibility.Collapsed;
        _shadow.Effect = _look.Shadow > 0.01 ? new DropShadowEffect { Color = Colors.Black, ShadowDepth = 1 + s / 30, BlurRadius = 3 + 4 * _look.Shadow, Opacity = Math.Min(1, 0.2 + 0.7 * _look.Shadow), Direction = 290 } : null;
        _shadow.Visibility = _look.Shadow > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        _glow.Visibility = _look.Glow > 0.01 ? Visibility.Visible : Visibility.Collapsed;
        if (_look.Glow > 0.01) _glow.Effect = new DropShadowEffect { Color = GlowColour(), ShadowDepth = 0, BlurRadius = 4 + 22 * _look.Glow, Opacity = 1 };
        Restart();
        Redraw();
    }

    private Color GlowColour()
    {
        if (_look.GlowColour.Length > 0) return ButtonLook.C(_look.GlowColour);
        return _look.Fill switch
        {
            "Solid" or "Gradient" => ButtonLook.C(_look.FillA),
            "Buttons" => _btnB, "Switches" => _swB,
            "Accent" => (Application.Current.TryFindResource("Accent") as SolidColorBrush)?.Color ?? Colors.MediumPurple,
            "Iridescent" => Color.FromRgb(0xC9, 0xB8, 0xFF), "Holographic" => Color.FromRgb(0x9B, 0xE7, 0xFF), "Metal" => Color.FromRgb(0xE8, 0xEC, 0xF2),
            _ => Color.FromRgb(0xFF, 0xD4, 0x5A),
        };
    }

    private void Restart()
    {
        if (_look.Animated && IsVisible) _timer.Start(); else _timer.Stop();
    }

    private void Tick()
    {
        double t = _clock.Elapsed.TotalSeconds * Math.Clamp(_look.Speed, 0.1, 4);
        if (_look.GlowPulse && _glow.Visibility == Visibility.Visible) _glow.Opacity = 0.45 + 0.55 * (0.5 + 0.5 * Math.Sin(t * 2.2));
        Redraw();
    }

    private void Redraw() { _main.InvalidateVisual(); _glow.InvalidateVisual(); _shadow.InvalidateVisual(); }

    // ---- drawing ----

    private sealed class Layer : FrameworkElement
    {
        public LogoView Owner = null!;
        public int Kind;          // 0 shadow, 1 glow, 2 the logo
        public Layer() { IsHitTestVisible = false; }
        protected override void OnRender(DrawingContext dc) => Owner.Draw(dc, Kind, ActualWidth);
    }

    private double T => _clock.Elapsed.TotalSeconds * Math.Clamp(_look.Speed, 0.1, 4);

    private static Point Dir(double deg, double turn)
    {
        double r = (deg + turn) * Math.PI / 180;
        return new Point(Math.Cos(r), Math.Sin(r));
    }

    private static LinearGradientBrush Grad(double angle, double turn, double shift, params Color[] cs)
    {
        var d = Dir(angle, turn);
        var g = new LinearGradientBrush { StartPoint = new Point(0.5 - d.X / 2, 0.5 - d.Y / 2), EndPoint = new Point(0.5 + d.X / 2, 0.5 + d.Y / 2), SpreadMethod = GradientSpreadMethod.Repeat };
        for (int i = 0; i < cs.Length; i++) g.GradientStops.Add(new GradientStop(cs[i], (double)i / (cs.Length - 1)));
        if (shift != 0) g.RelativeTransform = new TranslateTransform(shift, shift * 0.6);
        g.Freeze();
        return g;
    }

    private static Color C(string hex) => ButtonLook.C(hex);

    private Brush? FillBrush()
    {
        double t = T, flow = _look.FillFlow ? t : 0;
        return _look.Fill switch
        {
            "Solid" => new SolidColorBrush(C(_look.FillA)),
            "Gradient" => Grad(_look.FillAngle, flow * 25, 0, C(_look.FillA), C(_look.FillB), C(_look.FillA)),
            "Buttons" => Grad(90, flow * 25, 0, _btnA, _btnB, _btnA),
            "Switches" => Grad(90, flow * 25, 0, _swA, _swB, _swA),
            "Accent" => Application.Current.TryFindResource("Accent") as Brush,
            "Iridescent" => Grad(120, Math.Sin(t * 0.45) * 40, (t * 0.12) % 1,
                C("#86D8FF"), C("#F6C9FF"), C("#C7B8FF"), C("#A6F7EC"), C("#FFD9EC"), C("#9DB8FF"), C("#86D8FF")),
            "Holographic" => Grad(60, t * 18, (t * 0.18) % 1,
                C("#FF6FB1"), C("#FFD36B"), C("#86FF9E"), C("#6BE3FF"), C("#A98BFF"), C("#FF6FB1")),
            "Metal" => Grad(100, Math.Sin(t * 0.6) * 25, 0, C("#70757E"), C("#EEF1F5"), C("#9AA1AB"), C("#FAFBFC"), C("#7D838C")),
            _ => null,
        };
    }

    private Brush OutlineBrush()
    {
        double t = T;
        return _look.Outline switch
        {
            "Solid" => new SolidColorBrush(C(_look.OutlineA)),
            "Gradient" => Grad(0, t * 30, 0, C(_look.OutlineA), C(_look.OutlineB), C(_look.OutlineA)),
            "Rainbow" => Grad(0, t * 40, (t * 0.2) % 1, C("#FF5F6D"), C("#FFC371"), C("#7CFFB2"), C("#5CE1FF"), C("#B07CFF"), C("#FF5F6D")),
            "Silver" => Grad(45, t * 40, 0, C("#8A9099"), C("#FFFFFF"), C("#A9AFB8"), C("#F2F4F7"), C("#8A9099")),
            _ => new SolidColorBrush(C("#2A2A29")),
        };
    }

    private void Draw(DrawingContext dc, int kind, double size)
    {
        if (size <= 0) return;
        double k = size / 100.0;
        var g = Base.Clone(); g.Transform = new ScaleTransform(k, k); g.Freeze();
        var box = new Rect(0, 0, size, size);
        var fill = FillBrush();
        if (kind < 2)
        {
            // the shadow and the glow come from the shape itself (its effect draws them)
            dc.DrawGeometry(kind == 0 ? Brushes.Black : new SolidColorBrush(GlowColour()), null, g);
            return;
        }
        // the fill
        if (fill is null) dc.DrawImage(Png, box);
        else
        {
            dc.DrawGeometry(fill, null, g);
            if (_look.Fill is "Iridescent" or "Holographic")
            {
                // a soft sheen over the colours, like foil catching the light
                var sheen = Grad(135, Math.Sin(T * 0.8) * 30, 0, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(_look.Fill == "Holographic" ? (byte)110 : (byte)70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                dc.DrawGeometry(sheen, null, g);
            }
        }
        // the outline
        double w = Math.Max(0, _look.OutlineWidth) * size / 30.0;
        bool outline = _look.Outline != "None" && !(_look.Outline == "Original" && fill is null) && w > 0.05;
        if (outline)
        {
            var brush = OutlineBrush();
            double alpha = 1;
            if (_look.OutlineMotion == "Pulse") alpha = 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(T * 2.4));
            if (_look.OutlineMotion == "Orbit") alpha = 0.3;
            dc.PushOpacity(alpha);
            dc.DrawGeometry(null, new Pen(brush, w) { LineJoin = PenLineJoin.Round }, g);
            dc.Pop();
            if (_look.OutlineMotion == "Orbit")
            {
                // a light running round the edge: one bright dash that travels
                double per = Perimeter * k, dash = per * 0.16, wide = Math.Max(w * 2.2, 1.5);
                foreach (var (pw, op) in new[] { (wide, 0.35), (Math.Max(w, 1), 1.0) })
                {
                    var pen = new Pen(_look.Outline is "Original" ? Brushes.White : brush, pw)
                    {
                        LineJoin = PenLineJoin.Round, DashCap = PenLineCap.Round,
                        DashStyle = new DashStyle(new[] { dash / pw, (per - dash) / pw }, -(T * 0.35 % 1) * per / pw),
                    };
                    dc.PushOpacity(op); dc.DrawGeometry(null, pen, g); dc.Pop();
                }
            }
        }
        // the shine and the sparkles
        if (_look.Shine != "None")
        {
            double every = Math.Clamp(_look.ShineEvery, 1.5, 60), at = T % every;
            const double sweep = 0.9;
            if (at < sweep)
            {
                double p = at / sweep;                                         // 0 .. 1 across
                dc.PushClip(g);
                var band = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
                double c = -0.3 + 1.6 * p;
                band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), c - 0.18));
                band.GradientStops.Add(new GradientStop(Color.FromArgb(200, 255, 255, 255), c));
                band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), c + 0.18));
                dc.DrawRectangle(band, null, box);
                dc.Pop();
            }
            else if (_look.Shine == "Sparkle" && at < sweep + 0.8)
            {
                double p = (at - sweep) / 0.8;
                var rnd = new Random((int)(T / every));
                for (int i = 0; i < 3; i++)
                {
                    var pt = new Point(size * (0.25 + 0.55 * rnd.NextDouble()), size * (0.15 + 0.6 * rnd.NextDouble()));
                    double delay = i * 0.18, q = Math.Clamp((p - delay) / 0.55, 0, 1);
                    if (q <= 0 || q >= 1) continue;
                    double r = size * 0.11 * Math.Sin(q * Math.PI);
                    Star(dc, pt, r, Math.Sin(q * Math.PI));
                }
            }
        }
    }

    private static void Star(DrawingContext dc, Point c, double r, double alpha)
    {
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            double s = r * 0.22;
            ctx.BeginFigure(new Point(c.X, c.Y - r), true, true);
            ctx.LineTo(new Point(c.X + s, c.Y - s), true, false); ctx.LineTo(new Point(c.X + r, c.Y), true, false);
            ctx.LineTo(new Point(c.X + s, c.Y + s), true, false); ctx.LineTo(new Point(c.X, c.Y + r), true, false);
            ctx.LineTo(new Point(c.X - s, c.Y + s), true, false); ctx.LineTo(new Point(c.X - r, c.Y), true, false);
            ctx.LineTo(new Point(c.X - s, c.Y - s), true, false);
        }
        sg.Freeze();
        dc.PushOpacity(alpha);
        dc.DrawGeometry(Brushes.White, null, sg);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, c, r * 0.45, r * 0.45);
        dc.Pop();
    }
}
