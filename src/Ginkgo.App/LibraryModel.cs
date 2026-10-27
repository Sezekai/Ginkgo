using Ginkgo.Core;
using Ginkgo.Core.Layout;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>What the person has asked to see: filters and order.</summary>
public sealed class ViewQuery
{
    public bool General = true, Nsfw, Very, Images = true, Gifs = true;
    public HashSet<string> OffFolders = new(StringComparer.OrdinalIgnoreCase);
    public string Search = "";
    public string Sort = "FirstSeen";
    public int RandomSeed = 1;
}

/// <summary>The index held in memory as plain columns (fast to filter and sort), loaded once and refreshed when something changes.</summary>
public sealed class LibraryModel
{
    public int Count;
    public long[] Id = Array.Empty<long>();
    public string[] Path = Array.Empty<string>(), Folder = Array.Empty<string>();
    public byte[] Level = Array.Empty<byte>(), Kind = Array.Empty<byte>();
    public int[] W = Array.Empty<int>(), H = Array.Empty<int>();
    public long[] Size = Array.Empty<long>(), Mtime = Array.Empty<long>(), FirstSeen = Array.Empty<long>();
    public int[] FullPack = Array.Empty<int>(), SmallPack = Array.Empty<int>(), MicroPack = Array.Empty<int>(), LargePack = Array.Empty<int>();
    public long[] FullOff = Array.Empty<long>(), SmallOff = Array.Empty<long>(), MicroOff = Array.Empty<long>(), LargeOff = Array.Empty<long>();
    public int[] FullLen = Array.Empty<int>(), SmallLen = Array.Empty<int>(), MicroLen = Array.Empty<int>(), LargeLen = Array.Empty<int>();

    /// <summary>All files that have thumbnails and are not missing, in Home order (newest first seen).</summary>
    public static LibraryModel Load(IndexDb db)
    {
        var m = new LibraryModel();
        using var c = db.Open();
        long n = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE " + Visible.Shown + " AND kind<2");
        int cap = (int)n;
        m.Id = new long[cap]; m.Path = new string[cap]; m.Folder = new string[cap]; m.Level = new byte[cap]; m.Kind = new byte[cap];
        m.W = new int[cap]; m.H = new int[cap]; m.Size = new long[cap]; m.Mtime = new long[cap]; m.FirstSeen = new long[cap];
        m.FullPack = new int[cap]; m.SmallPack = new int[cap]; m.MicroPack = new int[cap]; m.LargePack = new int[cap];
        m.FullOff = new long[cap]; m.SmallOff = new long[cap]; m.MicroOff = new long[cap]; m.LargeOff = new long[cap];
        m.FullLen = new int[cap]; m.SmallLen = new int[cap]; m.MicroLen = new int[cap]; m.LargeLen = new int[cap];
        var folders = new Dictionary<string, string>();       // one string instance per distinct folder
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, folder, level, kind, w, h, size, mtime, first_seen, thumb_pack, thumb_off, thumb_len, " +
                          "s_pack, s_off, s_len, m_pack, m_off, m_len, l_pack, l_off, l_len FROM files WHERE " + Visible.Shown +
                          " AND kind<2 ORDER BY first_seen DESC, mtime DESC, id";
        using var r = cmd.ExecuteReader();
        int i = 0;
        while (r.Read() && i < cap)
        {
            m.Id[i] = r.GetInt64(0); m.Path[i] = r.GetString(1);
            string f = r.GetString(2);
            if (!folders.TryGetValue(f, out var fs)) folders[f] = fs = f;
            m.Folder[i] = fs; m.Level[i] = (byte)r.GetInt32(3); m.Kind[i] = (byte)r.GetInt32(4);
            m.W[i] = r.GetInt32(5); m.H[i] = r.GetInt32(6); m.Size[i] = r.GetInt64(7); m.Mtime[i] = r.GetInt64(8); m.FirstSeen[i] = r.GetInt64(9);
            m.FullPack[i] = r.GetInt32(10); m.FullOff[i] = r.GetInt64(11); m.FullLen[i] = r.GetInt32(12);
            m.SmallPack[i] = r.GetInt32(13); m.SmallOff[i] = r.GetInt64(14); m.SmallLen[i] = r.GetInt32(15);
            m.MicroPack[i] = r.GetInt32(16); m.MicroOff[i] = r.GetInt64(17); m.MicroLen[i] = r.GetInt32(18);
            m.LargePack[i] = r.GetInt32(19); m.LargeOff[i] = r.GetInt64(20); m.LargeLen[i] = r.GetInt32(21);
            i++;
        }
        m.Count = i;
        return m;
    }

