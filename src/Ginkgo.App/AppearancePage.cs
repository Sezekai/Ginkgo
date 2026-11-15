using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// Appearance: everything about how Ginkgo looks, one page each (theme, buttons, logo, backgrounds, folder pop-outs, sets,
/// favourites and details, video outlines). Every change shows at once in the main window. Each page has its own saved looks:
/// save the current one under a new name, save changes into the saved look it came from, load, rename and delete them.
/// </summary>
public sealed class AppearancePage : Border
{
    private readonly AppSettings _s;
    private readonly Action _themeChanged, _looksChanged;
    private readonly ListBox _nav = new() { Width = 196, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _subtitle = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
    private readonly ComboBox _saved = new() { Width = 220, Height = 28, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock _savedNote = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), FontSize = 11.5 };
    private readonly Button _saveBtn = new(), _saveNewBtn = new(), _renameBtn = new(), _deleteBtn = new(), _revertBtn = new(), _defaultBtn = new();
    private readonly WrapPanel _body = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private Page _page = null!;
    private bool _filling;

    /// <summary>One page: its kind of look, how to read and write the look in use, its default, and how to build its form.</summary>
    private sealed class Page
    {
        public string Key = "", Name = "", Icon = "", Subtitle = "";
        public Func<object> Get = null!;
        public Action<object> Set = null!;
        public Func<object> Default = null!;
        public Type Type = typeof(object);
        public Action Build = null!;
        public bool Theme;
        /// <summary>The objects the page's controls change (to find each setting's default in place).</summary>
        public Func<object[]> Live = () => Array.Empty<object>();
    }

    private readonly List<Page> _pages = new();

