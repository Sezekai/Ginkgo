using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

/// <summary>
/// The date a photo was taken, from the EXIF block at the start of a JPEG. Only the first part of the file is read (the camera
/// writes the date near the start). Other formats, and photos without a date, fall back to the modified date when sorting.
/// </summary>
public static class PhotoDates
{
    private static readonly HashSet<string> Jpeg = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jfif" };

    /// <summary>Unix seconds of DateTimeOriginal (or DateTimeDigitized), read as local time; null when there is none.</summary>
    public static long? Read(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
        var head = new byte[2];
        if (f.Read(head, 0, 2) != 2 || head[0] != 0xFF || head[1] != 0xD8) return null;
        var seg = new byte[4];
        for (int guard = 0; guard < 32; guard++)
        {
            if (f.Read(seg, 0, 4) != 4 || seg[0] != 0xFF) return null;
            int marker = seg[1], len = (seg[2] << 8) | seg[3];
            if (marker == 0xDA || marker == 0xD9 || len < 2) return null;        // the picture itself: no EXIF before it
            if (marker == 0xE1)
            {
                var data = new byte[len - 2];
                if (ReadFull(f, data) != data.Length) return null;
                if (data.Length > 14 && data[0] == 'E' && data[1] == 'x' && data[2] == 'i' && data[3] == 'f' && data[4] == 0 && data[5] == 0)
                    return FromTiff(data[6..]);
                continue;
            }
            f.Seek(len - 2, SeekOrigin.Current);
        }
        return null;
    }

    private static long? FromTiff(byte[] t)
    {
        if (t.Length < 8) return null;
        bool le = t[0] == 'I' && t[1] == 'I';
        if (!le && !(t[0] == 'M' && t[1] == 'M')) return null;
        int U16(int o) => o + 2 > t.Length ? -1 : le ? t[o] | (t[o + 1] << 8) : (t[o] << 8) | t[o + 1];
        long U32(int o) => o + 4 > t.Length ? -1 : le ? (uint)(t[o] | (t[o + 1] << 8) | (t[o + 2] << 16) | (t[o + 3] << 24)) : (uint)((t[o] << 24) | (t[o + 1] << 16) | (t[o + 2] << 8) | t[o + 3]);
        long ifd0 = U32(4);
        long exif = -1; string? fallback = null;
        string? Ascii(int entry)
        {
            long count = U32(entry + 4), off = U32(entry + 8);
            if (count < 19 || off < 0 || off + 19 > t.Length) return null;
            return System.Text.Encoding.ASCII.GetString(t, (int)off, 19);
        }
        int n0 = ifd0 >= 0 ? U16((int)ifd0) : -1;
        for (int k = 0; k < n0 && k < 200; k++)
        {
            int e = (int)ifd0 + 2 + k * 12; int tag = U16(e);
            if (tag == 0x8769) exif = U32(e + 8);
        }
        if (exif > 0 && exif < t.Length)
        {
            int n1 = U16((int)exif);
            for (int k = 0; k < n1 && k < 300; k++)
            {
                int e = (int)exif + 2 + k * 12; int tag = U16(e);
                if (tag == 0x9003) { var s = Ascii(e); if (Parse(s) is long v) return v; }
                if (tag == 0x9004) fallback ??= Ascii(e);
            }
        }
        return Parse(fallback);
    }

    private static long? Parse(string? s)
    {
        if (s is null || s.Length < 19) return null;
        if (!DateTime.TryParseExact(s[..19], "yyyy:MM:dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var d)) return null;
        if (d.Year < 1980 || d > DateTime.Now.AddYears(1)) return null;
        return new DateTimeOffset(d).ToUnixTimeSeconds();
    }

    private static int ReadFull(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length) { int n = s.Read(buf, got, buf.Length - got); if (n <= 0) break; got += n; }
        return got;
    }

    /// <summary>
    /// Reads the taken date of every JPEG that hasn't been read yet (in folder order, which keeps a hard drive reading forwards).
    /// Other files are marked as having none without being opened. Waits while <paramref name="hold"/> says so. Returns how many changed.
    /// </summary>
    public static int Run(IndexDb db, WorkProgress progress, Func<bool> hold, CancellationToken ct)
    {
        using var c = db.Open();
        var todo = new List<(long id, string path)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path FROM files WHERE taken=0 AND missing_since=0 AND kind=0 ORDER BY path";
            using var r = cmd.ExecuteReader();
            while (r.Read()) { var p = r.GetString(1); if (Jpeg.Contains(Path.GetExtension(p))) todo.Add((r.GetInt64(0), p)); }
        }
        int changed = 0;
        // nothing to read in videos, GIFs, PNGs and the rest
        changed += c.ExecuteCount("UPDATE files SET taken=-1 WHERE taken=0 AND missing_since=0 AND NOT (kind=0 AND (lower(path) LIKE '%.jpg' OR lower(path) LIKE '%.jpeg' OR lower(path) LIKE '%.jfif'))");
        if (todo.Count == 0) return changed;
        progress.Start("Reading the dates photos were taken", todo.Count, 0);
        var batch = new List<(long id, long v)>();
        try
        {
            foreach (var (id, path) in todo)
            {
                while (hold()) { ct.ThrowIfCancellationRequested(); Thread.Sleep(200); }
                ct.ThrowIfCancellationRequested();
                progress.Current = path;
                long v; try { v = Read(path) ?? -1; } catch { v = -1; }
                batch.Add((id, v));
                Interlocked.Increment(ref progress.FilesDone);
                if (batch.Count >= 500) { changed += Save(c, batch); batch.Clear(); }
            }
        }
        finally { changed += Save(c, batch); progress.Running = false; progress.Current = ""; }
        return changed;
    }

