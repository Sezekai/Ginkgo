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

    /// <summary>Makes the system title bar of a normal window dark (used by the Settings window).</summary>
    public static void DarkTitleBar(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, 4); } catch { }
        };
    }
}
