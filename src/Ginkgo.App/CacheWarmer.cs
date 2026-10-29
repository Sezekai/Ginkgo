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

    private const long Reserve = 1024L << 20;      // keep this much free for everything else
    private const long DropBelow = 500L << 20;     // let go of thumbnails if free memory falls this low

    public void Start(ThumbStores stores, CancellationToken ct)
    {
        var t = new Thread(() => Run(stores, ct)) { IsBackground = true, Name = "thumbs-into-memory", Priority = ThreadPriority.BelowNormal };
        t.Start();
    }

    private void Run(ThumbStores stores, CancellationToken ct)
    {
        var order = new[] { stores.Micro, stores.Small, stores.Full };       // smallest (cheapest, used for small tiles) first
        var loaded = new List<(ThumbStore store, int pack)>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int pref = Math.Clamp(Preferred, 0, 2);
                var ranked = new[] { order[pref] }.Concat(order.Where((_, i) => i != pref)).ToArray();
                var todo = ranked.SelectMany(st => st.Packs().Select(p => (st, p.pack, p.size))).ToList();
                Total = todo.Sum(x => x.size);
                Done = todo.Where(x => x.st.IsInMemory(x.pack)).Sum(x => x.size);
                bool stopped = false;
                foreach (var (st, pack, size) in todo)
                {
                    if (st.IsInMemory(pack)) continue;
                    if (AvailableBytes() - size < Reserve) { Note = "paused: memory is full (resumes when some is free)"; stopped = true; break; }
                    Running = true; Note = "";
                    if (st.LoadPack(pack, n => Done += n, ct)) { loaded.Add((st, pack)); }
                }
                Running = false;
                if (!stopped && Done >= Total) Note = "";
                InMemory = order.Sum(s => s.MemoryBytes);
                // wait, keeping an eye on memory: let go of the last-loaded files if other programs need the room
                for (int i = 0; i < 25 && !ct.IsCancellationRequested; i++)
                {
                    Thread.Sleep(200);
                    while (AvailableBytes() < DropBelow && loaded.Count > 0)
                    {
                        var (st, pack) = loaded[^1]; loaded.RemoveAt(loaded.Count - 1);
                        st.DropPack(pack);
                        GC.Collect(2, GCCollectionMode.Optimized, false);
                        Note = "gave memory back to other programs";
                    }
                    InMemory = order.Sum(s => s.MemoryBytes);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
