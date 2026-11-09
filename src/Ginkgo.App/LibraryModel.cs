using Ginkgo.Core;
using Ginkgo.Core.Layout;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>What the person has asked to see: filters and order.</summary>
public sealed class ViewQuery
{
    public bool General = true, Nsfw, Very, Images = true, Gifs = true, Videos = true;
    /// <summary>Folders switched off in Select folders (as Browse shows them: NSFW and Very folders merged into the folder above).</summary>
    public HashSet<string> OffFolders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Folders whose NSFW / Very switch is off (the folder holding the NSFW folder).</summary>
    public HashSet<string> NsfwOff = new(StringComparer.OrdinalIgnoreCase), VeryOff = new(StringComparer.OrdinalIgnoreCase);
    public string Search = "";
    public string Sort = "FirstSeen";
    public bool Reverse;
    public int RandomSeed = 1;
    public FilterSet F = new();
    /// <summary>Whether a file id is a favourite (for the Favourites filter).</summary>
    public Func<long, bool>? IsFavourite;
    /// <summary>Sets: how many images a set may have to show as a block (more: a stacked tile), which large sets are open, and
    /// whether sets sort by their first image for every sort.</summary>
    public int SetLimit = 5;
    public HashSet<string> OpenSets = new(StringComparer.OrdinalIgnoreCase);
    public bool SetsByFirst;
    /// <summary>Of several copies of the same file, prefer the one in this folder (Browse); otherwise the first by folder name.</summary>
    public string PreferFolder = "";
    /// <summary>Show each file once when the same file is in several folders.</summary>
    public bool HideCopies = true;
    /// <summary>One more test a row must pass (the Videos filter: watched, paused, not watched).</summary>
    public Func<int, bool>? RowFilter;
}

