using System.Security.Cryptography;
using Ginkgo.Core;
using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

/// <summary>
/// Content hashes. A file is first compared by size, then by a partial hash (its size, first 64 KB and last 64 KB), and only
/// when those match is it read in full. Each hash is the first 8 bytes of a SHA-256, never 0 (0 in the index means "not known").
/// </summary>
public static class ContentHash
{
    public const int Edge = 64 * 1024;

    private static long Of(ReadOnlySpan<byte> sha)
    {
        long v = BitConverter.ToInt64(sha[..8]);
        return v == 0 ? 1 : v;
    }

    /// <summary>Size, the first and the last 64 KB. Two different files of the same size almost never share this.</summary>
    public static long Partial(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        long size = f.Length;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        h.AppendData(BitConverter.GetBytes(size));
        var buf = new byte[Edge];
        int n = ReadFull(f, buf, (int)Math.Min(Edge, size));
        h.AppendData(buf, 0, n);
        if (size > Edge)
        {
            long tail = Math.Max(Edge, size - Edge);
            f.Position = tail;
            n = ReadFull(f, buf, (int)(size - tail));
            h.AppendData(buf, 0, n);
        }
        return Of(h.GetHashAndReset());
    }

    /// <summary>The whole file. <paramref name="read"/> is told how many bytes have been read so far.</summary>
    public static long Full(string path, Action<long>? read = null, CancellationToken ct = default)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buf = new byte[1 << 20];
        long done = 0;
        int n;
        while ((n = f.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            h.AppendData(buf, 0, n);
            done += n; read?.Invoke(done);
        }
        return Of(h.GetHashAndReset());
    }

    internal static long Finish(IncrementalHash h) => Of(h.GetHashAndReset());

    private static int ReadFull(Stream s, byte[] buf, int count)
    {
        int got = 0;
        while (got < count) { int n = s.Read(buf, got, count - got); if (n <= 0) break; got += n; }
        return got;
    }
}

/// <summary>Where a favourite goes inside the Favourites folder, and whether a path is inside it.</summary>
public static class FavouritePlaces
{
    /// <summary>Images in the root, videos in Videos, GIFs in Gifs; NSFW and NSFW\Very below each.</summary>
    public static string SubFolder(MediaKind kind, ContentLevel level)
    {
        string k = kind == MediaKind.Video ? "Videos" : kind == MediaKind.Gif ? "Gifs" : "";
        string l = level == ContentLevel.Very ? Path.Combine("NSFW", "Very") : level == ContentLevel.Nsfw ? "NSFW" : "";
        return k.Length == 0 ? l : l.Length == 0 ? k : Path.Combine(k, l);
    }

    public static string Prefix(string favRoot) => Path.GetFullPath(favRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;

    public static bool IsInside(string path, string favRoot) => path.StartsWith(Prefix(favRoot), StringComparison.OrdinalIgnoreCase);

    /// <summary>SQL that is true for rows inside the Favourites folder (needs the $favroot parameter, see <see cref="Bind"/>).</summary>
    public const string InsideSql = "substr(path,1,length($favroot))=$favroot COLLATE NOCASE";
    public static void Bind(SqliteCommand cmd, string favRoot) => cmd.Parameters.AddWithValue("$favroot", Prefix(favRoot));
}

/// <summary>Progress of a favourites check or copy, read by the screen while it runs.</summary>
public sealed class FavProgress
{
    public volatile bool Running;
    public volatile string What = "";
    public volatile string Current = "";
    public long FilesDone, FilesTotal, BytesDone, BytesTotal;
    public long StartedTicks;
    public void Start(string what, long files, long bytes)
    {
        What = what; Current = "";
        Interlocked.Exchange(ref FilesDone, 0); Interlocked.Exchange(ref FilesTotal, files);
        Interlocked.Exchange(ref BytesDone, 0); Interlocked.Exchange(ref BytesTotal, bytes);
        StartedTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        Running = true;
    }
    /// <summary>0..1, by bytes when known (reading is what takes the time), else by files.</summary>
    public double Fraction
    {
        get
        {
            long bt = Interlocked.Read(ref BytesTotal), ft = Interlocked.Read(ref FilesTotal);
            if (bt > 0) return Math.Clamp(Interlocked.Read(ref BytesDone) / (double)bt, 0, 1);
            return ft > 0 ? Math.Clamp(Interlocked.Read(ref FilesDone) / (double)ft, 0, 1) : 0;
        }
    }
    public TimeSpan Elapsed => TimeSpan.FromSeconds((System.Diagnostics.Stopwatch.GetTimestamp() - StartedTicks) / (double)System.Diagnostics.Stopwatch.Frequency);
    /// <summary>Estimated time left (null until there is enough to go on).</summary>
    public TimeSpan? Remaining
    {
        get
        {
            double f = Fraction, s = Elapsed.TotalSeconds;
            if (f < 0.02 || s < 0.5) return null;
            return TimeSpan.FromSeconds(s / f - s);
        }
    }
}

/// <summary>Which files are favourites, worked out from the Favourites folder by content.</summary>
public sealed class FavState
{
    /// <summary>Files in the Favourites folder.</summary>
    public HashSet<long> FavIds { get; } = new();
    /// <summary>Files elsewhere whose content is identical to a favourite (they show the star).</summary>
    public HashSet<long> Starred { get; } = new();
    /// <summary>Full content hash of each favourite and starred file, where it was needed and is known.</summary>
    public Dictionary<long, long> HashOf { get; } = new();
    /// <summary>The favourite files with each content.</summary>
    public Dictionary<long, List<string>> FavPathsByHash { get; } = new();
    public Dictionary<long, string> PathOf { get; } = new();
    public Dictionary<long, long> SizeOf { get; } = new();
    /// <summary>Favourites with at least one original found elsewhere.</summary>
    public int Linked;

