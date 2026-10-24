using SkiaSharp;

namespace Ginkgo.Data;

public sealed record ThumbResult(int Width, int Height, int ThumbWidth, int ThumbHeight, byte[] Bytes, byte[] Small, byte[] Micro);

public static class ThumbMaker
{
    public const int MaxW = 320, MaxH = 640, Quality = 72;
    public const int SmallW = 192, SmallH = 384, SmallQuality = 70;
    public const int MicroW = 112, MicroH = 224, MicroQuality = 62;
    public const int LargeW = 960, LargeH = 1600, LargeQuality = 78;

    /// <summary>Reads an image once, returns its real size and three WebP thumbnails (320, 192 and 112 px wide).</summary>
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
            return new ThumbResult(w, h, final.Width, final.Height,
                Encode(final, final.Width, final.Height, Quality),
                Encode(final, SmallW, SmallH, SmallQuality),
                Encode(final, MicroW, MicroH, MicroQuality));
        }
        finally { resized?.Dispose(); }
    }

    /// <summary>The sharper thumbnail (up to 960 px wide) for big tiles. Reads the original once.</summary>
    public static byte[] MakeLarge(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var codec = SKCodec.Create(fs) ?? throw new InvalidDataException("unsupported image");
        int w = codec.Info.Width, h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new InvalidDataException("bad size");
        float scale = Math.Min(1f, Math.Min(LargeW / (float)w, LargeH / (float)h));
        SKSizeI dec = scale < 1f ? codec.GetScaledDimensions(scale) : new SKSizeI(w, h);
        using var bmp = new SKBitmap(new SKImageInfo(dec.Width, dec.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var res = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) throw new InvalidDataException("decode failed: " + res);
        return Encode(bmp, LargeW, LargeH, LargeQuality);
    }

    /// <summary>Makes the small and micro sizes from an existing full-size thumbnail (no need to read the original picture).</summary>
    public static (byte[] small, byte[] micro) Derive(byte[] fullWebp)
    {
        using var src = SKBitmap.Decode(fullWebp) ?? throw new InvalidDataException("thumbnail unreadable");
        return (Encode(src, SmallW, SmallH, SmallQuality), Encode(src, MicroW, MicroH, MicroQuality));
    }

    private static byte[] Encode(SKBitmap src, int maxW, int maxH, int quality)
    {
        float scale = Math.Min(1f, Math.Min(maxW / (float)src.Width, maxH / (float)src.Height));
        SKBitmap? small = null;
        try
        {
            var use = src;
            if (scale < 1f)
            {
                int w = Math.Max(1, (int)Math.Round(src.Width * scale)), h = Math.Max(1, (int)Math.Round(src.Height * scale));
                small = src.Resize(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                if (small is not null) use = small;
            }
            using var img = SKImage.FromBitmap(use);
            using var data = img.Encode(SKEncodedImageFormat.Webp, quality);
            return data.ToArray();
        }
        finally { small?.Dispose(); }
    }
}
