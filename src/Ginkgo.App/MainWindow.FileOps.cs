using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ginkgo.Core;

namespace Ginkgo.App;

/// <summary>
/// Selecting files and working on them. Ctrl+click (Ctrl+Shift+click for a range), Ctrl+Space (Ctrl+Shift+Space) with the keys,
/// X or the tick icon in the viewer. Right-click acts on the selection when the file you click is in it, otherwise on that one file:
/// turn, delete (to the Recycle Bin), move or copy to a folder, move into or out of NSFW/Very, and the sorting moves (Find Higher
/// Quality, Upscale, Combine, Average &amp; LQ). Files stay selected afterwards (unless they were deleted), so several things can be
/// done one after another. Works in every section.
/// </summary>
public partial class MainWindow
{
    private readonly HashSet<long> _picked = new();
    private int _pickAnchor = -1;
    private bool _opRunning;

    /// <summary>A file to work on: where it is, its folder in the library ("" when outside it) and its content level.</summary>
    private sealed record OpFile(long Id, string Path, string Folder, ContentLevel Level, bool Video);

    private void InitFileOps()
    {
        View.Picked = IsPickedIndex;
    }

    private long IdAt(int index) => _model is not null && index >= _firstFile && index < _order.Length && _order[index] >= 0 ? _model.Id[_order[index]] : -1;
    private bool IsPickedIndex(int index) { if (_picked.Count == 0) return false; long id = IdAt(index); return id >= 0 && _picked.Contains(id); }

    // ---- selecting ----

    /// <summary>Ctrl+click or Ctrl+Space: select or unselect one file; with Shift, everything from the last one picked to this one.</summary>
    private void PickAt(int index, bool range)
    {
        long id = IdAt(index);
        if (id < 0 || IsFolderTile(index)) return;
        if (range && _pickAnchor >= _firstFile && _pickAnchor < _order.Length)
        {
            int a = Math.Min(_pickAnchor, index), b = Math.Max(_pickAnchor, index);
            for (int i = a; i <= b; i++) { long x = IdAt(i); if (x >= 0 && !IsFolderTile(i)) _picked.Add(x); }
        }
        else
        {
            if (!_picked.Remove(id)) _picked.Add(id);
            _pickAnchor = index;
        }
        PicksChanged();
    }

    private void ClearPicks()
    {
        if (_picked.Count == 0) return;
        _picked.Clear(); _pickAnchor = -1;
        PicksChanged();
    }

    private void PicksChanged()
    {
        View.InvalidateVisual();
        UpdateViewerPick();
        StatusText.Text = _picked.Count == 0 ? StatusLine() : $"{_picked.Count:N0} selected  ·  right-click one of them for what to do, Delete sends them to the Recycle Bin, Esc clears";
    }

    /// <summary>The viewer's tick: lit (accent) when the open file is selected.</summary>
    private void UpdateViewerPick()
    {
        if (!ViewerOpen) return;
        bool on = IsPickedIndex(_vIndex);
        PickBtn.Content = on ? "" : "";             // a ticked box or an empty one
        if (on) PickBtn.SetResourceReference(ForegroundProperty, "AccentSolid"); else PickBtn.ClearValue(ForegroundProperty);
        PickBtn.ToolTip = on ? "Selected (X to unselect)" : "Select (X)";
    }

    private void OnViewerPick(object s, RoutedEventArgs e) => PickAt(_vIndex, range: false);

    /// <summary>The files a command acts on: the selection when <paramref name="index"/> is in it (or no file is given), else that one file.</summary>
    private List<OpFile> TargetsFor(int index)
    {
        var list = new List<OpFile>();
        bool useSelection = _picked.Count > 0 && (index < 0 || IsPickedIndex(index));
        if (!useSelection)
        {
            if (_model is null || index < _firstFile || index >= _order.Length || _order[index] < 0) return list;
            if (FileOf(_model, _order[index]) is { } one) list.Add(one);
            return list;
        }
        var want = new HashSet<long>(_picked);
        foreach (var m in new[] { _model, _libModel, _favModel })
        {
            if (m is null || want.Count == 0) continue;
            for (int r = 0; r < m.Count && want.Count > 0; r++)
                if (want.Remove(m.Id[r]) && FileOf(m, r) is { } f) list.Add(f);
        }
        return list;
    }

