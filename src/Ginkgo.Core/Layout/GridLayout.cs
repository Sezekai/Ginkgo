namespace Ginkgo.Core.Layout;

/// <summary>Uniform cells in rows. Visible range is just arithmetic.</summary>
public sealed class GridLayout
{
    public int Count { get; }
    public int Columns { get; }
    public float CellWidth { get; }
    public float CellHeight { get; }
    public float Gap { get; }
    public float TotalHeight { get; }

    public GridLayout(int count, float width, int columns, float cellAspect, float gap)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        Count = count; Columns = columns; Gap = gap;
        CellWidth = (width - gap * (columns - 1)) / columns;
        CellHeight = CellWidth / (cellAspect > 0.01f ? cellAspect : 1f);
        int rows = (count + columns - 1) / columns;
        TotalHeight = rows == 0 ? 0 : rows * CellHeight + (rows - 1) * gap;
    }

    public (float X, float Y) Position(int index)
    {
        int row = index / Columns, col = index % Columns;
        return (col * (CellWidth + Gap), row * (CellHeight + Gap));
    }

    /// <summary>Half-open range of item indices whose cells intersect [top, bottom].</summary>
    public (int First, int EndExclusive) VisibleRange(float top, float bottom)
    {
        if (Count == 0) return (0, 0);
        float pitch = CellHeight + Gap;
        int firstRow = Math.Max(0, (int)MathF.Floor((top - CellHeight) / pitch) + 1);
        int lastRow = Math.Max(firstRow, (int)MathF.Floor(bottom / pitch));
        int first = Math.Min(Count, firstRow * Columns);
        int end = Math.Min(Count, (lastRow + 1) * Columns);
        return (first, end);
    }
}

/// <summary>
/// The Grid layout: every tile gets the same square cell. <paramref name="fit"/>: the picture keeps its shape inside its cell (centred);
/// otherwise it fills the cell (the view crops it). Hidden items take no space, as in the other layouts.
/// </summary>
public static class UniformGrid
{
    public static MasonryResult Compute(ReadOnlySpan<ItemSpec> items, float width, int columns, float gap, bool fit)
    {
        if (columns < 1) throw new ArgumentOutOfRangeException(nameof(columns));
        int n = items.Length;
        float cell = Math.Max(1, (width - gap * (columns - 1)) / columns);
        var x = new float[n]; var y = new float[n]; var w = new float[n]; var h = new float[n];
        var start = new ushort[n];
        var lists = new List<int>[columns];
        for (int c = 0; c < columns; c++) lists[c] = new List<int>();
        int k = 0;
        for (int i = 0; i < n; i++)
        {
            if (items[i].Hidden) { if (i > 0) { x[i] = x[i - 1]; y[i] = y[i - 1]; } continue; }
            int row = k / columns, col = k % columns; k++;
            float cx = col * (cell + gap), cy = row * (cell + gap);
            float a = items[i].Aspect > 0.01f ? items[i].Aspect : 1f;
            if (fit)
            {
                float fw = a >= 1 ? cell : cell * a, fh = a >= 1 ? cell / a : cell;
                x[i] = cx + (cell - fw) / 2; y[i] = cy + (cell - fh) / 2; w[i] = fw; h[i] = fh;
            }
            else { x[i] = cx; y[i] = cy; w[i] = cell; h[i] = cell; }
            start[i] = (ushort)col;
            lists[col].Add(i);
        }
        int rows = (k + columns - 1) / columns;
        float total = rows == 0 ? 0 : rows * cell + (rows - 1) * gap;
        return new MasonryResult(n, columns, cell, gap, total, x, y, w, h, start, lists.Select(l => l.ToArray()).ToArray());
    }
}
