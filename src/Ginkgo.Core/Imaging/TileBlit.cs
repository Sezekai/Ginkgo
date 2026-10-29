using System.Runtime.InteropServices;

namespace Ginkgo.Core.Imaging;

/// <summary>
/// Pixel copying for the picture grid: tiles are copied into one screen-sized surface, so the screen draws a single image per frame
/// instead of one per tile. All pixels are 4 bytes (premultiplied BGRA); every rectangle is clipped to the surface.
/// </summary>
public static class TileBlit
{
    private static bool Clip(int dw, int dh, int x, int y, int w, int h, out int x0, out int y0, out int x1, out int y1)
    {
        x0 = Math.Max(0, x); y0 = Math.Max(0, y); x1 = Math.Min(dw, x + w); y1 = Math.Min(dh, y + h);
        return x1 > x0 && y1 > y0;
    }

    /// <summary>Fills a rectangle with one colour.</summary>
    public static void Fill(Span<byte> dst, int stride, int dw, int dh, int x, int y, int w, int h, uint bgra)
    {
        if (!Clip(dw, dh, x, y, w, h, out int x0, out int y0, out int x1, out int y1)) return;
        int n = x1 - x0;
        var first = MemoryMarshal.Cast<byte, uint>(dst.Slice(y0 * stride + x0 * 4, n * 4));
        first.Fill(bgra);
        var firstBytes = dst.Slice(y0 * stride + x0 * 4, n * 4);
        for (int yy = y0 + 1; yy < y1; yy++) firstBytes.CopyTo(dst.Slice(yy * stride + x0 * 4, n * 4));
    }

    /// <summary>Copies a picture 1:1 with its top-left corner at (x, y).</summary>
    public static void Copy(Span<byte> dst, int stride, int dw, int dh, int x, int y, ReadOnlySpan<byte> src, int sw, int sh)
    {
        if (!Clip(dw, dh, x, y, sw, sh, out int x0, out int y0, out int x1, out int y1)) return;
        int n = (x1 - x0) * 4;
        for (int yy = y0; yy < y1; yy++)
            src.Slice(((yy - y) * sw + (x0 - x)) * 4, n).CopyTo(dst.Slice(yy * stride + x0 * 4, n));
    }

    /// <summary>Copies a picture stretched (nearest pixel) into the rectangle (x, y, w, h).</summary>
    public static void CopyScaled(Span<byte> dst, int stride, int dw, int dh, int x, int y, int w, int h, ReadOnlySpan<byte> src, int sw, int sh)
    {
        if (w <= 0 || h <= 0 || !Clip(dw, dh, x, y, w, h, out int x0, out int y0, out int x1, out int y1)) return;
        var s = MemoryMarshal.Cast<byte, uint>(src);
        for (int yy = y0; yy < y1; yy++)
        {
            int sy = (int)((long)(yy - y) * sh / h);
            var drow = MemoryMarshal.Cast<byte, uint>(dst.Slice(yy * stride, dw * 4));
            int srow = sy * sw;
            for (int xx = x0; xx < x1; xx++) drow[xx] = s[srow + (int)((long)(xx - x) * sw / w)];
        }
    }

    /// <summary>
    /// Cross-fade: shows <paramref name="prev"/> (or a plain colour when there is none) turning into <paramref name="next"/>.
    /// <paramref name="alpha"/> is 0 (all previous) to 256 (all new). Both pictures are stretched to the rectangle.
    /// </summary>
    public static void Blend(Span<byte> dst, int stride, int dw, int dh, int x, int y, int w, int h,
        ReadOnlySpan<byte> next, int nw, int nh, ReadOnlySpan<byte> prev, int pw, int ph, int alpha, uint plain)
    {
        if (w <= 0 || h <= 0 || !Clip(dw, dh, x, y, w, h, out int x0, out int y0, out int x1, out int y1)) return;
        var n = MemoryMarshal.Cast<byte, uint>(next);
        var p = MemoryMarshal.Cast<byte, uint>(prev);
        bool hasPrev = pw > 0 && ph > 0 && !prev.IsEmpty;
        alpha = Math.Clamp(alpha, 0, 256);
        for (int yy = y0; yy < y1; yy++)
        {
            var drow = MemoryMarshal.Cast<byte, uint>(dst.Slice(yy * stride, dw * 4));
            int ny = (int)((long)(yy - y) * nh / h) * nw, py = hasPrev ? (int)((long)(yy - y) * ph / h) * pw : 0;
            for (int xx = x0; xx < x1; xx++)
            {
                uint a = n[ny + (int)((long)(xx - x) * nw / w)];
                uint b = hasPrev ? p[py + (int)((long)(xx - x) * pw / w)] : plain;
                drow[xx] = Mix(b, a, alpha);
            }
        }
    }

    private static uint Mix(uint from, uint to, int alpha)
    {
        // two colour channels at a time: (from * (256 - alpha) + to * alpha) / 256
        uint ua = (uint)alpha, ia = 256 - ua;
        uint rb = ((from & 0x00FF00FFu) * ia + (to & 0x00FF00FFu) * ua >> 8) & 0x00FF00FFu;
        uint ag = (((from >> 8) & 0x00FF00FFu) * ia + ((to >> 8) & 0x00FF00FFu) * ua) & 0xFF00FF00u;
        return rb | ag;
    }

    public static uint Pack(byte r, byte g, byte b) => 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
}
