using System.Runtime.InteropServices;
using Ginkgo.Data;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace Ginkgo.App;

/// <summary>
/// Thumbnails in the graphics card's own memory: pieces of the pack files kept in GPU buffers when memory (RAM) runs short, so
/// lookups don't go back to the hard drive. A lookup copies the thumbnail's bytes back (one small copy, well under a millisecond),
/// which is slower than memory but far faster than the drive. Only the NVIDIA card's own memory is used; Windows' allowance for
/// Ginkgo is respected, and what is held is given back when that allowance shrinks (a game starting, say).
/// </summary>
public sealed unsafe class VramTier : ISecondHome, IDisposable
{
    private readonly object _lock = new();
    private ID3D11Device? _dev;
    private ID3D11DeviceContext? _ctx;
    private ID3D11Buffer? _staging;
    private const int StagingSize = 1 << 20;
    private IntPtr _adapter3;          // IDXGIAdapter3, for Windows' memory allowance
    private readonly Dictionary<(ThumbStore, long), (ID3D11Buffer buf, int len)> _held = new();
    private long _bytes;
    public long Bytes => Interlocked.Read(ref _bytes);
    public int Pieces { get { lock (_lock) return _held.Count; } }
    public string Name { get; }
    public volatile string Problem = "";
    /// <summary>The most it may hold (bytes, 0 = Windows' allowance decides).</summary>
    public long Cap;
    public long Reads, ReadTicks;

    private IntPtr _adapter;           // kept (with a reference) so the device can be made when first needed
    private bool _lost;

