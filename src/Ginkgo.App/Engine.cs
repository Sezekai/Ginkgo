using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Ginkgo.Core;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// The background half of the app: keeps the index and the thumbnails up to date without ever blocking the screen.
/// Order at start-up: open the index, scan the library, make missing thumbnails, fill in file IDs, then watch for changes.
/// </summary>
public sealed class Engine : IDisposable
{
    public AppSettings Settings { get; }
    public IndexDb? Db { get; private set; }
    public ThumbStores? Stores { get; private set; }
    public PipelineStats Pipe { get; } = new();
    public volatile string Status = "starting...";
    /// <summary>Reading each file's identity from the drive (this is what makes the disk busy after an update).</summary>
    public volatile bool IdsRunning; public int IdsDone, IdsTotal; public volatile bool IdsFinished;
    public volatile bool LibraryOffline;
    /// <summary>A scan wanted to hide a large share of the library and was stopped (null = no).</summary>
    public ScanResult? SafetyStop { get; private set; }

    /// <summary>The index and cache are open (raised on a background thread).</summary>
    public event Action? Opened;
    /// <summary>Something in the library changed (a scan found changes, or thumbnails were made).</summary>
    public event Action? DataChanged;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private LibraryWatcher? _watcher;
    private readonly ConcurrentQueue<(long id, int pack, long off, int len)> _largeQueue = new();
    private Timer? _saveTimer;
    private int _saving;
    private int _changesSinceView;
    public int ChangesSinceView => Volatile.Read(ref _changesSinceView);
    public void ViewRefreshed() => Interlocked.Exchange(ref _changesSinceView, 0);

    public const long NoCap = long.MaxValue;

    public Engine(AppSettings settings) { Settings = settings; }

    public IEnumerable<string> Excluded => MediaTypes.DefaultExcludedTopFolders;

    public void Start() => Task.Run(Run);

    private async Task Run()
    {
        try
        {
            Db = new IndexDb(AppSettings.IndexPath);
            if (Db.LargeInvalidated && Directory.Exists(Settings.CacheDir))
                foreach (var f in Directory.EnumerateFiles(Settings.CacheDir, "large-*.bin")) { try { File.Delete(f); } catch { } }
            string thumbDir = ChooseThumbDir();
            ThumbDir = thumbDir;
            Stores = new ThumbStores(thumbDir, Settings.CacheDir);
            _saveTimer = new Timer(_ => SaveLargeBatch(), null, 2000, 2000);
            if (Settings.WarmCache) Warmer.Start(Stores, _cts.Token);
            Opened?.Invoke();
            await Refresh(scan: true, confirmLargeRemoval: false);
            StartWatcher();
        }
        catch (Exception ex) { Status = "problem: " + ex.Message; }
    }

    /// <summary>Where the micro/small/full thumbnails are being read from and written to.</summary>
    public volatile string ThumbDir = "";
    /// <summary>Set while the small thumbnails are being copied to the fast drive (one time).</summary>
    public volatile bool Moving; public long MoveDone, MoveTotal;
    public volatile string FastCacheNote = "";
    public CacheWarmer Warmer { get; } = new();

