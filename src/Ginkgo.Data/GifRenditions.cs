using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using BCnEncoder.Decoder;
using BCnEncoder.Encoder;
using BCnEncoder.Shared;
using Ginkgo.Core;
using K4os.Compression.LZ4;
using SkiaSharp;

namespace Ginkgo.Data;

/// <summary>
/// A GIF made ready for the GPU grid (build brief, "GIFs: renditions"): every frame flattened (disposal handled), resized to the
/// tier's size, put on a fixed frame clock, cut to the length limit, encoded in a format the graphics card draws directly (BC7, or
/// BC1 when nothing is see-through) and LZ4-compressed for the drive. The grid plays these, never the original files.
/// </summary>
public sealed class GifRendition
{
    public int W, H;            // the picture's size
    public int PW, PH;          // padded to whole 4 x 4 blocks
    public int Fps, Frames, LengthMs, SourceMs;
    public bool Cut, Bc7;
    public int FrameBytes;      // compressed blocks of one frame
    public byte[] Blocks = Array.Empty<byte>();   // all frames, one after another
}

public static class GifRenditions
{
    /// <summary>
    /// The tiers: small (micro and small tile sizes), large (full size and up), and extra large (960 px, for big tiles when there are
    /// only a few columns; made only for the GIFs shown that big).
    /// </summary>
    public static readonly (int edge, int fps)[] Tiers = { (192, 10), (320, 15), (960, 15) };
    /// <summary>Tiers made for every GIF in the background (the others on demand).</summary>
    public const int BackfillTiers = 2;
    private const uint Magic = 0x314E5247;   // "GRN1"

    /// <summary>Like browsers: a delay of 0-10 ms plays as 100 ms.</summary>
    public static int Delay(int ms) => ms <= 10 ? 100 : ms;

    /// <summary>
    /// The fixed frame clock: for each output frame, which source frame shows. The loop is cut at <paramref name="capMs"/> when the
    /// GIF is longer (<paramref name="cut"/>).
    /// </summary>
    public static int[] Timeline(IReadOnlyList<int> delays, int fps, int capMs, out bool cut, out int lengthMs, out int sourceMs)
    {
        var ends = new int[delays.Count];
        int t = 0;
        for (int i = 0; i < delays.Count; i++) { t += Delay(delays[i]); ends[i] = t; }
        sourceMs = t;
        cut = t > capMs;
        lengthMs = cut ? capMs : t;
        int n = Math.Max(1, (int)Math.Round(lengthMs * fps / 1000.0));
        var map = new int[n];
        int s = 0;
        for (int k = 0; k < n; k++)
        {
            double at = k * 1000.0 / fps;
            while (s < ends.Length - 1 && ends[s] <= at) s++;
            map[k] = s;
        }
        return map;
    }

