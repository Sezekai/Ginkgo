namespace Ginkgo.Core.Layout;

/// <summary>
/// Folder tiles as a grid of equal "book covers" (picture on top, name and details underneath), followed by the folder's files
/// in their usual layout. The two parts are joined into one result so the view can scroll and look up tiles as usual.
/// </summary>
public static class CoverGridLayout
{
    /// <param name="gridCount">How many leading items are covers.</param>
    /// <param name="target">Wanted cover width.</param>
    /// <param name="aspect">Cover width / height (e.g. 0.7 for a book cover).</param>
    /// <param name="captionHeight">Room under each cover for its name and details.</param>
    /// <param name="gap">Space between covers, sideways and between rows.</param>
    /// <param name="rest">Layout of the items after the covers (indices 0.. in it are items gridCount.. here).</param>
    /// <param name="sectionGap">Space between the covers and the files.</param>
    public static MasonryResult Compose(int gridCount, float width, float target, float aspect, float captionHeight, float gap, MasonryResult? rest, float sectionGap)
    {
        int restCount = rest?.Count ?? 0;
        int n = gridCount + restCount;
        int cols = gridCount == 0 ? 0 : Math.Max(1, (int)MathF.Round((width + gap) / (target + gap)));
        float cellW = cols == 0 ? 0 : (width - gap * (cols - 1)) / cols;
        float cellH = aspect > 0.01f ? cellW / aspect : cellW;
        float pitch = cellH + captionHeight + gap;
        int rows = cols == 0 ? 0 : (gridCount + cols - 1) / cols;
        float gridHeight = rows * pitch - (rows > 0 ? gap : 0);

        var x = new float[n]; var y = new float[n]; var w = new float[n]; var h = new float[n];
        var start = new ushort[n];
        int restCols = rest?.Columns ?? 0;
        var columnItems = new int[cols + restCols][];
        for (int c = 0; c < cols; c++) columnItems[c] = new int[gridCount / cols + (c < gridCount % cols ? 1 : 0)];
        var fill = new int[Math.Max(1, cols)];
        for (int k = 0; k < gridCount; k++)
        {
            int c = k % cols, r = k / cols;
            x[k] = c * (cellW + gap); y[k] = r * pitch; w[k] = cellW; h[k] = cellH;
            start[k] = (ushort)c;
            columnItems[c][fill[c]++] = k;
        }
        float shift = gridCount > 0 && restCount > 0 ? gridHeight + sectionGap : 0;
        if (rest is not null)
        {
            for (int k = 0; k < restCount; k++)
            {
                int i = gridCount + k;
                x[i] = rest.X[k]; y[i] = rest.Y[k] + shift; w[i] = rest.W[k]; h[i] = rest.H[k];
                start[i] = (ushort)(cols + rest.StartColumns[k]);
            }
            for (int c = 0; c < restCols; c++)
            {
                var src = rest.ColumnItems[c];
                var dst = new int[src.Length];
                for (int j = 0; j < src.Length; j++) dst[j] = src[j] + gridCount;
                columnItems[cols + c] = dst;
            }
        }
        float total = restCount > 0 ? shift + rest!.TotalHeight : gridHeight;
        return new MasonryResult(n, cols + restCols, cellW, gap, total, x, y, w, h, start, columnItems);
    }
}
