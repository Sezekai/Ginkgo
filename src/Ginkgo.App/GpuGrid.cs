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
    /// <summary>The tile's number in the grid (so it can keep showing its last picture while a new one waits for upload).</summary>
    public int Index;
    public int X, Y, W, H;
    /// <summary>The picture (null = a plain colour, <see cref="Fill"/>).</summary>
    public ThumbLoader.Pix? Pix;
    /// <summary>While fading (A &lt; 1): the picture shown before, or null to fade from <see cref="Fill"/>.</summary>
    public ThumbLoader.Pix? Prev;
    public float A;
    public uint Fill;
    /// <summary>A playing GIF (its rendition): drawn instead of <see cref="Pix"/> once it's on the card.</summary>
    public GifClip? Gif;
    public int FrameA, FrameB;
    /// <summary>Cross-fade at the loop: how much of <see cref="FrameB"/> (the matching frame from the start) is mixed in.</summary>
    public float Weight;
}

/// <summary>One GIF rendition ready to play (its frames in RAM until they're on the card).</summary>
public sealed class GifClip
{
    public long Id; public int Tier;
    public Ginkgo.Data.GifRendition R = null!;
    /// <summary>When it started playing (ms), so each GIF starts at its first frame.</summary>
    public long Start;
    /// <summary>Its frames are on the card (set by the grid).</summary>
    public volatile bool OnCard;
}

/// <summary>
/// The GPU grid (build brief, phase 2): Direct3D 11 draws every tile as a textured rectangle into a texture the size of the view,
/// which WPF shows through a single D3DImage. Pictures are uploaded once into 4096 x 4096 pages, made as needed, and drawn from
/// there with one instanced draw per page. It does what the strip painter does: a straight copy when a picture is already the
/// tile's size, smooth scaling when it isn't (until the right size arrives), and the soft-to-sharp fade.
/// It takes the pixels the loader already decoded; nothing before drawing changes.
/// </summary>
public sealed unsafe class GpuGrid : IDisposable
{
    public const int Page = 4096;
    public const long PageBytes = (long)Page * Page * 4;
    private const int GifGroup = 1 << 20;     // draw groups at or above this are GIFs (one per GIF)

    [StructLayout(LayoutKind.Sequential)]
    private struct Inst
    {
        public float DstX, DstY, DstW, DstH;
        public float U0, V0, U1, V1;
        public float PU0, PV0, PU1, PV1;
        public float HasPrev, Unused, Alpha, Mode;      // mode 0 copy, 1 smooth, 2 plain colour
        public float R, G, B, A;
    }

    private const string Hlsl = @"
struct Inst { float4 dst; float4 uv; float4 uvp; float4 p; float4 col; };
StructuredBuffer<Inst> Insts : register(t1);
Texture2D<float4> Cur : register(t0);
Texture2D<float4> Old : register(t2);
Texture2DArray<float4> Gif : register(t3);
SamplerState Lin : register(s0);
cbuffer C : register(b0) { float2 Screen; uint Base; uint Unused; };
struct VO { float4 pos : SV_Position; float2 t : T0; float2 tp : T1; nointerpolation uint id : ID; };
VO VS(uint vid : SV_VertexID, uint iid : SV_InstanceID)
{
    Inst i = Insts[Base + iid];
    float2 c = float2(vid & 1, vid >> 1);
    float2 px = i.dst.xy + c * i.dst.zw;
    VO o;
    o.pos = float4(px.x / Screen.x * 2 - 1, 1 - px.y / Screen.y * 2, 0, 1);
    o.t = lerp(i.uv.xy, i.uv.zw, c);
    o.tp = lerp(i.uvp.xy, i.uvp.zw, c);
    o.id = Base + iid;
    return o;
}
float4 GetCur(float2 t, float4 r, float mode)
{
    if (mode < 0.5) return Cur.Load(int3(int2(floor(t)), 0));
    float2 c = clamp(t, r.xy + 0.5, r.zw - 0.5);
    return Cur.SampleLevel(Lin, c / 4096.0, 0);
}
float4 PS(VO v) : SV_Target
{
    Inst i = Insts[v.id];
    if (i.p.w > 2.5)
    {
        // a GIF frame (and, at the loop, the matching frame from the start)
        float2 c = clamp(v.t, 0.5, i.uv.zw - 0.5) / float2(i.p.x, i.p.y);
        float4 a = Gif.SampleLevel(Lin, float3(c, i.uvp.x), 0);
        if (i.uvp.z > 0.001) a = lerp(a, Gif.SampleLevel(Lin, float3(c, i.uvp.y), 0), i.uvp.z);
        return a;
    }
    if (i.p.w > 1.5) return i.col;
    float4 n = GetCur(v.t, i.uv, i.p.w);
    if (i.p.z >= 0.999) return n;
    float4 prev = i.p.x > 0.5 ? Old.Load(int3(int2(floor(v.tp)), 0)) : i.col;
    return lerp(prev, n, i.p.z);
}";

