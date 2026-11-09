namespace Ginkgo.Core.Layout;

/// <summary>
/// One thing to place: its width/height ratio, and how many columns it spans. <paramref name="Block"/> &gt; 1 on an item starts a
/// block of that many items placed together as one unit (a Connected Art set): side by side in one row across as many columns as
/// they need, or (<paramref name="Wide"/>, or too many to fit one row) the full width in rows. <paramref name="Hidden"/> items take
/// no space and are never visible (the rest of a collapsed set); they sit where the item before them is.
/// </summary>
public readonly record struct ItemSpec(float Aspect, int Span = 1, int Block = 0, bool Hidden = false, bool Wide = false);

/// <summary>The computed positions for every item, plus fast lookup of what is visible.</summary>
public sealed class MasonryResult
{
    public int Count { get; }
    public int Columns { get; }
    public float ColumnWidth { get; }
    public float Gap { get; }
    public float TotalHeight { get; }

    public float[] X { get; }
    public float[] Y { get; }
    public float[] W { get; }
    public float[] H { get; }

    private readonly ushort[] _startColumn;
    private readonly int[][] _columnItems; // item indices per column, ordered top to bottom
    internal ushort[] StartColumns => _startColumn;
    internal int[][] ColumnItems => _columnItems;

    internal MasonryResult(int count, int columns, float columnWidth, float gap, float totalHeight,
        float[] x, float[] y, float[] w, float[] h, ushort[] startColumn, int[][] columnItems)
    {
        Count = count; Columns = columns; ColumnWidth = columnWidth; Gap = gap; TotalHeight = totalHeight;
        X = x; Y = y; W = w; H = h; _startColumn = startColumn; _columnItems = columnItems;
    }

    /// <summary>Adds the index of every item that intersects [top, bottom] to <paramref name="output"/>, ascending, no duplicates.</summary>
    public void GetVisible(float top, float bottom, List<int> output)
    {
        output.Clear();
        for (int c = 0; c < Columns; c++)
        {
            var items = _columnItems[c];
            // first item whose bottom edge is at or below the top of the viewport
            int lo = 0, hi = items.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) >> 1;
                int i = items[mid];
                if (Y[i] + H[i] < top) lo = mid + 1; else hi = mid;
            }
            for (int k = lo; k < items.Length; k++)
            {
                int i = items[k];
                if (Y[i] > bottom) break;
                if (_startColumn[i] == c) output.Add(i); // spanning items appear in several columns: count once
            }
        }
        output.Sort();
    }
}

public static class MasonryLayout
{
    /// <summary>
    /// Standard shortest-column masonry. Items with Span &gt; 1 are placed where the tallest of the columns they cover is lowest.
    /// </summary>
    public static MasonryResult Compute(ReadOnlySpan<ItemSpec> items, float width, int columns, float gap)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (gap < 0) throw new ArgumentOutOfRangeException(nameof(gap));

        int n = items.Length;
        float colW = (width - gap * (columns - 1)) / columns;
        if (colW <= 0) throw new ArgumentException("Width too small for this many columns.");

        var x = new float[n]; var y = new float[n]; var w = new float[n]; var h = new float[n];
        var startCol = new ushort[n];
        var spanOf = new int[n];
        var heights = new float[columns];
        var counts = new int[columns];
        float pitch = colW + gap;

