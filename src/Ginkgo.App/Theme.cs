using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ginkgo.App;

public static class Theme
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>True once Windows has agreed to round the corners and draw the outline (Windows 11).</summary>
    public static bool HasRoundedFrame { get; private set; }

    /// <summary>Windows 11: rounded corners and a thin outline drawn by Windows itself (it leaves both off when maximised).</summary>
    public static void RoundedFrame(Window w, byte r, byte g, byte b)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int round = 2;                                        // DWMWCP_ROUND
            int ok = DwmSetWindowAttribute(h, 33, ref round, 4);   // DWMWA_WINDOW_CORNER_PREFERENCE
            int colour = r | (g << 8) | (b << 16);
            int ok2 = DwmSetWindowAttribute(h, 34, ref colour, 4); // DWMWA_BORDER_COLOR
            HasRoundedFrame = ok == 0 && ok2 == 0;
        }
        catch { HasRoundedFrame = false; }
    }

    /// <summary>Full screen: no rounded corners and no outline (DWMWCP_DONOTROUND / DWMWA_COLOR_NONE); off again restores them.</summary>
    public static void FullScreenFrame(Window w, bool full, byte r, byte g, byte b)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int round = full ? 1 : 2;
            DwmSetWindowAttribute(h, 33, ref round, 4);
            int colour = full ? unchecked((int)0xFFFFFFFE) : r | (g << 8) | (b << 16);
            DwmSetWindowAttribute(h, 34, ref colour, 4);
        }
        catch { }
    }

    /// <summary>
    /// Makes a normal window's system title bar match the theme: dark mode, and on Windows 11 the bar takes the theme's panel colour,
    /// with its text in the theme's text colour. Works whether it is called before or after the window has been created.
    /// </summary>
    public static void DarkTitleBar(Window w)
    {
        void Apply()
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int dark = IsLight() ? 0 : 1;
                if (DwmSetWindowAttribute(h, 20, ref dark, 4) != 0) DwmSetWindowAttribute(h, 19, ref dark, 4);   // DWMWA_USE_IMMERSIVE_DARK_MODE (and its old number)
                if (Application.Current?.TryFindResource("Bar") is System.Windows.Media.SolidColorBrush bar)
                {
                    var c = bar.Color; int colour = c.R | (c.G << 8) | (c.B << 16);
                    DwmSetWindowAttribute(h, 35, ref colour, 4);                                  // DWMWA_CAPTION_COLOR
                }
                if (Application.Current?.TryFindResource("Text") is System.Windows.Media.SolidColorBrush tx)
                {
                    var c = tx.Color; int colour = c.R | (c.G << 8) | (c.B << 16);
                    DwmSetWindowAttribute(h, 36, ref colour, 4);                                  // DWMWA_TEXT_COLOR
                }
            }
            catch { }
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
        w.Activated += (_, _) => Apply();                 // the theme may have changed since
    }

    private static bool IsLight()
    {
        if (Application.Current?.TryFindResource("Bar") is System.Windows.Media.SolidColorBrush b)
            return b.Color.R * 0.3 + b.Color.G * 0.59 + b.Color.B * 0.11 > 150;
        return false;
    }
}
