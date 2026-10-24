using Ginkgo.Core;
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

Console.WriteLine($"{pass} passed, {fail} failed");
return fail == 0 ? 0 : 1;
