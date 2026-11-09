using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The Select folders pop-out: one coloured button per folder, as wide as its name, flowing like masonry.
/// Green = shown, dark = hidden, amber = some of it shown. Click a button to switch the folder and everything in it;
/// the arrow opens its subfolders (and a "files here" button for the folder's own files).
/// </summary>
public sealed class FolderPanel : Border
{
    private sealed class Node
    {
        public string Path = "", Name = "";
        public Node? Parent;
        public List<Node> Kids = new();
        public int Own; public long OwnSize, OwnNewest;
        public int Sub; public long SubSize, SubNewest;
    }

    public event Action<HashSet<string>>? Changed;
    /// <summary>A folder was made standalone or put back, or sent to Rarely used or brought back.</summary>
    public event Action<HashSet<string>, HashSet<string>>? PlacesChanged;
    private readonly HashSet<string> _standalone = new(StringComparer.OrdinalIgnoreCase), _rarely = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBlock _note;
    /// <summary>Folders that stand on their own: a standalone or rarely used folder is left alone by its parent's switch.</summary>
    private bool Independent(Node n) => n.Path.Length > 0 && (_standalone.Contains(n.Path) || _rarely.Contains(n.Path));
    private static bool IsConnectedArt(Node n) => n.Parent is { Path.Length: 0 } && n.Name.Equals(Ginkgo.Core.ConnectedArt.FolderName, StringComparison.OrdinalIgnoreCase);
    private IEnumerable<Node> KidsOf(Node n) => IsConnectedArt(n) ? Enumerable.Empty<Node>() : n.Kids.Where(k => !Independent(k));

