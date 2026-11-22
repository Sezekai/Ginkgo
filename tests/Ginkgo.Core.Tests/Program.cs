using Ginkgo.Core;
using Ginkgo.Core.Imaging;
using Ginkgo.Core.Layout;

int pass = 0, fail = 0;
void Check(bool ok, string name) { if (ok) pass++; else { fail++; Console.WriteLine("FAIL: " + name); } }

// ---- Classifier ----
Check(ContentClassifier.ClassifyRelativeFolder("Artists/Foo") == ContentLevel.General, "plain is General");
foreach (var n in new[] { "NSFW", "[NSFW]", "nsfw" })
    Check(ContentClassifier.ClassifyRelativeFolder($"Artists/{n}/Foo") == ContentLevel.Nsfw, $"{n} is Nsfw");
foreach (var n in new[] { "Nsfw", "NSFW ", "[nsfw]", "NSFWish" })
    Check(ContentClassifier.ClassifyRelativeFolder($"Artists/{n}") == ContentLevel.General, $"'{n}' not a level");
foreach (var v in new[] { "Very", "VERY", "[Very]", "[VERY]" })
{
    Check(ContentClassifier.ClassifyRelativeFolder($"A/NSFW/{v}") == ContentLevel.Very, $"{v} inside NSFW is Very");
    Check(ContentClassifier.ClassifyRelativeFolder($"A/NSFW/{v}/Deep/Deeper") == ContentLevel.Very, $"{v} inherited below");
    Check(ContentClassifier.ClassifyRelativeFolder($"A/{v}") == ContentLevel.General, $"{v} outside NSFW is General");
    Check(ContentClassifier.ClassifyRelativeFolder($"A/NSFW/X/{v}") == ContentLevel.Nsfw, $"{v} not directly inside NSFW stays Nsfw");
}
Check(ContentClassifier.ClassifyRelativeFolder("Connected Art/NSFW/Very") == ContentLevel.Very, "connected art very");
Check(ContentClassifier.ClassifyRelativeFolder("") == ContentLevel.General, "root General");
Check(ContentClassifier.IsLevelFolder("NSFW", "A") && !ContentClassifier.IsLevelFolder("Very", "A"), "level folder detection");

// ---- File destinations ----
{
    var none = new Func<string, bool>(_ => false);
    Check(FileDestinations.LevelBase("Anime\\NSFW\\Very") == "Anime", "level base strips NSFW\\Very");
    Check(FileDestinations.LevelBase("Anime\\NSFW") == "Anime", "level base strips NSFW");
    Check(FileDestinations.LevelBase("Anime\\Very") == "Anime\\Very", "Very outside NSFW is a normal folder");
    Check(FileDestinations.ForLevel("Anime", ContentLevel.Nsfw, none) == "Anime\\NSFW", "general to NSFW subfolder");
    Check(FileDestinations.ForLevel("Anime", ContentLevel.Very, none) == "Anime\\NSFW\\Very", "general to Very subfolder");
    Check(FileDestinations.ForLevel("Anime\\NSFW", ContentLevel.Very, none) == "Anime\\NSFW\\Very", "NSFW to Very");
    Check(FileDestinations.ForLevel("Anime\\NSFW\\Very", ContentLevel.General, none) == "Anime", "Very up to General");
    Check(FileDestinations.ForLevel("Anime\\NSFW\\Very", ContentLevel.Nsfw, none) == "Anime\\NSFW", "Very up to NSFW");
    Check(FileDestinations.ForLevel("Anime\\NSFW", ContentLevel.General, none) == "Anime", "NSFW up to General");
    Check(FileDestinations.ForLevel("Anime\\NSFW", ContentLevel.Nsfw, none) is null, "already NSFW");
    Check(FileDestinations.ForLevel("Anime\\NSFW\\Artist", ContentLevel.General, none) is null, "NSFW from further up: can't go General here");
    Check(FileDestinations.ForLevel("Anime\\NSFW\\Artist", ContentLevel.Very, none) == "Anime\\NSFW\\Artist\\NSFW\\Very", "Very below an NSFW artist");
    Check(FileDestinations.ForLevel("Anime", ContentLevel.Nsfw, p => p == "Anime\\[NSFW]") == "Anime\\[NSFW]", "uses the existing spelling");
    Check(FileDestinations.SortTarget(FileDestinations.Upscale, "Anime Style\\NSFW") == "Sort\\Upscale\\Anime Style\\NSFW", "upscale keeps the folder");
    Check(FileDestinations.CombineName("Anime Style\\NSFW") == "Anime Style - NSFW", "combine name");
    Check(FileDestinations.CombineTarget("Anime Style\\NSFW", p => p.EndsWith(" 1") || p.EndsWith(" 2")) == "Sort\\Combine - Join Together\\Anime Style - NSFW 3", "combine numbering");
    Check(FileDestinations.AverageTarget("", ContentLevel.General, none) == "Average & LQ", "average general");
    Check(FileDestinations.AverageTarget("", ContentLevel.Very, p => p == "Average & LQ\\[NSFW]" || p == "Average & LQ\\[NSFW]\\[Very]") == "Average & LQ\\[NSFW]\\[Very]", "average very uses [NSFW]\\[Very]");
    Check(FileDestinations.AverageTarget("AI", ContentLevel.Nsfw, none) == "Average & LQ\\AI\\NSFW", "average subfolder nsfw");
    Check(FileDestinations.FreeName("a.jpg", n => n == "a.jpg" || n == "a (2).jpg") == "a (3).jpg", "free name");
    Check(FileDestinations.SameFolder(new[] { "A\\B", "A/B" }) && !FileDestinations.SameFolder(new[] { "A", "B" }), "same folder");
}