/// <summary>The filters beyond the switches in the bar (each 0 / empty = not used).</summary>
public sealed class FilterSet
{
    public HashSet<string> Formats { get; set; } = new(StringComparer.OrdinalIgnoreCase);     // "jpg", "png" ...
    public long MinSize { get; set; } public long MaxSize { get; set; }                                    // bytes
    public int MinW { get; set; } public int MaxW { get; set; } public int MinH { get; set; } public int MaxH { get; set; }
    public double MinMP { get; set; } public double MaxMP { get; set; }
    /// <summary>"", "Portrait", "Landscape", "Square" or "Ratio" (between <see cref="MinRatio"/> and <see cref="MaxRatio"/>, width / height).</summary>
    public string Orientation { get; set; } = "";
    public double MinRatio { get; set; } public double MaxRatio { get; set; }
    public double MinDur { get; set; } public double MaxDur { get; set; }                                    // seconds, videos only
    /// <summary>"Modified", "Created", "Taken" or "FirstSeen"; then either the last N days or a range (unix seconds).</summary>
    public string DateField { get; set; } = "Modified";
    public int LastDays { get; set; }
    public long DateFrom { get; set; } public long DateTo { get; set; }
    /// <summary>0 = all, 1 = favourites only, 2 = hide favourites. The same for sets.</summary>
    public int Favs { get; set; } public int Sets { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool Any => Formats.Count > 0 || MinSize > 0 || MaxSize > 0 || MinW > 0 || MaxW > 0 || MinH > 0 || MaxH > 0 || MinMP > 0 || MaxMP > 0 ||
        Orientation.Length > 0 || MinDur > 0 || MaxDur > 0 || LastDays > 0 || DateFrom > 0 || DateTo > 0 || Favs != 0 || Sets != 0;
    public FilterSet Copy() { var c = (FilterSet)MemberwiseClone(); c.Formats = new HashSet<string>(Formats, StringComparer.OrdinalIgnoreCase); return c; }
}


/// <summary>The rows to show, in order, with the sets among them and the other places of files shown once.</summary>
public sealed class Arranged
{
    public int[] Order = Array.Empty<int>();
    public List<SetBlock> Sets = new();
    /// <summary>Rows hidden because they are copies of a row shown (shown row -> the copies' rows).</summary>
    public Dictionary<int, List<int>> Copies = new();
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
    /// <summary>Created date, the date a photo was taken (0 or -1: none, so the modified date is used), and the content hash (0: not known).</summary>
    public long[] Ctime = Array.Empty<long>(), Taken = Array.Empty<long>(), FHash = Array.Empty<long>();
    /// <summary>The folder whose NSFW / Very switch a file follows (null for General files).</summary>
    public string?[] Owner = Array.Empty<string?>();
    /// <summary>File extension without the dot, lower case (one string per extension).</summary>
    public string[] Ext = Array.Empty<string>();
    /// <summary>The Connected Art set each row belongs to (-1: none), and the sets with their rows in file-name order.</summary>
    public int[] SetOf = Array.Empty<int>();
    public List<(string Key, string Name, int[] Rows)> Sets = new();
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
        m.Ctime = new long[cap]; m.Taken = new long[cap]; m.FHash = new long[cap];
        m.FullPack = new int[cap]; m.SmallPack = new int[cap]; m.MicroPack = new int[cap]; m.LargePack = new int[cap];
        m.FullOff = new long[cap]; m.SmallOff = new long[cap]; m.MicroOff = new long[cap]; m.LargeOff = new long[cap];
        m.FullLen = new int[cap]; m.SmallLen = new int[cap]; m.MicroLen = new int[cap]; m.LargeLen = new int[cap];
        var folders = new Dictionary<string, string>();       // one string instance per distinct folder
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, path, folder, level, kind, w, h, size, mtime, first_seen, thumb_pack, thumb_off, thumb_len, " +
                          "s_pack, s_off, s_len, m_pack, m_off, m_len, l_pack, l_off, l_len, duration, ctime, taken, fhash FROM files WHERE " + Visible.Shown +
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
            m.Ctime[i] = r.GetInt64(23); m.Taken[i] = r.GetInt64(24); m.FHash[i] = r.GetInt64(25);
            i++;
        }
        m.Count = i;
        var disp = new Dictionary<string, string>(StringComparer.Ordinal);
        string D(string f) { if (!disp.TryGetValue(f, out var d)) disp[f] = d = string.Intern(FolderPaths.Display(f)); return d; }
        m.Display = new string[m.Count];
        for (int k = 0; k < m.Count; k++) m.Display[k] = D(m.Folder[k]);
        var owners = new Dictionary<string, string?>(StringComparer.Ordinal);
        var exts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        m.Owner = new string?[m.Count]; m.Ext = new string[m.Count]; m.SetOf = new int[m.Count];
        var setIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var setRows = new List<List<int>>();
        for (int k = 0; k < m.Count; k++)
        {
            string f = m.Folder[k];
            if (m.Level[k] != (byte)ContentLevel.General)
            {
                if (!owners.TryGetValue(f, out var o)) { o = LevelOwner.Of(f); if (o is not null) o = string.Intern(o); owners[f] = o; }
                m.Owner[k] = o;
            }
            string e = System.IO.Path.GetExtension(m.Path[k]).TrimStart('.');
            if (!exts.TryGetValue(e, out var es)) exts[e] = es = e.ToLowerInvariant();
            m.Ext[k] = es;
            m.SetOf[k] = -1;
            if (f.StartsWith(ConnectedArt.FolderName, StringComparison.OrdinalIgnoreCase) && ConnectedArt.SetOf(f) is { } set)
            {
                if (!setIndex.TryGetValue(set.key, out int si)) { si = setRows.Count; setIndex[set.key] = si; setRows.Add(new List<int>()); m.Sets.Add((set.key, set.name, Array.Empty<int>())); }
                m.SetOf[k] = si; setRows[si].Add(k);
            }
        }
        for (int si = 0; si < setRows.Count; si++)
        {
            // inside a set: natural file-name order (the part of the path below the set's folder)
            string key = m.Sets[si].Key;
            var rows = setRows[si];
            rows.Sort((a, b) =>
            {
                int c = NaturalComparer.Instance.Compare(RelTo(m.Path[a], key), RelTo(m.Path[b], key));
                return c != 0 ? c : a.CompareTo(b);
            });
            m.Sets[si] = (key, m.Sets[si].Name, rows.ToArray());
        }

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

