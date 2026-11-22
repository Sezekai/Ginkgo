using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Ginkgo.App;

/// <summary>What to do when a file with the same name is already where a file is going.</summary>
public enum Clash { Skip, KeepBoth, KeepIncoming, KeepExisting }

/// <summary>
/// One name clash at a time: both files side by side (picture, size, dimensions, date), and what to do — leave it, keep both
/// (the one coming in gets a numbered name), or keep just one (the other goes to the Recycle Bin). "Do the same for the rest"
/// answers every clash still to come in this move or copy.
/// </summary>
public sealed class ConflictWindow : Window
{
    public Clash Choice { get; private set; } = Clash.Skip;
    public bool ForAll { get; private set; }

    public ConflictWindow(string incoming, string existing, string freeName, bool copy, int left)
    {
        Title = "A file with that name is already there";
        Width = 760; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(ForegroundProperty, "Text");
        Theme.DarkTitleBar(this);

        var st = new StackPanel { Margin = new Thickness(16) };
        st.Children.Add(new TextBlock
        {
            Text = $"“{Path.GetFileName(existing)}” is already in {Path.GetDirectoryName(existing)}.", TextWrapping = TextWrapping.Wrap,
            FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
        });
        var pair = new Grid();
        pair.ColumnDefinitions.Add(new ColumnDefinition()); pair.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) }); pair.ColumnDefinitions.Add(new ColumnDefinition());
        var a = Side(copy ? "Being copied" : "Being moved", incoming); Grid.SetColumn(a, 0);
        var b = Side("Already there", existing); Grid.SetColumn(b, 2);
        pair.Children.Add(a); pair.Children.Add(b);
        st.Children.Add(pair);

        var choices = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        Button Choose(string text, string tip, Clash c)
        {
            var btn = new Button { Content = text, ToolTip = tip, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 8, 8) };
            btn.Click += (_, _) => { Choice = c; DialogResult = true; };
            choices.Children.Add(btn);
            return btn;
        }
        Choose(copy ? "Don't copy it" : "Don't move it", "Leave this file where it is", Clash.Skip).IsCancel = true;
        Choose("Keep both", $"The one coming in is called “{freeName}”", Clash.KeepBoth).IsDefault = true;
        Choose("Keep the one coming in", "The one already there goes to the Recycle Bin", Clash.KeepIncoming);
        Choose("Keep the one already there", copy ? "Nothing is copied" : "The one being moved goes to the Recycle Bin", Clash.KeepExisting);
        st.Children.Add(choices);
        if (left > 0)
        {
            var all = new CheckBox { Content = $"Do the same for the other {left} clash{(left == 1 ? "" : "es")}", Margin = new Thickness(0, 4, 0, 0) };
            all.SetResourceReference(ForegroundProperty, "Text");
            all.Click += (_, _) => ForAll = all.IsChecked == true;
            st.Children.Add(all);
        }
        Content = st;
    }

    private static FrameworkElement Side(string head, string path)
    {
        var card = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10) };
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        card.SetResourceReference(Border.BackgroundProperty, "Surface2");
        var st = new StackPanel();
        st.Children.Add(new TextBlock { Text = head, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var img = new Image { Height = 240, Stretch = Stretch.Uniform, Source = ShellThumb.Get(path, 320) };
        st.Children.Add(new Border { Height = 240, Background = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), Child = img });
        string info = "";
        try
        {
            var fi = new FileInfo(path);
            info = $"{Path.GetFileName(path)}\n{Size(fi.Length)}   ·   {Dims(path)}\nchanged {fi.LastWriteTime:d MMM yyyy, HH:mm}";
        }
        catch { info = Path.GetFileName(path); }
        st.Children.Add(new TextBlock { Text = info, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.85 });
        card.Child = st;
        return card;
    }

    private static string Size(long b) => b >= 1 << 20 ? $"{b / 1048576.0:0.0} MB" : $"{b / 1024.0:0} KB";

    private static string Dims(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var f = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            return $"{f.PixelWidth} × {f.PixelHeight}";
        }
        catch { return "—"; }
    }
}

/// <summary>Windows' own thumbnail of any file (pictures and videos alike), or null.</summary>
public static class ShellThumb
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    public static ImageSource? Get(string path, int size)
    {
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var f);
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0x0 /* SIIGBF_RESIZETOFIT */, out var hb) != 0 || hb == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DeleteObject(hb); }
        }
        catch { return null; }
    }
}
