using SkiaSharp;

namespace Ginkgo.Data;

public sealed record ThumbResult(int Width, int Height, int ThumbWidth, int ThumbHeight, byte[] Bytes, byte[] Small, byte[] Micro);

public static class ThumbMaker
{
    // Sizes are set by WIDTH (the grid is width-driven). Extreme shapes are cropped, and tiles use the same limits.
    public const int MaxW = 320, Quality = 72;
    public const int SmallW = 192, SmallQuality = 70;
    public const int MicroW = 112, MicroQuality = 62;
    public const int LargeW = 1280, LargeQuality = 78;
    public const float MinAspect = 0.25f, MaxAspect = 4f;   // width / height limits shown in the grid

    public static float ClampAspect(int w, int h) => h <= 0 ? 1f : Math.Clamp(w / (float)h, MinAspect, MaxAspect);

    /// <summary>Reads an image once, returns its real size and three WebP thumbnails (320, 192 and 112 px wide).</summary>
    public static ThumbResult Make(string path)
    {
        using var bmp = DecodeCropped(path, MaxW, out int w, out int h);
        int tw = Math.Min(MaxW, bmp.Width);
        int th = Math.Max(1, (int)Math.Round(bmp.Height * (tw / (double)bmp.Width)));
        SKBitmap? resized = null;
        try
        {
            var final = bmp;
            if (tw != bmp.Width)
            {
                resized = bmp.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                if (resized is not null) final = resized;
            }
            return new ThumbResult(w, h, final.Width, final.Height,
                Encode(final, MaxW, Quality), Encode(final, SmallW, SmallQuality), Encode(final, MicroW, MicroQuality));
        }
        finally { resized?.Dispose(); }
    }

    /// <summary>The sharper thumbnail (up to 1280 px wide) for big tiles. Reads the original once.</summary>
    public static byte[] MakeLarge(string path)
    {
        using var bmp = DecodeLarge(path);
        return EncodeLarge(bmp);
    }

    /// <summary>Decodes the original at about 1280 px wide (cropped to the tile limits). The caller disposes the bitmap.</summary>
    public static SKBitmap DecodeLarge(string path) => DecodeCropped(path, LargeW, out _, out _);

    public static byte[] EncodeLarge(SKBitmap bmp) => Encode(bmp, LargeW, LargeQuality);

    /// <summary>Makes the small and micro sizes from an existing full-size thumbnail (no need to read the original picture).</summary>
    public static (byte[] small, byte[] micro) Derive(byte[] fullWebp)
    {
        using var src = SKBitmap.Decode(fullWebp) ?? throw new InvalidDataException("thumbnail unreadable");
        return (Encode(src, SmallW, SmallQuality), Encode(src, MicroW, MicroQuality));
    }

    private static SKBitmap DecodeCropped(string path, int targetW, out int w, out int h)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        using var codec = SKCodec.Create(fs) ?? throw new InvalidDataException("unsupported image");
        w = codec.Info.Width; h = codec.Info.Height;
        if (w <= 0 || h <= 0) throw new InvalidDataException("bad size");
        float scale = Math.Min(1f, targetW / (float)w);
        SKSizeI dec = new SKSizeI(w, h);
        if (scale < 1f)
        {
            // the decoder may round the scale DOWN; step up until the result is at least as wide as we want
            float s = scale;
            for (int i = 0; i < 10; i++)
            {
                dec = codec.GetScaledDimensions(s);
                if (dec.Width >= targetW || s >= 1f) break;
                s = Math.Min(1f, s * 1.12f);
            }
            if (dec.Width < targetW) dec = new SKSizeI(w, h);
        }
        var bmp = new SKBitmap(new SKImageInfo(dec.Width, dec.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var res = codec.GetPixels(bmp.Info, bmp.GetPixels());
        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput) { bmp.Dispose(); throw new InvalidDataException("decode failed: " + res); }
        return CropToLimits(bmp);
    }

    /// <summary>Very tall images keep their top part, very wide ones their middle, so tiles stay a sensible shape.</summary>
    private static SKBitmap CropToLimits(SKBitmap bmp)
    {
        float aspect = bmp.Width / (float)bmp.Height;
        int x = 0, y = 0, cw = bmp.Width, ch = bmp.Height;
        if (aspect < MinAspect) ch = Math.Max(1, (int)Math.Round(bmp.Width / MinAspect));
        else if (aspect > MaxAspect) { cw = Math.Max(1, (int)Math.Round(bmp.Height * MaxAspect)); x = (bmp.Width - cw) / 2; }
        else return bmp;
        var copy = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(copy)) canvas.DrawBitmap(bmp, -x, -y);
        bmp.Dispose();
        return copy;
    }

    private static byte[] Encode(SKBitmap src, int maxW, int quality)
    {
        SKBitmap? small = null;
        try
        {
            var use = src;
            if (src.Width > maxW)
            {
                int h = Math.Max(1, (int)Math.Round(src.Height * (maxW / (double)src.Width)));
                small = src.Resize(new SKImageInfo(maxW, h, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.Medium);
                if (small is not null) use = small;
            }
            using var img = SKImage.FromBitmap(use);
            using var data = img.Encode(SKEncodedImageFormat.Webp, quality);
            return data.ToArray();
        }
        finally { small?.Dispose(); }
    }
}