    /// <summary>The rows to show, in order.</summary>
    public int[] Select(ViewQuery q)
    {
        var keep = new List<int>(Count);
        string search = q.Search.Trim();
        for (int i = 0; i < Count; i++)
        {
            byte lv = Level[i];
            if (lv == (byte)ContentLevel.General ? !q.General : lv == (byte)ContentLevel.Nsfw ? !q.Nsfw : !q.Very) continue;
            if (Kind[i] == (byte)MediaKind.Image ? !q.Images : !q.Gifs) continue;
            if (q.OffFolders.Contains(Folder[i])) continue;
            if (search.Length > 0 && Path[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            keep.Add(i);
        }
        var order = keep.ToArray();
        switch (q.Sort)
        {
            case "Modified": Array.Sort(order, (a, b) => Mtime[b].CompareTo(Mtime[a])); break;
            case "Name": Array.Sort(order, (a, b) => string.Compare(System.IO.Path.GetFileName(Path[a]), System.IO.Path.GetFileName(Path[b]), StringComparison.OrdinalIgnoreCase)); break;
            case "SizeDesc": Array.Sort(order, (a, b) => Size[b].CompareTo(Size[a])); break;
            case "SizeAsc": Array.Sort(order, (a, b) => Size[a].CompareTo(Size[b])); break;
            case "Largest": Array.Sort(order, (a, b) => ((long)W[b] * H[b]).CompareTo((long)W[a] * H[a])); break;
            case "Random":
                var rnd = new Random(q.RandomSeed);
                for (int k = order.Length - 1; k > 0; k--) { int j = rnd.Next(k + 1); (order[k], order[j]) = (order[j], order[k]); }
                break;
            // "FirstSeen": the model is already in that order (stable)
        }
        return order;
    }

    /// <summary>The columns the thumbnail loader needs, for the chosen rows in the chosen order.</summary>
    public (ThumbLoader.TierData[] tiers, string[] paths, int[] origW, long[] ids, ItemSpec[] specs, byte[] badges) Slice(int[] order, ThumbStores stores)
    {
        int n = order.Length;
        var paths = new string[n]; var ow = new int[n]; var ids = new long[n]; var specs = new ItemSpec[n]; var badges = new byte[n];
        var mp = new int[n]; var mo = new long[n]; var ml = new int[n];
        var sp = new int[n]; var so = new long[n]; var sl = new int[n];
        var fp = new int[n]; var fo = new long[n]; var fl = new int[n];
        var lp = new int[n]; var lo = new long[n]; var ll = new int[n];
        for (int k = 0; k < n; k++)
        {
            int i = order[k];
            paths[k] = Path[i]; ow[k] = W[i]; ids[k] = Id[i];
            specs[k] = new ItemSpec(ThumbMaker.ClampAspect(W[i], H[i]));
            badges[k] = (byte)(Kind[i] == (byte)MediaKind.Gif ? 1 : 0);
            mp[k] = MicroPack[i]; mo[k] = MicroOff[i]; ml[k] = MicroLen[i];
            sp[k] = SmallPack[i]; so[k] = SmallOff[i]; sl[k] = SmallLen[i];
            fp[k] = FullPack[i]; fo[k] = FullOff[i]; fl[k] = FullLen[i];
            lp[k] = LargePack[i]; lo[k] = LargeOff[i]; ll[k] = LargeLen[i];
        }
        var tiers = new[]
        {
            new ThumbLoader.TierData(stores.Micro, mp, mo, ml), new ThumbLoader.TierData(stores.Small, sp, so, sl),
            new ThumbLoader.TierData(stores.Full, fp, fo, fl), new ThumbLoader.TierData(stores.Large, lp, lo, ll),
        };
        return (tiers, paths, ow, ids, specs, badges);
    }

    public sealed class FolderStat { public int Own; public long Size; public long Newest; }

    /// <summary>Every folder that has files (and its parents), with how much of its OWN content passes the level and type switches.</summary>
    public Dictionary<string, FolderStat> FolderStats(ViewQuery q)
    {
        var stats = new Dictionary<string, FolderStat>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Count; i++)
        {
            string f = Folder[i];
            if (!stats.TryGetValue(f, out var st))
            {
                stats[f] = st = new FolderStat();
                string p = f;                                         // make sure every parent exists too
                while (p.Length > 0)
                {
                    int cut = p.LastIndexOfAny(new[] { '\\', '/' });
                    p = cut < 0 ? "" : p.Substring(0, cut);
                    if (p.Length == 0 || stats.ContainsKey(p)) break;
                    stats[p] = new FolderStat();
                }
            }
            byte lv = Level[i];
            if (lv == (byte)ContentLevel.General ? !q.General : lv == (byte)ContentLevel.Nsfw ? !q.Nsfw : !q.Very) continue;
            if (Kind[i] == (byte)MediaKind.Image ? !q.Images : !q.Gifs) continue;
            st.Own++; st.Size += Size[i]; if (FirstSeen[i] > st.Newest) st.Newest = FirstSeen[i];
        }
        return stats;
    }
}