// ---- Masonry ----
var rnd = new Random(1);
var specs = new ItemSpec[5000];
for (int i = 0; i < specs.Length; i++) specs[i] = new ItemSpec(0.4f + (float)rnd.NextDouble() * 2f, i % 97 == 0 ? 3 : 1);
var m = MasonryLayout.Compute(specs, 1200, 6, 8);
bool overlap = false;
for (int i = 0; i < m.Count && !overlap; i++)
    for (int j = i + 1; j < Math.Min(m.Count, i + 60); j++)
        if (m.X[i] < m.X[j] + m.W[j] - .01f && m.X[j] < m.X[i] + m.W[i] - .01f &&
            m.Y[i] < m.Y[j] + m.H[j] - .01f && m.Y[j] < m.Y[i] + m.H[i] - .01f) { overlap = true; break; }
Check(!overlap, "no overlapping tiles");
Check(m.X.Zip(m.W).All(p => p.First + p.Second <= 1200.01f), "inside width");
Check(m.Y.Zip(m.H).All(p => p.First + p.Second <= m.TotalHeight + .01f), "inside total height");

var vis = new List<int>();
foreach (var (t, b) in new[] { (0f, 900f), (5000f, 6000f), (m.TotalHeight - 500, m.TotalHeight), (123f, 124f) })
{
    m.GetVisible(t, b, vis);
    var expected = Enumerable.Range(0, m.Count).Where(i => m.Y[i] <= b && m.Y[i] + m.H[i] >= t).ToList();
    Check(vis.SequenceEqual(expected), $"visible matches brute force [{t},{b}]");
}
var many = new ItemSpec[1_000_000];
for (int i = 0; i < many.Length; i++) many[i] = new ItemSpec(0.5f + (i % 17) * 0.1f);
var sw = System.Diagnostics.Stopwatch.StartNew();
var big = MasonryLayout.Compute(many, 2400, 10, 6);
var tLayout = sw.ElapsedMilliseconds; sw.Restart();
for (int k = 0; k < 1000; k++) big.GetVisible(k * 1000f, k * 1000f + 1400, vis);
Console.WriteLine($"1M items: layout {tLayout} ms, 1000 visible queries {sw.ElapsedMilliseconds} ms, last count {vis.Count}");

var span = MasonryLayout.Compute(new[] { new ItemSpec(1), new ItemSpec(1), new ItemSpec(2, 2) }, 210, 2, 10);
Check(span.W[2] == 210 && span.Y[2] >= span.Y[0] + span.H[0], "spanning item sits below both columns");