    private readonly JustifyPanel _flow = new();
    private readonly ComboBox _sortBox = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ToggleButton _rev = new();
    private readonly TextBlock _summary = new() { Foreground = (Brush)Application.Current.FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private List<Node> _roots = new();
    private string _sort = "Name"; private bool _reverse;
    private sealed class Btn { public Border B = null!; public TextBlock Name = null!; public Node N = null!; public bool Own; public Brush Fill = null!, Hover = null!; }
    private readonly List<Btn> _btns = new();
    private bool _painting, _paintOn;

    private static readonly Brush On = Frozen("#3E7A48"), OnHover = Frozen("#4B9058"), OffB = Frozen("#2A2A30"), OffHover = Frozen("#36363E"),
        Part = Frozen("#8A6A2A"), PartHover = Frozen("#A07C31"), Txt = Frozen("#E4E4E8"), TxtOff = Frozen("#8D939A"), Dim = Frozen("#B5BAC0");
    private static readonly Brush[] DepthEdge = { Frozen("#00000000"), Frozen("#6C9BD2"), Frozen("#B58BD6"), Frozen("#D6AC6C"), Frozen("#6CC4B4") };
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public FolderPanel(string sort, bool reverse)
    {
        _sort = sort; _reverse = reverse;
        Background = Frozen("#1C1C20"); BorderBrush = Frozen("#3A3A42"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6);
        Padding = new Thickness(12); Width = 640;

        foreach (var (label, key) in new[] { ("Name", "Name"), ("Number of files", "Files"), ("Size on disk", "Size"), ("Newest added", "Newest") })
            _sortBox.Items.Add(new ComboBoxItem { Content = label, Tag = key });
        foreach (ComboBoxItem it in _sortBox.Items) if ((string)it.Tag == _sort) _sortBox.SelectedItem = it;
        if (_sortBox.SelectedItem is null) _sortBox.SelectedIndex = 0;
        _sortBox.SelectionChanged += (_, _) => { _sort = (string)((ComboBoxItem)_sortBox.SelectedItem).Tag; Rebuild(); SortChanged?.Invoke(_sort, _reverse); };
        _rev.Content = "Reverse"; _rev.IsChecked = _reverse; _rev.ToolTip = "Flip the order (for Name: Z to A; for the others: smallest / oldest first)";
        _rev.Click += (_, _) => { _reverse = _rev.IsChecked == true; Rebuild(); SortChanged?.Invoke(_sort, _reverse); };

        var head = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        head.Children.Add(new TextBlock { Text = "Sort", Foreground = Dim, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(_sortBox); head.Children.Add(_rev);
        var all = new Button { Content = "All on" }; all.Click += (_, _) => SetAll(true);
        var none = new Button { Content = "All off" }; none.Click += (_, _) => SetAll(false);
        var fold = new Button { Content = "Close all" }; fold.Click += (_, _) => { _open.Clear(); Rebuild(); };
        head.Children.Add(all); head.Children.Add(none); head.Children.Add(fold); head.Children.Add(_summary);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460, Content = _flow };
        _note = new TextBlock
        {
            Text = "Click a folder to show or hide it and everything inside it. The arrow opens its subfolders. Right-click a folder to make it standalone (its own row and switch) or send it to Rarely used. Counts follow the General / NSFW / Very switches.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12
        };
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(scroll); stack.Children.Add(_note);
        Child = stack;
    }

    public event Action<string, bool>? SortChanged;

    /// <summary>The words under the folders (the NSFW and Very pop-outs say what their switches do).</summary>
    public string NoteText { set => _note.Text = value; }

    /// <summary>Gives the panel the folders, which of them are hidden, and which are standalone or rarely used.</summary>
    public void Load(Dictionary<string, LibraryModel.FolderStat> stats, IEnumerable<string> off, IEnumerable<string>? standalone = null, IEnumerable<string>? rarely = null)
    {
        _off.Clear(); foreach (var o in off) _off.Add(o);
        _standalone.Clear(); if (standalone is not null) foreach (var x in standalone) _standalone.Add(x);
        _rarely.Clear(); if (rarely is not null) foreach (var x in rarely) _rarely.Add(x);
        _nodes.Clear(); _roots = new();
        var root = new Node { Path = "", Name = "(files directly in the library)" };
        _nodes[""] = root;
        foreach (var key in stats.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Length == 0) continue;
            Ensure(key);
        }
        foreach (var (key, st) in stats)
            if (_nodes.TryGetValue(key, out var n)) { n.Own = st.Own; n.OwnSize = st.Size; n.OwnNewest = st.Newest; }
        Tally(root);
        _roots = root.Kids.Where(k => k.Sub > 0 && !Independent(k)).ToList();
        if (root.Own > 0) _roots.Insert(0, new Node { Path = "", Name = root.Name, Own = root.Own, Sub = root.Own, SubSize = root.OwnSize, SubNewest = root.OwnNewest });
        Rebuild();
    }

    private Node Ensure(string path)
    {
        if (_nodes.TryGetValue(path, out var n)) return n;
        int cut = path.LastIndexOfAny(new[] { '\\', '/' });
        var parent = Ensure(cut < 0 ? "" : path.Substring(0, cut));
        n = new Node { Path = path, Name = cut < 0 ? path : path.Substring(cut + 1), Parent = parent };
        parent.Kids.Add(n); _nodes[path] = n;
        return n;
    }

    /// <summary>Totals include everything beneath, except standalone and rarely used folders (they count on their own row).</summary>
    private void Tally(Node n)
    {
        n.Sub = n.Own; n.SubSize = n.OwnSize; n.SubNewest = n.OwnNewest;
        foreach (var k in n.Kids)
        {
            Tally(k);
            if (Independent(k)) continue;
            n.Sub += k.Sub; n.SubSize += k.SubSize; n.SubNewest = Math.Max(n.SubNewest, k.SubNewest);
        }
    }

    private IEnumerable<Node> Sorted(IEnumerable<Node> nodes)
    {
        var list = nodes.Where(n => n.Sub > 0).ToList();
        Comparison<Node> cmp = _sort switch
        {
            "Files" => (a, b) => b.Sub.CompareTo(a.Sub),
            "Size" => (a, b) => b.SubSize.CompareTo(a.SubSize),
            "Newest" => (a, b) => b.SubNewest.CompareTo(a.SubNewest),
            _ => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
        };
        list.Sort((a, b) => { int c = cmp(a, b); return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        if (_reverse) list.Reverse();
        return list;
    }

    // 0 = hidden, 1 = shown, 2 = partly
    private int State(Node n)
    {
        int shown = 0, hidden = 0;
        void Walk(Node x)
        {
            if (x.Own > 0) { if (_off.Contains(x.Path)) hidden++; else shown++; }
            foreach (var k in x.Kids) if (!Independent(k)) Walk(k);
        }
        Walk(n);
        return shown > 0 && hidden > 0 ? 2 : shown > 0 ? 1 : 0;
    }

    private void SetSubtree(Node n, bool on)
    {
        void Walk(Node x) { if (on) _off.Remove(x.Path); else _off.Add(x.Path); foreach (var k in x.Kids) if (!Independent(k)) Walk(k); }
        if (n.Path.Length == 0 && n.Kids.Count == 0) { if (on) _off.Remove(""); else _off.Add(""); return; }
        Walk(n);
    }

    /// <summary>Every folder on or off, except Rarely used ones (they are switched one by one).</summary>
    private void SetAll(bool on)
    {
        bool InRarely(Node n) { for (var x = n; x is not null; x = x.Parent) if (x.Path.Length > 0 && _rarely.Contains(x.Path)) return true; return false; }
        foreach (var n in _nodes.Values) { if (InRarely(n)) continue; if (on) _off.Remove(n.Path); else _off.Add(n.Path); }
        Rebuild(); Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        _flow.Children.Clear(); _btns.Clear();
        foreach (var n in Sorted(_roots)) Add(n, 0, _flow);
        // standalone folders: rows of their own, labelled with their path
        var alone = _standalone.Where(p => !_rarely.Contains(p) && _nodes.TryGetValue(p, out var x) && x.Sub > 0).Select(p => _nodes[p]);
        foreach (var n in Sorted(alone)) Add(n, 0, _flow, pathLabel: true);
        var rare = _rarely.Where(p => _nodes.TryGetValue(p, out var x) && x.Sub > 0).Select(p => _nodes[p]).ToList();
        if (rare.Count > 0)
        {
            var title = new TextBlock { Text = "Rarely used  (off unless switched on here; All on and All off leave them alone)", Foreground = Dim, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2, 10, 0, 2) };
            JustifyPanel.SetFullRow(title, true);
            _flow.Children.Add(title);
            foreach (var n in Sorted(rare)) Add(n, 0, _flow, pathLabel: true);
        }
        UpdateSummary();
    }

    private static string PathLabel(string path) => path.Replace("\\", "  ›  ");

    private void ShowMenu(Node n)
    {
        if (n.Path.Length == 0) return;
        var menu = new ContextMenu();
        void Item(string text, Action act) { var it = new MenuItem { Header = text }; it.Click += (_, _) => { act(); Rebuild(); PlacesChanged?.Invoke(new(_standalone, StringComparer.OrdinalIgnoreCase), new(_rarely, StringComparer.OrdinalIgnoreCase)); }; menu.Items.Add(it); }
        bool top = n.Parent is { Path.Length: 0 };
        if (_rarely.Contains(n.Path)) Item("Move back from Rarely used", () => _rarely.Remove(n.Path));
        else
        {
            Item("Send to Rarely used (switches it off)", () =>
            {
                _rarely.Add(n.Path);
                void Off(Node x) { _off.Add(x.Path); foreach (var k in x.Kids) Off(k); }
                Off(n);
                Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase));
            });
            if (_standalone.Contains(n.Path)) Item($"Put back inside {n.Parent?.Name}", () => _standalone.Remove(n.Path));
            else if (!top) Item("Make standalone (its own row and switch)", () => _standalone.Add(n.Path));
        }
        menu.IsOpen = true;
    }

