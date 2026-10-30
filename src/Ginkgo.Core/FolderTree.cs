namespace Ginkgo.Core;

/// <summary>
/// Folder paths as Browse shows them: content-level folders (NSFW, and Very inside NSFW) are not folders of their own,
/// their files belong to the folder above. Paths are relative to the library root, separated by '\', "" being the root.
/// </summary>
public static class FolderPaths
{
    public static string Display(string folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        var parts = folder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        var keep = new List<string>(parts.Length);
        string? prev = null;
        foreach (var p in parts)
        {
            if (!ContentClassifier.IsLevelFolder(p, prev)) keep.Add(p);
            prev = p;
        }
        return string.Join('\\', keep);
    }

    public static string Parent(string path) { int i = path.LastIndexOf('\\'); return i < 0 ? "" : path[..i]; }
    public static string Name(string path) { int i = path.LastIndexOf('\\'); return i < 0 ? path : path[(i + 1)..]; }

    /// <summary>True if <paramref name="path"/> is <paramref name="folder"/> or lies anywhere beneath it.</summary>
    public static bool IsUnder(string path, string folder) =>
        folder.Length == 0 || path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
        (path.Length > folder.Length && path[folder.Length] == '\\' && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>The child of <paramref name="folder"/> that <paramref name="path"/> lies in (null if it is the folder itself or not under it).</summary>
    public static string? ChildOf(string path, string folder)
    {
        if (folder.Length == 0)
        {
            if (path.Length == 0) return null;
            int c = path.IndexOf('\\');
            return c < 0 ? path : path[..c];
        }
        if (path.Length <= folder.Length || path[folder.Length] != '\\' || !path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return null;
        int end = path.IndexOf('\\', folder.Length + 1);
        return end < 0 ? path : path[..end];
    }
}

public enum FileKind { Image = 0, Gif = 1, Video = 2 }

public sealed class FolderNode
{
    public string Path = "", Name = "";
    public int Images, Gifs, Videos;
    public long Size, Newest;
    public FolderNode? Parent;
    public readonly List<FolderNode> Children = new();
    public int Files => Images + Gifs + Videos;
    public int Subfolders => Children.Count;
}

/// <summary>The folders that have something to show (under the current switches), with totals that include everything beneath.</summary>
public sealed class FolderTree
{
    public FolderNode Root { get; } = new();
    private readonly Dictionary<string, FolderNode> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public FolderTree() { _nodes[""] = Root; }

    public FolderNode? Find(string path) => _nodes.TryGetValue(path, out var n) ? n : null;

    /// <summary>The folder itself if it is shown, else the nearest parent that is.</summary>
    public FolderNode Nearest(string path)
    {
        while (true)
        {
            if (_nodes.TryGetValue(path, out var n)) return n;
            if (path.Length == 0) return Root;
            path = FolderPaths.Parent(path);
        }
    }

    /// <summary>Adds one visible file (its display folder, kind, size and first-seen time) to its folder and every folder above.</summary>
    public void Add(string displayFolder, FileKind kind, long size, long newest)
    {
        var node = Node(displayFolder);
        for (var n = node; n is not null; n = n.Parent)
        {
            if (kind == FileKind.Image) n.Images++; else if (kind == FileKind.Gif) n.Gifs++; else n.Videos++;
            n.Size += size;
            if (newest > n.Newest) n.Newest = newest;
        }
    }

    private FolderNode Node(string path)
    {
        if (_nodes.TryGetValue(path, out var n)) return n;
        var parent = Node(FolderPaths.Parent(path));
        n = new FolderNode { Path = path, Name = FolderPaths.Name(path), Parent = parent };
        parent.Children.Add(n);
        _nodes[path] = n;
        return n;
    }

    /// <summary>Sorts folder tiles. Keys: Name, Size, Updated, Images, Videos, Gifs, Files. Biggest / newest first, except names (A to Z).</summary>
    public static List<FolderNode> Sort(IEnumerable<FolderNode> nodes, string key, bool reverse)
    {
        var list = nodes.ToList();
        Comparison<FolderNode> byName = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        Comparison<FolderNode> cmp = key switch
        {
            "Size" => (a, b) => b.Size.CompareTo(a.Size),
            "Updated" => (a, b) => b.Newest.CompareTo(a.Newest),
            "Images" => (a, b) => b.Images.CompareTo(a.Images),
            "Videos" => (a, b) => b.Videos.CompareTo(a.Videos),
            "Gifs" => (a, b) => b.Gifs.CompareTo(a.Gifs),
            "Files" => (a, b) => b.Files.CompareTo(a.Files),
            _ => byName,
        };
        list.Sort((a, b) => { int c = cmp(a, b); if (c == 0 && key != "Name") c = byName(a, b); return reverse ? -c : c; });
        return list;
    }
}