    private OpFile? FileOf(LibraryModel m, int row)
    {
        string path = m.Path[row];
        string folder = Inside(path) is { } rel ? rel : "";
        return new OpFile(m.Id[row], path, folder, (ContentLevel)m.Level[row], m.Kind[row] == (byte)MediaKind.Video);
    }

    /// <summary>The folder (relative to the library) that a file is in, or null when it is outside the library.</summary>
    private string? Inside(string path)
    {
        string root = Path.GetFullPath(_settings.LibraryRoot).TrimEnd('\\');
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? "";
        if (dir.Equals(root, StringComparison.OrdinalIgnoreCase)) return "";
        return dir.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ? dir[(root.Length + 1)..] : null;
    }

    private string Abs(string rel) => rel.Length == 0 ? _settings.LibraryRoot : Path.Combine(_settings.LibraryRoot, rel);
    private bool ExistsRel(string rel) => Directory.Exists(Abs(rel));

    // ---- the right-click menu ----

    /// <summary>The file commands, added to a picture's right-click menu (in the grid or the viewer).</summary>
    private void AddFileOps(ContextMenu menu, int index)
    {
        var t = TargetsFor(index);
        if (t.Count == 0) return;
        string n = t.Count == 1 ? "" : $" ({t.Count:N0} files)";
        MenuItem Item(ItemsControl into, string text, Action act, bool enabled = true, string? tip = null)
        {
            var it = new MenuItem { Header = text, IsEnabled = enabled && !_opRunning, ToolTip = tip };
            it.Click += (_, e) => { if (e.OriginalSource == it) act(); };
            into.Items.Add(it);
            return it;
        }
        menu.Items.Add(new Separator());
        bool picked = IsPickedIndex(index);
        if (index >= 0) Item(menu, picked ? "Unselect" : "Select", () => PickAt(index, range: false), true, "Ctrl+click, Ctrl+Space, or X in the viewer");
        if (_picked.Count > 0) Item(menu, $"Clear the selection ({_picked.Count:N0})", ClearPicks);

        menu.Items.Add(new Separator());
        bool turnable = t.Any(f => FileOps.CanRotate(f.Path));
        Item(menu, "Rotate clockwise" + n, () => _ = Rotate(t, clockwise: true), turnable, turnable ? null : "Only JPEG, PNG, BMP, TIFF and still GIF pictures can be turned");
        Item(menu, "Rotate anticlockwise" + n, () => _ = Rotate(t, clockwise: false), turnable);

        menu.Items.Add(new Separator());
        Item(menu, "Move to folder…" + n, () => _ = MoveToPicked(t, copy: false));
        Item(menu, "Copy to folder…" + n, () => _ = MoveToPicked(t, copy: true));
        // into or out of NSFW / Very, in the same folder
        foreach (var (level, label) in new[] { (ContentLevel.Nsfw, "Move to the NSFW folder"), (ContentLevel.Very, "Move to the Very folder") })
        {
            var can = t.Where(f => Inside(f.Path) is not null && f.Level < level && FileDestinations.ForLevel(f.Folder, level, ExistsRel) is not null).ToList();
            if (can.Count > 0) Item(menu, label + (can.Count == 1 ? "" : $" ({can.Count:N0} files)"), () => _ = MoveByRule(can, f => FileDestinations.ForLevel(f.Folder, level, ExistsRel), "Moving"),
                true, "Into the NSFW (or NSFW › Very) folder inside the folder it is in now; made if it isn't there");
        }
        foreach (var (level, label) in new[] { (ContentLevel.General, "Move up to General"), (ContentLevel.Nsfw, "Move up to NSFW") })
        {
            var can = t.Where(f => Inside(f.Path) is not null && f.Level > level && FileDestinations.ForLevel(f.Folder, level, ExistsRel) is not null).ToList();
            if (can.Count > 0) Item(menu, label + (can.Count == 1 ? "" : $" ({can.Count:N0} files)"), () => _ = MoveByRule(can, f => FileDestinations.ForLevel(f.Folder, level, ExistsRel), "Moving"),
                true, "Out of its NSFW / Very folder, into the folder above");
        }

        menu.Items.Add(new Separator());
        var lib = t.Where(f => Inside(f.Path) is not null).ToList();
        Item(menu, "Find Higher Quality" + n, () => _ = MoveByRule(lib, f => FileDestinations.SortTarget(FileDestinations.FindHigherQuality, f.Folder), "Moving"),
            lib.Count > 0, @"To Sort\Find Higher Quality, in a folder named like the one it came from");
        Item(menu, "Upscale" + n, () => _ = MoveByRule(lib, f => FileDestinations.SortTarget(FileDestinations.Upscale, f.Folder), "Moving"),
            lib.Count > 0, @"To Sort\Upscale, in a folder named like the one it came from");
        bool combinable = lib.Count > 1 && FileDestinations.SameFolder(lib.Select(f => f.Folder));
        Item(menu, "Combine" + n, () =>
        {
            string target = FileDestinations.CombineTarget(lib[0].Folder, ExistsRel);
            _ = MoveByRule(lib, _ => target, "Moving");
        }, combinable, combinable ? $@"Together into Sort\Combine - Join Together\{FileDestinations.CombineName(lib[0].Folder)} (next number)" : "Select two or more files from the same folder");
        menu.Items.Add(AverageMenu(lib, n));

        menu.Items.Add(new Separator());
        Item(menu, "Delete (to the Recycle Bin)" + n, () => _ = Delete(t));
    }