    private ID3D11Device _dev;
    private ID3D11DeviceContext _ctx;
    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11SamplerState _lin;
    private readonly ID3D11RasterizerState _raster;
    private readonly ID3D11Buffer _cb;
    private ID3D11Buffer? _insts; private ID3D11ShaderResourceView? _instView; private int _instCap;
    private ID3D11Texture2D? _target; private ID3D11RenderTargetView? _rtv;
    private IntPtr _adapter3;
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>The target's handle for Direct3D 9Ex (the D3DImage side). Changes when the size changes.</summary>
    public IntPtr SharedHandle { get; private set; }
    public string AdapterName { get; }
    /// <summary>The most pages (64 MB each) it may make; Windows' allowance can hold it to fewer.</summary>
    public int MaxPages { get; set; } = 24;

    // pages: shelves of similar heights, filled left to right; made when needed, the one used longest ago is let go when full
    private sealed class Shelf { public int Y, H, X; }
    private sealed class PageInfo
    {
        public ID3D11Texture2D Tex = null!; public ID3D11ShaderResourceView View = null!;
        public readonly List<Shelf> Shelves = new(); public int Top; public long Used; public long UsedAt;
        public readonly List<ThumbLoader.Pix> Keys = new();
    }
    private readonly List<PageInfo> _pages = new();
    private readonly Dictionary<ThumbLoader.Pix, (PageInfo page, int x, int y)> _where = new(ReferenceEqualityComparer.Instance);
    /// <summary>What each tile showed last (still on the card): shown while its new picture waits for upload.</summary>
    private readonly Dictionary<int, ThumbLoader.Pix> _shown = new();
    private sealed class GifTex { public ID3D11Texture2D Tex = null!; public ID3D11ShaderResourceView View = null!; public long Bytes; public long Used; public long UsedAt; }
    private readonly Dictionary<GifClip, GifTex> _gifs = new(ReferenceEqualityComparer.Instance);
    private readonly List<GifTex> _gifSlots = new();
    private long _gifBytes;
    /// <summary>The most graphics memory the playing GIFs may take.</summary>
    public long MaxGifBytes { get; set; } = 1024L << 20;
    public long GifBytes => _gifBytes;
    public int GifTextures => _gifs.Count;
    public int LastGifsDrawn, LastGifsWaiting;
    /// <summary>GIFs a screen away: put on the card with any upload time left, so they play the moment they scroll in.</summary>
    public List<GifClip>? PreloadGifs;

    private void FreeGif(GifClip c, GifTex g)
    {
        g.View.Dispose(); g.Tex.Dispose();
        _gifs.Remove(c);
        c.OnCard = false;
        _gifBytes -= g.Bytes;
        Interlocked.Add(ref GpuMemory.GridBytes, -g.Bytes);
    }

