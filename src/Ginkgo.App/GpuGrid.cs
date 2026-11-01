using System.Diagnostics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Ginkgo.App;

/// <summary>One tile for the GPU grid: where it goes (device pixels, in page coordinates) and what it shows.</summary>
public struct GpuTile
{
    public int X, Y, W, H;
    /// <summary>The picture (null = a plain colour, <see cref="Fill"/>).</summary>
    public ThumbLoader.Pix? Pix;
    /// <summary>While fading (A &lt; 1): the picture shown before, or null to fade from <see cref="Fill"/>.</summary>
    public ThumbLoader.Pix? Prev;
    public float A;
    public uint Fill;
}

/// <summary>
/// The GPU grid (build brief, phase 2): Direct3D 11 draws every tile as a textured rectangle into one texture the size of the view,
/// which WPF shows through a single D3DImage. Pictures are uploaded once into a texture array of 4096 x 4096 pages and drawn
/// from there; everything on screen is one instanced draw. It does the same things the strip painter does: a straight copy when
/// a picture is already the tile's size, smooth scaling when it isn't (until the right size arrives), and the soft-to-sharp fade.
/// It takes the pixels the loader already decoded; nothing before drawing changes.
/// </summary>
public sealed unsafe class GpuGrid : IDisposable
{
    public const int Page = 4096;

    [StructLayout(LayoutKind.Sequential)]
    private struct Inst
    {
        public float DstX, DstY, DstW, DstH;
        public float U0, V0, U1, V1;
        public float PU0, PV0, PU1, PV1;
        public float Slice, PrevSlice, Alpha, Mode;      // mode 0 copy, 1 smooth, 2 plain colour
        public float R, G, B, A;
    }

    private const string Hlsl = @"
struct Inst { float4 dst; float4 uv; float4 uvp; float4 p; float4 col; };
StructuredBuffer<Inst> Insts : register(t1);
Texture2DArray<float4> Atlas : register(t0);
SamplerState Lin : register(s0);
cbuffer C : register(b0) { float2 Screen; float2 Unused; };
struct VO { float4 pos : SV_Position; float2 t : T0; float2 tp : T1; nointerpolation uint id : ID; };
VO VS(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst i = Insts[iid];
    float2 c = float2(vid & 1, vid >> 1);
    float2 px = i.dst.xy + c * i.dst.zw;
    VO o;
    o.pos = float4(px.x / Screen.x * 2 - 1, 1 - px.y / Screen.y * 2, 0, 1);
    o.t = lerp(i.uv.xy, i.uv.zw, c);
    o.tp = lerp(i.uvp.xy, i.uvp.zw, c);
    o.id = iid;
    return o;
}
float4 Get(float2 t, float4 r, float slice, float mode)
{
    if (mode < 0.5) return Atlas.Load(int4(int2(floor(t)), (int)slice, 0));
    float2 c = clamp(t, r.xy + 0.5, r.zw - 0.5);
    return Atlas.SampleLevel(Lin, float3(c / 4096.0, slice), 0);
}
float4 PS(VO v) : SV_Target
{
    Inst i = Insts[v.id];
    if (i.p.w > 1.5) return i.col;
    float4 n = Get(v.t, i.uv, i.p.x, i.p.w);
    if (i.p.z >= 0.999) return n;
    float4 prev = i.p.y >= 0 ? Get(v.tp, i.uvp, i.p.y, 0) : i.col;
    return lerp(prev, n, i.p.z);
}";

    private ID3D11Device _dev;
    private ID3D11DeviceContext _ctx;
    private readonly ID3D11Texture2D _atlas;
    private readonly ID3D11ShaderResourceView _atlasView;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _lin;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11Buffer _cb;
    private readonly ID3D11Query _done;
    private ID3D11Buffer? _insts; private ID3D11ShaderResourceView? _instView; private int _instCap;
    private ID3D11Texture2D? _target; private ID3D11RenderTargetView? _rtv;
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The target's handle for Direct3D 9Ex (the D3DImage side). Changes when the size changes.</summary>
    public IntPtr SharedHandle { get; private set; }
    public int Slices { get; }
    public string AdapterName { get; }