    public bool IsStarred(long id) => FavIds.Contains(id) || Starred.Contains(id);
}

/// <summary>
/// Works out the stars: each favourite is linked to the files elsewhere with identical content (size, then partial hash, then a
/// full hash). Only files whose size matches a favourite are ever read, and hashes are kept in the index until a file changes.
/// </summary>
public static class FavouriteIndex
{
    private sealed class Row { public long Id; public string Path = ""; public long Size; public long PHash, FHash; public bool Fav; }

    public static FavState Sync(IndexDb db, string favRoot, FavProgress? progress = null, CancellationToken ct = default)
    {
        var st = new FavState();
        var favs = new List<Row>();
        using var c = db.Open();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, size, phash, fhash FROM files WHERE missing_since=0 AND " + FavouritePlaces.InsideSql;
            FavouritePlaces.Bind(cmd, favRoot);
            using var r = cmd.ExecuteReader();
            while (r.Read()) favs.Add(new Row { Id = r.GetInt64(0), Path = r.GetString(1), Size = r.GetInt64(2), PHash = r.GetInt64(3), FHash = r.GetInt64(4), Fav = true });
        }
        foreach (var f in favs) { st.FavIds.Add(f.Id); st.PathOf[f.Id] = f.Path; st.SizeOf[f.Id] = f.Size; }
        if (favs.Count == 0) return st;

        // everything else with the same size as a favourite
        var bySize = favs.GroupBy(f => f.Size).ToDictionary(g => g.Key, g => g.ToList());
        var cands = new List<Row>();
        foreach (var chunk in bySize.Keys.Chunk(400))
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT id, path, size, phash, fhash FROM files WHERE missing_since=0 AND size IN (" + string.Join(",", chunk) + ") AND NOT (" + FavouritePlaces.InsideSql + ")";
            FavouritePlaces.Bind(cmd, favRoot);
            using var r = cmd.ExecuteReader();
            while (r.Read()) cands.Add(new Row { Id = r.GetInt64(0), Path = r.GetString(1), Size = r.GetInt64(2), PHash = r.GetInt64(3), FHash = r.GetInt64(4) });
        }

        // partial hashes: candidates, and favourites that have a candidate of their size
        var candSizes = cands.Select(x => x.Size).ToHashSet();
        var needP = cands.Where(x => x.PHash == 0).Concat(favs.Where(f => f.PHash == 0 && candSizes.Contains(f.Size))).ToList();
        // full hashes are worked out below; count the partial reads as small files
        progress?.Start("Checking favourites", needP.Count, 0);
        var saveP = new List<(long id, long size, long h)>();
        foreach (var x in needP)
        {
            ct.ThrowIfCancellationRequested();
            if (progress is not null) progress.Current = x.Path;
            try { x.PHash = ContentHash.Partial(x.Path); saveP.Add((x.Id, x.Size, x.PHash)); } catch { }
            if (progress is not null) Interlocked.Increment(ref progress.FilesDone);
        }
        Save(c, "phash", saveP);

