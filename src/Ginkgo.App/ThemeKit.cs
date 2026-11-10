using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace Ginkgo.App;

/// <summary>
/// A theme: the colours of everything (behind the pictures, the bars and panels, boxes inside them, fields, edges, text, dim text and
/// the accent), the font with its size and density, the icon style, and the viewer's background (a colour, a gradient or a pattern).
/// Saved as a single file in Ginkgo's data folder.
/// </summary>
public sealed class AppTheme
{
    public string Name { get; set; } = "Ginkgo dark";
    public bool Light { get; set; }
    public string Window { get; set; } = "#121214";       // behind the pictures
    public string Surface { get; set; } = "#1C1C20";      // bars and panels
    public string Inner { get; set; } = "#161619";        // boxes inside panels
    public string Field { get; set; } = "#26262C";
    public string Edge { get; set; } = "#3A3A42";
    public string Text { get; set; } = "#E4E4E8";
    public string Dim { get; set; } = "#9AA0A6";
    public string Accent { get; set; } = "#8A5CF5";
    /// <summary>Buttons that are off, and their text (the shape, glow and the rest stay in Button look).</summary>
    public string ButtonA { get; set; } = "#0E0E12";
    public string ButtonB { get; set; } = "#19191D";
    public string ButtonText { get; set; } = "#E4E4E8";

    public string Font { get; set; } = "Segoe UI";
    public double FontSize { get; set; } = 12;
    /// <summary>How roomy the controls are: 0.85 compact ... 1.25 roomy.</summary>
    public double Density { get; set; } = 1.0;
    /// <summary>"Fluent" (Windows 11 icons) or "Classic" (the Windows 10 set).</summary>
    public string Icons { get; set; } = "Fluent";

    /// <summary>The viewer's background: "Solid", "Gradient" or "Pattern".</summary>
    public string Backdrop { get; set; } = "Solid";
    public string BackA { get; set; } = "#000000";
    public string BackB { get; set; } = "#1A1630";
    public double BackAngle { get; set; } = 135;
    /// <summary>"Dots", "Grid", "Diagonal" or "Checks", drawn over the gradient.</summary>
    public string Pattern { get; set; } = "Dots";
    public string PatternColour { get; set; } = "#FFFFFF";
    public double PatternOpacity { get; set; } = 0.06;
    public double PatternSize { get; set; } = 18;

    public AppTheme Copy() => JsonSerializer.Deserialize<AppTheme>(JsonSerializer.Serialize(this))!;
}

public static class ThemeKit
{
    /// <summary>The fonts offered (all come with Windows).</summary>
    public static readonly string[] Fonts =
    {
        "Segoe UI", "Segoe UI Variable Text", "Segoe UI Variable Display", "Segoe UI Semilight", "Segoe UI Light", "Bahnschrift", "Bahnschrift Light",
        "Bahnschrift SemiCondensed", "Calibri", "Calibri Light", "Cambria", "Candara", "Consolas", "Constantia", "Corbel", "Corbel Light", "Franklin Gothic Medium",
        "Gabriola", "Georgia", "Ink Free", "Leelawadee UI", "Lucida Sans Unicode", "Lucida Console", "Malgun Gothic", "Microsoft JhengHei UI",
        "Microsoft YaHei UI", "MV Boli", "Nirmala UI", "Palatino Linotype", "Segoe Print", "Segoe Script", "Sitka Text", "Sylfaen", "Tahoma",
        "Trebuchet MS", "Verdana", "Yu Gothic UI", "Arial", "Times New Roman", "Comic Sans MS", "Ebrima", "Gadugi",
    };

    private static AppTheme Dark(string name, string window, string surface, string accent, string? text = null, string? dim = null)
    {
        Color s = ButtonLook.C(surface);
        string Shift(Color c, int d) => $"#{Math.Clamp(c.R + d, 0, 255):X2}{Math.Clamp(c.G + d, 0, 255):X2}{Math.Clamp(c.B + d, 0, 255):X2}";
        return new AppTheme
        {
            Name = name, Window = window, Surface = surface, Inner = Shift(s, -6), Field = Shift(s, 10), Edge = Shift(s, 30), Accent = accent,
            Text = text ?? "#E4E4E8", Dim = dim ?? "#9AA0A6", ButtonA = Shift(s, -14), ButtonB = Shift(s, -3), ButtonText = text ?? "#E4E4E8",
        };
    }

