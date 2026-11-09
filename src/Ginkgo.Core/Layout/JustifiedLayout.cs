namespace Ginkgo.Core.Layout;

/// <summary>
/// Rows of pictures that all share one height and together span the full width (the way photo sites lay out wide pictures).
/// The last row is left at the target height instead of being stretched.
/// </summary>
public static class JustifiedLayout
{
    public static MasonryResult Compute(ReadOnlySpan<ItemSpec> items, float width, float targetRowHeight, float gap)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (targetRowHeight <= 0) throw new ArgumentOutOfRangeException(nameof(targetRowHeight));
        if (gap < 0) throw new ArgumentOutOfRangeException(nameof(gap));
        int n = items.Length;
        var x = new float[n]; var y = new float[n]; var w = new float[n]; var h = new float[n];
        float top = 0;
        int start = 0;
        while (start < n)
        {
            float sum = 0; int end = start;
            float rowH = targetRowHeight;
            bool full = false;
            while (end < n)
            {
                if (items[end].Hidden) { end++; continue; }
                float a = items[end].Aspect > 0.01f ? items[end].Aspect : 1f;
                sum += a; end++;
                int shown = 0; for (int k = start; k < end; k++) if (!items[k].Hidden) shown++;
                rowH = (width - gap * (shown - 1)) / sum;
                if (rowH <= targetRowHeight) { full = true; break; }      // as many as fit while staying at or under the target height
            }
            while (end < n && items[end].Hidden) end++;                     // a collapsed set's hidden members stay with its tile
            if (!full) rowH = targetRowHeight;                             // a short last row keeps the target height, left aligned
            bool any = false; for (int k = start; k < end; k++) if (!items[k].Hidden) { any = true; break; }
            float cx = 0;
            for (int i = start; i < end; i++)
            {
                if (items[i].Hidden) { x[i] = i > 0 ? x[i - 1] : 0; y[i] = i > 0 ? y[i - 1] : top; continue; }
                float a = items[i].Aspect > 0.01f ? items[i].Aspect : 1f;
                x[i] = cx; y[i] = top; h[i] = rowH; w[i] = a * rowH;
                cx += w[i] + gap;
            }
            if (any) top += rowH + gap;
            start = end;
        }
        float total = n > 0 ? top - gap : 0;
        // one "column" holding everything in reading order lets the same fast visible-range search work
        var all = new List<int>(n);
        for (int i = 0; i < n; i++) if (!items[i].Hidden) all.Add(i);
        return new MasonryResult(n, 1, width, gap, total, x, y, w, h, new ushort[n], new[] { all.ToArray() });
    }
}
