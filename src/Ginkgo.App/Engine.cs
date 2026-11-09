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

    public IEnumerable<string> Excluded => Settings.ExcludedFolders;
    /// <summary>What the one-time move is moving ("thumbnails" to the fast drive, or the whole "cache").</summary>
    public volatile string MovingWhat = "thumbnails";

    public void Start() => Task.Run(Run);

    private async Task Run()
    {
        try
        {
            DoPendingResets();
            if (Settings.PendingCacheMove.Length > 0) MoveCache();
            Db = new IndexDb(AppSettings.IndexPath);
            if (Settings.PendingResets.Remove("thumbnails")) ForgetThumbnails();
            if (Settings.PendingRelocateFrom.Length > 0)
            {
                int n = Db.Relocate(Settings.PendingRelocateFrom, Settings.LibraryRoot);
                Status = $"library moved: {n:N0} files matched in the new place";
                Settings.PendingRelocateFrom = ""; Settings.Save();
            }
            if (Db.LargeInvalidated && Directory.Exists(Settings.CacheDir))
                foreach (var f in Directory.EnumerateFiles(Settings.CacheDir, "large-*.bin")) { try { File.Delete(f); } catch { } }
            string thumbDir = ChooseThumbDir();
            ThumbDir = thumbDir;
            Stores = new ThumbStores(thumbDir, Settings.CacheDir);
            _saveTimer = new Timer(_ => SaveLargeBatch(), null, 2000, 2000);
            Warmer.Enabled = Settings.WarmCache;
            Warmer.Cap = (long)(Settings.ThumbMemoryCapGB * 1073741824.0);
            Warmer.VramMode = Settings.ThumbVramMode;
            Warmer.VramOnBattery = Settings.ThumbVramOnBattery;
            Warmer.Start(Stores, _cts.Token);
            GifMaker = new GifRenditionMaker(Db, Settings.CacheDir, Pipe) { CapMs = (int)(Settings.GifLengthSec * 1000) };
            // the GIF versions live on the (slow, USB) hard drive too: keep them in memory or on the card like the thumbnails, so a
            // screenful of GIFs doesn't wait for one drive read each
            Warmer.Extra = new[] { GifMaker.Small, GifMaker.Large, GifMaker.Big };
            Warmer.ExtraEnabled = Settings.GifWarm;
            Warmer.ExtraCap = (long)(Settings.GifWarmCapGB * 1073741824.0);
            GifMaker.BigCapBytes = (long)(Settings.GifBigCapGB * 1073741824.0);
            Opened?.Invoke();
            await Refresh(scan: true, confirmLargeRemoval: false);
            StartWatcher();
            StartDetails();
        }
        catch (Exception ex) { Status = "problem: " + ex.Message; }
    }

    /// <summary>Rebuild the index (asked for in Settings > Resets): the old one is set aside before it is opened.</summary>
    private void DoPendingResets()
    {
        if (!Settings.PendingResets.Remove("index")) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            string f = AppSettings.IndexPath + suffix;
            try { if (File.Exists(f)) File.Move(f, f + ".old", true); } catch { }
        }
        if (!Settings.PendingResets.Contains("thumbnails")) Settings.PendingResets.Add("thumbnails");    // the new index can't find the old ones
        Settings.Save();
    }

    /// <summary>Clear thumbnails: the thumbnail files go, and every file is made again in the background.</summary>
    private void ForgetThumbnails()
    {
        foreach (var dir in new[] { Settings.CacheDir, Settings.FastCacheDir }.Distinct())
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.bin")) { try { File.Delete(f); } catch { } }
            }
            catch { }
        }
        using (var c = Db!.Open())
            IndexDb.Exec(c, "UPDATE files SET state=0, thumb_pack=0, thumb_off=0, thumb_len=0, s_pack=0, s_off=0, s_len=0, m_pack=0, m_off=0, m_len=0, l_pack=0, l_off=0, l_len=0, vhash=0");
        using (var c = Db!.Open()) { try { IndexDb.Exec(c, "DELETE FROM gif_renditions"); } catch { } }       // the GIF versions went with the files
        Settings.Save();
    }

    /// <summary>Move cache (Settings): every cache file is copied to the new folder with progress, then the old ones go.</summary>
    private void MoveCache()
    {
        string to = Settings.PendingCacheMove;
        try
        {
            MovingWhat = "cache"; MoveDone = 0; MoveTotal = 1; Moving = true;
            Status = "moving the cache to " + to + "...";
            CacheMigration.MoveAll(Settings.CacheDir, to, (d, t) => { MoveDone = d; MoveTotal = t; }, _cts.Token);
            Settings.CacheDir = to;
            Status = "cache moved to " + to;
        }
        catch (Exception ex) { Status = "could not move the cache (it stays where it was): " + ex.Message; }
        finally { Moving = false; MovingWhat = "thumbnails"; Settings.PendingCacheMove = ""; Settings.Save(); }
    }

    /// <summary>Folders removed in Settings: hidden at once; restored ones come back with the next scan.</summary>
    public async Task ExcludedChanged(IEnumerable<string> removed, bool anyRestored)
    {
        if (Db is null) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int hidden = 0;
        foreach (var f in removed) hidden += Db.HideFolder(Settings.LibraryRoot, f, now);
        if (hidden > 0) { Interlocked.Add(ref _changesSinceView, hidden); DataChanged?.Invoke(); }
        _watcher?.Dispose(); _watcher = null;
        if (anyRestored) await Refresh(scan: true, confirmLargeRemoval: false);
        StartWatcher();
    }

    /// <summary>Where the micro/small/full thumbnails are being read from and written to.</summary>
    public volatile string ThumbDir = "";
    /// <summary>Set while the small thumbnails are being copied to the fast drive (one time).</summary>
    public volatile bool Moving; public long MoveDone, MoveTotal;
    public volatile string FastCacheNote = "";
    public CacheWarmer Warmer { get; } = new();
    /// <summary>Makes the GIF versions the GPU grid plays (started when the GPU grid is chosen).</summary>
    public GifRenditionMaker? GifMaker { get; private set; }
    private bool _gifsStarted;
    public void StartGifRenditions()
    {
        if (_gifsStarted || GifMaker is null) return;
        _gifsStarted = true;
        GifMaker.Start(_cts.Token);
    }

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
            _detailsWake.Set();                                    // new or changed files: their dates and copies are looked at next
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

    // ---- details read in the background: when photos were taken, and copies of the same file (for showing each once) ----
    /// <summary>What the details job is doing (for the progress bar).</summary>
    public WorkProgress Details { get; } = new();
    public volatile string DetailsProblem = "";
    private readonly AutoResetEvent _detailsWake = new(false);
    private Thread? _details;

    private void StartDetails()
    {
        if (_details is not null || Db is null) return;
        var ct = _cts.Token;
        _details = new Thread(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    int a = PhotoDates.Run(Db!, Details, DetailsHold, ct);
                    int b = DuplicateHashes.Run(Db!, Details, DetailsHold, ct);
                    DetailsProblem = "";
                    if (a + b > 0) { Interlocked.Increment(ref _changesSinceView); DataChanged?.Invoke(); }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { DetailsProblem = ex.Message; }
                finally { Details.Running = false; }
                WaitHandle.WaitAny(new[] { _detailsWake, ct.WaitHandle }, TimeSpan.FromMinutes(15));
            }
        }) { IsBackground = true, Priority = ThreadPriority.Lowest, Name = "details" };
        _details.Start();
    }

    /// <summary>The details job waits while you scroll or use the viewer, and while thumbnails are being made or the library scanned.</summary>
    private bool DetailsHold() =>
        Pipe.Running || _work.CurrentCount == 0 ||
        Stopwatch.GetTimestamp() - Volatile.Read(ref Pipe.LastUiActivity) < Stopwatch.Frequency * 3 / 2;

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
