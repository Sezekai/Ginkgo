using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ginkgo.App;

/// <summary>
/// Adjusting how a folder's cover sits in its tile: drag to move, mouse wheel or the slider to zoom, "Show whole picture" to fit
/// it all in (with bars), "Fill" to crop to the tile again. What you see here is what the tile shows.
/// </summary>
public sealed class CoverEditWindow : Window
{
    private readonly BitmapSource _pic;
    private readonly double _aspect;
    private readonly Canvas _box;
    private readonly Image _img;
    private readonly Slider _zoomSlider;
    private float _zoom, _cx, _cy;
    private bool _dragging, _syncing;
    private Point _dragFrom; private float _dragCx, _dragCy;
    public (float zoom, float cx, float cy) Result { get; private set; }

    private const double BoxH = 420;

    public CoverEditWindow(string folderName, BitmapSource picture, double tileAspect, (float zoom, float cx, float cy) start)
    {
        _pic = picture; _aspect = tileAspect;
        (_zoom, _cx, _cy) = start;
        Result = start;
        Title = "Adjust cover: " + folderName;
        SizeToContent = SizeToContent.WidthAndHeight; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        SourceInitialized += (_, _) => Theme.DarkTitleBar(this);

        double boxW = Math.Round(BoxH * tileAspect);
        _img = new Image { Source = picture, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
        _box = new Canvas { Width = boxW, Height = BoxH, ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)), Cursor = Cursors.SizeAll };
        _box.Children.Add(_img);
        _box.MouseLeftButtonDown += (_, e) => { _dragging = true; _dragFrom = e.GetPosition(_box); _dragCx = _cx; _dragCy = _cy; _box.CaptureMouse(); };
        _box.MouseMove += (_, e) =>
        {
            if (!_dragging) return;
            var p = e.GetPosition(_box);
            double s = Scale();
            _cx = (float)(_dragCx - (p.X - _dragFrom.X) / (s * _pic.PixelWidth));
            _cy = (float)(_dragCy - (p.Y - _dragFrom.Y) / (s * _pic.PixelHeight));
            Place();
        };
        _box.MouseLeftButtonUp += (_, _) => { _dragging = false; _box.ReleaseMouseCapture(); Place(settle: true); };
        _box.MouseWheel += (_, e) => { SetZoom(_zoom * (e.Delta > 0 ? 1.1f : 1 / 1.1f)); e.Handled = true; };

        _zoomSlider = new Slider { Minimum = Math.Log(0.2), Maximum = Math.Log(5), Width = 220, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _zoomSlider.ValueChanged += (_, e) => { if (!_syncing) SetZoom((float)Math.Exp(e.NewValue)); };

        Button B(string text, RoutedEventHandler click) { var b = new Button { Content = text, Margin = new Thickness(0, 0, 8, 0), Height = 30 }; b.Click += click; return b; }
        var tools = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), MaxWidth = Math.Max(boxW, 420) };
        tools.Children.Add(B("Fill the tile", (_, _) => { _cx = CoverStore.DefaultCx; _cy = CoverStore.DefaultCy; SetZoom(1); }));
        tools.Children.Add(B("Show whole picture", (_, _) => { _cx = 0.5f; _cy = 0.5f; SetZoom(FitZoom()); }));
        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        zoomRow.Children.Add(new TextBlock { Text = "Zoom", Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center });
        zoomRow.Children.Add(_zoomSlider);
        var hint = new TextBlock { Text = "Drag the picture to move it. Mouse wheel zooms.", Foreground = (Brush)FindResource("Dim"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = Math.Max(boxW, 420) };
        var ok = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var save = B("Save", (_, _) => { Result = (_zoom, _cx, _cy); DialogResult = true; });
        save.Background = new SolidColorBrush(Color.FromRgb(46, 125, 70));
        ok.Children.Add(save);
        ok.Children.Add(B("Cancel", (_, _) => DialogResult = false));

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new Border { Child = _box, BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 80)), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center });
        root.Children.Add(tools); root.Children.Add(zoomRow); root.Children.Add(hint); root.Children.Add(ok);
        Content = root;
        KeyDown += (_, e) => { if (e.Key == Key.Escape) DialogResult = false; else if (e.Key == Key.Enter) { Result = (_zoom, _cx, _cy); DialogResult = true; } };
        SetZoom(_zoom);
    }

    private double Scale() => _zoom * Math.Max(_box.Width / _pic.PixelWidth, _box.Height / _pic.PixelHeight);
    private float FitZoom() => (float)(Math.Min(_box.Width / _pic.PixelWidth, _box.Height / _pic.PixelHeight) / Math.Max(_box.Width / _pic.PixelWidth, _box.Height / _pic.PixelHeight));

    private void SetZoom(float z)
    {
        _zoom = Math.Clamp(z, 0.2f, 5f);
        _syncing = true; _zoomSlider.Value = Math.Log(_zoom); _syncing = false;
        Place(settle: true);
    }

    /// <summary>Same arithmetic as the tile: the picture is kept against the edges when it is bigger than the tile, centred when smaller.</summary>
    private void Place(bool settle = false)
    {
        double sw = _pic.PixelWidth, sh = _pic.PixelHeight, s = Scale();
        double vw = _box.Width / s, vh = _box.Height / s;
        double left = _cx * sw - vw / 2, top = _cy * sh - vh / 2;
        left = vw <= sw ? Math.Clamp(left, 0, sw - vw) : (sw - vw) / 2;
        top = vh <= sh ? Math.Clamp(top, 0, sh - vh) : (sh - vh) / 2;
        if (settle || !_dragging) { _cx = (float)((left + vw / 2) / sw); _cy = (float)((top + vh / 2) / sh); }
        _img.Width = sw * s; _img.Height = sh * s;
        Canvas.SetLeft(_img, -left * s); Canvas.SetTop(_img, -top * s);
    }
}
