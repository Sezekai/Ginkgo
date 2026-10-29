using System.Runtime.InteropServices;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// Keeps the thumbnail files in the app's own memory, so finding a thumbnail never waits for the hard drive (which takes ~10 ms for
/// every separate lookup, and a shuffled grid needs hundreds). Each pack file is read in one long sequential read, smallest sizes
/// first. It uses whatever memory is free, leaving a reserve for other programs; if memory gets short later, the last-loaded
/// files are let go again, and loading resumes when memory frees up.
/// </summary>
public sealed class CacheWarmer
{
    public volatile bool Running; public long Done, Total; public volatile string Note = "";
    public long InMemory;
    /// <summary>The size the grid is showing now (0 micro, 1 small, 2 full): loaded first.</summary>
    public volatile int Preferred = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus s);

    public static long AvailableBytes() { var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() }; return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : long.MaxValue; }
    public static long TotalBytes() { var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() }; return GlobalMemoryStatusEx(ref m) ? (long)m.TotalPhys : 0; }

    private const long Reserve = 512L << 20;      // keep this much free for everything else
    private const long DropBelow = 300L << 20;    // let go of thumbnails if free memory falls this low

    public void Start(ThumbStores stores, CancellationToken ct)
    {
        var t = new Thread(() => Run(stores, ct)) { IsBackground = true, Name = "thumbs-into-memory", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private void Run(ThumbStores stores, CancellationToken ct)
    {
        var tiers = new[] { stores.Micro, stores.Small, stores.Full };
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int pref = Math.Clamp(Preferred, 0, 2);
                // every piece, most wanted first: pieces holding what was looked up lately, the size on screen first, then the rest in order
                var all = new List<(ThumbStore st, int pack, int seg, int size, double score, bool loaded)>();
                for (int t = 0; t < 3; t++)
                {
                    var st = tiers[t];
                    int order = t == pref ? 0 : 1 + Math.Abs(t - pref);
                    var segs = st.Segments();
                    for (int k = 0; k < segs.Count; k++)
                    {
                        var (pack, seg, size) = segs[k];
                        double score = st.Hits(pack, seg) * (t == pref ? 4.0 : 1.0) * 1000 - order * 10 - k * 0.0001;
                        all.Add((st, pack, seg, size, score, st.IsInMemory(pack, seg)));
                    }
                }
                Total = all.Sum(x => (long)x.size);
                Done = all.Where(x => x.loaded).Sum(x => (long)x.size);
                var wanted = all.Where(x => !x.loaded).OrderByDescending(x => x.score).ToList();
                var held = all.Where(x => x.loaded).OrderBy(x => x.score).ToList();      // least wanted first
                int swaps = 0; bool full = false;
                foreach (var w in wanted)
                {
                    if (ct.IsCancellationRequested) break;
                    if (AvailableBytes() - w.size < Reserve)
                    {
                        // memory is full: swap out a piece nobody is looking at for one that is wanted much more
                        if (held.Count > 0 && w.score > held[0].score + 1000 && swaps < 6)
                        {
                            var cold = held[0]; held.RemoveAt(0);
                            cold.st.DropSegment(cold.pack, cold.seg); Done -= cold.size; swaps++;
                            GC.Collect(2, GCCollectionMode.Forced, true, true);
                            if (AvailableBytes() - w.size < Reserve) { full = true; break; }
                        }
                        else { full = true; break; }
                    }
                    Running = true;
                    if (w.st.LoadSegment(w.pack, w.seg, ct)) Done += w.size;
                }
                Running = false;
                Note = full ? "memory is full: the most-viewed thumbnails are kept" : "";
                InMemory = tiers.Sum(s => s.MemoryBytes);
                foreach (var st in tiers) st.DecayHits();
                // keep an eye on memory for a couple of seconds: give room back if other programs need it
                for (int i = 0; i < 10 && !ct.IsCancellationRequested; i++)
                {
                    Thread.Sleep(200);
                    if (AvailableBytes() < DropBelow)
                    {
                        var heldNow = all.Where(x => x.st.IsInMemory(x.pack, x.seg)).OrderBy(x => x.score).ToList();
                        int n = 0;
                        while (AvailableBytes() < DropBelow + (256L << 20) && n < heldNow.Count) { var c = heldNow[n++]; c.st.DropSegment(c.pack, c.seg); }
                        if (n > 0) { GC.Collect(2, GCCollectionMode.Forced, true, true); Note = "gave memory back to other programs"; }
                    }
                    InMemory = tiers.Sum(s => s.MemoryBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
