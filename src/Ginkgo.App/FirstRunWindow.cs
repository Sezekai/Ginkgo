using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// The first time Ginkgo starts: the library folder, the folders to leave out, where the thumbnails are kept, and a theme. Each of
/// these can be changed later in Settings. Browsing starts straight away while the first look through the library runs.
/// </summary>
public sealed class FirstRunWindow : Window
{
    private readonly AppSettings _s;
    private readonly TextBox _lib = new() { MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBox _cache = new() { MinWidth = 380, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly WrapPanel _folders = new();
    private readonly HashSet<string> _excluded;

    public FirstRunWindow(AppSettings s)
    {
        _s = s;
        _excluded = new HashSet<string>(s.ExcludedFolders, StringComparer.OrdinalIgnoreCase);
        Title = "Welcome to Ginkgo"; Width = 720; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Bar");
        SetResourceReference(FontFamilyProperty, "AppFont");
        SetResourceReference(TextElement.ForegroundProperty, "Text");
        Theme.DarkTitleBar(this);
        var p = new StackPanel { Margin = new Thickness(22) };
        p.Children.Add(new TextBlock { Text = "Welcome to Ginkgo", FontSize = 22, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Note("Four things to choose; each can be changed later in Settings. Ginkgo only reads your library: it never changes, moves or deletes your files."));

        Head(p, "1. Your library folder");
        _lib.Text = s.LibraryRoot;
        p.Children.Add(PathRow(_lib, "Choose your library folder", () => FillFolders()));
        _lib.LostFocus += (_, _) => FillFolders();

        Head(p, "2. Folders to leave out");
        p.Children.Add(Note("Ticked folders are left out of the app entirely: not looked through, counted or given thumbnails. Your files are never touched."));
        p.Children.Add(new ScrollViewer { Content = _folders, MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        Head(p, "3. Where the thumbnails are kept");
        p.Children.Add(Note("They take a few GB for a large library. A folder on your external drive keeps them off the computer's own drive."));
        _cache.Text = s.CacheDir;
        p.Children.Add(PathRow(_cache, "Choose where the thumbnails go", null));

        Head(p, "4. A theme");
        var themes = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var t in ThemeKit.BuiltIn) themes.Items.Add(new ComboBoxItem { Content = t.Name + (t.Light ? "  (light)" : ""), Tag = t });
        themes.SelectedIndex = 0;
        themes.SelectionChanged += (_, _) => { if (themes.SelectedItem is ComboBoxItem { Tag: AppTheme t }) { _s.Theme = t.Copy(); ThemeKit.Apply(_s.Theme, _s.Look); } };
        p.Children.Add(themes);
        p.Children.Add(Note("More colours, fonts and backgrounds are in Appearance (side menu)."));

        var start = new Button { Content = "Start", IsDefault = true, MinWidth = 120, Height = 32, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        start.Click += (_, _) => Finish();
        p.Children.Add(start);
        Content = p;
        FillFolders();
    }

    private static TextBlock Note(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 2, 0, 8) };
    private static void Head(StackPanel p, string t) => p.Children.Add(new TextBlock { Text = t, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 4) });

    private FrameworkElement PathRow(TextBox box, string title, Action? after)
    {
        var d = new DockPanel();
        var b = new Button { Content = "Choose…", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        b.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(box.Text) ? box.Text : "" };
            if (dlg.ShowDialog(this) == true) { box.Text = dlg.FolderName; after?.Invoke(); }
        };
        DockPanel.SetDock(b, Dock.Right);
        d.Children.Add(b); d.Children.Add(box);
        return d;
    }

    private void FillFolders()
    {
        _folders.Children.Clear();
        string root = _lib.Text.Trim();
        string[] tops;
        try { tops = Directory.Exists(root) ? Directory.GetDirectories(root).Select(Path.GetFileName).Where(n => n is not null).Cast<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>(); }
        catch { tops = Array.Empty<string>(); }
        if (tops.Length == 0) { _folders.Children.Add(Note(Directory.Exists(root) ? "(no folders inside)" : "(the folder can't be found: is the drive plugged in?)")); return; }
        foreach (var name in tops)
        {
            var c = new CheckBox { Content = name, IsChecked = _excluded.Contains(name), Margin = new Thickness(0, 0, 14, 6), MinWidth = 150 };
            c.SetResourceReference(ForegroundProperty, "Text");
            string n = name;
            c.Click += (_, _) => { if (c.IsChecked == true) _excluded.Add(n); else _excluded.Remove(n); };
            _folders.Children.Add(c);
        }
    }

    private void Finish()
    {
        string lib = _lib.Text.Trim(), cache = _cache.Text.Trim();
        if (!Directory.Exists(lib) && MessageBox.Show(this, "That library folder can't be found right now. Use it anyway? (Ginkgo shows the library as offline until it is there.)", "Ginkgo", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        if (cache.Length == 0) { MessageBox.Show(this, "Choose a folder for the thumbnails.", "Ginkgo"); return; }
        _s.LibraryRoot = lib; _s.CacheDir = cache;
        _s.ExcludedFolders = _excluded.ToList();
        DialogResult = true;
    }
}