    /// <summary>
    /// The GIF's frames on the fixed clock, flattened and resized so the long edge is <paramref name="edge"/> (never enlarged).
    /// Premultiplied BGRA. Null for a GIF with a single frame (nothing to play).
    /// </summary>
    public static (List<byte[]> frames, int w, int h, bool alpha, bool cut, int lengthMs, int sourceMs)? Frames(Stream stream, int edge, int fps, int capMs, CancellationToken ct)
    {
        using var codec = SKCodec.Create(stream);
        if (codec is null) throw new InvalidDataException("not a picture SkiaSharp can read");
        int count = codec.FrameCount;
        if (count <= 1) return null;
        var infos = codec.FrameInfo;
        var map = Timeline(infos.Select(f => f.Duration).ToList(), fps, capMs, out bool cut, out int lengthMs, out int sourceMs);
        int lastNeeded = map.Max();
        int sw = codec.Info.Width, sh = codec.Info.Height;
        double scale = Math.Min(1.0, edge / (double)Math.Max(sw, sh));
        int w = Math.Max(1, (int)Math.Round(sw * scale)), h = Math.Max(1, (int)Math.Round(sh * scale));

        // frames that later frames are drawn on top of (other than the one just before) are kept
        var keep = new Dictionary<int, int>();          // frame -> last frame that needs it
        for (int f = 0; f <= lastNeeded; f++) { int r = infos[f].RequiredFrame; if (r >= 0 && r != f - 1) keep[r] = f; }
        var saved = new Dictionary<int, byte[]>();
        var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKBitmap(info);
        canvas.Erase(SKColors.Transparent);
        int bytes = sw * sh * 4, have = -1;
        var resized = new Dictionary<int, byte[]>();
        var outFrames = new List<byte[]>(map.Length);
        bool alpha = false;
        int k = 0;
        for (int f = 0; f <= lastNeeded; f++)
        {
            ct.ThrowIfCancellationRequested();
            int req = infos[f].RequiredFrame;
            if (req < 0) canvas.Erase(SKColors.Transparent);
            else if (req != have)
            {
                if (saved.TryGetValue(req, out var st)) System.Runtime.InteropServices.Marshal.Copy(st, 0, canvas.GetPixels(), bytes);
                else canvas.Erase(SKColors.Transparent);     // shouldn't happen; drawn on clear rather than wrong
            }
            var res = codec.GetPixels(info, canvas.GetPixels(), new SKCodecOptions(f, req));
            if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) throw new InvalidDataException("frame " + f + ": " + res);
            have = f;
            if (keep.ContainsKey(f))
            {
                var copy = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(canvas.GetPixels(), copy, 0, bytes);
                saved[f] = copy;
            }
            foreach (var done in keep.Where(p => p.Value == f).Select(p => p.Key).ToList()) { saved.Remove(done); keep.Remove(done); }
            // every output frame that shows this source frame
            byte[]? small = null;
            while (k < map.Length && map[k] == f)
            {
                if (small is null)
                {
                    using var r = canvas.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium)
                                  ?? throw new InvalidDataException("resize failed");
                    small = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(r.GetPixels(), small, 0, small.Length);
                    if (!alpha) for (int i = 3; i < small.Length; i += 4) if (small[i] != 255) { alpha = true; break; }
                }
                outFrames.Add(small);
                k++;
            }
        }
        return (outFrames, w, h, alpha, cut, lengthMs, sourceMs);
    }

    /// <summary>Makes the rendition file contents: a small header, then the frames' blocks, LZ4-compressed.</summary>
    public static byte[] Encode(List<byte[]> frames, int w, int h, bool alpha, int fps, bool cut, int lengthMs, int sourceMs, bool parallel = false)
    {
        int pw = (w + 3) & ~3, ph = (h + 3) & ~3;
        var enc = new BcEncoder(alpha ? CompressionFormat.Bc7 : CompressionFormat.Bc1);
        enc.OutputOptions.GenerateMipMaps = false;
        enc.OutputOptions.Quality = alpha ? CompressionQuality.Fast : CompressionQuality.Balanced;
        enc.Options.IsParallel = parallel;                // background work: one core (a big version someone is waiting for: all of them)
        var padded = new byte[pw * ph * 4];
        byte[]? all = null; int frameBytes = 0;
        for (int i = 0; i < frames.Count; i++)
        {
            Pad(frames[i], w, h, padded, pw, ph);
            var blocks = enc.EncodeToRawBytes(padded, pw, ph, PixelFormat.Bgra32, 0, out _, out _);
            if (all is null) { frameBytes = blocks.Length; all = new byte[frameBytes * frames.Count]; }
            Buffer.BlockCopy(blocks, 0, all, i * frameBytes, frameBytes);
        }
        var packed = LZ4Pickler.Pickle(all ?? Array.Empty<byte>());
        var outp = new byte[40 + packed.Length];
        var s = outp.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(s, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], w); BinaryPrimitives.WriteInt32LittleEndian(s[8..], h);
        BinaryPrimitives.WriteInt32LittleEndian(s[12..], fps); BinaryPrimitives.WriteInt32LittleEndian(s[16..], frames.Count);
        BinaryPrimitives.WriteInt32LittleEndian(s[20..], lengthMs); BinaryPrimitives.WriteInt32LittleEndian(s[24..], sourceMs);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], (cut ? 1 : 0) | (alpha ? 2 : 0));
        BinaryPrimitives.WriteInt32LittleEndian(s[32..], frameBytes);
        BinaryPrimitives.WriteInt32LittleEndian(s[36..], packed.Length);
        packed.CopyTo(s[40..]);
        return outp;
    }

    private static void Pad(byte[] src, int w, int h, byte[] dst, int pw, int ph)
    {
        for (int y = 0; y < ph; y++)
        {
            int sy = Math.Min(y, h - 1);
            Buffer.BlockCopy(src, sy * w * 4, dst, y * pw * 4, w * 4);
            for (int x = w; x < pw; x++) Buffer.BlockCopy(src, (sy * w + w - 1) * 4, dst, (y * pw + x) * 4, 4);   // repeat the edge
        }
    }

    public static GifRendition Read(byte[] blob)
    {
        var s = blob.AsSpan();
        if (blob.Length < 40 || BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) throw new InvalidDataException("not a GIF rendition");
        var r = new GifRendition
        {
            W = BinaryPrimitives.ReadInt32LittleEndian(s[4..]), H = BinaryPrimitives.ReadInt32LittleEndian(s[8..]),
            Fps = BinaryPrimitives.ReadInt32LittleEndian(s[12..]), Frames = BinaryPrimitives.ReadInt32LittleEndian(s[16..]),
            LengthMs = BinaryPrimitives.ReadInt32LittleEndian(s[20..]), SourceMs = BinaryPrimitives.ReadInt32LittleEndian(s[24..]),
            FrameBytes = BinaryPrimitives.ReadInt32LittleEndian(s[32..]),
        };
        int flags = BinaryPrimitives.ReadInt32LittleEndian(s[28..]);
        r.Cut = (flags & 1) != 0; r.Bc7 = (flags & 2) != 0;
        r.PW = (r.W + 3) & ~3; r.PH = (r.H + 3) & ~3;
        int len = BinaryPrimitives.ReadInt32LittleEndian(s[36..]);
        r.Blocks = LZ4Pickler.Unpickle(s.Slice(40, len));
        return r;
    }

    /// <summary>One frame back as premultiplied BGRA (padded size): for the tests, and for showing a rendition without the GPU.</summary>
    public static byte[] DecodeFrame(GifRendition r, int i)
    {
        var dec = new BcDecoder();
        var block = new byte[r.FrameBytes];
        Buffer.BlockCopy(r.Blocks, i * r.FrameBytes, block, 0, r.FrameBytes);
        var px = dec.DecodeRaw(block, r.PW, r.PH, r.Bc7 ? CompressionFormat.Bc7 : CompressionFormat.Bc1);
        var o = new byte[r.PW * r.PH * 4];
        for (int p = 0; p < px.Length; p++) { o[p * 4] = px[p].b; o[p * 4 + 1] = px[p].g; o[p * 4 + 2] = px[p].r; o[p * 4 + 3] = px[p].a; }
        return o;
    }
}

