using System.Diagnostics;
using System.Threading.Channels;
using Ginkgo.Core;
using Microsoft.Data.Sqlite;

namespace Ginkgo.Data;

public sealed class PipelineStats
{
    public long Total;          // items to do when this run started
    public long Done;
    public long Failed;
    public long BackfillTotal;  // existing thumbnails still needing their smaller sizes
    public long BackfillDone;
    public long SharpTotal;     // originals to prepare sharp versions for in this run
    public long SharpDone;
    public long SharpFailed;
    public volatile bool SharpRunning;
    public string LastError = "";
    public volatile bool Running;
    /// <summary>Stopwatch timestamp of the last time the user scrolled; background work pauses briefly after it.</summary>
    public long LastUiActivity;

    internal void WaitForQuiet(CancellationToken ct)
    {
        long pause = (long)(Stopwatch.Frequency * 0.8);
        while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() - Volatile.Read(ref LastUiActivity) < pause)
            Thread.Sleep(40);
    }
}

public readonly record struct Loc(int Pack, long Off, int Len);

/// <summary>Makes thumbnails (three sizes) for every image that doesn't have them yet, newest first, and records them in the index.</summary>
public static class ThumbnailPipeline
{
    private readonly record struct Outcome(long Id, ThumbResult? Result, Loc Full, Loc Small, Loc Micro, double Seconds = 0);

    public static async Task RunAsync(IndexDb db, ThumbStores stores, PipelineStats stats, int workers, CancellationToken ct)
    {
        var todo = new List<(long id, string path, int kind)>();
        bool videos = VideoTools.Available;
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            // Videos and GIF animation come later; GIFs get a first-frame thumbnail now.
            // videos too when ffmpeg is there (a frame for the thumbnail, plus their length); otherwise they wait
            cmd.CommandText = "SELECT id, path, kind FROM files WHERE state IN (0,3) AND kind<" + (videos ? 3 : 2) + " AND missing_since=0 ORDER BY first_seen DESC, mtime DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) todo.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        }
        stats.Total = todo.Count; stats.Done = 0; stats.Failed = 0; stats.Running = true;

        var channel = Channel.CreateBounded<Outcome>(new BoundedChannelOptions(2000) { SingleReader = true });
        var writer = Task.Run(async () =>
        {
            var batch = new List<Outcome>(500);
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < 500 && channel.Reader.TryRead(out var o)) batch.Add(o);
                if (batch.Count >= 500 || !channel.Reader.TryPeek(out _)) { Commit(db, stores, batch); batch.Clear(); }
            }
            if (batch.Count > 0) Commit(db, stores, batch);
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
                            stats.WaitForQuiet(ct); // while the person is scrolling, leave the drive and processor to the viewer
                            var item = todo[idx];
                            Outcome o;
                            try
                            {
                                ThumbResult th; double secs = 0;
                                if (item.kind == (int)MediaKind.Video)
                                {
                                    var info = VideoTools.Probe(item.path);
                                    secs = info.Seconds;
                                    using var frame = VideoTools.Frame(item.path, info.Seconds, ThumbMaker.MaxW);
                                    th = ThumbMaker.MakeFrom(frame, info.Width, info.Height);
                                }
                                else th = ThumbMaker.Make(item.path);
                                var f = stores.Full.Append(th.Bytes); var s = stores.Small.Append(th.Small); var m = stores.Micro.Append(th.Micro);
                                o = new Outcome(item.id, th, new Loc(f.pack, f.offset, f.length), new Loc(s.pack, s.offset, s.length), new Loc(m.pack, m.offset, m.length), secs);
                                Interlocked.Increment(ref stats.Done);
                            }
                            catch (Exception ex)
                            {
                                stats.LastError = Path.GetFileName(item.path) + ": " + ex.Message;
                                o = new Outcome(item.id, null, default, default, default);
                                Interlocked.Increment(ref stats.Failed);
                            }
                            try { channel.Writer.WriteAsync(o, ct).AsTask().GetAwaiter().GetResult(); }
                            catch (OperationCanceledException) { break; }
                        }
                    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumb-make" };
                    threads.Add(t); t.Start();
                }
                foreach (var t in threads) t.Join();
            }).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.Complete();
            await writer.ConfigureAwait(false);
            stats.Running = false;
        }
    }

    private static void Commit(IndexDb db, ThumbStores stores, List<Outcome> batch)
    {
        stores.Flush(); // thumbnails must be in the files before the index says where they are
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var ok = c.CreateCommand();
        ok.Transaction = tx;
        ok.CommandText = "UPDATE files SET w=$w,h=$h,tw=$tw,th=$th,thumb_pack=$p,thumb_off=$o,thumb_len=$l," +
                         "s_pack=$sp,s_off=$so,s_len=$sl,m_pack=$mp,m_off=$mo,m_len=$ml,duration=$d,state=1 WHERE id=$id";
        var P = ok.Parameters;
        var pw = P.Add("$w", SqliteType.Integer); var ph = P.Add("$h", SqliteType.Integer);
        var ptw = P.Add("$tw", SqliteType.Integer); var pth = P.Add("$th", SqliteType.Integer);
        var pp = P.Add("$p", SqliteType.Integer); var po = P.Add("$o", SqliteType.Integer); var pl = P.Add("$l", SqliteType.Integer);
        var psp = P.Add("$sp", SqliteType.Integer); var pso = P.Add("$so", SqliteType.Integer); var psl = P.Add("$sl", SqliteType.Integer);
        var pmp = P.Add("$mp", SqliteType.Integer); var pmo = P.Add("$mo", SqliteType.Integer); var pml = P.Add("$ml", SqliteType.Integer);
        var pid = P.Add("$id", SqliteType.Integer);
        var pd = P.Add("$d", SqliteType.Real);
        using var bad = c.CreateCommand();
        bad.Transaction = tx;
        bad.CommandText = "UPDATE files SET state=2 WHERE id=$id";
        var bid = bad.Parameters.Add("$id", SqliteType.Integer);
        foreach (var o in batch)
        {
            if (o.Result is null) { bid.Value = o.Id; bad.ExecuteNonQuery(); continue; }
            pw.Value = o.Result.Width; ph.Value = o.Result.Height; ptw.Value = o.Result.ThumbWidth; pth.Value = o.Result.ThumbHeight;
            pp.Value = o.Full.Pack; po.Value = o.Full.Off; pl.Value = o.Full.Len;
            psp.Value = o.Small.Pack; pso.Value = o.Small.Off; psl.Value = o.Small.Len;
            pmp.Value = o.Micro.Pack; pmo.Value = o.Micro.Off; pml.Value = o.Micro.Len;
            pid.Value = o.Id; pd.Value = o.Seconds;
            ok.ExecuteNonQuery();
        }
        tx.Commit();
    }
}

