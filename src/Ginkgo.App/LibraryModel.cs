using Ginkgo.Core;
using Ginkgo.Core.Layout;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>What the person has asked to see: filters and order.</summary>
public sealed class ViewQuery
{
    public bool General = true, Nsfw, Very, Images = true, Gifs = true, Videos = true;
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
    /// <summary>Videos: length in seconds (0 for pictures).</summary>
    public float[] Duration = Array.Empty<float>();
    public int[] FullPack = Array.Empty<int>(), SmallPack = Array.Empty<int>(), MicroPack = Array.Empty<int>(), LargePack = Array.Empty<int>();
    public long[] FullOff = Array.Empty<long>(), SmallOff = Array.Empty<long>(), MicroOff = Array.Empty<long>(), LargeOff = Array.Empty<long>();
    public int[] FullLen = Array.Empty<int>(), SmallLen = Array.Empty<int>(), MicroLen = Array.Empty<int>(), LargeLen = Array.Empty<int>();
    /// <summary>The folder as Browse shows it (NSFW / Very folders merged into the folder above).</summary>
    public string[] Display = Array.Empty<string>();
    // videos are not in the grid yet, but they count in Browse
    public int VideoCount;
    public string[] VDisplay = Array.Empty<string>();
    public byte[] VLevel = Array.Empty<byte>();
    public long[] VSize = Array.Empty<long>(), VFirstSeen = Array.Empty<long>();

    /// <summary>True for the Favourites section's model (only the Favourites folder); false for everything else.</summary>
    public bool Favourites;

    /// <summary>
    /// All files that have thumbnails and are not missing, in Home order (newest first seen). <paramref name="favourites"/>: only the
    /// Favourites folder; otherwise everything but it (favourites show once, in their own section).
    /// </summary>
    public static LibraryModel Load(IndexDb db, string favRoot, bool favourites)
    {
        var m = new LibraryModel { Favourites = favourites };
        using var c = db.Open();
        string where = (favourites ? " AND " : " AND NOT ") + "(" + FavouritePlaces.InsideSql + ")";
        long n;
        using (var cc = c.CreateCommand())
        {
            cc.CommandText = "SELECT COUNT(*) FROM files WHERE " + Visible.Shown + " AND kind<3" + where;
            FavouritePlaces.Bind(cc, favRoot);
            n = Convert.ToInt64(cc.ExecuteScalar());
        }
        int cap = (int)n;
        m.Id = new long[cap]; m.Path = new string[cap]; m.Folder = new string[cap]; m.Level = new byte[cap]; m.Kind = new byte[cap];
        m.W = new int[cap]; m.H = new int[cap]; m.Duration = new float[cap]; m.Size = new long[cap]; m.Mtime = new long[cap]; m.FirstSeen = new long[cap];
        m.FullPack = new int[cap]; m.SmallPack = new int[cap]; m.MicroPack = new int[cap]; m.LargePack = new int[cap];
        m.FullOff = new long[cap]; m.SmallOff = new long[cap]; m.MicroOff = new long[cap]; m.LargeOff = new long[cap];
        m.FullLen = new int[cap]; m.SmallLen = new int[cap]; m.MicroLen = new int[cap]; m.LargeLen = new int[cap];
        var folders = new Dictionary<string, string>();       // one string instance per distinct folder
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, folder, level, kind, w, h, size, mtime, first_seen, thumb_pack, thumb_off, thumb_len, " +
                          "s_pack, s_off, s_len, m_pack, m_off, m_len, l_pack, l_off, l_len, duration FROM files WHERE " + Visible.Shown +
                          " AND kind<3" + where + " ORDER BY first_seen DESC, mtime DESC, id";
        FavouritePlaces.Bind(cmd, favRoot);
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
            m.Duration[i] = (float)r.GetDouble(22);
            i++;
        }
        m.Count = i;
        var disp = new Dictionary<string, string>(StringComparer.Ordinal);
        string D(string f) { if (!disp.TryGetValue(f, out var d)) disp[f] = d = string.Intern(FolderPaths.Display(f)); return d; }
        m.Display = new string[m.Count];
        for (int k = 0; k < m.Count; k++) m.Display[k] = D(m.Folder[k]);

