using System.Diagnostics;

namespace Ginkgo.App;

/// <summary>
/// Where the UI thread's time goes (for the F3 readout). Every piece of work we do on the UI thread adds its duration here, so a
/// long gap between screen refreshes can be told apart: our own work, the garbage collector, or something outside our code.
/// </summary>
public static class Perf
{
    public static long BusyTicks;
    public static double RenderMax, PaintMax, PrepMax, DrainMax, TickMax;
    public static int PaintedBands;
    public static double GpuDrawMax, GpuUploadMax;
    public static int GpuUploads;
    public static void Add(long ticks) => BusyTicks += ticks;
    public static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    public static void Reset() { RenderMax = PaintMax = PrepMax = DrainMax = TickMax = 0; PaintedBands = 0; GpuDrawMax = GpuUploadMax = 0; GpuUploads = 0; }
}
