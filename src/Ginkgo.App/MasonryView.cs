using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ginkgo.Core.Imaging;
using Ginkgo.Core.Layout;

namespace Ginkgo.App;

/// <summary>Own-drawn virtual masonry: only tiles in view are drawn; scrolling is done by us, not by ScrollViewer.</summary>
public sealed class MasonryView : FrameworkElement
{
    private MasonryResult? _layout;
    private ItemSpec[] _items = Array.Empty<ItemSpec>();
    private string[]? _labels;
    private double _offset;
    private float _tileTarget = 220;
    private const float Gap = 3;
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
    /// <summary>A tile was double-clicked.</summary>
    public event Action<int>? ItemActivated;
    /// <summary>Optional per-tile badge: 1 = GIF.</summary>
    public byte[]? Badges { get; set; }
    public int HoverIndex => _hoverIndex;

    private ThumbLoader? _thumbs;
    public ThumbLoader? Thumbs { get => _thumbs; set { _thumbs = value; InvalidateVisual(); } }
    public void Refresh() => InvalidateVisual();

    public MasonryView()
    {
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);     // the surface is drawn pixel-for-pixel
        Focusable = true;
        // fades are redrawn once per screen refresh (never faster)
        Loaded += (_, _) => CompositionTarget.Rendering += OnRefresh;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRefresh;
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

    private void OnRefresh(object? sender, EventArgs e) { if (_fadingNow) { _fadingNow = false; InvalidateVisual(); } }

    public double TotalHeight => _layout is null ? 0 : _layout.TotalHeight + _inset;
    private float _inset;
    /// <summary>Empty space above the first row (room for the bar that floats over the top).</summary>
    public float TopInset { get => _inset; set { if (Math.Abs(value - _inset) < 0.5f) return; _inset = value; Relayout(); } }
    public int ItemCount => _items.Length;
    public int LastVisibleCount { get; private set; }
    public double LastRenderMs { get; private set; }
    private double _maxRender, _maxPlan;
    /// <summary>Slowest draw and slowest plan since the last call (for the F3 readout).</summary>
    public (double render, double plan) TakeWorst() { var r = (_maxRender, _maxPlan); _maxRender = _maxPlan = 0; return r; }

    public float TileTarget
    {
        get => _tileTarget;
        set { float v = Math.Clamp(value, MinTile, MaxTile); if (Math.Abs(v - _tileTarget) < 0.01f) return; _tileTarget = v; Relayout(); TileTargetChanged?.Invoke(v); }
    }

    public const float MinTile = 60, MaxTile = 1800;
    /// <summary>Raised whenever the size changes (slider, Ctrl+wheel), so the slider can follow.</summary>
    public event Action<float>? TileTargetChanged;

    private bool _rows;
    /// <summary>false = equal-width columns; true = rows of equal height (the size is then the row height).</summary>
    public bool RowsMode { get => _rows; set { if (_rows == value) return; _rows = value; Relayout(); } }

    public void SetItems(ItemSpec[] items, string[]? labels, bool keepPosition = false)
    {
        double keep = _offset;
        InRelayout = true;
        try
        {
            _items = items; _labels = labels; _offset = 0; _target = 0; _hoverIndex = -1; Relayout();
            if (keepPosition) { _offset = Math.Clamp(keep, 0, MaxOffset); _target = _offset; InvalidateVisual(); ScrollChanged?.Invoke(); }
        }
        finally { InRelayout = false; }
    }

    /// <summary>Scroll by a distance (eased).</summary>
    public void ScrollBy(double dy) { _target = Math.Clamp(_target + dy, 0, MaxOffset); StartAnimating(); }

    /// <summary>Jump so that tile <paramref name="index"/> is in view (centred if it is not already).</summary>
    public void ScrollToIndex(int index)
    {
        if (_layout is null || index < 0 || index >= _items.Length) return;
        double top = _layout.Y[index], bottom = top + _layout.H[index];
        if (top >= _offset && bottom <= _offset + ActualHeight) return;
        Offset = top - (ActualHeight - _layout.H[index]) / 2;
    }

    /// <summary>True while the offset is being recalculated because the layout changed (zoom, resize), not because the person scrolled.</summary>
    public bool InRelayout { get; private set; }

