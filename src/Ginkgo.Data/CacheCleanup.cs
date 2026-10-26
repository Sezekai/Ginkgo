using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

public sealed record TierUsage(string Name, long OnDisk, long InUse)
{
    public long Wasted => Math.Max(0, OnDisk - InUse);
}

public sealed record CompactResult(long BytesBefore, long BytesAfter, int Entries);

/// <summary>
/// Thumbnails are appended to big pack files and never edited, so thumbnails of files that were purged leave dead space.
/// Clean-up copies what is still in use into fresh packs, points the index at the copies, then deletes the old packs.
/// Nothing is capped and nothing in use is removed: only space belonging to files confirmed deleted is given back.
/// </summary>
public static class CacheCleanup
{
    private static readonly (string name, string pack, string off, string len)[] Tiers =
    {
        ("Micro", "m_pack", "m_off", "m_len"), ("Small", "s_pack", "s_off", "s_len"),
        ("Full", "thumb_pack", "thumb_off", "thumb_len"), ("Sharp", "l_pack", "l_off", "l_len"),
    };

    private static ThumbStore StoreFor(ThumbStores s, string name) => name switch
    { "Micro" => s.Micro, "Small" => s.Small, "Full" => s.Full, _ => s.Large };

    public static IReadOnlyList<TierUsage> Measure(IndexDb db, ThumbStores stores)
    {
        using var c = db.Open();
        var list = new List<TierUsage>();
        foreach (var t in Tiers)
            list.Add(new TierUsage(t.name, StoreFor(stores, t.name).TotalBytes(), IndexDb.Scalar(c, $"SELECT COALESCE(SUM({t.len}),0) FROM files")));
        return list;
    }

    /// <summary>
    /// Compacts every tier whose dead space is at least <paramref name="minWasteFraction"/> of its size.
    /// Safe to run while the app is open, but anything holding thumbnail locations in memory must reload them from the index afterwards.
    /// </summary>
    public static CompactResult Compact(IndexDb db, ThumbStores stores, double minWasteFraction, Action<string>? progress, CancellationToken ct)
    {
        long before = 0, after = 0; int entries = 0;
        foreach (var t in Tiers)
        {
            ct.ThrowIfCancellationRequested();
            var store = StoreFor(stores, t.name);
            long disk = store.TotalBytes();
            before += disk;
            using var c = db.Open();
            long live = IndexDb.Scalar(c, $"SELECT COALESCE(SUM({t.len}),0) FROM files");
            if (disk == 0 || disk - live < minWasteFraction * disk) { after += disk; continue; }
            progress?.Invoke($"Compacting {t.name} thumbnails");

            var rows = new List<(long id, int pack, long off, int len)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"SELECT id, {t.pack}, {t.off}, {t.len} FROM files WHERE {t.len}>0 ORDER BY {t.pack}, {t.off}";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3)));
            }
            store.Flush();
            int first = store.RollToNewPack();
            for (int at = 0; at < rows.Count; at += 2000)
            {
                ct.ThrowIfCancellationRequested();
                int n = Math.Min(2000, rows.Count - at);
                var moved = new List<(long id, int oldPack, long oldOff, int pack, long off)>(n);
                for (int i = at; i < at + n; i++)
                {
                    var row = rows[i];
                    var bytes = store.Read(row.pack, row.off, row.len);
                    if (bytes is null) continue;
                    var loc = store.Append(bytes);
                    moved.Add((row.id, row.pack, row.off, loc.pack, loc.offset));
                }
                store.Flush();
                using var tx = c.BeginTransaction();
                using var up = c.CreateCommand();
                up.Transaction = tx;
                // only if nobody changed this entry meanwhile
                up.CommandText = $"UPDATE files SET {t.pack}=$np, {t.off}=$no WHERE id=$id AND {t.pack}=$op AND {t.off}=$oo";
                var pnp = up.Parameters.Add("$np", SqliteType.Integer); var pno = up.Parameters.Add("$no", SqliteType.Integer);
                var pid = up.Parameters.Add("$id", SqliteType.Integer); var pop = up.Parameters.Add("$op", SqliteType.Integer);
                var poo = up.Parameters.Add("$oo", SqliteType.Integer);
                foreach (var m in moved) { pnp.Value = m.pack; pno.Value = m.off; pid.Value = m.id; pop.Value = m.oldPack; poo.Value = m.oldOff; up.ExecuteNonQuery(); }
                tx.Commit();
                entries += moved.Count;
            }
            store.DeletePacksBefore(first);
            after += store.TotalBytes();
        }
        return new CompactResult(before, after, entries);
    }
}
