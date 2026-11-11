using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Ginkgo.App;

/// <summary>
/// Puts the Appearance looks into effect: the logo, the folder pop-outs and their switches, how sets and favourites are marked,
/// and the backgrounds (bars, behind the pictures, the viewer).
/// </summary>
public partial class MainWindow
{
    private readonly DispatcherTimer _sceneDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _sceneHooked;

    /// <summary>Every look in effect (at start and whenever Appearance changes one).</summary>
    private void ApplyLooks()
    {
        Logo.Apply(_settings.Logo);
        Logo.SetButtonColours(_settings.Look);
        FolderPanel.SetLook(_settings.Folders.Toggles);
        PopCard.SetLook(_settings.Folders.Panel);
        _folders?.Restyle(); _nsfwPanel?.Restyle(); _veryPanel?.Restyle();
        MasonryView.SetStyle = _settings.SetStyle;
        MasonryView.FavStyle = _settings.FavStyle;
        ApplyVideoLook();
        ApplyScenes();
        View.RefreshLayout();
        View.ThemeChanged();
    }

    /// <summary>Called by the Appearance window after any change other than the theme.</summary>
    public void LooksApplied()
    {
        ApplyLooks();
        SaveSettings();
    }

    /// <summary>The backgrounds: the bars (with the bar colour over them as much as asked), behind the pictures, and the viewer.</summary>
    private void ApplyScenes()
    {
        if (!_sceneHooked)
        {
            _sceneHooked = true;
            _sceneDelay.Tick += (_, _) => { _sceneDelay.Stop(); ApplyScenes(); };
            SizeChanged += (_, _) => { if (UsesScenes) { _sceneDelay.Stop(); _sceneDelay.Start(); } };
        }
        var look = _settings.Scenes;
        var dpi = VisualTreeHelper.GetDpi(this);
        int w = (int)Math.Max(800, ActualWidth * dpi.DpiScaleX), h = (int)Math.Max(500, ActualHeight * dpi.DpiScaleY);
        var R = Application.Current.Resources;
        var theme = _settings.Theme;

        // the bars
        var bars = SceneKit.Render(look.BarsScene, w, h);
        if (bars is null) R["BarBack"] = Frozen(new SolidColorBrush(ButtonLook.C(theme.Surface)));
        else
        {
            var g = new DrawingGroup();
            var img = new ImageBrush(bars) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top, AlignmentX = AlignmentX.Left };
            g.Children.Add(new GeometryDrawing(img, null, new RectangleGeometry(new Rect(0, 0, w, h))));
            var bar = ButtonLook.C(theme.Surface); bar.A = (byte)(255 * (1 - Math.Clamp(look.SeeThrough, 0, 0.95)));
            g.Children.Add(new GeometryDrawing(new SolidColorBrush(bar), null, new RectangleGeometry(new Rect(0, 0, w, h))));
            R["BarBack"] = Frozen(new DrawingBrush(g) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top, AlignmentX = AlignmentX.Left });
        }

        // behind the pictures: the grid leaves its background clear and the window's shows through
        var win = SceneKit.Render(look.WindowScene, w, h);
        if (win is null)
        {
            Root.Background = null;
            MasonryView.SetBackground(ButtonLook.C(theme.Window), false);
        }
        else
        {
            Root.Background = Frozen(new ImageBrush(win) { Stretch = Stretch.UniformToFill });
            MasonryView.SetBackground(ButtonLook.C(theme.Window), true);
        }

        // the viewer
        var viewer = SceneKit.Render(look.ViewerScene, w, h);
        R["ViewerBack"] = viewer is null ? ThemeKit.Backdrop(theme) : Frozen(new ImageBrush(viewer) { Stretch = Stretch.UniformToFill });
        View.ThemeChanged();
    }

    private bool UsesScenes => _settings.Scenes.BarsScene.Kind != "Theme" || _settings.Scenes.WindowScene.Kind != "Theme" || _settings.Scenes.ViewerScene.Kind != "Theme";

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
