using Ginkgo.Data;
using System.Windows;
using System.Windows.Controls;

namespace Ginkgo.App;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _main;
    private readonly AppSettings _s;
    private readonly Engine _engine;
    private CancellationTokenSource? _sharpCts;
    private bool _init = true;

    public SettingsWindow(MainWindow main, AppSettings s, Engine engine)
    {
        InitializeComponent();
        Theme.DarkTitleBar(this);
        _main = main; _s = s; _engine = engine;
        LibraryBox.Text = s.LibraryRoot;
        CacheLine.Text = "Small thumbnails: " + (string.IsNullOrEmpty(engine.ThumbDir) ? s.FastCacheDir : engine.ThumbDir) + "   |   sharp versions: " + s.CacheDir + "   |   index: " + AppSettings.IndexPath + (engine.FastCacheNote.Length > 0 ? "   |   " + engine.FastCacheNote : "");
        foreach (var p in AppSettings.CacheProfiles) CacheBox.Items.Add(p.name);
        CacheBox.SelectedIndex = Math.Clamp(s.CacheProfile, 0, AppSettings.CacheProfiles.Length - 1);
        foreach (ComboBoxItem it in LookBox.Items) if (int.Parse((string)it.Tag) == s.LookAheadWindows) LookBox.SelectedItem = it;
        if (LookBox.SelectedItem is null) LookBox.SelectedIndex = 1;
        FadeBox.IsChecked = s.FadeSharpening;
        RoundBox.IsChecked = s.CoverRounded;
        RadiusSlider.Value = s.CoverRadius; RadiusSlider.IsEnabled = s.CoverRounded; RadiusText.Text = $"{s.CoverRadius} px";
        WarmBox.IsChecked = s.WarmCache;
        _init = false;
        OnMeasure(this, new RoutedEventArgs());
    }

    private void OnChooseLibrary(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your library folder", InitialDirectory = _s.LibraryRoot };
        if (dlg.ShowDialog(this) != true) return;
        _s.LibraryRoot = dlg.FolderName;
        LibraryBox.Text = _s.LibraryRoot;
        _main.ApplySettings();
        Result.Text = "Library folder changed. Restart Ginkgo to look through the new folder.";
    }

    private async void OnRescan(object s, RoutedEventArgs e) { Result.Text = "Looking for changes..."; await _engine.Rescan(); Result.Text = _engine.Status; }

    private void OnCacheChanged(object s, SelectionChangedEventArgs e) { if (_init) return; _s.CacheProfile = CacheBox.SelectedIndex; _main.ApplySettings(); }
    private void OnLookChanged(object s, SelectionChangedEventArgs e)
    {
        if (_init) return;
        _s.LookAheadWindows = int.Parse((string)((ComboBoxItem)LookBox.SelectedItem).Tag); _main.ApplySettings();
    }
    private void OnWarmChanged(object s, RoutedEventArgs e) { _s.WarmCache = WarmBox.IsChecked == true; _main.ApplySettings(); Result.Text = "Takes effect the next time Ginkgo starts."; }
    private void OnRoundChanged(object s, RoutedEventArgs e) { _s.CoverRounded = RoundBox.IsChecked == true; RadiusSlider.IsEnabled = _s.CoverRounded; _main.ApplySettings(); }
    private void OnRadiusChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (RadiusText is null) return;
        RadiusText.Text = $"{(int)e.NewValue} px";
        if (_init) return;
        _s.CoverRadius = (int)e.NewValue; _main.ApplySettings();
    }
    private void OnFadeChanged(object s, RoutedEventArgs e) { _s.FadeSharpening = FadeBox.IsChecked == true; _main.ApplySettings(); }

    private async Task RunSharp(bool everything)
    {
        SharpBigBtn.IsEnabled = SharpAllBtn.IsEnabled = false; SharpStopBtn.IsEnabled = true;
        _sharpCts = new CancellationTokenSource();
        try { await _engine.PrepareSharp(everything, _sharpCts.Token); Result.Text = "Sharp versions are ready. Use Update view in the main window to use them."; }
        catch (OperationCanceledException) { Result.Text = "Stopped."; }
        catch (Exception ex) { Result.Text = "Problem: " + ex.Message; }
        SharpBigBtn.IsEnabled = SharpAllBtn.IsEnabled = true; SharpStopBtn.IsEnabled = false;
    }

    private void OnSharpBig(object s, RoutedEventArgs e) => _ = RunSharp(false);
    private void OnSharpAll(object s, RoutedEventArgs e) => _ = RunSharp(true);
    private void OnSharpStop(object s, RoutedEventArgs e) => _sharpCts?.Cancel();

    private void OnMeasure(object s, RoutedEventArgs e)
    {
        var usage = _engine.CacheUsage();
        UsageText.Text = usage.Count == 0 ? "(not ready yet)" : string.Join("\n", usage.Select(u =>
            $"{u.Name,-6} {u.OnDisk / 1048576.0,9:F0} MB on disk   {u.Wasted / 1048576.0,7:F0} MB unused"));
    }

    private async void OnClean(object s, RoutedEventArgs e)
    {
        CleanBtn.IsEnabled = false; Result.Text = "Cleaning up... (the grid is rebuilt afterwards)";
        CompactResult? r = null;
        try { await _main.WithViewPaused(async () => { r = await Task.Run(() => _engine.CleanUpCache()); }); }
        catch (Exception ex) { Result.Text = "Problem: " + ex.Message; }
        if (r is not null) Result.Text = $"Cache went from {r.BytesBefore / 1048576.0:F0} MB to {r.BytesAfter / 1048576.0:F0} MB.";
        OnMeasure(this, new RoutedEventArgs());
        CleanBtn.IsEnabled = true;
    }

    private void OnClose(object s, RoutedEventArgs e) => Close();
}