    /// <summary>
    /// "Move to Average &amp; LQ": clicking it moves the files into Average &amp; LQ itself; hovering opens its folders (and theirs) to
    /// pick one. NSFW and Very folders aren't listed: each file goes into the right one for its rating wherever it lands.
    /// </summary>
    private MenuItem AverageMenu(List<OpFile> files, string n)
    {
        MenuItem Folder(string rel, string header)
        {
            var it = new MenuItem { Header = header, IsEnabled = files.Count > 0 && !_opRunning, ToolTip = "Click to move here (into its NSFW / Very folder for NSFW / Very files)" };
            var kids = SubFolders(rel);
            void Move() { (it.Parent as MenuItem)?.SetCurrentValue(MenuItem.IsSubmenuOpenProperty, false); CloseMenus(it); _ = MoveByRule(files, f => FileDestinations.AverageTarget(rel, f.Level, ExistsRel), "Moving"); }
            if (kids.Count == 0) it.Click += (_, e) => { if (e.OriginalSource == it) Move(); };
            else
            {
                // a folder with folders inside: hovering opens them, clicking it moves here
                it.Items.Add(new MenuItem { Header = "…" });
                it.SubmenuOpened += (_, e) =>
                {
                    if (e.OriginalSource != it || it.Items.Count != 1 || it.Items[0] is not MenuItem { Header: "…" }) return;
                    it.Items.Clear();
                    foreach (var k in kids) it.Items.Add(Folder(k, Path.GetFileName(k)));
                };
                it.PreviewMouseLeftButtonUp += (_, e) => { if (e.Source == it && it.IsMouseOver && !it.Items.Cast<object>().OfType<MenuItem>().Any(c => c.IsMouseOver)) { e.Handled = true; Move(); } };
            }
            return it;
        }
        var top = Folder(FileDestinations.Average, "Move to Average & LQ" + n);
        top.ToolTip = @"Click: into Average & LQ (its NSFW / Very folder for NSFW / Very files). Hover: choose a folder inside it.";
        return top;
    }

    private static void CloseMenus(DependencyObject from)
    {
        for (var d = from; d is not null; d = LogicalTreeHelper.GetParent(d) ?? System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ContextMenu cm) { cm.IsOpen = false; return; }
    }