    private void UpdateSummary()
    {
        int shown = _nodes.Values.Where(n => n.Own > 0 && !_off.Contains(n.Path)).Sum(n => n.Own);
        int total = _nodes.Values.Sum(n => n.Own);
        _summary.Text = $"{shown:N0} of {total:N0} files shown";
    }

    private static readonly Brush[] GroupFill = { Frozen("#1F2733"), Frozen("#2A2033"), Frozen("#332C1F"), Frozen("#1F3330") };

    /// <summary>A folder's button; when it is open, its subfolders sit in a tinted, outlined box right below it.</summary>
    private void Add(Node n, int depth, JustifyPanel into, bool pathLabel = false)
    {
        bool hasKids = KidsOf(n).Any(k => k.Sub > 0);
        bool open = hasKids && _open.Contains(n.Path);
        into.Children.Add(MakeButton(n, depth, hasKids, open, ownOnly: false, pathLabel));
        if (!open) return;
        int d = Math.Min(depth, 3);
        var inner = new JustifyPanel { Gap = 5 };
        var head = new DockPanel { Margin = new Thickness(2, 0, 0, 6) };
        var subsOn = new Button { Content = "Subfolders on", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
        var subsOff = new Button { Content = "Subfolders off", Padding = new Thickness(6, 0, 6, 0), FontSize = 11, Margin = new Thickness(6, 0, 0, 0) };
        void Subs(bool on) { foreach (var k in KidsOf(n)) SetSubtree(k, on); Recolor(); Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase)); }
        subsOn.Click += (_, _) => Subs(true); subsOff.Click += (_, _) => Subs(false);
        DockPanel.SetDock(subsOff, Dock.Right); DockPanel.SetDock(subsOn, Dock.Right);
        head.Children.Add(subsOff); head.Children.Add(subsOn);
        head.Children.Add(new TextBlock { Text = "Inside " + n.Name, Foreground = DepthEdge[d + 1], FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(inner);
        if (n.Own > 0) inner.Children.Add(MakeButton(n, depth + 1, false, false, ownOnly: true));
        foreach (var k in Sorted(KidsOf(n))) Add(k, depth + 1, inner);
        var box = new Border
        {
            Child = stack, Background = GroupFill[d], BorderBrush = DepthEdge[d + 1], BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 7, 9, 9), Margin = new Thickness(0, 0, 0, 2)
        };
        JustifyPanel.SetFullRow(box, true);
        into.Children.Add(box);
    }

