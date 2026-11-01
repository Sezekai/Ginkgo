// Measures how fast the real ThumbLoader fills a screen of tiles from generated thumbnail packs (random display order, like "Random" sort).
using System.Diagnostics;
using System.Windows.Threading;
using Ginkgo.App;
using Ginkgo.Data;
using SkiaSharp;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ginkgo.Core.Layout;

static class Program {
[STAThread]
static int Main(string[] args)
{
    // GPU grid phase 1: the test drawing works on Direct3D's software device (the runners have no GPU)
    try
    {
        var err = GpuCheck.TestRender(IntPtr.Zero, Vortice.Direct3D.DriverType.Warp);
        Console.WriteLine(err is null ? "::notice::GPU test drawing on WARP: ok" : "::error::GPU test drawing on WARP: " + err);
        var g = GpuCheck.Run(IntPtr.Zero);
        Console.WriteLine($"::notice::GPU check on the runner (expected to fail, no NVIDIA): {g.Short} ({g.Ms:F0} ms)");
    }
    catch (Exception ex) { Console.WriteLine("::error::GPU check crashed: " + ex); }
    try { GpuParity(); } catch (Exception ex) { Console.WriteLine("::error::GPU grid parity check crashed: " + ex); }
    var bt = new Thread(BarCheck); bt.SetApartmentState(ApartmentState.STA); bt.Start(); bt.Join();
    var rt = new Thread(RenderCheck); rt.SetApartmentState(ApartmentState.STA); rt.Start(); rt.Join();
    int total = 30000, screen = 700;
    var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ginkgo-bench-" + Guid.NewGuid().ToString("N"));
    var stores = new ThumbStores(dir);
    var rnd = new Random(5);
    // a handful of distinct pictures (noisy so they compress like photos), three sizes each
    byte[] Enc(int w, int h, int q)
    {
        using var bmp = new SKBitmap(w, h);
        var px = new byte[w * h * 4]; rnd.NextBytes(px);
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(px[i] / 4 + (i / 4 % w) * 255 / w / 2); px[i + 1] = (byte)(px[i + 1] / 4 + 60); px[i + 2] = (byte)(px[i + 2] / 4 + 90); px[i + 3] = 255; }
        System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Webp, q).ToArray();
    }
    var aspects = new[] { 0.6, 0.75, 1.0, 1.33, 1.6, 1.78 };
    var pics = aspects.Select(a => (full: Enc(320, (int)(320 / a), 72), small: Enc(192, (int)(192 / a), 70), micro: Enc(112, (int)(112 / a), 62), a)).ToArray();
    Console.WriteLine($"::notice::thumb sizes: full {pics[2].full.Length} B, small {pics[2].small.Length} B, micro {pics[2].micro.Length} B");

    Micro(pics[2].full, pics[2].small, pics[2].micro);
    var tiers = new ThumbLoader.TierData[4];
    var st = new[] { stores.Micro, stores.Small, stores.Full, stores.Large };
    for (int t = 0; t < 4; t++) tiers[t] = new ThumbLoader.TierData(st[t], new int[total], new long[total], new int[total]);
    var kind = new int[total];
    // stored in "library order"; the display order is shuffled (a random sort)
    var libLoc = new (int pack, long off, int len)[3][];
    for (int t = 0; t < 3; t++) libLoc[t] = new (int, long, int)[total];
    for (int i = 0; i < total; i++)
    {
        int k = rnd.Next(pics.Length); kind[i] = k;
        libLoc[0][i] = st[0].Append(pics[k].micro); libLoc[1][i] = st[1].Append(pics[k].small); libLoc[2][i] = st[2].Append(pics[k].full);
    }
    stores.Flush();
    foreach (var shuffled in new[] { false, true })
    {
        var order = Enumerable.Range(0, total).ToArray();
        if (shuffled) for (int i = total - 1; i > 0; i--) { int j = rnd.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
        var tr = new ThumbLoader.TierData[4];
        for (int t = 0; t < 4; t++) tr[t] = new ThumbLoader.TierData(st[t], new int[total], new long[total], new int[total]);
        for (int d = 0; d < total; d++)
            for (int t = 0; t < 3; t++) { var l = libLoc[t][order[d]]; tr[t].Pack[d] = l.pack; tr[t].Off[d] = l.off; tr[t].Len[d] = l.len; }
        var paths = new string[total]; var origW = new int[total]; Array.Fill(origW, 2000);
        foreach (int tileW in new[] { 70, 150, 260 })
        {
            var asp = pics.Select(p => p.a).ToArray(); bool sh = shuffled; int tw = tileW;
            var th = new Thread(() => Run(sh, tw, tr, paths, origW, kind, order, asp, stores, screen)); th.SetApartmentState(ApartmentState.STA); th.Start(); th.Join();
        }
    }
    Console.WriteLine("::notice::" + string.Join(" ;; ", _out.Select(x => x.Replace(" | read", " r").Replace("thumbs/read", "t/r"))));
    return 0;
}
static readonly List<string> _out = new();
static void Out(string line) => _out.Add(line);

