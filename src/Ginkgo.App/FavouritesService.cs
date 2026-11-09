using System.IO;
using System.Runtime.InteropServices;
using Ginkgo.Core;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// Favourites: the Favourites folder is the list. Starring copies a file in (never moves it); un-starring sends the copies to the
/// Recycle Bin. A file elsewhere shows the star when a file with identical content is in Favourites. The check that works the
/// stars out runs in the background, and again whenever the library changes.
/// </summary>
public sealed class FavouritesService
{
    private readonly Engine _engine;
    private readonly AppSettings _settings;
    private FavState _state = new();
    private int _syncing, _again;
    private readonly SemaphoreSlim _one = new(1, 1);          // one check or copy at a time (the drive does one thing at a time)

    /// <summary>The check or copy under way (for the progress bar).</summary>
    public WorkProgress Progress { get; } = new();
    /// <summary>The stars changed (raised on a background thread).</summary>
    public event Action? Changed;
    /// <summary>A copy or removal finished, with what to tell the person (raised on a background thread).</summary>
    public event Action<string>? Message;
    public bool Ready { get; private set; }
    public int FavCount => _state.FavIds.Count;
    public int Linked => _state.Linked;
    public string LastProblem { get; private set; } = "";

    public FavouritesService(Engine engine, AppSettings settings) { _engine = engine; _settings = settings; }

    public bool IsStarred(long id) => _state.IsStarred(id);
    public bool IsFavouriteFile(long id) => _state.FavIds.Contains(id);

    /// <summary>Works the stars out again (one at a time; a request while one runs makes it run once more after).</summary>
    public void RequestSync()
    {
        if (_engine.Db is null) return;
        if (Interlocked.Exchange(ref _syncing, 1) == 1) { Volatile.Write(ref _again, 1); return; }
        var t = new Thread(() =>
        {
            try
            {
                do
                {
                    Volatile.Write(ref _again, 0);
                    _one.Wait();
                    try { _state = FavouriteIndex.Sync(_engine.Db!, _settings.FavRoot, Progress); Ready = true; LastProblem = ""; }
                    catch (Exception ex) { LastProblem = ex.Message; }
                    finally { Progress.Running = false; _one.Release(); }
                    Changed?.Invoke();
                } while (Volatile.Read(ref _again) == 1);
            }
            finally { Volatile.Write(ref _syncing, 0); }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "favourites" };
        t.Start();
    }

    /// <summary>Stars or un-stars a file. Runs in the background; the result comes through <see cref="Message"/>.</summary>
    public void Toggle(long id, string path, MediaKind kind, ContentLevel level)
    {
        bool starred = IsStarred(id);
        _ = Task.Run(() =>
        {
            _one.Wait();
            try
            {
                if (starred) Unstar(id);
                else Star(id, path, kind, level);
            }
            catch (Exception ex) { Message?.Invoke("Favourites: " + ex.Message); }
            finally { Progress.Running = false; _one.Release(); }
        });
    }

    private void Star(long id, string path, MediaKind kind, ContentLevel level)
    {
        string dir = Path.Combine(_settings.FavRoot, FavouritePlaces.SubFolder(kind, level));
        var r = FavouriteCopier.Copy(path, dir, AppSettings.FavouritesLogPath, Progress);
        if (r.Outcome == CopyOutcome.Failed) { Message?.Invoke($"Couldn't add {Path.GetFileName(path)} to Favourites: {r.Message}"); return; }
        // the star shows straight away; the next check links any other copies of it
        var st = Clone(_state);
        st.Starred.Add(id); st.HashOf[id] = r.Hash; st.PathOf[id] = path;
        if (!st.FavPathsByHash.TryGetValue(r.Hash, out var l)) st.FavPathsByHash[r.Hash] = l = new List<string>();
        if (!l.Contains(r.Path, StringComparer.OrdinalIgnoreCase)) l.Add(r.Path);
        _state = st;
        Changed?.Invoke();
        string where = Path.GetRelativePath(_settings.LibraryRoot, r.Path);
        Message?.Invoke(r.Outcome == CopyOutcome.AlreadyThere ? $"Already in Favourites ({where})." : $"Added to Favourites: {where}");
    }

    private void Unstar(long id)
    {
        var copies = FavouriteIndex.CopiesOf(_engine.Db!, _state, id);
        if (copies.Count == 0) { Message?.Invoke("No copy of this file was found in Favourites."); return; }
        var left = new List<string>();
        Progress.Start("Removing from Favourites", copies.Count, 0);
        foreach (var c in copies)
        {
            Progress.Current = c;
            if (!Recycle(c)) left.Add(c);
            else FavouriteCopier.Log(AppSettings.FavouritesLogPath, $"recycled\t{c}");
            Interlocked.Increment(ref Progress.FilesDone);
        }
        var st = Clone(_state);
        long h = st.HashOf.TryGetValue(id, out long hh) ? hh : 0;
        if (left.Count == 0)
        {
            foreach (var k in st.HashOf.Where(kv => h != 0 && kv.Value == h).Select(kv => kv.Key).ToList()) st.Starred.Remove(k);
            st.Starred.Remove(id);
            if (h != 0) st.FavPathsByHash.Remove(h);
            foreach (var k in st.PathOf.Where(kv => copies.Contains(kv.Value, StringComparer.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList()) st.FavIds.Remove(k);
            _state = st;
            Changed?.Invoke();
            Message?.Invoke(copies.Count == 1 ? "Removed from Favourites (the copy is in the Recycle Bin)." : $"Removed from Favourites ({copies.Count} copies are in the Recycle Bin).");
        }
        else Message?.Invoke($"Windows couldn't put {Path.GetFileName(left[0])} in the Recycle Bin, so it was left where it is. Nothing was deleted.");
    }

    private static FavState Clone(FavState s)
    {
        var c = new FavState { Linked = s.Linked };
        c.FavIds.UnionWith(s.FavIds); c.Starred.UnionWith(s.Starred);
        foreach (var kv in s.HashOf) c.HashOf[kv.Key] = kv.Value;
        foreach (var kv in s.FavPathsByHash) c.FavPathsByHash[kv.Key] = new List<string>(kv.Value);
        foreach (var kv in s.PathOf) c.PathOf[kv.Key] = kv.Value;
        foreach (var kv in s.SizeOf) c.SizeOf[kv.Key] = kv.Value;
        return c;
    }

    // ---- the Recycle Bin: never a permanent delete ----

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc; public string pFrom; public string? pTo; public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted; public IntPtr hNameMappings; public string? lpszProgressTitle;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

    /// <summary>
    /// Sends a file to the Recycle Bin of its drive, without asking. If Windows would have to delete it for good instead (a drive
    /// with no Recycle Bin), Windows asks first; saying no leaves the file. True when the file is gone.
    /// </summary>
    public static bool Recycle(string path)
    {
        if (!File.Exists(path)) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE, pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        int r = SHFileOperation(ref op);
        return r == 0 && !op.fAnyOperationsAborted && !File.Exists(path);
    }
}
