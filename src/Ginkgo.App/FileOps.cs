using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace Ginkgo.App;

/// <summary>
/// The file changes Ginkgo makes when asked (delete, rotate). Deleting only ever sends files to the Recycle Bin: on a drive
/// without one nothing is deleted and the reason is given.
/// </summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHQueryRecycleBin(string pszRootPath, ref SHQUERYRBINFO info);

    /// <summary>Null when the file's drive has a Recycle Bin, else why not.</summary>
    public static string? NoRecycleBin(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return "Windows can't tell which drive it is on";
            if (root.StartsWith(@"\\")) return "it is on a network drive, which has no Recycle Bin";
            var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
            int hr = SHQueryRecycleBin(root, ref info);
            return hr == 0 ? null : $"the drive {root.TrimEnd('\\')} has no Recycle Bin";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Sends files to the Recycle Bin, one at a time. Returns the ones that went and the ones that didn't (with why).</summary>
    public static (List<string> done, List<(string path, string why)> failed) Recycle(IEnumerable<string> paths, Action<int>? progress = null)
    {
        var done = new List<string>(); var failed = new List<(string, string)>();
        int n = 0;
        foreach (var p in paths)
        {
            n++;
            if (!File.Exists(p)) { done.Add(p); progress?.Invoke(n); continue; }
            if (NoRecycleBin(p) is { } why) { failed.Add((p, why + ", so it was left where it is")); progress?.Invoke(n); continue; }
            if (FavouritesService.Recycle(p)) done.Add(p);
            else failed.Add((p, File.Exists(p) ? "Windows couldn't put it in the Recycle Bin (it may be open in another program)" : "it was already gone"));
            progress?.Invoke(n);
        }
        return (done, failed);
    }

    /// <summary>Kinds of file that can be turned (and saved back in the same format).</summary>
    public static bool CanRotate(string path) => Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jfif" or ".jpe" or ".png" or ".bmp" or ".tif" or ".tiff" or ".gif" or ".jxr" or ".wdp";

    /// <summary>
    /// Turns a picture a quarter turn and saves it in its own format. A JPEG is turned without losing quality (Windows moves its
    /// blocks rather than saving it again) and its orientation tag, if it has one, is reset; PNG, BMP, TIFF and still GIFs are saved
    /// again without loss. The new file is written next to the old one and then takes its place, so a failure leaves the original.
    /// </summary>
    public static void Rotate(string path, bool clockwise)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!CanRotate(path)) throw new InvalidOperationException($"Ginkgo can't save {ext} files, so it can't turn them");
        byte[] bytes = File.ReadAllBytes(path);
        var created = File.GetCreationTimeUtc(path);
        BitmapDecoder dec;
        using (var ms = new MemoryStream(bytes, writable: false))
        {
            dec = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (dec.Frames.Count > 1) throw new InvalidOperationException("it is animated (only still pictures can be turned)");
            var frame = dec.Frames[0];
            var turn = clockwise ? Rotation.Rotate90 : Rotation.Rotate270;
            BitmapEncoder enc;
            BitmapMetadata? meta = null;
            try { meta = frame.Metadata is BitmapMetadata m0 ? m0.Clone() : null; } catch { meta = null; }
            switch (ext)
            {
                case ".jpg": case ".jpeg": case ".jfif": case ".jpe":
                    enc = new JpegBitmapEncoder { QualityLevel = 96, Rotation = turn };
                    // the pixels are turned, so an orientation tag (which tells other programs to turn it) is put back to "as is"
                    try { if (meta?.GetQuery("/app1/ifd/{ushort=274}") is not null) meta.SetQuery("/app1/ifd/{ushort=274}", (ushort)1); } catch { }
                    enc.Frames.Add(BitmapFrame.Create(frame, null, meta, frame.ColorContexts));
                    break;
                default:
                    var turned = new TransformedBitmap(frame, new System.Windows.Media.RotateTransform(clockwise ? 90 : 270));
                    enc = ext switch
                    {
                        ".png" => new PngBitmapEncoder(),
                        ".bmp" => new BmpBitmapEncoder(),
                        ".tif" or ".tiff" => new TiffBitmapEncoder(),
                        ".gif" => new GifBitmapEncoder(),
                        _ => new WmpBitmapEncoder(),
                    };
                    BitmapMetadata? keep = null;
                    try { if (ext is ".png" or ".tif" or ".tiff") keep = meta; } catch { }
                    try { enc.Frames.Add(BitmapFrame.Create(turned, null, keep, frame.ColorContexts)); }
                    catch { enc.Frames.Clear(); enc.Frames.Add(BitmapFrame.Create(turned)); }
                    break;
            }
            string temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + ".ginkgo-turn");
            try
            {
                using (var f = File.Create(temp)) enc.Save(f);
                File.Replace(temp, path, null, ignoreMetadataErrors: true);
                try { File.SetCreationTimeUtc(path, created); } catch { }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }
    }

    /// <summary>Moves or copies one file. Throws with the reason when it can't.</summary>
    public static void Transfer(string src, string dst, bool copy)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        if (copy) File.Copy(src, dst, overwrite: false);
        else File.Move(src, dst, overwrite: false);
    }
}
