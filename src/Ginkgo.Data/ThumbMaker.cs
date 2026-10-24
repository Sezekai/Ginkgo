using SkiaSharp;

namespace Ginkgo.Data;

public sealed record ThumbResult(int Width, int Height, int ThumbWidth, int ThumbHeight, byte[] Bytes);

public static class ThumbMaker
{
    public const int MaxW = 320, MaxH = 640, Quality = 72;

    /// <summary>Reads an image once, returns its real size and a small WebP thumbnail.</summary>
    public static ThumbResult Make(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var codec = SKCodec.Create(fs) ?? throw new InvalidDataException("unsupported image");
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new InvalidDataException("bad size");

        float scale = Math.Min(1f, Math.Min(MaxW / (float)w, MaxH / (float)h));
        int tw = Math.Max(1, (int)Math.Round(w * scale)), th = Math.Max(1, (int)Math.Round(h * scale));
        SKSizeI dec = scale < 1f ? codec.GetScaledDimensions(scale) : new SKSizeI(w, h);

        using var bmp = new SKBitmap(new SKImageInfo(dec.Width, dec.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var res = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) throw new InvalidDataException("decode failed: " + res);

        SKBitmap? resized = null;
        try
        {
            var final = bmp;
            if (dec.Width != tw || dec.Height != th)
            {
                resized = bmp.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                if (resized is not null) final = resized;
            }
            using var img = SKImage.FromBitmap(final);
            using var data = img.Encode(SKEncodedImageFormat.Webp, Quality);
            return new ThumbResult(w, h, final.Width, final.Height, data.ToArray());
        }
        finally { resized?.Dispose(); }
    }
}