// ---- Grid ----
// justified rows: every row shares one height and fills the width
{
    var rndJ = new Random(7);
    var its = new ItemSpec[500];
    for (int i = 0; i < its.Length; i++) its[i] = new ItemSpec(0.4f + (float)rndJ.NextDouble() * 2.2f);
    var jr = JustifiedLayout.Compute(its, 1200, 200, 3);
    bool rowsOk = true, fill = true, noOverlap = true;
    int a0 = 0;
    while (a0 < its.Length)
    {
        int b0 = a0;
        while (b0 + 1 < its.Length && jr.Y[b0 + 1] == jr.Y[a0]) b0++;
        for (int i = a0; i <= b0; i++) if (Math.Abs(jr.H[i] - jr.H[a0]) > 0.001f) rowsOk = false;
        if (b0 + 1 < its.Length && Math.Abs(jr.X[b0] + jr.W[b0] - 1200) > 0.05f) fill = false;     // every row but the last reaches the right edge
        for (int i = a0; i < b0; i++) if (jr.X[i] + jr.W[i] + 3 > jr.X[i + 1] + 0.01f) noOverlap = false;
        if (b0 + 1 < its.Length && jr.H[a0] > 200.01f) rowsOk = false;                              // never taller than the target
        a0 = b0 + 1;
    }
    Check(rowsOk, "justified: tiles in a row share one height, never above target");
    Check(fill, "justified: rows fill the full width");
    Check(noOverlap, "justified: no overlap in a row");
    var visJ = new List<int>();
    jr.GetVisible(2000, 2600, visJ);
    bool visOk = visJ.Count > 0;
    for (int i = 0; i < its.Length; i++) { bool inside = jr.Y[i] + jr.H[i] >= 2000 && jr.Y[i] <= 2600; if (inside != visJ.Contains(i)) visOk = false; }
    Check(visOk, "justified: visible-range search is exact");
    Check(Math.Abs(jr.H[its.Length - 1] - 200) < 0.01f || jr.H[its.Length - 1] < 200, "justified: last row is not stretched");
    Check(JustifiedLayout.Compute(new ItemSpec[0], 500, 100, 3).TotalHeight == 0, "justified: empty list");
}
var g = new GridLayout(1003, 1000, 4, 1f, 0);
Check(g.VisibleRange(0, 10) == (0, 4), "grid first row");
Check(g.VisibleRange(250, 260) == (4, 8), "grid second row");
var (f, e) = g.VisibleRange(1e9f, 2e9f); Check(e == 1003 && f <= e, "grid clamped");

// ---- ReadPlanner ----
{
    int n = 200; var pk = new int[n]; var of = new long[n]; var ln = new int[n];
    long pos = 0; for (int i = 0; i < n; i++) { pk[i] = i < 100 ? 0 : 1; if (i == 100) pos = 0; of[i] = pos; ln[i] = 20000; pos += 20000; }
    var plan = ReadPlanner.Make(pk, of, ln, 50);
    Check(plan.Length <= 512 * 1024, "plan within size limit");
    Check(plan.LastIndex - plan.FirstIndex + 1 >= 24 && plan.FirstIndex < 50 && plan.LastIndex > 50, $"plan covers neighbours (got {plan.FirstIndex}..{plan.LastIndex})");
    Check(ReadPlanner.Covers(plan, pk, of, ln, 60) && !ReadPlanner.Covers(plan, pk, of, ln, 150), "covers neighbours, not other pack");
    var edge = ReadPlanner.Make(pk, of, ln, 99);
    Check(edge.Pack == 0 && edge.LastIndex == 99, "plan stops at pack boundary");
    var scattered = new long[n]; for (int i = 0; i < n; i++) scattered[i] = (long)((i * 7919) % n) * 1_000_000;
    var sp = ReadPlanner.Make(pk, scattered, ln, 50);
    Check(sp.FirstIndex == 50 && sp.LastIndex == 50 && sp.Length == 20000, "scattered items: reads just the one");
    var tl = ReadPlanner.Make(new[] { 0 }, new long[] { 0 }, new[] { 100 }, 0);
    Check(tl.Length == 100, "single item list");
}

