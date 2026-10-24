using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Ginkgo.Core.Layout;

namespace Ginkgo.Prototype;

public partial class MainWindow : Window
{
    private int _frames;
    private double _fps;
    private long _lastTick = Stopwatch.GetTimestamp();
    private bool _syncing;
    private string _mode = "none";
    private string _note = "";

    public MainWindow()
    {
        InitializeComponent();
        View.ScrollChanged += SyncBar;
        CompositionTarget.Rendering += (_, _) => _frames++;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(0.5) };
        timer.Tick += (_, _) => UpdateStats();
        timer.Start();
        Loaded += (_, _) => LoadSynthetic(200_000);
    }

    private void OnSynth200k(object s, RoutedEventArgs e) => LoadSynthetic(200_000);
    private void OnSynth1m(object s, RoutedEventArgs e) => LoadSynthetic(1_000_000);

    private void LoadSynthetic(int n)
    {
        var rnd = new Random(42);
        var items = new ItemSpec[n];
        for (int i = 0; i < n; i++)
        {
            // mix of portrait, square and landscape like a real library
            float a = rnd.Next(10) switch { < 5 => 0.55f + (float)rnd.NextDouble() * 0.25f, < 7 => 1f, _ => 1.3f + (float)rnd.NextDouble() * 0.6f };
            items[i] = new ItemSpec(a);
        }
        View.SetItems(items, null);
        _mode = $"{n:N0} fake tiles"; _note = "";
    }

    private async void OnReal(object s, RoutedEventArgs e)
    {
        const string root = @"E:\Pictures\IMGs";
        if (!Directory.Exists(root)) { _note = "E:\\Pictures\\IMGs not found"; return; }
        RealFiles.IsEnabled = false;
        _note = "scanning...";
        var sw = Stopwatch.StartNew();
        var exts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".webm", ".jfif", ".avif" };
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Doujinshi", "Sort", "sfw", "Anthologies, Art Books & Collections", "Grabber" };
        var names = await Task.Run(() =>
        {
            var list = new List<string>(200_000);
            foreach (var top in Directory.EnumerateDirectories(root))
            {
                if (skip.Contains(Path.GetFileName(top))) continue;
                foreach (var f in Directory.EnumerateFiles(top, "*", SearchOption.AllDirectories))
                    if (exts.Contains(Path.GetExtension(f))) list.Add(f);
            }
            return list;
        });
        var items = new ItemSpec[names.Count];
        var labels = new string[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            // dimensions are not read yet in this prototype: use a stable fake shape from the file name
            uint h = (uint)names[i].GetHashCode();
            items[i] = new ItemSpec(0.6f + (h % 100) / 100f * 1.0f);
            labels[i] = Path.GetFileName(names[i]);
        }
        View.SetItems(items, labels);
        _mode = $"{names.Count:N0} real files (shapes are fake)";
        _note = $"scan took {sw.Elapsed.TotalSeconds:F1}s";
        RealFiles.IsEnabled = true;
    }

    private void OnBarChanged(object s, RoutedPropertyChangedEventArgs<double> e)
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

    private void UpdateStats()
    {
        long now = Stopwatch.GetTimestamp();
        double secs = (now - _lastTick) / (double)Stopwatch.Frequency;
        _fps = _frames / secs; _frames = 0; _lastTick = now;
        double mb = Process.GetCurrentProcess().WorkingSet64 / 1048576.0;
        Stats.Text = $"{_mode} | drawn {View.LastVisibleCount} | frame draw {View.LastRenderMs:F1} ms | {_fps:F0} fps | RAM {mb:F0} MB | Ctrl+wheel = zoom, middle-click = autoscroll {_note}";
    }
}
