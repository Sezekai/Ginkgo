using System.Windows;
using System.Windows.Controls;

namespace Ginkgo.App;

/// <summary>
/// The menu bar's row of controls. When the window gets narrower, the controls that can (search box, sort list, size slider) get
/// narrower first, down to their MinWidth; after that the last controls move into the "more" drop-down. Controls marked Pinned
/// (the more button and the hide button) always stay at the end of the row.
/// </summary>
public sealed class BarPanel : Panel
{
    /// <summary>The width a shrinkable control would like (it may be given less, down to its MinWidth).</summary>
    public static readonly DependencyProperty PreferredWidthProperty = DependencyProperty.RegisterAttached(
        "PreferredWidth", typeof(double), typeof(BarPanel), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static double GetPreferredWidth(DependencyObject d) => (double)d.GetValue(PreferredWidthProperty);
    public static void SetPreferredWidth(DependencyObject d, double v) => d.SetValue(PreferredWidthProperty, v);

    public static readonly DependencyProperty PinnedProperty = DependencyProperty.RegisterAttached(
        "Pinned", typeof(bool), typeof(BarPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));
    public static bool GetPinned(DependencyObject d) => (bool)d.GetValue(PinnedProperty);
    public static void SetPinned(DependencyObject d, bool v) => d.SetValue(PinnedProperty, v);

    /// <summary>The button that opens the drop-down with the controls that did not fit (one of the pinned children).</summary>
    public FrameworkElement? MoreButton { get; set; }
    private bool _expanded;
    /// <summary>The drop-down is open: the controls that did not fit are laid out in rows just under the bar (inside the window).</summary>
    public bool Expanded { get => _expanded; set { if (_expanded == value) return; _expanded = value; InvalidateMeasure(); InvalidateVisual(); } }
    private readonly Dictionary<UIElement, Rect> _extra = new();
    private Rect _extraBounds = Rect.Empty;
    private static readonly System.Windows.Media.Brush DropBack = Frozen(System.Windows.Media.Color.FromRgb(28, 28, 32));
    private static readonly System.Windows.Media.Pen DropEdge = new(Frozen(System.Windows.Media.Color.FromRgb(58, 58, 66)), 1);
    private static System.Windows.Media.Brush Frozen(System.Windows.Media.Color c) { var b = new System.Windows.Media.SolidColorBrush(c); b.Freeze(); return b; }

    private bool _pinnedOnly;
    /// <summary>Only the pinned controls show (the logo, the section switch, its name, more and hide): in Appearance and Settings.</summary>
    public bool PinnedOnly { get => _pinnedOnly; set { if (_pinnedOnly == value) return; _pinnedOnly = value; InvalidateMeasure(); } }

    /// <summary>Controls that did not fit at the last layout, in their order in the row.</summary>
    public List<UIElement> Overflow { get; } = new();

    private readonly Dictionary<UIElement, double> _width = new();

    private static double Margins(UIElement c) => c is FrameworkElement f ? f.Margin.Left + f.Margin.Right : 0;

    protected override Size MeasureOverride(Size available)
    {
        double maxW = double.IsInfinity(available.Width) ? double.MaxValue : available.Width;
        double h = 0;
        var kids = InternalChildren.Cast<UIElement>().Where(c => c.Visibility != Visibility.Collapsed && (!_pinnedOnly || GetPinned(c))).ToList();
        var nominal = new Dictionary<UIElement, double>();
        var minimum = new Dictionary<UIElement, double>();
        foreach (var c in kids)
        {
            double pref = GetPreferredWidth(c);
            if (!double.IsNaN(pref))
            {
                c.Measure(new Size(pref, available.Height));
                nominal[c] = pref + Margins(c);
                double mn = c is FrameworkElement f && f.MinWidth > 0 ? f.MinWidth : pref;
                minimum[c] = Math.Min(pref, mn) + Margins(c);
            }
            else
            {
                c.Measure(new Size(double.PositiveInfinity, available.Height));
                nominal[c] = minimum[c] = c.DesiredSize.Width;
            }
            h = Math.Max(h, c.DesiredSize.Height);
        }

        var pinned = kids.Where(c => GetPinned(c) && c != MoreButton).ToList();
        var normal = kids.Where(c => !GetPinned(c)).ToList();
        double moreW = MoreButton is not null && MoreButton.Visibility != Visibility.Collapsed ? nominal.GetValueOrDefault(MoreButton) : 0;
        double pinnedW = pinned.Sum(c => nominal[c]);

        Overflow.Clear(); _width.Clear();
        double full = normal.Sum(c => nominal[c]) + pinnedW;
        if (full <= maxW)
        {
            foreach (var c in kids) _width[c] = nominal[c];
            if (MoreButton is not null) _width[MoreButton] = 0;
        }
        else
        {
            double total = normal.Sum(c => nominal[c]);
            double canShrink = normal.Sum(c => nominal[c] - minimum[c]);
            var keep = new List<UIElement>(normal);
            double room = maxW - pinnedW;
            bool needMore = total - canShrink > room;      // narrowing alone is not enough
            if (needMore)
            {
                room -= moreW;
                double least = keep.Sum(c => minimum[c]);
                while (keep.Count > 0 && least > room) { var last = keep[^1]; keep.RemoveAt(keep.Count - 1); Overflow.Insert(0, last); least -= minimum[last]; }
                total = keep.Sum(c => nominal[c]); canShrink = keep.Sum(c => nominal[c] - minimum[c]);
            }
            double cut = Math.Max(0, total - room);
            double share = canShrink > 0 ? Math.Min(1, cut / canShrink) : 0;
            foreach (var c in keep) _width[c] = nominal[c] - (nominal[c] - minimum[c]) * share;
            foreach (var c in pinned) _width[c] = nominal[c];
            if (MoreButton is not null) _width[MoreButton] = needMore ? moreW : 0;
            foreach (var c in Overflow) _width[c] = 0;
        }
        // the drop-down: the controls that did not fit, in rows right-aligned under the bar, never wider than the bar
        _extra.Clear(); _extraBounds = Rect.Empty;
        if (_expanded && Overflow.Count > 0)
        {
            double pad = 8, rowW = Math.Max(60, maxW - 2 * pad), x = 0, y = 0, rowH = 0, widest = 0;
            var rows = new List<List<(UIElement c, double w)>>(); var row = new List<(UIElement, double)>();
            foreach (var c in Overflow)
            {
                double w = Math.Min(nominal[c], rowW);
                if (x > 0 && x + w > rowW) { rows.Add(row); row = new(); widest = Math.Max(widest, x); x = 0; }
                row.Add((c, w)); x += w;
            }
            rows.Add(row); widest = Math.Max(widest, x);
            double left = Math.Max(pad, maxW - pad - widest);
            foreach (var r in rows)
            {
                rowH = r.Max(t => t.c.DesiredSize.Height);
                double rx = left;
                foreach (var (c, w) in r) { _extra[c] = new Rect(rx, h + 8 + pad + y, w, rowH); rx += w; }
                y += rowH;
            }
            _extraBounds = new Rect(left - pad, h + 8, widest + 2 * pad, y + 2 * pad);
        }
        double used = _width.Values.Sum();
        return new Size(Math.Min(used, maxW), h);         // the drop-down hangs below without making the bar taller
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            if (_pinnedOnly && !GetPinned(c)) { c.Arrange(new Rect(-10000, 0, 0, 0)); continue; }
            double w = _width.GetValueOrDefault(c);
            if (_extra.TryGetValue(c, out var er)) { c.Arrange(er); continue; }      // in the open drop-down
            if (w <= 0) { c.Arrange(new Rect(-10000, 0, 0, 0)); continue; }     // in the closed drop-down (or the more button, unused)
            c.Arrange(new Rect(x, 0, w, final.Height));              // the width includes the control's margins
            x += w;
        }
        return final;
    }

    protected override void OnRender(System.Windows.Media.DrawingContext dc)
    {
        base.OnRender(dc);
        if (!_extraBounds.IsEmpty) dc.DrawRoundedRectangle(DropBack, DropEdge, _extraBounds, 6, 6);
    }

    /// <summary>The drop-down area, for hit testing (it lies outside the bar's own bounds).</summary>
    public bool DropDownContains(Point p) => !_extraBounds.IsEmpty && _extraBounds.Contains(p);
    public bool HasOverflow => Overflow.Count > 0;

    protected override System.Windows.Media.HitTestResult? HitTestCore(System.Windows.Media.PointHitTestParameters p) =>
        DropDownContains(p.HitPoint) ? new System.Windows.Media.PointHitTestResult(this, p.HitPoint) : base.HitTestCore(p);
}