static void BarCheck()
{
    try
    {
        var bar = new BarPanel();
        System.Windows.Controls.Button Fixed(double w) => new System.Windows.Controls.Button { Width = w, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        for (int i = 0; i < 6; i++) bar.Children.Add(Fixed(80));                                      // 6 x 90 = 540
        var search = new System.Windows.Controls.TextBox { MinWidth = 90, Margin = new Thickness(0, 0, 10, 0) };
        BarPanel.SetPreferredWidth(search, 170); bar.Children.Add(search);                            // 180, can lose 80
        var more = Fixed(38); BarPanel.SetPinned(more, true); bar.Children.Add(more); bar.MoreButton = more;   // 48
        var hide = Fixed(38); BarPanel.SetPinned(hide, true); bar.Children.Add(hide);                 // 48
        var res = new List<string>(); bool ok = true;
        foreach (var (w, wantOverflow, wantSearch) in new (double, int, double)[] { (900, 0, 170), (740, 0, 142), (700, 0, 102), (600, 2, -1), (400, 4, -1) })
        {
            bar.Measure(new Size(w, 40)); bar.Arrange(new Rect(0, 0, w, 40));
            double sw = search.ActualWidth;
            bool moreShown = System.Windows.Media.VisualTreeHelper.GetOffset(more).X >= 0, hideShown = System.Windows.Media.VisualTreeHelper.GetOffset(hide).X >= 0;
            bool good = bar.Overflow.Count == wantOverflow && (wantSearch < 0 || Math.Abs(sw - wantSearch) < 1.5) && (wantOverflow > 0) == moreShown && hideShown;
            if (!good) ok = false;
            res.Add($"{w}:{bar.Overflow.Count} over, search {sw:F0}, more {(moreShown ? "on" : "off")}");
        }
        Console.WriteLine($"::notice::bar check: {(ok ? "ok" : "WRONG")} | {string.Join(" | ", res)}");
        if (!ok) Console.WriteLine("::error::bar check failed");
    }
    catch (Exception ex) { Console.WriteLine("::error::bar check crashed: " + ex); }
}

static void RenderCheck()
{
    try
    {
        var items = Enumerable.Range(0, 4000).Select(i => new ItemSpec(0.5f + (i % 9) * 0.25f)).ToArray();
        var view = new MasonryView { Width = 900, Height = 600 };
        view.Measure(new Size(900, 600)); view.Arrange(new Rect(0, 0, 900, 600)); view.UpdateLayout();
        view.TileTarget = 120;
        view.SetItems(items, null);
        var lay = MasonryLayout.Compute(items, 900f, MasonryLayout.ColumnsFor(900f, 120f, 3f), 3f);
        Color Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
            (double r, double g, double b) = h switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }
        int bad = 0, checkedPx = 0; var log = new List<string>();
        foreach (double off in new double[] { 0, 37, 255, 256, 257, 300, 513, 1000, 2047, 2048, 5000, 4999, 1000, 12, 20000 })
        {
            view.Offset = off; view.UpdateLayout();
            double real = view.Offset;
            var rtb = new RenderTargetBitmap(900, 600, 96, 96, PixelFormats.Pbgra32); rtb.Render(view);
            var px = new byte[900 * 600 * 4]; rtb.CopyPixels(px, 900 * 4, 0);
            int mine = 0, tot = 0;
            for (int i = 0; i < items.Length; i++)
            {
                double cy = lay.Y[i] + lay.H[i] / 2 - Math.Round(real), cx = lay.X[i] + lay.W[i] / 2;
                if (cy < 8 || cy > 590 || lay.H[i] < 12) continue;
                var c = Hsv(((i * 7) % 24) * 15.0, 0.35, 0.55);
                int o = ((int)cy * 900 + (int)cx) * 4;
                tot++; checkedPx++;
                if (Math.Abs(px[o + 2] - c.R) > 3 || Math.Abs(px[o + 1] - c.G) > 3 || Math.Abs(px[o] - c.B) > 3) { mine++; bad++; }
            }
            log.Add($"{real:F0}:{tot - mine}/{tot}");
        }
        Console.WriteLine($"::notice::grid render check (strips): {checkedPx - bad}/{checkedPx} tile centres right | {string.Join(" ", log)}");
        if (bad > 0) Console.WriteLine($"::error::grid render check found {bad} wrong tile centres");
    }
    catch (Exception ex) { Console.WriteLine("::error::grid render check crashed: " + ex); }
}