// ---- tile blit ----
{
    int dw = 8, dh = 6, st = dw * 4;
    var surf = new byte[st * dh];
    uint Px(int x, int y) => BitConverter.ToUInt32(surf, y * st + x * 4);
    TileBlit.Fill(surf, st, dw, dh, 0, 0, dw, dh, 0xFF112233);
    Check(Px(0, 0) == 0xFF112233 && Px(7, 5) == 0xFF112233, "blit: fill whole surface");
    TileBlit.Fill(surf, st, dw, dh, -2, -2, 4, 4, 0xFF445566);
    Check(Px(0, 0) == 0xFF445566 && Px(1, 1) == 0xFF445566 && Px(2, 2) == 0xFF112233, "blit: fill clipped at top-left");
    var tile = new byte[3 * 2 * 4];
    for (int i = 0; i < 6; i++) BitConverter.GetBytes(0xFF000000u | (uint)(i + 1)).CopyTo(tile, i * 4);
    TileBlit.Copy(surf, st, dw, dh, 6, 4, tile, 3, 2);                 // hangs over the right edge
    Check(Px(6, 4) == 0xFF000001 && Px(7, 4) == 0xFF000002 && Px(6, 5) == 0xFF000004 && Px(7, 5) == 0xFF000005, "blit: copy clipped at right edge");
    TileBlit.Copy(surf, st, dw, dh, -1, 0, tile, 3, 2);                // hangs over the left edge
    Check(Px(0, 0) == 0xFF000002 && Px(1, 0) == 0xFF000003, "blit: copy clipped at left edge");
    TileBlit.Fill(surf, st, dw, dh, 0, 0, dw, dh, 0);
    TileBlit.CopyScaled(surf, st, dw, dh, 2, 1, 4, 2, tile, 3, 2);     // 3x2 stretched to 4x2 (nearest)
    Check(Px(2, 1) == 0xFF000001 && Px(5, 1) == 0xFF000003 && Px(2, 2) == 0xFF000004 && Px(1, 1) == 0 && Px(6, 1) == 0, "blit: scaled copy");
    var white = new byte[4]; BitConverter.GetBytes(0xFFFFFFFFu).CopyTo(white, 0);
    var black = new byte[4]; BitConverter.GetBytes(0xFF000000u).CopyTo(black, 0);
    TileBlit.Blend(surf, st, dw, dh, 0, 0, 2, 2, white, 1, 1, black, 1, 1, 0, 0);
    Check(Px(0, 0) == 0xFF000000, "blit: blend alpha 0 shows previous");
    TileBlit.Blend(surf, st, dw, dh, 0, 0, 2, 2, white, 1, 1, black, 1, 1, 256, 0);
    Check(Px(1, 1) == 0xFFFFFFFE || Px(1, 1) == 0xFFFFFFFF, "blit: blend alpha 256 shows new");
    TileBlit.Blend(surf, st, dw, dh, 0, 0, 2, 2, white, 1, 1, ReadOnlySpan<byte>.Empty, 0, 0, 128, 0xFF000000);
    uint mid = Px(0, 0) & 0xFF;
    Check(mid >= 126 && mid <= 129, $"blit: blend halfway without previous (got {mid})");
    // smooth (bilinear) scaling
    {
        var flat = new byte[4 * 4 * 4]; for (int i = 0; i < 16; i++) BitConverter.GetBytes(0xFF336699u).CopyTo(flat, i * 4);
        TileBlit.Fill(surf, st, dw, dh, 0, 0, dw, dh, 0);
        TileBlit.CopyScaledSmooth(surf, st, dw, dh, 1, 1, 6, 4, flat, 4, 4);
        bool allSame = true; for (int yy = 1; yy < 5; yy++) for (int xx = 1; xx < 7; xx++) if (Px(xx, yy) != 0xFF336699) allSame = false;
        Check(allSame && Px(0, 0) == 0 && Px(7, 5) == 0, "blit: smooth scale of a flat colour stays flat, stays inside its rectangle");
        TileBlit.Fill(surf, st, dw, dh, 0, 0, dw, dh, 0);
        TileBlit.CopyScaledSmooth(surf, st, dw, dh, 2, 1, 3, 2, tile, 3, 2);   // same size: identical to a plain copy
        Check(Px(2, 1) == 0xFF000001 && Px(4, 1) == 0xFF000003 && Px(3, 2) == 0xFF000005, "blit: smooth scale at the same size is a plain copy");
        var ramp = new byte[2 * 4]; BitConverter.GetBytes(0xFF000000u).CopyTo(ramp, 0); BitConverter.GetBytes(0xFF0000FFu).CopyTo(ramp, 4);
        TileBlit.Fill(surf, st, dw, dh, 0, 0, dw, dh, 0);
        TileBlit.CopyScaledSmooth(surf, st, dw, dh, 0, 0, 8, 1, ramp, 2, 1);   // 2 px black to white-ish blue stretched to 8
        uint a0 = Px(0, 0) & 0xFF, a3 = Px(3, 0) & 0xFF, a4 = Px(4, 0) & 0xFF, a7 = Px(7, 0) & 0xFF;
        Check(a0 == 0 && a7 == 255 && a3 > a0 && a4 > a3 && a7 >= a4, $"blit: smooth scale ramps evenly ({a0},{a3},{a4},{a7})");
    }
}

