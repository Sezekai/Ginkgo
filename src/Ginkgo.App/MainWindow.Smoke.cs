using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ginkgo.App;

/// <summary>
/// The build's start-up check can also take pictures of the windows (when GINKGO_SHOTS names a folder): the main window, the
/// pop-outs, every Appearance page and Settings, so the look of a build can be checked without running it.
/// </summary>
public partial class MainWindow
{
    public async Task SmokeShots()
    {
        string dir = Environment.GetEnvironmentVariable("GINKGO_SHOTS") ?? "";
        if (dir.Length == 0) return;
        try
        {
            Directory.CreateDirectory(dir);
            await Task.Delay(3000);
            Shot(this, Path.Combine(dir, "01-main.png"));
            OnFolders(this, new RoutedEventArgs()); await Task.Delay(700);
            Shot(_folderCard, Path.Combine(dir, "02-folders.png"));
            _folderPopup.IsOpen = false;
            OnViewsMenu(this, new RoutedEventArgs()); await Task.Delay(500);
            if (SavedPopup.Child is FrameworkElement sv) Shot(sv, Path.Combine(dir, "03-views.png"));
            SavedPopup.IsOpen = false;
            // the bar at a laptop's width (1920 px at 125%): everything should fit without the More menu
            double w0 = Width, h0 = Height;
            Width = 1536; Height = 864; await Task.Delay(1200);
            Shot(this, Path.Combine(dir, "05-wide.png"));
            Width = w0; Height = h0; await Task.Delay(800);
            if (_order.Length > 0)
            {
                OpenViewer(0); await Task.Delay(1200);
                Shot(this, Path.Combine(dir, "06-viewer.png"));
                CloseViewer(fromUser: false); await Task.Delay(400);
            }
            ToggleSlidePanel(SlideshowBtn); await Task.Delay(500);
            if (_slidePopup?.Child is FrameworkElement sl) Shot(sl, Path.Combine(dir, "04-slideshow.png"));
            if (_slidePopup is not null) _slidePopup.IsOpen = false;
            // through the section switch, as a click would: the check fails if the page does not open
            Navigate("Appearance"); await Task.Delay(500);
            if (_pageKey != "Appearance") Fail("Appearance did not open from the section switch");
            int n = 10;
            foreach (var key in new[] { "Theme", "Text", "Buttons", "Top bar", "Logo", "Background", "Folders", "Sets", "Favourites", "Video outlines", "Scroll bars" })
            {
                _appearance?.ShowPage(key); await Task.Delay(900);
                Shot(this, Path.Combine(dir, $"{n++}-appearance-{key.Replace(' ', '-').ToLowerInvariant()}.png"));
            }
            Navigate("Settings"); await Task.Delay(1500);
            if (_pageKey != "Settings") Fail("Settings did not open from the section switch");
            Shot(this, Path.Combine(dir, "30-settings.png"));
            Navigate("Home"); await Task.Delay(300);
            if (InPage) Fail("Home did not close Settings");
            // the logo small in the title bar, as when the top bar slides away
            SetLogoSmall(true, animate: false); await Task.Delay(300);
            Shot(this, Path.Combine(dir, "07-logo-small.png"));
            SetLogoSmall(false, animate: false);
            await GoSection("Browse"); await Task.Delay(1500);
            Shot(this, Path.Combine(dir, "05-browse.png"));
            Keyboard.Focus(Sections); await Task.Delay(400);
            Shot(this, Path.Combine(dir, "06-switch-focused.png"));
        }
        catch (Exception ex) { App.LogError("taking the build's pictures", ex); }
    }

    /// <summary>A check that must not fail: thrown outside the pictures' own error catch, so the build's start-up check fails.</summary>
    private void Fail(string what) => Dispatcher.BeginInvoke(new Action(() => throw new InvalidOperationException("Start-up check: " + what)));

    private static void Shot(FrameworkElement el, string path)
    {
        el.UpdateLayout();
        double w = Math.Max(1, el.ActualWidth), h = Math.Max(1, el.ActualHeight);
        if (el is Window win && win.Content is FrameworkElement c) { w = c.ActualWidth; h = c.ActualHeight; }
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            Brush back = el is Window ww ? ww.Background ?? Brushes.Black : (Application.Current.TryFindResource("WindowBg") as Brush ?? Brushes.Black);
            dc.DrawRectangle(back, null, new Rect(0, 0, w, h));
            var target = el is Window wn && wn.Content is Visual v ? v : el;
            dc.DrawRectangle(new VisualBrush(target) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
