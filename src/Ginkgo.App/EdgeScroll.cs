using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// Scroll bars reach the right-hand edge of the window, page or pop-out they are in: a scroll viewer that sits inside some padding
/// is widened to the edge (its content keeps its place, so only the bar moves). Applied to every scroll viewer as it loads.
/// </summary>
public static class EdgeScroll
{
    private static readonly DependencyProperty DoneProperty = DependencyProperty.RegisterAttached("EdgeDone", typeof(bool), typeof(EdgeScroll));

    public static void Fit(ScrollViewer sv)
    {
        if (sv.GetValue(DoneProperty) is true) return;
        if (sv.TemplatedParent is TextBoxBase or ComboBox) return;            // text boxes and drop-down lists keep their own
        if (sv.VerticalScrollBarVisibility is ScrollBarVisibility.Disabled or ScrollBarVisibility.Hidden) return;
        sv.SetValue(DoneProperty, true);
        sv.Dispatcher.BeginInvoke(() => Widen(sv), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private static void Widen(ScrollViewer sv)
    {
        try
        {
            // the nearest thing with a visible edge: a bordered card, a popup's root, a page, or the window
            FrameworkElement? edge = null;
            double inset = 0;
            for (var d = VisualTreeHelper.GetParent(sv); d is not null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is Border b && b.BorderThickness.Right > 0) { edge = b; inset = b.BorderThickness.Right; break; }
                if (d is AppearancePage or SettingsPage or Window) { edge = (FrameworkElement)d; break; }
                if (VisualTreeHelper.GetParent(d) is null && d is FrameworkElement root) { edge = root; break; }       // a popup's root
            }
            if (edge is null || !edge.IsAncestorOf(sv)) return;
            var right = sv.TransformToAncestor(edge).Transform(new Point(sv.ActualWidth, 0)).X;
            double gap = edge.ActualWidth - inset - right;
            if (gap <= 0.5 || gap > 48) return;
            var m = sv.Margin;
            sv.Margin = new Thickness(m.Left, m.Top, m.Right - gap, m.Bottom);
            var p = sv.Padding;
            sv.Padding = new Thickness(p.Left, p.Top, p.Right + gap, p.Bottom);
        }
        catch { }
    }
}