        var vd = new List<string>(); var vl = new List<byte>(); var vs = new List<long>(); var vf = new List<long>();
        using (var vc = c.CreateCommand())
        {
            vc.CommandText = "SELECT folder, level, size, first_seen FROM files WHERE missing_since=0 AND state NOT IN (1,3) AND kind=2" + where;     // not in the grid yet
            FavouritePlaces.Bind(vc, favRoot);
            using var vr = vc.ExecuteReader();
            while (vr.Read()) { vd.Add(D(vr.GetString(0))); vl.Add((byte)vr.GetInt32(1)); vs.Add(vr.GetInt64(2)); vf.Add(vr.GetInt64(3)); }
        }
        m.VideoCount = vd.Count; m.VDisplay = vd.ToArray(); m.VLevel = vl.ToArray(); m.VSize = vs.ToArray(); m.VFirstSeen = vf.ToArray();
        return m;
    }

    private static bool LevelShown(byte lv, ViewQuery q) => lv == (byte)ContentLevel.General ? q.General : lv == (byte)ContentLevel.Nsfw ? q.Nsfw : q.Very;

    /// <summary>Passes the content-level and type switches (not folders or search).</summary>
    public bool Shown(int i, ViewQuery q) => LevelShown(Level[i], q) && KindShown(Kind[i], q);
    private static bool KindShown(byte k, ViewQuery q) => k == (byte)MediaKind.Image ? q.Images : k == (byte)MediaKind.Gif ? q.Gifs : q.Videos;

    /// <summary>The folder tree under the current switches (images and GIFs as switched, videos always counted).</summary>
    public FolderTree Tree(ViewQuery q)
    {
        var t = new FolderTree();
        for (int i = 0; i < Count; i++)
            if (Shown(i, q)) t.Add(Display[i], Kind[i] == (byte)MediaKind.Gif ? FileKind.Gif : Kind[i] == (byte)MediaKind.Video ? FileKind.Video : FileKind.Image, Size[i], FirstSeen[i]);
        for (int v = 0; v < VideoCount; v++)
            if (q.Videos && LevelShown(VLevel[v], q)) t.Add(VDisplay[v], FileKind.Video, VSize[v], VFirstSeen[v]);
        return t;
    }

    /// <summary>Puts rows in the chosen order (the rows are in first-seen order to start with).</summary>
    public int[] SortRows(int[] order, ViewQuery q)
    {
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
        }
        return order;
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
            if (!KindShown(Kind[i], q)) continue;
            if (q.OffFolders.Contains(Folder[i])) continue;
            if (search.Length > 0 && Path[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
            keep.Add(i);
        }
        return SortRows(keep.ToArray(), q);
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
            if (i < 0) { paths[k] = ""; specs[k] = new ItemSpec(1.4f); continue; }     // a folder tile with no picture to show
            paths[k] = Path[i]; ow[k] = W[i]; ids[k] = Id[i];
            specs[k] = new ItemSpec(ThumbMaker.ClampAspect(W[i], H[i]));
            badges[k] = (byte)(Kind[i] == (byte)MediaKind.Gif ? 1 : Kind[i] == (byte)MediaKind.Video ? 2 : 0);
            if (Kind[i] == (byte)MediaKind.Video) ow[k] = Math.Min(W[i], 320);      // no sharp versions from video files (the frame comes from ffmpeg)
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
            if (!KindShown(Kind[i], q)) continue;
            st.Own++; st.Size += Size[i]; if (FirstSeen[i] > st.Newest) st.Newest = FirstSeen[i];
        }
        return stats;
    }
}
