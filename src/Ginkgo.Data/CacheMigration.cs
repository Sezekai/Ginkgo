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
}
