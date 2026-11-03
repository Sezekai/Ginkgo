using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The video progress line, drawn the same way in a grid tile and in the viewer: thin while you're not near it; under the mouse it
/// grows, lights up to the point you're over, shows that time above it and a round handle you can drag.
/// </summary>
public static class TimelineArt
{
    public static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
    public static readonly Brush HoverFill = Freeze(new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)));
    public static readonly Brush Played = Freeze(new SolidColorBrush(Color.FromRgb(0x5F, 0xAF, 0x6B)));
    public static readonly Brush Knob = Freeze(new SolidColorBrush(Color.FromRgb(0x7E, 0xC9, 0x8A)));
    public static readonly Brush BubbleBack = Freeze(new SolidColorBrush(Color.FromArgb(215, 12, 12, 14)));
    private static readonly Typeface Face = new("Segoe UI");
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public static string Clock(double sec)
    {
        if (double.IsNaN(sec) || sec < 0) sec = 0;
        var t = TimeSpan.FromSeconds(sec);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    /// <summary>
    /// Draws the line along <paramref name="line"/> (its full width; the line sits on the rectangle's vertical centre for the
    /// viewer, at its bottom for a tile). <paramref name="hover"/> is 0..1 under the mouse, NaN when the mouse isn't on it.
    /// </summary>
    public static void Draw(DrawingContext dc, double left, double width, double y, double frac, double hover, bool active, double lenSec, double ppd, double bubbleTop)
    {
        frac = double.IsNaN(frac) ? 0 : Math.Clamp(frac, 0, 1);
        double th = active ? 5 : 3;
        var r = new Rect(left, y - th / 2, Math.Max(0, width), th);
        dc.DrawRectangle(Track, null, r);
        if (!double.IsNaN(hover)) dc.DrawRectangle(HoverFill, null, new Rect(left, r.Top, width * Math.Clamp(hover, 0, 1), th));
        dc.DrawRectangle(Played, null, new Rect(left, r.Top, width * frac, th));
        if (active) dc.DrawEllipse(Knob, null, new Point(left + width * frac, y), 6.5, 6.5);
        if (!double.IsNaN(hover) && lenSec > 0)
        {
            var ft = new FormattedText(Clock(hover * lenSec), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11.5, Brushes.White, ppd);
            double bw = ft.Width + 12, bh = 18;
            double bx = Math.Clamp(left + width * hover - bw / 2, left, left + width - bw);
            var box = new Rect(bx, bubbleTop, bw, bh);
            dc.DrawRoundedRectangle(BubbleBack, null, box, 4, 4);
            dc.DrawText(ft, new Point(box.Left + 6, box.Top + (bh - ft.Height) / 2));
        }
    }
}

/// <summary>The viewer's progress line (see <see cref="TimelineArt"/>). Click or drag anywhere on it to move through the video.</summary>
public sealed class VideoTimeline : FrameworkElement
{
    /// <summary>Where the video is (0..1) and how long it is; asked for on every redraw.</summary>
    public Func<(double frac, double len)>? Source { get; set; }
    /// <summary>A seek: where (0..1), and whether the drag has finished.</summary>
    public event Action<double, bool>? SeekTo;
    public bool Dragging { get; private set; }
    private double _hover = double.NaN;
    private double _dragFrac;
    private long _lastSeek;

    public VideoTimeline() { Cursor = Cursors.Hand; Focusable = false; }

    private const double Pad = 8;
    private double FracAt(Point p) => Math.Clamp((p.X - Pad) / Math.Max(1, ActualWidth - 2 * Pad), 0, 1);

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));      // the whole height is clickable
        var (frac, len) = Source?.Invoke() ?? (0, 0);
        if (Dragging) frac = _dragFrac;
        bool active = IsMouseOver || Dragging;
        double y = ActualHeight / 2 + 8;
        TimelineArt.Draw(dc, Pad, ActualWidth - 2 * Pad, y, frac, Dragging ? _dragFrac : _hover, active, len, VisualTreeHelper.GetDpi(this).PixelsPerDip, y - 27);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hover = FracAt(e.GetPosition(this));
        if (Dragging)
        {
            _dragFrac = _hover;
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if ((now - _lastSeek) * 1000 >= 40 * System.Diagnostics.Stopwatch.Frequency) { _lastSeek = now; SeekTo?.Invoke(_dragFrac, false); }
        }
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); _hover = double.NaN; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Dragging = true; CaptureMouse();
        _dragFrac = FracAt(e.GetPosition(this));
        _lastSeek = System.Diagnostics.Stopwatch.GetTimestamp();
        SeekTo?.Invoke(_dragFrac, false);
        e.Handled = true; InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!Dragging) return;
        Dragging = false; ReleaseMouseCapture();
        SeekTo?.Invoke(_dragFrac, true);
        e.Handled = true; InvalidateVisual();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e) { base.OnLostMouseCapture(e); if (Dragging) { Dragging = false; SeekTo?.Invoke(_dragFrac, true); } }
}

/// <summary>A coloured outline drawn inside a tile's edge, with an optional soft glow inward.</summary>
public sealed class OutlineStyle
{
    public bool On { get; set; } = true;
    public string Colour { get; set; } = "#FFB000";
    /// <summary>Line thickness in pixels.</summary>
    public double Width { get; set; } = 2;
    /// <summary>0..1</summary>
    public double Opacity { get; set; } = 0.9;
    /// <summary>How far the glow reaches into the tile, in pixels (0 = none).</summary>
    public double Glow { get; set; }
    /// <summary>0..1</summary>
    public double GlowStrength { get; set; } = 0.4;
    /// <summary>Glow on the tile only while paused/watched; "Pulse" makes it breathe slowly.</summary>
    public bool Pulse { get; set; }
    public OutlineStyle Copy() => (OutlineStyle)MemberwiseClone();
}
