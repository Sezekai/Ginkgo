using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Ginkgo.App;

public static class Theme
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Makes the system title bar of a normal window dark (used by the Settings window).</summary>
    public static void DarkTitleBar(Window w)
    {
        w.SourceInitialized += (_, _) =>
        {
            try { int on = 1; DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 20, ref on, 4); } catch { }
        };
    }
}
