using Ginkgo.Core;
using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

public sealed record ScanResult(int Added, int Changed, int Removed, int Total, double Seconds);

/// <summary>Walks the library folder and brings the index in line with what is on disk. Never touches the files.</summary>
public static class LibraryScanner
{
    public static ScanResult Scan(IndexDb db, string root, IEnumerable<string> excludedTop,
        Action<int>? progress, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var excluded = new HashSet<string>(excludedTop, StringComparer.OrdinalIgnoreCase);
        using var conn = db.Open();

        var existing = new Dictionary<string, (long id, long size, long mtime)>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, path, size, mtime FROM files";
            using var r = cmd.ExecuteReader();
            while (r.Read()) existing[r.GetString(1)] = (r.GetInt64(0), r.GetInt64(2), r.GetInt64(3));
        }
        bool firstScan = existing.Count == 0;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inserts = new List<(string path, string folder, string top, long size, long mtime, MediaKind kind, ContentLevel level)>();
        var changes = new List<(long id, long size, long mtime)>();
        int added = 0, changed = 0, counted = 0;

        void FlushInserts()
        {
            if (inserts.Count == 0) return;
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT OR IGNORE INTO files(path,folder,top,size,mtime,kind,level,first_seen,state) VALUES($p,$f,$t,$s,$m,$k,$l,$fs,0)";
            var p = cmd.Parameters;
            var pp = p.Add("$p", SqliteType.Text); var pf = p.Add("$f", SqliteType.Text); var pt = p.Add("$t", SqliteType.Text);
            var ps = p.Add("$s", SqliteType.Integer); var pm = p.Add("$m", SqliteType.Integer);
            var pk = p.Add("$k", SqliteType.Integer); var pl = p.Add("$l", SqliteType.Integer); var pfs = p.Add("$fs", SqliteType.Integer);
            foreach (var i in inserts)
            {
                pp.Value = i.path; pf.Value = i.folder; pt.Value = i.top; ps.Value = i.size; pm.Value = i.mtime;
                pk.Value = (int)i.kind; pl.Value = (int)i.level;
                pfs.Value = firstScan ? i.mtime : now; // very first scan: use the file date so "newest first" means something
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            inserts.Clear();
        }

        void Visit(FileInfo fi)
        {
            if (!MediaTypes.TryGetKind(fi.Extension, out var kind)) return;
            string full = fi.FullName;
            seen.Add(full);
            long size = fi.Length, mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (existing.TryGetValue(full, out var e))
            {
                if (e.size != size || e.mtime != mtime) { changes.Add((e.id, size, mtime)); changed++; }
            }
            else
            {
                string rel = System.IO.Path.GetRelativePath(root, full);
                string folder = System.IO.Path.GetDirectoryName(rel) ?? "";
                int cut = folder.IndexOfAny(new[] { '\\', '/' });
                string top = cut < 0 ? folder : folder.Substring(0, cut);
                inserts.Add((full, folder, top, size, mtime, kind, ContentClassifier.ClassifyRelativeFolder(folder)));
                added++;
                if (inserts.Count >= 5000) FlushInserts();
            }
            if (++counted % 2000 == 0) progress?.Invoke(counted);
        }

        var rootDir = new DirectoryInfo(root);
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var f in rootDir.EnumerateFiles("*", opts)) Visit(f);
        foreach (var top in rootDir.EnumerateDirectories("*", opts))
        {
            if (excluded.Contains(top.Name)) continue;
            ct.ThrowIfCancellationRequested();
            var all = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
            foreach (var f in top.EnumerateFiles("*", all)) { Visit(f); if ((counted & 1023) == 0) ct.ThrowIfCancellationRequested(); }
        }
        FlushInserts();

        using (var tx = conn.BeginTransaction())
        {
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE files SET size=$s, mtime=$m, state=0 WHERE id=$id";
            var ps = upd.Parameters.Add("$s", SqliteType.Integer); var pm = upd.Parameters.Add("$m", SqliteType.Integer); var pi = upd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var c in changes) { ps.Value = c.size; pm.Value = c.mtime; pi.Value = c.id; upd.ExecuteNonQuery(); }

            int removed0 = 0;
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText = "DELETE FROM files WHERE id=$id";
            var di = del.Parameters.Add("$id", SqliteType.Integer);
            foreach (var kv in existing)
                if (!seen.Contains(kv.Key)) { di.Value = kv.Value.id; del.ExecuteNonQuery(); removed0++; }
            tx.Commit();
            progress?.Invoke(counted);
            return new ScanResult(added, changed, removed0, seen.Count, (DateTime.UtcNow - started).TotalSeconds);
        }
    }
}
