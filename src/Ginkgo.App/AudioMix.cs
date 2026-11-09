using System.Runtime.InteropServices;

namespace Ginkgo.App;

/// <summary>
/// Video sound, done by Ginkgo itself rather than by LibVLC's sound output. LibVLC hands each player's decoded sound over (with
/// the moment each piece is due); Ginkgo mixes all players into one output, each with its own volume, and a muted player simply
/// adds nothing. (LibVLC's own outputs keep volume and mute in places several players share, so settings leaked between videos.)
///
/// Timing: LibVLC sends sound about a second early, stamped with when it should be heard on LibVLC's clock. The mixer is asked
/// for the next stretch of output, works out when that stretch will be heard, and takes from each player exactly the sound due
/// then (waiting with silence if it's early, skipping if it's late), so sound stays with the picture.
/// </summary>
public static class AudioMixer
{
    public const int Rate = 48000;
    private static readonly List<AudioStream> s_streams = new();
    private static AudioStream[] s_snapshot = Array.Empty<AudioStream>();

    [DllImport("libvlc", EntryPoint = "libvlc_clock")] private static extern long VlcClock();

    /// <summary>LibVLC's clock, in microseconds (the clock sound and pictures are stamped with).</summary>
    public static long Now() { try { return VlcClock(); } catch { return System.Diagnostics.Stopwatch.GetTimestamp() * 1_000_000 / System.Diagnostics.Stopwatch.Frequency; } }

    public static void Add(AudioStream s) { lock (s_streams) { s_streams.Add(s); s_snapshot = s_streams.ToArray(); } }
    public static void Remove(AudioStream s) { lock (s_streams) { s_streams.Remove(s); s_snapshot = s_streams.ToArray(); } }

    /// <summary>
    /// Fills <paramref name="frames"/> stereo frames (interleaved floats) starting at <paramref name="offset"/> (in floats), to be heard
    /// <paramref name="latencyUs"/> from now.
    /// </summary>
    public static void Mix(float[] buf, int offset, int frames, long latencyUs)
    {
        Array.Clear(buf, offset, frames * 2);
        long t0 = Now() + latencyUs;
        foreach (var s in s_snapshot) s.MixInto(buf, offset, frames, t0);
        // keep it in range
        for (int i = offset, n = offset + frames * 2; i < n; i++)
        {
            float v = buf[i];
            if (v > 1f) buf[i] = 1f; else if (v < -1f) buf[i] = -1f;
        }
    }
}

/// <summary>One player's sound, queued with when each piece is due.</summary>
public sealed class AudioStream
{
    private sealed class Block { public long Pts; public float[] Data = null!; public int Pos; }
    private readonly Queue<Block> _q = new();
    private readonly object _lock = new();
    private volatile float _gain;                 // 0 = silent (muted)
    private float _lastGain;
    private bool _paused;
    private long _pausedAt;
    private const long Slack = 20_000;            // how far off (µs) a piece may be before it's waited for or skipped
    private const long MaxQueued = 3_000_000;     // never more than this much ahead (µs): anything beyond is a mistake

    /// <summary>Volume 0..100 and mute, as heard.</summary>
    public void Set(int volume, bool muted)
    {
        double v = Math.Clamp(volume, 0, 100) / 100.0;
        _gain = muted ? 0f : (float)(v * v);       // a curve that sounds even: half way sounds about half as loud
    }

    // ---- from LibVLC's sound thread ----

    public unsafe void Push(IntPtr samples, uint count, long pts)
    {
        int n = (int)count * 2;
        var data = new float[n];
        short* src = (short*)samples;
        for (int i = 0; i < n; i++) data[i] = src[i] / 32768f;
        lock (_lock)
        {
            // something gone wrong (a jump LibVLC didn't flush): don't let it pile up
            if (_q.Count > 0 && pts - _q.Peek().Pts > MaxQueued) _q.Clear();
            _q.Enqueue(new Block { Pts = pts, Data = data });
            HasSound = true;
        }
    }

    /// <summary>Sound has arrived since the last reset.</summary>
    public bool HasSound { get; private set; }
    public void Flush() { lock (_lock) _q.Clear(); }

    /// <summary>A new file on this player: nothing queued, and not paused (a pause from the last file must not silence this one).</summary>
    public void Reset() { lock (_lock) { _q.Clear(); _paused = false; HasSound = false; } }

    /// <summary>Paused at <paramref name="date"/> (LibVLC's clock).</summary>
    public void Pause(long date) { lock (_lock) { if (!_paused) { _paused = true; _pausedAt = date; } } }

    /// <summary>Resumed at <paramref name="date"/>: what was waiting is due that much later.</summary>
    public void Resume(long date)
    {
        lock (_lock)
        {
            if (!_paused) return;
            _paused = false;
            long shift = date - _pausedAt;
            if (shift > 0) foreach (var b in _q) b.Pts += shift;
        }
    }

    // ---- from the output ----

    private static long FramesToUs(long f) => f * 1_000_000 / AudioMixer.Rate;
    private static int UsToFrames(long us) => (int)(us * AudioMixer.Rate / 1_000_000);

    public void MixInto(float[] buf, int offset, int frames, long t0)
    {
        float g1 = _gain, g0 = _lastGain;
        _lastGain = g1;
        // turned silent: silent straight away (a fade down from an older volume let a moment of sound through)
        if (g1 == 0) g0 = 0;
        lock (_lock)
        {
            if (_paused) return;
            int f = 0;
            while (f < frames && _q.Count > 0)
            {
                var b = _q.Peek();
                int left = b.Data.Length / 2 - b.Pos / 2;
                if (left <= 0) { _q.Dequeue(); continue; }
                long due = b.Pts + FramesToUs(b.Pos / 2);
                long at = t0 + FramesToUs(f);
                long early = due - at;
                if (early > Slack) { f += Math.Min(frames - f, Math.Max(1, UsToFrames(early))); continue; }        // not yet: silence
                if (early < -Slack)
                {
                    int drop = Math.Min(left, Math.Max(1, UsToFrames(-early)));                                        // late: skip ahead
                    b.Pos += drop * 2;
                    continue;
                }
                int take = Math.Min(frames - f, left);
                if (g0 != 0 || g1 != 0)
                {
                    int o = offset + f * 2, p = b.Pos;
                    for (int k = 0; k < take; k++)
                    {
                        // the volume glides over each stretch, so changes don't click
                        float g = g0 + (g1 - g0) * ((f + k) / (float)frames);
                        buf[o++] += b.Data[p++] * g;
                        buf[o++] += b.Data[p++] * g;
                    }
                }
                b.Pos += take * 2;
                f += take;
            }
        }
    }
}
