using Ginkgo.Core;
using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

/// <param name="Removed">How many files were newly hidden because they could not be found (they are kept until deletion is confirmed).</param>
public sealed record ScanResult(int Added, int Changed, int Removed, int Total, double Seconds,
    int Moved = 0, int Restored = 0, int Purged = 0, int Deferred = 0,
    bool SafetyTripped = false, bool RootMissing = false, int WouldHide = 0);

public sealed class ScanOptions
{
    /// <summary>Gives files and folders an identity that survives moves; null = path only.</summary>
    public IFileIdProvider? FileIds { get; init; } = FileIdProviders.Default;
    /// <summary>How long a missing file is kept (hidden) before its entry and thumbnails may be removed.</summary>
    public TimeSpan Grace { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Safety check: hiding at least <see cref="SafetyMinCount"/> files that are more than this share of the library is refused.</summary>
    public double SafetyFraction { get; init; } = 0.2;
    public int SafetyMinCount { get; init; } = 200;
    /// <summary>The person confirmed a large removal: hide it even though it trips the safety check.</summary>
    public bool AcceptLargeRemoval { get; init; }
    /// <summary>Only look at this folder (relative to the library). Null = the whole library.</summary>
    public string? SubFolder { get; init; }
    /// <summary>With <see cref="SubFolder"/>: include its subfolders.</summary>
    public bool Recursive { get; init; } = true;
    /// <summary>Files modified within this many seconds are left for later (they may still be being copied).</summary>
    public int SkipRecentSeconds { get; init; }
    public Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;
}

/// <summary>
/// Walks the library folder and brings the index in line with what is on disk. Never touches the files.
/// A file that can't be found is hidden, not forgotten: its entry and thumbnails stay for a grace period, and come back
/// if the file reappears (or is found elsewhere by its file ID).
/// </summary>
public static class LibraryScanner
{
    public static ScanResult Scan(IndexDb db, string root, IEnumerable<string> excludedTop,
        Action<int>? progress, CancellationToken ct, ScanOptions? options = null)
    {
        var opt = options ?? new ScanOptions();
        var started = DateTime.UtcNow;
        if (!Directory.Exists(root)) return new ScanResult(0, 0, 0, 0, 0, RootMissing: true);   // drive unplugged: change nothing
        var excluded = new HashSet<string>(excludedTop, StringComparer.OrdinalIgnoreCase);
        using var conn = db.Open();

        string sep = Path.DirectorySeparatorChar.ToString();
        string? scopeFull = null;
        string rel0 = (opt.SubFolder ?? "").Trim('\\', '/');
        bool whole = rel0.Length == 0 && (opt.SubFolder is null || opt.Recursive);
        if (!whole) scopeFull = Path.GetFullPath(Path.Combine(root, rel0)).TrimEnd(Path.DirectorySeparatorChar);
        bool InScope(string path)
        {
            if (scopeFull is null) return true;
            string dir = Path.GetDirectoryName(path) ?? "";
            return opt.Recursive
                ? dir.Equals(scopeFull, StringComparison.OrdinalIgnoreCase) || dir.StartsWith(scopeFull + sep, StringComparison.OrdinalIgnoreCase)
                : dir.Equals(scopeFull, StringComparison.OrdinalIgnoreCase);
        }

        // ---- what the index knows now (a scoped scan only loads its own part of the index)
        var existing = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, Row>();
        using (var cmd = conn.CreateCommand())
        {
            string cols = "SELECT id, path, size, mtime, file_id, missing_since, ctime FROM files";
            if (scopeFull is null) cmd.CommandText = cols;
            else if (rel0.Length == 0) cmd.CommandText = cols + " WHERE folder=''";
            else
            {
                cmd.CommandText = cols + " WHERE path LIKE $pfx ESCAPE '^'";
                cmd.Parameters.AddWithValue("$pfx", scopeFull.Replace("^", "^^").Replace("%", "^%").Replace("_", "^_") + sep + "%");
            }
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Row(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt64(6));
                if (!InScope(row.Path)) continue;
                existing[row.Path] = row;
                if (row.FileId is not null) byId.TryAdd(row.FileId, row);
            }
        }
        Row? FindById(string id)
        {
            if (scopeFull is null) return byId.GetValueOrDefault(id);
            using var cmd = conn.CreateCommand();      // the file may have come from outside this folder
            cmd.CommandText = "SELECT id, path, size, mtime, file_id, missing_since, ctime FROM files WHERE file_id=$f LIMIT 1";
            cmd.Parameters.AddWithValue("$f", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? new Row(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)) : null;
        }
        bool firstScan = existing.Count == 0;
        DateTime nowUtc = opt.UtcNow();
        long now = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();