    /// <summary>The GIF's frames on the card, uploading them if there's time and room (null: shows its cover this frame).</summary>
    private GifTex? PlaceGif(GifClip c, long budgetEnd)
    {
        if (_gifs.TryGetValue(c, out var g)) { g.Used = _frame; return g; }
        var r = c.R;
        if (r.Frames <= 0 || r.Blocks.Length < (long)r.FrameBytes * r.Frames || Stopwatch.GetTimestamp() > budgetEnd) return null;
        long bytes = r.Blocks.Length;
        // room: let go of GIFs not on screen, oldest first
        var (budget, _) = VramTier.Allowance(_adapter3);
        bool Over() => _gifBytes + bytes > MaxGifBytes ||
            (budget > 0 && Interlocked.Read(ref GpuMemory.GridBytes) + Interlocked.Read(ref GpuMemory.ThumbBytes) + bytes + GpuMemory.Spare > budget * 0.9);
        while (Over())
        {
            GifClip? old = null; GifTex? og = null;
            foreach (var (k, v) in _gifs) if (v.Used < _frame && (og is null || v.Used < og.Used)) { old = k; og = v; }
            if (old is null) return null;
            FreeGif(old, og!);
        }
        int blockBytes = r.Bc7 ? 16 : 8;
        uint rowPitch = (uint)(r.PW / 4 * blockBytes);
        var init = new SubresourceData[r.Frames];
        fixed (byte* b = r.Blocks)
        {
            for (int f = 0; f < r.Frames; f++) init[f] = new SubresourceData((IntPtr)(b + (long)f * r.FrameBytes), rowPitch, (uint)r.FrameBytes);
            var tex = _dev.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)r.PW, Height = (uint)r.PH, MipLevels = 1, ArraySize = (uint)r.Frames,
                Format = r.Bc7 ? Format.BC7_UNorm : Format.BC1_UNorm, SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable, BindFlags = BindFlags.ShaderResource,
            }, init.AsSpan());
            g = new GifTex { Tex = tex, View = _dev.CreateShaderResourceView(tex), Bytes = bytes, Used = _frame, UsedAt = Environment.TickCount64 };
        }
        _gifs[c] = g;
        c.OnCard = true;
        _gifBytes += bytes;
        Interlocked.Add(ref GpuMemory.GridBytes, bytes);
        LastUploads++;
        return g;
    }

    private long _frame;
    private Inst[] _buf = new Inst[256];
    private (int page, int prev, int i)[] _order = new (int, int, int)[256];
    private Inst[] _sorted = new Inst[256];
    private int _waiting;

    // GPU timing without waiting: timestamps read back a few frames later
    private readonly ID3D11Query[] _tsDisjoint = new ID3D11Query[4], _tsStart = new ID3D11Query[4], _tsEnd = new ID3D11Query[4];
    private readonly bool[] _tsPending = new bool[4];

    // what F3 shows
    public int LastUploads; public double LastUploadMs, LastDrawMs, GpuMs; public int Evictions; public int LastWaiting;
    public long UsedBytes => _pages.Count * PageBytes;
    public long BudgetBytes => MaxPages * PageBytes;

    /// <param name="adapter">An IDXGIAdapter (zero with <see cref="DriverType.Warp"/>, the software device used in the tests).</param>
    public GpuGrid(IntPtr adapter, DriverType type, int maxPages, string name)
    {
        AdapterName = name;
        MaxPages = Math.Max(2, maxPages);
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
        var hr = D3D11.D3D11CreateDevice(adapter, type, DeviceCreationFlags.BgraSupport, levels, out _dev, out _ctx);
        if (hr.Failure || _dev is null) throw new InvalidOperationException($"no Direct3D 11 device (0x{hr.Code:X8})");
        _adapter3 = VramTier.Adapter3(adapter);
        var vs = Compiler.Compile(Hlsl, "VS", "grid", "vs_5_0");
        var ps = Compiler.Compile(Hlsl, "PS", "grid", "ps_5_0");
        _vs = _dev.CreateVertexShader(vs.Span);
        _ps = _dev.CreatePixelShader(ps.Span);
        _lin = _dev.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp,
            0, 1, ComparisonFunction.Never, new Color4(0, 0, 0, 0)));
        _raster = _dev.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        _cb = _dev.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer));
        for (int k = 0; k < 4; k++)
        {
            _tsDisjoint[k] = _dev.CreateQuery(QueryType.TimestampDisjoint);
            _tsStart[k] = _dev.CreateQuery(QueryType.Timestamp);
            _tsEnd[k] = _dev.CreateQuery(QueryType.Timestamp);
        }
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

    private bool MayAddPage()
    {
        if (_pages.Count >= MaxPages) return false;
        if ((_pages.Count + 1) * PageBytes <= GpuMemory.GridFloor) return true;      // always allowed a few pages
        var (budget, _) = VramTier.Allowance(_adapter3);
        return budget <= 0 || Interlocked.Read(ref GpuMemory.GridBytes) + Interlocked.Read(ref GpuMemory.ThumbBytes) + PageBytes + GpuMemory.Spare <= budget * 0.9;
    }

    private PageInfo NewPage()
    {
        var pg = new PageInfo();
        pg.Tex = _dev.CreateTexture2D(new Texture2DDescription
        {
            Width = Page, Height = Page, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });
        pg.View = _dev.CreateShaderResourceView(pg.Tex);
        _pages.Add(pg);
        Interlocked.Add(ref GpuMemory.GridBytes, PageBytes);
        return pg;
    }

    /// <summary>Pictures the card let go of since last asked (the loader drops the ones it only had on the card).</summary>
    private HashSet<ThumbLoader.Pix> _evicted = new(ReferenceEqualityComparer.Instance);
    public HashSet<ThumbLoader.Pix> TakeEvicted() { var e = _evicted; _evicted = new(ReferenceEqualityComparer.Instance); return e; }
    public bool IsResident(ThumbLoader.Pix p) => _where.ContainsKey(p);

    private void Empty(PageInfo o)
    {
        foreach (var k in o.Keys) { _where.Remove(k); if (k.Data.Length == 0) _evicted.Add(k); }
        o.Keys.Clear(); o.Shelves.Clear(); o.Top = 0;
        Evictions++;
    }

    private void FreePage(PageInfo o)
    {
        Empty(o);
        o.View.Dispose(); o.Tex.Dispose();
        _pages.Remove(o);
        Interlocked.Add(ref GpuMemory.GridBytes, -PageBytes);
    }

    /// <summary>Finds room for a w x h picture; null when every page holds something on screen now.</summary>
    private (PageInfo page, int x, int y)? Alloc(int w, int h)
    {
        int bh = Bucket(h);
        for (int pass = 0; pass < 2; pass++)
        {
            foreach (var pg in _pages)
            {
                foreach (var s in pg.Shelves)
                    if (s.H == bh && s.X + w <= Page) { var r = (pg, s.X, s.Y); s.X += w; return r; }
                if (pg.Top + bh <= Page)
                {
                    var s = new Shelf { Y = pg.Top, H = bh, X = w };
                    pg.Shelves.Add(s); pg.Top += bh;
                    return (pg, 0, s.Y);
                }
            }
            if (pass == 1) break;
            if (MayAddPage()) { NewPage(); continue; }
            // full: empty the page used longest ago (never one holding a picture on screen now)
            PageInfo? old = null;
            foreach (var pg in _pages) if (pg.Used < _frame && (old is null || pg.Used < old.Used)) old = pg;
            // every page holds something on screen (a size change: old and new pictures at once): the screen comes first, so
            // add a page anyway (up to the most allowed); the thumbnails on the card give room back within a few seconds
            if (old is null && _pages.Count < MaxPages) { NewPage(); continue; }
            if (old is null) return null;
            Empty(old);
        }
        return null;
    }

    /// <summary>Where the picture is on the card, uploading it if needed (null: no time or room left this frame).</summary>
    private (PageInfo page, int x, int y)? Place(ThumbLoader.Pix p, long budgetEnd)
    {
        if (_where.TryGetValue(p, out var at)) { at.page.Used = _frame; return at; }
        if (p.W <= 0 || p.H <= 0 || p.W > Page || p.H > Page || p.Data.Length < p.W * p.H * 4) return null;
        if (Stopwatch.GetTimestamp() > budgetEnd) return null;
        var spot = Alloc(p.W, p.H);
        if (spot is null) return null;
        var (pg, x, y) = spot.Value;
        fixed (byte* d = p.Data)
            _ctx.UpdateSubresource(pg.Tex, 0, new Box(x, y, 0, x + p.W, y + p.H, 1), (IntPtr)d, (uint)(p.W * 4), 0);
        _where[p] = spot.Value;
        pg.Keys.Add(p);
        pg.Used = _frame;
        LastUploads++;
        return spot;
    }

    private static void Col(ref Inst i, uint c)
    {
        i.R = ((c >> 16) & 255) / 255f; i.G = ((c >> 8) & 255) / 255f; i.B = (c & 255) / 255f; i.A = (c >> 24) / 255f;
    }

    /// <summary>
    /// Draws the tiles (page coordinates; <paramref name="offPx"/> is the scroll position) into the w x h target. It does not wait
    /// for the card: Direct3D keeps the hand-off to WPF in order. False when some pictures could not be uploaded this frame
    /// (those tiles keep their previous picture; draw again).
    /// </summary>
    public bool Render(IReadOnlyList<GpuTile> tiles, int offPx, int w, int h, uint background, double uploadBudgetMs)
    {
        var t0 = Stopwatch.GetTimestamp();
        _frame++;
        LastUploads = 0;
        EnsureTarget(Math.Max(1, w), Math.Max(1, h));
        ReadTimings();
        // many pictures waiting (a size change, a jump): allow more upload time this frame
        double budgetMs = _waiting > 40 ? Math.Max(uploadBudgetMs, 8) : _waiting > 0 ? Math.Max(uploadBudgetMs, 4) : uploadBudgetMs;
        long budgetEnd = t0 + (long)(budgetMs * Stopwatch.Frequency / 1000);
        bool OnScreen(in GpuTile t) => t.W > 0 && t.H > 0 && t.Y + t.H > offPx && t.Y < offPx + h && t.X < w && t.X + t.W > 0;

        // 1. everything on screen that is already on the card is marked in use first, so making room never throws it out
        for (int k = 0; k < tiles.Count; k++)
        {
            var t = tiles[k];
            if (!OnScreen(t)) continue;
            if (t.Pix is not null && _where.TryGetValue(t.Pix, out var a)) a.page.Used = _frame;
            if (t.Prev is not null && _where.TryGetValue(t.Prev, out var b)) b.page.Used = _frame;
            if (_shown.TryGetValue(t.Index, out var s) && _where.TryGetValue(s, out var c)) c.page.Used = _frame;
            if (t.Gif is not null && _gifs.TryGetValue(t.Gif, out var gt)) gt.Used = _frame;
        }
        _gifSlots.Clear();
        var gifSlot = new Dictionary<GifTex, int>();
        int gifsDrawn = 0, gifWaiting = 0;
        long gifBudgetEnd = Stopwatch.GetTimestamp() + (long)(6.0 * Stopwatch.Frequency / 1000);   // GIFs get their own upload time

        // 2. the instances (uploading what's new, on-screen tiles nearest the top first)
        if (_buf.Length < tiles.Count) { _buf = new Inst[tiles.Count * 2]; _order = new (int, int, int)[tiles.Count * 2]; _sorted = new Inst[tiles.Count * 2]; }
        var pageIndex = new Dictionary<PageInfo, int>();
        int Pi(PageInfo p) { if (!pageIndex.TryGetValue(p, out int i)) { i = _pages.IndexOf(p); pageIndex[p] = i; } return i; }
        int n = 0, waiting = 0;
        for (int k = 0; k < tiles.Count; k++)
        {
            var t = tiles[k];
            if (!OnScreen(t)) continue;
            ref var i = ref _buf[n];
            i = default;
            i.DstX = t.X; i.DstY = t.Y - offPx; i.DstW = t.W; i.DstH = t.H;
            Col(ref i, t.Fill);
            i.Alpha = Math.Clamp(t.A, 0, 1);
            int page = -1, prevPage = -1;
            GifTex? gtex = t.Gif is { } gc ? PlaceGif(gc, gifBudgetEnd) : null;
            if (t.Gif is { } gw && gtex is null && gw.R.Blocks.Length > 0) gifWaiting++;     // in memory, waiting for upload time
            if (gtex is not null)
            {
                var clip = t.Gif!;
                if (!gifSlot.TryGetValue(gtex, out int sl)) { sl = _gifSlots.Count; _gifSlots.Add(gtex); gifSlot[gtex] = sl; }
                var r = clip.R;
                i.Mode = 3; i.Alpha = 1;
                i.U0 = 0; i.V0 = 0; i.U1 = r.W; i.V1 = r.H;
                i.HasPrev = r.PW; i.Unused = r.PH;
                i.PU0 = Math.Clamp(t.FrameA, 0, r.Frames - 1); i.PV0 = Math.Clamp(t.FrameB, 0, r.Frames - 1); i.PU1 = Math.Clamp(t.Weight, 0, 1);
                _order[n] = (GifGroup + sl, -1, n);
                n++; gifsDrawn++;
                continue;
            }
            var pix = t.Pix;
            var at = pix is null ? null : Place(pix, budgetEnd);
            if (at is null && pix is not null)
            {
                // not on the card yet: keep showing what this tile showed last (stretched to fit), like the strips do
                waiting++;
                if (_shown.TryGetValue(t.Index, out var last) && _where.TryGetValue(last, out var la)) { pix = last; at = la; i.Alpha = 1; }
                else pix = null;
            }
            if (at is null || pix is null) { i.Mode = 2; i.Alpha = 1; }
            else
            {
                var (pg, x, y) = at.Value;
                page = Pi(pg);
                i.U0 = x; i.V0 = y; i.U1 = x + pix.W; i.V1 = y + pix.H;
                i.Mode = pix.W == t.W && pix.H == t.H ? 0 : 1;
                if (ReferenceEquals(pix, t.Pix)) _shown[t.Index] = pix;
                if (i.Alpha < 1 && t.Prev is { } pv && Place(pv, budgetEnd) is { } pa)
                {
                    prevPage = Pi(pa.page); i.HasPrev = 1;
                    i.PU0 = pa.x; i.PV0 = pa.y; i.PU1 = pa.x + pv.W; i.PV1 = pa.y + pv.H;
                }
            }
            _order[n] = (page, prevPage, n);
            n++;
        }
        if (PreloadGifs is { } pre)
            foreach (var c in pre) PlaceGif(c, gifBudgetEnd);       // marked as used, so making room never throws them out either
        _waiting = waiting; LastWaiting = waiting; LastGifsDrawn = gifsDrawn; LastGifsWaiting = gifWaiting;
        if (_shown.Count > 20000) _shown.Clear();
        var t1 = Stopwatch.GetTimestamp();
        LastUploadMs = (t1 - t0) * 1000.0 / Stopwatch.Frequency;

        // 3. one draw per page (and per old page, for the few tiles fading between pages)
        Array.Sort(_order, 0, n, Comparer<(int page, int prev, int i)>.Create((a, b) => a.page != b.page ? a.page.CompareTo(b.page) : a.prev != b.prev ? a.prev.CompareTo(b.prev) : a.i.CompareTo(b.i)));
        for (int k = 0; k < n; k++) _sorted[k] = _buf[_order[k].i];
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
            fixed (Inst* src = _sorted) Buffer.MemoryCopy(src, (void*)m.DataPointer, (long)_instCap * sizeof(Inst), (long)n * sizeof(Inst));
            _ctx.Unmap(_insts!, 0);
        }
        int slot = (int)(_frame % 4);
        bool timing = !_tsPending[slot];
        if (timing) { _ctx.Begin(_tsDisjoint[slot]); _ctx.End(_tsStart[slot]); }

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
            _ctx.PSSetShaderResource(1, _instView!);
            _ctx.PSSetSampler(0, _lin);
            var cb = stackalloc uint[4];
            ((float*)cb)[0] = Width; ((float*)cb)[1] = Height;
            int start = 0;
            while (start < n)
            {
                var (pg, pv, _) = _order[start];
                int end = start + 1;
                while (end < n && _order[end].page == pg && _order[end].prev == pv) end++;
                cb[2] = (uint)start;
                _ctx.UpdateSubresource(_cb, 0, null, (IntPtr)cb, 16, 0);
                if (pg >= GifGroup) _ctx.PSSetShaderResource(3, _gifSlots[pg - GifGroup].View);
                else if (pg >= 0) _ctx.PSSetShaderResource(0, _pages[pg].View);
                if (pv >= 0) _ctx.PSSetShaderResource(2, _pages[pv].View);
                _ctx.DrawInstanced(4, (uint)(end - start), 0, 0);
                start = end;
            }
        }
        if (timing) { _ctx.End(_tsEnd[slot]); _ctx.End(_tsDisjoint[slot]); _tsPending[slot] = true; }
        _ctx.Flush();
        var reason = _dev.DeviceRemovedReason;
        if (reason.Failure) throw new InvalidOperationException($"the GPU device was lost (0x{reason.Code:X8})");

        // pages nobody has used for half a minute are given back (old sizes after zooming)
        long now = Environment.TickCount64;
        foreach (var pg in _pages) if (pg.Used == _frame) pg.UsedAt = now;
        foreach (var gt in _gifs.Values) if (gt.Used == _frame) gt.UsedAt = now;
        if (_frame % 30 == 0) foreach (var (k, gt) in _gifs.Where(p => p.Value.Used < _frame && now - p.Value.UsedAt > 10_000).ToList()) FreeGif(k, gt);
        if (_frame % 60 == 0)
        {
            var (budget, _) = VramTier.Allowance(_adapter3);
            bool over = budget > 0 && Interlocked.Read(ref GpuMemory.GridBytes) + Interlocked.Read(ref GpuMemory.ThumbBytes) > budget * 0.95;
            foreach (var pg in _pages.Where(p => p.Used < _frame && (now - p.UsedAt > 10_000 || over)).OrderBy(p => p.Used).Take(over ? 2 : 1).ToList()) FreePage(pg);
        }
        LastDrawMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
        return waiting == 0 && LastGifsWaiting == 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Disjoint { public ulong Frequency; public int IsDisjoint; public int Pad; }

    /// <summary>GPU time of earlier frames, read without waiting.</summary>
    private void ReadTimings()
    {
        for (int k = 0; k < 4; k++)
        {
            if (!_tsPending[k]) continue;
            if (!_ctx.GetData<Disjoint>(_tsDisjoint[k], AsyncGetDataFlags.DoNotFlush, out var dj)) continue;
            if (!_ctx.GetData<ulong>(_tsStart[k], AsyncGetDataFlags.DoNotFlush, out var a) || !_ctx.GetData<ulong>(_tsEnd[k], AsyncGetDataFlags.DoNotFlush, out var b)) continue;
            _tsPending[k] = false;
            if (dj.IsDisjoint == 0 && dj.Frequency > 0 && b >= a) GpuMs = (b - a) * 1000.0 / dj.Frequency;
        }
    }

    /// <summary>The last frame's pixels (BGRA, Width x Height): for the tests. Waits for the card.</summary>
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

    public int PageCount => _pages.Count;

    /// <summary>Forgets every uploaded picture (they upload again as needed).</summary>
    public void ForgetAll() { foreach (var p in _pages.ToList()) FreePage(p); _shown.Clear(); }

    public void Dispose()
    {
        foreach (var p in _pages) { p.View.Dispose(); p.Tex.Dispose(); }
        Interlocked.Add(ref GpuMemory.GridBytes, -_pages.Count * PageBytes);
        foreach (var (k, gt) in _gifs.ToList()) FreeGif(k, gt);
        _pages.Clear(); _where.Clear(); _shown.Clear();
        for (int k = 0; k < 4; k++) { _tsDisjoint[k].Dispose(); _tsStart[k].Dispose(); _tsEnd[k].Dispose(); }
        _rtv?.Dispose(); _target?.Dispose(); _instView?.Dispose(); _insts?.Dispose();
        _cb.Dispose(); _raster.Dispose(); _lin.Dispose(); _ps.Dispose(); _vs.Dispose();
        _ctx.ClearState(); _ctx.Flush(); _ctx.Dispose(); _dev.Dispose();
        VramTier.ReleaseCom(ref _adapter3);
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
