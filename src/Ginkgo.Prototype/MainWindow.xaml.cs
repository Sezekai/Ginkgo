using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ginkgo.Core;
using Ginkgo.Core.Layout;
using System.Collections.Concurrent;
using Ginkgo.Data;

namespace Ginkgo.Prototype;

public partial class MainWindow : Window
{
    // Prototype locations. Index lives with the app's data; thumbnails go to the external drive (outside the library).
    private const string LibraryRoot = @"E:\Pictures\IMGs";
    private static readonly string CacheDir = @"E:\GinkgoCache";
    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "index.db");

    private int _frames; private double _fps; private long _lastTick = Stopwatch.GetTimestamp();
    private bool _syncing;
    private string _mode = "nothing loaded";

    private IndexDb? _db;
    private ThumbStores? _store;
    private ThumbLoader? _loader;
    private string[] _paths = Array.Empty<string>();
    private CancellationTokenSource? _buildCts;
    private readonly PipelineStats _pipe = new();
    private string _scanInfo = "";
    private Task? _buildTask;
    private long[] _ids = Array.Empty<long>();
    private readonly ConcurrentQueue<(long id, int pack, long off, int len)> _largeQueue = new();
    private int _savingLarge;

    public MainWindow()
    {
        InitializeComponent();
        // avoid long pauses from memory clean-up while scrolling
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        View.ScrollChanged += SyncBar;
        View.ScrollChanged += () => Interlocked.Exchange(ref _pipe.LastUiActivity, Stopwatch.GetTimestamp());
        View.HoverChanged += i => Hover.Text = i >= 0 && i < _paths.Length ? _paths[i] : " ";
        CompositionTarget.Rendering += (_, _) => _frames++;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        timer.Tick += (_, _) => { UpdateStats(); SaveLargeBatch(); };
        timer.Start();
        Loaded += (_, _) => Startup();
        Closing += (_, _) =>
        {
            _buildCts?.Cancel(); _loader?.Dispose();
            try
            {
                var rest = new List<(long id, int pack, long off, int len)>();
                while (_largeQueue.TryDequeue(out var r)) rest.Add(r);
                if (_db is not null && _store is not null) IndexUpdates.SaveLarge(_db, _store, rest);
            }
            catch { }
        };
    }

    private void Startup()
    {
        try
        {
            _db = new IndexDb(DbPath);
            if (_db.LargeInvalidated && Directory.Exists(CacheDir))   // the sharp size changed to 1280 px: the old sharp files are no longer used
                foreach (var f in Directory.EnumerateFiles(CacheDir, "large-*.bin")) { try { File.Delete(f); } catch { } }
            _store = new ThumbStores(CacheDir);
            using var c = _db.Open();
            long done = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state IN (1,3)");
            long redo = IndexDb.Scalar(c, "SELECT COUNT(*) FROM files WHERE state=3");
            if (redo > 0) _scanInfo = $"{redo:N0} long/wide images need new thumbnails - press Scan to rebuild them";
            if (done > 0) LoadLibrary(); else LoadSynthetic(200_000);
        }
        catch (Exception ex) { _scanInfo = "startup problem: " + ex.Message; LoadSynthetic(200_000); }
    }

    private void OnSynth200k(object s, RoutedEventArgs e) => LoadSynthetic(200_000);
    private void OnFadeChanged(object s, RoutedEventArgs e) { if (_loader is not null) _loader.FadeEnabled = FadeBox.IsChecked == true; }
    private void OnShow(object s, RoutedEventArgs e) => LoadLibrary();

    private void LoadSynthetic(int n)
    {
        DetachLoader();
        var rnd = new Random(42);
        var items = new ItemSpec[n];
        for (int i = 0; i < n; i++)
        {
            float a = rnd.Next(10) switch { < 5 => 0.55f + (float)rnd.NextDouble() * 0.25f, < 7 => 1f, _ => 1.3f + (float)rnd.NextDouble() * 0.6f };
            items[i] = new ItemSpec(a);
        }
        _paths = Array.Empty<string>();
        View.SetItems(items, null);
        _mode = $"{n:N0} fake tiles";
    }

    private void DetachLoader()
    {
        View.Thumbs = null;
        _loader?.Dispose(); _loader = null;
    }

    private void LoadLibrary()
    {
        if (_db is null || _store is null) return;
        var sw = Stopwatch.StartNew();
        var specs = new List<ItemSpec>(); var paths = new List<string>(); var ids = new List<long>(); var origW = new List<int>();
        var lpack = new List<int>(); var loff = new List<long>(); var llen = new List<int>();
        var pack = new List<int>(); var off = new List<long>(); var len = new List<int>();
        var spack = new List<int>(); var soff = new List<long>(); var slen = new List<int>();
        var mpack = new List<int>(); var moff = new List<long>(); var mlen = new List<int>();
        using (var c = _db.Open())
        using (var cmd = c.CreateCommand())
        {
            // Home order: newest first seen, then newest file date.
            cmd.CommandText = "SELECT path, w, h, thumb_pack, thumb_off, thumb_len, s_pack, s_off, s_len, m_pack, m_off, m_len, id, l_pack, l_off, l_len FROM files WHERE state IN (1,3) ORDER BY first_seen DESC, mtime DESC, id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                int w = r.GetInt32(1), h = r.GetInt32(2);
                specs.Add(new ItemSpec(ThumbMaker.ClampAspect(w, h)));
                paths.Add(r.GetString(0));
                pack.Add(r.GetInt32(3)); off.Add(r.GetInt64(4)); len.Add(r.GetInt32(5));
                spack.Add(r.GetInt32(6)); soff.Add(r.GetInt64(7)); slen.Add(r.GetInt32(8));
                mpack.Add(r.GetInt32(9)); moff.Add(r.GetInt64(10)); mlen.Add(r.GetInt32(11));
                ids.Add(r.GetInt64(12)); origW.Add(w);
                lpack.Add(r.GetInt32(13)); loff.Add(r.GetInt64(14)); llen.Add(r.GetInt32(15));
            }
        }
        if (specs.Count == 0) { _scanInfo = "nothing ready yet - press Scan"; return; }
        DetachLoader();
        _paths = paths.ToArray();
        var tiers = new[]
        {
            new ThumbLoader.TierData(_store.Micro, mpack.ToArray(), moff.ToArray(), mlen.ToArray()),
            new ThumbLoader.TierData(_store.Small, spack.ToArray(), soff.ToArray(), slen.ToArray()),
            new ThumbLoader.TierData(_store.Full, pack.ToArray(), off.ToArray(), len.ToArray()),
            new ThumbLoader.TierData(_store.Large, lpack.ToArray(), loff.ToArray(), llen.ToArray()),
        };
        _ids = ids.ToArray();
        _loader = new ThumbLoader(tiers, _paths, origW.ToArray(), _store.Large, 6L * 1024 * 1024 * 1024,
            (i, loc) => _largeQueue.Enqueue((_ids[i], loc.Pack, loc.Off, loc.Len)), Dispatcher, () => View.Refresh(), 128L * 1024 * 1024, 64L * 1024 * 1024);
        _loader.FadeEnabled = FadeBox.IsChecked == true;
        View.Thumbs = _loader;
        View.SetItems(specs.ToArray(), null);
        _mode = $"library: {specs.Count:N0} ready";
        _scanInfo = $"view built in {sw.Elapsed.TotalSeconds:F1}s";
    }

    private async void OnBuild(object s, RoutedEventArgs e)
    {
        if (_buildTask is { IsCompleted: false }) { _buildCts?.Cancel(); BuildBtn.IsEnabled = false; return; }
        if (_db is null || _store is null) return;
        if (!Directory.Exists(LibraryRoot)) { _scanInfo = LibraryRoot + " not found"; return; }
        _buildCts = new CancellationTokenSource();
        var ct = _buildCts.Token;
        BuildBtn.Content = "Stop";
        _buildTask = Task.Run(async () =>
        {
            try
            {
                _scanInfo = "scanning folders...";
                var res = LibraryScanner.Scan(_db, LibraryRoot, MediaTypes.DefaultExcludedTopFolders, n => _scanInfo = $"scanning... {n:N0} files seen", ct);
                _scanInfo = $"scan: {res.Total:N0} files, +{res.Added:N0} new, {res.Changed:N0} changed, {res.Removed:N0} gone, {res.Seconds:F0}s";
                int workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
                await ThumbnailPipeline.RunAsync(_db, _store, _pipe, workers, ct);
                await TierBackfill.RunAsync(_db, _store, _pipe, Math.Clamp(workers / 2, 2, 3), ct);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _scanInfo = "problem: " + ex.Message; }
        });
        await _buildTask;
        BuildBtn.Content = "Scan library + make thumbnails";
        BuildBtn.IsEnabled = true;
        LoadLibrary();
    }

    private void OnBarChanged(object s, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing) return;
        _syncing = true; View.Offset = e.NewValue; _syncing = false;
    }

    private void SyncBar()
    {
        if (_syncing) return;
        _syncing = true;
        double max = Math.Max(0, View.TotalHeight - View.ActualHeight);
        Bar.Maximum = max; Bar.ViewportSize = View.ActualHeight;
        Bar.LargeChange = View.ActualHeight * 0.9; Bar.SmallChange = 60;
        Bar.Value = View.Offset;
        _syncing = false;
    }

    private void SaveLargeBatch()
    {
        if (_db is null || _store is null || _largeQueue.IsEmpty || Interlocked.Exchange(ref _savingLarge, 1) == 1) return;
        var rows = new List<(long id, int pack, long off, int len)>();
        while (rows.Count < 2000 && _largeQueue.TryDequeue(out var r)) rows.Add(r);
        var db = _db; var store = _store;
        Task.Run(() => { try { IndexUpdates.SaveLarge(db, store, rows); } catch { } finally { Volatile.Write(ref _savingLarge, 0); } });
    }

    private long _lastGc = Stopwatch.GetTimestamp();

    /// <summary>Picture memory freed by the app only goes back to Windows when .NET cleans up, so do that when you've stopped scrolling.</summary>
    private void IdleCleanup()
    {
        long now = Stopwatch.GetTimestamp();
        double idle = (now - Volatile.Read(ref _pipe.LastUiActivity)) / (double)Stopwatch.Frequency;
        double since = (now - _lastGc) / (double)Stopwatch.Frequency;
        if (idle > 2.0 && since > 10 && Process.GetCurrentProcess().WorkingSet64 > 600L * 1024 * 1024)
        {
            _lastGc = now;
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
    }

    private void UpdateStats()
    {
        IdleCleanup();
        long now = Stopwatch.GetTimestamp();
        double secs = (now - _lastTick) / (double)Stopwatch.Frequency;
        _fps = _frames / secs; _frames = 0; _lastTick = now;
        double mb = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        string cache = _loader is null ? "" :
            $" | pictures in RAM {_loader.CachedCount:N0} ({_loader.CachedBytes / 1048576.0:F0} MB) + compressed {_loader.EncodedBytes / 1048576.0:F0} MB" +
            $" | sharp versions made {_loader.LargeMade:N0} | waiting {_loader.PendingCount} | disk read {_loader.AvgReadMs:F1} ms ({_loader.ThumbsPerRead:F0} thumbs/read) | decode {_loader.AvgDecodeMs:F1} ms";
        Stats.Text = $"{_mode} | drawn {View.LastVisibleCount} | {_fps:F0} fps | RAM {mb:F0} MB{cache}";
        string pipe = _pipe.Running || _pipe.Done + _pipe.Failed + _pipe.BackfillTotal > 0
            ? $" | thumbnails {_pipe.Done:N0}/{_pipe.Total:N0} done, {_pipe.Failed:N0} failed{(_pipe.Failed > 0 ? " (last: " + _pipe.LastError + ")" : "")}{(_pipe.BackfillTotal > 0 ? $" | small sizes {_pipe.BackfillDone:N0}/{_pipe.BackfillTotal:N0}" : "")}"
            : "";
        Status.Text = _scanInfo + pipe;
    }
}
