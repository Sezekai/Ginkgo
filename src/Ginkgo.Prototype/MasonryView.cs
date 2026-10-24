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
    private readonly Pen _hover = MakePen();
    private static Pen MakePen() { var p = new Pen(Brushes.White, 2); p.Freeze(); return p; }
    private int _hoverIndex = -1;
    private double _lastWidth;
    private double _target;                 // where scrolling is heading
    private double _velocity;               // px/sec for autoscroll
    private bool _animating;
    private long _lastTick;
    private bool _autoScroll;
    private double _autoAnchorY, _autoMouseY;
    private long _autoDownTick;
    private bool _autoSticky;
    private readonly Dictionary<int, FormattedText> _textCache = new();
    private static readonly Brush Background = Freeze(new SolidColorBrush(Color.FromRgb(18, 18, 20)));
    private static Brush Freeze(Brush b) { b.Freeze(); return b; }

    public event Action? ScrollChanged;
    public event Action<int>? HoverChanged;

    private ThumbLoader? _thumbs;
    public ThumbLoader? Thumbs { get => _thumbs; set { _thumbs = value; InvalidateVisual(); } }
    public void Refresh() => InvalidateVisual();

    public MasonryView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
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
            _offset = v; _target = v; InvalidateVisual(); ScrollChanged?.Invoke();
        }
    }

    private double MaxOffset => Math.Max(0, TotalHeight - ActualHeight);

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true; _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false; CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;
        double max = MaxOffset;
        if (_autoScroll)
        {
            double d = _autoMouseY - _autoAnchorY;
            double dead = 8;
            double speed = Math.Abs(d) <= dead ? 0 : Math.Sign(d) * (Math.Abs(d) - dead) * 14; // px/sec, grows with distance
            _target = Math.Clamp(_target + speed * dt, 0, max);
            _offset = _target;
        }
        else
        {
            // exponential ease toward the target, independent of frame rate
            double k = 1 - Math.Exp(-dt * 16);
            _target = Math.Clamp(_target, 0, max);
            double next = _offset + (_target - _offset) * k;
            if (Math.Abs(_target - _offset) < 0.5) { next = _target; StopAnimating(); }
            _offset = next;
        }
        InvalidateVisual();
        ScrollChanged?.Invoke();
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
        _items = items; _labels = labels; _offset = 0; _target = 0; _hoverIndex = -1; Relayout();
    }

    private void Relayout()
    {
        if (ActualWidth < 50 || _items.Length == 0) { _layout = null; InvalidateVisual(); ScrollChanged?.Invoke(); return; }
        // keep the same position in the content after a resize/zoom
        double frac = TotalHeight > 0 ? _offset / TotalHeight : 0;
        int cols = MasonryLayout.ColumnsFor((float)ActualWidth, _tileTarget, Gap);
        _layout = MasonryLayout.Compute(_items, (float)ActualWidth, cols, Gap);
        _offset = Math.Clamp(frac * _layout.TotalHeight, 0, Math.Max(0, _layout.TotalHeight - ActualHeight));
        _target = _offset;
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
        else
        {
            _target = Math.Clamp(_target - e.Delta * 2.0, 0, MaxOffset);
            StartAnimating();
        }
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton != MouseButton.Middle) { if (_autoScroll && _autoSticky) EndAutoScroll(); return; }
        if (_autoScroll) { EndAutoScroll(); return; }
        _autoScroll = true; _autoSticky = false;
        _autoAnchorY = e.GetPosition(this).Y; _autoMouseY = _autoAnchorY;
        _autoDownTick = Stopwatch.GetTimestamp();
        _target = _offset;
        CaptureMouse(); Cursor = Cursors.ScrollAll;
        StartAnimating();
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton != MouseButton.Middle || !_autoScroll) return;
        double heldMs = (Stopwatch.GetTimestamp() - _autoDownTick) * 1000.0 / Stopwatch.Frequency;
        double moved = Math.Abs(_autoMouseY - _autoAnchorY);
        if (heldMs < 250 && moved < 8) { _autoSticky = true; return; } // quick click: keep scrolling until next click
        EndAutoScroll();
    }

    private void EndAutoScroll()
    {
        _autoScroll = false; _autoSticky = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _target = _offset;
        StopAnimating();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout is null) return;
        var p = e.GetPosition(this);
        if (_autoScroll) { _autoMouseY = p.Y; return; }
        int found = -1;
        foreach (var i in _visible)
            if (p.X >= _layout.X[i] && p.X <= _layout.X[i] + _layout.W[i] &&
                p.Y + Math.Round(_offset) >= _layout.Y[i] && p.Y + Math.Round(_offset) <= _layout.Y[i] + _layout.H[i]) { found = i; break; }
        if (found != _hoverIndex) { _hoverIndex = found; HoverChanged?.Invoke(found); InvalidateVisual(); }
    }

    private readonly List<int> _ahead = new();
    private long _lastPrefetch;
    private int _pfLo = int.MaxValue, _pfHi = int.MinValue;
    private double _lastDir = 1;

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_layout is null) return;
        double off = Math.Round(_offset); // whole pixels: no shimmer
        double H = ActualHeight;
        _layout.GetVisible((float)off - 200, (float)(off + H) + 200, _visible);
        LastVisibleCount = _visible.Count;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        if (_thumbs is not null && _visible.Count > 0)
        {
            // Where are we heading? Warm up the tiles ahead (and where a flick will land) before they're needed.
            double dir = _target - _offset;
            if (Math.Abs(dir) > 1) _lastDir = Math.Sign(dir);
            long now = Stopwatch.GetTimestamp();
            if ((now - _lastPrefetch) * 1000 / Stopwatch.Frequency > 70)
            {
                _lastPrefetch = now;
                _pfLo = int.MaxValue; _pfHi = int.MinValue;
                int wpx = (int)Math.Round(_layout.ColumnWidth * dpi);
                void Zone(double top, double bottom)
                {
                    _layout.GetVisible((float)top, (float)bottom, _ahead);
                    // nearest to the screen first
                    double mid = off + H / 2;
                    _ahead.Sort((x, y) => Math.Abs(_layout.Y[x] - mid).CompareTo(Math.Abs(_layout.Y[y] - mid)));
                    foreach (int i in _ahead)
                    {
                        if (i < _pfLo) _pfLo = i; if (i > _pfHi) _pfHi = i;
                        _thumbs.Prefetch(i, wpx);
                    }
                }
                if (Math.Abs(_target - _offset) > H * 0.25) Zone(_target - 100, _target + H + 100); // where a flick is landing
                Zone(off - H * 0.5, off + H * 1.5);
                if (_lastDir >= 0) Zone(off + H * 1.5, off + H * 3.5); else Zone(off - H * 2.5, off - H * 0.5);
            }
            int lo = Math.Min(_visible[0], _pfLo), hi = Math.Max(_visible[^1], _pfHi);
            _thumbs.SetWanted(lo - 100, hi + 100);
        }

        foreach (int i in _visible)
        {
            var r = new Rect(Math.Round(_layout.X[i]), Math.Round(_layout.Y[i] - off), Math.Round(_layout.W[i]), Math.Round(_layout.H[i]));
            var img = _thumbs?.Get(i, (int)Math.Round(r.Width * dpi));
            if (img is not null)
            {
                dc.DrawImage(img, r);
                if (i == _hoverIndex) dc.DrawRectangle(null, _hover, r);
            }
            else dc.DrawRectangle(_palette[(i * 7) % _palette.Length], i == _hoverIndex ? _hover : null, r); // plain placeholder: cheap
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