    // atlas pages: shelves of similar heights, filled left to right; a full array lets go of the page used longest ago
    private sealed class Shelf { public int Y, H, X; }
    private sealed class PageInfo { public readonly List<Shelf> Shelves = new(); public int Top; public long Used; public readonly List<ThumbLoader.Pix> Keys = new(); }
    private readonly PageInfo[] _pages;
    private readonly Dictionary<ThumbLoader.Pix, (int page, int x, int y)> _where = new(ReferenceEqualityComparer.Instance);
    private long _frame;
    private Inst[] _buf = new Inst[256];

    // what F3 shows
    public int LastUploads; public double LastUploadMs, LastDrawMs; public int Evictions;
    public long UsedBytes => _pages.Count(p => p.Top > 0) * (long)Page * Page * 4;
    public long BudgetBytes => (long)Slices * Page * Page * 4;

    /// <param name="adapter">An IDXGIAdapter (null with <see cref="DriverType.Warp"/>, the software device used in the tests).</param>
    public GpuGrid(IntPtr adapter, DriverType type, int slices, string name)
    {
        AdapterName = name;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var hr = D3D11.D3D11CreateDevice(adapter, type, DeviceCreationFlags.BgraSupport, levels, out _dev, out _ctx);
        if (hr.Failure || _dev is null) throw new InvalidOperationException($"no Direct3D 11 device (0x{hr.Code:X8})");
        Slices = slices;
        _pages = Enumerable.Range(0, slices).Select(_ => new PageInfo()).ToArray();
        _atlas = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = Page, Height = Page, MipLevels = 1, ArraySize = (uint)slices, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        _atlasView = _dev.CreateShaderResourceView(_atlas);
        var vs = Compiler.Compile(Hlsl, "VS", "grid", "vs_5_0");
        var ps = Compiler.Compile(Hlsl, "PS", "grid", "ps_5_0");
        _vs = _dev.CreateVertexShader(vs.Span);
        _ps = _dev.CreatePixelShader(ps.Span);
        _lin = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            0, 1, ComparisonFunction.Never, new Color4(0, 0, 0, 0)));
        _raster = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cb = _dev.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
        _done = _dev.CreateQuery(QueryType.Event);
    }

    private void EnsureTarget(int w, int h)
    {
        if (_target is not null && w == Width && h == Height) return;
        _rtv?.Dispose(); _target?.Dispose();
        _target = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource, MiscFlags = ResourceOptionFlags.Shared,
        });
        _rtv = _dev.CreateRenderTargetView(_target);
        Width = w; Height = h;
        // IDXGIResource::GetSharedHandle (vtable 8)
        using var res = _target.QueryInterface<IDXGIResource>();
        IntPtr handle;
        var p = res.NativePointer;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)(*(void***)p)[8])(p, &handle);
        SharedHandle = hr >= 0 ? handle : IntPtr.Zero;
    }

    private static int Bucket(int h) => h <= 16 ? 16 : (h + 15) & ~15;

    /// <summary>Finds room for a w x h picture; null when every page is in use this frame.</summary>
    private (int page, int x, int y)? Alloc(int w, int h)
    {
        int bh = Bucket(h);
        for (int pass = 0; pass < 2; pass++)
        {
            for (int pi = 0; pi < _pages.Length; pi++)
            {
                var pg = _pages[pi];
                foreach (var s in pg.Shelves)
                    if (s.H == bh && s.X + w <= Page) { var r = (pi, s.X, s.Y); s.X += w; return r; }
                if (pg.Top + bh <= Page)
                {
                    var s = new Shelf { Y = pg.Top, H = bh, X = w };
                    pg.Shelves.Add(s); pg.Top += bh;
                    return (pi, 0, s.Y);
                }
            }
            if (pass == 1) break;
            // full: let go of the page used longest ago (never one used for this frame)
            int old = -1;
            for (int pi = 0; pi < _pages.Length; pi++)
                if (_pages[pi].Used < _frame && (old < 0 || _pages[pi].Used < _pages[old].Used)) old = pi;
            if (old < 0) return null;
            var o = _pages[old];
            foreach (var k in o.Keys) _where.Remove(k);
            o.Keys.Clear(); o.Shelves.Clear(); o.Top = 0;
            Evictions++;
        }
        return null;
    }

    /// <summary>Where the picture is in the atlas, uploading it if needed (null: no time or room left this frame).</summary>
    private (int page, int x, int y)? Place(ThumbLoader.Pix p, ref long budgetEnd)
    {
        if (_where.TryGetValue(p, out var at)) { _pages[at.page].Used = _frame; return at; }
        if (p.W <= 0 || p.H <= 0 || p.W > Page || p.H > Page || p.Data.Length < p.W * p.H * 4) return null;
        if (Stopwatch.GetTimestamp() > budgetEnd) return null;
        var spot = Alloc(p.W, p.H);
        if (spot is null) return null;
        var (pg, x, y) = spot.Value;
        fixed (byte* d = p.Data)
            _ctx.UpdateSubresource(_atlas, (uint)pg, new Box(x, y, 0, x + p.W, y + p.H, 1), (IntPtr)d, (uint)(p.W * 4), 0);
        _where[p] = spot.Value;
        _pages[pg].Keys.Add(p);
        _pages[pg].Used = _frame;
        LastUploads++;
        return spot;
    }

    private static void Col(ref Inst i, uint c)
    {
        i.R = ((c >> 16) & 255) / 255f; i.G = ((c >> 8) & 255) / 255f; i.B = (c & 255) / 255f; i.A = (c >> 24) / 255f;
    }

    /// <summary>
    /// Draws the tiles (page coordinates; <paramref name="offPx"/> is the scroll position) into the w x h target and waits until the
    /// GPU has finished. False when some pictures could not be uploaded this frame (they show their plain colour; draw again).
    /// </summary>
    public bool Render(IReadOnlyList<GpuTile> tiles, int offPx, int w, int h, uint background, double uploadBudgetMs)
    {
        var t0 = Stopwatch.GetTimestamp();
        _frame++;
        LastUploads = 0;
        EnsureTarget(Math.Max(1, w), Math.Max(1, h));
        long budgetEnd = t0 + (long)(uploadBudgetMs * Stopwatch.Frequency / 1000);
        if (_buf.Length < tiles.Count) _buf = new Inst[tiles.Count * 2];
        int n = 0; bool complete = true;
        // on-screen tiles are uploaded first: the caller lists them nearest the top; tiles off screen are skipped
        for (int k = 0; k < tiles.Count; k++)
        {
            var t = tiles[k];
            if (t.W <= 0 || t.H <= 0 || t.Y + t.H <= offPx || t.Y >= offPx + h || t.X >= w || t.X + t.W <= 0) continue;
            ref var i = ref _buf[n];
            i = default;
            i.DstX = t.X; i.DstY = t.Y - offPx; i.DstW = t.W; i.DstH = t.H;
            Col(ref i, t.Fill);
            i.Alpha = Math.Clamp(t.A, 0, 1); i.PrevSlice = -1;
            var at = t.Pix is null ? null : Place(t.Pix, ref budgetEnd);
            if (at is null)
            {
                if (t.Pix is not null) complete = false;
                i.Mode = 2; i.Alpha = 1;
            }
            else
            {
                var (pg, x, y) = at.Value;
                i.Slice = pg; i.U0 = x; i.V0 = y; i.U1 = x + t.Pix!.W; i.V1 = y + t.Pix.H;
                i.Mode = t.Pix.W == t.W && t.Pix.H == t.H ? 0 : 1;
                if (i.Alpha < 1 && t.Prev is { } pv && Place(pv, ref budgetEnd) is { } pa)
                {
                    i.PrevSlice = pa.page; i.PU0 = pa.x; i.PV0 = pa.y; i.PU1 = pa.x + pv.W; i.PV1 = pa.y + pv.H;
                }
            }
            n++;
        }
        var t1 = Stopwatch.GetTimestamp();
        LastUploadMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;

        if (n > _instCap)
        {
            _instView?.Dispose(); _insts?.Dispose();
            _instCap = Math.Max(256, n * 2);
            _insts = _dev.CreateBuffer(new BufferDescription((uint)(_instCap * sizeof(Inst)), BindFlags.ShaderResource, ResourceUsage.Dynamic,
                CpuAccessFlags.Write, ResourceOptionFlags.BufferStructured, (uint)sizeof(Inst)));
            _instView = _dev.CreateShaderResourceView(_insts);
        }
        if (n > 0)
        {
            var m = _ctx.Map(_insts!, 0, MapMode.WriteDiscard);
            fixed (Inst* src = _buf) Buffer.MemoryCopy(src, (void*)m.DataPointer, (long)_instCap * sizeof(Inst), (long)n * sizeof(Inst));
            _ctx.Unmap(_insts!, 0);
        }
        var screen = stackalloc float[4] { Width, Height, 0, 0 };
        _ctx.UpdateSubresource(_cb, 0, null, (IntPtr)screen, 16, 0);

        _ctx.ClearRenderTargetView(_rtv!, new Color4(((background >> 16) & 255) / 255f, ((background >> 8) & 255) / 255f, (background & 255) / 255f, 1f));
        if (n > 0)
        {
            _ctx.OMSetRenderTargets(_rtv!);
            _ctx.RSSetViewport(0, 0, Width, Height);
            _ctx.RSSetState(_raster);
            _ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            _ctx.VSSetShader(_vs); _ctx.PSSetShader(_ps);
            _ctx.VSSetConstantBuffer(0, _cb);
            _ctx.VSSetShaderResource(1, _instView);
            _ctx.PSSetShaderResource(0, _atlasView);
            _ctx.PSSetShaderResource(1, _instView!);
            _ctx.PSSetSampler(0, _lin);
            _ctx.DrawInstanced(4, (uint)n, 0, 0);
        }
        // finish before WPF shows the texture
        _ctx.End(_done);
        _ctx.Flush();
        var waitEnd = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10;
        while (!_ctx.GetData<int>(_done, AsyncGetDataFlags.None, out _) && Stopwatch.GetTimestamp() < waitEnd) Thread.SpinWait(50);
        var reason = _dev.DeviceRemovedReason;
        if (reason.Failure) throw new InvalidOperationException($"the GPU device was lost (0x{reason.Code:X8})");
        LastDrawMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
        return complete;
    }

    /// <summary>The last frame's pixels (BGRA, Width x Height): for the tests.</summary>
    public byte[] ReadBack()
    {
        using var staging = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CPUAccessFlags = CpuAccessFlags.Read,
        });
        _ctx.CopyResource(staging, _target!);
        var m = _ctx.Map(staging, 0, MapMode.Read);
        var px = new byte[Width * Height * 4];
        for (int y = 0; y < Height; y++)
            Marshal.Copy(m.DataPointer + (nint)(y * m.RowPitch), px, y * Width * 4, Width * 4);
        _ctx.Unmap(staging, 0);
        return px;
    }

    /// <summary>Forgets every uploaded picture (they upload again as needed).</summary>
    public void ForgetAll() { _where.Clear(); foreach (var p in _pages) { p.Keys.Clear(); p.Shelves.Clear(); p.Top = 0; } }

    public void Dispose()
    {
        _rtv?.Dispose(); _target?.Dispose(); _instView?.Dispose(); _insts?.Dispose();
        _done.Dispose(); _cb.Dispose(); _raster.Dispose(); _lin.Dispose(); _ps.Dispose(); _vs.Dispose();
        _atlasView.Dispose(); _atlas.Dispose();
        _ctx.ClearState(); _ctx.Flush(); _ctx.Dispose(); _dev.Dispose();
    }
}

