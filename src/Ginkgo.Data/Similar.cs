using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace Ginkgo.Data;

/// <summary>
/// Visual fingerprints for near copies: a 64-bit difference hash of a picture's smallest thumbnail (9 x 8 grey levels, each bit says
/// whether a pixel is brighter than the one to its right). Resized, re-saved or slightly edited copies differ in only a few bits.
/// Videos use the frame their thumbnail was made from.
/// </summary>
public static class VisualHash
{
    public static long FromImage(byte[] encoded)
    {
        using var bmp = SKBitmap.Decode(encoded);
        if (bmp is null) return 0;
        return FromBitmap(bmp);
    }

    public static long FromBitmap(SKBitmap bmp)
    {
        var info = new SKImageInfo(9, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var small = bmp.Resize(info, SKFilterQuality.Medium);
        if (small is null) return 0;
        ulong bits = 0; int k = 0;
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
            {
                if (Grey(small.GetPixel(x, y)) > Grey(small.GetPixel(x + 1, y))) bits |= 1UL << k;
                k++;
            }
        long v = unchecked((long)bits);
        return v == 0 ? 1 : v;                                           // 0 means "not made"
    }

    private static int Grey(SKColor c) => c.Red * 299 + c.Green * 587 + c.Blue * 114;

    public static int Distance(long a, long b) => System.Numerics.BitOperations.PopCount(unchecked((ulong)(a ^ b)));

    /// <summary>
    /// Makes the fingerprints the index lacks, for the given files (id and where their smallest thumbnail is). Waits while
    /// <paramref name="hold"/> says so. Returns every file's fingerprint, the ones already known included.
    /// </summary>
    public static Dictionary<long, long> Ensure(IndexDb db, ThumbStore micro, IReadOnlyList<(long id, int pack, long off, int len)> files,
        WorkProgress progress, Func<bool> hold, CancellationToken ct)
    {
        var known = new Dictionary<long, long>();
        using var c = db.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, vhash FROM files WHERE vhash<>0";
            using var r = cmd.ExecuteReader();
            while (r.Read()) known[r.GetInt64(0)] = r.GetInt64(1);
        }
        var todo = files.Where(f => !known.ContainsKey(f.id) && f.len > 0).ToList();
        if (todo.Count == 0) return known;
        progress.Start("Making visual fingerprints", todo.Count, 0);
        var batch = new List<(long id, long h)>();
        void Flush()
        {
            if (batch.Count == 0) return;
            using var tx = c.BeginTransaction();
            using var cmd = c.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = "UPDATE files SET vhash=$h WHERE id=$id";
            var ph = cmd.Parameters.Add("$h", SqliteType.Integer); var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, h) in batch) { ph.Value = h; pi.Value = id; cmd.ExecuteNonQuery(); }
            tx.Commit(); batch.Clear();
        }
        try
        {
            // in pack order, so the drive reads forwards
            foreach (var f in todo.OrderBy(t => t.pack).ThenBy(t => t.off))
            {
                while (hold()) { ct.ThrowIfCancellationRequested(); Thread.Sleep(150); }
                ct.ThrowIfCancellationRequested();
                long h = 0;
                try { var data = micro.Read(f.pack, f.off, f.len); if (data is not null) h = FromImage(data); } catch { }
                if (h != 0) { known[f.id] = h; batch.Add((f.id, h)); }
                Interlocked.Increment(ref progress.FilesDone);
                if (batch.Count >= 500) Flush();
            }
        }
        finally { Flush(); progress.Running = false; }
        return known;
    }
}