    private int StateOf(Btn x) => x.Own ? (_off.Contains(x.N.Path) ? 0 : 1) : State(x.N);

    /// <summary>Re-colours every button in place (no rebuild, so the mouse keeps its place while painting).</summary>
    private void Recolor()
    {
        foreach (var x in _btns) ColorOne(x);
        UpdateSummary();
    }

    private void ColorOne(Btn x)
    {
        int st = StateOf(x);
        x.Fill = st == 1 ? On : st == 2 ? Part : OffB;
        x.Hover = st == 1 ? OnHover : st == 2 ? PartHover : OffHover;
        x.Name.Foreground = st == 0 ? TxtOff : Txt;
        x.B.Background = x.B.IsMouseOver ? x.Hover : x.Fill;
    }

    private void Apply(Btn x, bool on)
    {
        if (x.Own) { if (on) _off.Remove(x.N.Path); else _off.Add(x.N.Path); }
        else SetSubtree(x.N, on);
        Recolor();
        Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase));
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e) { _painting = false; base.OnPreviewMouseLeftButtonUp(e); }

    private FrameworkElement MakeButton(Node n, int depth, bool hasKids, bool open, bool ownOnly, bool pathLabel = false)
    {
        var x = new Btn { N = n, Own = ownOnly };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        if (hasKids)
        {
            var arrow = new TextBlock
            {
                Text = open ? "\uE70D" : "\uE76C", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Txt,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(7, 6, 7, 6), Margin = new Thickness(-8, -4, 2, -4),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = open ? "Close subfolders" : "Show subfolders"
            };
            arrow.MouseLeftButtonDown += (_, e) => e.Handled = true;           // the arrow only opens; it doesn't start painting
            arrow.MouseLeftButtonUp += (_, e) => { if (_open.Contains(n.Path)) _open.Remove(n.Path); else _open.Add(n.Path); Rebuild(); e.Handled = true; };
            arrow.MouseEnter += (_, _) => arrow.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            arrow.MouseLeave += (_, _) => arrow.Background = Brushes.Transparent;
            row.Children.Add(arrow);
        }
        string name = ownOnly ? "files here" : pathLabel ? PathLabel(n.Path) : n.Name;
        int count = ownOnly ? n.Own : n.Sub;
        x.Name = new TextBlock { Text = name, Foreground = Txt, VerticalAlignment = VerticalAlignment.Center, FontStyle = ownOnly ? FontStyles.Italic : FontStyles.Normal, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 520 };
        row.Children.Add(x.Name);
        row.Children.Add(new TextBlock { Text = count.ToString("N0"), Foreground = Dim, Opacity = 0.75, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) });
        x.B = new Border
        {
            Child = row, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 5, 10, 5), Cursor = Cursors.Hand,
            BorderBrush = DepthEdge[Math.Min(depth, DepthEdge.Length - 1)], BorderThickness = new Thickness(0, 0, 0, depth == 0 ? 0 : 2),
            ToolTip = ownOnly ? $"{count:N0} files directly in this folder" : $"{count:N0} files, {n.SubSize / 1048576.0:N0} MB" + (n.Path.Length > 0 ? "\n" + n.Path : "")
        };
        _btns.Add(x);
        // press on a folder and keep the button down while moving over others: they all switch the same way
        x.B.MouseLeftButtonDown += (_, e) =>
        {
            _paintOn = StateOf(x) != 1;
            _painting = true;
            Apply(x, _paintOn);
            e.Handled = true;
        };
        x.B.MouseEnter += (_, _) =>
        {
            x.B.Background = x.Hover;
            if (_painting)
            {
                if (Mouse.LeftButton != MouseButtonState.Pressed) { _painting = false; return; }
                if ((StateOf(x) == 1) != _paintOn || StateOf(x) == 2) Apply(x, _paintOn);
            }
        };
        x.B.MouseLeave += (_, _) => x.B.Background = x.Fill;
        if (!ownOnly) x.B.MouseRightButtonUp += (_, e) => { ShowMenu(n); e.Handled = true; };
        ColorOne(x);
        return x.B;
    }
}