    /// <summary>The folders inside a library folder, without NSFW / Very folders (those follow the files' rating).</summary>
    private List<string> SubFolders(string rel)
    {
        try
        {
            return Directory.EnumerateDirectories(Abs(rel)).Select(d => Path.Combine(rel, Path.GetFileName(d)))
                .Where(r => !FileDestinations.IsLevelFolder(r) && !Path.GetFileName(r).StartsWith('.'))
                .OrderBy(r => r, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return new List<string>(); }
    }

    // ---- doing it ----

    private async Task MoveToPicked(List<OpFile> files, bool copy)
    {
        var w = new FolderPickerWindow(_settings.LibraryRoot, copy ? "Copy" : "Move", files.Count) { Owner = this };
        if (w.ShowDialog() != true || w.Chosen is null) return;
        string dest = w.Chosen;
        await Transfer(files.Select(f => (f, dest)).ToList(), copy);
    }

    /// <summary>Moves each file to the library folder a rule gives it (null: it stays).</summary>
    private Task MoveByRule(List<OpFile> files, Func<OpFile, string?> rule, string verb) =>
        Transfer(files.Select(f => (f, rule(f) is { } rel ? Abs(rel) : null)).Where(x => x.Item2 is not null).Select(x => (x.f, x.Item2!)).ToList(), copy: false);

    /// <summary>Moves or copies files into folders (made when missing), asking what to do about each name clash.</summary>
    private async Task Transfer(List<(OpFile file, string destDir)> work, bool copy)
    {
        if (_opRunning || work.Count == 0) return;
        // what will happen to each file: where it goes, under what name, and what goes to the Recycle Bin first
        var steps = new List<(OpFile f, string dst, string? recycleFirst, bool recycleSource)>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clashes = work.Where(x => !SameDir(x.file.Path, x.destDir) && File.Exists(Path.Combine(x.destDir, Path.GetFileName(x.file.Path)))).Count();
        Clash? always = null;
        int asked = 0;
        foreach (var (f, dir) in work)
        {
            if (SameDir(f.Path, dir)) continue;                      // already there
            string name = Path.GetFileName(f.Path);
            string dst = Path.Combine(dir, name);
            bool takenOnDisk = File.Exists(dst);
            if (planned.Contains(dst) && !takenOnDisk)
            {
                // two of the files going there have the same name: the second gets a numbered name
                name = FileDestinations.FreeName(name, x => File.Exists(Path.Combine(dir, x)) || planned.Contains(Path.Combine(dir, x)));
                dst = Path.Combine(dir, name);
            }
            else if (takenOnDisk)
            {
                string free = FileDestinations.FreeName(name, x => File.Exists(Path.Combine(dir, x)) || planned.Contains(Path.Combine(dir, x)));
                Clash c;
                if (always is { } a) c = a;
                else
                {
                    asked++;
                    var cw = new ConflictWindow(f.Path, dst, free, copy, clashes - asked) { Owner = this };
                    if (cw.ShowDialog() != true) c = Clash.Skip; else c = cw.Choice;
                    if (cw.ForAll) always = c;
                }
                switch (c)
                {
                    case Clash.Skip: continue;
                    case Clash.KeepBoth: dst = Path.Combine(dir, free); break;
                    case Clash.KeepIncoming: steps.Add((f, dst, dst, false)); planned.Add(dst); continue;
                    case Clash.KeepExisting: if (!copy) steps.Add((f, "", null, true)); continue;
                }
            }
            planned.Add(dst);
            steps.Add((f, dst, null, false));
        }
        if (steps.Count == 0) { StatusText.Text = "Nothing to " + (copy ? "copy" : "move") + " (already there, or left where it was)."; return; }

        string verb = copy ? "Copying" : "Moving";
        ReleaseFiles(steps.Select(s => s.f.Path));
        var failed = new List<(string path, string why)>();
        var gone = new List<long>();
        var touched = new List<string>();      // library folders to look at again: destinations first, then where files came from
        var from = new List<string>();
        await RunWithProgress(verb, steps.Count, (i, tick) =>
        {
            var (f, dst, recycleFirst, recycleSource) = steps[i];
            try
            {
                if (recycleSource)
                {
                    var (_, bad) = FileOps.Recycle(new[] { f.Path });
                    if (bad.Count > 0) failed.Add((f.Path, bad[0].why)); else lock (gone) gone.Add(f.Id);
                    if (Inside(f.Path) is { } s0) lock (from) from.Add(s0);
                    return;
                }
                if (recycleFirst is not null)
                {
                    var (_, bad) = FileOps.Recycle(new[] { recycleFirst });
                    if (bad.Count > 0) { failed.Add((f.Path, $"the file already at {recycleFirst} couldn't go to the Recycle Bin ({bad[0].why})")); return; }
                }
                FileOps.Transfer(f.Path, dst, copy);
                if (Inside(dst) is { } d) lock (touched) touched.Add(d);
                else if (!copy) lock (gone) gone.Add(f.Id);               // moved out of the library
                if (!copy && Inside(f.Path) is { } s) lock (from) from.Add(s);
            }
            catch (Exception ex) { lock (failed) failed.Add((f.Path, ex.Message)); }
        });
        foreach (var id in gone) _picked.Remove(id);
        if (touched.Count > 0) FolderPickerWindow.Forget();
        await AfterChange(touched.Concat(from).ToList(), $"{(copy ? "Copied" : "Moved")} {steps.Count - failed.Count:N0} file{(steps.Count - failed.Count == 1 ? "" : "s")}.", failed);
    }

    private static bool SameDir(string file, string dir) =>
        string.Equals(Path.GetFullPath(Path.GetDirectoryName(file)!).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private async Task Rotate(List<OpFile> files, bool clockwise)
    {
        if (_opRunning) return;
        var turnable = files.Where(f => FileOps.CanRotate(f.Path)).ToList();
        var failed = files.Where(f => !FileOps.CanRotate(f.Path)).Select(f => (f.Path, $"Ginkgo can't turn {Path.GetExtension(f.Path)} files")).ToList();
        if (turnable.Count == 0) { ShowFailures("Nothing was turned.", failed); return; }
        ReleaseFiles(turnable.Select(f => f.Path));
        int turned = 0;
        await RunWithProgress("Turning", turnable.Count, (i, _) =>
        {
            try { FileOps.Rotate(turnable[i].Path, clockwise); Interlocked.Increment(ref turned); }
            catch (Exception ex) { lock (failed) failed.Add((turnable[i].Path, ex.Message)); }
        });
        await AfterChange(turnable.Select(f => f.Folder).ToList(), $"Turned {turned:N0} {(turned == 1 ? "picture" : "pictures")} {(clockwise ? "clockwise" : "anticlockwise")}.", failed);
    }

    /// <summary>To the Recycle Bin. On a drive without one, nothing is deleted and the reason is shown.</summary>
    private async Task Delete(List<OpFile> files)
    {
        if (_opRunning || files.Count == 0) return;
        var blocked = files.Select(f => (f, why: FileOps.NoRecycleBin(f.Path))).Where(x => x.why is not null).ToList();
        if (blocked.Count > 0)
        {
            ShowFailures("Nothing was deleted.", blocked.Select(b => (b.f.Path, b.why! + " — Ginkgo only deletes to the Recycle Bin")).ToList());
            return;
        }
        ReleaseFiles(files.Select(f => f.Path));
        var failed = new List<(string, string)>();
        var gone = new List<long>();
        await RunWithProgress("Deleting", files.Count, (i, _) =>
        {
            var (done, bad) = FileOps.Recycle(new[] { files[i].Path });
            if (bad.Count > 0) lock (failed) failed.Add((files[i].Path, bad[0].why)); else lock (gone) gone.Add(files[i].Id);
        });
        foreach (var id in gone) _picked.Remove(id);
        await AfterChange(files.Select(f => f.Folder).ToList(), $"{gone.Count:N0} file{(gone.Count == 1 ? "" : "s")} in the Recycle Bin.", failed);
    }

    /// <summary>The Delete key: the viewer's picture, else the selection, else the highlighted picture.</summary>
    private void DeleteKey()
    {
        int index = ViewerOpen ? _vIndex : _picked.Count > 0 ? -1 : View.SelectedIndex >= 0 ? View.SelectedIndex : View.HoverIndex;
        if (ViewerOpen && IsPickedIndex(_vIndex)) index = _vIndex;      // the open picture is in the selection: all of it
        var t = TargetsFor(index);
        if (t.Count == 0) { StatusText.Text = "Highlight a picture (arrow keys or the mouse) or select some, then press Delete."; return; }
        _ = Delete(t);
    }

    /// <summary>Lets go of the files: their videos stop (players keep files open, which would stop them moving or being deleted).</summary>
    private void ReleaseFiles(IEnumerable<string> paths)
    {
        var set = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        foreach (var g in _gridVids.ToList()) if (g.Index >= 0 && g.Index < _paths.Length && set.Contains(_paths[g.Index])) ReleaseGrid(g);
        if (ViewerOpen && _vIndex >= 0 && _vIndex < _paths.Length && set.Contains(_paths[_vIndex])) { LeaveViewerVideo(); StopViewerGif(); }
    }

    // ---- progress, and afterwards ----

    /// <summary>
    /// Does the work in the background with a bar in the status bar: how many, the share done, the time it has taken and the time
    /// still to go (the files are done one at a time; the drive does one thing at a time).
    /// </summary>
    private async Task RunWithProgress(string verb, int count, Action<int, Action> one)
    {
        _opRunning = true;
        OpBar.Value = 0; OpBar.Visibility = Visibility.Visible; OpText.Visibility = Visibility.Visible;
        var sw = Stopwatch.StartNew();
        int done = 0;
        void Show()
        {
            double f = count == 0 ? 1 : done / (double)count;
            OpBar.Value = f;
            var left = done == 0 ? "" : $"  ·  {Clock(TimeSpan.FromTicks((long)(sw.Elapsed.Ticks * (count - done) / (double)done)))} left";
            OpText.Text = $"{verb} {Math.Min(done + 1, count):N0} of {count:N0}  ·  {f * 100:0}%  ·  {Clock(sw.Elapsed)}{left}";
        }
        Show();
        var tick = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        tick.Tick += (_, _) => Show();
        tick.Start();
        try
        {
            await Task.Run(() =>
            {
                for (int i = 0; i < count; i++) { one(i, () => { }); Interlocked.Increment(ref done); }
            });
        }
        finally
        {
            tick.Stop();
            Show();
            OpBar.Visibility = Visibility.Collapsed; OpText.Visibility = Visibility.Collapsed;
            _opRunning = false;
        }
    }

    private static string Clock(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    /// <summary>
    /// After files changed: the folders involved are looked at again straight away, the view is laid out again where you were, the
    /// selection stays, and an open viewer stays on the same file (or the next one, when it went).
    /// </summary>
    private async Task AfterChange(List<string> folders, string message, List<(string path, string why)> failed)
    {
        long viewId = ViewerOpen ? IdAt(_vIndex) : -1;
        var nextIds = new List<long>();
        if (ViewerOpen) for (int i = _vIndex + 1; i < Math.Min(_order.Length, _vIndex + 400); i++) { long x = IdAt(i); if (x >= 0) nextIds.Add(x); }
        if (ViewerOpen) for (int i = _vIndex - 1; i >= Math.Max(_firstFile, _vIndex - 50); i--) { long x = IdAt(i); if (x >= 0) nextIds.Add(x); }
        StatusText.Text = message + "  Updating the view...";
        await Task.Run(() => _engine.ScanFolders(folders.Where(f => f is not null)));
        await ReloadModel(keepPosition: true);
        if (viewId >= 0)
        {
            int at = IndexOfId(viewId);
            if (at < 0) foreach (var nx in nextIds) { at = IndexOfId(nx); if (at >= 0) break; }
            if (at >= _firstFile) { if (!ViewerOpen) OpenViewer(at, remember: false); else ShowIndex(at, remember: false); }
            else if (ViewerOpen) CloseViewer(fromUser: false);
        }
        PicksChanged();
        StatusText.Text = message + (_picked.Count > 0 ? $"  ({_picked.Count:N0} still selected)" : "");
        if (failed.Count > 0) ShowFailures(message, failed);
    }

    private void ShowFailures(string head, List<(string path, string why)> failed)
    {
        var lines = failed.Take(12).Select(f => $"•  {Path.GetFileName(f.path)}: {f.why}");
        string more = failed.Count > 12 ? $"\n…and {failed.Count - 12:N0} more." : "";
        MessageBox.Show(this, $"{head}\n\n{failed.Count:N0} file{(failed.Count == 1 ? "" : "s")} couldn't be done:\n\n{string.Join("\n", lines)}{more}", "Ginkgo", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
