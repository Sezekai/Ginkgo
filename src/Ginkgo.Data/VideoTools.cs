using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SkiaSharp;

namespace Ginkgo.Data;

/// <summary>
/// Videos: reading their length and size (ffprobe) and grabbing one frame for the thumbnail (ffmpeg). Both programs come in the
/// app's "tools" folder; without them videos simply wait (nothing is marked as failed).
/// </summary>
public static class VideoTools
{
    public sealed record Info(int Width, int Height, double Seconds, string Codec);

    private static string? _ffmpeg, _ffprobe;
    private static bool _looked;

    private static string? Find(string exe)
    {
        var dirs = new List<string>();
        string? env = Environment.GetEnvironmentVariable("GINKGO_TOOLS");
        if (!string.IsNullOrEmpty(env)) dirs.Add(env);
        dirs.Add(Path.Combine(AppContext.BaseDirectory, "tools"));
        dirs.Add(AppContext.BaseDirectory);
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)) if (d.Length > 0) dirs.Add(d);
        foreach (var d in dirs)
        {
            foreach (var name in new[] { exe + ".exe", exe })
            {
                try { var p = Path.Combine(d, name); if (File.Exists(p)) return p; } catch { }
            }
        }
        return null;
    }

    private static void Look()
    {
        if (_looked) return;
        _ffmpeg = Find("ffmpeg"); _ffprobe = Find("ffprobe"); _looked = true;
    }

    /// <summary>Tests switch the video tools off to check what happens without them.</summary>
    public static bool Disabled { get; set; }
    public static bool Available { get { Look(); return !Disabled && _ffmpeg is not null && _ffprobe is not null; } }
    public static string? FfmpegPath { get { Look(); return _ffmpeg; } }

    private static (byte[] stdout, string stderr, int code) Run(string exe, string args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        var ms = new MemoryStream();
        var outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
        var errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } throw new TimeoutException(Path.GetFileName(exe) + " took too long"); }
        outTask.Wait(5000);
        return (ms.ToArray(), errTask.Wait(2000) ? errTask.Result : "", p.ExitCode);
    }

    private static string Q(string path) => "\"" + path.Replace("\"", "\\\"") + "\"";

    public static Info Probe(string path)
    {
        Look();
        if (_ffprobe is null) throw new InvalidOperationException("ffprobe not found");
        var (o, err, code) = Run(_ffprobe, "-v error -select_streams v:0 -show_entries stream=width,height,codec_name,duration:stream_tags=rotate:stream_side_data=rotation:format=duration -of json " + Q(path), 20000);
        if (code != 0) throw new InvalidDataException("ffprobe: " + err.Trim());
        using var doc = JsonDocument.Parse(o);
        int w = 0, h = 0; double secs = 0; string codec = "";
        if (doc.RootElement.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
        {
            var st = streams[0];
            if (st.TryGetProperty("width", out var jw)) w = jw.GetInt32();
            if (st.TryGetProperty("height", out var jh)) h = jh.GetInt32();
            if (st.TryGetProperty("codec_name", out var jc)) codec = jc.GetString() ?? "";
            if (st.TryGetProperty("duration", out var jd) && double.TryParse(jd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) secs = d;
            int rot = 0;
            if (st.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var r) && int.TryParse(r.GetString(), out var rr)) rot = rr;
            if (st.TryGetProperty("side_data_list", out var sdl))
                foreach (var sd in sdl.EnumerateArray()) if (sd.TryGetProperty("rotation", out var rv) && rv.TryGetInt32(out var rvi)) rot = rvi;
            if (Math.Abs(rot) % 180 == 90) (w, h) = (h, w);                                   // phone videos filmed upright
        }
        if (secs <= 0 && doc.RootElement.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var fd)
            && double.TryParse(fd.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var fdur)) secs = fdur;
        if (w <= 0 || h <= 0) throw new InvalidDataException("no video stream");
        return new Info(w, h, secs, codec);
    }

    /// <summary>One frame, a little way in (past black openings), at most <paramref name="maxW"/> wide.</summary>
    public static SKBitmap Frame(string path, double seconds, int maxW = 1280)
    {
        Look();
        if (_ffmpeg is null) throw new InvalidOperationException("ffmpeg not found");
        double at = seconds > 0 ? Math.Min(seconds * 0.1, Math.Max(0, seconds - 0.2)) : 0;
        string ss = at.ToString("0.###", CultureInfo.InvariantCulture);
        string args = $"-v error -ss {ss} -i {Q(path)} -frames:v 1 -vf \"scale='min({maxW},iw)':-2\" -f image2pipe -vcodec bmp -";
        var (o, err, code) = Run(_ffmpeg, args, 30000);
        if (o.Length == 0 && at > 0) (o, err, code) = Run(_ffmpeg, $"-v error -i {Q(path)} -frames:v 1 -vf \"scale='min({maxW},iw)':-2\" -f image2pipe -vcodec bmp -", 30000);
        if (o.Length == 0) throw new InvalidDataException("ffmpeg: " + (err.Trim().Length > 0 ? err.Trim() : "no frame"));
        return SKBitmap.Decode(o) ?? throw new InvalidDataException("frame unreadable");
    }
}