public sealed class GifStats
{
    public long Animated, Made, Failed, Stills, Pending, Bytes, Big;
    public volatile bool Running, Paused;
    public volatile string LastError = "";
}

/// <summary>
/// Makes GIF renditions in the background: GIFs on screen first (<see cref="Want"/>), then every other GIF. One thread at the lowest
/// priority, paused while scrolling, so it never competes with scrolling or the thumbnails. Changing the length limit remakes them;
/// the old ones stay in use until replaced. Stored in their own pack files (gifsmall-*.bin, giflarge-*.bin) next to the others.
/// </summary>
public sealed class GifRenditionMaker
{
    private readonly IndexDb _db;
    public ThumbStore Small { get; }
    public ThumbStore Large { get; }
    /// <summary>Extra-large versions (made on demand).</summary>
    public ThumbStore Big { get; }
    private ThumbStore StoreFor(int tier) => tier == 0 ? Small : tier == 1 ? Large : Big;
    private readonly ConcurrentQueue<long> _wantBig = new();
    /// <summary>A big version was made (file id): the player can use it now.</summary>
    public event Action<long>? BigMade;
    /// <summary>Make the extra-large version of a GIF (shown big): before anything else.</summary>
    public void WantBig(long id) => _wantBig.Enqueue(id);
    /// <summary>The most the extra-large versions may take on the drive (0 = no limit): the oldest pack files go first.</summary>
    public long BigCapBytes;
    /// <summary>Size of the extra-large versions on the drive.</summary>
    public long BigBytes => Big.Size;