// ---- folder tree (Browse) ----
{
    Check(FolderPaths.Display(@"Artists\A\NSFW\Very") == @"Artists\A", "level folders merge into their parent");
    Check(FolderPaths.Display("Artists/B/nsfw") == @"Artists\B", "forward slashes and lower-case nsfw");
    Check(FolderPaths.Display(@"Anime\Very") == @"Anime\Very", "Very outside NSFW is an ordinary folder");
    Check(FolderPaths.Display(@"NSFW") == "", "a top-level NSFW folder belongs to the root");
    Check(FolderPaths.IsUnder(@"Artists\A", "Artists") && !FolderPaths.IsUnder(@"ArtistsX\A", "Artists") && FolderPaths.IsUnder("x", ""), "is-under checks whole names");
    Check(FolderPaths.ChildOf(@"Artists\A\B", "Artists") == @"Artists\A" && FolderPaths.ChildOf("Artists", "Artists") is null && FolderPaths.ChildOf(@"Anime\X", "") == "Anime", "child of a folder");
    var tree = new FolderTree();
    tree.Add(@"Artists\A", FileKind.Image, 100, 5);
    tree.Add(@"Artists\A", FileKind.Gif, 50, 9);
    tree.Add(@"Artists\B", FileKind.Video, 1000, 3);
    tree.Add("Anime", FileKind.Image, 10, 1);
    tree.Add("", FileKind.Image, 1, 0);
    var artists = tree.Find("Artists")!;
    Check(artists.Images == 1 && artists.Gifs == 1 && artists.Videos == 1 && artists.Size == 1150 && artists.Newest == 9 && artists.Subfolders == 2, "totals include everything beneath");
    Check(tree.Root.Files == 5 && tree.Root.Subfolders == 2, "root totals");
    Check(tree.Nearest(@"Artists\Gone\Deeper").Path == "Artists" && tree.Nearest("Nope").Path == "", "nearest shown folder");
    var byFiles = FolderTree.Sort(tree.Root.Children, "Files", false);
    Check(byFiles[0].Name == "Artists" && byFiles[1].Name == "Anime", "sort by most files");
    var byName = FolderTree.Sort(tree.Root.Children, "Name", true);
    Check(byName[0].Name == "Artists", "sort by name, reversed");
    var bySize = FolderTree.Sort(artists.Children, "Size", false);
    Check(bySize[0].Name == "B", "sort by total size");
}

