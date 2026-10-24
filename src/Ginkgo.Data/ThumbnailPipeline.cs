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
    public string LastError = "";
    public volatile bool Running;
    /// <summary>Stopwatch timestamp of the last time the user scrolled; thumbnail making pauses briefly after it.</summary>
    public long LastUiActivity;
}

/// <summary>Makes a thumbnail for every image that doesn't have one yet, newest first, and records it in the index.</summary>
public static class ThumbnailPipeline
{
    private readonly record struct Outcome(long Id, ThumbResult? Result, int Pack, long Off, int Len);

    public static async Task RunAsync(IndexDb db, ThumbStore store, PipelineStats stats, int workers, CancellationToken ct)
    {
        var todo = new List<(long id, string path)>();
        using (var c = db.Open())
        using (var cmd = c.CreateCommand())
        {
            // Videos and GIF animation come later; GIFs get a first-frame thumbnail now.
            cmd.CommandText = "SELECT id, path FROM files WHERE state=0 AND kind<2 ORDER BY first_seen DESC, mtime DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read()) todo.Add((r.GetInt64(0), r.GetString(1)));
        }
        stats.Total = todo.Count; stats.Done = 0; stats.Failed = 0; stats.Running = true;

        var channel = Channel.CreateBounded<Outcome>(new BoundedChannelOptions(2000) { SingleReader = true });
        var writer = Task.Run(async () =>
        {
            var batch = new List<Outcome>(500);
            while (await channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < 500 && channel.Reader.TryRead(out var o)) batch.Add(o);
                if (batch.Count >= 500 || !channel.Reader.TryPeek(out _)) { Commit(db, store, batch); batch.Clear(); }
            }
            if (batch.Count > 0) Commit(db, store, batch);
        });

        try
        {
            await Task.Run(() =>
            {
                int next = -1;
                long pauseTicks = (long)(Stopwatch.Frequency * 0.8);
                var threads = new List<Thread>();
                for (int w = 0; w < workers; w++)
                {
                    var t = new Thread(() =>
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            int idx = Interlocked.Increment(ref next);
                            if (idx >= todo.Count) break;
                            // while the person is scrolling, leave the drive and processor to the viewer
                            while (!ct.IsCancellationRequested && Stopwatch.GetTimestamp() - Volatile.Read(ref stats.LastUiActivity) < pauseTicks)
                                Thread.Sleep(40);
                            var item = todo[idx];
                            Outcome o;
                            try
                            {
                                var th = ThumbMaker.Make(item.path);
                                var loc = store.Append(th.Bytes);
                                o = new Outcome(item.id, th, loc.pack, loc.offset, loc.length);
                                Interlocked.Increment(ref stats.Done);
                            }
                            catch (Exception ex)
                            {
                                stats.LastError = Path.GetFileName(item.path) + ": " + ex.Message;
                                o = new Outcome(item.id, null, 0, 0, 0);
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

    private static void Commit(IndexDb db, ThumbStore store, List<Outcome> batch)
    {
        store.Flush(); // thumbnails must be in the file before the index says where they are
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        using var ok = c.CreateCommand();
        ok.Transaction = tx;
        ok.CommandText = "UPDATE files SET w=$w,h=$h,tw=$tw,th=$th,thumb_pack=$p,thumb_off=$o,thumb_len=$l,state=1 WHERE id=$id";
        var pw = ok.Parameters.Add("$w", SqliteType.Integer); var ph = ok.Parameters.Add("$h", SqliteType.Integer);
        var ptw = ok.Parameters.Add("$tw", SqliteType.Integer); var pth = ok.Parameters.Add("$th", SqliteType.Integer);
        var pp = ok.Parameters.Add("$p", SqliteType.Integer); var po = ok.Parameters.Add("$o", SqliteType.Integer);
        var pl = ok.Parameters.Add("$l", SqliteType.Integer); var pid = ok.Parameters.Add("$id", SqliteType.Integer);
        using var bad = c.CreateCommand();
        bad.Transaction = tx;
        bad.CommandText = "UPDATE files SET state=2 WHERE id=$id";
        var bid = bad.Parameters.Add("$id", SqliteType.Integer);
        foreach (var o in batch)
        {
            if (o.Result is null) { bid.Value = o.Id; bad.ExecuteNonQuery(); continue; }
            pw.Value = o.Result.Width; ph.Value = o.Result.Height; ptw.Value = o.Result.ThumbWidth; pth.Value = o.Result.ThumbHeight;
            pp.Value = o.Pack; po.Value = o.Off; pl.Value = o.Len; pid.Value = o.Id;
            ok.ExecuteNonQuery();
        }
        tx.Commit();
    }
}
