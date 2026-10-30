using System.Globalization;
using System.IO;
using System.Text.Json;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// Folder covers, kept in Ginkgo's own data (never in your folders). A cover is the picture you picked, or else a random one that
/// stays the same until you ask for a new one. A cover is always a picture that is shown under the current switches.
/// </summary>
public sealed class CoverStore
{
    public sealed class Cover { public long? Pinned { get; set; } public long? Random { get; set; } }

    private readonly Dictionary<string, Cover> _covers;
    private readonly object _lock = new();
    private bool _dirty;
    private readonly Random _rnd = new();

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "covers.json");

    private CoverStore(Dictionary<string, Cover> covers) { _covers = covers; }

    public static CoverStore Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, Cover>>(File.ReadAllText(FilePath));
                if (d is not null) return new CoverStore(new Dictionary<string, Cover>(d, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch { }
        return new CoverStore(new Dictionary<string, Cover>(StringComparer.OrdinalIgnoreCase));
    }

    public void Save()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(_covers));
                _dirty = false;
            }
            catch { }
        }
    }

    /// <summary>The row to show as the cover of <paramref name="folder"/>, chosen from <paramref name="candidates"/> (rows shown right now); -1 if none.</summary>
    public int Choose(string folder, List<int> candidates, LibraryModel m)
    {
        if (candidates.Count == 0) return -1;
        lock (_lock)
        {
            _covers.TryGetValue(folder, out var c);
            if (c?.Pinned is long pin) { int r = Find(candidates, m, pin); if (r >= 0) return r; }
            if (c?.Random is long ran) { int r = Find(candidates, m, ran); if (r >= 0) return r; }
            int pick = PickRandom(candidates, m, -1);
            c ??= _covers[folder] = new Cover();
            c.Random = m.Id[pick]; _dirty = true;
            return pick;
        }
    }

    /// <summary>A new random cover (different from the current one when possible).</summary>
    public void Reroll(string folder, List<int> candidates, LibraryModel m)
    {
        if (candidates.Count == 0) return;
        lock (_lock)
        {
            _covers.TryGetValue(folder, out var c);
            c ??= _covers[folder] = new Cover();
            long current = c.Pinned ?? c.Random ?? -1;
            c.Pinned = null;
            c.Random = m.Id[PickRandom(candidates, m, current)];
            _dirty = true;
        }
    }

    public void Pin(string folder, long id) { lock (_lock) { if (!_covers.TryGetValue(folder, out var c)) _covers[folder] = c = new Cover(); c.Pinned = id; _dirty = true; } }
    public void Reset(string folder) { lock (_lock) { if (_covers.Remove(folder)) _dirty = true; } }
    public bool IsPinned(string folder) { lock (_lock) return _covers.TryGetValue(folder, out var c) && c.Pinned is not null; }

    private static int Find(List<int> rows, LibraryModel m, long id) { foreach (int r in rows) if (m.Id[r] == id) return r; return -1; }

    /// <summary>Random, preferring General pictures so a cover does not vanish when NSFW is switched off.</summary>
    private int PickRandom(List<int> rows, LibraryModel m, long avoid)
    {
        var general = rows.Where(r => m.Level[r] == (byte)ContentLevel.General && m.Id[r] != avoid).ToList();
        var pool = general.Count > 0 ? general : rows.Where(r => m.Id[r] != avoid).ToList();
        if (pool.Count == 0) pool = rows;
        return pool[_rnd.Next(pool.Count)];
    }
}

/// <summary>What Browse shows for one folder: subfolder tiles first (by the folder sort), then the files (by the file sort).</summary>
public sealed class BrowsePage
{
    public string Folder = "";
    public FolderNode Node = new();
    public List<FolderNode> Tiles = new();
    public int[] Order = Array.Empty<int>();        // cover rows for the tiles (-1 = no picture), then file rows
    public string?[] Labels = Array.Empty<string?>();
    public Dictionary<string, List<int>> Candidates = new(StringComparer.OrdinalIgnoreCase);
    public int FirstFile => Tiles.Count;

    public static readonly (string key, string name)[] DetailKinds =
    {
        ("Images", "Image count"), ("Videos", "Video count"), ("Gifs", "GIF count"), ("Size", "Total size"), ("Updated", "Last updated"), ("Subfolders", "Subfolder count"),
    };

    public static BrowsePage Build(LibraryModel m, ViewQuery q, string folder, bool ownOnly, string folderSort, bool folderReverse,
        CoverStore covers, ISet<string> details)
    {
        var p = new BrowsePage();
        var tree = m.Tree(q);
        var node = tree.Nearest(folder);
        p.Folder = node.Path; p.Node = node;
        string search = q.Search.Trim();

        // which rows lie in each subfolder (cover candidates) and which are this folder's files
        var files = new List<int>();
        for (int i = 0; i < m.Count; i++)
        {
            if (!m.Shown(i, q)) continue;
            string d = m.Display[i];
            if (!FolderPaths.IsUnder(d, p.Folder)) continue;
            string? child = FolderPaths.ChildOf(d, p.Folder);
            if (child is not null)
            {
                if (!p.Candidates.TryGetValue(child, out var list)) p.Candidates[child] = list = new List<int>();
                list.Add(i);
            }
            // the top of Browse shows folders, plus only the files lying directly in the library folder
            bool mine = ownOnly || p.Folder.Length == 0 ? child is null : true;
            if (mine && (search.Length == 0 || m.Path[i].IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)) files.Add(i);
        }
        var kids = node.Children.Where(c => search.Length == 0 || c.Name.Contains(search, StringComparison.OrdinalIgnoreCase));
        p.Tiles = FolderTree.Sort(kids, folderSort, folderReverse);

        var order = new List<int>(p.Tiles.Count + files.Count);
        var labels = new List<string?>(p.Tiles.Count + files.Count);
        foreach (var t in p.Tiles)
        {
            p.Candidates.TryGetValue(t.Path, out var cand);
            order.Add(covers.Choose(t.Path, cand ?? new List<int>(), m));
            labels.Add(t.Name + "\n" + DetailLine(t, details));
        }
        var sorted = m.SortRows(files.ToArray(), q);
        order.AddRange(sorted);
        for (int k = 0; k < sorted.Length; k++) labels.Add(null);
        p.Order = order.ToArray(); p.Labels = labels.ToArray();
        return p;
    }

    public static string DetailLine(FolderNode n, ISet<string> details)
    {
        var parts = new List<string>();
        if (details.Contains("Images") && n.Images > 0) parts.Add($"{n.Images:N0} image{(n.Images == 1 ? "" : "s")}");
        if (details.Contains("Gifs") && n.Gifs > 0) parts.Add($"{n.Gifs:N0} GIF{(n.Gifs == 1 ? "" : "s")}");
        if (details.Contains("Videos") && n.Videos > 0) parts.Add($"{n.Videos:N0} video{(n.Videos == 1 ? "" : "s")}");
        if (details.Contains("Size")) parts.Add(SizeText(n.Size));
        if (details.Contains("Subfolders") && n.Subfolders > 0) parts.Add($"{n.Subfolders:N0} folder{(n.Subfolders == 1 ? "" : "s")}");
        if (details.Contains("Updated") && n.Newest > 0) parts.Add("updated " + DateTimeOffset.FromUnixTimeSeconds(n.Newest).ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture));
        return string.Join("  ·  ", parts);
    }

    public static string SizeText(long b) => b >= 1L << 30 ? $"{b / 1073741824.0:F1} GB" : b >= 1L << 20 ? $"{b / 1048576.0:F0} MB" : $"{b / 1024.0:F0} KB";
}
