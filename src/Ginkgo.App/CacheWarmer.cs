using System.IO;
using System.Runtime.InteropServices;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// Reads the thumbnail files from start to finish once, in the background, so Windows keeps them in its own file cache (memory it
/// gives back the moment another program needs it). A hard drive reads one long stretch quickly but takes ~10 ms for every
/// separate small read; a shuffled grid asks for hundreds of separate thumbnails, which is slow from the drive and instant from
/// the file cache. It runs at the lowest disk priority, stops while the screen is busy, and never uses memory that is running short.
/// </summary>
public sealed class CacheWarmer
{
    public volatile bool Running; public long Done, Total; public volatile string Note = "";

    [StructLayout(LayoutKind.Sequential)]
    private struct MemStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirtual, AvailVirtual, AvailExt; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemStatus s);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern bool SetThreadPriority(IntPtr h, int p);
    private const int BackgroundBegin = 0x10000;

    private static long AvailableBytes() { var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() }; return GlobalMemoryStatusEx(ref m) ? (long)m.AvailPhys : long.MaxValue; }

    /// <param name="dir">Where the thumbnail files are.</param>
    /// <param name="screenBusy">True while the person is scrolling (the warm-up steps aside).</param>
    /// <param name="spareBytes">Memory that must stay free.</param>
    public void Start(string dir, Func<bool> screenBusy, long spareBytes, CancellationToken ct)
    {
        var t = new Thread(() => Run(dir, screenBusy, spareBytes, ct)) { IsBackground = true, Name = "cache-warm", Priority = ThreadPriority.Lowest };
        t.Start();
    }

    private void Run(string dir, Func<bool> screenBusy, long spare, CancellationToken ct)
    {
        try
        {
            SetThreadPriority(GetCurrentThread(), BackgroundBegin);       // lowest disk priority as well: real reads always go first
            var files = new List<string>();
            foreach (var prefix in new[] { "micro", "small", "pack" })   // smallest (most used at small tile sizes) first
                if (Directory.Exists(dir)) files.AddRange(Directory.EnumerateFiles(dir, prefix + "-*.bin").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            Total = files.Sum(f => new FileInfo(f).Length); Done = 0; Running = true;
            var buf = new byte[2 << 20];
            foreach (var f in files)
            {
                long size = new FileInfo(f).Length;
                if (AvailableBytes() - size < spare) { Note = "stopped: memory is limited"; break; }
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.None);
                int n;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    Done += n;
                    while (screenBusy() && !ct.IsCancellationRequested) Thread.Sleep(100);
                    if (AvailableBytes() < spare) { Note = "stopped: memory is limited"; return; }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Note = "stopped: " + ex.Message; }
        finally { Running = false; }
    }
}