        // ---- walk the disk
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newFiles = new List<(string full, long size, long mtime, MediaKind kind)>();
        var edits = new List<(long id, long size, long mtime)>();
        var restores = new List<long>();
        var created = new List<(long id, long ctime)>();          // creation dates the index doesn't have yet (or that changed)
        var createdOf = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int counted = 0, deferred = 0;

        void Visit(FileInfo fi)
        {
            if (!MediaTypes.TryGetKind(fi.Extension, out var kind)) return;
            string full = fi.FullName;
            seen.Add(full);
            long size = fi.Length, mtime = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
            long ctime; try { ctime = new DateTimeOffset(fi.CreationTimeUtc).ToUnixTimeSeconds(); } catch { ctime = mtime; }
            createdOf[full] = ctime;
            bool recent = opt.SkipRecentSeconds > 0 && (nowUtc - fi.LastWriteTimeUtc).TotalSeconds < opt.SkipRecentSeconds;
            if (existing.TryGetValue(full, out var e))
            {
                if (e.MissingSince > 0) restores.Add(e.Id);
                if (e.Ctime != ctime) created.Add((e.Id, ctime));
                if (e.Size != size || e.Mtime != mtime) { if (recent) deferred++; else edits.Add((e.Id, size, mtime)); }
            }
            else if (recent) deferred++;
            else newFiles.Add((full, size, mtime, kind));
            if (++counted % 2000 == 0) progress?.Invoke(counted);
        }

        var rootDir = new DirectoryInfo(root);
        var opts = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var deep = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        if (scopeFull is null)
        {
            foreach (var f in rootDir.EnumerateFiles("*", opts)) Visit(f);
            foreach (var top in rootDir.EnumerateDirectories("*", opts))
            {
                if (excluded.Contains(top.Name)) continue;
                ct.ThrowIfCancellationRequested();
                foreach (var f in top.EnumerateFiles("*", deep)) { Visit(f); if ((counted & 1023) == 0) ct.ThrowIfCancellationRequested(); }
            }
        }
        else
        {
            string firstSeg = rel0.Split('\\', '/')[0];
            var dir = new DirectoryInfo(scopeFull);
            if (!(rel0.Length > 0 && excluded.Contains(firstSeg)) && dir.Exists)
                foreach (var f in dir.EnumerateFiles("*", opt.Recursive ? deep : opts)) { Visit(f); if ((counted & 1023) == 0) ct.ThrowIfCancellationRequested(); }
        }

        // ---- new paths: moved files are recognised by their file ID
        var ids = new string?[newFiles.Count];
        if (opt.FileIds is not null && newFiles.Count > 0)
            Parallel.For(0, newFiles.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                i => ids[i] = opt.FileIds.GetFileId(newFiles[i].full, false));
        var inserts = new List<(string path, string folder, string top, long size, long mtime, MediaKind kind, ContentLevel level, string? id)>();
        var moves = new List<(Row Old, string path, string folder, string top, long size, long mtime, ContentLevel level, string? id)>();
        var claimed = new HashSet<long>();
        for (int i = 0; i < newFiles.Count; i++)
        {
            var nf = newFiles[i];
            string folder = RelFolder(root, nf.full, out string top);
            Row? old = ids[i] is null ? null : FindById(ids[i]!);
            if (old is not null && !claimed.Contains(old.Id)
                && !old.Path.Equals(nf.full, StringComparison.OrdinalIgnoreCase) && !seen.Contains(old.Path) && !File.Exists(old.Path))
            {
                claimed.Add(old.Id);
                moves.Add((old, nf.full, folder, top, nf.size, nf.mtime, ContentClassifier.ClassifyRelativeFolder(folder), ids[i]));
            }
            else inserts.Add((nf.full, folder, top, nf.size, nf.mtime, nf.kind, ContentClassifier.ClassifyRelativeFolder(folder), ids[i]));
        }

        // ---- what disappeared
        var gone = new List<Row>();
        int activeInScope = 0;
        foreach (var row in existing.Values)
        {
            if (!InScope(row.Path)) continue;
            if (row.MissingSince == 0) activeInScope++;
            if (!seen.Contains(row.Path) && !claimed.Contains(row.Id) && row.MissingSince == 0) gone.Add(row);
        }
        bool trip = !opt.AcceptLargeRemoval && activeInScope > 0 &&
            ((gone.Count >= opt.SafetyMinCount && gone.Count > opt.SafetyFraction * activeInScope) || (scopeFull is null && seen.Count == 0));
        int wouldHide = trip ? gone.Count : 0;

