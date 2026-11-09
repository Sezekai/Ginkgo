namespace Ginkgo.Data;

/// <summary>
/// Moves the small thumbnail packs (micro, small, full) from a slow drive to a fast one. Looking up hundreds of thumbnails in
/// random order (a shuffled grid) is what a hard drive is worst at, and an SSD is best at. Nothing is deleted from the old place
/// until everything has been copied, and a finished move is marked with a small file, so an interrupted move simply resumes.
/// </summary>
public static class CacheMigration
{
    public static readonly string[] Prefixes = { "micro", "small", "pack" };
    public const string Marker = "moved.ok";

    public readonly record struct Item(string Path, long Size);
    public sealed record MovePlan(IReadOnlyList<Item> Items, long TotalBytes);

    public static bool IsDone(string to) => File.Exists(System.IO.Path.Combine(to, Marker));

    public static MovePlan Plan(string from)
    {
        var items = new List<Item>();
        if (Directory.Exists(from))
            foreach (var prefix in Prefixes)
                foreach (var f in Directory.EnumerateFiles(from, prefix + "-*.bin").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    items.Add(new Item(f, new FileInfo(f).Length));
        return new MovePlan(items, items.Sum(i => i.Size));
    }

    /// <summary>Copies every file of the plan into <paramref name="to"/> (skipping ones already there in full), then removes the originals and writes the marker.</summary>
    public static void Run(MovePlan plan, string to, Action<long, long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        long done = 0;
        var buf = new byte[4 << 20];
        foreach (var item in plan.Items)
        {
            ct.ThrowIfCancellationRequested();
            string name = System.IO.Path.GetFileName(item.Path);
            string dest = System.IO.Path.Combine(to, name), part = dest + ".part";
            if (File.Exists(dest) && new FileInfo(dest).Length == item.Size) { done += item.Size; progress?.Invoke(done, plan.TotalBytes); continue; }
            using (var src = new FileStream(item.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buf, 0, n); done += n; progress?.Invoke(done, plan.TotalBytes);
                }
            }
            if (new FileInfo(part).Length != item.Size) throw new IOException("copy of " + name + " is incomplete");
            File.Move(part, dest, true);
        }
        foreach (var item in plan.Items) { try { File.Delete(item.Path); } catch { } }   // only after everything arrived
        File.WriteAllText(System.IO.Path.Combine(to, Marker), DateTime.UtcNow.ToString("O"));
    }

    /// <summary>Marks a folder as the place to use without copying anything (nothing to move).</summary>
    public static void MarkEmpty(string to) { Directory.CreateDirectory(to); File.WriteAllText(System.IO.Path.Combine(to, Marker), DateTime.UtcNow.ToString("O")); }

    /// <summary>
    /// Moves the whole cache folder (every file, folders included) to <paramref name="to"/>: copied first, checked, and the originals
    /// removed only after everything arrived. Files already there in full are skipped, so a stopped move carries on.
    /// </summary>
    public static void MoveAll(string from, string to, Action<long, long>? progress, CancellationToken ct)
    {
        string a = System.IO.Path.GetFullPath(from).TrimEnd('\\', '/'), b = System.IO.Path.GetFullPath(to).TrimEnd('\\', '/');
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(a)) return;
        var files = Directory.EnumerateFiles(a, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .Select(f => (path: f, size: new FileInfo(f).Length)).ToList();
        long total = files.Sum(f => f.size), done = 0;
        var buf = new byte[4 << 20];
        foreach (var (path, size) in files)
        {
            ct.ThrowIfCancellationRequested();
            string dest = System.IO.Path.Combine(b, System.IO.Path.GetRelativePath(a, path)), part = dest + ".part";
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
            if (File.Exists(dest) && new FileInfo(dest).Length == size) { done += size; progress?.Invoke(done, total); continue; }
            using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, FileOptions.SequentialScan))
            using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0) { ct.ThrowIfCancellationRequested(); dst.Write(buf, 0, n); done += n; progress?.Invoke(done, total); }
            }
            if (new FileInfo(part).Length != size) throw new IOException("copy of " + path + " is incomplete");
            File.Move(part, dest, true);
        }
        foreach (var (path, _) in files) { try { File.Delete(path); } catch { } }
    }
}