        for (int i = 0; i < n; i++)
        {
            if (items[i].Hidden)
            {
                if (i > 0) { x[i] = x[i - 1]; y[i] = y[i - 1]; }
                spanOf[i] = 0;
                continue;
            }
            if (items[i].Block > 1)
            {
                int len = Math.Min(items[i].Block, n - i);
                float sum = 0;
                for (int k = i; k < i + len; k++) sum += items[k].Aspect > 0.01f ? items[k].Aspect : 1f;
                int want = Math.Max(1, (int)MathF.Round(sum));
                bool oneRow = !items[i].Wide && want <= columns;
                int bspan = oneRow ? want : columns;
                int bs = 0; float btop = float.MaxValue;
                for (int s = 0; s + bspan <= columns; s++)
                {
                    float top = 0;
                    for (int c = s; c < s + bspan; c++) if (heights[c] > top) top = heights[c];
                    if (top < btop) { btop = top; bs = s; }
                }
                float bw = colW * bspan + gap * (bspan - 1), bx = bs * pitch;
                float bh = PlaceRows(items, i, len, bx, btop, bw, oneRow ? float.MaxValue : colW, gap, x, y, w, h);
                for (int k = i; k < i + len; k++)
                {
                    int c0 = Math.Clamp((int)((x[k] - 0.5f) / pitch + 0.0001f), 0, columns - 1);
                    if (x[k] - c0 * pitch < -0.5f) c0 = Math.Max(0, c0 - 1);
                    int c1 = Math.Clamp((int)((x[k] + w[k] - 0.5f) / pitch), c0, columns - 1);
                    startCol[k] = (ushort)c0; spanOf[k] = c1 - c0 + 1;
                    for (int c = c0; c <= c1; c++) counts[c]++;
                }
                for (int c = bs; c < bs + bspan; c++) heights[c] = btop + bh + gap;
                i += len - 1;
                continue;
            }
            int span = Math.Clamp(items[i].Span, 1, columns);
            float aspect = items[i].Aspect > 0.01f ? items[i].Aspect : 1f;
            int best = 0; float bestTop = float.MaxValue;
            if (span == 1)
            {
                for (int c = 0; c < columns; c++)
                    if (heights[c] < bestTop) { bestTop = heights[c]; best = c; }
            }
            else
            {
                for (int s = 0; s + span <= columns; s++)
                {
                    float top = 0;
                    for (int c = s; c < s + span; c++) if (heights[c] > top) top = heights[c];
                    if (top < bestTop) { bestTop = top; best = s; }
                }
            }
            float itemW = colW * span + gap * (span - 1);
            float itemH = itemW / aspect;
            x[i] = best * (colW + gap); y[i] = bestTop; w[i] = itemW; h[i] = itemH;
            startCol[i] = (ushort)best; spanOf[i] = span;
            for (int c = best; c < best + span; c++) { heights[c] = bestTop + itemH + gap; counts[c]++; }
        }

        var columnItems = new int[columns][];
        for (int c = 0; c < columns; c++) columnItems[c] = new int[counts[c]];
        var fill = new int[columns];
        for (int i = 0; i < n; i++)
            for (int c = startCol[i]; c < startCol[i] + spanOf[i]; c++) columnItems[c][fill[c]++] = i;

        float total = 0;
        for (int c = 0; c < columns; c++) if (heights[c] > total) total = heights[c];
        if (n > 0) total -= gap;
        return new MasonryResult(n, columns, colW, gap, total, x, y, w, h, startCol, columnItems);
    }

    /// <summary>
    /// Lays items [first, first+count) out in rows inside a box at (bx, top) of width bw: one row filling the width when
    /// <paramref name="target"/> is infinite, otherwise rows near the target height (the last row not stretched). Returns the height.
    /// </summary>
    internal static float PlaceRows(ReadOnlySpan<ItemSpec> items, int first, int count, float bx, float top, float bw, float target, float gap,
        float[] x, float[] y, float[] w, float[] h)
    {
        float yy = top; int start = first, end = first + count;
        if (target == float.MaxValue)
        {
            float total = 0;
            for (int k = first; k < end; k++) total += items[k].Aspect > 0.01f ? items[k].Aspect : 1f;
            float rh = (bw - gap * (count - 1)) / total, cx0 = bx;
            for (int k = first; k < end; k++)
            {
                float a = items[k].Aspect > 0.01f ? items[k].Aspect : 1f;
                x[k] = cx0; y[k] = top; h[k] = rh; w[k] = a * rh; cx0 += w[k] + gap;
            }
            return rh;
        }
        while (start < end)
        {
            float sum = 0; int stop = start; float rowH = target; bool full = false;
            while (stop < end)
            {
                sum += items[stop].Aspect > 0.01f ? items[stop].Aspect : 1f; stop++;
                rowH = (bw - gap * (stop - start - 1)) / sum;
                if (rowH <= target) { full = true; break; }
            }
            if (!full) rowH = target;
            float cx = bx;
            for (int k = start; k < stop; k++)
            {
                float a = items[k].Aspect > 0.01f ? items[k].Aspect : 1f;
                x[k] = cx; y[k] = yy; h[k] = rowH; w[k] = a * rowH;
                cx += w[k] + gap;
            }
            yy += rowH + gap;
            start = stop;
        }
        return yy - gap - top;
    }

    /// <summary>Picks a column count that gives tiles close to <paramref name="targetTileWidth"/>.</summary>
    public static int ColumnsFor(float width, float targetTileWidth, float gap)
    {
        if (targetTileWidth <= 0) return 1;
        int cols = (int)MathF.Round((width + gap) / (targetTileWidth + gap));
        return Math.Max(1, cols);
    }
}