    public AppearancePage(AppSettings s, Action themeChanged, Action looksChanged)
    {
        _s = s; _themeChanged = themeChanged; _looksChanged = looksChanged;
        SetResourceReference(TextElement.FontFamilyProperty, "AppFont");
        SetResourceReference(TextElement.ForegroundProperty, "Text");
        _nav.SetResourceReference(Control.ForegroundProperty, "Text");
        _subtitle.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        _savedNote.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        DefinePages();

        // the rail: one item per page
        foreach (var p in _pages)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 6) };
            // the icon centred by its shape in a fixed box, the name centred on the same line
            var ic = new IconGlyph { Glyph = p.Icon, Size = 15, Width = 30, Height = 24, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            ic.SetBinding(IconGlyph.ForegroundProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBoxItem), 1) });
            row.Children.Add(ic);
            row.Children.Add(new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center, FontSize = 13, Margin = new Thickness(0, 0, 0, 1) });
            _nav.Items.Add(new ListBoxItem { Content = row, Tag = p, Padding = new Thickness(6, 2, 6, 2) });
        }
        _nav.SelectionChanged += (_, _) => { if (_nav.SelectedItem is ListBoxItem { Tag: Page p }) Show(p); };
        var rail = new Border { Padding = new Thickness(10, 14, 10, 14), BorderThickness = new Thickness(0, 0, 1, 0) };
        rail.SetResourceReference(Border.BackgroundProperty, "Bar");
        rail.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var railStack = new DockPanel();
        var resetAll = new Button { Content = "Everything back to default…", Height = 28, Margin = new Thickness(0, 10, 0, 0), ToolTip = "Every page back to Ginkgo's own look (your saved looks stay)" };
        resetAll.Click += (_, _) => ResetEverything();
        DockPanel.SetDock(resetAll, Dock.Bottom);
        railStack.Children.Add(resetAll);
        railStack.Children.Add(_nav);
        rail.Child = railStack;

        // the page: title, the saved-looks bar, then the cards
        var top = new StackPanel { Margin = new Thickness(22, 16, 22, 10) };
        top.Children.Add(_title); top.Children.Add(_subtitle);
        var bar = new Border { Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
        bar.SetResourceReference(Border.BackgroundProperty, "Bar");
        bar.SetResourceReference(Border.BorderBrushProperty, "Edge");
        var barRow = new WrapPanel();
        var savedLabel = new TextBlock { Text = "Saved looks", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), FontWeight = FontWeights.SemiBold };
        barRow.Children.Add(savedLabel);
        barRow.Children.Add(_saved);
        void Btn(Button b, string text, string tip, Action act) { b.Content = text; b.ToolTip = tip; b.Height = 28; b.Margin = new Thickness(8, 0, 0, 0); b.Click += (_, _) => act(); barRow.Children.Add(b); }
        Btn(_saveBtn, "Save", "Save the changes into the saved look you are using", SaveInto);
        Btn(_saveNewBtn, "Save as new…", "Keep this look under a new name (the one you are using stays as it was)", SaveNew);
        Btn(_renameBtn, "Rename…", "Give the saved look another name", Rename);
        Btn(_deleteBtn, "Delete…", "Delete the saved look (the look in use stays)", Delete);
        Btn(_revertBtn, "Undo changes", "Back to the saved look as it was saved", Revert);
        Btn(_defaultBtn, "Default", "Ginkgo's own look for this page (your saved looks stay)", ToDefault);
        barRow.Children.Add(_savedNote);
        bar.Child = barRow;
        top.Children.Add(bar);
        _saved.SelectionChanged += (_, _) => { if (!_filling && _saved.SelectedItem is ComboBoxItem { Tag: string name }) LoadSaved(name); };

        _body.Margin = new Thickness(16, 0, 10, 24);
        _scroll.Content = _body;
        var right = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        right.Children.Add(top); right.Children.Add(_scroll);
        var root = new DockPanel();
        DockPanel.SetDock(rail, Dock.Left);
        root.Children.Add(rail); root.Children.Add(right);
        Child = root;
        SizeChanged += (_, _) => FitCards();
        _nav.SelectedIndex = 0;
    }

    // =====================================================================================================================
    // the pages
    // =====================================================================================================================

    private void DefinePages()
    {
        _pages.Add(new Page
        {
            Key = "Theme", Name = "Theme", Icon = "", Theme = true,
            Subtitle = "The colours of everything, the font, density, icons and the viewer's background. Built-in themes and your own.",
            Get = () => _s.Theme.Copy(), Set = o => _s.Theme = ((AppTheme)o).Copy(), Default = () => new AppTheme(), Type = typeof(AppTheme), Build = BuildTheme, Live = () => new object[] { _s.Theme },
        });
        _pages.Add(new Page
        {
            Key = "Buttons", Name = "Buttons", Icon = "",
            Subtitle = "The buttons and switches in the bars: shape, size, fills, outline, light, glow and shadow.",
            Get = () => _s.Look.Copy(), Set = o => CopyInto(_s.Look, (ButtonLook)o), Default = () => new ButtonLook(), Type = typeof(ButtonLook), Build = BuildButtons, Live = () => new object[] { _s.Look },
        });
        _pages.Add(new Page
        {
            Key = "Logo", Name = "Logo", Icon = "",
            Subtitle = "The logo in the top bar: fill, outline, glow, shadow and moving light. Mix any of them.",
            Get = () => _s.Logo.Copy(), Set = o => _s.Logo = ((LogoLook)o).Copy(), Default = () => new LogoLook(), Type = typeof(LogoLook), Build = BuildLogo, Live = () => new object[] { _s.Logo },
        });
        _pages.Add(new Page
        {
            Key = "Text", Name = "Text", Icon = "\uE8D2",
            Subtitle = "A font and size for each part of Ginkgo. A part left on Theme uses the theme's font (Theme page) at its usual size.",
            Get = () => _s.Fonts.Copy(), Set = o => _s.Fonts = ((FontsLook)o).Copy(), Default = () => new FontsLook(), Type = typeof(FontsLook), Build = BuildText, Live = () => new object[] { _s.Fonts },
        });
        _pages.Add(new Page
        {
            Key = "Top bar", Name = "Top bar", Icon = "\uE8A0",
            Subtitle = "Dial in the top bar: the logo's size and place (big, and small while the bar is hidden), the section switch, the section's name, and their colours.",
            Get = () => _s.TopBarStyle.Copy(), Set = o => _s.TopBarStyle = ((TopBarLook)o).Copy(), Default = () => new TopBarLook(), Type = typeof(TopBarLook), Build = BuildTopBar, Live = () => new object[] { _s.TopBarStyle },
        });
        _pages.Add(new Page
        {
            Key = "Background", Name = "Backgrounds", Icon = "",
            Subtitle = "Starfields, night skies, gradients and more: for the bars, behind the pictures and the viewer, together or each its own.",
            Get = () => _s.Scenes.Copy(), Set = o => _s.Scenes = ((SceneLook)o).Copy(), Default = () => new SceneLook(), Type = typeof(SceneLook), Build = BuildScenes, Live = () => new object[] { _s.Scenes },
        });
        _pages.Add(new Page
        {
            Key = "Folders", Name = "Folder pop-outs", Icon = "",
            Subtitle = "Select folders, NSFW and Very: the pop-out card and the folder switches inside it.",
            Get = () => _s.Folders.Copy(), Set = o => _s.Folders = ((FolderLook)o).Copy(), Default = () => new FolderLook(), Type = typeof(FolderLook), Build = BuildFolders, Live = () => new object[] { _s.Folders },
        });
        _pages.Add(new Page
        {
            Key = "Sets", Name = "Connected sets", Icon = "",
            Subtitle = "How the pictures of a Connected Art set are shown to belong together. Changes show in the main window as you make them.",
            Get = () => _s.SetStyle.Copy(), Set = o => _s.SetStyle = ((SetLook)o).Copy(), Default = () => new SetLook(), Type = typeof(SetLook), Build = BuildSets, Live = () => new object[] { _s.SetStyle },
        });
        _pages.Add(new Page
        {
            Key = "Favourites", Name = "Favourites and details", Icon = "",
            Subtitle = "How favourites are marked in the grid (not in Favourites itself), and where a picture's size and resolution show.",
            Get = () => _s.FavStyle.Copy(), Set = o => _s.FavStyle = ((FavLook)o).Copy(), Default = () => new FavLook(), Type = typeof(FavLook), Build = BuildFavs, Live = () => new object[] { _s.FavStyle },
        });
        _pages.Add(new Page
        {
            Key = "Video outlines", Name = "Video outlines", Icon = "",
            Subtitle = "The outlines round paused and watched videos in the grid.",
            Get = () => new VideoOutlineLook { Paused = _s.PausedOutline.Copy(), Watched = _s.WatchedOutline.Copy() },
            Set = o => { var v = (VideoOutlineLook)o; _s.PausedOutline = v.Paused.Copy(); _s.WatchedOutline = v.Watched.Copy(); },
            Default = () => new VideoOutlineLook(), Type = typeof(VideoOutlineLook), Build = BuildOutlines,
        });
        _pages.Add(new Page
        {
            Key = "Scroll bars", Name = "Scroll bars", Icon = "\uE8CB",
            Subtitle = "The pictures' scroll bar: when it shows, how long and wide it is, and a glow or shine so it is easy to find.",
            Get = () => _s.ScrollBars.Copy(), Set = o => _s.ScrollBars = ((ScrollLook)o).Copy(), Default = () => new ScrollLook(), Type = typeof(ScrollLook), Build = BuildScrollBars, Live = () => new object[] { _s.ScrollBars },
        });
    }

    private void BuildText()
    {
        foreach (var (key, name, usual, min, max, note) in FontsLook.All)
        {
            var card = Card(name, note.Length > 0 ? note : null);
            var pick = _s.Fonts.Get(key);
            var box = new ComboBox { Width = 190, Height = 28 };
            box.Items.Add(new ComboBoxItem { Content = "Theme (" + _s.Theme.Font + ")", Tag = "" });
            foreach (var f in ThemeKit.Fonts) box.Items.Add(new ComboBoxItem { Content = f, FontFamily = ThemeKit.Family(f), Tag = f });
            foreach (ComboBoxItem it in box.Items) if ((string)it.Tag == pick.Font) box.SelectedItem = it;
            if (box.SelectedItem is null) box.SelectedIndex = 0;
            box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string f }) { pick.Font = f; Changed(); } };
            var fontRow = Row(card, "Font", box);
            AddReset(card, fontRow, () => pick.Font, v => pick.Font = v);
            // the section name and the window name keep their size with the top bar's layout
            if (key == "Section") Slide(card, "Size", min, max, () => _s.TopBarStyle.TitleSize, v => _s.TopBarStyle.TitleSize = v, v => $"{v:0.#} px", 0.5);
            else if (key == "Window") Slide(card, "Size", min, max, () => _s.TopBarStyle.NameSize, v => _s.TopBarStyle.NameSize = v, v => $"{v:0.#} px", 0.5);
            else Slide(card, "Size", min - 0.5, max, () => pick.Size <= 0 ? min - 0.5 : pick.Size, v => pick.Size = v < min ? 0 : v,
                       v => v < min ? $"usual ({(usual > 0 ? usual : _s.Theme.FontSize):0.#})" : $"{v:0.#} px", 0.5);
        }
    }

    /// <summary>Set by the main window: shows the small logo for a moment, as when the top bar hides.</summary>
    public static Action? PreviewSmallLogo;

    private void BuildTopBar()
    {
        var t = _s.TopBarStyle;
        static string Px(double v) => $"{v:0} px";
        static string Auto(double v) => v <= 0 ? "auto" : $"{v:0} px";

        var big = Card("Logo", "Changes show in the bar above as you make them. The big logo covers the title bar and the top bar (turn that off in Appearance → Logo).");
        Slide(big, "Size", 0, 160, () => t.LogoSize, v => t.LogoSize = v, Auto, 1);
        Slide(big, "From the left", 0, 80, () => t.LogoX, v => t.LogoX = v, Px, 1);
        Slide(big, "From the top", -10, 40, () => t.LogoY, v => t.LogoY = v, Px, 1);
        Slide(big, "Space before the switch", 0, 60, () => t.SwitchGap, v => t.SwitchGap = v, Px, 1);

        var small = Card("Logo while the bar is hidden", "When you scroll down and the top bar slides away, the logo shrinks into the title bar's top-left corner and the window's name slides along beside it.");
        Slide(small, "Size", 0, 40, () => t.SmallSize, v => t.SmallSize = v, v => v <= 0 ? "auto" : $"{v:0} px", 1);
        Slide(small, "From the left", 0, 60, () => t.SmallX, v => t.SmallX = v, Px, 1);
        Slide(small, "Up or down", -10, 10, () => t.SmallY, v => t.SmallY = v, Px, 1);
        Slide(small, "Animation", 60, 1000, () => t.Speed, v => t.Speed = v, v => $"{v:0} ms", 20);
        small.Children.Add(Small("Try it", () => PreviewSmallLogo?.Invoke(), "The logo goes small for two seconds, as when the bar hides"));

        var sw = Card("Section switch");
        Slide(sw, "Height", 24, 72, () => t.SwitchHeight, v => t.SwitchHeight = v, Px, 1);
        Slide(sw, "Icon size", 0.5, 1.6, () => t.IconScale, v => t.IconScale = v, v => $"{v * 100:0}%", 0.05);
        Slide(sw, "Up or down", -12, 12, () => t.SwitchY, v => t.SwitchY = v, Px, 1);
        Colour(sw, "Icons", () => t.IconColour, v => t.IconColour = v, allowBlank: true);
        Colour(sw, "Icon under the mouse", () => t.IconHoverColour, v => t.IconHoverColour = v, allowBlank: true);
        Colour(sw, "Icon of the open section", () => t.IconOnColour, v => t.IconOnColour = v, allowBlank: true);

        var title = Card("Section name");
        Slide(title, "Text size", 10, 48, () => t.TitleSize, v => t.TitleSize = v, Px, 1);
        Slide(title, "Space before it", 0, 60, () => t.TitleGap, v => t.TitleGap = v, Px, 1);
        Slide(title, "Up or down", -12, 12, () => t.TitleY, v => t.TitleY = v, Px, 1);
        Check(title, "Bold", () => t.TitleBold, v => t.TitleBold = v);
        Colour(title, "Colour", () => t.TitleColour, v => t.TitleColour = v, allowBlank: true);

        var name = Card("Window name (\"Ginkgo\" in the title bar)", "Its own place with the whole top bar showing, and with only the title bar showing (after scrolling down).");
        Slide(name, "Text size", 8, 24, () => t.NameSize, v => t.NameSize = v, Px, 1);
        Slide(name, "Space after the logo", 0, 40, () => t.NameGap, v => t.NameGap = v, Px, 1);
        Slide(name, "Whole bar: left or right", -200, 600, () => t.NameX, v => t.NameX = v, Px, 1);
        Slide(name, "Whole bar: up or down", -14, 14, () => t.NameY, v => t.NameY = v, Px, 1);
        Slide(name, "Title bar only: left or right", -100, 600, () => t.SmallNameX, v => t.SmallNameX = v, Px, 1);
        Slide(name, "Title bar only: up or down", -14, 14, () => t.SmallNameY, v => t.SmallNameY = v, Px, 1);
        Colour(name, "Colour", () => t.NameColour, v => t.NameColour = v, allowBlank: true);

        var bar = Card("Title bar");
        Slide(bar, "Height", 20, 64, () => t.TitleHeight, v => t.TitleHeight = v, Px, 1);
        Choice(bar, "Line under it", new[] { ("Line", "A line"), ("None", "No line") }, () => t.Separator, v => t.Separator = v, Rebuild);
        if (t.Separator != "None")
        {
            Slide(bar, "Line starts after the logo", -100, 400, () => t.LineGap, v => t.LineGap = v, Px, 1);
            Colour(bar, "Line colour", () => t.LineColour, v => t.LineColour = v, allowBlank: true, blankText: "Edge");
        }

        var win = Card("Window buttons (minimise, maximise, close)");
        Check(win, "Outline round them", () => t.CaptionOutline, v => t.CaptionOutline = v, after: Rebuild);
        if (t.CaptionOutline)
        {
            Colour(win, "Outline colour", () => t.CaptionOutlineColour, v => t.CaptionOutlineColour = v, allowBlank: true, blankText: "Edge");
            Slide(win, "Outline width", 0.5, 4, () => t.CaptionOutlineWidth, v => t.CaptionOutlineWidth = v, v => $"{v:0.0} px", 0.5);
            Slide(win, "Rounded corners", 0, 20, () => t.CaptionRadius, v => t.CaptionRadius = v, Px, 1);
        }
        Slide(win, "Width (each)", 24, 100, () => t.CaptionWidth, v => t.CaptionWidth = v, Px, 1);
        Slide(win, "Height", 0, 64, () => t.CaptionHeight, v => t.CaptionHeight = v, v => v <= 0 ? "full height" : $"{v:0} px", 1);
        Slide(win, "Icon size", 6, 20, () => t.CaptionIcon, v => t.CaptionIcon = v, Px, 0.5);
        Slide(win, "Space from the corner", 0, 20, () => t.CaptionInset, v => t.CaptionInset = v, Px, 1);

        var ctl = Card("Controls under the title bar", "The section switch, its name and the buttons after them.");
        Slide(ctl, "Up or down", -80, 80, () => t.ControlsY, v => t.ControlsY = v, Px, 1);
        Slide(ctl, "Space above", 0, 60, () => t.SpaceAbove, v => t.SpaceAbove = v, Px, 1);
        Slide(ctl, "Space below", 0, 60, () => t.SpaceBelow, v => t.SpaceBelow = v, Px, 1);
    }

    private void BuildScrollBars()
    {
        var l = _s.ScrollBars;
        var main = Card("The pictures' scroll bar", "It stays out of sight until the mouse comes near the right edge of the window, then fades in. Drag it to jump through the pictures.");
        Slide(main, "Shows when the mouse is within", 0.02, 0.2, () => l.Reach, v => l.Reach = v, v => $"{v * 100:0}% of the edge", 0.01);
        Slide(main, "Width", 8, 28, () => l.Width, v => l.Width = v, v => $"{v:0} px", 1);
        Slide(main, "Length", 0.5, 3, () => l.Length, v => l.Length = v, v => $"{v:0.##}x (longer with bigger pictures)", 0.25);
        Slide(main, "Outline", 0.5, 4, () => l.OutlineWidth, v => l.OutlineWidth = v, v => $"{v:0.0} px", 0.5);
        Slide(main, "Filled in", 0, 0.6, () => l.Fill, v => l.Fill = v, v => v < 0.01 ? "just the outline" : $"{v * 100:0}%", 0.02);

        var fx = Card("Easy to find");
        Choice(fx, "Effect", new[] { ("None", "None"), ("Glow", "Glow"), ("Pulse", "Pulsing glow"), ("Shine", "A shine runs along it"), ("PulseShine", "Pulse and shine") }, () => l.Effect, v => l.Effect = v);
        Colour(fx, "Colour", () => l.Colour, v => l.Colour = v, allowBlank: true, blankText: "Accent");

        var all = Card("Every other scroll bar", wide: true);
        all.Children.Add(new TextBlock { Text = "Scroll bars in pages and pop-outs are see-through, reach the edge of what they are in, and have an outline in the colour of its edge.", TextWrapping = TextWrapping.Wrap });
    }

    private static void CopyInto(ButtonLook to, ButtonLook from) { foreach (var p in typeof(ButtonLook).GetProperties()) if (p.CanWrite) p.SetValue(to, p.GetValue(from)); }

    /// <summary>A page was opened (its key), so the section can open at it again next time.</summary>
    public event Action<string>? PageShown;

    private void Show(Page p)
    {
        _page = p;
        PageShown?.Invoke(p.Key);
        _title.Text = p.Name; _subtitle.Text = p.Subtitle;
        FillSaved();
        Rebuild();
    }

    private void Rebuild()
    {
        _body.Children.Clear();
        _cardResets.Clear();
        _pending.Clear();
        _marks.Clear();
        _page.Build();
        ResolveDefaults();
        // a card's own reset shows only when it has something to put back
        foreach (var (card, (btn, list)) in _cardResets) btn.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FitCards();
        UpdateSavedState();
    }

    // ---- going back to the default: for one setting, or for one card ----

    private readonly Dictionary<StackPanel, (Button btn, List<Func<bool>> resets)> _cardResets = new();
    /// <summary>Brightens each reset button whose setting is away from its default (after every change).</summary>
    private readonly List<Action> _marks = new();
    /// <summary>The settings on the page, waiting for their defaults: found after the page is built, all at once.</summary>
    private readonly List<(StackPanel card, DockPanel? row, Func<object?> get, Action<object?> attach)> _pending = new();

    /// <summary>A setting's reset (beside it, and in its card's reset). Its default is read after the page is built.</summary>
    private void AddReset<T>(StackPanel card, DockPanel? row, Func<T> get, Action<T> set, Func<T, T, bool>? same = null)
    {
        same ??= (a, b) => EqualityComparer<T>.Default.Equals(a, b);
        _pending.Add((card, row, () => get(), d =>
        {
            if (d is not T def) return;
            bool AtDefault() => same(get(), def);
            if (row is not null)
            {
                var b = new Button { Content = "\uE7A7", Style = (Style)FindResource("IconButton"), Width = 24, Height = 24, FontSize = 11, Margin = new Thickness(6, 0, 0, 0),
                                     VerticalAlignment = VerticalAlignment.Center, ToolTip = $"Back to the default ({ShowValue(def)})" };
                void Mark() => b.Opacity = AtDefault() ? 0.3 : 1;
                Mark();
                b.Click += (_, _) => { if (AtDefault()) return; set(def); Changed(); Rebuild(); };
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Insert(Math.Min(1, row.Children.Count), b);
                if (row.Children.Count == 2 && row.Children[1] == b) { var first = row.Children[0]; row.Children.RemoveAt(0); row.Children.Add(first); }
                _marks.Add(Mark);
            }
            if (_cardResets.TryGetValue(card, out var cr)) cr.resets.Add(() => { if (AtDefault()) return false; set(def); return true; });
        }));
    }

    /// <summary>Every look is put to its default in place for a moment, each setting's default is read, and the looks are put back.
    /// (In place, so the controls' own objects are the ones read.)</summary>
    private void ResolveDefaults()
    {
        if (_pending.Count == 0) return;
        var swaps = new List<(object live, object saved)>();
        try
        {
            foreach (var p in _pages)
            {
                var live = p.Live();
                if (live.Length != 1) continue;
                var saved = JsonCopy(live[0]);
                InPlace(live[0], p.Default());
                swaps.Add((live[0], saved));
            }
            var defs = _pending.Select(x => { try { return x.get(); } catch { return null; } }).ToList();
            foreach (var (live, saved) in swaps) InPlace(live, saved);
            swaps.Clear();
            for (int i = 0; i < _pending.Count; i++) _pending[i].attach(defs[i]);
        }
        finally
        {
            foreach (var (live, saved) in swaps) InPlace(live, saved);
            _pending.Clear();
        }
    }

    private static object JsonCopy(object o) => System.Text.Json.JsonSerializer.Deserialize(System.Text.Json.JsonSerializer.Serialize(o, o.GetType()), o.GetType())!;

    /// <summary>Copies one look into another of the same kind, keeping the objects inside it (and the dictionary entries) the same.</summary>
    private static void InPlace(object target, object source)
    {
        foreach (var pr in target.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!pr.CanRead || pr.GetIndexParameters().Length > 0) continue;
            var t = pr.PropertyType;
            object? sv = pr.GetValue(source), tv = pr.GetValue(target);
            if (t.IsValueType || t == typeof(string) || sv is null || tv is null)
            {
                if (pr.CanWrite) pr.SetValue(target, sv is null || t.IsValueType || t == typeof(string) ? sv : JsonCopy(sv));
            }
            else if (tv is System.Collections.IDictionary td && sv is System.Collections.IDictionary sd)
            {
                var valueType = t.IsGenericType ? t.GetGenericArguments()[1] : typeof(object);
                foreach (var k in td.Keys.Cast<object>().ToList())
                {
                    var src = sd.Contains(k) ? sd[k] : Activator.CreateInstance(valueType);
                    if (td[k] is { } tvk && src is not null && !valueType.IsValueType && valueType != typeof(string)) InPlace(tvk, src); else td[k] = src;
                }
                foreach (var k in sd.Keys.Cast<object>()) if (!td.Contains(k)) td[k] = sd[k] is { } x && !valueType.IsValueType && valueType != typeof(string) ? JsonCopy(x) : sd[k];
            }
            else if (tv is System.Collections.IEnumerable)
            {
                if (pr.CanWrite) pr.SetValue(target, JsonCopy(sv));
            }
            else InPlace(tv, sv);
        }
    }

    private static string ShowValue(object? v) => v switch
    {
        null => "none", string s when s.Length == 0 => "theme", bool b => b ? "on" : "off", double x => x.ToString("0.##"), _ => v.ToString() ?? "",
    };

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    /// <summary>A whole card's reset that isn't made of single settings (Video outlines).</summary>
    private void CardReset(StackPanel card, Func<bool> reset)
    {
        if (_cardResets.TryGetValue(card, out var cr)) cr.resets.Add(reset);
    }

    /// <summary>Two columns of cards when there is room, one when not.</summary>
    private void FitCards()
    {
        // the room inside the scroll area (less the page's margins and the scroll bar); each card has 14 px on its right
        double avail = Math.Max(300, _scroll.ActualWidth - 26 - 14 - SystemParameters.VerticalScrollBarWidth);
        double w = avail >= 760 ? Math.Floor((avail - 28) / 2) : avail - 14;
        foreach (FrameworkElement c in _body.Children) c.Width = c.Tag as string == "wide" ? avail - 14 : w;
    }

    /// <summary>Something on the page changed: into effect at once.</summary>
    private void Changed()
    {
        foreach (var m in _marks) m();
        if (_page.Theme) { ThemeKit.Apply(_s.Theme, _s.Look); _themeChanged(); }
        else if (_page.Key == "Buttons") { ThemeKit.Apply(_s.Theme, _s.Look); _themeChanged(); }
        else _looksChanged();
        if (_page.Key == "Logo" && _logoBig is not null)
        {
            var big = _s.Logo.Copy(); big.Size = 96;
            _logoBig.Apply(big); _logoSmall?.Apply(_s.Logo);
        }
        if (_page.Key == "Folders") FillFolderDemo();
        UpdateSavedState();
    }

    /// <summary>Opens a page by its key (Theme, Buttons, Logo, Background, Folders, Sets, Favourites, Video outlines).</summary>
    public void ShowPage(string key)
    {
        foreach (ListBoxItem it in _nav.Items) if (it.Tag is Page p && p.Key == key) { _nav.SelectedItem = it; return; }
    }

    // ---- saved looks ----

    private string CurrentName { get => _s.LookNames.TryGetValue(_page.Key, out var n) ? n : ""; set => _s.LookNames[_page.Key] = value; }

    private List<string> SavedNames()
    {
        if (_page.Theme) return ThemeKit.BuiltIn.Select(t => t.Name).Concat(ThemeKit.LoadOwn().Select(t => t.Name)).ToList();
        return LookLibrary.Names(_page.Key);
    }

    private bool IsBuiltInTheme(string name) => _page.Theme && ThemeKit.BuiltIn.Any(t => t.Name == name);

    private void FillSaved()
    {
        _filling = true;
        _saved.Items.Clear();
        if (_page.Theme && string.IsNullOrEmpty(CurrentName)) CurrentName = _s.Theme.Name;
        var names = SavedNames();
        if (_page.Theme)
        {
            _saved.Items.Add(new ComboBoxItem { Content = "Built in", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var t in ThemeKit.BuiltIn) _saved.Items.Add(new ComboBoxItem { Content = "   " + t.Name, Tag = t.Name });
            var own = ThemeKit.LoadOwn();
            if (own.Count > 0)
            {
                _saved.Items.Add(new ComboBoxItem { Content = "Yours", IsEnabled = false, FontWeight = FontWeights.SemiBold });
                foreach (var t in own) _saved.Items.Add(new ComboBoxItem { Content = "   " + t.Name, Tag = t.Name });
            }
        }
        else
        {
            _saved.Items.Add(new ComboBoxItem { Content = names.Count == 0 ? "(none saved yet)" : "(not a saved look)", Tag = null, IsEnabled = false });
            foreach (var n in names) _saved.Items.Add(new ComboBoxItem { Content = n, Tag = n });
        }
        _saved.SelectedItem = _saved.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == CurrentName) ?? _saved.Items[0];
        _filling = false;
        UpdateSavedState();
    }

    private object? LoadNamed(string name)
    {
        if (_page.Theme) return ThemeKit.BuiltIn.FirstOrDefault(t => t.Name == name)?.Copy() ?? ThemeKit.LoadOwn().FirstOrDefault(t => t.Name == name);
        var m = typeof(LookLibrary).GetMethod(nameof(LookLibrary.Load))!.MakeGenericMethod(_page.Type);
        return m.Invoke(null, new object[] { _page.Key, name });
    }

    private bool SameAsSaved()
    {
        string name = CurrentName;
        if (name.Length == 0) return false;
        var saved = LoadNamed(name);
        if (saved is null) return false;
        return JsonSerializer.Serialize(saved, _page.Type) == JsonSerializer.Serialize(_page.Get(), _page.Type);
    }

    private void UpdateSavedState()
    {
        if (_page is null) return;
        string name = CurrentName;
        bool has = name.Length > 0 && (_page.Theme ? SavedNames().Contains(name) : LookLibrary.Has(_page.Key, name));
        bool builtIn = IsBuiltInTheme(name);
        bool same = has && SameAsSaved();
        _saveBtn.IsEnabled = has && !builtIn && !same;
        _saveBtn.Content = has && !builtIn ? $"Save into “{name}”" : "Save";
        _renameBtn.IsEnabled = _deleteBtn.IsEnabled = has && !builtIn;
        _revertBtn.IsEnabled = has && !same;
        _savedNote.Text = !has ? "Changes are kept as your current look. Save as new to keep it under a name."
            : same ? "In use, as saved." : builtIn ? "Changed from the built-in theme: Save as new to keep it." : "Changed since it was saved.";
    }

    private void LoadSaved(string name)
    {
        var look = LoadNamed(name);
        if (look is null) return;
        _page.Set(look);
        CurrentName = name;
        Changed();
        Rebuild();
    }

    private void SaveInto()
    {
        string name = CurrentName;
        if (name.Length == 0 || IsBuiltInTheme(name)) { SaveNew(); return; }
        Store(name);
        UpdateSavedState();
    }

    private void Store(string name)
    {
        if (_page.Theme) { _s.Theme.Name = name; ThemeKit.SaveOwn(_s.Theme); return; }
        var m = typeof(LookLibrary).GetMethod(nameof(LookLibrary.Save))!.MakeGenericMethod(_page.Type);
        m.Invoke(null, new object[] { _page.Key, name, _page.Get() });
    }

    private void SaveNew()
    {
        string start = CurrentName.Length > 0 ? CurrentName + " (2)" : _page.Name + " look";
        if (Ask("Save as a new look", "Name:", start) is not { } name) return;
        if (IsBuiltInTheme(name)) { Message("That name belongs to a built-in theme. Choose another."); return; }
        if (SavedNames().Contains(name) && !Confirm($"There is already a look called “{name}”. Replace it?")) return;
        Store(name);
        CurrentName = name;
        if (_page.Theme) { ThemeKit.Apply(_s.Theme, _s.Look); _themeChanged(); }
        else _looksChanged();
        FillSaved();
    }

    private void Rename()
    {
        string old = CurrentName;
        if (old.Length == 0 || IsBuiltInTheme(old)) return;
        if (Ask("Rename the look", "New name:", old) is not { } name || name == old) return;
        if (IsBuiltInTheme(name) || SavedNames().Contains(name)) { Message("There is already a look with that name."); return; }
        if (_page.Theme)
        {
            var t = ThemeKit.LoadOwn().FirstOrDefault(x => x.Name == old);
            ThemeKit.DeleteOwn(old);
            if (t is not null) { t.Name = name; ThemeKit.SaveOwn(t); }
            _s.Theme.Name = name;
        }
        else LookLibrary.Rename(_page.Key, old, name);
        CurrentName = name;
        Changed();
        FillSaved();
    }

    private void Delete()
    {
        string name = CurrentName;
        if (name.Length == 0 || IsBuiltInTheme(name)) return;
        if (!Confirm($"Delete the saved look “{name}”? The look in use stays as it is.")) return;
        if (_page.Theme) ThemeKit.DeleteOwn(name); else LookLibrary.Delete(_page.Key, name);
        CurrentName = "";
        FillSaved();
    }

    private void Revert()
    {
        if (CurrentName.Length > 0) LoadSaved(CurrentName);
    }

    private void ToDefault()
    {
        _page.Set(_page.Default());
        CurrentName = _page.Theme ? ThemeKit.BuiltIn[0].Name : "";
        Changed();
        FillSaved();
        Rebuild();
    }

    private void ResetEverything()
    {
        if (!Confirm("Every page back to Ginkgo's own look? Your saved looks and themes stay, so you can load them again.")) return;
        var keep = _page;
        foreach (var p in _pages) { p.Set(p.Default()); _s.LookNames[p.Key] = p.Theme ? ThemeKit.BuiltIn[0].Name : ""; }
        ThemeKit.Apply(_s.Theme, _s.Look); _themeChanged(); _looksChanged();
        Show(keep);
    }

    // =====================================================================================================================
    // building blocks: cards and rows
    // =====================================================================================================================

    private StackPanel Card(string title, string? note = null, bool wide = false)
    {
        var st = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, note is null ? 10 : 2) };
        var t = new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        // the card's own reset: everything in this card back to its default, the rest of the page as it is
        var reset = new Button { Content = "Reset", Height = 24, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(8, 0, 0, 0), FontSize = 11,
                                 VerticalAlignment = VerticalAlignment.Center, ToolTip = "Put everything in this card back to its default (the rest of the page stays as it is)" };
        var list = new List<Func<bool>>();
        reset.Click += (_, _) =>
        {
            bool any = false;
            foreach (var r in list.ToList()) any |= r();
            if (any) { Changed(); Rebuild(); }
        };
        _cardResets[st] = (reset, list);
        DockPanel.SetDock(reset, Dock.Right);
        head.Children.Add(reset);
        head.Children.Add(t);
        st.Children.Add(head);
        if (note is not null)
        {
            var n = new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, FontSize = 11.5, Margin = new Thickness(0, 0, 0, 10) };
            n.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            st.Children.Add(n);
        }
        var card = new Border { Child = st, Padding = new Thickness(16, 14, 16, 14), Margin = new Thickness(0, 0, 14, 14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Tag = wide ? "wide" : null };
        card.SetResourceReference(Border.BackgroundProperty, "Bar");
        card.SetResourceReference(Border.BorderBrushProperty, "Edge");
        _body.Children.Add(card);
        return st;
    }

    private static DockPanel Row(StackPanel card, string label, UIElement control, string? tip = null)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 8), ToolTip = tip };
        var l = new TextBlock { Text = label, Width = 150, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(l, Dock.Left);
        row.Children.Add(l);
        row.Children.Add(control);
        if (control is FrameworkElement fe && fe.HorizontalAlignment == HorizontalAlignment.Stretch && control is not Slider && control is not DockPanel) fe.HorizontalAlignment = HorizontalAlignment.Left;
        card.Children.Add(row);
        return row;
    }

    private void Colour(StackPanel card, string label, Func<string> get, Action<string> set, bool allowBlank = false, string blankText = "Theme")
    {
        if (!allowBlank)
        {
            var r0 = Row(card, label, new ColourButton(get().Length > 0 ? get() : "#808080", v => { set(v); Changed(); }));
            AddReset(card, r0, get, set, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
            return;
        }
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var useTheme = new ToggleButton { Content = blankText, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(0, 0, 8, 0), IsChecked = get().Length == 0, ToolTip = "Follow the theme" };
        var pick = new ColourButton(get().Length > 0 ? get() : "#8A5CF5", v => { set(v); useTheme.IsChecked = false; Changed(); });
        useTheme.Click += (_, _) => { if (useTheme.IsChecked == true) set(""); else set("#8A5CF5"); Changed(); };
        sp.Children.Add(useTheme); sp.Children.Add(pick);
        var row = Row(card, label, sp);
        AddReset(card, row, get, set, (a, b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase));
    }

    private void Slide(StackPanel card, string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> show, double step = 0)
    {
        var dp = new DockPanel();
        var value = new TextBlock { Width = 58, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(get(), min, max), VerticalAlignment = VerticalAlignment.Center, MinWidth = 120 };
        if (step > 0) { s.TickFrequency = step; s.IsSnapToTickEnabled = true; }
        value.Text = show(s.Value);
        s.ValueChanged += (_, e) => { set(e.NewValue); value.Text = show(e.NewValue); Changed(); };
        DockPanel.SetDock(value, Dock.Right);
        dp.Children.Add(value); dp.Children.Add(s);
        var row = Row(card, label, dp);
        AddReset(card, row, get, set, Near);
    }

    private void Choice(StackPanel card, string label, (string key, string name)[] options, Func<string> get, Action<string> set, Action? after = null)
    {
        var wrap = new WrapPanel();
        var btns = new List<(ToggleButton b, string k)>();
        foreach (var (key, name) in options)
        {
            var b = new ToggleButton { Content = name, Height = 26, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 4, 4), IsChecked = get() == key };
            string k = key;
            b.Click += (_, _) => { set(k); foreach (var (bb, kk) in btns) bb.IsChecked = kk == k; Changed(); after?.Invoke(); };
            btns.Add((b, key)); wrap.Children.Add(b);
        }
        var row = Row(card, label, wrap);
        AddReset(card, row, get, set);
    }

    private void Check(StackPanel card, string text, Func<bool> get, Action<bool> set, string? tip = null, Action? after = null)
    {
        var c = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 2, 0, 8), ToolTip = tip };
        c.SetResourceReference(Control.ForegroundProperty, "Text");
        c.Click += (_, _) => { set(c.IsChecked == true); Changed(); after?.Invoke(); };
        var row = new DockPanel();
        row.Children.Add(c);
        card.Children.Add(row);
        AddReset(card, row, get, set);
    }

    private static Button Small(string text, Action act, string? tip = null)
    {
        var b = new Button { Content = text, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 6), ToolTip = tip };
        b.Click += (_, _) => act();
        return b;
    }

    // =====================================================================================================================
    // Theme
    // =====================================================================================================================

    private void BuildTheme()
    {
        var t = _s.Theme;
        var pick = Card("Themes", "Click one to use it. Changes to a built-in theme are kept as your current look; Save as new keeps them as a theme of your own.", wide: true);
        var wrap = new WrapPanel();
        foreach (var th in ThemeKit.BuiltIn.Concat(ThemeKit.LoadOwn()))
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var c in new[] { th.Window, th.Surface, th.Accent })
                sp.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(ButtonLook.C(c)), Margin = new Thickness(0, 0, 3, 0), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(0.5) });
            sp.Children.Add(new TextBlock { Text = th.Name, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var b = new ToggleButton { Content = sp, Height = 30, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 6), IsChecked = th.Name == t.Name };
            var chosen = th;
            b.Click += (_, _) => { _s.Theme = chosen.Copy(); CurrentName = chosen.Name; Changed(); FillSaved(); Rebuild(); };
            wrap.Children.Add(b);
        }
        pick.Children.Add(wrap);

        var colours = Card("Colours");
        Colour(colours, "Behind the pictures", () => t.Window, v => t.Window = v);
        Colour(colours, "Bars and panels", () => t.Surface, v => t.Surface = v);
        Colour(colours, "Boxes inside panels", () => t.Inner, v => t.Inner = v);
        Colour(colours, "Fields", () => t.Field, v => t.Field = v);
        Colour(colours, "Edges", () => t.Edge, v => t.Edge = v);
        Colour(colours, "Text", () => t.Text, v => t.Text = v);
        Colour(colours, "Dim text", () => t.Dim, v => t.Dim = v);
        Colour(colours, "Accent", () => t.Accent, v => t.Accent = v);

        var text = Card("Text, density and icons");
        var font = new ComboBox { Width = 190, Height = 28 };
        foreach (var f in ThemeKit.Fonts) font.Items.Add(new ComboBoxItem { Content = f, FontFamily = ThemeKit.Family(f), Tag = f });
        foreach (ComboBoxItem it in font.Items) if ((string)it.Tag == t.Font) font.SelectedItem = it;
        font.SelectionChanged += (_, _) => { if (font.SelectedItem is ComboBoxItem { Tag: string f }) { t.Font = f; Changed(); } };
        var themeFontRow = Row(text, "Font", font);
        AddReset(text, themeFontRow, () => t.Font, v => t.Font = v);
        Slide(text, "Text size", 10, 16, () => t.FontSize, v => t.FontSize = v, v => $"{v:0.#}", 0.5);
        Slide(text, "Density", 0.8, 1.3, () => t.Density, v => t.Density = v, v => v < 0.95 ? "compact" : v > 1.05 ? "roomy" : "normal", 0.05);
        Choice(text, "Icons", new[] { ("Fluent", "Fluent (Windows 11)"), ("Classic", "Classic (Windows 10)") }, () => t.Icons, v => t.Icons = v);
        Check(text, "Light theme (the buttons take the colours below)", () => t.Light, v => t.Light = v);
        Colour(text, "Buttons (top)", () => t.ButtonA, v => t.ButtonA = v);
        Colour(text, "Buttons (bottom)", () => t.ButtonB, v => t.ButtonB = v);
        Colour(text, "Button text", () => t.ButtonText, v => t.ButtonText = v);

        var viewer = Card("Viewer background", "Used when Backgrounds leaves the viewer on the theme.");
        Choice(viewer, "Kind", new[] { ("Solid", "Colour"), ("Gradient", "Gradient"), ("Pattern", "Gradient and pattern") }, () => t.Backdrop, v => t.Backdrop = v);
        Colour(viewer, "Colour (gradient start)", () => t.BackA, v => t.BackA = v);
        Colour(viewer, "Gradient end", () => t.BackB, v => t.BackB = v);
        Slide(viewer, "Gradient angle", 0, 360, () => t.BackAngle, v => t.BackAngle = v, v => $"{v:0}°", 15);
        Choice(viewer, "Pattern", new[] { ("Dots", "Dots"), ("Grid", "Grid"), ("Diagonal", "Lines"), ("Checks", "Checks") }, () => t.Pattern, v => t.Pattern = v);
        Colour(viewer, "Pattern colour", () => t.PatternColour, v => t.PatternColour = v);
        Slide(viewer, "Pattern strength", 0, 0.4, () => t.PatternOpacity, v => t.PatternOpacity = v, v => $"{v * 100:0}%", 0.01);
        Slide(viewer, "Pattern size", 6, 80, () => t.PatternSize, v => t.PatternSize = v, v => $"{v:0} px", 1);

        var files = Card("Theme files");
        var row = new WrapPanel();
        row.Children.Add(Small("Export…", () =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "Export theme", FileName = t.Name + ".ginkgo-theme.json", Filter = "Ginkgo theme|*.json" };
            if (dlg.ShowDialog(Host) == true) { try { ThemeKit.Export(t, dlg.FileName); } catch (Exception ex) { Message(ex.Message); } }
        }, "Save the theme as a file to share or keep"));
        row.Children.Add(Small("Import…", () =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Import theme", Filter = "Ginkgo theme|*.json|All files|*.*" };
            if (dlg.ShowDialog(Host) != true) return;
            if (ThemeKit.Import(dlg.FileName) is not { } th) { Message("That file isn't a Ginkgo theme."); return; }
            if (ThemeKit.BuiltIn.Any(b => b.Name == th.Name)) th.Name += " (imported)";
            ThemeKit.SaveOwn(th); _s.Theme = th; CurrentName = th.Name; Changed(); FillSaved(); Rebuild();
        }, "Load a theme file"));
        files.Children.Add(row);
    }

    // =====================================================================================================================
    // Buttons
    // =====================================================================================================================

    private void BuildButtons()
    {
        var l = _s.Look;
        var preview = Card("Preview", wide: true);
        var row = new WrapPanel();
        foreach (FrameworkElement el in new FrameworkElement[]
        {
            new Button { Content = "Select folders", Style = (Style)FindResource("DropButton") },
            new ToggleButton { Content = "Images", IsChecked = true }, new ToggleButton { Content = "GIFs" },
            new ToggleButton { Content = "General", IsChecked = true, Style = (Style)FindResource("ContentToggle") },
            new ToggleButton { Content = "NSFW", Style = (Style)FindResource("ContentToggle") },
            new Button { Content = "", Style = (Style)FindResource("IconButton") },
            new SectionSwitch(),
        })
        {
            el.Margin = new Thickness(0, 0, 10, 0); el.SetResourceReference(HeightProperty, "KeyHeight");
            row.Children.Add(el);
        }
        preview.Children.Add(row);

        var presets = Card("Start from a preset", wide: true);
        var pw = new WrapPanel();
        foreach (var (name, set) in ButtonLook.Presets)
            pw.Children.Add(Small(name, () => { set(l); Changed(); Rebuild(); }));
        presets.Children.Add(pw);

        var shape = Card("Shape and size");
        Slide(shape, "Roundness", 0, 20, () => Math.Min(l.Radius, 20), v => l.Radius = v >= 19.5 ? 99 : v, v => v >= 19.5 ? "pill" : $"{v:F0}");
        Slide(shape, "Height", 22, 38, () => l.Height, v => l.Height = v, v => $"{v:F0} px", 1);
        Slide(shape, "Icon buttons: roundness", 0, 20, () => Math.Min(l.IconRadius, 20), v => l.IconRadius = v >= 19.5 ? 99 : v, v => v >= 19.5 ? "circle" : $"{v:F0}");
        Slide(shape, "Icon buttons: width", 24, 44, () => l.IconSize, v => l.IconSize = v, v => $"{v:F0} px", 1);
        Slide(shape, "Icon size", 9, 18, () => l.IconGlyph, v => l.IconGlyph = v, v => $"{v:F0} px", 1);
        Colour(shape, "Icon colour", () => l.IconColour, v => l.IconColour = v);

        var fills = new[] { ("Solid", "Solid"), ("Vertical", "Top to bottom"), ("Horizontal", "Centre out"), ("BottomGlow", "Bottom glow") };
        var off = Card("Buttons that are off");
        Choice(off, "Fill", fills, () => l.OffStyle, v => l.OffStyle = v);
        Colour(off, "Colour 1", () => l.OffA, v => l.OffA = v);
        Colour(off, "Colour 2", () => l.OffB, v => l.OffB = v);
        Colour(off, "Text", () => l.Text, v => l.Text = v);

        var on = Card("Switches that are on", "General, NSFW and Very have their own colours; the other switches (and the section switch's knob) share theirs.");
        Choice(on, "Fill", fills, () => l.OnStyle, v => l.OnStyle = v);
        Colour(on, "General/NSFW/Very 1", () => l.ContentA, v => l.ContentA = v);
        Colour(on, "General/NSFW/Very 2", () => l.ContentB, v => l.ContentB = v);
        Colour(on, "General/NSFW/Very text", () => l.ContentText, v => l.ContentText = v);
        Colour(on, "Other switches 1", () => l.OtherA, v => l.OtherA = v);
        Colour(on, "Other switches 2", () => l.OtherB, v => l.OtherB = v);
        Colour(on, "Other switches text", () => l.OtherText, v => l.OtherText = v);

        var edge = Card("Outline, light, glow and shadow");
        Choice(edge, "Outline", new[] { ("None", "None"), ("Solid", "Colour"), ("Lighter", "Lighter shade"), ("Gradient", "Gradient") }, () => l.Outline, v => l.Outline = v, Rebuild);
        if (l.Outline == "Gradient")
        {
            Colour(edge, "Gradient colour 1", () => l.OutlineA, v => l.OutlineA = v);
            Colour(edge, "Gradient colour 2", () => l.OutlineB, v => l.OutlineB = v);
            Colour(edge, "Gradient colour 3", () => l.OutlineC, v => l.OutlineC = v);
            Slide(edge, "Gradient direction", 0, 360, () => l.OutlineAngle, v => l.OutlineAngle = v, v => $"{v:0}°", 15);
        }
        else Colour(edge, "Outline colour", () => l.OutlineColour, v => l.OutlineColour = v);
        Slide(edge, "Outline thickness", 0.5, 3, () => l.OutlineWidth, v => l.OutlineWidth = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:F1} px");
        Slide(edge, "Light along the top", 0, 1, () => l.Shine, v => l.Shine = v, v => $"{v * 100:F0}%");
        Slide(edge, "Glow when on", 0, 1, () => l.Glow, v => l.Glow = v, v => $"{v * 100:F0}%");
        Slide(edge, "Shadow", 0, 1, () => l.Shadow, v => l.Shadow = v, v => $"{v * 100:F0}%");
    }

    // =====================================================================================================================
    // Logo
    // =====================================================================================================================

    private LogoView? _logoBig, _logoSmall;

    private void BuildLogo()
    {
        var l = _s.Logo;
        var preview = Card("Preview", wide: true);
        var pr = new StackPanel { Orientation = Orientation.Horizontal };
        _logoBig = new LogoView(); var big = l.Copy(); big.Size = 96; _logoBig.Apply(big); _logoBig.SetButtonColours(_s.Look); _logoBig.Margin = new Thickness(10, 6, 40, 6);
        _logoSmall = new LogoView(); _logoSmall.Apply(l); _logoSmall.SetButtonColours(_s.Look); _logoSmall.VerticalAlignment = VerticalAlignment.Center;
        var bar = new Border { Padding = new Thickness(14, 10, 18, 10), CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center };
        bar.SetResourceReference(Border.BackgroundProperty, "BarBack");
        var barRow = new StackPanel { Orientation = Orientation.Horizontal };
        barRow.Children.Add(_logoSmall);
        var sw = new SectionSwitch { Height = 36, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        barRow.Children.Add(sw);
        barRow.Children.Add(new TextBlock { Text = "Home", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        bar.Child = barRow;
        pr.Children.Add(_logoBig); pr.Children.Add(bar);
        preview.Children.Add(pr);

        var presets = Card("Start from a preset", "Each is a starting point: change anything after.", wide: true);
        var pw = new WrapPanel();
        foreach (var (name, make) in LogoLook.Presets)
            pw.Children.Add(Small(name, () => { var n = make(); n.Size = l.Size; _s.Logo = n; Changed(); Rebuild(); }));
        presets.Children.Add(pw);

        var fill = Card("Fill");
        Choice(fill, "Fill", new[]
        {
            ("Original", "Its own colours"), ("Solid", "Colour"), ("Gradient", "Gradient"), ("Buttons", "Match the switches"), ("Switches", "Match General/NSFW"),
            ("Accent", "Accent"), ("Iridescent", "Iridescent"), ("Holographic", "Holographic"), ("Metal", "Metal"),
        }, () => l.Fill, v => l.Fill = v);
        Colour(fill, "Colour 1", () => l.FillA, v => l.FillA = v);
        Colour(fill, "Colour 2 (gradient)", () => l.FillB, v => l.FillB = v);
        Slide(fill, "Gradient angle", 0, 360, () => l.FillAngle, v => l.FillAngle = v, v => $"{v:0}°", 15);
        Check(fill, "The colours slowly move", () => l.FillFlow, v => l.FillFlow = v, "Gradients, metal and the switch colours drift; iridescent and holographic always move");

        var outline = Card("Outline");
        Choice(outline, "Outline", new[] { ("None", "None"), ("Original", "Its own"), ("Solid", "Colour"), ("Gradient", "Gradient"), ("Rainbow", "Rainbow"), ("Silver", "Silver") }, () => l.Outline, v => l.Outline = v);
        Colour(outline, "Colour 1", () => l.OutlineA, v => l.OutlineA = v);
        Colour(outline, "Colour 2 (gradient)", () => l.OutlineB, v => l.OutlineB = v);
        Slide(outline, "Thickness", 0, 4, () => l.OutlineWidth, v => l.OutlineWidth = v, v => $"{v:0.0}", 0.1);
        Choice(outline, "Movement", new[] { ("Still", "Still"), ("Pulse", "Pulse"), ("Orbit", "A light goes round") }, () => l.OutlineMotion, v => l.OutlineMotion = v);

        var light = Card("Glow, shadow and shine");
        Slide(light, "Glow", 0, 1, () => l.Glow, v => l.Glow = v, v => $"{v * 100:0}%", 0.05);
        Colour(light, "Glow colour", () => l.GlowColour, v => l.GlowColour = v, allowBlank: true, blankText: "Fill's colour");
        Check(light, "The glow pulses", () => l.GlowPulse, v => l.GlowPulse = v);
        Slide(light, "Shadow", 0, 1, () => l.Shadow, v => l.Shadow = v, v => $"{v * 100:0}%", 0.05);
        Choice(light, "Shine", new[] { ("None", "None"), ("Sweep", "A shine crosses it"), ("Sparkle", "Shine, then sparkles") }, () => l.Shine, v => l.Shine = v);
        Slide(light, "Shine every", 2, 30, () => l.ShineEvery, v => l.ShineEvery = v, v => $"{v:0} s", 1);

        var size = Card("Size and speed");
        Check(size, "Big: over the title bar and the top bar together", () => l.Big, v => l.Big = v, "The window's name and its line start after it. In full screen it goes back to its size in the bar.");
        Slide(size, "Size in the bar", 20, 52, () => l.Size, v => l.Size = v, v => $"{v:0} px", 1);
        Slide(size, "Speed of movement", 0.25, 3, () => l.Speed, v => l.Speed = v, v => $"{v:0.##}x", 0.25);
        Check(size, "A tile behind the logo", () => l.Tile, v => l.Tile = v);
        Colour(size, "Tile colour", () => l.TileColour, v => l.TileColour = v, allowBlank: true);
    }

    // =====================================================================================================================
    // Backgrounds
    // =====================================================================================================================

    private void BuildScenes()
    {
        var look = _s.Scenes;
        var where = Card("Where", "One background for the whole window (bars, behind the pictures and the viewer), or a different one for each place.", wide: true);
        Choice(where, "Backgrounds", new[] { ("One", "One for everything"), ("Each", "Each its own") }, () => look.Mode, v => look.Mode = v, Rebuild);
        Choice(where, "Bars' background", new[] { ("Window", "Spread over the window"), ("Bars", "Fitted into the bars") }, () => look.BarsFit, v => look.BarsFit = v);
        Slide(where, "Bars show through", 0, 0.95, () => look.SeeThrough, v => look.SeeThrough = v, v => v < 0.02 ? "solid" : $"{v * 100:0}%", 0.05);

        if (look.Mode == "One") SceneCard("Everywhere", look.All);
        else
        {
            SceneCard("The bars", look.Bars);
            SceneCard("Behind the pictures", look.Window);
            SceneCard("The viewer", look.Viewer);
        }
    }

    private void SceneCard(string title, Scene s)
    {
        var card = Card(title);
        var preview = new Image { Height = 110, Stretch = Stretch.UniformToFill, Margin = new Thickness(0, 0, 0, 10) };
        var frame = new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = preview, Height = 110 };
        frame.SetResourceReference(Border.BackgroundProperty, "WindowBg");
        void Refresh() => preview.Source = SceneKit.Preview(s, 360, 200);
        card.Children.Add(frame);
        var kinds = new WrapPanel();
        var btns = new List<(ToggleButton b, string k)>();
        foreach (var k in Scene.Kinds)
        {
            var b = new ToggleButton { Content = k == "Theme" ? "Theme colour" : k, Height = 26, Padding = new Thickness(9, 0, 9, 0), Margin = new Thickness(0, 0, 4, 4), IsChecked = s.Kind == k };
            string kk = k;
            b.Click += (_, _) =>
            {
                var p = SceneKit.Preset(kk);
                s.Kind = kk; s.A = p.A; s.B = p.B; s.C = p.C; s.Angle = p.Angle;
                Changed(); Rebuild();
            };
            btns.Add((b, k)); kinds.Children.Add(b);
        }
        var kindRow = Row(card, "Kind", kinds);
        AddReset(card, kindRow, () => s.Kind, v => { var p = SceneKit.Preset(v); s.Kind = v; s.A = p.A; s.B = p.B; s.C = p.C; s.Angle = p.Angle; });
        if (s.Kind != "Theme")
        {
            Colour(card, "Colour 1", () => s.A, v => { s.A = v; Refresh(); });
            Colour(card, "Colour 2", () => s.B, v => { s.B = v; Refresh(); });
            if (s.Kind != "Gradient") Colour(card, "Colour 3", () => s.C, v => { s.C = v; Refresh(); });
            if (s.Kind is "Gradient" or "Milky Way") Slide(card, "Angle", 0, 360, () => s.Angle, v => { s.Angle = v; Refresh(); }, v => $"{v:0}°", 15);
            if (s.Kind != "Gradient")
            {
                Slide(card, "Brightness", 0.2, 1.8, () => s.Strength, v => { s.Strength = v; Refresh(); }, v => $"{v * 100:0}%", 0.05);
                Slide(card, "Stars", 0, 5, () => s.Stars, v => { s.Stars = v; Refresh(); }, v => v < 0.05 ? "none" : $"{v * 100:0}%", 0.05);
            }
        }
        Refresh();
    }

    // =====================================================================================================================
    // Folder pop-outs
    // =====================================================================================================================

    private void BuildFolders()
    {
        var f = _s.Folders;
        var preview = Card("Preview", "The folder switches: shown, partly shown and hidden.", wide: true);
        _folderDemo = new WrapPanel { Margin = new Thickness(12, 8, 12, 12) };
        FillFolderDemo();
        PopCard.SetLook(f.Panel);
        var card = new PopCard(_folderDemo, "Select folders") { NotchX = 60 };
        preview.Children.Add(card);

        var panel = Card("The pop-out card");
        Colour(panel, "Background", () => f.Panel.Background, v => f.Panel.Background = v, allowBlank: true);
        Slide(panel, "Opacity", 0.5, 1, () => f.Panel.Opacity, v => f.Panel.Opacity = v, v => $"{v * 100:0}%", 0.05);
        Colour(panel, "Edge", () => f.Panel.Border, v => f.Panel.Border = v, allowBlank: true, blankText: "Accent");
        Colour(panel, "Edge, second colour", () => f.Panel.Border2, v => f.Panel.Border2 = v, allowBlank: true, blankText: "None");
        Slide(panel, "Edge thickness", 0, 4, () => f.Panel.BorderWidth, v => f.Panel.BorderWidth = v, v => $"{v:0.0} px", 0.5);
        Slide(panel, "Corners", 0, 24, () => f.Panel.Radius, v => f.Panel.Radius = v, v => $"{v:0}", 1);
        Slide(panel, "Glow", 0, 1, () => f.Panel.Glow, v => f.Panel.Glow = v, v => $"{v * 100:0}%", 0.05);
        Slide(panel, "Shadow", 0, 1, () => f.Panel.Shadow, v => f.Panel.Shadow = v, v => $"{v * 100:0}%", 0.05);
        Check(panel, "A pointer up to its button", () => f.Panel.Notch, v => f.Panel.Notch = v, after: Rebuild);
        Check(panel, "A title at the top", () => f.Panel.Title, v => f.Panel.Title = v, after: Rebuild);

        var t = f.Toggles;
        var fills = new[] { ("Solid", "Solid"), ("Vertical", "Top to bottom"), ("Horizontal", "Centre out"), ("BottomGlow", "Bottom glow") };
        var sw = Card("The folder switches");
        Small2(sw, "Default switches", () => { f.Toggles = FolderLook.DefaultToggles(); Changed(); Rebuild(); });
        Slide(sw, "Roundness", 0, 16, () => t.Radius, v => t.Radius = v, v => $"{v:0}", 1);
        Choice(sw, "Fill when hidden", fills, () => t.OffStyle, v => t.OffStyle = v, Rebuild);
        Colour(sw, "Hidden 1", () => t.OffA, v => t.OffA = v);
        Colour(sw, "Hidden 2", () => t.OffB, v => t.OffB = v);
        Colour(sw, "Hidden text", () => t.Text, v => t.Text = v);
        Choice(sw, "Fill when shown", fills, () => t.OnStyle, v => t.OnStyle = v, Rebuild);
        Colour(sw, "Shown 1", () => t.OtherA, v => t.OtherA = v);
        Colour(sw, "Shown 2", () => t.OtherB, v => t.OtherB = v);
        Colour(sw, "Shown text", () => t.OtherText, v => t.OtherText = v);
        Colour(sw, "Partly shown 1", () => t.ContentA, v => t.ContentA = v);
        Colour(sw, "Partly shown 2", () => t.ContentB, v => t.ContentB = v);
        Colour(sw, "Partly shown text", () => t.ContentText, v => t.ContentText = v);

        var edge = Card("Switch outline, light and glow");
        Choice(edge, "Outline", new[] { ("None", "None"), ("Solid", "Colour"), ("Lighter", "Lighter shade"), ("Gradient", "Gradient") }, () => t.Outline, v => t.Outline = v, Rebuild);
        if (t.Outline == "Gradient")
        {
            Colour(edge, "Gradient colour 1", () => t.OutlineA, v => t.OutlineA = v);
            Colour(edge, "Gradient colour 2", () => t.OutlineB, v => t.OutlineB = v);
            Colour(edge, "Gradient colour 3", () => t.OutlineC, v => t.OutlineC = v);
            Slide(edge, "Gradient direction", 0, 360, () => t.OutlineAngle, v => t.OutlineAngle = v, v => $"{v:0}°", 15);
        }
        else Colour(edge, "Outline colour", () => t.OutlineColour, v => t.OutlineColour = v);
        Slide(edge, "Outline thickness", 0.5, 3, () => t.OutlineWidth, v => t.OutlineWidth = Math.Round(v * 2) / 2, v => $"{Math.Round(v * 2) / 2:0.0} px");
        Slide(edge, "Light along the top", 0, 1, () => t.Shine, v => t.Shine = v, v => $"{v * 100:0}%", 0.05);
        Slide(edge, "Glow when shown", 0, 1, () => t.Glow, v => t.Glow = v, v => $"{v * 100:0}%", 0.05);
        Slide(edge, "Shadow", 0, 1, () => t.Shadow, v => t.Shadow = v, v => $"{v * 100:0}%", 0.05);
    }

    private WrapPanel? _folderDemo;

    private void FillFolderDemo()
    {
        if (_folderDemo is null) return;
        var t = _s.Folders.Toggles;
        _folderDemo.Children.Clear();
        var kits = new[] { t.KitFor(0), t.KitFor(1), t.KitFor(2) };
        foreach (var (name, st) in new[] { ("Games", 1), ("Art", 1), ("Wallpapers", 0), ("Screenshots", 2), ("Photos", 1), ("Old", 0) })
        {
            var k = kits[st];
            var inside = new Grid();
            inside.Children.Add(new TextBlock { Text = name, Foreground = k.Text, Margin = new Thickness(12, 5, 12, 5) });
            inside.Children.Add(new Border { BorderBrush = k.Shine, BorderThickness = new Thickness(1), CornerRadius = k.Radius, IsHitTestVisible = false });
            _folderDemo.Children.Add(new Border
            {
                Background = k.Fill, BorderBrush = k.Border, BorderThickness = k.BorderWidth, CornerRadius = k.Radius, Effect = t.Glow > 0.01 && st != 0 ? k.Glow : k.Shadow,
                Margin = new Thickness(0, 0, 6, 6), Child = inside,
            });
        }
    }

    private static void Small2(StackPanel card, string text, Action act) => card.Children.Add(Small(text, act));

    // =====================================================================================================================
    // Sets
    // =====================================================================================================================

    private void BuildSets()
    {
        var l = _s.SetStyle;
        var note = Card("How it works", "Sets show like this wherever they mix with other pictures. When only sets are shown (only Connected Art picked in Select folders, Only sets in Filters, or Browse inside Connected Art), their pictures simply show one by one.", wide: true);
        note.Children.Add(new TextBlock { Text = "Try the outline in a couple of colours with the glass or the links: they mix.", TextWrapping = TextWrapping.Wrap });

        var o = Card("Outline");
        Choice(o, "Outline", new[] { ("None", "None"), ("Solid", "Colour"), ("Gradient", "Gradient"), ("Dashed", "Dashed"), ("Glow", "Glowing") }, () => l.Outline, v => l.Outline = v);
        Colour(o, "Colour 1", () => l.ColourA, v => l.ColourA = v);
        Colour(o, "Colour 2 (gradient)", () => l.ColourB, v => l.ColourB = v);
        Slide(o, "Thickness", 0.5, 6, () => l.Width, v => l.Width = v, v => $"{v:0.0} px", 0.5);
        Slide(o, "Corners", 0, 16, () => l.Radius, v => l.Radius = v, v => $"{v:0}", 1);
        Slide(o, "Distance from the pictures", 0, 6, () => l.Offset, v => l.Offset = v, v => $"{v:0.0} px", 0.5);
        Slide(o, "Glow", 0, 1, () => l.Glow, v => l.Glow = v, v => $"{v * 100:0}%", 0.05);

        var m = Card("More ways to show it");
        Check(m, "No gap between the pictures of a set (columns)", () => l.NoGap, v => l.NoGap = v);
        Check(m, "The set's name in its corner", () => l.Label, v => l.Label = v, "Large sets keep their label: it opens and closes them");
        Check(m, "Links on the seams between its pictures", () => l.Links, v => l.Links = v);
        Check(m, "A glassy tint over the set", () => l.Glass, v => l.Glass = v);
        Colour(m, "Glass colour", () => l.GlassColour, v => l.GlassColour = v);
        Slide(m, "Glass strength", 0, 0.6, () => l.GlassStrength, v => l.GlassStrength = v, v => $"{v * 100:0}%", 0.02);
        Check(m, "A shine crosses each set now and then", () => l.Shine, v => l.Shine = v);
        Slide(m, "Shine every", 3, 40, () => l.ShineEvery, v => l.ShineEvery = v, v => $"{v:0} s", 1);
    }

    // =====================================================================================================================
    // Favourites and details
    // =====================================================================================================================

    private void BuildFavs()
    {
        var l = _s.FavStyle;
        var mark = Card("Favourites in the grid", "Favourites are marked everywhere except in Favourites itself. Add or remove one from the right-click menu, the viewer's star, or the star button if you turn it on.");
        Choice(mark, "Outline", new[] { ("None", "None"), ("Gold", "Gold"), ("Holographic", "Holographic"), ("Solid", "Colour"), ("Gradient", "Gradient") }, () => l.Outline, v => l.Outline = v);
        Colour(mark, "Colour 1", () => l.ColourA, v => l.ColourA = v);
        Colour(mark, "Colour 2 (gradient)", () => l.ColourB, v => l.ColourB = v);
        Slide(mark, "Thickness", 0.5, 6, () => l.Width, v => l.Width = v, v => $"{v:0.0} px", 0.5);
        Slide(mark, "Glow", 0, 1, () => l.Glow, v => l.Glow = v, v => $"{v * 100:0}%", 0.05);
        Choice(mark, "Card shine", new[] { ("None", "None"), ("Hover", "When the mouse is over it"), ("Always", "Now and then") }, () => l.Shine, v => l.Shine = v);
        Slide(mark, "Shine every", 3, 40, () => l.ShineEvery, v => l.ShineEvery = v, v => $"{v:0} s", 1);
        Check(mark, "A star on favourites", () => l.Star, v => l.Star = v);
        Check(mark, "A star button on the picture under the mouse (to add a favourite)", () => l.StarButton, v => l.StarButton = v);

        var info = Card("Picture details", "The resolution and size of the picture under the mouse (they are also in the bottom bar).");
        Choice(info, "Show them", new[] { ("None", "Only in the bottom bar"), ("Strip", "In a strip under the picture"), ("Corner", "In the top-right corner") }, () => l.Info, v => l.Info = v);
        Colour(info, "Background", () => l.InfoBack, v => l.InfoBack = v);
        Slide(info, "Background strength", 0.2, 1, () => l.InfoOpacity, v => l.InfoOpacity = v, v => $"{v * 100:0}%", 0.05);
        Colour(info, "Text", () => l.InfoText, v => l.InfoText = v);
        Slide(info, "Text size", 8, 15, () => l.InfoSize, v => l.InfoSize = v, v => $"{v:0.#}", 0.5);
    }

    // =====================================================================================================================
    // Video outlines
    // =====================================================================================================================

    private void BuildOutlines()
    {
        var card = Card("Paused and watched videos", wide: false);
        var ed = new OutlineEditor(_s) { Margin = new Thickness(0) };
        ed.Changed += Changed;
        card.Children.Add(ed);
        CardReset(card, () => { _page.Set(_page.Default()); return true; });
    }

    // =====================================================================================================================
    // little dialogs
    // =====================================================================================================================

    private string? Ask(string title, string prompt, string start)
    {
        var box = new TextBox { Text = start, MinWidth = 320, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 14) };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 84, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Height = 28 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row.Children.Add(ok); row.Children.Add(cancel);
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(new TextBlock { Text = prompt }); p.Children.Add(box); p.Children.Add(row);
        var w = new Window { Title = title, Content = p, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, Owner = Host, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        w.SetResourceReference(Window.BackgroundProperty, "Bar");
        w.SetResourceReference(TextElement.ForegroundProperty, "Text");
        w.SetResourceReference(Window.FontFamilyProperty, "AppFont");
        w.SourceInitialized += (_, _) => Theme.DarkTitleBar(w);
        ok.Click += (_, _) => w.DialogResult = true;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        if (w.ShowDialog() != true) return null;
        string t = box.Text.Trim();
        return t.Length == 0 ? null : t;
    }

    private Window Host => Window.GetWindow(this) ?? Application.Current.MainWindow!;

    private void Message(string text) => MessageBox.Show(Host, text, "Appearance");
    private bool Confirm(string text) => MessageBox.Show(Host, text, "Appearance", MessageBoxButton.YesNo) == MessageBoxResult.Yes;
}
