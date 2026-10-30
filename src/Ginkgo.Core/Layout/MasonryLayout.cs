namespace Ginkgo.Core.Layout;

/// <summary>One thing to place: its width/height ratio, and how many columns it spans (sets span more than one).</summary>
public readonly record struct ItemSpec(float Aspect, int Span = 1);

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
        var heights = new float[columns];
        var counts = new int[columns];

        for (int i = 0; i < n; i++)
        {
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
            startCol[i] = (ushort)best;
            for (int c = best; c < best + span; c++) { heights[c] = bestTop + itemH + gap; counts[c]++; }
        }

        var columnItems = new int[columns][];
        for (int c = 0; c < columns; c++) columnItems[c] = new int[counts[c]];
        var fill = new int[columns];
        for (int i = 0; i < n; i++)
        {
            int span = Math.Clamp(items[i].Span, 1, columns);
            for (int c = startCol[i]; c < startCol[i] + span; c++) columnItems[c][fill[c]++] = i;
        }

        float total = 0;
        for (int c = 0; c < columns; c++) if (heights[c] > total) total = heights[c];
        if (n > 0) total -= gap;
        return new MasonryResult(n, columns, colW, gap, total, x, y, w, h, startCol, columnItems);
    }

    /// <summary>Picks a column count that gives tiles close to <paramref name="targetTileWidth"/>.</summary>
    public static int ColumnsFor(float width, float targetTileWidth, float gap)
    {
        if (targetTileWidth <= 0) return 1;
        int cols = (int)MathF.Round((width + gap) / (targetTileWidth + gap));
        return Math.Max(1, cols);
    }
}