    private static AppTheme LightTheme(string name, string window, string surface, string accent) => new()
    {
        Name = name, Light = true, Window = window, Surface = surface, Inner = "#F4F4F6", Field = "#FFFFFF", Edge = "#C9C9D1", Accent = accent,
        Text = "#1E1E24", Dim = "#5C6168", ButtonA = "#ECECEF", ButtonB = "#FAFAFB", ButtonText = "#1E1E24", BackA = "#F2F2F4", BackB = "#DCDCE4",
        PatternColour = "#000000",
    };

    /// <summary>The built-in themes: dark ones first, then a few light ones. Ginkgo dark is the default.</summary>
    public static readonly AppTheme[] BuiltIn =
    {
        new AppTheme(),
        Dark("Graphite", "#101010", "#1B1B1B", "#5B9BFF"),
        Dark("Midnight", "#0B0E16", "#141A26", "#4D8DFF"),
        Dark("Deep ocean", "#071318", "#0E1E26", "#2EC4D6"),
        Dark("Forest", "#0C120E", "#152019", "#5FAF6B"),
        Dark("Moss", "#11130C", "#1C2015", "#9BC25B"),
        Dark("Ember", "#140D0B", "#211714", "#FF7A45"),
        Dark("Rosewood", "#140C10", "#22151B", "#E85D8A"),
        Dark("Plum", "#120C16", "#1E1525", "#B07CFF"),
        Dark("Amethyst", "#0F0B18", "#1A1428", "#9C6BFF"),
        Dark("Slate", "#0F1215", "#1A1F24", "#7FA7C9"),
        Dark("Steel", "#131518", "#1F2328", "#A0AEC0"),
        Dark("Coffee", "#130F0C", "#201914", "#C8955B"),
        Dark("Sand dune", "#15130E", "#221F17", "#E0B860"),
        Dark("Crimson", "#120A0B", "#201214", "#E5484D"),
        Dark("Teal night", "#08120F", "#10201B", "#3FD0B0"),
        Dark("Neon", "#09090D", "#121218", "#FF3DAA", "#F2F2F8"),
        Dark("Mono", "#0A0A0A", "#161616", "#D8D8D8", "#EDEDED", "#9A9A9A"),
        Dark("OLED black", "#000000", "#0A0A0A", "#8A5CF5"),
        Dark("Nord", "#1B1F27", "#242933", "#88C0D0", "#ECEFF4", "#A3ABB9"),
        Dark("Dracula", "#1B1C24", "#252632", "#BD93F9", "#F8F8F2", "#A0A3B8"),
        Dark("Solarized dark", "#00212B", "#073642", "#B58900", "#EEE8D5", "#93A1A1"),
        LightTheme("Paper", "#E9E9EC", "#FAFAFB", "#6A3CF0"),
        LightTheme("Daylight", "#E4E8EE", "#F7F9FC", "#2F6FED"),
        LightTheme("Linen", "#ECE7DF", "#FBF8F3", "#B5652B"),
    };

    private static ResourceDictionary R => Application.Current.Resources;