        // ---- write it all
        using (var tx = conn.BeginTransaction())
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR IGNORE INTO files(path,folder,top,size,mtime,kind,level,first_seen,state,file_id,ctime) VALUES($p,$f,$t,$s,$m,$k,$l,$fs,0,$id,$c)";
                var p = cmd.Parameters;
                var pp = p.Add("$p", SqliteType.Text); var pf = p.Add("$f", SqliteType.Text); var pt = p.Add("$t", SqliteType.Text);
                var ps = p.Add("$s", SqliteType.Integer); var pm = p.Add("$m", SqliteType.Integer);
                var pk = p.Add("$k", SqliteType.Integer); var pl = p.Add("$l", SqliteType.Integer); var pfs = p.Add("$fs", SqliteType.Integer);
                var pid = p.Add("$id", SqliteType.Text);
                var pc = p.Add("$c", SqliteType.Integer);
                foreach (var i in inserts)
                {
                    pc.Value = createdOf.TryGetValue(i.path, out long ct0) ? ct0 : i.mtime;
                    pp.Value = i.path; pf.Value = i.folder; pt.Value = i.top; ps.Value = i.size; pm.Value = i.mtime;
                    pk.Value = (int)i.kind; pl.Value = (int)i.level;
                    pfs.Value = firstScan ? i.mtime : now; // very first scan: use the file date so "newest first" means something
                    pid.Value = (object?)i.id ?? DBNull.Value;
                    cmd.ExecuteNonQuery();
                }
            }

