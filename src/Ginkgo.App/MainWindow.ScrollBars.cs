using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Ginkgo.App;

/// <summary>
/// The pictures' scroll bar: out of sight until the mouse comes near the window's right edge (Appearance &gt; Scroll bars sets how
/// near), then it fades in. Its handle grows with the picture size, and can glow, pulse or have a shine run along it.
/// </summary>
public partial class MainWindow
{
    private bool _scrollShown, _scrollHooked;

    private void HookScrollBar()
    {
        if (_scrollHooked) return;
        _scrollHooked = true;
        PreviewMouseMove += (_, e) => ScrollNear(e.GetPosition(Root));
        MouseLeave += (_, _) => { if (!Scroll.IsMouseCaptureWithin) ShowScroll(false); };
        Scroll.LostMouseCapture += (_, _) => ScrollNear(Mouse.GetPosition(Root));
        Scroll.Loaded += (_, _) => ApplyScrollLook();
    }

    private void ScrollNear(Point p)
    {
        if (Scroll.IsMouseCaptureWithin) { ShowScroll(true); return; }
        double reach = Math.Clamp(_settings.ScrollBars.Reach, 0.01, 0.3) * Math.Max(1, Root.ActualWidth);
        bool near = !ViewerOpen && !InPage && Scroll.Maximum > 0 && p.X >= Root.ActualWidth - Math.Max(reach, Scroll.ActualWidth + 4)
                    && p.Y >= 0 && p.Y <= Root.ActualHeight - StatusBar.ActualHeight;
        ShowScroll(near);
    }

    private void ShowScroll(bool show)
    {
        if (show == _scrollShown) return;
        _scrollShown = show;
        Scroll.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, TimeSpan.FromMilliseconds(show ? 130 : 260)));
        if (show && _syncPending) SyncBar();
    }

    /// <summary>The handle's length: in step with the picture size (bigger pictures, a longer handle), and never shorter than the
    /// list's own share of the screen.</summary>
    private double ScrollViewport(double max, double viewport)
    {
        double track = Math.Max(40, Scroll.ActualHeight > 0 ? Scroll.ActualHeight : View.ActualHeight);
        double hi = track * 0.6;
        double want = Math.Clamp((50 + View.TileTarget * 0.55) * Math.Clamp(_settings.ScrollBars.Length, 0.25, 4), Math.Min(36, hi), hi);
        if (max <= 0) return viewport;
        double natural = track * viewport / (max + viewport);
        if (natural >= want) return viewport;
        return want * max / Math.Max(1, track - want);       // a viewport that gives the handle that length
    }

    /// <summary>Width, outline, fill, colour and effect of the pictures' scroll bar.</summary>
    private void ApplyScrollLook()
    {
        HookScrollBar();
        var l = _settings.ScrollBars;
        Scroll.Width = Math.Clamp(l.Width, 6, 32);
        if (Scroll.Template?.FindName("PART_Track", Scroll) is not Track track || track.Thumb is not Thumb thumb) return;
        thumb.ApplyTemplate();
        if (thumb.Template.FindName("T", thumb) is not Border t || thumb.Template.FindName("Shine", thumb) is not Border shine) return;
        Color c = l.Colour.Length > 0 ? ButtonLook.C(l.Colour) : ThemeKit.AccentColor;
        var edge = new SolidColorBrush(c); edge.Freeze();
        t.BorderBrush = edge;
        t.BorderThickness = new Thickness(Math.Clamp(l.OutlineWidth, 0.5, 4));
        t.Background = l.Fill > 0.005 ? Frozen(new SolidColorBrush(Color.FromArgb((byte)(255 * Math.Clamp(l.Fill, 0, 0.8)), c.R, c.G, c.B))) : Brushes.Transparent;
        t.CornerRadius = new CornerRadius(Math.Max(3, (Scroll.Width - 4) / 2));
        shine.CornerRadius = t.CornerRadius;

        // a glow, steady or breathing
        if (l.Effect is "Glow" or "Pulse" or "PulseShine")
        {
            var glow = new DropShadowEffect { Color = c, ShadowDepth = 0, BlurRadius = 14, Opacity = 0.9 };
            if (l.Effect != "Glow")
                glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromSeconds(1.1)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() });
            t.Effect = glow;
        }
        else t.Effect = null;

        // a band of light running down the handle again and again
        if (l.Effect is "Shine" or "PulseShine")
        {
            var move = new TranslateTransform();
            var band = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1), RelativeTransform = move };
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.5));
            band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65));
            shine.Background = band;
            shine.Visibility = Visibility.Visible;
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimationUsingKeyFrames
            {
                RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames = { new LinearDoubleKeyFrame(-0.7, KeyTime.FromTimeSpan(TimeSpan.Zero)), new EasingDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)), new SineEase()),
                              new DiscreteDoubleKeyFrame(0.7, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(3.2))) },
            });
        }
        else { shine.Background = null; shine.Visibility = Visibility.Collapsed; }
        SyncBar();
    }
}