        // full hashes where size and partial hash agree
        var favKeys = favs.Where(f => f.PHash != 0).GroupBy(f => (f.Size, f.PHash)).ToDictionary(g => g.Key, g => g.ToList());
        var pairs = cands.Where(x => x.PHash != 0 && favKeys.ContainsKey((x.Size, x.PHash))).ToList();
        var needF = new List<Row>();
        foreach (var x in pairs)
        {
            if (x.FHash == 0) needF.Add(x);
            foreach (var f in favKeys[(x.Size, x.PHash)]) if (f.FHash == 0 && !needF.Contains(f)) needF.Add(f);
        }
        if (needF.Count > 0)
        {
            progress?.Start("Confirming favourites", needF.Count, needF.Sum(x => x.Size));
            var saveF = new List<(long id, long size, long h)>();
            long before = 0;
            foreach (var x in needF)
            {
                ct.ThrowIfCancellationRequested();
                if (progress is not null) progress.Current = x.Path;
                long b0 = before;
                try { x.FHash = ContentHash.Full(x.Path, n => { if (progress is not null) Interlocked.Exchange(ref progress.BytesDone, b0 + n); }, ct); saveF.Add((x.Id, x.Size, x.FHash)); }
                catch (OperationCanceledException) { throw; }
                catch { }
                before += x.Size;
                if (progress is not null) { Interlocked.Exchange(ref progress.BytesDone, before); Interlocked.Increment(ref progress.FilesDone); }
            }
            Save(c, "fhash", saveF);
        }

