using System.Runtime;
using System.Windows;
namespace Ginkgo.App;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // avoid long, screen-stopping full collections while the app is in use
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        DispatcherUnhandledException += (_, a) => { Crashed(a.Exception); a.Handled = true; Shutdown(1); };
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Crashed(a.ExceptionObject as Exception);
        base.OnStartup(e);
        var settings = AppSettings.Load();
        ThemeKit.Apply(settings.Theme, settings.Look);
        if (!AppSettings.Exists && !Smoke)
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
        if (Smoke)
        {
            // the build's start-up check: open, run for a while, close (any error on the way fails the build)
            w.ContentRendered += async (_, _) => { await System.Threading.Tasks.Task.Delay(15000); w.Close(); };
        }
        w.Show();
    }

    /// <summary>Started with --smoke (the build's start-up check): errors end the process with code 3 instead of showing a message.</summary>
    public static bool Smoke => Environment.GetCommandLineArgs().Contains("--smoke");
    private static int _crashed;
    /// <summary>Something went wrong that nothing else caught: it is written to crash.log in Ginkgo's data folder and shown,
    /// instead of Ginkgo vanishing without a word.</summary>
    private static void Crashed(Exception? ex)
    {
        if (System.Threading.Interlocked.Exchange(ref _crashed, 1) == 1) return;
        string text = ex?.ToString() ?? "Unknown error";
        string log = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "crash.log");
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(log)!);
            System.IO.File.AppendAllText(log, $"---- {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}{text}{Environment.NewLine}");
        }
        catch { }
        if (Smoke) Environment.Exit(3);
        try { MessageBox.Show($"Ginkgo hit an error and has to close.\n\n{ex?.Message}\n\nThe details are in:\n{log}", "Ginkgo", MessageBoxButton.OK, MessageBoxImage.Error); }
        catch { }
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
