using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The Optimisations pop-out (next to Settings in the menu bar): how much memory the thumbnails may use, and how many GIFs play
/// at once. Every change takes effect straight away.
/// </summary>
public sealed class OptimisePanel : Border
{
    public event Action? Changed;

    private readonly AppSettings _s;
    private readonly CacheWarmer _warmer;
    private readonly ToggleButton _memOn = new() { Content = "On", MinWidth = 64 };
    private readonly List<(ToggleButton b, double gb)> _caps = new();
    private readonly List<(ToggleButton b, int n)> _gifs = new();
    private readonly TextBlock _memStat = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _capLabel = new() { Text = "Most it may use", Margin = new Thickness(0, 12, 0, 6) };
    private readonly WrapPanel _capRow = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private static readonly Brush Dim = Frozen("#B5BAC0"), Txt = Frozen("#E4E4E8"), Line = Frozen("#2E2E36");
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public static readonly double[] CapChoices = { 0, 0.5, 1, 2, 4, 8, 16 };
    public static readonly int[] GifChoices = { 10, 20, 30, 0 };

    public OptimisePanel(AppSettings s, CacheWarmer warmer)
    {
        _s = s; _warmer = warmer;
        Background = Frozen("#1C1C20"); BorderBrush = Frozen("#3A3A42"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(10);
        Padding = new Thickness(16, 14, 16, 14); Width = 420;
        Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.55 };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = "Optimisations", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Txt, Margin = new Thickness(0, 0, 0, 12) });

        // ---- thumbnails in memory ----
        var memHead = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_memOn, Dock.Right);
        memHead.Children.Add(_memOn);
        memHead.Children.Add(Title("Thumbnails in memory"));
        stack.Children.Add(memHead);
        stack.Children.Add(Note("Keeps the thumbnail files in memory so scrolling never waits for the hard drive. Turn it off, or set a limit, when you want the memory for other programs. It always gives memory back when Windows runs short."));
        _capLabel.Foreground = Dim;
        stack.Children.Add(_capLabel);
        foreach (var gb in CapChoices)
        {
            var b = Choice(gb == 0 ? "Automatic" : gb < 1 ? $"{gb:0.#} GB" : $"{gb:0} GB");
            b.ToolTip = gb == 0 ? "Use whatever memory is free, keeping some spare for other programs" : $"Never more than {gb:0.#} GB";
            double g = gb;
            b.Click += (_, _) => { _s.ThumbMemoryCapGB = g; _warmer.Cap = (long)(g * 1073741824.0); Sync(); Changed?.Invoke(); };
            _caps.Add((b, gb)); _capRow.Children.Add(b);
        }
        stack.Children.Add(_capRow);
        _memStat.Foreground = Dim;
        stack.Children.Add(_memStat);
        _memOn.Click += (_, _) =>
        {
            _s.WarmCache = _memOn.IsChecked == true;
            _warmer.Enabled = _s.WarmCache;
            Sync(); Changed?.Invoke();
        };

        stack.Children.Add(new Border { Height = 1, Background = Line, Margin = new Thickness(0, 14, 0, 14) });

        // ---- GIFs ----
        stack.Children.Add(Title("GIFs playing at once"));
        stack.Children.Add(Note("The GIFs nearest the middle of the screen play; the rest show their first frame. Fewer is lighter on the processor and memory."));
        var gifRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var n in GifChoices)
        {
            var b = Choice(n == 0 ? "All" : n.ToString());
            b.ToolTip = n == 0 ? "Every GIF on screen plays" : $"Up to {n} GIFs play at a time";
            int k = n;
            b.Click += (_, _) => { _s.GifLimit = k; Sync(); Changed?.Invoke(); };
            _gifs.Add((b, n)); gifRow.Children.Add(b);
        }
        stack.Children.Add(gifRow);

        Child = stack;
        _timer.Tick += (_, _) => UpdateStat();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Sync(); _timer.Start(); } else _timer.Stop(); };
        Sync();
    }

    private static TextBlock Title(string t) => new() { Text = t, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Txt, VerticalAlignment = VerticalAlignment.Center };
    private static TextBlock Note(string t) => new() { Text = t, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private static ToggleButton Choice(string t) => new() { Content = t, MinWidth = 52, Margin = new Thickness(0, 0, 6, 6) };

    public void Sync()
    {
        _memOn.IsChecked = _s.WarmCache;
        _memOn.Content = _s.WarmCache ? "On" : "Off";
        foreach (var (b, gb) in _caps) { b.IsChecked = Math.Abs(gb - _s.ThumbMemoryCapGB) < 0.01; b.IsEnabled = _s.WarmCache; }
        _capLabel.Opacity = _s.WarmCache ? 1 : 0.45;
        foreach (var (b, n) in _gifs) b.IsChecked = n == _s.GifLimit;
        UpdateStat();
    }

    private void UpdateStat()
    {
        double held = Interlocked.Read(ref _warmer.InMemory) / 1073741824.0, free = CacheWarmer.AvailableBytes() / 1073741824.0;
        long total = Interlocked.Read(ref _warmer.Total);
        if (!_s.WarmCache) { _memStat.Text = $"Off: thumbnails are read from the drive as needed.  Free memory {free:0.0} GB"; return; }
        string pct = total > 0 ? $" ({Interlocked.Read(ref _warmer.Done) * 100 / total}% of all thumbnails)" : "";
        string note = _warmer.Note.Length > 0 ? "  ·  " + _warmer.Note : "";
        _memStat.Text = $"Using {held:0.0} GB{pct}  ·  free memory {free:0.0} GB{note}";
    }
}