    private static string Root(string path) { try { return System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)) ?? ""; } catch { return ""; } }

    /// <summary>
    /// Picks the folder for the small thumbnails. On the first start with a fast drive available they are copied there (one time,
    /// with progress); if there is not enough room, or anything goes wrong, the old place keeps being used.
    /// </summary>
    private string ChooseThumbDir()
    {
        string old = Settings.CacheDir, fast = Settings.FastCacheDir;
        try
        {
            if (!Settings.UseFastCache || string.IsNullOrWhiteSpace(fast) || string.Equals(Root(fast), Root(old), StringComparison.OrdinalIgnoreCase)) return old;
            if (CacheMigration.IsDone(fast)) return fast;
            var plan = CacheMigration.Plan(old);
            if (plan.Items.Count == 0) { CacheMigration.MarkEmpty(fast); return fast; }      // nothing built yet: start on the fast drive
            long free = new DriveInfo(Root(fast)).AvailableFreeSpace;
            long need = plan.TotalBytes + (6L << 30);                                        // leave room to grow
            if (free < need)
            {
                FastCacheNote = $"not enough room on {Root(fast)} to move the thumbnails ({plan.TotalBytes / 1048576.0 / 1024:F1} GB needed + 6 GB spare)";
                return old;
            }
            MoveTotal = plan.TotalBytes; MoveDone = 0; Moving = true;
            Status = "moving thumbnails to the computer's own drive (one time)...";
            CacheMigration.Run(plan, fast, (d, t) => { MoveDone = d; MoveTotal = t; }, _cts.Token);
            return fast;
        }
        catch (Exception ex) { FastCacheNote = "could not move the thumbnails: " + ex.Message; return old; }
        finally { Moving = false; }
    }

    /// <summary>Scans the library (optional), makes missing thumbnails and fills in IDs.</summary>
    public async Task Refresh(bool scan, bool confirmLargeRemoval)
    {
        if (Db is null || Stores is null) return;
        await _work.WaitAsync(_cts.Token);
        try
        {
            var ct = _cts.Token;
            if (scan)
            {
                if (!Directory.Exists(Settings.LibraryRoot)) { LibraryOffline = true; Status = "library folder not found: " + Settings.LibraryRoot; }
                else
                {
                    LibraryOffline = false;
                    Status = "looking for changes in the library...";
                    var res = LibraryScanner.Scan(Db, Settings.LibraryRoot, Excluded, n => Status = $"looking... {n:N0} files seen", ct,
                        new ScanOptions { AcceptLargeRemoval = confirmLargeRemoval });
                    SafetyStop = res.SafetyTripped ? res : null;
                    int changes = res.Added + res.Changed + res.Removed + res.Moved + res.Restored + res.Purged;
                    Status = res.RootMissing ? "library folder not found" :
                        $"{res.Total:N0} files | +{res.Added:N0} new, {res.Moved:N0} moved, {res.Changed:N0} changed, {res.Removed:N0} hidden, {res.Restored:N0} back in {res.Seconds:F0}s";
                    if (changes > 0) { Interlocked.Add(ref _changesSinceView, changes); DataChanged?.Invoke(); }
                }
            }
            IdsRunning = true; IdsFinished = false;
            try { FileIdBackfill.Run(Db, Settings.LibraryRoot, FileIdProviders.Default, (d, t) => { IdsDone = d; IdsTotal = t; }, ct); IdsFinished = true; }
            finally { IdsRunning = false; }
            await MakeThumbnails(ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status = "problem: " + ex.Message; }
        finally { _work.Release(); }
    }

    private async Task MakeThumbnails(CancellationToken ct)
    {
        if (Db is null || Stores is null) return;
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
        long before = Pipe.Done;
        await ThumbnailPipeline.RunAsync(Db, Stores, Pipe, workers, ct);
        await TierBackfill.RunAsync(Db, Stores, Pipe, Math.Clamp(workers / 2, 2, 3), ct);
        long made = Pipe.Done - before;
        if (made > 0) { Interlocked.Add(ref _changesSinceView, (int)Math.Min(made, int.MaxValue / 2)); DataChanged?.Invoke(); }
    }

    private void StartWatcher()
    {
        if (Db is null || LibraryOffline || _watcher is not null) return;
        string cache = Path.GetFullPath(Settings.CacheDir);
        try
        {
            _watcher = new LibraryWatcher(Db, Settings.LibraryRoot, Excluded,
                p => p.StartsWith(cache, StringComparison.OrdinalIgnoreCase), new ScanOptions());
            _watcher.Scanned += async r =>
            {
                int changes = r.Added + r.Changed + r.Removed + r.Moved + r.Restored;
                if (r.SafetyTripped) SafetyStop = r;
                if (changes == 0) return;
                Interlocked.Add(ref _changesSinceView, changes);
                DataChanged?.Invoke();
                await Refresh(scan: false, confirmLargeRemoval: false);
            };
        }
        catch (Exception ex) { Status += " | watcher problem: " + ex.Message; }
    }

    public Task Rescan() => Refresh(scan: true, confirmLargeRemoval: false);
    public Task ConfirmLargeRemoval() { SafetyStop = null; return Refresh(scan: true, confirmLargeRemoval: true); }
    public void DismissSafetyStop() => SafetyStop = null;

    // ---- sharp versions made on demand are remembered in the index in small batches
    public void QueueLarge(long id, int pack, long off, int len) => _largeQueue.Enqueue((id, pack, off, len));

    public void SaveLargeBatch()
    {
        if (Db is null || Stores is null || _largeQueue.IsEmpty || Interlocked.Exchange(ref _saving, 1) == 1) return;
        try
        {
            var rows = new List<(long id, int pack, long off, int len)>();
            while (rows.Count < 2000 && _largeQueue.TryDequeue(out var r)) rows.Add(r);
            IndexUpdates.SaveLarge(Db, Stores, rows);
        }
        catch { }
        finally { Volatile.Write(ref _saving, 0); }
    }

    // ---- maintenance
    public async Task PrepareSharp(bool everything, CancellationToken ct)
    {
        if (Db is null || Stores is null) return;
        SaveLargeBatch();
        long big = everything ? 1 : SharpPrebuild.DefaultBigFileBytes;
        var (n, bytes) = SharpPrebuild.Count(Db, big);
        Status = $"preparing sharp versions: {n:N0} originals ({bytes / 1e9:F1} GB to read)";
        await SharpPrebuild.RunAsync(Db, Stores, Pipe, big, NoCap, 3, ct);
        Status = "sharp versions ready";
        Interlocked.Increment(ref _changesSinceView);
        DataChanged?.Invoke();
    }

    public IReadOnlyList<TierUsage> CacheUsage() => Db is null || Stores is null ? Array.Empty<TierUsage>() : CacheCleanup.Measure(Db, Stores);

    public CompactResult? CleanUpCache()
    {
        if (Db is null || Stores is null) return null;
        SaveLargeBatch();
        var r = CacheCleanup.Compact(Db, Stores, 0.0, s => Status = s, _cts.Token);
        Interlocked.Increment(ref _changesSinceView);
        DataChanged?.Invoke();
        return r;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _watcher?.Dispose();
        _saveTimer?.Dispose();
        SaveLargeBatch();
        Stores?.Dispose();
    }
}
