using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Ginkgo.Core;
using Ginkgo.Data;

namespace Ginkgo.App;

/// <summary>
/// Tools: the duplicate finder (exact copies and near copies, each top-level folder on its own) and the low-resolution finder.
/// Tools only report: the only actions are Show in Explorer, Copy path and opening in the viewer.
/// </summary>
public partial class MainWindow
{
    private bool InTools => _section == "Tools";

    private sealed class DupResult { public int[] Order = Array.Empty<int>(); public List<SetBlock> Blocks = new(); public List<List<int>> Groups = new(); }
    private DupResult? _dupResult;
    private readonly WorkProgress _toolProgress = new();
    private CancellationTokenSource? _toolCts;
    private volatile bool _toolPaused;
    private Button _findBtn = null!, _pauseBtn = null!, _stopBtn = null!, _csvBtn = null!;
    private ProgressBar _toolBar = null!;
    private TextBlock _toolText = null!;
    private StackPanel _dupPanel = null!, _lowPanel = null!;
    private ComboBox _presetBox = null!;

    private void InitTools()
    {
        var root = new StackPanel();
        var pick = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        var dupBtn = new ToggleButton { Content = "Duplicate finder", Margin = new Thickness(0, 0, 6, 0), Height = 28 };
        var lowBtn = new ToggleButton { Content = "Low-resolution finder", Margin = new Thickness(0, 0, 16, 0), Height = 28 };
        void ShowTool()
        {
            dupBtn.IsChecked = _settings.Tool == "Duplicates"; lowBtn.IsChecked = _settings.Tool == "LowRes";
            _dupPanel.Visibility = _settings.Tool == "Duplicates" ? Visibility.Visible : Visibility.Collapsed;
            _lowPanel.Visibility = _settings.Tool == "LowRes" ? Visibility.Visible : Visibility.Collapsed;
        }
        dupBtn.Click += (_, _) => { _settings.Tool = "Duplicates"; ShowTool(); if (InTools) BuildView(false); };
        lowBtn.Click += (_, _) => { _settings.Tool = "LowRes"; ShowTool(); if (InTools) BuildView(false); };
        pick.Children.Add(dupBtn); pick.Children.Add(lowBtn);
        pick.Children.Add(new TextBlock { Text = "Tools only report: nothing is moved or deleted. Right-click a file to show it in Explorer or copy its path.", Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(pick);

        // ---- duplicates ----
        _dupPanel = new StackPanel();
        var row = new WrapPanel();
        var exact = new ToggleButton { Content = "Exact copies", Margin = new Thickness(0, 0, 4, 4), Height = 28 };
        var near = new ToggleButton { Content = "Near copies", Margin = new Thickness(0, 0, 10, 4), Height = 28, ToolTip = "Resized, re-saved or slightly edited versions, by a visual fingerprint. Always check them yourself." };
        var bits = new Slider { Minimum = 1, Maximum = 12, Width = 110, Value = _settings.DupBits, IsSnapToTickEnabled = true, TickFrequency = 1, VerticalAlignment = VerticalAlignment.Center, ToolTip = "How loose a near match may be" };
        var bitsText = new TextBlock { Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 4), Width = 64 };
        void Bits() => bitsText.Text = _settings.DupBits <= 3 ? "strict" : _settings.DupBits <= 7 ? "medium" : "loose";
        void Mode() { exact.IsChecked = !_settings.DupNear; near.IsChecked = _settings.DupNear; bits.Visibility = bitsText.Visibility = _settings.DupNear ? Visibility.Visible : Visibility.Collapsed; Bits(); }
        exact.Click += (_, _) => { _settings.DupNear = false; Mode(); };
        near.Click += (_, _) => { _settings.DupNear = true; Mode(); };
        bits.ValueChanged += (_, e) => { _settings.DupBits = (int)e.NewValue; Bits(); };
        row.Children.Add(exact); row.Children.Add(near); row.Children.Add(bits); row.Children.Add(bitsText);
        var minBox = new TextBox { Width = 60, Text = _settings.DupMinMB > 0 ? _settings.DupMinMB.ToString("0.##", CultureInfo.CurrentCulture) : "", VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 12, 4), ToolTip = "Leave empty for every size" };
        minBox.LostFocus += (_, _) => _settings.DupMinMB = FilterPanel.ParseNumber(minBox.Text, false);
        row.Children.Add(new TextBlock { Text = "Smallest file (MB)", Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 4) });
        row.Children.Add(minBox);
        var vids = new CheckBox { Content = "Videos", IsChecked = _settings.DupVideos, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 4) };
        vids.Click += (_, _) => _settings.DupVideos = vids.IsChecked == true;
        var gifs = new CheckBox { Content = "GIFs", IsChecked = _settings.DupGifs, Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 4) };
        gifs.Click += (_, _) => _settings.DupGifs = gifs.IsChecked == true;
        row.Children.Add(vids); row.Children.Add(gifs);
        var order = new ComboBox { Width = 150, Margin = new Thickness(0, 0, 10, 4), Height = 28 };
        order.Items.Add(new ComboBoxItem { Content = "Most copies first", Tag = "Copies" });
        order.Items.Add(new ComboBoxItem { Content = "Most space wasted first", Tag = "Wasted" });
        foreach (ComboBoxItem it in order.Items) if ((string)it.Tag == _settings.DupOrder) order.SelectedItem = it;
        if (order.SelectedItem is null) order.SelectedIndex = 0;
        order.SelectionChanged += (_, _) => { _settings.DupOrder = (string)((ComboBoxItem)order.SelectedItem).Tag; if (_dupResult is { } d) { _dupResult = Arrange(d.Groups); BuildView(false); } };
        row.Children.Add(order);
        _dupPanel.Children.Add(row);

        var run = new WrapPanel { Margin = new Thickness(0, 2, 0, 0) };
        _findBtn = new Button { Content = "Find", Padding = new Thickness(14, 2, 14, 2), Margin = new Thickness(0, 0, 6, 0), Height = 28 };
        _pauseBtn = new Button { Content = "Pause", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), Height = 28, IsEnabled = false };
        _stopBtn = new Button { Content = "Stop", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 6, 0), Height = 28, IsEnabled = false };
        _csvBtn = new Button { Content = "Export CSV…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 0, 12, 0), Height = 28, IsEnabled = false };
        _findBtn.Click += async (_, _) => await FindDuplicates();
        _pauseBtn.Click += (_, _) => { _toolPaused = !_toolPaused; _pauseBtn.Content = _toolPaused ? "Resume" : "Pause"; };
        _stopBtn.Click += (_, _) => _toolCts?.Cancel();
        _csvBtn.Click += (_, _) => ExportCsv();
        _toolBar = new ProgressBar { Width = 160, Height = 10, Minimum = 0, Maximum = 1, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 10, 0) };
        _toolText = new TextBlock { Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 900 };
        run.Children.Add(_findBtn); run.Children.Add(_pauseBtn); run.Children.Add(_stopBtn); run.Children.Add(_csvBtn); run.Children.Add(_toolBar); run.Children.Add(_toolText);
        _dupPanel.Children.Add(run);
        root.Children.Add(_dupPanel);

        // ---- low resolution ----
        _lowPanel = new StackPanel();
        var lr = _settings.LowRes;
        var crit = new WrapPanel();
        void Crit(string label, Func<bool> on, Action<bool> setOn, Func<double> get, Action<double> set, string tip)
        {
            var cb = new CheckBox { Content = label, IsChecked = on(), Foreground = (Brush)FindResource("Text"), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip };
            var tb = new TextBox { Width = 58, Text = get().ToString("0.##", CultureInfo.CurrentCulture), Margin = new Thickness(4, 0, 14, 4), VerticalContentAlignment = VerticalAlignment.Center };
            cb.Click += (_, _) => { setOn(cb.IsChecked == true); LowResChanged(); };
            void Apply() { double v = FilterPanel.ParseNumber(tb.Text, false); if (Math.Abs(v - get()) > 1e-9) { set(v); LowResChanged(); } }
            tb.LostFocus += (_, _) => Apply();
            tb.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Apply(); };
            _lowSyncs.Add(() => { cb.IsChecked = on(); if (!tb.IsKeyboardFocused) tb.Text = get().ToString("0.##", CultureInfo.CurrentCulture); });
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            sp.Children.Add(cb); sp.Children.Add(tb);
            crit.Children.Add(sp);
        }
        LowResRule R() => _settings.LowRes;
        Crit("Width under", () => R().UseWidth, v => R().UseWidth = v, () => R().MinWidth, v => R().MinWidth = (int)v, "pixels");
        Crit("Height under", () => R().UseHeight, v => R().UseHeight = v, () => R().MinHeight, v => R().MinHeight = (int)v, "pixels");
        Crit("Megapixels under", () => R().UseMegapixels, v => R().UseMegapixels = v, () => R().MinMegapixels, v => R().MinMegapixels = v, "");
        Crit("File size under (KB)", () => R().UseSize, v => R().UseSize = v, () => R().MinSizeKB, v => R().MinSizeKB = v, "");
        Crit("Video resolution under", () => R().UseVideoHeight, v => R().UseVideoHeight = v, () => R().MinVideoHeight, v => R().MinVideoHeight = (int)v, "The shorter side, in pixels (480 = 480p)");
        Crit("Video shorter than (s)", () => R().UseDuration, v => R().UseDuration = v, () => R().MinDurationSec, v => R().MinDurationSec = v, "");
        Crit("Video bitrate under (kbit/s)", () => R().UseBitrate, v => R().UseBitrate = v, () => R().MinBitrateKbps, v => R().MinBitrateKbps = v, "Worked out from the file size and length");
        _lowPanel.Children.Add(crit);
        var lrow = new WrapPanel();
        var all = new ToggleButton { Content = "All of these (AND)", Height = 28, Margin = new Thickness(0, 0, 4, 0) };
        var any = new ToggleButton { Content = "Any of these (OR)", Height = 28, Margin = new Thickness(0, 0, 16, 0) };
        all.Click += (_, _) => { R().Any = false; LowResChanged(); };
        any.Click += (_, _) => { R().Any = true; LowResChanged(); };
        _lowSyncs.Add(() => { all.IsChecked = !R().Any; any.IsChecked = R().Any; });
        lrow.Children.Add(all); lrow.Children.Add(any);
        lrow.Children.Add(new TextBlock { Text = "Preset", Foreground = (Brush)FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _presetBox = new ComboBox { Width = 160, Height = 28, Margin = new Thickness(0, 0, 6, 0) };
        _presetBox.SelectionChanged += (_, _) =>
        {
            if (_presetBox.SelectedItem is ComboBoxItem { Tag: LowResRule p })
            {
                _settings.LowRes = System.Text.Json.JsonSerializer.Deserialize<LowResRule>(System.Text.Json.JsonSerializer.Serialize(p))!;
                LowResChanged();
            }
        };
        var savePreset = new Button { Content = "Save as preset…", Height = 28, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 6, 0) };
        savePreset.Click += (_, _) =>
        {
            if (AskName("Save preset", "Name for these settings (for example, the cutoff for Average & LQ):", R().Name.Length > 0 ? R().Name : "My cutoff") is not { } name) return;
            var copy = System.Text.Json.JsonSerializer.Deserialize<LowResRule>(System.Text.Json.JsonSerializer.Serialize(R()))!;
            copy.Name = name; R().Name = name;
            _settings.LowResPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            _settings.LowResPresets.Add(copy);
            SaveSettings(); FillPresets();
        };
        var delPreset = new Button { Content = "Delete preset", Height = 28, Padding = new Thickness(8, 0, 8, 0) };
        delPreset.Click += (_, _) => { if (_presetBox.SelectedItem is ComboBoxItem { Tag: LowResRule p }) { _settings.LowResPresets.Remove(p); SaveSettings(); FillPresets(); } };
        lrow.Children.Add(_presetBox); lrow.Children.Add(savePreset); lrow.Children.Add(delPreset);
        _lowPanel.Children.Add(lrow);
        _lowPanel.Children.Add(new TextBlock { Text = "Shows the files below these minimums in the folders chosen in Select folders, with the usual filters and order. Picture tests apply to pictures and GIFs, video tests to videos; file size to both.", Foreground = (Brush)FindResource("Dim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(_lowPanel);

        ToolsBar.Child = root;
        Mode(); ShowTool(); FillPresets(); foreach (var a in _lowSyncs) a();
    }

    private readonly List<Action> _lowSyncs = new();

    private void FillPresets()
    {
        _presetBox.Items.Clear();
        foreach (var p in _settings.LowResPresets) _presetBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p });
    }

    private void LowResChanged()
    {
        foreach (var a in _lowSyncs) a();
        if (InTools && _settings.Tool == "LowRes") _filterDelay.Run(() => BuildView(false));
    }

    /// <summary>Which rows the low-resolution finder shows.</summary>
    private Func<int, bool> LowResFilter(LibraryModel m)
    {
        var rule = System.Text.Json.JsonSerializer.Deserialize<LowResRule>(System.Text.Json.JsonSerializer.Serialize(_settings.LowRes))!;
        return r => rule.IsLow(m.Kind[r] == (byte)MediaKind.Video, m.W[r], m.H[r], m.Size[r], m.Duration[r]);
    }

    // ---- the duplicate finder ----

    private async Task FindDuplicates()
    {
        if (_engine.Db is not { } db || _engine.Stores is not { } stores || _libModel is not { } m) return;
        _toolCts?.Cancel();
        var cts = _toolCts = new CancellationTokenSource();
        _toolPaused = false; _pauseBtn.Content = "Pause";
        _findBtn.IsEnabled = false; _pauseBtn.IsEnabled = _stopBtn.IsEnabled = true;
        bool near = _settings.DupNear, vids = _settings.DupVideos, gifs = _settings.DupGifs;
        int bits = _settings.DupBits;
        long minBytes = (long)(_settings.DupMinMB * 1048576);
        var q = CurrentQuery();
        // the files to look at: everything shown under the content switches, with the kinds and size asked for
        var rows = Enumerable.Range(0, m.Count).Where(i => m.Shown(i, q) && m.Size[i] >= minBytes &&
            (m.Kind[i] != (byte)MediaKind.Video || vids) && (m.Kind[i] != (byte)MediaKind.Gif || gifs)).ToList();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => ShowToolProgress();
        timer.Start();
        try
        {
            var groups = await Task.Run(() =>
            {
                string Top(int r) { string d = m.Display[r]; int c = d.IndexOf('\\'); return c < 0 ? d : d[..c]; }
                if (!near)
                {
                    DuplicateHashes.Run(db, _toolProgress, () => _toolPaused, cts.Token);
                    var hash = new Dictionary<long, long>();
                    using (var c = db.Open())
                    using (var cmd = c.CreateCommand())
                    {
                        cmd.CommandText = "SELECT id, fhash FROM files WHERE fhash<>0 AND missing_since=0";
                        using var r = cmd.ExecuteReader();
                        while (r.Read()) hash[r.GetInt64(0)] = r.GetInt64(1);
                    }
                    // each top-level folder on its own: the same file in Games and in God of War is not reported
                    return rows.Where(r => hash.ContainsKey(m.Id[r]))
                        .GroupBy(r => (Top(r).ToLowerInvariant(), m.Size[r], hash[m.Id[r]]))
                        .Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
                }
                var files = rows.Select(r => (m.Id[r], m.MicroPack[r], m.MicroOff[r], m.MicroLen[r])).ToList();
                var vh = VisualHash.Ensure(db, stores.Micro, files, _toolProgress, () => _toolPaused, cts.Token);
                _toolProgress.Start("Comparing fingerprints", rows.Count, 0);
                var hashes = rows.Select(r => vh.TryGetValue(m.Id[r], out long h) ? h : 0).ToList();
                var scopes = rows.Select(Top).ToList();
                var found = NearGroups.Find(hashes, scopes, bits, cts.Token);
                _toolProgress.Running = false;
                return found.Select(g => g.Select(k => rows[k]).ToList()).ToList();
            }, cts.Token);
            _dupResult = Arrange(groups);
            _toolText.Text = groups.Count == 0 ? "No copies found." : $"{groups.Count:N0} groups of {(near ? "near" : "exact")} copies, {groups.Sum(g => g.Count):N0} files, {BrowsePage.SizeText(Wasted(groups))} taken by the extra copies.";
            _csvBtn.IsEnabled = groups.Count > 0;
            if (InTools) await BuildViewAsync(false, 0);
        }
        catch (OperationCanceledException) { _toolText.Text = "Stopped. Find again carries on from where it got to (the work done is kept)."; }
        catch (Exception ex) { _toolText.Text = "Problem: " + ex.Message; }
        finally
        {
            timer.Stop(); _toolProgress.Running = false; _toolBar.Visibility = Visibility.Collapsed;
            _findBtn.IsEnabled = true; _pauseBtn.IsEnabled = _stopBtn.IsEnabled = false; _pauseBtn.Content = "Pause";
        }
    }

    private long Wasted(List<List<int>> groups) => _libModel is not { } m ? 0 : groups.Sum(g => g.Sum(r => m.Size[r]) - g.Max(r => m.Size[r]));

    /// <summary>Groups one after another (largest file first in each), as outlined blocks with what they cost.</summary>
    private DupResult Arrange(List<List<int>> groups)
    {
        var m = _libModel!;
        var res = new DupResult { Groups = groups };
        IEnumerable<List<int>> ordered = _settings.DupOrder == "Wasted"
            ? groups.OrderByDescending(g => g.Sum(r => m.Size[r]) - g.Max(r => m.Size[r]))
            : groups.OrderByDescending(g => g.Count).ThenByDescending(g => g.Sum(r => m.Size[r]));
        var order = new List<int>();
        int n = 0;
        foreach (var g in ordered)
        {
            var sorted = g.OrderByDescending(r => m.Size[r]).ThenBy(r => m.Path[r], StringComparer.OrdinalIgnoreCase).ToList();
            long extra = g.Sum(r => m.Size[r]) - g.Max(r => m.Size[r]);
            string top = m.Display[sorted[0]]; int c = top.IndexOf('\\'); if (c >= 0) top = top[..c];
            res.Blocks.Add(new SetBlock(order.Count, sorted.Count, "dup:" + (++n), $"{top}: {BrowsePage.SizeText(extra)} extra", sorted.Count, false));
            order.AddRange(sorted);
        }
        res.Order = order.ToArray();
        return res;
    }

    private void ShowToolProgress()
    {
        var p = _toolProgress;
        _toolBar.Visibility = p.Running ? Visibility.Visible : Visibility.Collapsed;
        if (!p.Running) { if (_toolPaused) _toolText.Text = "Paused."; return; }
        _toolBar.Value = p.Fraction;
        string left = p.Remaining is { } r ? $", about {Clock(r.TotalSeconds)} left" : "";
        long bt = Interlocked.Read(ref p.BytesTotal);
        string amount = bt > 0 ? $"{Interlocked.Read(ref p.BytesDone) / 1048576.0:N0} of {bt / 1048576.0:N0} MB" : $"{Interlocked.Read(ref p.FilesDone):N0} of {Interlocked.Read(ref p.FilesTotal):N0} files";
        _toolText.Text = (_toolPaused ? "Paused: " : "") + $"{p.What}: {p.Fraction * 100:F0}%, {amount}, {Clock(p.Elapsed.TotalSeconds)} so far{left}" + (p.Current.Length > 0 ? "  (" + Path.GetFileName(p.Current) + ")" : "");
    }

    private void ExportCsv()
    {
        if (_dupResult is not { } d || _libModel is not { } m) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export the groups", FileName = "ginkgo-duplicates.csv", Filter = "CSV files|*.csv" };
        if (dlg.ShowDialog(this) != true) return;
        static string Q(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
        var lines = new List<string> { "group,path,folder,size_bytes,width,height" };
        for (int k = 0; k < d.Blocks.Count; k++)
        {
            var b = d.Blocks[k];
            for (int i = b.First; i < b.First + b.Count; i++)
            {
                int r = d.Order[i];
                lines.Add($"{k + 1},{Q(m.Path[r])},{Q(m.Display[r])},{m.Size[r]},{m.W[r]},{m.H[r]}");
            }
        }
        try { File.WriteAllLines(dlg.FileName, lines, new System.Text.UTF8Encoding(true)); StatusText.Text = $"Exported {d.Blocks.Count:N0} groups to {dlg.FileName}"; }
        catch (Exception ex) { StatusText.Text = "Couldn't export: " + ex.Message; }
    }
}
