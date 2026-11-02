using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace Ginkgo.App;

/// <summary>
/// Animated GIFs in the grid: the GIFs on screen (at most <see cref="Limit"/>, nearest the middle first) are decoded in the
/// background at their tile size, then play; everything else shows its first frame. GIFs that leave the screen are let go.
/// </summary>
public sealed class GifAnimator
{
    /// <summary>How many GIFs play at once (0 = every one on screen).</summary>
    public int Limit { get; set; } = 20;
    /// <summary>Decoded frames of one GIF; bigger ones are kept at a lower resolution. Raised for the hovered GIF in the GPU grid (full quality).</summary>
    public long BudgetPerGif { get; set; } = 24L << 20;
    public int MaxFrames { get; set; } = 300;

    private sealed class Anim
    {
        public int W, H;
        public ThumbLoader.Pix[]? Frames;
        public int[] Ends = Array.Empty<int>();      // when each frame ends, in ms from the start of a loop
        public int Total;
        public long Start;
        public volatile bool Ready;
        public int Shown = -1;
        public readonly CancellationTokenSource Cts = new();
    }

    private readonly Func<int, string?> _pathOf;
    private readonly Dictionary<int, Anim> _anims = new();
    private readonly HashSet<string> _still = new(StringComparer.OrdinalIgnoreCase);      // files that turned out to have one frame
    private readonly SemaphoreSlim _gate = new(2);
    private readonly Action _ready;

    /// <param name="pathOf">The file of tile i.</param>
    /// <param name="ready">Called (on any thread) when a GIF has finished loading.</param>
    public GifAnimator(Func<int, string?> pathOf, Action ready) { _pathOf = pathOf; _ready = ready; }

    private static long NowMs => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency;

    private static int FrameAt(Anim a, long now)
    {
        if (a.Total <= 0) return 0;
        int t = (int)((now - a.Start) % a.Total);
        int lo = 0, hi = a.Ends.Length - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (a.Ends[mid] <= t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    /// <summary>UI thread. The frame to show now for tile i, or null to show its normal picture.</summary>
    public ThumbLoader.Pix? Frame(int i)
    {
        if (!_anims.TryGetValue(i, out var a) || !a.Ready || a.Frames is null) return null;
        int f = FrameAt(a, NowMs);
        a.Shown = f;
        return a.Frames[f];
    }

    /// <summary>UI thread. True when any playing GIF should now show a different frame.</summary>
    public bool Due()
    {
        long now = NowMs;
        foreach (var a in _anims.Values) if (a.Ready && a.Frames is not null && FrameAt(a, now) != a.Shown) return true;
        return false;
    }

    /// <summary>UI thread. The GIF tiles on screen, nearest the middle first, with their sizes in pixels.</summary>
    public void SetVisible(List<(int index, int w, int h)> wanted)
    {
        var keep = new HashSet<int>();
        foreach (var (i, w, h) in wanted)
        {
            if (Limit > 0 && keep.Count >= Limit) break;
            if (w < 24 || h < 24) continue;
            string? path = _pathOf(i);
            if (path is null) continue;
            lock (_still) if (_still.Contains(path)) continue;
            keep.Add(i);
            if (_anims.TryGetValue(i, out var a) && a.W == w && a.H == h) continue;
            if (a is not null) a.Cts.Cancel();
            var n = new Anim { W = w, H = h };
            _anims[i] = n;
            _ = Task.Run(() => Load(n, path));
        }
        foreach (var k in _anims.Keys.Where(k => !keep.Contains(k)).ToList()) { _anims[k].Cts.Cancel(); _anims.Remove(k); }
    }

    /// <summary>GIFs playing now (loaded and on screen).</summary>
    public int Playing => _anims.Values.Count(a => a.Ready);

    public void Clear() { foreach (var a in _anims.Values) a.Cts.Cancel(); _anims.Clear(); }

    private async Task Load(Anim a, string path)
    {
        var ct = a.Cts.Token;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
                using var codec = SKCodec.Create(stream);
                if (codec is null) return;
                int count = codec.FrameCount;
                if (count <= 1) { lock (_still) _still.Add(path); return; }
                var infos = codec.FrameInfo;
                count = Math.Min(count, MaxFrames);
                // frames at the tile's size, or smaller when a long GIF would take too much memory
                double scale = Math.Min(1, Math.Sqrt(BudgetPerGif / ((double)count * a.W * a.H * 4)));
                int ow = Math.Max(8, (int)(a.W * scale)), oh = Math.Max(8, (int)(a.H * scale));
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKBitmap(info);
                canvas.Erase(SKColors.Transparent);
                var frames = new ThumbLoader.Pix[count];
                var ends = new int[count];
                int t = 0;
                // frames that later frames draw on top of (other than the one just before) are kept, so "restore to previous" is right
                var keep = new Dictionary<int, int>();
                for (int f = 0; f < count; f++) { int rq = infos[f].RequiredFrame; if (rq >= 0 && rq != f - 1) keep[rq] = f; }
                var saved = new Dictionary<int, byte[]>();
                int bytes = info.Width * info.Height * 4, have = -1;
                for (int f = 0; f < count; f++)
                {
                    ct.ThrowIfCancellationRequested();
                    int req = infos[f].RequiredFrame;
                    if (req < 0) canvas.Erase(SKColors.Transparent);
                    else if (req != have)
                    {
                        if (saved.TryGetValue(req, out var st)) System.Runtime.InteropServices.Marshal.Copy(st, 0, canvas.GetPixels(), bytes);
                        else canvas.Erase(SKColors.Transparent);
                    }
                    var opts = new SKCodecOptions(f, req);
                    var res = codec.GetPixels(info, canvas.GetPixels(), opts);
                    if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) { count = f; break; }
                    have = f;
                    if (keep.ContainsKey(f)) { var cp = new byte[bytes]; System.Runtime.InteropServices.Marshal.Copy(canvas.GetPixels(), cp, 0, bytes); saved[f] = cp; }
                    foreach (var done in keep.Where(p => p.Value == f).Select(p => p.Key).ToList()) { saved.Remove(done); keep.Remove(done); }
                    using var small = canvas.Resize(new SKImageInfo(ow, oh, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                    if (small is null) { count = f; break; }
                    var data = new byte[ow * oh * 4];
                    System.Runtime.InteropServices.Marshal.Copy(small.GetPixels(), data, 0, data.Length);
                    frames[f] = new ThumbLoader.Pix { Data = data, W = ow, H = oh, SrcW = ow };
                    int d = infos[f].Duration;
                    t += d <= 10 ? 100 : d;                                          // like browsers: very short delays play at 10 per second
                    ends[f] = t;
                }
                if (count <= 1) return;
                if (count < frames.Length) { Array.Resize(ref frames, count); Array.Resize(ref ends, count); }
                a.Frames = frames; a.Ends = ends; a.Total = t; a.Start = NowMs; a.Ready = true;
            }
            finally { _gate.Release(); }
            _ready();
        }
        catch (OperationCanceledException) { }
        catch { lock (_still) _still.Add(path); }
    }
}