    public VramTier(IntPtr adapter, string name)
    {
        Name = name;
        _adapter = adapter;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)adapter)[1])(adapter);      // AddRef
        _adapter3 = Adapter3(adapter);
    }

    /// <summary>The adapter's IDXGIAdapter3 (for Windows' memory allowance), or zero.</summary>
    public static IntPtr Adapter3(IntPtr adapter)
    {
        if (adapter == IntPtr.Zero) return IntPtr.Zero;
        var iid = new Guid("645967a4-1392-4310-a798-8053ce3e93fd");
        IntPtr a3;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)adapter)[0])(adapter, &iid, &a3) >= 0 ? a3 : IntPtr.Zero;
    }

    /// <summary>Windows' allowance for this process on the card's own memory, and what the process uses now (bytes); zeros if unknown.</summary>
    public static (long budget, long usage) Allowance(IntPtr adapter3)
    {
        if (adapter3 == IntPtr.Zero) return (0, 0);
        var info = stackalloc long[4];
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, long*, int>)(*(void***)adapter3)[14])(adapter3, 0, 0, info);   // node 0, local
        return hr >= 0 ? (info[0], info[1]) : (0, 0);
    }

    public static void ReleaseCom(ref IntPtr p) { if (p != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)p)[2])(p); p = IntPtr.Zero; } }

    /// <summary>Windows' allowance for Ginkgo on this card and what Ginkgo uses now (bytes); zeros if unknown.</summary>
    public (long budget, long usage) Allowance() => Allowance(_adapter3);

    /// <summary>How much more may be put here now: the user's limit, and Windows' allowance (keeping room for the GPU grid and others).</summary>
    public long Room()
    {
        long held = Bytes;
        long limit = Cap > 0 ? Cap : long.MaxValue;
        var (budget, usage) = Allowance();
        if (budget > 0)
        {
            long others = Math.Max(0, usage - held);                     // the GPU grid and anything else Ginkgo has on the card
            long fromWindows = (long)(budget * 0.85) - others - (512L << 20);
            limit = Math.Min(limit, Math.Max(0, fromWindows));
        }
        return limit - held;
    }

    public bool Has(ThumbStore store, long key) { lock (_lock) return _held.ContainsKey((store, key)); }

    public bool Read(ThumbStore store, long key, int inSeg, Span<byte> dst)
    {
        if (dst.Length > StagingSize) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_lock)
        {
            if (_ctx is null || _staging is null || !_held.TryGetValue((store, key), out var h) || inSeg + dst.Length > h.len) return false;
            try
            {
                _ctx.CopySubresourceRegion(_staging, 0, 0, 0, 0, h.buf, 0, new Box(inSeg, 0, 0, inSeg + dst.Length, 1, 1));
                var m = _ctx.Map(_staging, 0, MapMode.Read);
                new ReadOnlySpan<byte>((void*)m.DataPointer, dst.Length).CopyTo(dst);
                _ctx.Unmap(_staging, 0);
            }
            catch (Exception ex) { Lost(ex); return false; }
        }
        Interlocked.Increment(ref Reads);
        Interlocked.Add(ref ReadTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        return true;
    }

    /// <summary>The device is made only when something is first put here, so the card can sleep while nothing is (on battery, say).</summary>
    private bool EnsureDevice()
    {
        if (_dev is not null) return true;
        if (_lost || _adapter == IntPtr.Zero) return false;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        var hr = D3D11.D3D11CreateDevice(_adapter, DriverType.Unknown, DeviceCreationFlags.None, levels, out _dev, out _ctx);
        if (hr.Failure || _dev is null) { _dev = null; _ctx = null; Problem = $"no Direct3D 11 device (0x{hr.Code:X8})"; return false; }
        _staging = _dev.CreateBuffer(new BufferDescription(StagingSize, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read));
        return true;
    }

    private void FreeDevice()
    {
        try { _staging?.Dispose(); _ctx?.ClearState(); _ctx?.Dispose(); _dev?.Dispose(); } catch { }
        _staging = null; _ctx = null; _dev = null;
    }

    public bool Put(ThumbStore store, long key, IntPtr data, int length)
    {
        if (length <= 0 || Room() < length) return false;
        lock (_lock)
        {
            if (!EnsureDevice() || _dev is null || _ctx is null) return false;
            if (_held.ContainsKey((store, key))) return true;
            try
            {
                var buf = _dev.CreateBuffer(new BufferDescription((uint)length, BindFlags.ShaderResource));
                _ctx.UpdateSubresource(buf, 0, null, data, (uint)length, 0);
                _ctx.Flush();
                _held[(store, key)] = (buf, length);
                Interlocked.Add(ref _bytes, length);
                return true;
            }
            catch (Exception ex) { Lost(ex); return false; }
        }
    }

    public void Remove(ThumbStore store, long key)
    {
        lock (_lock)
        {
            if (!_held.Remove((store, key), out var h)) return;
            Interlocked.Add(ref _bytes, -h.len);
            h.buf.Dispose();
        }
    }

    public void RemovePack(ThumbStore store, int pack)
    {
        lock (_lock)
            foreach (var k in _held.Keys.Where(k => k.Item1 == store && (int)(k.Item2 >> 20) == pack).ToList())
            {
                var h = _held[k]; _held.Remove(k); Interlocked.Add(ref _bytes, -h.len); h.buf.Dispose();
            }
    }

    /// <summary>Everything held, for choosing what to give back: (store, pack, piece).</summary>
    public List<(ThumbStore store, int pack, int seg)> Held()
    {
        lock (_lock) return _held.Keys.Select(k => (k.Item1, (int)(k.Item2 >> 20), (int)(k.Item2 & 0xFFFFF))).ToList();
    }

    /// <summary>Lets go of everything, and of the device too (so the card can power down).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var h in _held.Values) h.buf.Dispose();
            _held.Clear(); Interlocked.Exchange(ref _bytes, 0);
            FreeDevice();
        }
    }

    /// <summary>The card was reset (sleep, driver update): everything here is gone; it is filled again from the drive.</summary>
    private void Lost(Exception ex)
    {
        Problem = "graphics memory lost: " + ex.Message;
        foreach (var h in _held.Values) { try { h.buf.Dispose(); } catch { } }
        _held.Clear(); Interlocked.Exchange(ref _bytes, 0);
        FreeDevice();
        _lost = true;
    }

    /// <summary>False after the card was reset (the owner makes a new one).</summary>
    public bool Alive { get { lock (_lock) return !_lost; } }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (var h in _held.Values) h.buf.Dispose();
            _held.Clear(); Interlocked.Exchange(ref _bytes, 0);
            FreeDevice();
            _lost = true;
        }
        if (_adapter != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)_adapter)[2])(_adapter); _adapter = IntPtr.Zero; }
        if (_adapter3 != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)_adapter3)[2])(_adapter3); _adapter3 = IntPtr.Zero; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus { public byte AC, Battery, Percent, Saver; public int Life, Full; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out PowerStatus s);
    /// <summary>Running on battery (not plugged in).</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var p) && p.AC == 0;
}
