using System.IO;
using System.Windows;

namespace Ginkgo.App;

/// <summary>The GPU grid: the GPU check, starting and stopping the GPU drawing, recovering when the device is lost, crash safety and F3.</summary>
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
        View.GpuFailed += OnGpuFailed;
        View.GpuFrontBufferBack += () => { GpuDeviceResets++; StopGrid(); ApplyRenderer(); };
        CheckGpu(null);          // also finds the card for thumbnails in graphics memory
    }

    /// <summary>Makes (or remakes, after the card was reset) the store for thumbnails in the NVIDIA card's memory.</summary>
    private void ApplyVramTier()
    {
        var w = _engine.Warmer;
        if (w.Vram is { Alive: false } dead) { w.Vram = null; Task.Delay(1500).ContinueWith(_ => dead.Dispose()); GpuDeviceResets++; w.VramRemade++; }
        if (w.Vram is not null || _gpu is null) return;
        if (_gpu.Luid == 0) { w.VramStatus = "No NVIDIA graphics card with its own memory was found, so this isn't available."; return; }
        IntPtr adapter = GpuCheck.AdapterByLuid(_gpu.Luid);
        try
        {
            if (adapter == IntPtr.Zero) throw new InvalidOperationException("the NVIDIA card went missing");
            w.Vram = new VramTier(adapter, _gpu.Name) { Cap = (long)(_settings.ThumbVramCapGB * 1073741824.0) };
            w.VramStatus = "";
        }
        catch (Exception ex) { w.VramStatus = "The graphics card's memory couldn't be used: " + ex.Message; }
        finally { if (adapter != IntPtr.Zero) GpuCheck.Release(adapter); }
    }

    private GpuGrid? _grid;
    private GpuPresenter? _presenter;
    private readonly List<long> _gpuFailures = new();
    private bool _gpuOffForSession;

    /// <summary>Starts or stops the GPU drawing to match the Drawing setting and the check.</summary>
    private void ApplyRenderer()
    {
        bool want = _settings.Renderer == "GPU" && _gpu is { Ok: true } && !_gpuOffForSession;
        if (want && _grid is null) StartGrid();
        else if (!want && _grid is not null) StopGrid();
    }

    private void StartGrid()
    {
        var g = _gpu!;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        IntPtr adapter = GpuCheck.AdapterByLuid(g.Luid);
        try
        {
            if (adapter == IntPtr.Zero) throw new InvalidOperationException("the NVIDIA GPU went missing");
            int slices = (int)Math.Clamp(g.VideoMemory / 4 / GpuGrid.PageBytes, 4, 12);   // pages are made as needed and let go after 10 s unused: at most 768 MB
            _grid = new GpuGrid(adapter, Vortice.Direct3D.DriverType.Unknown, slices, g.Name);
            _presenter = new GpuPresenter(hwnd, g.Luid);
            View.UseGpu(_grid, _presenter);
            _gpuFallback = "";
            _engine.StartGifRenditions();      // the GIF versions it will play (made in the background, paused while scrolling)
            if (_engine.GifMaker is { } gm)
            {
                if (_player is null)
                {
                    _player = new GifPlayer(gm);
                    _player.Loaded += () => Dispatcher.BeginInvoke(View.Refresh);
                    gm.BigMade += id => { _player.Made(id, 2); Dispatcher.BeginInvoke(View.Refresh); };
                    gm.BigDropped += () => _player.ForgetBig();
                }
                ApplyGifSettings();
                View.Player = _player;
                _gifs.BudgetPerGif = 384L << 20; _gifs.MaxFrames = 3000;     // only the hovered GIF plays the original: full quality, full length
                View.IdOf = i => _model is not null && i >= 0 && i < _order.Length && _order[i] >= 0 && _order[i] < _model.Id.Length ? _model.Id[_order[i]] : -1;
            }
        }
        catch (Exception ex)
        {
            StopGrid();
            _gpuFallback = $"GPU grid couldn't start ({ex.Message}), so Current is drawing";
            StatusText.Text = _gpuFallback + ".";
        }
        finally { if (adapter != IntPtr.Zero) GpuCheck.Release(adapter); }
    }

    private GifPlayer? _player;

    /// <summary>The GIF settings (Settings → GPU grid, and GIFs playing at once in Optimisations).</summary>
    public void ApplyGifSettings()
    {
        if (_player is null) return;
        _player.Animate = _settings.GifAnimate;
        _player.CrossFade = _settings.GifCrossFade;
        _player.CrossFadeSec = _settings.GifCrossFadeSec;
        _player.FixedFps = _settings.GifFps;
        _player.Limit = _settings.GifLimit;
        if (_engine.GifMaker is { } gm) gm.CapMs = (int)(_settings.GifLengthSec * 1000);
        View.Refresh();
    }

    private void StopGrid()
    {
        View.Player = null;
        _gifs.BudgetPerGif = 24L << 20; _gifs.MaxFrames = 300;
        View.UseGpu(null, null);
        try { _grid?.Dispose(); } catch { }
        try { _presenter?.Dispose(); } catch { }
        _grid = null; _presenter = null;
    }

    /// <summary>Drawing failed (device lost and the like): make it again; twice within a minute and Current draws for the rest of the session.</summary>
    private void OnGpuFailed(Exception ex)
    {
        GpuDeviceResets++;
        StopGrid();
        long now = Environment.TickCount64;
        _gpuFailures.Add(now);
        _gpuFailures.RemoveAll(t => now - t > 60_000);
        if (_gpuFailures.Count >= 2)
        {
            _gpuOffForSession = true;
            _gpuFallback = $"the GPU grid failed twice within a minute ({ex.Message}), so Current is drawing until Ginkgo restarts";
            StatusText.Text = "The " + _gpuFallback + ".";
            return;
        }
        ApplyRenderer();
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
                if (r.Ok) _gpuOffForSession = false;
                if (_settings.Renderer == "GPU" && !r.Ok) _gpuFallback = $"GPU grid can't be used ({r.FailedStep}: {r.Status}), so Current is drawing";
                else if (r.Ok && _gpuFallback.StartsWith("GPU grid can't")) _gpuFallback = "";
                ApplyRenderer();
                ApplyVramTier();
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
        if (r == "GPU" && _gpu is null) CheckGpu(null); else ApplyRenderer();
    }

    /// <summary>The GPU grid is drawing now.</summary>
    public bool GpuDrawing => View.UsingGpu;

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
        string renderer = View.UsingGpu ? "GPU" : _settings.Renderer == "GPU"
            ? (_gpuChecking ? "GPU chosen (checking...)" : "Current (" + (_gpuFallback.Length > 0 ? _gpuFallback : "GPU grid not started") + ")")
            : "Current";
        if (View.Gpu is { } gg)
            renderer += $" | vram {gg.UsedBytes / 1048576} / {gg.BudgetBytes / 1048576} MB ({gg.PageCount} pages, {gg.Evictions} emptied) | uploads {Perf.GpuUploads}/s, max {Perf.GpuUploadMax:F1} ms, waiting {Perf.GpuWaiting} | gpu draw {Perf.GpuDrawMax:F1} ms (card {Perf.GpuTimeMax:F2} ms)";
        string gpu = _gpu is null ? "not checked" : _gpu.Ok ? _gpu.Short : $"{(_gpu.Name.Length > 0 ? _gpu.Name + ", " : "")}{_gpu.FailedStep}: {_gpu.Status}";
        var vw = _engine.Warmer;
        string vram = vw.Vram is { } vt && vw.VramActive ? $" | thumbs on card {vt.Bytes / 1073741824.0:F1} GB, {Interlocked.Read(ref vt.Reads)} lookups, refused {vt.Refused}, remade {vw.VramRemade}" : "";
        string play = View.UsingGpu && _player is { } pl
            ? $" | gifs {pl.Playing}/{pl.OnScreen} playing at {pl.Fps} fps, {pl.TierNote}{(pl.Paused ? ", paused (Ginkgo not in front)" : "")}, {(View.Gpu?.GifTextures ?? 0)} on card ({(View.Gpu?.GifBytes ?? 0) / 1048576} MB), {pl.RamBytes / 1048576} MB in memory, {pl.Waiting} waiting to be read, {View.Gpu?.LastGifsWaiting ?? 0} waiting for the card" : "";
        string gifr = _engine.GifMaker is { } gm && gm.Stats.Running
            ? $" | gif renditions {gm.Stats.Made:N0}/{gm.Stats.Animated - gm.Stats.Stills:N0} made, {gm.Stats.Pending:N0} to do, {gm.Stats.Bytes / 1048576:N0} MB{(gm.Stats.Paused ? ", paused while scrolling" : "")}{(gm.Stats.MakingBig ? ", making an extra-large one" : "")}{(gm.Stats.Failed > 0 ? $", {gm.Stats.Failed} failed" : "")}" : "";
        return vram + play + gifr + $" | renderer {renderer} | gpu {gpu} | gifs {_gifs.Playing} playing (limit {(_gifs.Limit == 0 ? "all" : _gifs.Limit.ToString())}) | device resets {GpuDeviceResets}";
    }
}
