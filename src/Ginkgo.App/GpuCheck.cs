using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Ginkgo.App;

/// <summary>
/// Can the GPU grid run, and on which GPU? Four steps (GPU grid build brief, "GPU check and fallbacks"):
/// 1. find the NVIDIA GPU (DXGI, high-performance order, vendor 0x10DE, at least 4 GB of its own memory);
/// 2. confirm the window's Direct3D 9Ex adapter (the one WPF draws with) is that GPU, or the Windows per-app preference is missing;
/// 3. prove it works: a Direct3D 11 device on it draws a test tile that is read back;
/// 4. note the display path: which GPU drives the monitor the window is on (a different one means Windows copies each frame across).
/// Any failed step means the Current renderer, with the step and the reason.
/// </summary>
public sealed class GpuCheck
{
    public bool Ok;
    public string Name = "", Status = "not checked yet", FailedStep = "";
    public long VideoMemory;
    /// <summary>Windows already runs Ginkgo on the high-performance GPU (per-app preference, or WPF's adapter is the NVIDIA one).</summary>
    public bool OnNvidia;
    public bool PreferenceSet;
    /// <summary>The monitor is driven by another GPU: Windows copies each frame across.</summary>
    public bool CrossGpu;
    public string DisplayGpu = "";
    public double Ms;

    public const uint Nvidia = 0x10DE;
    private const long MinMemory = 4L << 30;

    public string Short => Ok ? $"{Name} · {VideoMemory / 1073741824.0:0} GB{(CrossGpu ? $" · display on {DisplayGpu}, cross-GPU copy" : "")}" : $"{FailedStep}: {Status}";

    public static GpuCheck Run(IntPtr hwnd)
    {
        var r = new GpuCheck();
        var sw = Stopwatch.StartNew();
        try { r.Steps(hwnd); }
        catch (Exception ex) { r.Fail(r.FailedStep.Length > 0 ? r.FailedStep : "check", ex.Message); }
        r.Ms = sw.Elapsed.TotalMilliseconds;
        return r;
    }

    private void Fail(string step, string why) { Ok = false; FailedStep = step; Status = why; }

    private void Steps(IntPtr hwnd)
    {
        PreferenceSet = PreferenceIsSet();
        // 1. the NVIDIA GPU
        FailedStep = "finding the NVIDIA GPU";
        var adapters = Dxgi.HighPerformanceAdapters();
        var nv = adapters.FirstOrDefault(a => a.Vendor == Nvidia && a.Memory >= MinMemory);
        if (nv.Ptr == IntPtr.Zero)
        {
            var any = adapters.FirstOrDefault(a => a.Vendor == Nvidia);
            Fail(FailedStep, any.Ptr != IntPtr.Zero ? $"{any.Name} has only {any.Memory / 1073741824.0:0.#} GB of its own memory (needs 4)" :
                 "no NVIDIA GPU found (" + string.Join(", ", adapters.Select(a => a.Name)) + ")");
            Dxgi.Release(adapters);
            return;
        }
        Name = nv.Name; VideoMemory = nv.Memory;

        // 2. is WPF (Direct3D 9Ex) drawing on it?
        FailedStep = "checking Ginkgo runs on it";
        var monitor = MonitorFromWindow(hwnd, 2);
        var wpfLuid = D3D9.AdapterLuidFor(monitor);
        OnNvidia = wpfLuid == nv.Luid;
        // 4. (noted before step 3 so a failure still reports it) which GPU owns the monitor
        var owner = Dxgi.MonitorOwner(monitor);
        DisplayGpu = owner.name;
        CrossGpu = owner.luid != 0 && owner.luid != nv.Luid;
        if (!OnNvidia)
        {
            string wpf = adapters.FirstOrDefault(a => a.Luid == wpfLuid).Name ?? "another GPU";
            Fail(FailedStep, $"Windows runs Ginkgo on {(string.IsNullOrEmpty(wpf) ? "another GPU" : wpf)}" +
                 (PreferenceSet ? " even though the preference is set (restart Ginkgo)" : ": use the button below, then restart"));
            Dxgi.Release(adapters);
            return;
        }

        // 3. prove it works
        FailedStep = "test drawing";
        string? err = TestRender(nv.Ptr, DriverType.Unknown);
        Dxgi.Release(adapters);
        if (err is not null) { Fail(FailedStep, err); return; }

        FailedStep = "";
        Ok = true;
        Status = CrossGpu ? $"ready; the screen is driven by {DisplayGpu}, so Windows copies each frame across (can affect smoothness)" : "ready";
    }