    private void Relayout()
    {
        InRelayout = true;
        try { RelayoutCore(); } finally { InRelayout = false; }
    }

    private void RelayoutCore()
    {
        if (ActualWidth < 50 || _items.Length == 0) { _layout = null; InvalidateVisual(); ScrollChanged?.Invoke(); return; }
        // keep the same position in the content after a resize/zoom
        double frac = TotalHeight > 0 ? _offset / TotalHeight : 0;
        if (_rows) _layout = JustifiedLayout.Compute(_items, (float)ActualWidth, _tileTarget, Gap);
        else
        {
            int cols = MasonryLayout.ColumnsFor((float)ActualWidth, _tileTarget, Gap);
            _layout = MasonryLayout.Compute(_items, (float)ActualWidth, cols, Gap);
        }
        if (_inset > 0) for (int i = 0; i < _layout.Y.Length; i++) _layout.Y[i] += _inset;
        _offset = Math.Clamp(frac * TotalHeight, 0, Math.Max(0, TotalHeight - ActualHeight));
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
        if (e.ChangedButton == MouseButton.Left)
        {
            Focus();
            if (e.ClickCount == 2 && _hoverIndex >= 0) { ItemActivated?.Invoke(_hoverIndex); e.Handled = true; return; }
        }
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

    private readonly List<ThumbLoader.PlanItem> _planItems = new();
    private readonly List<int> _zone = new();
    private long _lastPlanTick;
    private double _planOff = double.NaN, _planTarget = double.NaN;
    private double _lastDir = 1;
    private bool _fadingNow;
    private static readonly Brush FadeBase = Freeze(new SolidColorBrush(Color.FromRgb(40, 40, 46)));

    /// <summary>How many bytes of pictures the view may ask to have warmed up beyond the screen.</summary>
    public long PrefetchBudgetBytes { get; set; } = 72L * 1024 * 1024;

    /// <summary>Look-ahead: windows ahead (in the scroll direction) whose sharp pictures are prepared in idle moments; one behind too. 0 = off.</summary>
    public int LookAheadWindows { get; set; } = 3;
    private double _prevRenderOff = double.NaN;

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_layout is null) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double offPx = Math.Round(_offset * dpi);       // scroll position in whole device pixels
        double off = offPx / dpi;
        double H = ActualHeight;
        if (double.IsNaN(_prevRenderOff) || Math.Abs(off - _prevRenderOff) > 0.5) { _thumbs?.NoteScroll(); _prevRenderOff = off; }
        _layout.GetVisible((float)off - 200, (float)(off + H) + 200, _visible);
        LastVisibleCount = _visible.Count;

        if (_thumbs is not null && _visible.Count > 0)
        {
            double dir = _target - _offset;
            if (Math.Abs(dir) > 1) _lastDir = Math.Sign(dir);
            long now = Stopwatch.GetTimestamp();
            bool moved = double.IsNaN(_planOff) || Math.Abs(off - _planOff) > H * 0.08 || Math.Abs(_target - _planTarget) > H * 0.08;
            if ((now - _lastPlanTick) * 1000 / Stopwatch.Frequency > (moved ? 250 : 800))
            {
                _lastPlanTick = now; _planOff = off; _planTarget = _target;
                long p0 = Stopwatch.GetTimestamp();
                _thumbs.PurgeStale();
                BuildPlan(off, H, dpi);
                BuildLookAhead(off, H, dpi);
                _maxPlan = Math.Max(_maxPlan, (Stopwatch.GetTimestamp() - p0) * 1000.0 / Stopwatch.Frequency);
            }
            _thumbs.SetWanted(_visible[0] - 12, _visible[^1] + 12);   // requests for tiles that have scrolled away are dropped
        }

