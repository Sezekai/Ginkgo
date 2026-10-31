using System.IO;
using System.Windows;

namespace Ginkgo.App;

/// <summary>The GPU grid (phase 1): the GPU check, crash safety and what F3 shows. Drawing still uses the Current renderer.</summary>
public partial class MainWindow
{
    private GpuCheck? _gpu;
    private volatile bool _gpuChecking;
    /// <summary>Why the GPU grid is not drawing, when it was chosen (shown in F3 and Settings).</summary>
    private string _gpuFallback = "";
    public int GpuDeviceResets { get; private set; }

    private static string GpuMarker => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "gpu-running");

    public GpuCheck? Gpu => _gpu;
    public bool GpuChecking => _gpuChecking;
    public string GpuFallback => _gpuFallback;

    /// <summary>At start: crash safety (two unclean exits in a row with the GPU grid → start with Current), then the check.</summary>
    private void StartGpu()
    {
        bool crashed = File.Exists(GpuMarker);
        if (crashed && _settings.Renderer == "GPU") _settings.GpuUncleanExits++;
        else if (!crashed) _settings.GpuUncleanExits = 0;
        if (_settings.Renderer == "GPU" && _settings.GpuUncleanExits >= 2)
        {
            _settings.Renderer = "Current";
            _settings.GpuUncleanExits = 0;
            _gpuFallback = "Ginkgo closed unexpectedly twice in a row with the GPU grid on, so it started with Current drawing this time";
            Dispatcher.BeginInvoke(() => StatusText.Text = _gpuFallback + ". You can turn the GPU grid back on in Settings.");
        }
        SaveSettings();
        MarkGpuRunning(_settings.Renderer == "GPU");
        if (_settings.Renderer == "GPU") CheckGpu(null);
    }

    private static void MarkGpuRunning(bool on)
    {
        try
        {
            if (on) { Directory.CreateDirectory(Path.GetDirectoryName(GpuMarker)!); File.WriteAllText(GpuMarker, DateTime.Now.ToString("o")); }
            else if (File.Exists(GpuMarker)) File.Delete(GpuMarker);
        }
        catch { }
    }

    private void CleanGpuExit() { MarkGpuRunning(false); _settings.GpuUncleanExits = 0; }

    /// <summary>Runs the four-step check in the background; <paramref name="done"/> runs on the UI thread afterwards.</summary>
    public void CheckGpu(Action? done)
    {
        if (_gpuChecking) return;
        _gpuChecking = true;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var t = new Thread(() =>
        {
            var r = GpuCheck.Run(hwnd);
            Dispatcher.BeginInvoke(() =>
            {
                _gpu = r; _gpuChecking = false;
                if (_settings.Renderer == "GPU" && !r.Ok) _gpuFallback = $"GPU grid can't be used ({r.FailedStep}: {r.Status}), so Current is drawing";
                else if (r.Ok && _gpuFallback.StartsWith("GPU grid can't")) _gpuFallback = "";
                done?.Invoke();
            });
        }) { IsBackground = true, Name = "gpu-check" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }

    /// <summary>The Drawing choice changed in Settings.</summary>
    public void SetRenderer(string r)
    {
        _settings.Renderer = r;
        if (r != "GPU") _gpuFallback = "";
        MarkGpuRunning(r == "GPU");
        SaveSettings();
    }

    /// <summary>Restart Ginkgo (after setting the GPU preference, which Windows reads at start).</summary>
    public void Restart()
    {
        try
        {
            CleanGpuExit(); SaveSettings();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(GpuCheck.ExePath) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex) { StatusText.Text = "Couldn't restart: " + ex.Message; }
    }

    private string GpuDiag()
    {
        string renderer = _settings.Renderer == "GPU"
            ? (_gpu is { Ok: true } ? "GPU chosen (check passed; this build still draws with Current)" : _gpuChecking ? "GPU chosen (checking...)" : "Current (" + (_gpuFallback.Length > 0 ? _gpuFallback : "GPU check pending") + ")")
            : "Current";
        string gpu = _gpu is null ? "not checked" : _gpu.Ok ? _gpu.Short : $"{(_gpu.Name.Length > 0 ? _gpu.Name + ", " : "")}{_gpu.FailedStep}: {_gpu.Status}";
        return $" | renderer {renderer} | gpu {gpu} | gifs {_gifs.Playing} playing (limit {(_gifs.Limit == 0 ? "all" : _gifs.Limit.ToString())}) | device resets {GpuDeviceResets}";
    }
}
