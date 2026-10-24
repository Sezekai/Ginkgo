using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ginkgo.Core.Layout;

namespace Ginkgo.Prototype;

/// <summary>Own-drawn virtual masonry: only tiles in view are drawn; scrolling is done by us, not by ScrollViewer.</summary>
public sealed class MasonryView : FrameworkElement
{
    private MasonryResult? _layout;
    private ItemSpec[] _items = Array.Empty<ItemSpec>();
    private string[]? _labels;
    private double _offset;
    private float _tileTarget = 220;
    private const float Gap = 6;
    private readonly List<int> _visible = new();
    private readonly Typeface _face = new("Segoe UI");
    private readonly Brush[] _palette;
    private readonly Pen _hover = new(Brushes.White, 2);
    private int _hoverIndex = -1;
    private double _lastWidth;

    public event Action? ScrollChanged;

    public MasonryView()
    {
        ClipToBounds = true;
        Focusable = true;
        _palette = new Brush[24];
        for (int i = 0; i < _palette.Length; i++)
        {
            var c = HsvToColor(i * 15.0, 0.35, 0.55);
            var b = new SolidColorBrush(c); b.Freeze(); _palette[i] = b;
        }
    }

    public double Offset
    {
        get => _offset;
        set
        {
            double max = Math.Max(0, TotalHeight - ActualHeight);
            double v = Math.Clamp(value, 0, max);
            if (Math.Abs(v - _offset) < 0.01) return;
            _offset = v; InvalidateVisual(); ScrollChanged?.Invoke();
        }
    }

    public double TotalHeight => _layout?.TotalHeight ?? 0;
    public int ItemCount => _items.Length;
    public int LastVisibleCount { get; private set; }
    public double LastRenderMs { get; private set; }

    public float TileTarget
    {
        get => _tileTarget;
        set { _tileTarget = Math.Clamp(value, 60, 800); Relayout(); }
    }

    public void SetItems(ItemSpec[] items, string[]? labels)
    {
        _items = items; _labels = labels; _offset = 0; Relayout();
    }

    private void Relayout()
    {
        if (ActualWidth < 50 || _items.Length == 0) { _layout = null; InvalidateVisual(); ScrollChanged?.Invoke(); return; }
        // keep the same position in the content after a resize/zoom
        double frac = TotalHeight > 0 ? _offset / TotalHeight : 0;
        int cols = MasonryLayout.ColumnsFor((float)ActualWidth, _tileTarget, Gap);
        _layout = MasonryLayout.Compute(_items, (float)ActualWidth, cols, Gap);
        _offset = Math.Clamp(frac * _layout.TotalHeight, 0, Math.Max(0, _layout.TotalHeight - ActualHeight));
        _lastWidth = ActualWidth;
        InvalidateVisual(); ScrollChanged?.Invoke();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (Math.Abs(ActualWidth - _lastWidth) > 0.5) Relayout(); else { InvalidateVisual(); ScrollChanged?.Invoke(); }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) TileTarget *= e.Delta > 0 ? 1.1f : 1 / 1.1f;
        else Offset -= e.Delta * 1.2;
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout is null) return;
        var p = e.GetPosition(this);
        int found = -1;
        foreach (var i in _visible)
            if (p.X >= _layout.X[i] && p.X <= _layout.X[i] + _layout.W[i] &&
                p.Y + _offset >= _layout.Y[i] && p.Y + _offset <= _layout.Y[i] + _layout.H[i]) { found = i; break; }
        if (found != _hoverIndex) { _hoverIndex = found; InvalidateVisual(); }
    }

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(18, 18, 20)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_layout is null) return;
        _layout.GetVisible((float)_offset - 200, (float)(_offset + ActualHeight) + 200, _visible);
        LastVisibleCount = _visible.Count;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (int i in _visible)
        {
            var r = new Rect(_layout.X[i], _layout.Y[i] - _offset, _layout.W[i], _layout.H[i]);
            dc.DrawRectangle(_palette[(i * 7) % _palette.Length], i == _hoverIndex ? _hover : null, r);
            if (r.Width > 70)
            {
                string text = _labels is not null ? _labels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
                var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 12,
                    Brushes.White, dpi) { MaxTextWidth = Math.Max(10, r.Width - 8), MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
                dc.DrawText(ft, new Point(r.X + 4, r.Y + 4));
            }
        }
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
