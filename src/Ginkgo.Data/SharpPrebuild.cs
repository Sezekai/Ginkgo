using System.Diagnostics;
using System.Threading.Channels;

namespace Ginkgo.Data;

/// <summary>
/// Makes the sharp (1280 px) version ahead of time for the originals that are slowest to open: every PNG
/// (they cannot be decoded at reduced size) and every file over a size limit. Runs in the background, one drive-friendly
/// pass, pausing while the person scrolls.
/// </summary>
public static class SharpPrebuild
{
    public const long DefaultBigFileBytes = 3L * 1024 * 1024;
    private const string Where = "state IN (1,3) AND missing_since=0 AND kind=0 AND l_len=0 AND w>340 AND (lower(path) LIKE '%.png' OR size>=$big)";

    /// <summary>How many originals qualify, and their total size.</summary>
    public static (long count, long bytes) Count(IndexDb db, long bigBytes)
    {
        using var c = db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*), COALESCE(SUM(size),0) FROM files WHERE {Where}";
        cmd.Parameters.AddWithValue("$big", bigBytes);
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1));
    }

    public static async Task RunAsync(IndexDb db, ThumbStores stores, PipelineStats stats, long bigBytes, long capBytes, int workers, CancellationToken ct)
    {
        var todo = new List<(long id, string path)>();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, path FROM files WHERE {Where} ORDER BY first_seen DESC, mtime DESC";
            cmd.Parameters.AddWithValue("$big", bigBytes);
            using var r = cmd.ExecuteReader();
            while (r.Read()) todo.Add((r.GetInt64(0), r.GetString(1)));
        }
        stats.SharpTotal = todo.Count; stats.SharpDone = 0; stats.SharpFailed = 0; stats.SharpRunning = true;

        var channel = Channel.CreateBounded<(long id, int pack, long off, int len)>(new BoundedChannelOptions(500) { SingleReader = true });
        var writer = Task.Run(async () =>
        {
            var batch = new List<(long id, int pack, long off, int len)>(200);
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < 200 && channel.Reader.TryRead(out var o)) batch.Add(o);
                if (batch.Count >= 200 || !channel.Reader.TryPeek(out _)) { IndexUpdates.SaveLarge(db, stores, batch); batch.Clear(); }
            }
            if (batch.Count > 0) IndexUpdates.SaveLarge(db, stores, batch);
        });

        try
        {
            await Task.Run(() =>
            {
                int next = -1;
                var threads = new List<Thread>();
                for (int w = 0; w < workers; w++)
                {
                    var t = new Thread(() =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            int idx = Interlocked.Increment(ref next);
                            if (idx >= todo.Count) break;
                            if (stores.Large.Size > capBytes) { stats.LastError = "sharp cache size limit reached"; break; }
                            stats.WaitForQuiet(ct);
                            var item = todo[idx];
                            try
                            {
                                var bytes = ThumbMaker.MakeLarge(item.path);
                                var loc = stores.Large.Append(bytes);
                                channel.Writer.WriteAsync((item.id, loc.pack, loc.offset, loc.length), ct).AsTask().GetAwaiter().GetResult();
                                Interlocked.Increment(ref stats.SharpDone);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex)
                            {
                                stats.LastError = "sharp " + Path.GetFileName(item.path) + ": " + ex.Message;
                                Interlocked.Increment(ref stats.SharpFailed);
                            }
                        }
                    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "sharp-prebuild" };
                    threads.Add(t); t.Start();
                }
                foreach (var t in threads) t.Join();
            }).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.Complete();
            await writer.ConfigureAwait(false);
            stats.SharpRunning = false;
        }
    }
}
