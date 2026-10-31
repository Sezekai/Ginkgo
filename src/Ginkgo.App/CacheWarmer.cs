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
    /// <summary>Keep thumbnails in memory at all. Turning it off gives all of it back at once.</summary>
    public volatile bool Enabled = true;
    /// <summary>The most memory the thumbnails may take, in bytes (0 = no limit beyond what is free).</summary>
    public long Cap;

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
        // How much may be held. When other programs need room, the ceiling comes down to what is held after giving some back,
        // so loading does not race straight back up and get thrown out again. It only rises again once there is clear room to spare.
        long ceiling = long.MaxValue;
        long lastSqueeze = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!Enabled)
                {
                    foreach (var st in tiers) st.DropAll();
                    InMemory = Done = 0; Running = false; Note = "off";
                    while (!Enabled && !ct.IsCancellationRequested) Thread.Sleep(250);
                    Note = "";
                    continue;
                }
                long cap = Interlocked.Read(ref Cap) > 0 ? Interlocked.Read(ref Cap) : long.MaxValue;
                int pref = Math.Clamp(Preferred, 0, 2);
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
                long held = tiers.Sum(s => s.MemoryBytes);
                long sinceSqueeze = Environment.TickCount64 - lastSqueeze;
                if (ceiling != long.MaxValue && sinceSqueeze > 20_000 && AvailableBytes() > Reserve + (1L << 30))
                    ceiling = held + (256L << 20);                    // plenty of room again: allow a little more
                if (ceiling != long.MaxValue && sinceSqueeze > 120_000 && AvailableBytes() > Reserve + (2L << 30))
                    ceiling = long.MaxValue;

                if (held > cap)
                {
                    // over the cap the user chose: let go of the least-viewed pieces until under it
                    foreach (var c in all.Where(x => x.loaded).OrderBy(x => x.score))
                    {
                        if (held <= cap) break;
                        c.st.DropSegment(c.pack, c.seg); held -= c.size;
                    }
                    all = all.Select(x => (x.st, x.pack, x.seg, x.size, x.score, loaded: x.st.IsInMemory(x.pack, x.seg))).ToList();
                }
                long limit = Math.Min(ceiling, cap);

                var wanted = all.Where(x => !x.loaded).OrderByDescending(x => x.score).ToList();
                var heldList = all.Where(x => x.loaded).OrderBy(x => x.score).ToList();      // least wanted first
                int swaps = 0; bool full = false;
                foreach (var w in wanted)
                {
                    if (ct.IsCancellationRequested || !Enabled || Interlocked.Read(ref Cap) != (cap == long.MaxValue ? 0 : cap)) break;
                    bool room = AvailableBytes() - w.size >= Reserve && held + w.size <= limit;
                    if (!room)
                    {
                        // full: swap a piece nobody is looking at for one that is wanted much more (the total stays the same)
                        if (heldList.Count > 0 && w.score > heldList[0].score + 1000 && swaps < 6 && AvailableBytes() >= Reserve / 2)
                        {
                            var cold = heldList[0]; heldList.RemoveAt(0);
                            cold.st.DropSegment(cold.pack, cold.seg); held -= cold.size; swaps++;
                        }
                        else { full = true; break; }
                    }
                    Running = true;
                    if (w.st.LoadSegment(w.pack, w.seg, ct)) held += w.size;
                }
                Running = false;
                Done = tiers.Sum(s => s.MemoryBytes);
                Note = full ? (held + (64L << 20) >= cap ? "at your limit: the most-viewed thumbnails are kept" : "memory is full: the most-viewed thumbnails are kept") : "";
                InMemory = Done;
                foreach (var st in tiers) st.DecayHits();

                // watch memory for a couple of seconds; if other programs need room, give back just enough (least-viewed first)
                for (int i = 0; i < 10 && !ct.IsCancellationRequested && Enabled && Interlocked.Read(ref Cap) == (cap == long.MaxValue ? 0 : cap); i++)
                {
                    Thread.Sleep(200);
                    long avail = AvailableBytes();
                    if (avail < DropBelow)
                    {
                        long need = Reserve - avail;                   // back up to the reserve, no further
                        var heldNow = all.Where(x => x.st.IsInMemory(x.pack, x.seg)).OrderBy(x => x.score).ToList();
                        long freed = 0; int n = 0;
                        while (freed < need && n < heldNow.Count) { var c = heldNow[n++]; c.st.DropSegment(c.pack, c.seg); freed += c.size; }
                        ceiling = Math.Max(0, tiers.Sum(s => s.MemoryBytes));
                        lastSqueeze = Environment.TickCount64;
                        if (n > 0) Note = $"gave {freed / 1048576} MB back to other programs";
                    }
                    InMemory = Done = tiers.Sum(s => s.MemoryBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
