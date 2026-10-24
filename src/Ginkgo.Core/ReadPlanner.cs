namespace Ginkgo.Core;

/// <summary>
/// Hard drives are slow at jumping around and fast at reading one stretch. When one thumbnail is needed, this works out
/// a single stretch of the pack file that also covers the thumbnails next to it in the list, so one read fetches many.
/// </summary>
public static class ReadPlanner
{
    public readonly record struct Plan(int Pack, long Start, int Length, int FirstIndex, int LastIndex);

    public static Plan Make(int[] pack, long[] off, int[] len, int i, int before = 8, int after = 32, int maxBytes = 512 * 1024)
    {
        long start = off[i], end = off[i] + len[i];
        int p = pack[i];
        // grow outward one neighbour at a time, nearest first, while it stays in the same pack and under the size limit
        for (int d = 1; d <= Math.Max(before, after); d++)
        {
            foreach (int j in new[] { i - d, i + d })
            {
                if (j < 0 || j >= pack.Length || len[j] <= 0 || pack[j] != p) continue;
                if (j < i && d > before) continue;
                if (j > i && d > after) continue;
                long ns = Math.Min(start, off[j]), ne = Math.Max(end, off[j] + len[j]);
                if (ne - ns > maxBytes) continue;
                start = ns; end = ne;
            }
        }
        int first = i, last = i;
        for (int j = Math.Max(0, i - before); j <= Math.Min(pack.Length - 1, i + after); j++)
        {
            if (len[j] > 0 && pack[j] == p && off[j] >= start && off[j] + len[j] <= end) { if (j < first) first = j; if (j > last) last = j; }
        }
        return new Plan(p, start, (int)(end - start), first, last);
    }

    public static bool Covers(in Plan plan, int[] pack, long[] off, int[] len, int j) =>
        len[j] > 0 && pack[j] == plan.Pack && off[j] >= plan.Start && off[j] + len[j] <= plan.Start + plan.Length;
}