        foreach (var f in favs.Where(f => f.FHash != 0))
        {
            st.HashOf[f.Id] = f.FHash;
            if (!st.FavPathsByHash.TryGetValue(f.FHash, out var l)) st.FavPathsByHash[f.FHash] = l = new List<string>();
            l.Add(f.Path);
        }
        var linked = new HashSet<long>();
        foreach (var x in pairs)
        {
            if (x.FHash == 0 || !st.FavPathsByHash.ContainsKey(x.FHash)) continue;
            st.Starred.Add(x.Id); st.HashOf[x.Id] = x.FHash; st.PathOf[x.Id] = x.Path; st.SizeOf[x.Id] = x.Size;
            linked.Add(x.FHash);
        }
        st.Linked = favs.Count(f => f.FHash != 0 && linked.Contains(f.FHash));
        if (progress is not null) { progress.Running = false; progress.Current = ""; }
        return st;
    }

    private static void Save(SqliteConnection c, string col, List<(long id, long size, long h)> rows)
    {
        if (rows.Count == 0) return;
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE files SET {col}=$h WHERE id=$id AND size=$s";      // a file that changed meanwhile keeps 0
        var ph = cmd.Parameters.Add("$h", SqliteType.Integer); var pi = cmd.Parameters.Add("$id", SqliteType.Integer); var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
        foreach (var r in rows) { ph.Value = r.h; pi.Value = r.id; ps.Value = r.size; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>
    /// The favourite copies with the same content as file <paramref name="id"/> (which may itself be a favourite). Hashes what it
    /// has to, so it works for a favourite that has no original too.
    /// </summary>
    public static List<string> CopiesOf(IndexDb db, FavState st, long id)
    {
        if (st.HashOf.TryGetValue(id, out long h) && st.FavPathsByHash.TryGetValue(h, out var known)) return known.Where(File.Exists).ToList();
        if (!st.FavIds.Contains(id) || !st.PathOf.TryGetValue(id, out var path)) return new List<string>();
        // a favourite never compared with anything: it, plus any other favourite of the same size and content
        var result = new List<string> { path };
        long size = st.SizeOf[id];
        var same = st.FavIds.Where(o => o != id && st.SizeOf.TryGetValue(o, out long s) && s == size).ToList();
        if (same.Count == 0) return result;
        try
        {
            long mine = ContentHash.Full(path);
            foreach (var o in same) { try { if (ContentHash.Full(st.PathOf[o]) == mine) result.Add(st.PathOf[o]); } catch { } }
        }
        catch { }
        return result;
    }
}

public enum CopyOutcome { Copied, AlreadyThere, Failed }
public sealed record FavCopyResult(CopyOutcome Outcome, string Path, long Hash, string Message);

/// <summary>
/// Copies a file into the Favourites folder: under a temporary name first, checked against the original, then renamed. An
/// existing file is never overwritten, and nothing is left behind if anything goes wrong. The original is only read.
/// </summary>
public static class FavouriteCopier
{
    public static FavCopyResult Copy(string src, string destDir, string? logPath, FavProgress? progress = null, CancellationToken ct = default)
    {
        string? temp = null;
        try
        {
            var info = new FileInfo(src);
            if (!info.Exists) return Fail("The file can't be found any more.");
            long size = info.Length;
            progress?.Start("Copying to Favourites", 1, size);
            if (progress is not null) progress.Current = src;
            Directory.CreateDirectory(destDir);
            string name = Path.GetFileName(src);
            string target = Path.Combine(destDir, name);

            // the same name: identical content means it's already there; otherwise the new copy gets a number
            if (File.Exists(target))
            {
                if (new FileInfo(target).Length == size)
                {
                    long a = ContentHash.Full(src, null, ct), b = ContentHash.Full(target, null, ct);
                    if (a == b) { Done(); return new FavCopyResult(CopyOutcome.AlreadyThere, target, a, "Already in Favourites."); }
                }
                target = FreeName(destDir, name);
            }

            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(destDir));
                if (!string.IsNullOrEmpty(root))
                {
                    long free = new DriveInfo(root).AvailableFreeSpace;
                    if (free < size + (16L << 20)) return Fail($"Not enough room on {root} ({free / 1048576.0:N0} MB free, {size / 1048576.0:N0} MB needed).");
                }
            }
            catch { }

            temp = Path.Combine(destDir, "." + name + ".ginkgo-" + Guid.NewGuid().ToString("N")[..8] + ".part");
            long srcHash;
            using (var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1))
            using (var h = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                var buf = new byte[1 << 20];
                long done = 0; int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    h.AppendData(buf, 0, n);
                    output.Write(buf, 0, n);
                    done += n;
                    if (progress is not null) Interlocked.Exchange(ref progress.BytesDone, done / 2);      // copying is the first half
                }
                output.Flush(true);
                srcHash = ContentHash.Finish(h);
            }
            // check the copy: same length, same content
            if (new FileInfo(temp).Length != size) return Fail("The copy came out a different size.");
            long copyHash = ContentHash.Full(temp, n => { if (progress is not null) Interlocked.Exchange(ref progress.BytesDone, size / 2 + n / 2); }, ct);
            if (copyHash != srcHash) return Fail("The copy didn't match the original.");
            try { File.SetLastWriteTimeUtc(temp, info.LastWriteTimeUtc); } catch { }

            for (int attempt = 0; ; attempt++)
            {
                try { File.Move(temp, target, overwrite: false); break; }
                catch (IOException) when (attempt < 5 && File.Exists(target)) { target = FreeName(destDir, name); }
            }
            temp = null;
            Log(logPath, $"copied\t{src}\t{target}\t{size}");
            Done();
            return new FavCopyResult(CopyOutcome.Copied, target, srcHash, "");

            void Done() { if (progress is not null) { Interlocked.Exchange(ref progress.BytesDone, size); progress.FilesDone = 1; } }
        }
        catch (OperationCanceledException) { return Fail("Stopped."); }
        catch (Exception ex) { return Fail(ex.Message); }
        finally
        {
            if (temp is not null) { try { File.Delete(temp); } catch { } }        // our own unfinished copy, never anything else
            if (progress is not null) { progress.Running = false; progress.Current = ""; }
        }
    }

    /// <summary>"name (2).ext", "name (3).ext" ... the first that isn't taken.</summary>
    public static string FreeName(string dir, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int k = 2; ; k++)
        {
            string p = Path.Combine(dir, $"{stem} ({k}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    public static void Log(string? logPath, string line)
    {
        if (logPath is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.AppendAllText(logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\t" + line + Environment.NewLine);
        }
        catch { }
    }

    private static FavCopyResult Fail(string why) => new(CopyOutcome.Failed, "", 0, why);
}
