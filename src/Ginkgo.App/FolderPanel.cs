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

    private readonly WrapPanel _flow = new() { Orientation = Orientation.Horizontal };
    private readonly ComboBox _sortBox = new() { Width = 140, Margin = new Thickness(0, 0, 8, 0) };
    private readonly ToggleButton _rev = new();
    private readonly TextBlock _summary = new() { Foreground = (Brush)Application.Current.FindResource("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    private readonly HashSet<string> _off = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private List<Node> _roots = new();
    private string _sort = "Name"; private bool _reverse;
    private bool _quiet;

    private static readonly Brush On = Frozen("#3E7A48"), OnHover = Frozen("#4B9058"), OffB = Frozen("#2A2A30"), OffHover = Frozen("#36363E"),
        Part = Frozen("#8A6A2A"), PartHover = Frozen("#A07C31"), Txt = Frozen("#E4E4E8"), TxtOff = Frozen("#8D939A"), Dim = Frozen("#B5BAC0");
    private static readonly Brush[] DepthEdge = { Frozen("#00000000"), Frozen("#6BA675"), Frozen("#6C8FB8"), Frozen("#B08AC4"), Frozen("#C4A06C") };
    private static Brush Frozen(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; b.Freeze(); return b; }

    public FolderPanel(string sort, bool reverse)
    {
        _sort = sort; _reverse = reverse;
        Background = Frozen("#1E1E23"); BorderBrush = Frozen("#3A3A42"); BorderThickness = new Thickness(1); CornerRadius = new CornerRadius(6);
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
        var note = new TextBlock
        {
            Text = "Click a folder to show or hide it and everything inside it. The arrow opens its subfolders. Counts follow the General / NSFW / Very switches.",
            Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), FontSize = 12
        };
        var stack = new StackPanel();
        stack.Children.Add(head); stack.Children.Add(scroll); stack.Children.Add(note);
        Child = stack;
    }

    public event Action<string, bool>? SortChanged;

    /// <summary>Gives the panel the folders and which of them are hidden.</summary>
    public void Load(Dictionary<string, LibraryModel.FolderStat> stats, IEnumerable<string> off)
    {
        _off.Clear(); foreach (var o in off) _off.Add(o);
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
        _roots = root.Kids.Where(k => k.Sub > 0).ToList();
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

    private static void Tally(Node n)
    {
        n.Sub = n.Own; n.SubSize = n.OwnSize; n.SubNewest = n.OwnNewest;
        foreach (var k in n.Kids) { Tally(k); n.Sub += k.Sub; n.SubSize += k.SubSize; n.SubNewest = Math.Max(n.SubNewest, k.SubNewest); }
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
            foreach (var k in x.Kids) Walk(k);
        }
        Walk(n);
        return shown > 0 && hidden > 0 ? 2 : shown > 0 ? 1 : 0;
    }

    private void SetSubtree(Node n, bool on)
    {
        void Walk(Node x) { if (on) _off.Remove(x.Path); else _off.Add(x.Path); foreach (var k in x.Kids) Walk(k); }
        if (n.Path.Length == 0 && n.Kids.Count == 0) { if (on) _off.Remove(""); else _off.Add(""); return; }
        Walk(n);
    }

    private void SetAll(bool on)
    {
        foreach (var n in _nodes.Values) { if (on) _off.Remove(n.Path); else _off.Add(n.Path); }
        Rebuild(); Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase));
    }

    private void Rebuild()
    {
        _flow.Children.Clear();
        foreach (var n in Sorted(_roots)) Add(n, 0);
        int shown = _nodes.Values.Where(n => n.Own > 0 && !_off.Contains(n.Path)).Sum(n => n.Own);
        int total = _nodes.Values.Sum(n => n.Own);
        _summary.Text = $"{shown:N0} of {total:N0} files shown";
    }

    private void Add(Node n, int depth)
    {
        bool hasKids = n.Kids.Any(k => k.Sub > 0);
        bool open = hasKids && _open.Contains(n.Path);
        _flow.Children.Add(MakeButton(n, depth, hasKids, open, ownOnly: false));
        if (!open) return;
        if (n.Own > 0)
            _flow.Children.Add(MakeButton(n, depth + 1, false, false, ownOnly: true));
        foreach (var k in Sorted(n.Kids)) Add(k, depth + 1);
    }

    private FrameworkElement MakeButton(Node n, int depth, bool hasKids, bool open, bool ownOnly)
    {
        int state = ownOnly ? (_off.Contains(n.Path) ? 0 : 1) : State(n);
        var fill = state == 1 ? On : state == 2 ? Part : OffB;
        var hover = state == 1 ? OnHover : state == 2 ? PartHover : OffHover;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (hasKids)
        {
            var arrow = new TextBlock
            {
                Text = open ? "" : "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = Txt,
                VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(7, 6, 7, 6), Margin = new Thickness(-8, -4, 2, -4),
                Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = open ? "Close subfolders" : "Show subfolders"
            };
            arrow.MouseLeftButtonUp += (_, e) => { if (_open.Contains(n.Path)) _open.Remove(n.Path); else _open.Add(n.Path); Rebuild(); e.Handled = true; };
            arrow.MouseEnter += (_, _) => arrow.Background = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            arrow.MouseLeave += (_, _) => arrow.Background = Brushes.Transparent;
            row.Children.Add(arrow);
        }
        string name = ownOnly ? "files here" : n.Name;
        int count = ownOnly ? n.Own : n.Sub;
        row.Children.Add(new TextBlock { Text = name, Foreground = state == 0 ? TxtOff : Txt, VerticalAlignment = VerticalAlignment.Center, FontStyle = ownOnly ? FontStyles.Italic : FontStyles.Normal });
        row.Children.Add(new TextBlock { Text = count.ToString("N0"), Foreground = Dim, Opacity = 0.75, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 1, 0, 0) });
        var b = new Border
        {
            Child = row, Background = fill, CornerRadius = new CornerRadius(5), Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand,
            BorderBrush = DepthEdge[Math.Min(depth, DepthEdge.Length - 1)], BorderThickness = new Thickness(depth == 0 ? 0 : 3, 0, 0, 0),
            ToolTip = ownOnly ? $"{count:N0} files directly in this folder" : $"{count:N0} files, {n.SubSize / 1048576.0:N0} MB" + (n.Path.Length > 0 ? "\n" + n.Path : "")
        };
        b.MouseEnter += (_, _) => b.Background = hover;
        b.MouseLeave += (_, _) => b.Background = fill;
        b.MouseLeftButtonUp += (_, e) =>
        {
            if (ownOnly) { if (!_off.Remove(n.Path)) _off.Add(n.Path); }
            else SetSubtree(n, state != 1);
            Rebuild(); Changed?.Invoke(new HashSet<string>(_off, StringComparer.OrdinalIgnoreCase));
            e.Handled = true;
        };
        return b;
    }
}
