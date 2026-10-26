using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;

namespace Ginkgo.App;

public sealed class FolderNode : INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public int Count { get; init; }
    public string CountText => Count.ToString("N0");
    public FolderNode? Parent { get; init; }
    public ObservableCollection<FolderNode> Children { get; } = new();
    private bool _expanded; public bool IsExpanded { get => _expanded; set { _expanded = value; Raise(nameof(IsExpanded)); } }
    private bool? _checked = true;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    public bool? IsChecked
    {
        get => _checked;
        set
        {
            if (value == _checked) return;
            bool? v = value ?? false;                       // a click on a mixed box turns it off
            Set(v, true, true);
        }
    }

    /// <summary>Sets this box, and optionally the boxes inside and above it.</summary>
    public void Set(bool? v, bool down, bool up)
    {
        _checked = v; Raise(nameof(IsChecked));
        if (down && v is not null) foreach (var c in Children) c.Set(v, true, false);
        if (up && Parent is { } p) p.Recompute();
    }

    private void Recompute()
    {
        bool all = Children.All(c => c.IsChecked == true), none = Children.All(c => c.IsChecked == false);
        _checked = all ? true : none ? false : null;
        Raise(nameof(IsChecked));
        Parent?.Recompute();
    }
}

public partial class SelectFoldersWindow : Window
{
    private readonly ObservableCollection<FolderNode> _roots = new();
    public List<string> OffFolders { get; private set; }

    public SelectFoldersWindow(Dictionary<string, int> counts, List<string> off)
    {
        InitializeComponent();
        OffFolders = off;
        var offSet = new HashSet<string>(off, StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in counts.Keys.Where(k => k.Length > 0).OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            int cut = path.LastIndexOfAny(new[] { '\\', '/' });
            string parentPath = cut < 0 ? "" : path.Substring(0, cut);
            nodes.TryGetValue(parentPath, out var parent);
            var node = new FolderNode { Name = path.Substring(cut + 1), Path = path, Count = counts[path], Parent = parent };
            nodes[path] = node;
            if (parent is null) _roots.Add(node); else parent.Children.Add(node);
        }
        // restore the saved choice: a folder is off if it, or one above it, is in the list
        foreach (var n in nodes.Values) if (IsOff(n.Path, offSet)) n.Set(false, false, false);
        foreach (var n in nodes.Values.Where(n => n.Children.Count > 0)) RecomputeFromChildren(n);
        Tree.ItemsSource = _roots;
        UpdateAll();
        foreach (var r in _roots) r.PropertyChanged += (_, _) => UpdateAll();
        foreach (var n in nodes.Values) n.PropertyChanged += (_, _) => UpdateAll();
    }

    private static bool IsOff(string path, HashSet<string> off)
    {
        while (path.Length > 0)
        {
            if (off.Contains(path)) return true;
            int cut = path.LastIndexOfAny(new[] { '\\', '/' });
            path = cut < 0 ? "" : path.Substring(0, cut);
        }
        return false;
    }

    private static void RecomputeFromChildren(FolderNode n)
    {
        foreach (var c in n.Children) if (c.Children.Count > 0) RecomputeFromChildren(c);
        bool all = n.Children.All(c => c.IsChecked == true), none = n.Children.All(c => c.IsChecked == false);
        if (n.IsChecked == false) return;               // switched off itself: stays off
        n.Set(all ? true : none ? false : null, false, false);
    }

    private void UpdateAll()
    {
        bool all = _roots.All(r => r.IsChecked == true), none = _roots.All(r => r.IsChecked == false);
        AllBox.IsChecked = all ? true : none ? false : null;
    }

    private void OnAllClick(object s, RoutedEventArgs e)
    {
        bool on = AllBox.IsChecked == true;
        foreach (var r in _roots) r.Set(on, true, false);
        UpdateAll();
    }

    private static void Collect(FolderNode n, List<string> off)
    {
        if (n.IsChecked == false) { off.Add(n.Path); return; }       // the folder and everything inside it
        foreach (var c in n.Children) Collect(c, off);
    }

    private void OnOk(object s, RoutedEventArgs e)
    {
        var list = new List<string>();
        foreach (var r in _roots) Collect(r, list);
        OffFolders = list;
        DialogResult = true;
    }

    private void OnCancel(object s, RoutedEventArgs e) => DialogResult = false;
}