    private static int Save(SqliteConnection c, List<(long id, long v)> rows)
    {
        if (rows.Count == 0) return 0;
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "UPDATE files SET taken=$v WHERE id=$id";
        var pv = cmd.Parameters.Add("$v", SqliteType.Integer); var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var r in rows) { pv.Value = r.v; pi.Value = r.id; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return rows.Count;
    }
}

/// <summary>
/// Finds files that are exact copies of each other anywhere in the library, so normal views can show each picture once: files of
/// the same size get a partial hash, and only those whose partial hashes match are read in full. Hashes stay until a file changes.
/// </summary>
public static class DuplicateHashes
{
    /// <summary>Returns how many files gained a full hash (0 = nothing new).</summary>
    public static int Run(IndexDb db, WorkProgress progress, Func<bool> hold, CancellationToken ct)
    {
        using var c = db.Open();
        // 1. partial hashes for every file that shares its size with another
        var partial = new List<(long id, string path, long size)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, size FROM files WHERE missing_since=0 AND phash=0 AND size>0 AND size IN " +
                              "(SELECT size FROM files WHERE missing_since=0 AND size>0 GROUP BY size HAVING COUNT(*)>1) ORDER BY path";
            using var r = cmd.ExecuteReader();
            while (r.Read()) partial.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }
        if (partial.Count > 0)
        {
            progress.Start("Looking for copies of the same file (quick check)", partial.Count, 0);
            Hash(c, "phash", partial, progress, hold, ct, full: false);
        }
        // 2. full hashes where size and partial hash agree
        var full = new List<(long id, string path, long size)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, size FROM files WHERE missing_since=0 AND fhash=0 AND phash<>0 AND (size, phash) IN " +
                              "(SELECT size, phash FROM files WHERE missing_since=0 AND phash<>0 GROUP BY size, phash HAVING COUNT(*)>1) ORDER BY path";
            using var r = cmd.ExecuteReader();
            while (r.Read()) full.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2)));
        }
        if (full.Count == 0) return 0;
        progress.Start("Confirming copies of the same file", full.Count, full.Sum(f => f.size));
        return Hash(c, "fhash", full, progress, hold, ct, full: true);
    }

    private static int Hash(SqliteConnection c, string col, List<(long id, string path, long size)> rows, WorkProgress progress, Func<bool> hold, CancellationToken ct, bool full)
    {
        var batch = new List<(long id, long size, long h)>();
        int saved = 0; long before = 0;
        try
        {
            foreach (var (id, path, size) in rows)
            {
                while (hold()) { ct.ThrowIfCancellationRequested(); Thread.Sleep(200); }
                ct.ThrowIfCancellationRequested();
                progress.Current = path;
                try
                {
                    long b0 = before;
                    long h = full ? ContentHash.Full(path, n => Interlocked.Exchange(ref progress.BytesDone, b0 + n), ct) : ContentHash.Partial(path);
                    batch.Add((id, size, h));
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                before += size;
                if (full) Interlocked.Exchange(ref progress.BytesDone, before);
                Interlocked.Increment(ref progress.FilesDone);
                if (batch.Count >= (full ? 50 : 500)) { saved += Save(c, col, batch); batch.Clear(); }
            }
        }
        finally { saved += Save(c, col, batch); progress.Running = false; progress.Current = ""; }
        return saved;
    }

    private static int Save(SqliteConnection c, string col, List<(long id, long size, long h)> rows)
    {
        if (rows.Count == 0) return 0;
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE files SET {col}=$h WHERE id=$id AND size=$s";
        var ph = cmd.Parameters.Add("$h", SqliteType.Integer); var pi = cmd.Parameters.Add("$id", SqliteType.Integer); var ps = cmd.Parameters.Add("$s", SqliteType.Integer);
        foreach (var r in rows) { ph.Value = r.h; pi.Value = r.id; ps.Value = r.size; cmd.ExecuteNonQuery(); }
        tx.Commit();
        return rows.Count;
    }
}

internal static class SqliteExt
{
    public static int ExecuteCount(this SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand(); cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }
}
