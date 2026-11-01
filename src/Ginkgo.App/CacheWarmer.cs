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

    /// <summary>The graphics card's memory, used when memory (RAM) runs short (null: not available).</summary>
    public volatile VramTier? Vram;
    /// <summary>"Off", "Overflow" (memory first, then the graphics card) or "GpuOnly" (the graphics card only).</summary>
    public volatile string VramMode = "Overflow";
    /// <summary>Use the graphics card's memory on battery too (it keeps the card awake).</summary>
    public volatile bool VramOnBattery;
    public long InVram;
    public volatile bool VramActive;
    private long _cardOverSince;
    /// <summary>Why the graphics card's memory isn't available (shown in Optimisations).</summary>
    public volatile string VramStatus = "";
    /// <summary>Times the graphics-memory store had to be made again (the card was reset).</summary>
    public int VramRemade;

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
                var vram = Vram;
                bool tierOn = Enabled && vram is not null && vram.Alive && VramMode != "Off" && (VramOnBattery || !VramTier.OnBattery());
                foreach (var st in tiers) st.Second = tierOn ? vram : null;
                VramActive = tierOn;
                if (!tierOn && vram is not null && vram.Bytes > 0) vram.Clear();
                InVram = vram?.Bytes ?? 0;
                if (!Enabled)
                {
                    foreach (var st in tiers) st.DropAll();
                    InMemory = Done = 0; Running = false; Note = "off";
                    while (!Enabled && !ct.IsCancellationRequested) Thread.Sleep(250);
                    Note = "";
                    continue;
                }
                bool ramOn = !(tierOn && VramMode == "GpuOnly");
                long capSetting = Interlocked.Read(ref Cap); string modeSetting = VramMode; bool batterySetting = VramOnBattery;
                bool Same() => Interlocked.Read(ref Cap) == capSetting && VramMode == modeSetting && VramOnBattery == batterySetting && Vram == vram && Enabled;
                long cap = Interlocked.Read(ref Cap) > 0 ? Interlocked.Read(ref Cap) : long.MaxValue;
                if (!ramOn) cap = 0;
                int pref = Math.Clamp(Preferred, 0, 2);
                var all = new List<(ThumbStore st, int pack, int seg, int size, double score, bool loaded, bool onCard)>();
                for (int t = 0; t < 3; t++)
                {
                    var st = tiers[t];
                    int order = t == pref ? 0 : 1 + Math.Abs(t - pref);
                    var segs = st.Segments();
                    for (int k = 0; k < segs.Count; k++)
                    {
                        var (pack, seg, size) = segs[k];
                        double score = st.Hits(pack, seg) * (t == pref ? 4.0 : 1.0) * 1000 - order * 10 - k * 0.0001;
                        all.Add((st, pack, seg, size, score, st.IsInMemory(pack, seg), tierOn && st.InSecond(pack, seg)));
                    }
                }
                Total = all.Sum(x => (long)x.size);
                long held = tiers.Sum(s => s.MemoryBytes);
                long sinceSqueeze = Environment.TickCount64 - lastSqueeze;
                if (ceiling != long.MaxValue && sinceSqueeze > 20_000 && AvailableBytes() > Reserve + (1L << 30))
                    ceiling = held + (256L << 20);                    // plenty of room again: allow a little more
                if (ceiling != long.MaxValue && sinceSqueeze > 120_000 && AvailableBytes() > Reserve + (2L << 30))
                    ceiling = long.MaxValue;

                // letting go of a piece in memory: to the graphics card if it's on, else back to the drive
                void LetGo((ThumbStore st, int pack, int seg, int size, double score, bool loaded, bool onCard) c)
                {
                    if (!(tierOn && c.st.MoveToSecond(c.pack, c.seg))) c.st.DropSegment(c.pack, c.seg);
                }

                if (held > cap)
                {
                    // over the limit (or memory is switched off): let go of the least-viewed pieces until under it
                    foreach (var c in all.Where(x => x.loaded).OrderBy(x => x.score))
                    {
                        if (held <= cap) break;
                        LetGo(c); held -= c.size;
                    }
                    all = all.Select(x => (x.st, x.pack, x.seg, x.size, x.score, loaded: x.st.IsInMemory(x.pack, x.seg), onCard: tierOn && x.st.InSecond(x.pack, x.seg))).ToList();
                }
                long limit = Math.Min(ceiling, cap);

                var wanted = all.Where(x => !x.loaded).OrderByDescending(x => x.score).ToList();
                var heldList = all.Where(x => x.loaded).OrderBy(x => x.score).ToList();      // least wanted first
                int swaps = 0; bool full = false;
                var notPlaced = new List<(ThumbStore st, int pack, int seg, int size, double score, bool loaded, bool onCard)>();
                foreach (var w in wanted)
                {
                    if (ct.IsCancellationRequested || !Same()) break;
                    if (full || !ramOn) { notPlaced.Add(w); continue; }
                    // already on the card: only bring it back into memory when there's plenty to spare, or pieces would go back
                    // and forth (memory frees up because pieces moved to the card, so they'd be loaded again, and so on)
                    if (w.onCard && AvailableBytes() < Reserve + (2L << 30)) continue;
                    bool room = AvailableBytes() - w.size >= Reserve && held + w.size <= limit;
                    if (!room)
                    {
                        // full: swap a piece nobody is looking at for one that is wanted much more (the total stays the same)
                        if (heldList.Count > 0 && w.score > heldList[0].score + 1000 && swaps < 6 && AvailableBytes() >= Reserve / 2)
                        {
                            var cold = heldList[0]; heldList.RemoveAt(0);
                            LetGo(cold); held -= cold.size; swaps++;
                        }
                        else { full = true; notPlaced.Add(w); continue; }
                    }
                    Running = true;
                    if (w.st.LoadSegment(w.pack, w.seg, ct))
                    {
                        held += w.size;           // the card's copy stays: if memory runs short again, nothing has to be read twice
                    }
                }

                // what didn't fit in memory goes to the graphics card (most wanted first), swapping out colder pieces held there
                if (tierOn && vram is not null)
                {
                    var onCard = all.Where(x => x.onCard && !x.st.IsInMemory(x.pack, x.seg)).OrderBy(x => x.score).ToList();
                    // Windows' allowance shrank (a game started, say): give back the least-viewed pieces
                    // (only when it's clearly over for a few seconds: Windows' figure can dip for a moment)
                    long over = -vram.Room();
                    if (over > (128L << 20)) { if (_cardOverSince == 0) _cardOverSince = Environment.TickCount64; }
                    else _cardOverSince = 0;
                    if (_cardOverSince != 0 && Environment.TickCount64 - _cardOverSince > 3000)
                    {
                        long freed = 0;
                        while (freed < over && onCard.Count > 0) { var c = onCard[0]; onCard.RemoveAt(0); c.st.DropSecond(c.pack, c.seg); freed += c.size; }
                        _cardOverSince = 0;
                        Note = $"gave {freed / 1048576} MB of the card back";
                    }
                    int cardSwaps = 0;
                    foreach (var w in notPlaced)
                    {
                        if (ct.IsCancellationRequested || !Same()) break;
                        if (w.onCard) continue;
                        if (vram.Room() < w.size)
                        {
                            if (onCard.Count > 0 && w.score > onCard[0].score + 1000 && cardSwaps < 6) { var c = onCard[0]; onCard.RemoveAt(0); c.st.DropSecond(c.pack, c.seg); cardSwaps++; }
                            else break;
                            if (vram.Room() < w.size) break;
                        }
                        Running = true;
                        w.st.LoadToSecond(w.pack, w.seg, ct);
                    }
                }
                Running = false;
                InVram = vram?.Bytes ?? 0;
                // a piece can be in memory and on the card at once: count it once
                Done = all.Where(x => x.st.IsInMemory(x.pack, x.seg) || (tierOn && x.st.InSecond(x.pack, x.seg))).Sum(x => (long)x.size);
                Note = full ? (held + (64L << 20) >= cap ? "at your limit: the most-viewed thumbnails are kept" : "memory is full: the most-viewed thumbnails are kept") : "";
                if (tierOn && full) Note = InVram > 0 ? "the rest are on the graphics card" : Note;
                InMemory = tiers.Sum(s => s.MemoryBytes);
                foreach (var st in tiers) st.DecayHits();

                // watch memory for a couple of seconds; if other programs need room, give back just enough (least-viewed first)
                for (int i = 0; i < 10 && !ct.IsCancellationRequested && Same(); i++)
                {
                    Thread.Sleep(200);
                    long avail = AvailableBytes();
                    if (avail < DropBelow)
                    {
                        long need = Reserve - avail;                   // back up to the reserve, no further
                        var heldNow = all.Where(x => x.st.IsInMemory(x.pack, x.seg)).OrderBy(x => x.score).ToList();
                        long freed = 0; int n = 0;
                        while (freed < need && n < heldNow.Count) { var c = heldNow[n++]; LetGo(c); freed += c.size; }
                        ceiling = Math.Max(0, tiers.Sum(s => s.MemoryBytes));
                        lastSqueeze = Environment.TickCount64;
                        if (n > 0) Note = tierOn ? $"moved {freed / 1048576} MB to the graphics card for other programs" : $"gave {freed / 1048576} MB back to other programs";
                    }
                    InMemory = tiers.Sum(s => s.MemoryBytes);
                    InVram = vram?.Bytes ?? 0;
                    Done = Math.Min(Total, Math.Max(Done, InMemory));
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
