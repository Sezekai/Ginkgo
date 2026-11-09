namespace Ginkgo.Data;

/// <summary>
/// Keeps the index current while the app is open. Changes are gathered for a moment, then only the folders that changed
/// are looked at again. Files that are still being copied are left until they settle. If Windows reports that it lost
/// track of changes, the whole library is rescanned.
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly IndexDb _db;
    private readonly string _root;
    private readonly HashSet<string> _excluded;
    private readonly Func<string, bool> _ignore;
    private readonly ScanOptions _baseOptions;
    private readonly FileSystemWatcher _fsw;
    private readonly Timer _timer;
    private readonly object _lock = new();
    private readonly Dictionary<string, bool> _dirty = new(StringComparer.OrdinalIgnoreCase);   // folder -> needs its subfolders too
    private bool _everything;
    private long _lastEvent;
    private int _running;
    private readonly int _quietMs;

    /// <summary>Raised after each round of scanning with the combined result.</summary>
    public event Action<ScanResult>? Scanned;
    public event Action<Exception>? Failed;

    /// <param name="ignore">Full paths to ignore, such as the cache folder and the Favourites folder.</param>
    public LibraryWatcher(IndexDb db, string root, IEnumerable<string> excludedTop, Func<string, bool>? ignore = null,
        ScanOptions? options = null, int quietMilliseconds = 1500)
    {
        _db = db; _root = root; _quietMs = quietMilliseconds;
        _excluded = new HashSet<string>(excludedTop, StringComparer.OrdinalIgnoreCase);
        _ignore = ignore ?? (_ => false);
        _baseOptions = options ?? new ScanOptions();
        _fsw = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = 64 * 1024,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _fsw.Created += (_, e) => Note(e.FullPath, null);
        _fsw.Deleted += (_, e) => Note(e.FullPath, null);
        _fsw.Changed += (_, e) => Note(e.FullPath, false);
        _fsw.Renamed += (_, e) => { Note(e.OldFullPath, null); Note(e.FullPath, null); };
        _fsw.Error += (_, _) => { lock (_lock) { _everything = true; _lastEvent = Environment.TickCount64; } };
        _fsw.EnableRaisingEvents = true;
        _timer = new Timer(_ => Tick(), null, 500, 500);
    }

    /// <summary>A change at <paramref name="full"/>. isDir: true / false when known, null when it has to be guessed.</summary>
    private void Note(string full, bool? isDir)
    {
        if (_ignore(full)) return;
        string rel = Path.GetRelativePath(_root, full);
        if (rel.StartsWith("..")) return;
        if (rel == ".") rel = "";
        string top = rel.Split('\\', '/')[0];
        if (_excluded.Contains(top) && rel.Length > 0) return;
        string relB = rel.Replace('/', '\\');
        foreach (var e in _excluded) if (e.Contains('\\') && (relB.Equals(e, StringComparison.OrdinalIgnoreCase) || relB.StartsWith(e + "\\", StringComparison.OrdinalIgnoreCase))) return;
        // something that no longer exists could have been a file or a folder: look at it as a folder and at its parent
        bool exists = isDir.HasValue || Directory.Exists(full) || File.Exists(full);
        bool dir = isDir ?? (Directory.Exists(full) || !exists);
        string parent = Path.GetDirectoryName(rel) ?? "";
        lock (_lock)
        {
            // a folder event needs its whole subtree looked at; a file event only its own folder
            string folder = dir ? rel : parent;
            _dirty[folder] = (_dirty.TryGetValue(folder, out bool deep) && deep) || dir;
            if (dir && !_dirty.ContainsKey(parent)) _dirty[parent] = false;
            if (!exists && !_dirty.ContainsKey(parent)) _dirty[parent] = false;
            _lastEvent = Environment.TickCount64;
        }
    }

    private void Tick()
    {
        Dictionary<string, bool> work; bool everything;
        lock (_lock)
        {
            if (_dirty.Count == 0 && !_everything) return;
            if (Environment.TickCount64 - _lastEvent < _quietMs) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
            work = new Dictionary<string, bool>(_dirty, StringComparer.OrdinalIgnoreCase); _dirty.Clear();
            everything = _everything || work.Count > 300; _everything = false;
        }
        try
        {
            int added = 0, changed = 0, removed = 0, moved = 0, restored = 0, deferred = 0, total = 0; double secs = 0; bool tripped = false;
            void Add(ScanResult r)
            {
                added += r.Added; changed += r.Changed; removed += r.Removed; moved += r.Moved; restored += r.Restored;
                deferred += r.Deferred; total = Math.Max(total, r.Total); secs += r.Seconds; tripped |= r.SafetyTripped;
            }
            if (everything) Add(LibraryScanner.Scan(_db, _root, _excluded, null, CancellationToken.None, Copy(_baseOptions, null, true, 3)));
            else
            {
                // a folder whose parent is already being looked at in full needs no scan of its own
                foreach (var kv in work.OrderBy(k => k.Key.Length))
                {
                    if (work.Any(o => o.Value && o.Key.Length < kv.Key.Length && (o.Key.Length == 0 || kv.Key.StartsWith(o.Key + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || kv.Key.StartsWith(o.Key + "/", StringComparison.OrdinalIgnoreCase)))) continue;
                    Add(LibraryScanner.Scan(_db, _root, _excluded, null, CancellationToken.None, Copy(_baseOptions, kv.Key, kv.Value, 3)));
                }
            }
            if (deferred > 0)                                   // still being written: look again soon
                lock (_lock) { foreach (var k in work.Keys) _dirty[k] = work[k]; _lastEvent = Environment.TickCount64; }
            Scanned?.Invoke(new ScanResult(added, changed, removed, total, secs, moved, restored, 0, deferred, tripped));
        }
        catch (Exception ex) { Failed?.Invoke(ex); }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static ScanOptions Copy(ScanOptions o, string? sub, bool recursive, int skipRecent) => new()
    {
        FileIds = o.FileIds, Grace = o.Grace, SafetyFraction = o.SafetyFraction, SafetyMinCount = o.SafetyMinCount,
        AcceptLargeRemoval = o.AcceptLargeRemoval, SubFolder = sub, Recursive = recursive, SkipRecentSeconds = skipRecent, UtcNow = o.UtcNow,
    };

    public void Dispose() { _timer.Dispose(); _fsw.Dispose(); }
}
