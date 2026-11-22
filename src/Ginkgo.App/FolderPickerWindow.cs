using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// Pick a folder in the library to move or copy files to. Type to narrow the list (every word must be in the folder's path),
/// Up and Down to choose, Enter to go. A new folder can be made inside the chosen one (or at the top), and "Outside IMGs" opens
/// Windows' own folder picker.
/// </summary>
public sealed class FolderPickerWindow : Window
{
    private static List<string>? _all;                       // every folder in the library (relative), read once per run
    private static readonly List<string> Recent = new();     // where files went lately (relative or full), newest first

    private readonly string _root;
    private readonly TextBox _search = new() { Height = 30, FontSize = 14, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBlock _count = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7 };
    private readonly Button _go;
    /// <summary>The chosen folder (a full path), or null.</summary>
    public string? Chosen { get; private set; }

    public FolderPickerWindow(string root, string verb, int files)
    {
        _root = root;
        Title = $"{verb} {(files == 1 ? "1 file" : $"{files:N0} files")} to…";
        Width = 640; Height = 620; MinWidth = 420; MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBg");
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(ForegroundProperty, "Text");
        Theme.DarkTitleBar(this);

        var dock = new DockPanel { Margin = new Thickness(14) };
        var head = new TextBlock { Text = "Type to find a folder: every word you type must be in its path. Up and Down choose, Enter goes.", TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(head, Dock.Top); dock.Children.Add(head);
        DockPanel.SetDock(_search, Dock.Top); dock.Children.Add(_search);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = new Button { Content = "Cancel", Width = 90, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        _go = new Button { Content = $"{verb} here", MinWidth = 110, Height = 30, Margin = new Thickness(8, 0, 0, 0), IsDefault = true, Padding = new Thickness(12, 0, 12, 0) };
        var make = new Button { Content = "New folder…", Height = 30, Padding = new Thickness(10, 0, 10, 0), ToolTip = "Make a folder inside the chosen one (or at the top of the library if none is chosen)" };
        var outside = new Button { Content = "Outside IMGs…", Height = 30, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Choose any folder with Windows' folder picker" };
        DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(_go, Dock.Right);
        buttons.Children.Add(cancel); buttons.Children.Add(_go);
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(make); left.Children.Add(outside);
        left.Children.Add(new Border { Width = 12 }); left.Children.Add(_count);
        buttons.Children.Add(left);
        dock.Children.Add(buttons);

        var frame = new Border { Margin = new Thickness(0, 8, 0, 0), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list };
        frame.SetResourceReference(Border.BorderBrushProperty, "Edge");
        frame.SetResourceReference(Border.BackgroundProperty, "Surface2");
        dock.Children.Add(frame);
        Content = dock;

        VirtualizingPanel.SetIsVirtualizing(_list, true);
        VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
        _list.MouseDoubleClick += (_, _) => Go();
        _go.Click += (_, _) => Go();
        cancel.Click += (_, _) => Close();
        make.Click += (_, _) => MakeFolder();
        outside.Click += (_, _) => Outside();
        _search.TextChanged += (_, _) => Fill();
        _search.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up or Key.PageDown or Key.PageUp)
            {
                int n = _list.Items.Count; if (n == 0) return;
                int step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 };
                _list.SelectedIndex = Math.Clamp((_list.SelectedIndex < 0 ? -1 : _list.SelectedIndex) + step, 0, n - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
            }
        };
        Loaded += async (_, _) =>
        {
            _search.Focus();
            if (_all is null)
            {
                _count.Text = "reading the library's folders...";
                _all = await Task.Run(() => ReadAll(_root));
            }
            Fill();
        };
    }

    private sealed record Row(string Rel, string Show, bool IsRecent)
    {
        public override string ToString() => Show;
    }

    private static List<string> ReadAll(string root)
    {
        var list = new List<string>();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            foreach (var d in Directory.EnumerateDirectories(root, "*", opts))
            {
                string rel = Path.GetRelativePath(root, d);
                if (rel.StartsWith(".") || rel.Contains("\\.")) continue;
                list.Add(rel);
            }
        }
        catch { }
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private void Fill()
    {
        if (_all is null) return;
        var words = _search.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Match(string rel) => words.All(w => rel.Contains(w, StringComparison.OrdinalIgnoreCase));
        var rows = new List<Row>();
        if (words.Length == 0)
            foreach (var r in Recent.Where(r => !Path.IsPathRooted(r) || Directory.Exists(r)))
                rows.Add(new Row(r, "Recently used:  " + Pretty(r), true));
        foreach (var rel in _all) if (Match(rel)) rows.Add(new Row(rel, Pretty(rel), false));
        _list.ItemsSource = rows;
        _count.Text = rows.Count == _all.Count ? $"{_all.Count:N0} folders" : $"{rows.Count(r => !r.IsRecent):N0} of {_all.Count:N0} folders";
        if (rows.Count > 0) _list.SelectedIndex = 0;
    }

    private static string Pretty(string rel) => Path.IsPathRooted(rel) ? rel : rel.Replace("\\", "  ›  ");

    private string? SelectedFull() => _list.SelectedItem is Row r ? (Path.IsPathRooted(r.Rel) ? r.Rel : Path.Combine(_root, r.Rel)) : null;

    private void Go()
    {
        var full = SelectedFull();
        if (full is null) return;
        Remember(full);
        Chosen = full;
        DialogResult = true;
    }

    private void Remember(string full)
    {
        string rel = full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? Path.GetRelativePath(_root, full) : full;
        Recent.RemoveAll(r => r.Equals(rel, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, rel);
        if (Recent.Count > 8) Recent.RemoveAt(Recent.Count - 1);
    }

    private void MakeFolder()
    {
        string parent = SelectedFull() ?? _root;
        string? name = Ask.Text(this, "New folder", $"A new folder inside {(parent == _root ? "the library (IMGs)" : Path.GetRelativePath(_root, parent))}:", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { MessageBox.Show(this, "A folder name can't have any of these: \\ / : * ? \" < > |", "Ginkgo"); return; }
        string full = Path.Combine(parent, name);
        try { Directory.CreateDirectory(full); }
        catch (Exception ex) { MessageBox.Show(this, "Couldn't make the folder: " + ex.Message, "Ginkgo"); return; }
        if (full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) && _all is not null)
        {
            string rel = Path.GetRelativePath(_root, full);
            if (!_all.Contains(rel, StringComparer.OrdinalIgnoreCase)) { _all.Add(rel); _all.Sort(StringComparer.OrdinalIgnoreCase); }
            _search.Text = name;
            Fill();
            for (int i = 0; i < _list.Items.Count; i++)
                if (_list.Items[i] is Row r && r.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)) { _list.SelectedIndex = i; _list.ScrollIntoView(r); break; }
        }
    }

    private void Outside()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose a folder", Multiselect = false };
        if (dlg.ShowDialog(this) == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            Remember(dlg.FolderName);
            Chosen = dlg.FolderName;
            DialogResult = true;
        }
    }

    /// <summary>The library's folder list is read again next time (after folders were made or moved).</summary>
    public static void Forget() => _all = null;
}

/// <summary>A small themed question with a text box.</summary>
public static class Ask
{
    public static string? Text(Window owner, string title, string prompt, string start)
    {
        var w = new Window
        {
            Title = title, Width = 440, SizeToContent = SizeToContent.Height, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize,
        };
        w.SetResourceReference(Window.BackgroundProperty, "WindowBg");
        w.SetResourceReference(Window.FontFamilyProperty, "AppFont");
        w.SetResourceReference(Window.ForegroundProperty, "Text");
        Theme.DarkTitleBar(w);
        var st = new StackPanel { Margin = new Thickness(16) };
        st.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var box = new TextBox { Text = start, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
        st.Children.Add(box);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, Height = 28, IsDefault = true };
        var no = new Button { Content = "Cancel", Width = 80, Height = 28, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        ok.Click += (_, _) => w.DialogResult = true;
        row.Children.Add(ok); row.Children.Add(no);
        st.Children.Add(row);
        w.Content = st;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return w.ShowDialog() == true ? box.Text : null;
    }
}
