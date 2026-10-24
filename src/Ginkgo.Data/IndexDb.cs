using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

/// <summary>The SQLite index of every media file. One connection per use; WAL mode so reading never blocks writing.</summary>
public sealed class IndexDb
{
    public string Path { get; }

    public IndexDb(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS files (
              id INTEGER PRIMARY KEY,
              path TEXT NOT NULL UNIQUE,
              folder TEXT NOT NULL,
              top TEXT NOT NULL,
              size INTEGER NOT NULL,
              mtime INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              level INTEGER NOT NULL,
              first_seen INTEGER NOT NULL,
              w INTEGER NOT NULL DEFAULT 0,
              h INTEGER NOT NULL DEFAULT 0,
              tw INTEGER NOT NULL DEFAULT 0,
              th INTEGER NOT NULL DEFAULT 0,
              thumb_pack INTEGER NOT NULL DEFAULT 0,
              thumb_off INTEGER NOT NULL DEFAULT 0,
              thumb_len INTEGER NOT NULL DEFAULT 0,
              state INTEGER NOT NULL DEFAULT 0   -- 0 waiting for thumbnail, 1 done, 2 failed
            );
            CREATE INDEX IF NOT EXISTS ix_files_state ON files(state);
            CREATE INDEX IF NOT EXISTS ix_files_seen ON files(first_seen DESC, mtime DESC);
            """);
        // added later: the other thumbnail sizes (small = 192 px, micro = 112 px, large = up to 960 px, made on demand)
        var have = new HashSet<string>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(files)";
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(1));
        }
        foreach (var col in new[] { "s_pack", "s_off", "s_len", "m_pack", "m_off", "m_len", "l_pack", "l_off", "l_len" })
            if (!have.Contains(col)) Exec(c, $"ALTER TABLE files ADD COLUMN {col} INTEGER NOT NULL DEFAULT 0");
    }

    public SqliteConnection Open()
    {
        var c = new SqliteConnection($"Data Source={Path}");
        c.Open();
        Exec(c, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=15000;");
        return c;
    }

    public static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? 0 : Convert.ToInt64(o);
    }
}

public static class IndexUpdates
{
    /// <summary>Records where on-demand large thumbnails were saved.</summary>
    public static void SaveLarge(IndexDb db, ThumbStores stores, IReadOnlyList<(long id, int pack, long off, int len)> rows)
    {
        if (rows.Count == 0) return;
        stores.Large.Flush(); // the files first, then the index
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE files SET l_pack=$p, l_off=$o, l_len=$l WHERE id=$id";
        var pp = cmd.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Integer);
        var po = cmd.Parameters.Add("$o", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pl = cmd.Parameters.Add("$l", Microsoft.Data.Sqlite.SqliteType.Integer);
        var pi = cmd.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
        foreach (var r in rows) { pp.Value = r.pack; po.Value = r.off; pl.Value = r.len; pi.Value = r.id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }
}
