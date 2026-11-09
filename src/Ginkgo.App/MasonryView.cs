using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Ginkgo.Core.Imaging;
using Ginkgo.Core.Layout;

namespace Ginkgo.App;

/// <summary>A Connected Art set as the view shows it: where it starts among the tiles, how many, and whether it is a stacked tile.</summary>
public sealed record SetBlock(int First, int Count, string Key, string Name, int Total, bool Collapsed, bool Large = false);

/// <summary>Own-drawn virtual masonry: only tiles in view are drawn; scrolling is done by us, not by ScrollViewer.</summary>
public sealed class MasonryView : FrameworkElement
{
    private MasonryResult? _layout;
    private ItemSpec[] _items = Array.Empty<ItemSpec>();
    private string?[]? _labels;
    private double _offset;
    private float _tileTarget = 220;
    private float _gap = 3;
    /// <summary>The space between tiles (screen units).</summary>
    public float Gap { get => _gap; set { value = Math.Clamp(value, 0, 40); if (Math.Abs(_gap - value) < 0.01) return; _gap = value; Relayout(); } }
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
    private double _lastRenderSecs;
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
    /// <summary>A tile was clicked once (left button pressed and released on the same tile).</summary>
    public event Action<int>? ItemClicked;
    /// <summary>A tile was right-clicked.</summary>
    public event Action<int>? ItemContextRequested;
    /// <summary>Whether tile i is a favourite (shows the gold star).</summary>
    public Func<int, bool>? Starred { get; set; }
    /// <summary>The star in a tile's top-left corner was clicked.</summary>
    public event Action<int>? StarClicked;
    /// <summary>Connected Art sets among the tiles (outlined blocks, or stacked tiles that open when clicked).</summary>
    public List<SetBlock>? Sets { get; set; }
    /// <summary>A set's name label was clicked (the first tile's index): open or close it.</summary>
    public event Action<int>? SetLabelClicked;
    /// <summary>How many other copies of tile i's file are hidden (shown as a small "+1" in the corner).</summary>
    public Func<int, int>? CopiesOf { get; set; }
    private readonly List<(int first, Rect rect)> _setLabels = new();
    public static readonly Brush StarFill = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)));
    private (int index, Rect rect) _starHit = (-1, Rect.Empty);
    private int _downIndex = -1;
    /// <summary>Optional per-tile badge: 1 = GIF.</summary>
    public byte[]? Badges { get; set; }
    /// <summary>Text for each tile's badge (video length).</summary>
    public string?[]? BadgeText { get; set; }
    /// <summary>Plays the GIFs on screen.</summary>
    public GifAnimator? Gifs { get; set; }

    // ---- videos playing in their tiles (clicked ones, and the hover preview) ----
    public sealed class VideoTile
    {
        public VideoEngine E = null!;
        public bool Controls;
        public ThumbLoader.Pix? Pix;
        public byte[]? Buf;
        public int Seq = -1;
        public Rect Mute = Rect.Empty, Bar = Rect.Empty, Vol = Rect.Empty, Tile = Rect.Empty;
        public bool IconShown;
    }
    private readonly Dictionary<int, VideoTile> _videos = new();
    /// <summary>The speaker on a playing tile was clicked.</summary>
    public event Action<int>? VideoMuteClicked;
    /// <summary>A seek on a tile's progress line: tile, where (0..1), and whether the drag has finished.</summary>
    public event Action<int, double, bool>? VideoSeek;
    /// <summary>A tile's own volume, set on the slider under its speaker (0..100).</summary>
    public event Action<int, int>? VideoVolume;
    /// <summary>Tile i's video: watched (for its outline), and where it was paused (-1: not paused) for the Paused label.</summary>
    public Func<int, (bool watched, double pausedAt)>? VideoInfo { get; set; }
    /// <summary>The last frame of a video paused earlier whose player has been let go: shown instead of its thumbnail.</summary>
    public Func<int, ThumbLoader.Pix?>? FrozenFrame { get; set; }
    /// <summary>The frame tile i's player shows now (to keep when the player goes).</summary>
    public ThumbLoader.Pix? FrameOf(int i) => _videos.TryGetValue(i, out var vt) ? vt.Pix : null;
    private int _volDragIndex = -1;
    private Point _mouse = new(-1, -1);
    private long _mouseMovedAt;
    private bool _iconsUp;
    public OutlineStyle? PausedOutline { get; set; }
    public OutlineStyle? WatchedOutline { get; set; }
    private int _scrubIndex = -1, _dragIndex = -1;
    private double _scrubFrac = double.NaN, _dragFrac;
    private long _lastScrubSeek;

    /// <summary>Shows a player's frames in tile <paramref name="index"/> (null: the still picture again). Controls = speaker, time and a line you can drag.</summary>
    public void SetVideo(int index, VideoEngine? e, bool controls)
    {
        if (index < 0) return;
        _sigPrev.Remove(index);
        if (e is null) { _videos.Remove(index); if (_dragIndex == index) EndScrubDrag(); if (_volDragIndex == index) EndVolDrag(); }
        else if (_videos.TryGetValue(index, out var vt) && ReferenceEquals(vt.E, e)) vt.Controls = controls;
        else _videos[index] = new VideoTile { E = e, Controls = controls };
        InvalidateVisual();
    }
    public void ClearVideos() { foreach (var k in _videos.Keys) _sigPrev.Remove(k); _videos.Clear(); EndScrubDrag(); InvalidateVisual(); }
    public bool ShowsVideo(int index) => _videos.ContainsKey(index);
    private void EndVolDrag() { if (_volDragIndex >= 0) { _volDragIndex = -1; if (IsMouseCaptured && !_autoScroll && _dragIndex < 0) ReleaseMouseCapture(); } }
    private void VolFromY(VideoTile vt, int index, double y)
    {
        var tr = VolTrack(vt.Vol);
        int v = (int)Math.Round(Math.Clamp((tr.Bottom - y) / Math.Max(1, tr.Height), 0, 1) * 100);
        VideoVolume?.Invoke(index, v);
        InvalidateVisual();
    }
    private static Rect VolTrack(Rect panel) => new(panel.Left + panel.Width / 2 - 2, panel.Top + 8, 4, Math.Max(10, panel.Height - 30));
    private void EndScrubDrag() { if (_dragIndex >= 0) { _dragIndex = -1; if (IsMouseCaptured && !_autoScroll) ReleaseMouseCapture(); } }

    private readonly List<(int index, int w, int h, double dist)> _gifWanted = new();
    public int HoverIndex => _hoverIndex;

    private ThumbLoader? _thumbs;
    public ThumbLoader? Thumbs { get => _thumbs; set { _thumbs = value; InvalidateVisual(); } }
    /// <summary>New pictures arrived: redraw at the next screen refresh (at most once per refresh, however many arrive).</summary>
    public void Refresh() => _needsRender = true;
    private bool _needsRender;

    public MasonryView()
    {
        // The view is cut off a little past its own top and bottom edges, under the title bar and the bottom bar, which are drawn
        // over it. Cutting it off exactly at its edges left a dotted row of pixels there.
        ClipToBounds = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);     // the surface is drawn pixel-for-pixel
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);       // no soft half-pixel row where the view is cut off at its top and bottom edges
        Focusable = true;
        FocusVisualStyle = null;     // the default focus rectangle is a dotted line right round the view's edges
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
        _animating = true; _lastTick = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopAnimating()
    {
        if (!_animating) return;
        _animating = false; CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        long f0 = Stopwatch.GetTimestamp();
        try { OnFrameCore(e); } finally { Perf.Add(Stopwatch.GetTimestamp() - f0); }
    }

    private void OnFrameCore(EventArgs e)
    {
        // time between screen refreshes as the compositor counts it (steadier than reading the clock when our code happens to run)
        double now = e is RenderingEventArgs re ? re.RenderingTime.TotalSeconds : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        double dt = _lastTick == 0 ? 1.0 / 60 : Math.Clamp(now - _lastRenderSecs, 0, 0.05);
        if (e is RenderingEventArgs && now == _lastRenderSecs) return;       // same refresh seen twice: nothing to move
        _lastRenderSecs = now; _lastTick = 1;
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

    private void OnRefresh(object? sender, EventArgs e)
    {
        if (Gifs is not null && Gifs.Due()) _needsRender = true;            // a playing GIF moves on to its next frame
        // GPU grid: GIFs on screen move on at the governor's frame rate (and start again 150 ms after a scroll stops)
        if (_gpu is not null && Player is { OnScreen: > 0, Animate: true, Paused: false } p && p.NowMs - _lastGifFrameMs >= 1000 / Math.Max(1, p.Fps) - 2) _needsRender = true;
        foreach (var vt in _videos.Values)
        {
            // a video's next frame; a playing one also moves its progress line smoothly at the screen's rate
            if (vt.E.FrameSeq != vt.Seq || (vt.Controls && vt.E.Playing)) { _needsRender = true; break; }
        }
        if (_pulseOn || _iconsUp) _needsRender = true;
        if (_fadingNow || _needsRender) { _fadingNow = false; _needsRender = false; InvalidateVisual(); }
    }

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
    private bool _grid, _gridFit;
    /// <summary>The Grid layout: square cells of one size (<see cref="GridFit"/>: pictures keep their shape inside; otherwise cropped to fill).</summary>
    public bool GridMode { get => _grid; set { if (_grid == value) return; _grid = value; Relayout(); } }
    public bool GridFit { get => _gridFit; set { if (_gridFit == value) return; _gridFit = value; Relayout(); } }
    /// <summary>Short details for the tile under the mouse ("1920 x 1080  ·  2.4 MB"), drawn in its top-right corner (null: none).</summary>
    public Func<int, string?>? HoverDetails { get; set; }

    /// <summary>The tile at a point in the content (scroll offset + y), for the scrubber's label (-1: none).</summary>
    public int TileAtContent(double y)
    {
        if (_layout is null || _layout.Count == 0) return -1;
        var l = new List<int>();
        _layout.GetVisible((float)y, (float)y + 1, l);
        if (l.Count == 0) _layout.GetVisible((float)y - 400, (float)y + 400, l);
        return l.Count == 0 ? -1 : l[0];
    }

    /// <summary>How many leading items are folder covers, shown as a grid of equal book-cover tiles with their names underneath.</summary>
    public int CoverCount { get; private set; }
    public const float CoverAspect = 0.7f, CaptionHeight = 50, CoverGap = 14, SectionGap = 28;
    private float CoverTarget => Math.Clamp(_tileTarget, 110, 420);
    /// <summary>Rounded corners on the covers, in screen units (0 = square).</summary>
    public double CoverRadius { get; set; }

    public void SetItems(ItemSpec[] items, string?[]? labels, bool keepPosition = false, int coverCount = 0)
    {
        CoverCount = Math.Clamp(coverCount, 0, items.Length);
        double keep = _offset;
        InRelayout = true;
        try
        {
            _items = items; _labels = labels; _textCache.Clear(); _subCache.Clear(); _offset = 0; _target = 0; _hoverIndex = -1; Relayout();
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

    /// <summary>
    /// Tiles big enough for a video's time and Paused label: columns at least 200 wide (about 12 across a 2560 screen), or equal
    /// rows at least 140 tall.
    /// </summary>
    private bool LabelsFit(Rect r) => _rows && !_grid ? r.Height >= 140 : r.Width >= 200;

    private void RelayoutCore()
    {
        if (ActualWidth < 50 || _items.Length == 0) { _layout = null; InvalidateVisual(); ScrollChanged?.Invoke(); return; }
        // keep the same position in the content after a resize/zoom
        double frac = TotalHeight > 0 ? _offset / TotalHeight : 0;
        MasonryResult Files(ItemSpec[] items)
        {
            if (_grid) return UniformGrid.Compute(items, (float)ActualWidth, MasonryLayout.ColumnsFor((float)ActualWidth, _tileTarget, Gap), Gap, _gridFit);
            if (_rows) return JustifiedLayout.Compute(items, (float)ActualWidth, _tileTarget, Gap);
            int cols = MasonryLayout.ColumnsFor((float)ActualWidth, _tileTarget, Gap);
            return MasonryLayout.Compute(items, (float)ActualWidth, cols, Gap);
        }
        if (CoverCount > 0)
        {
            var rest = CoverCount < _items.Length ? Files(_items[CoverCount..]) : null;
            _layout = CoverGridLayout.Compose(CoverCount, (float)ActualWidth, CoverTarget, CoverAspect, CaptionHeight, CoverGap, rest, SectionGap);
        }
        else _layout = Files(_items);
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
            var mp = e.GetPosition(this);
            foreach (var (first, rect) in _setLabels)
                if (rect.Contains(mp)) { _downIndex = -1; if (e.ClickCount == 1) SetLabelClicked?.Invoke(first); e.Handled = true; return; }
            if (_starHit.index >= 0 && _starHit.index == _hoverIndex && _starHit.rect.Contains(mp))
            {
                _downIndex = -1;
                if (e.ClickCount == 1) StarClicked?.Invoke(_starHit.index);
                e.Handled = true; return;
            }
            foreach (var (vi, vt) in _videos)
            {
                if (!vt.Controls) continue;
                if (vt.IconShown && vt.Mute.Contains(mp)) { _downIndex = -1; VideoMuteClicked?.Invoke(vi); e.Handled = true; return; }
                if (!vt.Vol.IsEmpty && vt.Vol.Contains(mp))
                {
                    _downIndex = -1; _volDragIndex = vi; CaptureMouse();
                    VolFromY(vt, vi, mp.Y);
                    e.Handled = true; return;
                }
                if (vt.Bar.Contains(mp) && vt.Bar.Width > 0)
                {
                    _downIndex = -1;
                    _dragIndex = vi; _dragFrac = Math.Clamp((mp.X - vt.Bar.Left) / vt.Bar.Width, 0, 1);
                    CaptureMouse();
                    _lastScrubSeek = Stopwatch.GetTimestamp();
                    VideoSeek?.Invoke(vi, _dragFrac, false);
                    InvalidateVisual();
                    e.Handled = true; return;
                }
            }
            if (e.ClickCount == 2 && _hoverIndex >= 0) { _downIndex = -1; ItemActivated?.Invoke(_hoverIndex); e.Handled = true; return; }
            _downIndex = e.ClickCount == 1 ? _hoverIndex : -1;
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
        if (e.ChangedButton == MouseButton.Left && _volDragIndex >= 0) { EndVolDrag(); InvalidateVisual(); e.Handled = true; return; }
        if (e.ChangedButton == MouseButton.Left && _dragIndex >= 0)
        {
            int di = _dragIndex;
            EndScrubDrag();
            VideoSeek?.Invoke(di, _dragFrac, true);
            InvalidateVisual();
            e.Handled = true; return;
        }
        if (e.ChangedButton == MouseButton.Left)
        {
            int d = _downIndex; _downIndex = -1;
            if (d >= 0 && d == _hoverIndex && !_autoScroll) ItemClicked?.Invoke(d);
            return;
        }
        if (e.ChangedButton == MouseButton.Right) { if (_hoverIndex >= 0) { ItemContextRequested?.Invoke(_hoverIndex); e.Handled = true; } return; }
        if (e.ChangedButton != MouseButton.Middle || !_autoScroll) return;
        double heldMs = (Stopwatch.GetTimestamp() - _autoDownTick) * 1000.0 / Stopwatch.Frequency;
        double moved = Math.Abs(_autoMouseY - _autoAnchorY);
        if (heldMs < 250 && moved < 8) { _autoSticky = true; return; } // quick click: keep scrolling until next click
        EndAutoScroll();
    }

    /// <summary>
    /// The mouse pointer: hidden while it rests on a playing video (it comes back as soon as it moves), a hand on a progress line.
    /// </summary>
    private void UpdateCursor()
    {
        if (_autoScroll) return;
        Cursor want = Cursors.Arrow;
        if (_scrubIndex >= 0 || _dragIndex >= 0) want = Cursors.Hand;
        else if (_volDragIndex < 0 && _mouse.X >= 0)
        {
            long now = Stopwatch.GetTimestamp();
            bool still = now - _mouseMovedAt > Stopwatch.Frequency / 2;
            foreach (var vt in _videos.Values)
                if (vt.E.Playing && !vt.Tile.IsEmpty && vt.Tile.Contains(_mouse) && !(vt.IconShown && vt.Mute.Contains(_mouse)) && vt.Vol.IsEmpty)
                { if (still) want = Cursors.None; else _iconsUp = true; break; }
        }
        if (!ReferenceEquals(Cursor, want)) Cursor = want;
    }

    private void EndAutoScroll()
    {
        _autoScroll = false; _autoSticky = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        _target = _offset;
        StopAnimating();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _mouse = new Point(-1, -1);
        if (_scrubIndex >= 0) { _scrubIndex = -1; _scrubFrac = double.NaN; }
        if (!_autoScroll) Cursor = Cursors.Arrow;
        if (_videos.Count > 0) InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_layout is null) return;
        var p = e.GetPosition(this);
        if (_autoScroll) { _autoMouseY = p.Y; return; }
        if (Math.Abs(p.X - _mouse.X) + Math.Abs(p.Y - _mouse.Y) > 1) { _mouseMovedAt = Stopwatch.GetTimestamp(); if (_videos.Count > 0) InvalidateVisual(); }
        if (_starHit.index >= 0 && _starHit.rect.Contains(p) != _starHit.rect.Contains(_mouse)) InvalidateVisual();     // the star lights up under the mouse
        _mouse = p;
        if (_volDragIndex >= 0 && _videos.TryGetValue(_volDragIndex, out var vv)) { VolFromY(vv, _volDragIndex, p.Y); return; }
        if (_dragIndex >= 0 && _videos.TryGetValue(_dragIndex, out var dv) && dv.Bar.Width > 0)
        {
            _dragFrac = Math.Clamp((p.X - dv.Bar.Left) / dv.Bar.Width, 0, 1);
            long now = Stopwatch.GetTimestamp();
            if ((now - _lastScrubSeek) * 1000 >= 40 * Stopwatch.Frequency) { _lastScrubSeek = now; VideoSeek?.Invoke(_dragIndex, _dragFrac, false); }
            InvalidateVisual();
            return;
        }
        // over a playing tile's progress line: it grows and shows the time under the mouse
        int si = -1; double sf = double.NaN;
        foreach (var (vi, vt) in _videos)
            if (vt.Controls && vt.Bar.Contains(p) && vt.Bar.Width > 0) { si = vi; sf = Math.Clamp((p.X - vt.Bar.Left) / vt.Bar.Width, 0, 1); break; }
        if (si != _scrubIndex || (si >= 0 && Math.Abs(sf - _scrubFrac) > 0.0005)) { _scrubIndex = si; _scrubFrac = sf; UpdateCursor(); InvalidateVisual(); }
        else if (!ReferenceEquals(Cursor, Cursors.Arrow) && !ReferenceEquals(Cursor, Cursors.Hand)) UpdateCursor();
        int found = -1;
        // just below a playing video's progress line still counts as on that video (the line reaches a little past its edge)
        foreach (var (vi, vt) in _videos)
            if (vt.Controls && !vt.Bar.IsEmpty && vt.Bar.Contains(p)) { found = vi; break; }
        if (found < 0)
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

    // ---- drawing: the content is kept as a stack of 256-pixel-tall strips ("bands"), each a bitmap of its own. Scrolling only moves
    // the strips (the graphics card does that, nothing is copied by the processor); a strip is repainted when it first comes
    // into reach or when a picture in it arrives. ----
    private const int BandH = 256;
    private sealed class Band { public int K; public WriteableBitmap Bmp = null!; public bool Fresh; public readonly List<int> Work = new(); }
    private readonly Dictionary<int, Band> _bands = new();
    private readonly Stack<WriteableBitmap> _bandPool = new();
    private int _bandW;
    private object? _lastLayout;
    private double _lastDpi;
    private Dictionary<int, object?> _sigPrev = new(), _sigNow = new();
    private readonly List<TileDraw> _tiles = new();
    private readonly List<(Rect r, bool hovered, bool gif, int label, int index)> _overlay = new();
    private static readonly uint BgPixel = TileBlit.Pack(18, 18, 20), FadePixel = TileBlit.Pack(40, 40, 46);
    private uint[]? _paletteBgra;

    private struct TileDraw { public int Index, X, Y, W, H; public ThumbLoader.Entry? E; public double A; public bool Changed; public ThumbLoader.Pix? Over; }

    protected override void OnRender(DrawingContext dc)
    {
        var sw = Stopwatch.StartNew();
        dc.PushClip(new RectangleGeometry(new Rect(0, -EdgeBleed, ActualWidth, ActualHeight + 2 * EdgeBleed)));
        try { RenderInside(dc, sw); } finally { dc.Pop(); }
    }

    /// <summary>How far past its top and bottom edges the view draws (hidden under the bars next to it).</summary>
    private const double EdgeBleed = 3;

    private void RenderInside(DrawingContext dc, Stopwatch sw)
    {
        dc.DrawRectangle(Background, null, new Rect(0, -EdgeBleed, ActualWidth, ActualHeight + 2 * EdgeBleed));
        if (_layout is null) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        int offPx = (int)Math.Round(_offset * dpi);       // scroll position in whole device pixels
        double off = offPx / dpi;
        double H = ActualHeight;
        int pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi)), ph = Math.Max(1, (int)Math.Round(H * dpi));
        if (double.IsNaN(_prevRenderOff) || Math.Abs(off - _prevRenderOff) > 0.5) { _thumbs?.NoteScroll(); _prevRenderOff = off; }

        // the strips to keep: the screen plus one strip above and below
        int topPx = offPx - BandH, botPx = offPx + ph + BandH;
        _layout.GetVisible((float)(topPx / dpi), (float)(botPx / dpi), _visible);
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
            int sLo = int.MaxValue, sHi = -1;
            foreach (int i in _visible)
            {
                int ty = (int)Math.Round(_layout.Y[i] * dpi), th = (int)Math.Round((_layout.Y[i] + _layout.H[i]) * dpi);
                if (th > offPx && ty < offPx + ph) { if (i < sLo) sLo = i; if (i > sHi) sHi = i; }
            }
            _thumbs.SetScreen(sLo, sHi);
        }

        if (_gpu is null && (!ReferenceEquals(_layout, _lastLayout) || dpi != _lastDpi || pw != _bandW)) ResetBands(pw);
        _lastLayout = _layout; _lastDpi = dpi;
        int k0 = Math.Max(0, (int)Math.Floor(topPx / (double)BandH)), k1 = Math.Max(k0, (int)Math.Floor((botPx - 1) / (double)BandH));
        _dropKeys.Clear();
        foreach (var k in _bands.Keys) if (k < k0 || k > k1) _dropKeys.Add(k);
        foreach (var k in _dropKeys) { _bandPool.Push(_bands[k].Bmp); _bands.Remove(k); }
        for (int k = k0; k <= k1 && _gpu is null; k++)
            if (!_bands.ContainsKey(k))
                _bands[k] = new Band { K = k, Fresh = true, Bmp = _bandPool.Count > 0 ? _bandPool.Pop() : new WriteableBitmap(pw, BandH, 96, 96, PixelFormats.Pbgra32, null) };

        // what each tile shows now, and which ones differ from what is already painted
        bool fading = false;
        long tick = Stopwatch.GetTimestamp();
        _tiles.Clear(); _overlay.Clear(); _sigNow.Clear(); _hoverOver = null;
        foreach (var (vi, vt) in _videos)
        {
            vt.Mute = vt.Bar = vt.Vol = vt.Tile = Rect.Empty; vt.IconShown = false;
            int seq = vt.E.FrameSeq;
            if (seq != vt.Seq && vt.E.FileId >= 0 && vt.E.CopyFrame(ref vt.Buf, out int fw, out int fh))
            {
                vt.Seq = seq;
                if (vt.Pix is null || vt.Pix.W != fw || vt.Pix.H != fh) vt.Pix = new ThumbLoader.Pix { W = fw, H = fh, SrcW = fw };
                vt.Pix.Data = vt.Buf!;
                vt.Pix.Version++;
                _sigPrev.Remove(vi);                          // the strips repaint that tile
            }
        }
        _pulseOn = false; _iconsUp = false;
        for (int vi = _visible.Count - 1; vi >= 0; vi--)   // bottom to top, so the newest-first queue serves the top tiles first
        {
            int i = _visible[vi];
            // snap every edge to a whole device pixel, so a tile is always copied pixel-for-pixel
            double l = Math.Round(_layout.X[i] * dpi), t = Math.Round(_layout.Y[i] * dpi);
            double rr = Math.Round((_layout.X[i] + _layout.W[i]) * dpi), bb = Math.Round((_layout.Y[i] + _layout.H[i]) * dpi);
            int x = (int)l, y = (int)t, wPx = (int)(rr - l), hPx = (int)(bb - t);
            var e = _thumbs?.Get(i, wPx, hPx);
            double a = 1;
            if (e is not null && e.FadeSecs > 0)
            {
                a = (tick - e.FadeStart) / (e.FadeSecs * Stopwatch.Frequency);
                if (a >= 1) { a = 1; _thumbs!.EndFade(e); } else fading = true;
            }
            // a playing GIF shows its current frame instead of the still picture
            ThumbLoader.Pix? over = null;
            if (_videos.TryGetValue(i, out var vtile) && vtile.Pix is not null) over = vtile.Pix;
            else if (FrozenFrame is { } ff && Badges is { } fbd && i < fbd.Length && fbd[i] == 2 && ff(i) is { } frozen) over = frozen;
            else if (Gifs is not null && e is not null && Badges is { } gb && i < gb.Length && gb[i] == 1 && y < offPx + ph && y + hPx > offPx
                && (Player is null || _gpu is null || i == _hoverIndex))
            {
                over = Gifs.Frame(i);
                if (i == _hoverIndex) _hoverOver = over;
                double cy = y + hPx / 2.0 - offPx - ph / 2.0;
                _gifWanted.Add((i, wPx, hPx, Math.Abs(cy)));
            }
            object? sig = (object?)over ?? e?.Pix;
            _sigNow[i] = sig;
            bool changed = a < 1 || !_sigPrev.TryGetValue(i, out var old) || !ReferenceEquals(old, sig);
            _tiles.Add(new TileDraw { Index = i, X = x, Y = y, W = wPx, H = hPx, E = e, A = a, Changed = changed, Over = over });
            bool gif = e is not null && Badges is { } bd && i < bd.Length && bd[i] != 0;
            int below = i < CoverCount ? (int)(CaptionHeight * dpi) : 0;
            bool labelled = _labels is { } lb && i < lb.Length && lb[i] is not null && y < offPx + ph && y + hPx + below > offPx;
            bool starred = e is not null && ((Starred is { } stf && stf(i)) || (CopiesOf is { } cof && cof(i) > 0));
            if (i == _hoverIndex || gif || labelled || starred) _overlay.Add((new Rect(x / dpi, (y - offPx) / dpi, wPx / dpi, hPx / dpi), i == _hoverIndex, gif, labelled ? i : -1, i));
        }
        (_sigPrev, _sigNow) = (_sigNow, _sigPrev);
        if (Gifs is not null)
        {
            _gifWanted.Sort((p, q) => p.dist.CompareTo(q.dist));
            Gifs.SetVisible(_gifWanted.Select(g => (g.index, g.w, g.h)).ToList());
            _gifWanted.Clear();
        }
        Perf.PrepMax = Math.Max(Perf.PrepMax, sw.Elapsed.TotalMilliseconds);

        long pt0 = Stopwatch.GetTimestamp();
        if (!RenderGpu(dc, offPx, pw, ph, dpi))
        {
            if (_bands.Count == 0 || _bandW != pw)
            {
                // just came back from the GPU grid: make the strips again
                ResetBands(pw); _lastLayout = _layout; _lastDpi = dpi;
                for (int k = k0; k <= k1; k++)
                    _bands[k] = new Band { K = k, Fresh = true, Bmp = new WriteableBitmap(pw, BandH, 96, 96, PixelFormats.Pbgra32, null) };
            }
            PaintBands(pw, k0, k1);
            foreach (var kv in _bands.Values)
                dc.DrawImage(kv.Bmp, new Rect(0, (kv.K * BandH - offPx) / dpi, pw / dpi, BandH / dpi));
        }
        Perf.PaintMax = Math.Max(Perf.PaintMax, Perf.Ms(Stopwatch.GetTimestamp() - pt0));
        _starHit = (-1, Rect.Empty);
        foreach (var (r, hovered, gif, label, index) in _overlay)
        {
            if (label >= 0) { if (label < CoverCount) DrawCaptionBelow(dc, r, label, dpi); else DrawCaption(dc, r, label, dpi); }
            if (hovered) dc.DrawRectangle(null, _hover, r);
            bool playingHere = _videos.TryGetValue(index, out var vt) && vt.E.FileId >= 0;
            bool isVideo = gif && Badges is { } vb && index < vb.Length && vb[index] == 2;
            var info = isVideo && VideoInfo is { } vinf ? vinf(index) : (false, -1.0);
            if (isVideo) DrawVideoOutline(dc, r, playingHere ? vt : null, info.Item1, info.Item2);
            if (playingHere) DrawVideoBar(dc, r, index, vt!);
            else if (isVideo && info.Item2 >= 0) DrawPausedLabel(dc, r, index, info.Item2);
            else if (gif && r.Width > 70 && (!isVideo || LabelsFit(r))) DrawBadge(dc, r, index);
            if (label < 0 || index >= CoverCount) DrawStar(dc, r, index, hovered);
            if (hovered && index >= CoverCount && !playingHere && HoverDetails is { } hd && r.Width >= 150 && r.Height >= 60 && hd(index) is { Length: > 0 } det) DrawHoverDetails(dc, r, det);
            if (CopiesOf is { } co && index >= CoverCount && r.Width >= 60 && co(index) is int nc and > 0) DrawCopies(dc, r, nc, isVideo && playingHere);
        }
        DrawSets(dc, offPx, dpi);
        UpdateCursor();
        _fadingNow = fading;
        LastRenderMs = sw.Elapsed.TotalMilliseconds;
        _maxRender = Math.Max(_maxRender, LastRenderMs);
        Perf.Add(sw.ElapsedTicks);
    }

    // ---- the GPU grid (when on, it draws the tiles instead of the strips) ----

    private GpuGrid? _gpu;
    private GpuPresenter? _presenter;
    private D3DImage? _d3d;
    private readonly List<GpuTile> _gpuTiles = new();
    private int _releaseTick;
    /// <summary>GPU grid: keep decoded pictures only on the card (memory holds just what's waiting to be uploaded).</summary>
    public bool CardOnly { get; set; } = true;
    /// <summary>The GPU grid failed while drawing (device lost and the like). Raised on the UI thread; drawing is back on the strips.</summary>
    public event Action<Exception>? GpuFailed;
    /// <summary>The front buffer went away and came back (sleep, a driver update, a monitor change): the GPU objects need making again.</summary>
    public event Action? GpuFrontBufferBack;
    public bool UsingGpu => _gpu is not null;
    /// <summary>Plays GIFs from their renditions while the GPU grid draws (null: they play the old way).</summary>
    public GifPlayer? Player { get; set; }
    /// <summary>The file id of tile i (for the GIF renditions).</summary>
    public Func<int, long>? IdOf { get; set; }
    private long _lastGifFrameMs, _lastMoveMs;
    private int _lastOffPx = int.MinValue;
    private ThumbLoader.Pix? _hoverOver;
    private readonly List<(int index, long id, int w, int h, double dist)> _gpuGifs = new();
    private readonly List<(long id, int w, int h, double dist)> _nearGifs = new();
    private readonly List<int> _nearIdx = new();

    /// <summary>The first and last tile on screen (approximately: the drawn strips).</summary>
    public (int first, int last) VisibleRange() => _visible.Count == 0 ? (0, -1) : (_visible.Min(), _visible.Max());

    /// <summary>The GIF tiles on screen now (for making their GPU versions first).</summary>
    public List<int> VisibleGifs()
    {
        var l = new List<int>();
        if (Badges is { } b) foreach (int i in _visible) if (i < b.Length && b[i] == 1) l.Add(i);
        return l;
    }
    public GpuGrid? Gpu => _gpu;

    /// <summary>Draw with the GPU grid (or null for the strips again). The caller owns the objects and disposes them after switching away.</summary>
    public void UseGpu(GpuGrid? grid, GpuPresenter? presenter)
    {
        if (_d3d is not null) _d3d.IsFrontBufferAvailableChanged -= OnFrontBuffer;
        if (grid is null && _gpu is not null) _thumbs?.ForgetCardOnly();     // back to the strips: they need the pixels in memory
        _gpu = grid; _presenter = presenter;
        _d3d = grid is null ? null : new D3DImage();
        if (_d3d is not null) _d3d.IsFrontBufferAvailableChanged += OnFrontBuffer;
        // the strips are let go while the GPU draws (and made fresh when it stops)
        _bandPool.Clear(); _bands.Clear(); _sigPrev.Clear(); _bandW = -1; _lastLayout = null;
        InvalidateVisual();
    }

    private void OnFrontBuffer(object? s, DependencyPropertyChangedEventArgs e)
    {
        if (_d3d is not null && _d3d.IsFrontBufferAvailable) GpuFrontBufferBack?.Invoke();
    }

    /// <summary>Draws the tiles with the GPU grid. False if it failed (the strips draw this frame instead).</summary>
    private bool RenderGpu(DrawingContext dc, int offPx, int pw, int ph, double dpi)
    {
        if (_gpu is null || _presenter is null || _d3d is null) return false;
        _gpuTiles.Clear();
        for (int k = _tiles.Count - 1; k >= 0; k--)          // _tiles runs bottom to top; the GPU uploads top first
        {
            var t = _tiles[k];
            var g = new GpuTile { Index = t.Index, X = t.X, Y = t.Y, W = t.W, H = t.H, A = 1 };
            if (t.Over is { } o) { g.Pix = o; g.Fill = BgPixel; }
            else if (t.E is null) g.Fill = Palette()[(t.Index * 7) % Palette().Length];
            else if (t.E.Pix.Data.Length < t.E.Pix.W * t.E.Pix.H * 4 && !_gpu.IsResident(t.E.Pix)) g.Fill = FadePixel;
            else
            {
                g.Pix = t.E.Pix; g.Fill = FadePixel;
                if (t.A < 1)
                {
                    g.A = (float)t.A;
                    var prev = t.E.Prev;
                    if (prev is not null && (prev.Data.Length >= prev.W * prev.H * 4 || _gpu.IsResident(prev))) g.Prev = prev;
                }
            }
            _gpuTiles.Add(g);
        }
        if (Player is { } player && IdOf is { } idOf && Badges is { } badges)
        {
            long nowMs = player.NowMs;
            if (offPx != _lastOffPx) { _lastOffPx = offPx; _lastMoveMs = nowMs; }
            _gpuGifs.Clear();
            for (int k = 0; k < _gpuTiles.Count; k++)
            {
                var g = _gpuTiles[k];
                if (g.Index >= badges.Length || badges[g.Index] != 1 || g.Y + g.H <= offPx || g.Y >= offPx + ph) continue;
                double cy = g.Y + g.H / 2.0 - offPx - ph / 2.0;
                _gpuGifs.Add((k, idOf(g.Index), g.W, g.H, Math.Abs(cy)));
            }
            // the screen above and the screen below: their GIFs are read, put on the card and started ahead of time
            _nearGifs.Clear();
            if (_layout is not null)
            {
                _nearIdx.Clear();
                _layout.GetVisible((float)((offPx - ph) / dpi), (float)((offPx + 2.0 * ph) / dpi), _nearIdx);
                foreach (int i in _nearIdx)
                {
                    if (i >= badges.Length || badges[i] != 1) continue;
                    double y0 = _layout.Y[i] * dpi, y1 = (_layout.Y[i] + _layout.H[i]) * dpi;
                    if (y1 > offPx && y0 < offPx + ph) continue;                       // on screen: already in the list
                    double dist = y1 <= offPx ? offPx - y1 : y0 - (offPx + ph);
                    _nearGifs.Add((idOf(i), (int)(_layout.W[i] * dpi), (int)(_layout.H[i] * dpi), dist));
                }
                _nearGifs.Sort((a, b) => a.dist.CompareTo(b.dist));
            }
            bool active = Window.GetWindow(this)?.IsActive ?? true;
            player.HoverId = _hoverIndex >= 0 && _hoverIndex < badges.Length && badges[_hoverIndex] == 1 ? idOf(_hoverIndex) : -1;
            // no pause while scrolling: GIFs keep playing (they're on the card, so it costs little)
            var frames = player.Frame(_gpuGifs, _nearGifs.Select(g => (g.id, g.w, g.h)).ToList(), false, active);
            _gpu.PreloadGifs = player.Ahead;
            foreach (var (k, f) in frames)
            {
                var g = _gpuTiles[k];
                if (g.Index == _hoverIndex && g.Pix is not null && ReferenceEquals(g.Pix, _hoverOver)) continue;   // the hovered GIF plays the original
                g.Gif = f.clip; g.FrameA = f.a; g.FrameB = f.b; g.Weight = f.w;
                _gpuTiles[k] = g;
            }
            _lastGifFrameMs = nowMs;
        }
        bool locked = false;
        try
        {
            _d3d.Lock(); locked = true;
            bool complete = _gpu.Render(_gpuTiles, offPx, pw, ph, BgPixel, 2.0);
            var surf = _presenter.Surface(_gpu.SharedHandle, _gpu.Width, _gpu.Height);
            _d3d.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surf, true);
            _d3d.AddDirtyRect(new Int32Rect(0, 0, _gpu.Width, _gpu.Height));
            _d3d.Unlock(); locked = false;
            if (!complete) _needsRender = true;            // some pictures wait for the next frame's upload time
            // the decoded pictures live on the card: memory keeps only what is waiting to go up (phase 5 of the GPU plan)
            if (_thumbs is not null)
            {
                var gone = _gpu.TakeEvicted();
                if (gone.Count > 0) { _thumbs.Forget(gone); _needsRender = true; }
                if (CardOnly && ++_releaseTick % 15 == 0) Perf.FreedToCard += _thumbs.ReleaseToCard(_gpu.IsResident);
            }
            dc.DrawImage(_d3d, new Rect(0, 0, pw / dpi, ph / dpi));
            Perf.GpuDrawMax = Math.Max(Perf.GpuDrawMax, _gpu.LastDrawMs);
            Perf.GpuTimeMax = Math.Max(Perf.GpuTimeMax, _gpu.GpuMs);
            Perf.GpuWaiting = Math.Max(Perf.GpuWaiting, _gpu.LastWaiting);
            Perf.GpuUploadMax = Math.Max(Perf.GpuUploadMax, _gpu.LastUploadMs);
            Perf.GpuUploads += _gpu.LastUploads;
            return true;
        }
        catch (Exception ex)
        {
            if (locked) try { _d3d.Unlock(); } catch { }
            UseGpu(null, null);
            Dispatcher.BeginInvoke(() => GpuFailed?.Invoke(ex));
            return false;
        }
    }

    private uint[] Palette() => _paletteBgra ??= Enumerable.Range(0, 24).Select(i => { var c = HsvToColor(i * 15.0, 0.35, 0.55); return TileBlit.Pack(c.R, c.G, c.B); }).ToArray();

    private void ResetBands(int pw)
    {
        if (pw != _bandW) _bandPool.Clear();
        else foreach (var b in _bands.Values) _bandPool.Push(b.Bmp);
        _bands.Clear(); _sigPrev.Clear(); _bandW = pw;
    }

    private readonly List<Band> _work = new();
    private readonly List<(int band, int tile, long area)> _pieces = new();
    private readonly List<int> _dropKeys = new();
    private (IntPtr p, int stride)[] _ptrs = new (IntPtr, int)[16];

    /// <summary>Repaints the strips that need it: new ones completely, the others only where a picture changed. Big jobs are shared across cores.</summary>
    private unsafe void PaintBands(int pw, int k0, int k1)
    {
        Palette();
        _work.Clear(); _pieces.Clear();
        long area = 0;
        foreach (var b in _bands.Values)
        {
            b.Work.Clear();
            int top = b.K * BandH, bottom = top + BandH;
            for (int ti = 0; ti < _tiles.Count; ti++)
            {
                var t = _tiles[ti];
                if (t.Y >= bottom || t.Y + t.H <= top) continue;
                if (b.Fresh || t.Changed) b.Work.Add(ti);
            }
            if (!b.Fresh && b.Work.Count == 0) continue;
            int bi = _work.Count;
            _work.Add(b);
            foreach (int ti in b.Work)
            {
                var t = _tiles[ti];
                long a = (long)t.W * Math.Min(BandH, Math.Min(t.Y + t.H, bottom) - Math.Max(t.Y, top));
                if (t.A < 1) a *= 4;                         // a fade costs more per pixel
                else if (t.E is { } e && (e.Pix.W != t.W || e.Pix.H != t.H)) a *= 3;
                _pieces.Add((bi, ti, a)); area += a;
            }
        }
        if (_work.Count == 0) return;
        Perf.PaintedBands += _work.Count;
        if (_ptrs.Length < _work.Count) _ptrs = new (IntPtr, int)[_work.Count * 2];
        var ptrs = _ptrs;
        for (int w = 0; w < _work.Count; w++) { _work[w].Bmp.Lock(); ptrs[w] = (_work[w].Bmp.BackBuffer, _work[w].Bmp.BackBufferStride); }
        for (int w = 0; w < _work.Count; w++)
            if (_work[w].Fresh) { var (p, st) = ptrs[w]; TileBlit.Fill(new Span<byte>((void*)p, st * BandH), st, pw, BandH, 0, 0, pw, BandH, BgPixel); }
        void Piece(int k)
        {
            var (bi, ti, _) = _pieces[k];
            var (p, st) = ptrs[bi];
            var t = _tiles[ti];
            var tb = new Span<byte>((void*)p, st * BandH);
            int ty = t.Y - _work[bi].K * BandH;
            if (t.Over is { } o)
            {
                TileBlit.Fill(tb, st, pw, BandH, t.X, ty, t.W, t.H, BgPixel);            // GIF frames can have see-through parts
                if (o.W == t.W && o.H == t.H) TileBlit.Copy(tb, st, pw, BandH, t.X, ty, o.Data, o.W, o.H);
                else TileBlit.CopyScaledSmooth(tb, st, pw, BandH, t.X, ty, t.W, t.H, o.Data, o.W, o.H);
            }
            else DrawTile(tb, st, pw, BandH, t.X, ty, t.W, t.H, t.E, t.A, t.Index);
        }
        // pieces never overlap (tiles don't, and a tile is cut into one piece per strip), so they can be painted side by side
        if (area > 120_000 && _pieces.Count > 1) Parallel.For(0, _pieces.Count, Piece);
        else for (int k = 0; k < _pieces.Count; k++) Piece(k);
        for (int w = 0; w < _work.Count; w++)
        {
            var b = _work[w];
            if (b.Fresh) b.Bmp.AddDirtyRect(new Int32Rect(0, 0, pw, BandH));
            else
            {
                int top = b.K * BandH;
                foreach (int ti in b.Work)
                {
                    var t = _tiles[ti];
                    int x0 = Math.Max(0, t.X), y0 = Math.Max(0, t.Y - top), x1 = Math.Min(pw, t.X + t.W), y1 = Math.Min(BandH, t.Y - top + t.H);
                    if (x1 > x0 && y1 > y0) b.Bmp.AddDirtyRect(new Int32Rect(x0, y0, x1 - x0, y1 - y0));
                }
            }
            b.Bmp.Unlock(); b.Fresh = false;
        }
    }

    private void DrawTile(Span<byte> buf, int stride, int pw, int ph, int x, int y, int wPx, int hPx, ThumbLoader.Entry? e, double a, int i)
    {
        if (e is null) { TileBlit.Fill(buf, stride, pw, ph, x, y, wPx, hPx, _paletteBgra![(i * 7) % _paletteBgra.Length]); return; }   // plain placeholder: cheap
        var px = e.Pix;
        if (px.Data.Length < px.W * px.H * 4) { TileBlit.Fill(buf, stride, pw, ph, x, y, wPx, hPx, FadePixel); return; }     // safety: picture already handed back
        if (a < 1)
        {
            var prev = e.Prev;
            if (prev is not null && prev.Data.Length < prev.W * prev.H * 4) prev = null;
            TileBlit.Blend(buf, stride, pw, ph, x, y, wPx, hPx, px.Data, px.W, px.H,
                prev is null ? ReadOnlySpan<byte>.Empty : prev.Data, prev?.W ?? 0, prev?.H ?? 0, (int)(a * 256), FadePixel);
        }
        else if (px.W == wPx && px.H == hPx) TileBlit.Copy(buf, stride, pw, ph, x, y, px.Data, px.W, px.H);
        else TileBlit.CopyScaledSmooth(buf, stride, pw, ph, x, y, wPx, hPx, px.Data, px.W, px.H);       // only until the right size arrives
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
    private static readonly Brush CaptionShade = MakeShade();
    private static Brush MakeShade()
    {
        var b = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Color.FromArgb(215, 0, 0, 0), 90);
        b.Freeze(); return b;
    }
    private static readonly Brush CaptionDim = Freeze(new SolidColorBrush(Color.FromRgb(214, 214, 222)));
    private readonly Dictionary<int, FormattedText> _subCache = new();

    private static readonly Pen CoverEdge = MakeEdge();
    private static Pen MakeEdge() { var p = new Pen(new SolidColorBrush(Color.FromRgb(52, 52, 60)), 1); p.Freeze(); return p; }

    /// <summary>Cover grid: a thin frame round the cover, and the folder's name and details underneath it.</summary>
    private void DrawCaptionBelow(DrawingContext dc, Rect r, int i, double dpi)
    {
        dc.DrawRoundedRectangle(null, CoverEdge, new Rect(r.Left + 0.5, r.Top + 0.5, Math.Max(0, r.Width - 1), Math.Max(0, r.Height - 1)), CoverRadius, CoverRadius);
        string text = _labels![i]!;
        int nl = text.IndexOf('\n');
        string name = nl < 0 ? text : text[..nl], sub = nl < 0 ? "" : text[(nl + 1)..];
        double maxW = Math.Max(10, r.Width - 2);
        if (!_textCache.TryGetValue(i, out var ft) || Math.Abs(ft.MaxTextWidth - maxW) > 0.5)
        {
            ft = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, 13, Brushes.White, dpi)
            { MaxTextWidth = maxW, MaxLineCount = sub.Length > 0 ? 1 : 2, Trimming = TextTrimming.CharacterEllipsis };
            ft.SetFontWeight(FontWeights.SemiBold);
            _textCache[i] = ft;
            _subCache[i] = new FormattedText(sub, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, 11.5, CaptionDim, dpi)
            { MaxTextWidth = maxW, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
        }
        dc.DrawText(ft, new Point(r.Left + 1, r.Bottom + 5));
        if (sub.Length > 0) dc.DrawText(_subCache[i], new Point(r.Left + 1, r.Bottom + 5 + ft.Height + 1));
    }

    /// <summary>Folder tiles: the folder's name and details over a dark fade at the bottom of the tile.</summary>
    private void DrawCaption(DrawingContext dc, Rect r, int i, double dpi)
    {
        string text = _labels![i]!;
        int nl = text.IndexOf('\n');
        string name = nl < 0 ? text : text[..nl], sub = nl < 0 ? "" : text[(nl + 1)..];
        double maxW = Math.Max(10, r.Width - 16);
        if (!_textCache.TryGetValue(i, out var ft) || Math.Abs(ft.MaxTextWidth - maxW) > 0.5)
        {
            ft = new FormattedText("\uE8B7  " + name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, 14, Brushes.White, dpi)
            { MaxTextWidth = maxW, MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            ft.SetFontFamily(new FontFamily("Segoe MDL2 Assets"), 0, 1);
            ft.SetFontSize(12, 0, 1);
            ft.SetFontWeight(FontWeights.SemiBold, 1, ft.Text.Length - 1);
            _textCache[i] = ft;
            _subCache[i] = new FormattedText(sub, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, _face, 11.5, CaptionDim, dpi)
            { MaxTextWidth = maxW, MaxLineCount = 2, Trimming = TextTrimming.CharacterEllipsis };
        }
        var st = _subCache[i];
        double h = ft.Height + (sub.Length > 0 ? st.Height + 2 : 0) + 18;
        if (r.Height < h) return;
        var shade = new Rect(r.Left, r.Bottom - Math.Min(r.Height, h + 22), r.Width, Math.Min(r.Height, h + 22));
        dc.DrawRectangle(CaptionShade, null, shade);
        double ty = r.Bottom - h + 8;
        dc.DrawText(ft, new Point(r.Left + 8, ty));
        if (sub.Length > 0) dc.DrawText(st, new Point(r.Left + 8, ty + ft.Height + 2));
    }

    private readonly Dictionary<string, FormattedText> _badgeCache = new();

    /// <summary>
    /// The favourite star in the top-left corner: gold on favourites, and on the tile under the mouse an outline to click (a click
    /// on a gold one takes it out of Favourites).
    /// </summary>
    private void DrawStar(DrawingContext dc, Rect r, int i, bool hovered)
    {
        if (Starred is null || r.Width < 48 || r.Height < 40) return;
        bool on = Starred(i);
        if (!on && !hovered) return;
        double k = UiScale(r);
        double sz = Math.Round(26 * k);
        var box = new Rect(r.Left + 6 * k, r.Top + 6 * k, sz, sz);
        bool over = hovered && box.Contains(_mouse);
        if (hovered || over) dc.DrawEllipse(SpeakerBack, null, new Point(box.Left + sz / 2, box.Top + sz / 2), sz / 2, sz / 2);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string glyph = on || over ? "\uE735" : "\uE734";
        double fs = 15 * k;
        var shadow = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, fs, IconShadow, ppd);
        var ft = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, fs, on ? StarFill : over ? StarHover : Brushes.White, ppd);
        var at = new Point(box.Left + (sz - ft.Width) / 2, box.Top + (sz - ft.Height) / 2);
        if (!hovered) dc.DrawText(shadow, new Point(at.X + 1, at.Y + 1));
        dc.DrawText(ft, at);
        if (hovered) _starHit = (i, box);
    }
    private static readonly Pen SetPen = MakeSetPen();
    private static Pen MakeSetPen() { var p = new Pen(new SolidColorBrush(Color.FromArgb(220, 0xB8, 0x9C, 0xFF)), 1.5); p.Freeze(); return p; }
    private static readonly Brush SetBack = Freeze(new SolidColorBrush(Color.FromArgb(215, 0x24, 0x1C, 0x3A)));

    /// <summary>
    /// Connected Art sets: an outline round each set's pictures with its name and size in the bottom-left corner. A large set that
    /// isn't open is one stacked tile (edges of the cards behind it, and how many it holds); clicking its label opens it, and the
    /// label of an open one closes it.
    /// </summary>
    private void DrawSets(DrawingContext dc, int offPx, double dpi)
    {
        _setLabels.Clear();
        if (Sets is not { Count: > 0 } sets || _layout is null || _visible.Count == 0) return;
        int lo = _visible[0], hi = _visible[^1];
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (var b in sets)
        {
            if (b.First + b.Count - 1 < lo || b.First > hi || b.First >= _layout.Count) continue;
            // the set's area, row by row in equal rows (it can wrap), as one box in columns
            var parts = new List<Rect>();
            for (int k = b.First; k < Math.Min(b.First + b.Count, _layout.Count); k++)
            {
                if (_layout.W[k] <= 0) continue;
                var rk = new Rect(_layout.X[k], _layout.Y[k] - offPx / dpi, _layout.W[k], _layout.H[k]);
                // in rows and the grid a set can wrap: one outline per row (tiles in the same row overlap vertically)
                if (parts.Count > 0 && (!(_rows || _grid) || (rk.Top < parts[^1].Bottom - 0.5 && rk.Bottom > parts[^1].Top + 0.5))) parts[^1] = Rect.Union(parts[^1], rk);
                else parts.Add(rk);
            }
            if (parts.Count == 0) continue;
            foreach (var pr in parts)
            {
                var o = pr; o.Inflate(1.5, 1.5);
                dc.DrawRoundedRectangle(null, SetPen, o, 4, 4);
            }
            var first = parts[0];
            if (b.Collapsed)
            {
                // cards behind: two edges peeking out at the top right
                for (int k = 1; k <= 2; k++)
                {
                    double d = 4 * k;
                    var edge = new Rect(first.Left + d + 2, first.Top - d + 2, Math.Max(0, first.Width - d - 2), Math.Max(0, first.Height - d - 2));
                    dc.PushOpacity(0.55 / k);
                    dc.DrawRoundedRectangle(null, SetPen, edge, 4, 4);
                    dc.Pop();
                }
            }
            if (first.Width < 70 || first.Height < 36) continue;
            string text = b.Collapsed ? $"{b.Name}  ·  {b.Total} images  ▸" : b.Large ? $"{b.Name}  ·  {b.Total}  ▴" : $"{b.Name}  ·  {b.Total}";
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 11, Brushes.White, ppd)
            { MaxTextWidth = Math.Max(10, first.Width - 24), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
            var at = parts[^1];                                    // the bottom-left corner of the set (its last row in equal rows)
            var box = new Rect(at.Left + 6, at.Bottom - 22, ft.Width + 12, 17);
            dc.DrawRoundedRectangle(SetBack, SetPen, box, 4, 4);
            dc.DrawText(ft, new Point(box.Left + 6, box.Top + (box.Height - ft.Height) / 2));
            if (b.Collapsed || b.Large) _setLabels.Add((b.First, box));
        }
    }

    private void DrawHoverDetails(DrawingContext dc, Rect r, string text)
    {
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5, Brushes.White, ppd)
        { MaxTextWidth = Math.Max(10, r.Width - 56), MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        var box = new Rect(r.Right - ft.Width - 16, r.Top + 6, ft.Width + 10, 17);
        dc.DrawRoundedRectangle(BadgeBack, null, box, 3, 3);
        dc.DrawText(ft, new Point(box.Left + 5, box.Top + (17 - ft.Height) / 2));
    }

    /// <summary>"+1": the same file is in another folder too (it shows once).</summary>
    private void DrawCopies(DrawingContext dc, Rect r, int n, bool busyCorner)
    {
        if (busyCorner) return;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var ft = new FormattedText("+" + n, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5, Brushes.White, ppd);
        var box = new Rect(r.Right - ft.Width - 16, r.Bottom - 22, ft.Width + 10, 16);
        dc.DrawRoundedRectangle(BadgeBack, null, box, 3, 3);
        dc.DrawText(ft, new Point(box.Left + 5, box.Top + (16 - ft.Height) / 2));
    }

    private static readonly Brush StarHover = Freeze(new SolidColorBrush(Color.FromArgb(200, 0xFF, 0xC8, 0x3D)));

    /// <summary>"GIF", or a video's play mark and length, in the bottom-left corner of the tile.</summary>
    private void DrawBadge(DrawingContext dc, Rect r, int i)
    {
        bool video = Badges is { } bd && i < bd.Length && bd[i] == 2;
        string text = video ? (BadgeText is { } bt && i < bt.Length ? bt[i] ?? "" : "") : "GIF";
        if (text.Length == 0) return;
        // a video's length shrinks with the tile, like the time on a playing one
        double k = video ? Math.Round(UiScale(r) * 20) / 20 : 1;
        string key = k < 1 ? text + "|" + k : text;
        if (!_badgeCache.TryGetValue(key, out var ft))
        {
            ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 11 * k, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            if (_badgeCache.Count > 500) _badgeCache.Clear();
            _badgeCache[key] = ft;
        }
        double bh = Math.Round(16 * k), pad = 5 * k;
        var box = video ? new Rect(r.Left + 6 * k, r.Bottom - 12 * k - bh, ft.Width + 2 * pad, bh) : new Rect(r.Left + 6, r.Bottom - 22, ft.Width + 10, 16);
        dc.DrawRoundedRectangle(BadgeBack, null, box, 3, 3);
        dc.DrawText(ft, new Point(box.Left + pad, box.Top + (bh - ft.Height) / 2));
    }

    private static readonly Brush SpeakerBack = Freeze(new SolidColorBrush(Color.FromArgb(170, 20, 20, 24)));
    private static readonly Typeface IconFace = new("Segoe MDL2 Assets");
    private static readonly Brush PausedText = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC0, 0x40)));
    private bool _pulseOn;

    private static double UiScale(Rect r) => Math.Clamp(Math.Min(r.Width / 260.0, r.Height / 170.0), 0.62, 1.0);
    private static readonly Brush IconShadow = Freeze(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)));
    private static readonly Brush VolPanelBack = Freeze(new SolidColorBrush(Color.FromArgb(205, 16, 16, 20)));

    /// <summary>"0:06 / 1:02  Paused" in the bottom-left corner, sized to the tile.</summary>
    private void DrawTimeLabel(DrawingContext dc, Rect r, string time, bool paused, double ppd)
    {
        double k = UiScale(r);
        if (r.Width < 56 || !LabelsFit(r)) return;
        double fs = 11 * k, bh = Math.Round(16 * k);
        var ft = new FormattedText(time, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, fs, Brushes.White, ppd);
        FormattedText? pz = paused ? new FormattedText("Paused", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(_face.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), fs, PausedText, ppd) : null;
        double pad = 5 * k;
        double wBox = ft.Width + 2 * pad + (pz is null ? 0 : pz.Width + 7 * k);
        if (wBox > r.Width - 8 && pz is not null) { pz = null; wBox = ft.Width + 2 * pad; }       // no room: just the time
        var box = new Rect(r.Left + 6 * k, r.Bottom - 12 * k - bh, wBox, bh);
        dc.DrawRoundedRectangle(BadgeBack, null, box, 3, 3);
        dc.DrawText(ft, new Point(box.Left + pad, box.Top + (bh - ft.Height) / 2));
        if (pz is not null) dc.DrawText(pz, new Point(box.Left + pad + ft.Width + 7 * k, box.Top + (bh - pz.Height) / 2));
    }

    /// <summary>A video paused earlier whose player has been let go: where it was paused, and Paused.</summary>
    private void DrawPausedLabel(DrawingContext dc, Rect r, int i, double at)
    {
        string len = BadgeText is { } bt && i < bt.Length ? bt[i] ?? "" : "";
        DrawTimeLabel(dc, r, Clock(at) + (len.Length > 0 && len.Contains(':') ? " / " + len : ""), true, VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    /// <summary>
    /// A playing (or paused) tile: its progress line along the bottom, the time, and a speaker that shows while the mouse moves on
    /// the tile (or is on it). Resting on the speaker drops a volume slider under it.
    /// </summary>
    private void DrawVideoBar(DrawingContext dc, Rect r, int index, VideoTile vt)
    {
        var v = vt.E;
        double len = v.LengthSec, pos = v.SmoothPositionSec;
        bool dragging = _dragIndex == index;
        double frac = dragging ? _dragFrac : len > 0 ? Math.Clamp(pos / len, 0, 1) : 0;
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double k = UiScale(r);
        vt.Tile = r;
        if (!vt.Controls)
        {
            TimelineArt.Draw(dc, r.Left, r.Width, r.Bottom - 1.5, frac, double.NaN, false, len, ppd, 0);
            return;
        }
        // the line reacts from as far above its bottom edge as below it
        vt.Bar = new Rect(r.Left, r.Bottom - 14, r.Width, 28);
        double hover = dragging ? _dragFrac : _scrubIndex == index ? _scrubFrac : double.NaN;
        bool active = !double.IsNaN(hover);
        double shown = dragging && len > 0 ? _dragFrac * len : pos;
        DrawTimeLabel(dc, r, Clock(shown) + (len > 0 ? " / " + Clock(len) : ""), v.Paused, ppd);

        // the speaker: shows while the mouse moves on the tile, or rests on the speaker / its slider
        double ic = Math.Round(26 * k);
        var icon = new Rect(r.Right - ic - 6 * k, r.Top + 6 * k, ic, ic);
        double panelH = Math.Min(110 * k, r.Height - icon.Bottom + r.Top - 20);
        var panel = panelH >= 40 ? new Rect(icon.Left, icon.Bottom + 2, ic, panelH) : Rect.Empty;
        var zone = panel.IsEmpty ? icon : Rect.Union(icon, panel);
        zone.Inflate(6, 6);
        long now = Stopwatch.GetTimestamp();
        bool onTile = r.Contains(_mouse) || vt.Bar.Contains(_mouse);
        bool moving = onTile && (now - _mouseMovedAt) < Stopwatch.Frequency / 2;
        bool onIcon = zone.Contains(_mouse) && (icon.Contains(_mouse) || _volDragIndex == index || (!panel.IsEmpty && _openVol == index));
        bool showIcon = (moving || onIcon || _volDragIndex == index) && r.Width > 50 && r.Height > 40;
        if (onTile) _iconsUp = true;
        if (showIcon)
        {
            vt.IconShown = true;
            vt.Mute = icon;
            var ift = new FormattedText(v.Muted || v.Volume == 0 ? "\uE74F" : v.Volume < 34 ? "\uE993" : v.Volume < 67 ? "\uE994" : "\uE995", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, 16 * k, Brushes.White, ppd);
            var ishadow = new FormattedText(ift.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, IconFace, 16 * k, IconShadow, ppd);
            var at = new Point(icon.Left + (ic - ift.Width) / 2, icon.Top + (ic - ift.Height) / 2);
            dc.DrawText(ishadow, new Point(at.X + 1, at.Y + 1));
            dc.DrawText(ift, at);
            bool open = !panel.IsEmpty && (icon.Contains(_mouse) || _volDragIndex == index || (_openVol == index && zone.Contains(_mouse)));
            _openVol = open ? index : _openVol == index ? -1 : _openVol;
            if (open)
            {
                vt.Vol = panel;
                dc.DrawRoundedRectangle(VolPanelBack, null, panel, 6 * k, 6 * k);
                var tr = VolTrack(panel);
                int shownVol = v.Muted ? 0 : v.Volume;
                dc.DrawRoundedRectangle(TimelineArt.Track, null, tr, 2, 2);
                double fh = tr.Height * shownVol / 100.0;
                dc.DrawRoundedRectangle(TimelineArt.Played, null, new Rect(tr.Left, tr.Bottom - fh, tr.Width, fh), 2, 2);
                dc.DrawEllipse(TimelineArt.Knob, null, new Point(tr.Left + tr.Width / 2, tr.Bottom - fh), 5 * k, 5 * k);
                var nt = new FormattedText(shownVol.ToString(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _face, 10.5 * k, Brushes.White, ppd);
                dc.DrawText(nt, new Point(panel.Left + (panel.Width - nt.Width) / 2, panel.Bottom - 6 * k - nt.Height));
            }
        }
        if (!v.Paused || active) TimelineArt.Draw(dc, r.Left, r.Width, r.Bottom - (active ? 3.5 : 1.5), frac, hover, active, len, ppd, r.Bottom - 34);
    }
    private int _openVol = -1;

    private readonly Dictionary<string, Color> _colours = new();
    private Color ColourOf(string hex)
    {
        if (_colours.TryGetValue(hex, out var c)) return c;
        try { c = (Color)ColorConverter.ConvertFromString(hex); } catch { c = Colors.Orange; }
        _colours[hex] = c;
        return c;
    }

    /// <summary>
    /// The outline round a watched or paused video (watched comes first), drawn inside the tile's edge with an optional soft glow
    /// inward. None while it plays.
    /// </summary>
    private void DrawVideoOutline(DrawingContext dc, Rect r, VideoTile? vt, bool watched, double pausedAt)
    {
        if (vt is not null && vt.E.FileId >= 0 && vt.E.Playing) return;
        bool paused = (vt is not null && vt.Controls && vt.E.Paused) || pausedAt >= 0;
        OutlineStyle? st = null;
        if (watched && WatchedOutline is { On: true }) st = WatchedOutline;
        else if (paused && PausedOutline is { On: true }) st = PausedOutline;
        if (st is null || r.Width < 8 || r.Height < 8) return;
        var c = ColourOf(st.Colour);
        double pulse = 1;
        if (st.Pulse)
        {
            _pulseOn = true;
            pulse = 0.55 + 0.45 * (0.5 + 0.5 * Math.Sin(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency * Math.PI * 2 / 1.6));
        }
        // the glow: lines further in, fading
        double glow = Math.Max(0, st.Glow);
        if (glow > 0.5 && st.GlowStrength > 0)
        {
            int steps = Math.Max(1, (int)Math.Ceiling(glow / 1.5));
            for (int k = 0; k < steps; k++)
            {
                double t = (k + 1) / (double)steps;
                double a = st.GlowStrength * pulse * Math.Pow(1 - t + 1.0 / steps, 2);
                double inset = st.Width + k * 1.5 + 0.75;
                if (inset * 2 >= Math.Min(r.Width, r.Height)) break;
                var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(a * 255, 0, 255), c.R, c.G, c.B)), 1.5);
                dc.DrawRectangle(null, pen, new Rect(r.Left + inset, r.Top + inset, r.Width - 2 * inset, r.Height - 2 * inset));
            }
        }
        double w = Math.Clamp(st.Width, 0.5, 12);
        var line = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(st.Opacity * pulse * 255, 0, 255), c.R, c.G, c.B)), w);
        dc.DrawRectangle(null, line, new Rect(r.Left + w / 2, r.Top + w / 2, Math.Max(0, r.Width - w), Math.Max(0, r.Height - w)));
    }

    private static string Clock(double sec) => TimelineArt.Clock(sec);

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
