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