    /// <summary>
    /// Makes a Direct3D 11 device (BGRA support) on the adapter (or WARP when <paramref name="adapter"/> is zero and the driver type
    /// is Warp), uploads a test tile, draws it into a target, and reads it back. Null when every pixel came back right.
    /// </summary>
    public static string? TestRender(IntPtr adapter, DriverType type)
    {
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        var hr = D3D11.D3D11CreateDevice(adapter, type, DeviceCreationFlags.BgraSupport, levels, out ID3D11Device device, out ID3D11DeviceContext ctx);
        if (hr.Failure || device is null) return $"could not make a Direct3D 11 device (0x{hr.Code:X8})";
        try
        {
            const uint W = 8;
            var tile = new uint[W * W];
            for (int i = 0; i < tile.Length; i++) tile[i] = 0xFF2E8B4A;                  // opaque green, BGRA
            using var src = device.CreateTexture2D(tile, Format.B8G8R8A8_UNorm, W, W);
            using var target = device.CreateTexture2D(Format.B8G8R8A8_UNorm, 16, 16, bindFlags: BindFlags.RenderTarget | BindFlags.ShaderResource);
            using var rtv = device.CreateRenderTargetView(target);
            ctx.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(0.2f, 0.2f, 0.25f, 1f));
            ctx.CopySubresourceRegion(target, 0, 4, 4, 0, src, 0);
            using var staging = device.CreateTexture2D(Format.B8G8R8A8_UNorm, 16, 16, bindFlags: BindFlags.None, usage: ResourceUsage.Staging, cpuAccessFlags: CpuAccessFlags.Read);
            ctx.CopyResource(staging, target);
            var map = ctx.Map(staging, 0, MapMode.Read);
            uint inside, outside;
            unsafe
            {
                byte* p = (byte*)map.DataPointer;
                inside = *(uint*)(p + 6 * map.RowPitch + 6 * 4);
                outside = *(uint*)(p + 1 * map.RowPitch + 1 * 4);
            }
            ctx.Unmap(staging, 0);
            if (inside != 0xFF2E8B4A) return $"the test tile came back wrong (0x{inside:X8})";
            if ((outside & 0xFF000000) != 0xFF000000) return $"the background came back wrong (0x{outside:X8})";
            return null;
        }
        catch (Exception ex) { return "test drawing failed: " + ex.Message; }
        finally { ctx?.Dispose(); device.Dispose(); }
    }

    // ---- Windows' per-app GPU preference ----

    private const string PrefKey = @"Software\Microsoft\DirectX\UserGpuPreferences";
    public static string ExePath => Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? "";

    public static bool PreferenceIsSet()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(PrefKey);
            return k?.GetValue(ExePath) is string v && v.Replace(" ", "").Contains("GpuPreference=2;", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>The same setting as Windows Settings > Display > Graphics > Ginkgo > High performance. Takes effect after a restart.</summary>
    public static bool SetPreference()
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(PrefKey);
            k.SetValue(ExePath, "GpuPreference=2;", RegistryValueKind.String);
            return true;
        }
        catch { return false; }
    }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    // ---- DXGI, by hand (only enumeration is needed here) ----

    private static class Dxgi
    {
        public struct Adapter { public IntPtr Ptr; public string Name; public uint Vendor; public long Memory; public long Luid; }

        [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        private static Guid IidFactory6 = new("c1b6694f-ff09-44a9-b03c-77900a0a1d17");
        private static Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
        private static Guid IidAdapter1 = new("29038f61-3839-4626-91fd-086879011a05");

        private static unsafe void** V(IntPtr p) => *(void***)p;
        public static unsafe void Release(IntPtr p) { if (p != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p)[2])(p); }
        public static void Release(List<Adapter> list) { foreach (var a in list) Release(a.Ptr); }

        private static unsafe Adapter Describe(IntPtr adapter)
        {
            var buf = stackalloc byte[312];                                   // DXGI_ADAPTER_DESC1
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(adapter)[10])(adapter, buf);   // IDXGIAdapter1::GetDesc1
            if (hr < 0) return new Adapter { Ptr = adapter, Name = "?" };
            string name = new string((char*)buf).TrimEnd('\0').Trim();
            uint vendor = *(uint*)(buf + 256);
            long mem = (long)*(nuint*)(buf + 272);
            long luid = (long)*(uint*)(buf + 296) | ((long)*(int*)(buf + 300) << 32);
            return new Adapter { Ptr = adapter, Name = name, Vendor = vendor, Memory = mem, Luid = luid };
        }

        /// <summary>Adapters in high-performance order (IDXGIFactory6::EnumAdapterByGpuPreference). The caller releases them.</summary>
        public static unsafe List<Adapter> HighPerformanceAdapters()
        {
            var list = new List<Adapter>();
            if (CreateDXGIFactory1(ref IidFactory6, out var f) < 0 || f == IntPtr.Zero) throw new InvalidOperationException("DXGI 1.6 is not available (Windows 10 1803 or later is needed)");
            try
            {
                var iid = IidAdapter1;
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr a;
                    int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, Guid*, IntPtr*, int>)V(f)[29])(f, i, 2 /* HIGH_PERFORMANCE */, &iid, &a);
                    if (hr < 0 || a == IntPtr.Zero) break;
                    list.Add(Describe(a));
                }
            }
            finally { Release(f); }
            return list;
        }

        /// <summary>The GPU whose output shows the monitor (EnumAdapters1 → EnumOutputs → GetDesc).</summary>
        public static unsafe (string name, long luid) MonitorOwner(IntPtr monitor)
        {
            if (CreateDXGIFactory1(ref IidFactory1, out var f) < 0 || f == IntPtr.Zero) return ("", 0);
            try
            {
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr a;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(f)[12])(f, i, &a) < 0 || a == IntPtr.Zero) break;   // EnumAdapters1
                    var d = Describe(a);
                    bool found = false;
                    for (uint j = 0; j < 16 && !found; j++)
                    {
                        IntPtr o;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(a)[7])(a, j, &o) < 0 || o == IntPtr.Zero) break;      // EnumOutputs
                        var od = stackalloc byte[96];                                                                                          // DXGI_OUTPUT_DESC
                        if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)V(o)[7])(o, od) >= 0 && *(IntPtr*)(od + 88) == monitor) found = true;
                        Release(o);
                    }
                    Release(a);
                    if (found) return (d.Name, d.Luid);
                }
            }
            finally { Release(f); }
            return ("", 0);
        }
    }

    // ---- Direct3D 9Ex: the adapter WPF draws with for this monitor ----

    private static class D3D9
    {
        [DllImport("d3d9.dll")] private static extern int Direct3DCreate9Ex(uint sdk, out IntPtr d3d);

        public static unsafe long AdapterLuidFor(IntPtr monitor)
        {
            if (Direct3DCreate9Ex(32, out var d) < 0 || d == IntPtr.Zero) throw new InvalidOperationException("Direct3D 9Ex is not available");
            var v = *(void***)d;
            try
            {
                uint n = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)v[4])(d);                       // GetAdapterCount
                for (uint i = 0; i < n; i++)
                {
                    IntPtr m = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr>)v[15])(d, i);     // GetAdapterMonitor
                    if (m != monitor) continue;
                    long luid;
                    if (((delegate* unmanaged[Stdcall]<IntPtr, uint, long*, int>)v[21])(d, i, &luid) >= 0) return luid;   // GetAdapterLUID
                }
                // monitor not matched: WPF uses the default adapter
                long l0;
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint, long*, int>)v[21])(d, 0, &l0) >= 0 ? l0 : 0;
            }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)v[2])(d); }
        }
    }
}
