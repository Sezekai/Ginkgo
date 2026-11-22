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
        FileOpsCheck();
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
            // (the build machine's screen is small, so the top bar alone is laid out at those widths and pictured)
            foreach (int bw in new[] { 1536, 1920 })
            {
                double bh = TopBar.ActualHeight;
                TopBar.Measure(new Size(bw, bh)); TopBar.Arrange(new Rect(0, 0, bw, bh));
                var rt = new RenderTargetBitmap(bw, (int)Math.Ceiling(bh), 96, 96, PixelFormats.Pbgra32);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen()) { dc.DrawRectangle(new VisualBrush(TopBar) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, bw, bh)); }
                rt.Render(dv);
                var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(rt));
                using (var f = File.Create(Path.Combine(dir, $"05-bar-{bw}.png"))) enc.Save(f);
                TopBar.InvalidateMeasure(); InvalidateMeasure(); UpdateLayout();
                await Task.Delay(300);
            }
            if (_order.Length > 0)
            {
                OpenViewer(0); await Task.Delay(1200);
                Shot(this, Path.Combine(dir, "06-viewer.png"));
                CloseViewer(fromUser: false); await Task.Delay(400);
            }
            // the file operation windows: the folder picker and a name clash
            {
                var picker = new FolderPickerWindow(_settings.LibraryRoot, "Move", 3) { Owner = this };
                picker.Show(); await Task.Delay(1500);
                Shot(picker, Path.Combine(dir, "08-folder-picker.png")); picker.Close();
                string tmp = Path.Combine(Path.GetTempPath(), "ginkgo-clash-" + Environment.ProcessId);
                Directory.CreateDirectory(Path.Combine(tmp, "B"));
                foreach (var (p0, c) in new[] { (Path.Combine(tmp, "x.png"), Colors.SteelBlue), (Path.Combine(tmp, "B", "x.png"), Colors.IndianRed) })
                {
                    var px = new byte[120 * 80 * 4];
                    for (int i = 0; i < px.Length; i += 4) { px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255; }
                    var enc = new PngBitmapEncoder(); enc.Frames.Add(BitmapFrame.Create(BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, px, 480)));
                    using var f = File.Create(p0); enc.Save(f);
                }
                var clash = new ConflictWindow(Path.Combine(tmp, "x.png"), Path.Combine(tmp, "B", "x.png"), "x (2).png", copy: false, left: 4) { Owner = this };
                clash.Show(); await Task.Delay(800);
                Shot(clash, Path.Combine(dir, "09-name-clash.png")); clash.Close();
                try { Directory.Delete(tmp, true); } catch { }
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

    /// <summary>
    /// The file operations on throwaway files: a JPEG and a PNG are turned (their sides swap), a file is moved and one copied with a
    /// name already taken (a numbered name), and one goes to the Recycle Bin. A wrong result fails the start-up check.
    /// </summary>
    private void FileOpsCheck()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ginkgo-fileops-" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(dir);
            BitmapSource Pic(int w, int h)
            {
                var px = new byte[w * h * 4];
                for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(i % 251); px[i + 1] = 90; px[i + 2] = 200; px[i + 3] = 255; }
                return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            }
            void Save(BitmapEncoder enc, BitmapSource b, string path) { enc.Frames.Add(BitmapFrame.Create(b)); using var f = File.Create(path); enc.Save(f); }
            (int w, int h) Size(string path) { var d = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad); return (d.Frames[0].PixelWidth, d.Frames[0].PixelHeight); }
            string jpg = Path.Combine(dir, "a.jpg"), png = Path.Combine(dir, "b.png");
            Save(new JpegBitmapEncoder { QualityLevel = 90 }, Pic(64, 32), jpg);
            Save(new PngBitmapEncoder(), Pic(40, 24), png);
            FileOps.Rotate(jpg, clockwise: true);
            FileOps.Rotate(png, clockwise: false);
            if (Size(jpg) != (32, 64)) Fail($"turning a JPEG gave {Size(jpg)}");
            if (Size(png) != (24, 40)) Fail($"turning a PNG gave {Size(png)}");
            string sub = Path.Combine(dir, "Sub");
            FileOps.Transfer(png, Path.Combine(sub, "b.png"), copy: false);
            if (File.Exists(png) || !File.Exists(Path.Combine(sub, "b.png"))) Fail("moving a file");
            string free = Ginkgo.Core.FileDestinations.FreeName("b.png", n => File.Exists(Path.Combine(sub, n)));
            FileOps.Transfer(Path.Combine(sub, "b.png"), Path.Combine(sub, free), copy: true);
            if (free != "b (2).png" || !File.Exists(Path.Combine(sub, "b (2).png"))) Fail("copying with a numbered name");
            if (FileOps.NoRecycleBin(jpg) is { } why) Fail("the temp drive has no Recycle Bin: " + why);
            var (done, bad) = FileOps.Recycle(new[] { jpg });
            if (bad.Count > 0 || File.Exists(jpg)) Fail("sending a file to the Recycle Bin: " + (bad.Count > 0 ? bad[0].why : "it is still there"));
        }
        catch (Exception ex) { Fail("file operations: " + ex.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