            // a moved file keeps its row: thumbnails, first-seen date and everything else stay; its place and content level are redone
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET path=$p, folder=$f, top=$t, level=$l, missing_since=0 WHERE id=$id";
                var pp = cmd.Parameters.Add("$p", SqliteType.Text); var pf = cmd.Parameters.Add("$f", SqliteType.Text);
                var pt = cmd.Parameters.Add("$t", SqliteType.Text); var pl = cmd.Parameters.Add("$l", SqliteType.Integer);
                var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var m in moves)
                {
                    pp.Value = m.path; pf.Value = m.folder; pt.Value = m.top; pl.Value = (int)m.level; pi.Value = m.Old.Id;
                    cmd.ExecuteNonQuery();
                    if (m.Old.Size != m.size || m.Old.Mtime != m.mtime) edits.Add((m.Old.Id, m.size, m.mtime));
                }
            }

            // an edited file keeps showing its old picture while a new one is made; its sharp version is forgotten
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET size=$s, mtime=$m, l_pack=0, l_off=0, l_len=0, phash=0, fhash=0, taken=0, vhash=0, " +
                                  "state=CASE state WHEN 1 THEN 3 WHEN 2 THEN 0 ELSE state END WHERE id=$id";
                var ps = cmd.Parameters.Add("$s", SqliteType.Integer); var pm = cmd.Parameters.Add("$m", SqliteType.Integer);
                var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var c in edits) { ps.Value = c.size; pm.Value = c.mtime; pi.Value = c.id; cmd.ExecuteNonQuery(); }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET ctime=$c WHERE id=$id";
                var pc = cmd.Parameters.Add("$c", SqliteType.Integer); var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var c in created) { pc.Value = c.ctime; pi.Value = c.id; cmd.ExecuteNonQuery(); }
                // a moved file keeps its row: its creation date comes from its new place
                foreach (var m in moves) if (createdOf.TryGetValue(m.path, out long mc)) { pc.Value = mc; pi.Value = m.Old.Id; cmd.ExecuteNonQuery(); }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET missing_since=0 WHERE id=$id";
                var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var id in restores) { pi.Value = id; cmd.ExecuteNonQuery(); }
            }

            if (!trip)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "UPDATE files SET missing_since=$now WHERE id=$id AND missing_since=0";
                cmd.Parameters.AddWithValue("$now", now);
                var pi = cmd.Parameters.Add("$id", SqliteType.Integer);
                foreach (var g in gone) { pi.Value = g.Id; cmd.ExecuteNonQuery(); }
            }

            // confirmed gone: missing for longer than the grace period, and only judged on a trustworthy full scan
            int purged = 0;
            if (!trip && scopeFull is null)
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "DELETE FROM files WHERE missing_since>0 AND missing_since<$cut";
                cmd.Parameters.AddWithValue("$cut", now - (long)opt.Grace.TotalSeconds);
                purged = cmd.ExecuteNonQuery();
            }

            UpdateFolders(conn, tx, root, opt, seen, scopeFull is null && !trip, now, excluded);
            tx.Commit();
            progress?.Invoke(counted);
            return new ScanResult(inserts.Count, edits.Count, trip ? 0 : gone.Count, seen.Count, (DateTime.UtcNow - started).TotalSeconds,
                Moved: moves.Count, Restored: restores.Count, Purged: purged, Deferred: deferred, SafetyTripped: trip, WouldHide: wouldHide);
        }
    }

    private sealed record Row(long Id, string Path, long Size, long Mtime, string? FileId, long MissingSince, long Ctime);

    private static string RelFolder(string root, string full, out string top)
    {
        string rel = Path.GetRelativePath(root, full);
        string folder = Path.GetDirectoryName(rel) ?? "";
        int cut = folder.IndexOfAny(new[] { '\\', '/' });
        top = cut < 0 ? folder : folder.Substring(0, cut);
        return folder;
    }

    /// <summary>Keeps one row per folder with its own identity, so a renamed or moved folder is still the same folder.</summary>
    private static void UpdateFolders(SqliteConnection conn, SqliteTransaction tx, string root, ScanOptions opt,
        HashSet<string> seenFiles, bool judgeMissing, long now, HashSet<string> excluded)
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in seenFiles)
        {
            string folder = RelFolder(root, f, out _);
            while (folder.Length > 0 && current.Add(folder))
                folder = Path.GetDirectoryName(folder) ?? "";
        }
        var rows = new Dictionary<string, (long id, string? fid, long missing)>(StringComparer.OrdinalIgnoreCase);
        var byId = new Dictionary<string, string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, path, file_id, missing_since FROM folders";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                string? fid = r.IsDBNull(2) ? null : r.GetString(2);
                rows[r.GetString(1)] = (r.GetInt64(0), fid, r.GetInt64(3));
                if (fid is not null) byId.TryAdd(fid, r.GetString(1));
            }
        }
        using var ins = conn.CreateCommand(); ins.Transaction = tx;
        ins.CommandText = "INSERT OR IGNORE INTO folders(path,file_id) VALUES($p,$f)";
        var ip = ins.Parameters.Add("$p", SqliteType.Text); var ifid = ins.Parameters.Add("$f", SqliteType.Text);
        using var ren = conn.CreateCommand(); ren.Transaction = tx;
        ren.CommandText = "UPDATE folders SET path=$p, missing_since=0 WHERE path=$old";
        var rp = ren.Parameters.Add("$p", SqliteType.Text); var ro = ren.Parameters.Add("$old", SqliteType.Text);
        using var back = conn.CreateCommand(); back.Transaction = tx;
        back.CommandText = "UPDATE folders SET missing_since=0 WHERE path=$p";
        var bp = back.Parameters.Add("$p", SqliteType.Text);
        var renamedFrom = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in current.OrderBy(x => x.Length))
        {
            if (rows.TryGetValue(folder, out var row)) { if (row.missing > 0) { bp.Value = folder; back.ExecuteNonQuery(); } continue; }
            string? fid = opt.FileIds?.GetFileId(Path.Combine(root, folder), true);
            if (fid is not null && byId.TryGetValue(fid, out var oldPath) && !current.Contains(oldPath) && !renamedFrom.Contains(oldPath)
                && !Directory.Exists(Path.Combine(root, oldPath)))
            {
                rp.Value = folder; ro.Value = oldPath; ren.ExecuteNonQuery();      // same folder, new place: keeps its identity
                renamedFrom.Add(oldPath);
            }
            else { ip.Value = folder; ifid.Value = (object?)fid ?? DBNull.Value; ins.ExecuteNonQuery(); }
        }

        if (!judgeMissing) return;
        using var miss = conn.CreateCommand(); miss.Transaction = tx;
        miss.CommandText = "UPDATE folders SET missing_since=$now WHERE path=$p AND missing_since=0";
        miss.Parameters.AddWithValue("$now", now);
        var mp = miss.Parameters.Add("$p", SqliteType.Text);
        foreach (var kv in rows)
            if (!current.Contains(kv.Key) && !renamedFrom.Contains(kv.Key) && kv.Value.missing == 0) { mp.Value = kv.Key; miss.ExecuteNonQuery(); }
        using var del = conn.CreateCommand(); del.Transaction = tx;
        del.CommandText = "DELETE FROM folders WHERE missing_since>0 AND missing_since<$cut";
        del.Parameters.AddWithValue("$cut", now - (long)opt.Grace.TotalSeconds);
        del.ExecuteNonQuery();
    }
}
