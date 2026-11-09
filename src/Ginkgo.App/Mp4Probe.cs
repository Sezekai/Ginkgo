using System.IO;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace Ginkgo.App;

/// <summary>
/// Looks inside an MP4 for one quirk: the video track's edit list starting seconds into its own timeline, which means the pictures
/// are stamped (decode order) seconds ahead of when they show. LibVLC's MP4 reader mishandles that (pictures arrive late and are
/// dropped: a still picture for seconds, then a skip). Only the file's index is read, once per file.
/// </summary>
public static class Mp4Probe
{
    private static readonly ConcurrentDictionary<string, bool> s_seen = new(StringComparer.OrdinalIgnoreCase);
    private const double Threshold = 0.5;        // seconds

    public static bool PicturesStampedAhead(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".m4v" or ".mov" or ".3gp")) return false;
        return s_seen.GetOrAdd(path, Check);
    }

    private static bool Check(string path)
    {
        try
        {
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.RandomAccess);
            long pos = 0, len = f.Length;
            var head = new byte[16];
            while (pos + 8 <= len)
            {
                f.Position = pos;
                if (f.Read(head, 0, 16) < 8) return false;
                long size = BinaryPrimitives.ReadUInt32BigEndian(head);
                string type = System.Text.Encoding.ASCII.GetString(head, 4, 4);
                int hdr = 8;
                if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(head.AsSpan(8)); hdr = 16; }
                else if (size == 0) size = len - pos;
                if (size < hdr) return false;
                if (type == "moov")
                {
                    if (size > 64L * 1024 * 1024) return false;
                    var moov = new byte[size - hdr];
                    f.Position = pos + hdr;
                    int got = 0;
                    while (got < moov.Length) { int n = f.Read(moov, got, moov.Length - got); if (n <= 0) return false; got += n; }
                    return InMoov(moov);
                }
                pos += size;
            }
        }
        catch { }
        return false;
    }

    private static bool InMoov(byte[] moov)
    {
        foreach (var (type, start, end) in Boxes(moov, 0, moov.Length))
        {
            if (type != "trak") continue;
            bool video = false; uint timescale = 0; long mediaTime = -1;
            foreach (var (t2, s2, e2) in Boxes(moov, start, end))
            {
                if (t2 == "edts")
                    foreach (var (t3, s3, e3) in Boxes(moov, s2, e2))
                        if (t3 == "elst" && e3 - s3 >= 8)
                        {
                            int ver = moov[s3];
                            uint count = BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(s3 + 4));
                            // the first entry that isn't an empty gap
                            int p = s3 + 8;
                            for (uint k = 0; k < count && p < e3; k++)
                            {
                                long mt;
                                if (ver == 1) { if (p + 20 > e3) break; mt = BinaryPrimitives.ReadInt64BigEndian(moov.AsSpan(p + 8)); p += 20; }
                                else { if (p + 12 > e3) break; mt = BinaryPrimitives.ReadInt32BigEndian(moov.AsSpan(p + 4)); p += 12; }
                                if (mt >= 0) { mediaTime = mt; break; }
                            }
                        }
                if (t2 == "mdia")
                    foreach (var (t3, s3, e3) in Boxes(moov, s2, e2))
                    {
                        if (t3 == "hdlr" && e3 - s3 >= 12) video = System.Text.Encoding.ASCII.GetString(moov, s3 + 8, 4) == "vide";
                        if (t3 == "mdhd" && e3 - s3 >= 24)
                            timescale = moov[s3] == 1 ? BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(s3 + 20)) : BinaryPrimitives.ReadUInt32BigEndian(moov.AsSpan(s3 + 12));
                    }
            }
            if (video && timescale > 0 && mediaTime > 0 && mediaTime / (double)timescale >= Threshold) return true;
        }
        return false;
    }

    /// <summary>The boxes directly inside [start, end): type and where their contents are.</summary>
    private static IEnumerable<(string type, int start, int end)> Boxes(byte[] b, int start, int end)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
            string type = System.Text.Encoding.ASCII.GetString(b, p + 4, 4);
            int hdr = 8;
            if (size == 1) { if (p + 16 > end) yield break; size = (long)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(p + 8)); hdr = 16; }
            else if (size == 0) size = end - p;
            if (size < hdr || p + size > end) yield break;
            yield return (type, p + hdr, (int)(p + size));
            p += (int)size;
        }
    }
}