/// <summary>
/// Lays buttons out in rows and stretches every row to the full width, so there are no gaps at the ends.
/// A child marked FullRow always gets a row of its own (used for the boxes holding open subfolders).
/// </summary>
public sealed class JustifyPanel : Panel
{
    public static readonly DependencyProperty FullRowProperty = DependencyProperty.RegisterAttached("FullRow", typeof(bool), typeof(JustifyPanel));
    public static void SetFullRow(DependencyObject o, bool v) => o.SetValue(FullRowProperty, v);
    public static bool GetFullRow(DependencyObject o) => (bool)o.GetValue(FullRowProperty);
    public double Gap { get; set; } = 6;

    protected override Size MeasureOverride(Size avail)
    {
        double w = double.IsInfinity(avail.Width) ? 600 : avail.Width;
        foreach (UIElement c in InternalChildren) c.Measure(new Size(GetFullRow(c) ? w : w, double.PositiveInfinity));
        return new Size(w, Layout(w, false));
    }

    protected override Size ArrangeOverride(Size final) => new(final.Width, Layout(final.Width, true));

    private double Layout(double w, bool arrange)
    {
        double y = 0;
        var row = new List<UIElement>();
        double used = 0;
        void Flush(bool last)
        {
            if (row.Count == 0) return;
            double sum = row.Sum(c => Math.Min(c.DesiredSize.Width, w));
            double room = w - Gap * (row.Count - 1);
            double f = sum > 0 ? room / sum : 1;
            if (last) f = Math.Min(f, 3.0);                       // a short last row is not stretched absurdly
            double h = row.Max(c => c.DesiredSize.Height), x = 0;
            foreach (var c in row)
            {
                double cw = Math.Min(c.DesiredSize.Width, w) * f;
                if (arrange) c.Arrange(new Rect(x, y, cw, h));
                x += cw + Gap;
            }
            y += h + Gap; row.Clear(); used = 0;
        }
        UIElement? lastChild = null;
        foreach (UIElement c in InternalChildren)
        {
            if (c.Visibility == Visibility.Collapsed) continue;
            lastChild = c;
            if (GetFullRow(c))
            {
                Flush(false);
                if (arrange) c.Arrange(new Rect(0, y, w, c.DesiredSize.Height));
                y += c.DesiredSize.Height + Gap;
                continue;
            }
            double cw = Math.Min(c.DesiredSize.Width, w);
            if (row.Count > 0 && used + Gap + cw > w) Flush(false);
            used += (row.Count > 0 ? Gap : 0) + cw; row.Add(c);
        }
        Flush(true);
        return Math.Max(0, y - Gap);
    }
}