// ---- cover grid + files ----
{
    var restItems = Enumerable.Range(0, 30).Select(i => new ItemSpec(0.5f + i % 4 * 0.4f)).ToArray();
    var rest = MasonryLayout.Compute(restItems, 1000f, 5, 3f);
    var cg = CoverGridLayout.Compose(13, 1000f, 150f, 0.7f, 40f, 12f, rest, 24f);
    float inner = 1000f - 24f;
    int cols = (int)MathF.Round((inner + 12f) / (150f + 12f));
    float cw = (inner - 12f * (cols - 1)) / cols, pitch = cw / 0.7f + 40f + 12f;
    Check(cg.Count == 43 && cg.Columns == cols + 5, $"grid has covers then files ({cg.Count}, {cg.Columns} columns)");
    Check(Math.Abs(cg.W[0] - cw) < 0.01 && Math.Abs(cg.H[0] - cw / 0.7f) < 0.01 && Math.Abs(cg.W[12] - cw) < 0.01, "covers are all the same size");
    Check(Math.Abs(cg.X[0] - 12f) < 0.01 && Math.Abs(cg.Y[0] - 12f) < 0.01, "covers have the gap as a margin on the left and top");
    Check(Math.Abs(cg.X[cols - 1] + cw - (1000f - 12f)) < 0.05, "and on the right");
    Check(Math.Abs(cg.Y[cols] - (12f + pitch)) < 0.01, "second row sits below the first row's names");
    int rows = (13 + cols - 1) / cols;
    float gridH = 12f + rows * pitch;
    Check(Math.Abs(cg.Y[13] - (rest.Y[0] + gridH - 12f + 24f)) < 0.01 && Math.Abs(cg.X[13] - rest.X[0]) < 0.01, "files start below the covers and use the full width");
    var seen = new List<int>();
    cg.GetVisible(0, cg.TotalHeight, seen);
    Check(seen.Count == 43 && seen.Distinct().Count() == 43, "every tile can be found");
    cg.GetVisible(gridH + 30f, gridH + 60f, seen);
    Check(seen.All(i => i >= 13) && seen.Count > 0, "looking below the covers finds only files");
    var only = CoverGridLayout.Compose(4, 1000f, 150f, 0.7f, 40f, 12f, null, 24f);
    Check(only.Count == 4 && Math.Abs(only.TotalHeight - (12f + pitch)) < 0.01, "covers only, with a margin below");
}

// ---- Connected Art sets, level owners, natural order ----
Check(ConnectedArt.SetOf(@"Connected Art\Story") == (@"Connected Art\Story", "Story"), "a set folder");
Check(ConnectedArt.SetOf(@"Connected Art\NSFW\Very\Story\extra") == (@"Connected Art\NSFW\Very\Story", "Story"), "a set behind NSFW\\Very, with a subfolder");
Check(ConnectedArt.SetOf(@"Connected Art") is null && ConnectedArt.SetOf(@"Connected Art\NSFW") is null, "loose files are not a set");
Check(ConnectedArt.SetOf(@"Artists\Story") is null, "outside Connected Art: no set");
Check(LevelOwner.Of(@"Artists\A\NSFW\Sub") == @"Artists\A" && LevelOwner.Of(@"Artists\A\NSFW\Very") == @"Artists\A", "NSFW and Very follow the folder holding NSFW");
Check(LevelOwner.Of(@"Artists\A") is null && LevelOwner.Of("NSFW") == "", "General has no owner; NSFW at the top belongs to the library");
var nat = new[] { "page 10.jpg", "page 2.jpg", "Page 1.jpg", "page 02.jpg", "a.jpg" }.OrderBy(x => x, NaturalComparer.Instance).ToArray();
Check(nat.SequenceEqual(new[] { "a.jpg", "Page 1.jpg", "page 2.jpg", "page 02.jpg", "page 10.jpg" }), "natural order: " + string.Join(", ", nat));