/// <summary>Groups of files whose fingerprints are within a few bits of each other (each group joined through near pairs).</summary>
public static class NearGroups
{
    /// <summary>
    /// Items are (key, fingerprint); only items with the same <paramref name="scopeOf"/> are compared (the duplicate finder never
    /// compares across top-level folders). A pair within <paramref name="maxBits"/> joins a group. Returns groups of 2 or more.
    /// </summary>
    public static List<List<int>> Find(IReadOnlyList<long> hashes, IReadOnlyList<string> scopeOf, int maxBits, CancellationToken ct = default)
    {
        int n = hashes.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Root(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Join(int a, int b) { int ra = Root(a), rb = Root(b); if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb); }
        maxBits = Math.Clamp(maxBits, 0, 20);
        // two fingerprints within r bits agree exactly on at least one of r+1 slices (pigeonhole): only compare inside slices
        int slices = maxBits + 1;
        foreach (var scope in Enumerable.Range(0, n).Where(i => hashes[i] != 0).GroupBy(i => scopeOf[i], StringComparer.OrdinalIgnoreCase))
        {
            var idx = scope.ToArray();
            if (idx.Length < 2) continue;
            for (int s = 0; s < slices; s++)
            {
                ct.ThrowIfCancellationRequested();
                int lo = s * 64 / slices, hi = (s + 1) * 64 / slices;
                ulong mask = hi - lo >= 64 ? ulong.MaxValue : ((1UL << (hi - lo)) - 1) << lo;
                var buckets = new Dictionary<ulong, List<int>>();
                foreach (int i in idx)
                {
                    ulong key = unchecked((ulong)hashes[i]) & mask;
                    if (!buckets.TryGetValue(key, out var l)) buckets[key] = l = new List<int>();
                    l.Add(i);
                }
                foreach (var l in buckets.Values)
                {
                    if (l.Count < 2) continue;
                    if (l.Count > 4000) continue;                       // a flat colour every blank picture shares: not useful
                    for (int a = 0; a < l.Count; a++)
                        for (int b = a + 1; b < l.Count; b++)
                            if (Root(l[a]) != Root(l[b]) && VisualHash.Distance(hashes[l[a]], hashes[l[b]]) <= maxBits) Join(l[a], l[b]);
                }
            }
        }
        return Enumerable.Range(0, n).Where(i => hashes[i] != 0).GroupBy(Root).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
    }
}

/// <summary>The low-resolution finder's test: below each minimum switched on, all of them (AND) or any (OR).</summary>
public sealed class LowResRule
{
    public string Name { get; set; } = "";
    public bool UseWidth { get; set; } public int MinWidth { get; set; } = 800;
    public bool UseHeight { get; set; } public int MinHeight { get; set; } = 800;
    public bool UseMegapixels { get; set; } = true; public double MinMegapixels { get; set; } = 0.5;
    public bool UseSize { get; set; } public double MinSizeKB { get; set; } = 100;
    public bool UseVideoHeight { get; set; } public int MinVideoHeight { get; set; } = 480;
    public bool UseDuration { get; set; } public double MinDurationSec { get; set; } = 5;
    public bool UseBitrate { get; set; } public double MinBitrateKbps { get; set; } = 1000;
    public bool Any { get; set; }                                       // false = AND, true = OR

    /// <summary>True when the file falls below the rule. Video tests apply to videos only; picture tests to pictures and GIFs.</summary>
    public bool IsLow(bool video, int w, int h, long size, double seconds)
    {
        var tests = new List<bool>();
        if (!video)
        {
            if (UseWidth) tests.Add(w < MinWidth);
            if (UseHeight) tests.Add(h < MinHeight);
            if (UseMegapixels) tests.Add((double)w * h / 1e6 < MinMegapixels);
        }
        else
        {
            if (UseVideoHeight) tests.Add(Math.Min(w, h) < MinVideoHeight);
            if (UseDuration) tests.Add(seconds < MinDurationSec);
            if (UseBitrate) tests.Add(seconds > 0 && size * 8 / 1000.0 / seconds < MinBitrateKbps);
        }
        if (UseSize) tests.Add(size / 1024.0 < MinSizeKB);
        if (tests.Count == 0) return false;
        return Any ? tests.Any(t => t) : tests.All(t => t);
    }
}
