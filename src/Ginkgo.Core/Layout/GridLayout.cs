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