/// <summary>
/// The Direct3D 9Ex side of the hand-off: WPF's D3DImage takes a Direct3D 9 surface, so the Direct3D 11 target is opened here
/// through its shared handle. Both devices must be on the same GPU (the GPU check makes sure of that).
/// </summary>
public sealed unsafe class GpuPresenter : IDisposable
{
    [DllImport("d3d9.dll")] private static extern int Direct3DCreate9Ex(uint sdk, out IntPtr d3d);
    private IntPtr _d3d, _dev, _tex, _surf;
    private IntPtr _handle;

    private static void** V(IntPtr p) => *(void***)p;
    private static void Release(ref IntPtr p) { if (p != IntPtr.Zero) { ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(p)[2])(p); p = IntPtr.Zero; } }

    /// <param name="luid">The GPU to use (the same one as the Direct3D 11 device); 0 = the first.</param>
    public GpuPresenter(IntPtr hwnd, long luid)
    {
        int hr = Direct3DCreate9Ex(32, out _d3d);
        if (hr < 0 || _d3d == IntPtr.Zero) throw new InvalidOperationException($"no Direct3D 9Ex (0x{hr:X8})");
        uint n = ((delegate* unmanaged[Stdcall]<IntPtr, uint>)V(_d3d)[4])(_d3d), ordinal = 0;
        for (uint i = 0; i < n && luid != 0; i++)
        {
            long l;
            if (((delegate* unmanaged[Stdcall]<IntPtr, uint, long*, int>)V(_d3d)[21])(_d3d, i, &l) >= 0 && l == luid) { ordinal = i; break; }
        }
        var pp = stackalloc byte[64];
        new Span<byte>(pp, 64).Clear();
        *(uint*)(pp + 0) = 1; *(uint*)(pp + 4) = 1;                 // 1 x 1 back buffer (never presented)
        *(int*)(pp + 24) = 1;                                       // D3DSWAPEFFECT_DISCARD
        *(IntPtr*)(pp + 32) = hwnd;
        *(int*)(pp + 40) = 1;                                       // windowed
        *(uint*)(pp + 60) = 0x80000000;                             // D3DPRESENT_INTERVAL_IMMEDIATE
        IntPtr dev;
        const uint flags = 0x40 | 0x4 | 0x2;                         // hardware vertex processing, multithreaded, keep the FPU state
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, byte*, void*, IntPtr*, int>)V(_d3d)[20])(_d3d, ordinal, 1, hwnd, flags, pp, null, &dev);
        if (hr < 0) { Release(ref _d3d); throw new InvalidOperationException($"no Direct3D 9Ex device (0x{hr:X8})"); }
        _dev = dev;
    }

    /// <summary>The Direct3D 9 surface for the Direct3D 11 target with this shared handle (opened once per handle).</summary>
    public IntPtr Surface(IntPtr sharedHandle, int w, int h)
    {
        if (sharedHandle == _handle && _surf != IntPtr.Zero) return _surf;
        Release(ref _surf); Release(ref _tex);
        IntPtr tex, handle = sharedHandle;
        // IDirect3DDevice9::CreateTexture(w, h, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &tex, &shared)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, int, int, IntPtr*, IntPtr*, int>)V(_dev)[23])(_dev, (uint)w, (uint)h, 1, 1, 21, 0, &tex, &handle);
        if (hr < 0) throw new InvalidOperationException($"couldn't open the shared texture (0x{hr:X8})");
        _tex = tex;
        IntPtr surf;
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)V(_tex)[18])(_tex, 0, &surf);       // GetSurfaceLevel(0)
        if (hr < 0) throw new InvalidOperationException($"couldn't get the shared surface (0x{hr:X8})");
        _surf = surf; _handle = sharedHandle;
        return _surf;
    }

    public void Dispose() { Release(ref _surf); Release(ref _tex); Release(ref _dev); Release(ref _d3d); }
}