static void Micro(byte[] full, byte[] small, byte[] micro)
{
    foreach (var (name, data, w0) in new[] { ("full", full, 320), ("small", small, 192), ("micro", micro, 112) })
    foreach (int target in new[] { 70, 150, 260 })
    {
        if (target > w0 * 1.3) continue;
        int th = (int)(target * 0.75);
        double Time(Action a) { for (int i = 0; i < 20; i++) a(); var sw = Stopwatch.StartNew(); for (int i = 0; i < 300; i++) a(); return sw.Elapsed.TotalMilliseconds / 300; }
        double plain = Time(() => { using var b = SKBitmap.Decode(data); });
        double med = Time(() => { using var b = SKBitmap.Decode(data); using var r = b.Resize(new SKImageInfo(target, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium); });
        double low = Time(() => { using var b = SKBitmap.Decode(data); using var r = b.Resize(new SKImageInfo(target, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Low); });
        string dims = "";
        double scaled = Time(() =>
        {
            using var codec = SKCodec.Create(new SKMemoryStream(data));
            var d = codec.GetScaledDimensions(target / (float)w0);
            using var bm = new SKBitmap(new SKImageInfo(d.Width, d.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            codec.GetPixels(bm.Info, bm.GetPixels());
            dims = $"{d.Width}x{d.Height}";
        });
        Out($"decode {name}->{target}px: decode only {plain:F2} ms | +resize medium {med:F2} | +resize low {low:F2} | codec scaled decode {scaled:F2} ms ({dims})");
    }
}

static void Run(bool shuffled, int tileW, ThumbLoader.TierData[] tiers, string[] paths, int[] origW, int[] kind, int[] order, double[] aspects, ThumbStores stores, int screen)
{
    var disp = Dispatcher.CurrentDispatcher;
    int ready = 0;
    var loader = new ThumbLoader(tiers, paths, origW, stores.Large, long.MaxValue, (_, _) => { }, disp, () => { }, 96L << 20, 48L << 20);
    int first = 5000;
    loader.SetWanted(first - 12, first + screen + 12);
    var sw = Stopwatch.StartNew();
    var marks = new List<string>(); int nextMark = 100;
    var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(11) };
    timer.Tick += (_, _) =>
    {
        int got = 0;
        for (int i = first; i < first + screen; i++)
        {
            double a = aspects[kind[order[i]]];
            int w = tileW + (i % 5) * 7, h = (int)(w / a);
            if (loader.Get(i, w, h) is not null) got++;
        }
        ready = got;
        while (ready >= nextMark && nextMark <= screen) { marks.Add($"{nextMark}@{sw.ElapsedMilliseconds}ms"); nextMark += 100; }
        if (got >= screen || sw.Elapsed.TotalSeconds > 40) { timer.Stop(); disp.InvokeShutdown(); }
    };
    timer.Start();
    Dispatcher.Run();
    Out($"shuffled={shuffled} tileW={tileW}: {ready}/{screen} in {sw.ElapsedMilliseconds} ms = {ready * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0}/s | read {loader.AvgReadMs:F2} ms, decode {loader.AvgDecodeMs:F2} ms, thumbs/read {loader.ThumbsPerRead:F1} | {string.Join(" ", marks)}");
    loader.Dispose();
    Thread.Sleep(300);
}


// GPU grid (phase 2): the GPU draws the same picture as the strip painter. WARP (Direct3D's software device) stands in for the GPU.
static void GpuParity()
{
    var rnd = new Random(11);
    const int W = 900, H = 700, PageH = 7000;
    uint bg = Ginkgo.Core.Imaging.TileBlit.Pack(18, 18, 20), fade = Ginkgo.Core.Imaging.TileBlit.Pack(40, 40, 46);
    ThumbLoader.Pix Pic(int w, int h)
    {
        var d = new byte[w * h * 4];
        int r0 = rnd.Next(256), g0 = rnd.Next(256), b0 = rnd.Next(256);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                d[i] = (byte)((b0 + x * 160 / w) & 255); d[i + 1] = (byte)((g0 + y * 160 / h) & 255); d[i + 2] = (byte)((r0 + (x + y) * 80 / (w + h)) & 255); d[i + 3] = 255;
            }
        return new ThumbLoader.Pix { Data = d, W = w, H = h, SrcW = w };
    }
    var tiles = new List<(GpuTile t, int mode)>();      // mode 0 exact, 1 scaled, 2 fade, 3 plain
    for (int col = 0; col < 4; col++)
    {
        int x = col * 226, y = 0;
        while (y < PageH)
        {
            int w = 222, h = rnd.Next(150, 400);
            var t = new GpuTile { X = x, Y = y, W = w, H = h, A = 1, Fill = fade };
            int r = rnd.Next(100), mode;
            if (r < 55) { t.Pix = Pic(w, h); mode = 0; }
            else if (r < 70) { t.Pix = Pic(w / 2, h / 2); mode = 1; }
            else if (r < 80) { t.Pix = Pic(w * 3 / 2, h * 3 / 2); mode = 1; }
            else if (r < 90) { t.Pix = Pic(w, h); t.Prev = Pic(w / 2, h / 2); t.A = (float)(0.2 + rnd.NextDouble() * 0.6); mode = 2; }
            else if (r < 94) { t.Pix = Pic(w, h); t.A = 0.5f; mode = 2; }
            else { t.Fill = Ginkgo.Core.Imaging.TileBlit.Pack((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)); mode = 3; }
            tiles.Add((t, mode));
            y += h + 4;
        }
    }
    var list = tiles.Select(p => p.t).ToList();
    byte[] Cpu(int off)
    {
        var buf = new byte[W * H * 4];
        Ginkgo.Core.Imaging.TileBlit.Fill(buf, W * 4, W, H, 0, 0, W, H, bg);
        foreach (var (t, mode) in tiles)
        {
            int y = t.Y - off;
            if (y >= H || y + t.H <= 0) continue;
            if (t.Pix is null) Ginkgo.Core.Imaging.TileBlit.Fill(buf, W * 4, W, H, t.X, y, t.W, t.H, t.Fill);
            else if (t.A < 1)
                Ginkgo.Core.Imaging.TileBlit.Blend(buf, W * 4, W, H, t.X, y, t.W, t.H, t.Pix.Data, t.Pix.W, t.Pix.H,
                    t.Prev is null ? ReadOnlySpan<byte>.Empty : t.Prev.Data, t.Prev?.W ?? 0, t.Prev?.H ?? 0, (int)(t.A * 256), t.Fill);
            else if (t.Pix.W == t.W && t.Pix.H == t.H) Ginkgo.Core.Imaging.TileBlit.Copy(buf, W * 4, W, H, t.X, y, t.Pix.Data, t.Pix.W, t.Pix.H);
            else Ginkgo.Core.Imaging.TileBlit.CopyScaledSmooth(buf, W * 4, W, H, t.X, y, t.W, t.H, t.Pix.Data, t.Pix.W, t.Pix.H);
        }
        return buf;
    }
    var sw = Stopwatch.StartNew();
    var grid = new GpuGrid(IntPtr.Zero, Vortice.Direct3D.DriverType.Warp, 2, "WARP");
    int good = 0, all = 0; double worstMean = 0; var bad = new List<string>(); var per = new List<string>();
    int[] offsets = { 0, 37, 255, 256, 257, 300, 513, 1000, 2047, 2048, 5000, 4999, 1000, 12, 6300 };
    for (int oi = 0; oi <= offsets.Length; oi++)
    {
        bool lost = oi == offsets.Length;
        int off = lost ? 2048 : offsets[oi];
        if (lost) { grid.Dispose(); grid = new GpuGrid(IntPtr.Zero, Vortice.Direct3D.DriverType.Warp, 2, "WARP"); }   // a lost device: make it again, upload again
        bool complete = grid.Render(list, off, W, H, bg, 10_000);
        var gpu = grid.ReadBack();
        var cpu = Cpu(off);
        long sum = 0;
        for (int i = 0; i < gpu.Length; i++) sum += Math.Abs(gpu[i] - cpu[i]);
        double mean = sum / (double)gpu.Length;
        worstMean = Math.Max(worstMean, mean);
        int ok = 0, n = 0;
        foreach (var (t, mode) in tiles)
        {
            int y0 = Math.Max(0, t.Y - off), y1 = Math.Min(H, t.Y - off + t.H);
            if (y1 <= y0) continue;
            int cx = t.X + t.W / 2, cy = (y0 + y1) / 2;
            int i = (cy * W + cx) * 4, d = 0;
            for (int c = 0; c < 4; c++) d = Math.Max(d, Math.Abs(gpu[i + c] - cpu[i + c]));
            int tol = mode switch { 0 => 1, 3 => 1, 2 => 14, _ => 24 };
            n++;
            if (d <= tol) ok++; else if (bad.Count < 6) bad.Add($"off {off} tile at {t.X},{t.Y} mode {mode}: off by {d}");
        }
        good += ok; all += n;
        per.Add($"{(lost ? "after reset " : "")}{off}:{ok}/{n}{(complete ? "" : " (incomplete)")}, mean {mean:F2}");
    }
    grid.Dispose();
    string line = $"GPU grid parity (WARP): {good}/{all} tile centres match the strip painter, worst mean difference {worstMean:F2}/255 per channel, {sw.ElapsedMilliseconds} ms | " + string.Join(" | ", per);
    Console.WriteLine((good == all && worstMean < 3 ? "::notice::" : "::error::") + line + (bad.Count > 0 ? " || " + string.Join("; ", bad) : ""));
}
}