/// <summary>Gives thumbnails made before the smaller sizes existed their small and micro versions, without touching the original pictures.</summary>
public static class TierBackfill
{
    private readonly record struct Outcome(long Id, Loc Small, Loc Micro);

    public static async Task RunAsync(IndexDb db, ThumbStores stores, PipelineStats stats, int workers, CancellationToken ct)
    {
        var todo = new List<(long id, int pack, long off, int len)>();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            // in pack order, so the drive reads along the file instead of jumping around
            cmd.CommandText = "SELECT id, thumb_pack, thumb_off, thumb_len FROM files WHERE state=1 AND missing_since=0 AND m_len=0 AND thumb_len>0 ORDER BY thumb_pack, thumb_off";
            using var r = cmd.ExecuteReader();
            while (r.Read()) todo.Add((r.GetInt64(0), r.GetInt32(1), r.GetInt64(2), r.GetInt32(3)));
        }
        stats.BackfillTotal = todo.Count; stats.BackfillDone = 0;
        if (todo.Count == 0) return;

        var channel = Channel.CreateBounded<Outcome>(new BoundedChannelOptions(2000) { SingleReader = true });
        var writer = Task.Run(async () =>
        {
            var batch = new List<Outcome>(1000);
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < 1000 && channel.Reader.TryRead(out var o)) batch.Add(o);
                if (batch.Count >= 1000 || !channel.Reader.TryPeek(out _)) { Commit(db, stores, batch); batch.Clear(); }
            }
            if (batch.Count > 0) Commit(db, stores, batch);
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
                            stats.WaitForQuiet(ct);
                            var item = todo[idx];
                            try
                            {
                                var full = stores.Full.Read(item.pack, item.off, item.len);
                                if (full is null) continue;
                                var (small, micro) = ThumbMaker.Derive(full);
                                var s = stores.Small.Append(small); var m = stores.Micro.Append(micro);
                                channel.Writer.WriteAsync(new Outcome(item.id, new Loc(s.pack, s.offset, s.length), new Loc(m.pack, m.offset, m.length)), ct).AsTask().GetAwaiter().GetResult();
                                Interlocked.Increment(ref stats.BackfillDone);
                            }
                            catch (OperationCanceledException) { break; }
                            catch (Exception ex) { stats.LastError = "backfill " + item.id + ": " + ex.Message; }
                        }
                    }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "thumb-backfill" };
                    threads.Add(t); t.Start();
                }
                foreach (var t in threads) t.Join();
            }).ConfigureAwait(false);
        }
        finally
        {
            channel.Writer.Complete();
            await writer.ConfigureAwait(false);
        }
    }

    private static void Commit(IndexDb db, ThumbStores stores, List<Outcome> batch)
    {
        stores.Flush();
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var cmd = c.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE files SET s_pack=$sp,s_off=$so,s_len=$sl,m_pack=$mp,m_off=$mo,m_len=$ml WHERE id=$id";
        var P = cmd.Parameters;
        var psp = P.Add("$sp", SqliteType.Integer); var pso = P.Add("$so", SqliteType.Integer); var psl = P.Add("$sl", SqliteType.Integer);
        var pmp = P.Add("$mp", SqliteType.Integer); var pmo = P.Add("$mo", SqliteType.Integer); var pml = P.Add("$ml", SqliteType.Integer);
        var pid = P.Add("$id", SqliteType.Integer);
        foreach (var o in batch)
        {
            psp.Value = o.Small.Pack; pso.Value = o.Small.Off; psl.Value = o.Small.Len;
            pmp.Value = o.Micro.Pack; pmo.Value = o.Micro.Off; pml.Value = o.Micro.Len;
            pid.Value = o.Id;
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