// ---- blocks (sets) and hidden items in the layouts ----
{
    var bs = new List<ItemSpec>();
    for (int i = 0; i < 10; i++) bs.Add(new ItemSpec(1f));
    bs.Add(new ItemSpec(1.5f, Block: 3)); bs.Add(new ItemSpec(1f)); bs.Add(new ItemSpec(0.5f));     // a set of three: 3 columns wide
    bs.Add(new ItemSpec(1f)); bs.Add(new ItemSpec(1f, Hidden: true)); bs.Add(new ItemSpec(1f, Hidden: true));   // a collapsed set
    for (int i = 0; i < 10; i++) bs.Add(new ItemSpec(0.8f));
    var arr = bs.ToArray();
    var bm = MasonryLayout.Compute(arr, 1200, 6, 8);
    float colW = (1200 - 40) / 6f;
    Check(Math.Abs(bm.Y[10] - bm.Y[11]) < 0.01 && Math.Abs(bm.Y[11] - bm.Y[12]) < 0.01, "a small set sits in one row");
    float bw = bm.X[12] + bm.W[12] - bm.X[10];
    Check(Math.Abs(bw - (colW * 3 + 16)) < 0.1, $"the set spans exactly 3 columns ({bw:F1})");
    Check(Math.Abs(bm.H[10] - bm.H[12]) < 0.01 && bm.W[10] / bm.H[10] > 1.49f, "set members share a height and keep their shapes");
    Check(bm.W[14] == 0 && bm.H[14] == 0 && bm.Y[14] == bm.Y[13], "hidden members take no space and sit with their tile");
    var bv = new List<int>(); bm.GetVisible(0, bm.TotalHeight, bv);
    Check(!bv.Contains(14) && !bv.Contains(15) && bv.Count == arr.Length - 2 && bv.Distinct().Count() == bv.Count, "hidden members are never visible; the rest are found once");
    bool ov = false;
    for (int i = 0; i < arr.Length; i++) for (int j = i + 1; j < arr.Length; j++)
        if (bm.W[i] > 0 && bm.W[j] > 0 && bm.X[i] < bm.X[j] + bm.W[j] - .01f && bm.X[j] < bm.X[i] + bm.W[i] - .01f && bm.Y[i] < bm.Y[j] + bm.H[j] - .01f && bm.Y[j] < bm.Y[i] + bm.H[i] - .01f) ov = true;
    Check(!ov, "blocks don't overlap other tiles");
    foreach (var (t, b) in new[] { (bm.Y[11] + 1, bm.Y[11] + 2), (0f, 50f), (bm.TotalHeight - 10, bm.TotalHeight) })
    {
        bm.GetVisible(t, b, bv);
        var brute = Enumerable.Range(0, arr.Length).Where(i => bm.W[i] > 0 && bm.Y[i] <= b && bm.Y[i] + bm.H[i] >= t).ToList();
        Check(bv.SequenceEqual(brute), $"visible search with a block at {t:F0}");
    }
    // a big expanded set: full width, several rows, nothing past the edge
    var wideSet = new ItemSpec[40]; wideSet[0] = new ItemSpec(1f, Block: 40, Wide: true); for (int i = 1; i < 40; i++) wideSet[i] = new ItemSpec(0.7f + (i % 3) * 0.3f);
    var gm = MasonryLayout.Compute(wideSet, 1200, 6, 8);
    Check(gm.X.Zip(gm.W).All(p => p.First + p.Second <= 1200.05f) && gm.Y.Distinct().Count() > 2, "a wide set fills the width in rows");
    var jm = JustifiedLayout.Compute(arr, 1200, 200, 8);
    jm.GetVisible(0, jm.TotalHeight, bv);
    Check(jm.W[14] == 0 && !bv.Contains(14) && bv.Count == arr.Length - 2, "equal rows skip hidden members too");
    var tail = new[] { new ItemSpec(1f), new ItemSpec(1f, Hidden: true) };
    Check(Math.Abs(JustifiedLayout.Compute(tail, 1200, 200, 8).TotalHeight - 200) < 0.01, "hidden members at the end add no empty row");
}

// ---- the Grid layout ----
{
    var gs = new[] { new ItemSpec(2f), new ItemSpec(0.5f), new ItemSpec(1f), new ItemSpec(1f, Hidden: true), new ItemSpec(1f), new ItemSpec(1.5f) };
    var crop = UniformGrid.Compute(gs, 620, 3, 10, fit: false);
    Check(crop.W.Where((v, i) => !gs[i].Hidden).All(v => Math.Abs(v - 200) < 0.01) && Math.Abs(crop.TotalHeight - 410) < 0.01, "grid: square cells, two rows");
    Check(crop.X[4] == 0 && Math.Abs(crop.Y[4] - 210) < 0.01 && crop.W[3] == 0, "a hidden item takes no cell");
    var fitG = UniformGrid.Compute(gs, 620, 3, 10, fit: true);
    Check(Math.Abs(fitG.W[0] - 200) < 0.01 && Math.Abs(fitG.H[0] - 100) < 0.01 && Math.Abs(fitG.Y[0] - 50) < 0.01, "fit: a wide picture keeps its shape, centred");
    Check(Math.Abs(fitG.W[1] - 100) < 0.01 && Math.Abs(fitG.X[1] - (210 + 50)) < 0.01, "fit: a tall one too");
    var gv = new List<int>(); fitG.GetVisible(0, fitG.TotalHeight, gv);
    Check(gv.Count == 5 && !gv.Contains(3), "every grid tile is found once, hidden ones never");
}

Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
