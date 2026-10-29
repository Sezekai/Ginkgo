// Measures how fast the real ThumbLoader fills a screen of tiles from generated thumbnail packs (random display order, like "Random" sort).
using System.Diagnostics;
using System.Windows.Threading;
using Ginkgo.App;
using Ginkgo.Data;
using SkiaSharp;

static class Program {
[STAThread]
static int Main(string[] args)
{
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
    return 0;
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
    Console.WriteLine($"::notice::shuffled={shuffled} tileW={tileW}: {ready}/{screen} in {sw.ElapsedMilliseconds} ms = {ready * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F0}/s | read {loader.AvgReadMs:F2} ms, decode {loader.AvgDecodeMs:F2} ms, thumbs/read {loader.ThumbsPerRead:F1} | {string.Join(" ", marks)}");
    loader.Dispose();
    Thread.Sleep(300);
}

}