    /// <summary>Over the limit: the oldest pack files of extra-large versions are deleted (with their index rows). They're made again if needed.</summary>
    public void EnforceBigCap()
    {
        long cap = Interlocked.Read(ref BigCapBytes);
        if (cap <= 0) return;
        var packs = Big.Packs();
        long total = packs.Sum(p => p.size);
        if (total <= cap) return;
        int firstKept = packs[^1].pack;
        if (packs.Count == 1) firstKept = Big.RollToNewPack();      // one big file: start a new one so the old can go
        else
        {
            foreach (var (pack, size) in packs.Take(packs.Count - 1))
            {
                if (total <= cap) { firstKept = pack; break; }
                total -= size;
                firstKept = pack + 1;
            }
        }
        DropBig(firstKept);
    }

    /// <summary>Deletes every extra-large version (they're made again when GIFs are shown big).</summary>
    public void ClearBig() => DropBig(Big.RollToNewPack());

    private void DropBig(int firstKept)
    {
        using (var c = _db.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM gif_renditions WHERE tier=2 AND pack<$p";
            cmd.Parameters.AddWithValue("$p", firstKept);
            cmd.ExecuteNonQuery();
        }
        Big.DeletePacksBefore(firstKept);
        BigDropped?.Invoke();
    }

    /// <summary>Some extra-large versions were deleted (the player forgets them).</summary>
    public event Action? BigDropped;
    public GifStats Stats { get; } = new();
    private readonly ConcurrentQueue<long> _want = new();
    private readonly PipelineStats _pipe;
    public volatile int CapMs = 5000;
    /// <summary>Test hook: true while "scrolling" (work waits).</summary>
    public Func<bool>? Busy;

    public GifRenditionMaker(IndexDb db, string cacheDir, PipelineStats pipe)
    {
        _db = db; _pipe = pipe;
        Small = new ThumbStore(cacheDir, "gifsmall");
        Large = new ThumbStore(cacheDir, "giflarge");
        Big = new ThumbStore(cacheDir, "gifbig");
        using var c = db.Open();
        IndexDb.Exec(c, """
            CREATE TABLE IF NOT EXISTS gif_renditions (
              id INTEGER NOT NULL, tier INTEGER NOT NULL,
              pack INTEGER NOT NULL, off INTEGER NOT NULL, len INTEGER NOT NULL,   -- len 0: a still GIF (one frame), nothing to play
              cap_ms INTEGER NOT NULL, frames INTEGER NOT NULL, fps INTEGER NOT NULL, w INTEGER NOT NULL, h INTEGER NOT NULL, cut INTEGER NOT NULL,
              PRIMARY KEY (id, tier))
            """);
    }

    /// <summary>GIFs on screen: made before the rest.</summary>
    public void Want(IEnumerable<long> ids) { foreach (var id in ids) _want.Enqueue(id); }

