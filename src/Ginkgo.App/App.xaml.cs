using System.Runtime;
using System.Windows;
namespace Ginkgo.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // avoid long, screen-stopping full collections while the app is in use
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        base.OnStartup(e);
        var settings = AppSettings.Load();
        ThemeKit.Apply(settings.Theme, settings.Look);
        if (!AppSettings.Exists)
        {
            // first run: library, folders to leave out, where the thumbnails go, and a theme
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var first = new FirstRunWindow(settings);
            if (first.ShowDialog() != true) { Shutdown(); return; }
            settings.Save();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }
        var w = new MainWindow();
        MainWindow = w;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        w.Show();
    }

    /// <summary>Starts Ginkgo again (after something that needs a fresh start: a reset, moving the cache, a new library folder).</summary>
    public static void Restart()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            // a moment's wait, so this copy has closed its files before the next one opens them
            if (exe is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & start \"\" \"{exe}\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden });
        }
        catch { }
        Current.Shutdown();
    }
}