    /// <summary>The icon font for code that draws its own icons.</summary>
    public static FontFamily IconFamily { get; private set; } = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    private static Typeface? _iconFace;
    public static Typeface IconTypeface => _iconFace ??= new Typeface(IconFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>A theme brush as it is now. The window's own parts follow theme changes through DynamicResource; brushes taken
    /// here are a snapshot (application resources are frozen when they are stored, so they can't be changed in place).</summary>
    public static SolidColorBrush Brush(string key)
        => Application.Current?.TryFindResource(key) as SolidColorBrush ?? Gray;
    private static readonly SolidColorBrush Gray = MakeGray();
    private static SolidColorBrush MakeGray() { var b = new SolidColorBrush(Colors.Gray); b.Freeze(); return b; }

    /// <summary>Puts a theme into effect everywhere (brushes, fonts, icons, the grid and the viewer's background).</summary>
    public static void Apply(AppTheme t, ButtonLook look)
    {
        void Set(string key, string hex) { var b = new SolidColorBrush(ButtonLook.C(hex)); b.Freeze(); R[key] = b; }
        Set("WindowBg", t.Window); Set("Bar", t.Surface); Set("Surface2", t.Inner); Set("Field", t.Field); Set("Edge", t.Edge);
        Set("Text", t.Text); Set("Dim", t.Dim); Set("Accent", t.Accent);
        R["AppFont"] = new FontFamily(t.Font);
        R["AppFontSize"] = Math.Clamp(t.FontSize, 9, 18);
        IconFamily = new FontFamily(t.Icons == "Classic" ? "Segoe MDL2 Assets" : "Segoe Fluent Icons, Segoe MDL2 Assets");
        _iconFace = null;
        R["IconFont"] = IconFamily;
        R["ViewerBack"] = Backdrop(t);
        MasonryView.SetBackground(ButtonLook.C(t.Window));
        // the buttons come from Button look (a light theme brings its own off-button colours), sized by the theme's density
        var l = look.Copy();
        if (t.Light) { l.OffA = t.ButtonA; l.OffB = t.ButtonB; l.Text = t.ButtonText; }      // dark buttons would be lost on a light theme
        l.Height = Math.Round(look.Height * Math.Clamp(t.Density, 0.75, 1.4));
        l.Apply(R);
    }

    /// <summary>The viewer's background: a colour, a gradient at an angle, or a gradient with a pattern drawn over it.</summary>
    public static Brush Backdrop(AppTheme t)
    {
        Color a = ButtonLook.C(t.BackA), b = ButtonLook.C(t.BackB);
        if (t.Backdrop == "Solid") { var s = new SolidColorBrush(a); s.Freeze(); return s; }
        double rad = t.BackAngle * Math.PI / 180;
        var grad = new LinearGradientBrush(a, b, new Point(0.5 - Math.Cos(rad) / 2, 0.5 - Math.Sin(rad) / 2), new Point(0.5 + Math.Cos(rad) / 2, 0.5 + Math.Sin(rad) / 2));
        if (t.Backdrop != "Pattern") { grad.Freeze(); return grad; }
        double sz = Math.Clamp(t.PatternSize, 4, 120);
        var pc = ButtonLook.C(t.PatternColour); pc.A = (byte)(Math.Clamp(t.PatternOpacity, 0, 1) * 255);
        var ink = new SolidColorBrush(pc); ink.Freeze();
        var pen = new Pen(ink, Math.Max(1, sz / 18)); pen.Freeze();
        Drawing tile = t.Pattern switch
        {
            "Grid" => new GeometryDrawing(null, pen, Geometry.Parse($"M0,0 L{sz},0 M0,0 L0,{sz}")),
            "Diagonal" => new GeometryDrawing(null, pen, Geometry.Parse($"M0,{sz} L{sz},0")),
            "Checks" => new GeometryDrawing(ink, null, Geometry.Parse($"M0,0 h{sz / 2} v{sz / 2} h-{sz / 2} Z M{sz / 2},{sz / 2} h{sz / 2} v{sz / 2} h-{sz / 2} Z")),
            _ => new GeometryDrawing(ink, null, new EllipseGeometry(new Point(sz / 2, sz / 2), sz / 12 + 0.8, sz / 12 + 0.8)),
        };
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, sz, sz))));
        group.Children.Add(tile);
        var pattern = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, sz, sz), ViewportUnits = BrushMappingMode.Absolute };
        // the gradient fills, the pattern tiles over it
        var layered = new DrawingGroup();
        layered.Children.Add(new GeometryDrawing(grad, null, new RectangleGeometry(new Rect(0, 0, 4000, 4000))));
        layered.Children.Add(new GeometryDrawing(pattern, null, new RectangleGeometry(new Rect(0, 0, 4000, 4000))));
        var result = new DrawingBrush(layered) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        result.Freeze();
        return result;
    }

    // ---- your own themes: one file each in Ginkgo's data folder ----

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "themes");

    public static List<AppTheme> LoadOwn()
    {
        var list = new List<AppTheme>();
        try
        {
            if (Directory.Exists(Folder))
                foreach (var f in Directory.EnumerateFiles(Folder, "*.json").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                    try { if (JsonSerializer.Deserialize<AppTheme>(File.ReadAllText(f)) is { } t) list.Add(t); } catch { }
        }
        catch { }
        return list;
    }

    private static string FileOf(string name) => Path.Combine(Folder, string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json");

    public static void SaveOwn(AppTheme t)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FileOf(t.Name), JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void DeleteOwn(string name) { try { File.Delete(FileOf(name)); } catch { } }

    public static void Export(AppTheme t, string path) => File.WriteAllText(path, JsonSerializer.Serialize(t, new JsonSerializerOptions { WriteIndented = true }));
    public static AppTheme? Import(string path) { try { return JsonSerializer.Deserialize<AppTheme>(File.ReadAllText(path)); } catch { return null; } }

    /// <summary>Removes every theme of your own (Reset themes).</summary>
    public static void ResetOwn() { try { if (Directory.Exists(Folder)) Directory.Delete(Folder, true); } catch { } }
}