    /// <summary>Where a GIF's rendition for a tier is, if made for the current length limit (or an older one, still in use).</summary>
    public (int pack, long off, int len)? Where(long id, int tier)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT pack, off, len FROM gif_renditions WHERE id=$id AND tier=$t";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$t", tier);
        using var r = cmd.ExecuteReader();
        return r.Read() && r.GetInt32(2) > 0 ? (r.GetInt32(0), r.GetInt64(1), r.GetInt32(2)) : null;
    }

    /// <summary>Every rendition made so far: (file id, tier).</summary>
    public List<(long id, int tier)> Made()
    {
        var l = new List<(long, int)>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, tier FROM gif_renditions WHERE len>0";
        using var r = cmd.ExecuteReader();
        while (r.Read()) l.Add((r.GetInt64(0), r.GetInt32(1)));
        return l;
    }

    public GifRendition? Load(long id, int tier)
    {
        var at = Where(id, tier);
        if (at is null) return null;
        var blob = StoreFor(tier).Read(at.Value.pack, at.Value.off, at.Value.len);
        return blob is null ? null : GifRenditions.Read(blob);
    }

    private List<(long id, string path)> Todo()
    {
        var list = new List<(long, string)>();
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        // GIFs with no rendition yet, or one made for another length limit (stills are never remade)
        cmd.CommandText = """
            SELECT f.id, f.path FROM files f
            WHERE f.kind=1 AND f.missing_since=0 AND (
              (SELECT COUNT(*) FROM gif_renditions g WHERE g.id=f.id AND g.tier<2 AND (g.cap_ms=$cap OR g.len=0)) < 2)
            ORDER BY f.first_seen DESC
            """;
        cmd.Parameters.AddWithValue("$cap", CapMs);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    private void Count()
    {
        using var c = _db.Open();
        Stats.Animated = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE kind=1 AND missing_since=0");
        Stats.Stills = IndexDb.Scalar(c, "SELECT COUNT(DISTINCT id) FROM gif_renditions WHERE len=0");
        Stats.Made = IndexDb.Scalar(c, $"SELECT COUNT(DISTINCT id) FROM gif_renditions WHERE len>0 AND tier<2 AND cap_ms={CapMs}");
        Stats.Big = IndexDb.Scalar(c, "SELECT COUNT(*) FROM gif_renditions WHERE len>0 AND tier=2");
        Stats.Bytes = IndexDb.Scalar(c, "SELECT COALESCE(SUM(len),0) FROM gif_renditions");
    }

    private void WaitForQuiet(CancellationToken ct)
    {
        Stats.Paused = false;
        while (!ct.IsCancellationRequested && (Busy?.Invoke() ?? Stopwatch.GetTimestamp() - Volatile.Read(ref _pipe.LastUiActivity) < Stopwatch.Frequency))
        {
            Stats.Paused = true;
            Thread.Sleep(50);
        }
        Stats.Paused = false;
    }

    /// <summary>Makes one GIF's renditions (both tiers) and records them. True if made (or found to be a still).</summary>
    public bool MakeOne(long id, string path, CancellationToken ct) => Make(id, path, 0, GifRenditions.BackfillTiers, false, ct);

    /// <summary>Makes the renditions for tiers [from, to) and records them.</summary>
    public bool Make(long id, string path, int from, int to, bool parallel, CancellationToken ct)
    {
        try
        {
            var rows = new List<(int tier, (int pack, long off, int length) at, GifRendition? meta, bool still)>();
            for (int t = from; t < to; t++)
            {
                var (edge, fps) = GifRenditions.Tiers[t];
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                var fr = GifRenditions.Frames(fs, edge, fps, CapMs, ct);
                if (fr is null) { rows.Add((t, (0, 0, 0), null, true)); continue; }
                var (frames, w, h, alpha, cut, lengthMs, sourceMs) = fr.Value;
                var blob = GifRenditions.Encode(frames, w, h, alpha, fps, cut, lengthMs, sourceMs, parallel);
                var at = StoreFor(t).Append(blob);
                rows.Add((t, at, new GifRendition { W = w, H = h, Fps = fps, Frames = frames.Count, Cut = cut }, false));
            }
            Small.Flush(); Large.Flush(); Big.Flush();      // the files first, then the index
            using var c = _db.Open();
            using var tx = c.BeginTransaction();
            foreach (var (tier, at, meta, still) in rows)
            {
                using var cmd = c.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT OR REPLACE INTO gif_renditions (id, tier, pack, off, len, cap_ms, frames, fps, w, h, cut) VALUES ($id,$t,$p,$o,$l,$cap,$n,$fps,$w,$h,$cut)";
                cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$t", tier);
                cmd.Parameters.AddWithValue("$p", at.pack); cmd.Parameters.AddWithValue("$o", at.off); cmd.Parameters.AddWithValue("$l", at.length);
                cmd.Parameters.AddWithValue("$cap", CapMs); cmd.Parameters.AddWithValue("$n", meta?.Frames ?? 0); cmd.Parameters.AddWithValue("$fps", meta?.Fps ?? 0);
                cmd.Parameters.AddWithValue("$w", meta?.W ?? 0); cmd.Parameters.AddWithValue("$h", meta?.H ?? 0); cmd.Parameters.AddWithValue("$cut", meta?.Cut == true ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Stats.Failed++; Stats.LastError = Path.GetFileName(path) + ": " + ex.Message; return false; }
    }

    /// <summary>Runs until everything is made (then checks again now and then), or until cancelled.</summary>
    public void Run(CancellationToken ct)
    {
        Stats.Running = true;
        var failed = new HashSet<long>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                MakeBigOnes(ct);
                Count();
                var todo = Todo().Where(t => !failed.Contains(t.id)).ToList();
                Stats.Pending = todo.Count;
                if (todo.Count == 0) { for (int i = 0; i < 100 && !ct.IsCancellationRequested && _want.IsEmpty && _wantBig.IsEmpty; i++) Thread.Sleep(100); continue; }
                var byId = todo.ToDictionary(t => t.id, t => t.path);
                int madeThisRound = 0;
                while (!ct.IsCancellationRequested && byId.Count > 0 && madeThisRound < 200)
                {
                    if (!_wantBig.IsEmpty) MakeBigOnes(ct);
                    WaitForQuiet(ct);
                    // on-screen GIFs first
                    long id = -1;
                    while (_want.TryDequeue(out var w)) if (byId.ContainsKey(w)) { id = w; break; }
                    if (id < 0) id = byId.Keys.First();
                    var path = byId[id]; byId.Remove(id);
                    if (!MakeOne(id, path, ct)) failed.Add(id);
                    madeThisRound++;
                    Stats.Pending = byId.Count;
                    if (madeThisRound % 20 == 0) Count();
                }
            }
        }
        catch (OperationCanceledException) { }
        finally { Stats.Running = false; }
    }

    private string? PathOf(long id)
    {
        using var c = _db.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT path FROM files WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>The extra-large versions asked for (GIFs shown big right now): made straight away, with every core.</summary>
    private void MakeBigOnes(CancellationToken ct)
    {
        var seen = new HashSet<long>();
        while (!ct.IsCancellationRequested && _wantBig.TryDequeue(out var id))
        {
            if (!seen.Add(id) || Where(id, 2) is not null) continue;
            var path = PathOf(id);
            if (path is null) continue;
            if (Make(id, path, 2, 3, true, ct)) BigMade?.Invoke(id);
        }
        try { EnforceBigCap(); } catch (Exception ex) { Stats.LastError = "tidying extra-large GIFs: " + ex.Message; }
    }

    public void Start(CancellationToken ct)
    {
        var t = new Thread(() => Run(ct)) { IsBackground = true, Name = "gif-renditions", Priority = ThreadPriority.Lowest };
        t.Start();
    }
}