    private static string RelTo(string path, string setKey)
    {
        int at = path.IndexOf(setKey, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? System.IO.Path.GetFileName(path) : path[(at + setKey.Length)..].TrimStart('\\', '/');
    }

    private static bool LevelShown(byte lv, ViewQuery q) => lv == (byte)ContentLevel.General ? q.General : lv == (byte)ContentLevel.Nsfw ? q.Nsfw : q.Very;

    /// <summary>The level switches: global, then the switch of the folder holding the NSFW folder.</summary>
    private bool LevelOk(int i, ViewQuery q)
    {
        byte lv = Level[i];
        if (lv == (byte)ContentLevel.General) return q.General;
        if (lv == (byte)ContentLevel.Nsfw) return q.Nsfw && (Owner[i] is not { } o || !q.NsfwOff.Contains(o));
        return q.Very && (Owner[i] is not { } ov || !q.VeryOff.Contains(ov));
    }

    /// <summary>Passes the content-level and type switches (not folders, search or the other filters).</summary>
    public bool Shown(int i, ViewQuery q) => LevelOk(i, q) && KindShown(Kind[i], q);

    /// <summary>The date a date filter or sort uses.</summary>
    public long DateOf(int i, string field) => field switch
    {
        "Created" => Ctime[i] > 0 ? Ctime[i] : Mtime[i],
        "Taken" => Taken[i] > 0 ? Taken[i] : Mtime[i],
        "FirstSeen" => FirstSeen[i],
        _ => Mtime[i],
    };

    /// <summary>Passes every filter except folders, search, sets and favourites (those need more than the row).</summary>
    public bool PassesFilters(int i, FilterSet f)
    {
        if (f.Formats.Count > 0 && !f.Formats.Contains(Ext[i])) return false;
        if (f.MinSize > 0 && Size[i] < f.MinSize) return false;
        if (f.MaxSize > 0 && Size[i] > f.MaxSize) return false;
        if (f.MinW > 0 && W[i] < f.MinW) return false;
        if (f.MaxW > 0 && W[i] > f.MaxW) return false;
        if (f.MinH > 0 && H[i] < f.MinH) return false;
        if (f.MaxH > 0 && H[i] > f.MaxH) return false;
        double mp = (double)W[i] * H[i] / 1e6;
        if (f.MinMP > 0 && mp < f.MinMP) return false;
        if (f.MaxMP > 0 && mp > f.MaxMP) return false;
        if (f.Orientation.Length > 0 && W[i] > 0 && H[i] > 0)
        {
            double r = (double)W[i] / H[i];
            bool ok = f.Orientation switch
            {
                "Portrait" => r < 0.95, "Landscape" => r > 1.05, "Square" => r >= 0.95 && r <= 1.05,
                "Ratio" => (f.MinRatio <= 0 || r >= f.MinRatio) && (f.MaxRatio <= 0 || r <= f.MaxRatio),
                _ => true,
            };
            if (!ok) return false;
        }
        if (f.MinDur > 0 || f.MaxDur > 0)
        {
            if (Kind[i] != (byte)MediaKind.Video) return false;
            if (f.MinDur > 0 && Duration[i] < f.MinDur) return false;
            if (f.MaxDur > 0 && Duration[i] > f.MaxDur) return false;
        }
        if (f.LastDays > 0 || f.DateFrom > 0 || f.DateTo > 0)
        {
            long d = DateOf(i, f.DateField);
            if (f.LastDays > 0 && d < DateTimeOffset.UtcNow.ToUnixTimeSeconds() - f.LastDays * 86400L) return false;
            if (f.LastDays == 0 && f.DateFrom > 0 && d < f.DateFrom) return false;
            if (f.LastDays == 0 && f.DateTo > 0 && d > f.DateTo) return false;
        }
        return true;
    }

    private bool PassesAll(int i, ViewQuery q, string search)
    {
        if (!Shown(i, q)) return false;
        if (!PassesFilters(i, q.F)) return false;
        if (q.F.Favs != 0 && q.IsFavourite is { } fav && fav(Id[i]) != (q.F.Favs == 1)) return false;
        if (search.Length > 0 && Path[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (q.RowFilter is { } rf && !rf(i)) return false;
        return true;
    }

    /// <summary>The formats in this part of the library, most common first (for the Format filter).</summary>
    public List<(string ext, int count)> Formats()
    {
        var d = new Dictionary<string, int>();
        for (int i = 0; i < Count; i++) d[Ext[i]] = d.GetValueOrDefault(Ext[i]) + 1;
        return d.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
    }
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

    /// <summary>Whether a sort runs largest / newest / longest first before Reverse (names and folders go A to Z).</summary>
    public static bool Descending(string sort) => sort is not ("Name" or "Folder");

    /// <summary>
    /// Arranges rows that passed the filters into what the view shows: each file once (the copy in <see cref="ViewQuery.PreferFolder"/>,
    /// else the first by folder name), Connected Art sets kept together (a set shows whole when any of its images passed), and
    /// everything in the chosen order. A set sorts by its folder name, total size, newest file, or its first image for the rest.
    /// </summary>
    public Arranged Arrange(IEnumerable<int> passed, ViewQuery q)
    {
        var res = new Arranged();
        var loose = new List<int>();
        var setsHit = new HashSet<int>();
        foreach (int i in passed)
        {
            if (SetOf[i] >= 0) setsHit.Add(SetOf[i]);
            else loose.Add(i);
        }
        if (q.F.Sets == 1) loose.Clear();
        if (q.F.Sets == 2) setsHit.Clear();

        // one of each file
        if (q.HideCopies)
        {
            var byHash = new Dictionary<long, int>();
            var keep = new List<int>(loose.Count);
            foreach (int i in loose)
            {
                long h = FHash[i];
                if (h == 0) { keep.Add(i); continue; }
                if (!byHash.TryGetValue(h, out int other)) { byHash[h] = i; keep.Add(i); continue; }
                int win = Prefer(i, other, q.PreferFolder) ? i : other, lose = win == i ? other : i;
                if (win != other) { int at = keep.IndexOf(other); keep[at] = i; byHash[h] = i; if (res.Copies.Remove(other, out var moved)) res.Copies[i] = moved; }
                if (!res.Copies.TryGetValue(win, out var l)) res.Copies[win] = l = new List<int>();
                l.Add(lose);
            }
            loose = keep;
        }

        // units: a loose file, or a whole set
        var units = new List<(int first, int set)>(loose.Count + setsHit.Count);
        foreach (int i in loose) units.Add((i, -1));
        foreach (int si in setsHit) units.Add((Sets[si].Rows[0], si));
        string sort = q.Sort;
        bool desc = Descending(sort) != q.Reverse;
        if (sort == "Random")
        {
            units.Sort((a, b) => a.first.CompareTo(b.first));
            var rnd = new Random(q.RandomSeed);
            for (int k = units.Count - 1; k > 0; k--) { int j = rnd.Next(k + 1); (units[k], units[j]) = (units[j], units[k]); }
            if (q.Reverse) units.Reverse();
        }
        else if (sort is "Name" or "Folder")
        {
            string Key(int first, int set) => set >= 0 && !q.SetsByFirst
                ? (sort == "Name" ? Sets[set].Name : Display[first])
                : sort == "Name" ? System.IO.Path.GetFileName(Path[first]) : Display[first] + "\\" + System.IO.Path.GetFileName(Path[first]);
            var keys = units.Select(u => Key(u.first, u.set)).ToArray();
            var idx = Enumerable.Range(0, units.Count).ToArray();
            Array.Sort(idx, (a, b) =>
            {
                int c = NaturalComparer.Instance.Compare(keys[a], keys[b]);
                if (c == 0) c = units[a].first.CompareTo(units[b].first);
                return desc ? -c : c;
            });
            units = idx.Select(k => units[k]).ToList();
        }
        else
        {
            double Value(int first, int set)
            {
                if (set >= 0 && !q.SetsByFirst)
                {
                    var rows = Sets[set].Rows;
                    switch (sort)
                    {
                        case "Size": { long t = 0; foreach (int r in rows) t += Size[r]; return t; }
                        case "FirstSeen": case "Modified": case "Created": case "Taken":
                            { long n = long.MinValue; foreach (int r in rows) n = Math.Max(n, DateOf(r, sort)); return n; }
                    }
                }
                return sort switch
                {
                    "Size" => Size[first], "Width" => W[first], "Height" => H[first], "Megapixels" => (double)W[first] * H[first],
                    "Duration" => Duration[first],
                    "FirstSeen" => FirstSeen[first], "Modified" or "Created" or "Taken" => DateOf(first, sort),
                    _ => -first,
                };
            }
            var vals = units.Select(u => Value(u.first, u.set)).ToArray();
            var idx = Enumerable.Range(0, units.Count).ToArray();
            Array.Sort(idx, (a, b) =>
            {
                int c = vals[a].CompareTo(vals[b]);
                if (desc) c = -c;
                return c != 0 ? c : units[a].first.CompareTo(units[b].first);      // ties: newest first seen first, as the rows come
            });
            units = idx.Select(k => units[k]).ToList();
        }

        // write the order; a set's images follow one another in file-name order
        var order = new List<int>(loose.Count + setsHit.Sum(si => Sets[si].Rows.Length));
        foreach (var (first, set) in units)
        {
            if (set < 0) { order.Add(first); continue; }
            var rows = Sets[set].Rows;
            bool large = rows.Length > Math.Max(1, q.SetLimit);
            bool collapsed = large && !q.OpenSets.Contains(Sets[set].Key);
            res.Sets.Add(new SetBlock(order.Count, rows.Length, Sets[set].Key, Sets[set].Name, rows.Length, collapsed, large));
            order.AddRange(rows);
        }
        res.Order = order.ToArray();
        return res;
    }

    /// <summary>Which of two copies of a file to show: the one in the preferred folder, else the first by folder name.</summary>
    private bool Prefer(int a, int b, string preferFolder)
    {
        if (preferFolder.Length > 0)
        {
            bool ia = FolderPaths.IsUnder(Display[a], preferFolder), ib = FolderPaths.IsUnder(Display[b], preferFolder);
            if (ia != ib) return ia;
        }
        int c = string.Compare(Display[a], Display[b], StringComparison.OrdinalIgnoreCase);
        if (c != 0) return c < 0;
        return string.Compare(Path[a], Path[b], StringComparison.OrdinalIgnoreCase) < 0;
    }

    /// <summary>Home and Favourites: the rows to show, in order (folders switched off, search, filters, sets, copies).</summary>
    public Arranged Select(ViewQuery q)
    {
        string search = q.Search.Trim();
        var keep = new List<int>(Count);
        for (int i = 0; i < Count; i++)
        {
            if (q.OffFolders.Count > 0 && q.OffFolders.Contains(Display[i])) continue;
            if (!PassesAll(i, q, search)) continue;
            keep.Add(i);
        }
        return Arrange(keep, q);
    }

    /// <summary>Browse: the same filters, for rows already chosen by folder and search.</summary>
    public Arranged ArrangeFiltered(IEnumerable<int> rows, ViewQuery q) => Arrange(rows.Where(i => PassesAll(i, q, "")), q);

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

    /// <summary>
    /// Every folder that has files (and its parents), as Select folders shows them (NSFW and Very folders merged into the folder
    /// above), with how much of its OWN content passes the level and type switches.
    /// </summary>
    public Dictionary<string, FolderStat> FolderStats(ViewQuery q) => Stats(i => Shown(i, q) ? Display[i] : null, i => Display[i]);

    /// <summary>For the NSFW or Very pop-out: the folders holding NSFW folders, with how many files of that level each has.</summary>
    public Dictionary<string, FolderStat> LevelStats(ViewQuery q, ContentLevel level) =>
        Stats(i => Level[i] == (byte)level && KindShown(Kind[i], q) ? Owner[i] : null, i => Level[i] == (byte)level ? Owner[i] : null);

    private Dictionary<string, FolderStat> Stats(Func<int, string?> counted, Func<int, string?> exists)
    {
        var stats = new Dictionary<string, FolderStat>(StringComparer.OrdinalIgnoreCase);
        FolderStat Ensure(string f)
        {
            if (stats.TryGetValue(f, out var st)) return st;
            stats[f] = st = new FolderStat();
            string p = f;                                         // make sure every parent exists too
            while (p.Length > 0)
            {
                int cut = p.LastIndexOf('\\');
                p = cut < 0 ? "" : p.Substring(0, cut);
                if (p.Length == 0 || stats.ContainsKey(p)) break;
                stats[p] = new FolderStat();
            }
            return st;
        }
        for (int i = 0; i < Count; i++)
        {
            if (exists(i) is not { } f) continue;
            var st = Ensure(f);
            if (counted(i) is null) continue;
            st.Own++; st.Size += Size[i]; if (FirstSeen[i] > st.Newest) st.Newest = FirstSeen[i];
        }
        return stats;
    }
}