        // All tiles are copied into one screen-sized picture, so the screen draws a single image per frame
        // (WPF is slow at drawing hundreds of separate small images).
        int pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi)), ph = Math.Max(1, (int)Math.Round(H * dpi));
        if (_surface is null || _surfW != pw || _surfH != ph)
        {
            _surface = new WriteableBitmap(pw, ph, 96, 96, PixelFormats.Pbgra32, null); _surfW = pw; _surfH = ph;
        }
        bool fading = false;
        long tick = Stopwatch.GetTimestamp();
        _overlay.Clear();
        _surface.Lock();
        try { fading = ComposeTiles(pw, ph, dpi, offPx, tick); }
        finally { _surface.AddDirtyRect(new Int32Rect(0, 0, pw, ph)); _surface.Unlock(); }
        dc.DrawImage(_surface, new Rect(0, 0, pw / dpi, ph / dpi));
        foreach (var (r, hovered, gif) in _overlay)
        {
            if (hovered) dc.DrawRectangle(null, _hover, r);
            if (gif && r.Width > 70) DrawBadge(dc, r);
        }
        _fadingNow = fading;
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        _maxRender = Math.Max(_maxRender, LastRenderMs);
    }

    private WriteableBitmap? _surface;
    private int _surfW, _surfH;
    private readonly List<(Rect r, bool hovered, bool gif)> _overlay = new();
    private static readonly uint BgPixel = TileBlit.Pack(18, 18, 20), FadePixel = TileBlit.Pack(40, 40, 46);
    private uint[]? _paletteBgra;

    private unsafe bool ComposeTiles(int pw, int ph, double dpi, double offPx, long tick)
    {
        _paletteBgra ??= Enumerable.Range(0, 24).Select(i => { var c = HsvToColor(i * 15.0, 0.35, 0.55); return TileBlit.Pack(c.R, c.G, c.B); }).ToArray();
        int stride = _surface!.BackBufferStride;
        var buf = new Span<byte>((void*)_surface.BackBuffer, stride * ph);
        TileBlit.Fill(buf, stride, pw, ph, 0, 0, pw, ph, BgPixel);
        bool fading = false;
        for (int vi = _visible.Count - 1; vi >= 0; vi--)   // bottom to top, so the newest-first queue serves the top tiles first
        {
            int i = _visible[vi];
            // snap every edge to a whole device pixel, so a tile is always copied pixel-for-pixel
            double l = Math.Round(_layout!.X[i] * dpi), t = Math.Round(_layout.Y[i] * dpi) - offPx;
            double rr = Math.Round((_layout.X[i] + _layout.W[i]) * dpi), bb = Math.Round((_layout.Y[i] + _layout.H[i]) * dpi) - offPx;
            int x = (int)l, y = (int)t, wPx = (int)(rr - l), hPx = (int)(bb - t);
            var e = _thumbs?.Get(i, wPx, hPx);
            if (e is not null)
            {
                double a = 1;
                if (e.FadeSecs > 0)
                {
                    a = (tick - e.FadeStart) / (e.FadeSecs * Stopwatch.Frequency);
                    if (a >= 1) { a = 1; e.FadeSecs = 0; e.Prev = null; } else fading = true;
                }
                var px = e.Pix;
                if (a < 1)
                {
                    var prev = e.Prev;
                    TileBlit.Blend(buf, stride, pw, ph, x, y, wPx, hPx, px.Data, px.W, px.H,
                        prev is null ? ReadOnlySpan<byte>.Empty : prev.Data, prev?.W ?? 0, prev?.H ?? 0, (int)(a * 256), FadePixel);
                }
                else if (px.W == wPx && px.H == hPx) TileBlit.Copy(buf, stride, pw, ph, x, y, px.Data, px.W, px.H);
                else TileBlit.CopyScaled(buf, stride, pw, ph, x, y, wPx, hPx, px.Data, px.W, px.H);       // only until the right size arrives
            }
            else TileBlit.Fill(buf, stride, pw, ph, x, y, wPx, hPx, _paletteBgra[(i * 7) % _paletteBgra.Length]);   // plain placeholder: cheap
            bool gif = e is not null && Badges is { } bd && i < bd.Length && bd[i] == 1;
            if (i == _hoverIndex || gif) _overlay.Add((new Rect(x / dpi, y / dpi, wPx / dpi, hPx / dpi), i == _hoverIndex, gif));
        }
        return fading;
    }

    /// <summary>
    /// What to warm up next: tiles ahead in the scroll direction first, then those behind, nearest first. When a flick is
    /// heading somewhere, the landing spot comes first. Pictures are limited by a memory budget; beyond that only the
    /// compressed thumbnails are fetched (cheap), so scrolling back or on is quick.
    /// </summary>
    private void BuildPlan(double off, double H, double dpi)
    {
        var layout = _layout!;
        bool flick = Math.Abs(_target - off) > H * 0.5;
        double lo = Math.Min(off, _target) - H * 1.5, hi = Math.Max(off, _target) + H * 3;
        layout.GetVisible((float)lo, (float)hi, _zone);

        double viewTop = off, viewBottom = off + H;
        var keyed = new List<(double p, int i)>(_zone.Count);
        foreach (int i in _zone)
        {
            double top = layout.Y[i], bottom = top + layout.H[i];
            if (bottom > viewTop && top < viewBottom) continue;           // on screen: handled directly
            double below = top - viewBottom, above = viewTop - bottom;
            double ahead = _lastDir >= 0 ? below : above, behind = _lastDir >= 0 ? above : below;
            double p = ahead > 0 ? ahead : behind * 2.5;
            if (flick)
            {
                double tg = bottom < _target ? _target - bottom : top > _target + H ? top - (_target + H) : 0;
                p = Math.Min(p, tg);
            }
            keyed.Add((p, i));
        }
        keyed.Sort((x, y) => x.p.CompareTo(y.p));

        _planItems.Clear();
        long budget = PrefetchBudgetBytes, used = 0;
        int encodedOnly = 0, n = 0;
        bool allowLarge = !flick && Math.Abs(_target - off) < H * 0.2;   // sharp versions only when the scroll has settled
        foreach (var (_, i) in keyed)
        {
            int wPx = (int)Math.Round(layout.W[i] * dpi), hPx = (int)Math.Round(layout.H[i] * dpi);
            long bytes = (long)wPx * hPx * 4;
            if (used + bytes <= budget && _planItems.Count < 2000) { used += bytes; _thumbs!.AppendPlan(i, wPx, hPx, _planItems, true, allowLarge && n++ < 24); }
            else if (encodedOnly < 3000) { int before = _planItems.Count; _thumbs!.AppendPlan(i, wPx, hPx, _planItems, false, false); encodedOnly += _planItems.Count - before; }
            else break;
        }
        _thumbs!.SetPlan(_planItems.ToArray());
    }

    private readonly List<int> _laZone = new();
    private void BuildLookAhead(double off, double H, double dpi)
    {
        int la = LookAheadWindows;
        var layout = _layout!;
        if (la <= 0 || Math.Abs(_target - off) > H * 0.2) { _thumbs!.SetLookAhead(Array.Empty<int>()); if (la <= 0) _thumbs.LookAheadWhy = "off"; return; }
        layout.GetVisible((float)(off - H), (float)(off + H * (1 + la)), _laZone);
        var keyed = new List<(double p, int i)>(_laZone.Count);
        foreach (int i in _laZone)
        {
            double top = layout.Y[i], bottom = top + layout.H[i];
            double below = top - (off + H), above = off - bottom;
            double ahead = _lastDir >= 0 ? below : above, behind = _lastDir >= 0 ? above : below;
            double p = ahead > 0 ? ahead : behind > 0 ? behind * 2.5 : 0;     // on screen: first
            int wPx = (int)Math.Round(layout.W[i] * dpi);
            if (_thumbs!.NeedsSharp(i, wPx)) keyed.Add((p, i));
        }
        keyed.Sort((a, b) => a.p.CompareTo(b.p));
        int n = Math.Min(keyed.Count, 1500);
        var arr = new int[n];
        for (int k = 0; k < n; k++) arr[k] = keyed[k].i;
        _thumbs!.SetLookAhead(arr);
    }

    private FormattedText? _gifText;
    private static readonly Brush BadgeBack = Freeze(new SolidColorBrush(Color.FromArgb(190, 0, 0, 0)));
    private void DrawBadge(DrawingContext dc, Rect r)
    {
        _gifText ??= new FormattedText("GIF", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var box = new Rect(r.Left + 6, r.Bottom - 22, _gifText.Width + 10, 16);
        dc.DrawRoundedRectangle(BadgeBack, null, box, 3, 3);
        dc.DrawText(_gifText, new Point(box.Left + 5, box.Top + 1));
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
