using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ginkgo.App;

// =====================================================================================================================
// The looks you can dial in on the Appearance window (besides the theme and the buttons): the logo, the backgrounds,
// the folder pop-outs and their switches, how Connected Art sets are marked, and how favourites and picture details show.
// Each kind can be saved under names of your own (see LookLibrary); the one in use lives in the settings.
// =====================================================================================================================

/// <summary>The logo in the top-left corner: its fill, outline, glow, shadow and moving light. Everything mixes with everything.</summary>
public sealed class LogoLook
{
    /// <summary>Original (the logo's own colours), Solid, Gradient, Buttons (the "on" button colours), Switches (General/NSFW/Very),
    /// Accent, Iridescent (soft flowing pastels), Holographic (a moving rainbow foil), Metal (brushed silver).</summary>
    public string Fill { get; set; } = "Original";
    public string FillA { get; set; } = "#D8BB62";
    public string FillB { get; set; } = "#8FE6A8";
    public double FillAngle { get; set; } = 135;
    /// <summary>The colours slowly move (gradients, iridescent, holographic and metal).</summary>
    public bool FillFlow { get; set; } = true;

    /// <summary>None, Original (the drawing's own dark line), Solid, Gradient, Rainbow, Silver.</summary>
    public string Outline { get; set; } = "Original";
    public string OutlineA { get; set; } = "#FFFFFF";
    public string OutlineB { get; set; } = "#8A5CF5";
    public double OutlineWidth { get; set; } = 1.2;
    /// <summary>Still, Pulse (breathes), Orbit (a light runs round the edge).</summary>
    public string OutlineMotion { get; set; } = "Still";

    public double Glow { get; set; }
    /// <summary>"" = the fill's own colour.</summary>
    public string GlowColour { get; set; } = "";
    public bool GlowPulse { get; set; }
    public double Shadow { get; set; } = 0.35;

    /// <summary>None, Sweep (a band of light crosses it now and then), Sparkle (the sweep, then a few sparkles).</summary>
    public string Shine { get; set; } = "None";
    public double ShineEvery { get; set; } = 6;
    /// <summary>How fast everything moves (0.25 slow ... 3 fast).</summary>
    public double Speed { get; set; } = 1;
    public double Size { get; set; } = 30;
    /// <summary>A rounded tile behind the logo ("" colour = the theme's field colour).</summary>
    public bool Tile { get; set; }
    public string TileColour { get; set; } = "";

    public LogoLook Copy() => JsonSerializer.Deserialize<LogoLook>(JsonSerializer.Serialize(this))!;

    public bool Animated => (FillFlow && Fill is "Gradient" or "Buttons" or "Switches" or "Metal") || Fill is "Iridescent" or "Holographic"
        || OutlineMotion != "Still" || GlowPulse || Shine != "None";

    public static readonly (string name, Func<LogoLook> make)[] Presets =
    {
        ("Original", () => new LogoLook()),
        ("Iridescent", () => new LogoLook { Fill = "Iridescent", Outline = "None", Glow = 0.25, GlowColour = "#C9B8FF", Shadow = 0.2 }),
        ("Holographic", () => new LogoLook { Fill = "Holographic", Outline = "Silver", OutlineWidth = 0.8, Shine = "Sweep", Glow = 0.2, Shadow = 0.25 }),
        ("Glowing", () => new LogoLook { Fill = "Original", Glow = 0.8, GlowColour = "#FFD45A", GlowPulse = true, Shadow = 0 }),
        ("Silver orbit", () => new LogoLook { Fill = "Metal", Outline = "Silver", OutlineWidth = 1.4, OutlineMotion = "Orbit", Shadow = 0.3 }),
        ("Shine and sparkle", () => new LogoLook { Fill = "Original", Shine = "Sparkle", ShineEvery = 5, Shadow = 0.3 }),
        ("Rainbow orbit", () => new LogoLook { Fill = "Original", Outline = "Rainbow", OutlineWidth = 1.6, OutlineMotion = "Orbit", Glow = 0.3 }),
        ("Match the buttons", () => new LogoLook { Fill = "Buttons", Outline = "None", Glow = 0.45, Shadow = 0.2 }),
        ("Neon outline", () => new LogoLook { Fill = "Solid", FillA = "#101014", Outline = "Gradient", OutlineA = "#2FD6B4", OutlineB = "#B07CFF", OutlineWidth = 1.8, OutlineMotion = "Pulse", Glow = 0.6, GlowColour = "#2FD6B4" }),
    };
}

/// <summary>A background: a plain colour, a gradient, or a night-sky scene drawn by Ginkgo.</summary>
public sealed class Scene
{
    /// <summary>Theme (the theme's own colour), Gradient, Starfield, Night sky, Milky Way, Nebula, Aurora, Deep space, Dusk.</summary>
    public string Kind { get; set; } = "Theme";
    public string A { get; set; } = "#05060B";
    public string B { get; set; } = "#1A1630";
    public string C { get; set; } = "#2FD6B4";
    public double Angle { get; set; } = 135;
    /// <summary>How bright the stars and clouds are (0.2 ... 1.5).</summary>
    public double Strength { get; set; } = 1;
    /// <summary>How many stars (0.2 ... 2).</summary>
    public double Stars { get; set; } = 1;

    public Scene Copy() => JsonSerializer.Deserialize<Scene>(JsonSerializer.Serialize(this))!;
    public static readonly string[] Kinds = { "Theme", "Gradient", "Starfield", "Night sky", "Milky Way", "Nebula", "Aurora", "Deep space", "Dusk" };
}

/// <summary>Where backgrounds go: one for the whole window, or one each for the bars, behind the pictures, and the viewer.</summary>
public sealed class SceneLook
{
    /// <summary>"One": the same background everywhere; "Each": one per place.</summary>
    public string Mode { get; set; } = "Each";
    public Scene All { get; set; } = new() { Kind = "Starfield" };
    public Scene Bars { get; set; } = new();
    public Scene Window { get; set; } = new();
    public Scene Viewer { get; set; } = new();
    /// <summary>How much of the background shows through the bars (0 = solid bars ... 0.9 = nearly clear).</summary>
    public double SeeThrough { get; set; } = 0.35;

    public SceneLook Copy() => JsonSerializer.Deserialize<SceneLook>(JsonSerializer.Serialize(this))!;
    public Scene BarsScene => Mode == "One" ? All : Bars;
    public Scene WindowScene => Mode == "One" ? All : Window;
    public Scene ViewerScene => Mode == "One" ? All : Viewer;
}

/// <summary>The folder pop-outs (Select folders, NSFW, Very): the card itself.</summary>
public sealed class PanelLook
{
    /// <summary>"" = the theme's bar colour.</summary>
    public string Background { get; set; } = "";
    public double Opacity { get; set; } = 1;
    /// <summary>"" = the theme's accent.</summary>
    public string Border { get; set; } = "";
    public string Border2 { get; set; } = "";
    public double BorderWidth { get; set; } = 1.5;
    public double Radius { get; set; } = 12;
    public double Shadow { get; set; } = 0.6;
    public double Glow { get; set; } = 0.3;
    /// <summary>A small pointer from the card up to its button.</summary>
    public bool Notch { get; set; } = true;
    /// <summary>A title at the top of the card.</summary>
    public bool Title { get; set; } = true;
    public PanelLook Copy() => JsonSerializer.Deserialize<PanelLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>The folder pop-outs: the card, and the folder switches inside it (shown / hidden / partly shown).</summary>
public sealed class FolderLook
{
    public PanelLook Panel { get; set; } = new();
    /// <summary>The switches, as a button look: off = Off colours, on = "other" on colours, partly on = "content" on colours.</summary>
    public ButtonLook Toggles { get; set; } = DefaultToggles();

    public static ButtonLook DefaultToggles() => new()
    {
        Radius = 5, OffStyle = "Solid", OffA = "#2A2A30", OffB = "#2A2A30", Text = "#8D939A",
        OnStyle = "Solid", OtherA = "#3E7A48", OtherB = "#4B9058", OtherText = "#E4E4E8",
        ContentA = "#8A6A2A", ContentB = "#A07C31", ContentText = "#E4E4E8",
        Outline = "None", OutlineWidth = 1, Shine = 0, Glow = 0, Shadow = 0,
    };

    public FolderLook Copy() => JsonSerializer.Deserialize<FolderLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>How a Connected Art set is marked in the grid. Everything mixes.</summary>
public sealed class SetLook
{
    /// <summary>None, Solid, Gradient, Dashed, Glow.</summary>
    public string Outline { get; set; } = "Solid";
    public string ColourA { get; set; } = "#B89CFF";
    public string ColourB { get; set; } = "#FF7AC6";
    public double Width { get; set; } = 1.5;
    public double Radius { get; set; } = 4;
    /// <summary>How far the outline sits outside the pictures.</summary>
    public double Offset { get; set; } = 1.5;
    /// <summary>The pictures of a set touch (no gap between them; columns).</summary>
    public bool NoGap { get; set; }
    /// <summary>The set's name and size in its bottom-left corner.</summary>
    public bool Label { get; set; } = true;
    /// <summary>Little links on the seams between the pictures of a set.</summary>
    public bool Links { get; set; }
    /// <summary>A glassy tint over the set.</summary>
    public bool Glass { get; set; }
    public string GlassColour { get; set; } = "#B89CFF";
    public double GlassStrength { get; set; } = 0.16;
    /// <summary>A band of light crosses each set now and then.</summary>
    public bool Shine { get; set; }
    public double ShineEvery { get; set; } = 7;
    public double Glow { get; set; }

    public SetLook Copy() => JsonSerializer.Deserialize<SetLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>How favourites are marked in the grid, and where a picture's size and resolution show.</summary>
public sealed class FavLook
{
    /// <summary>A star on favourites (top-left corner).</summary>
    public bool Star { get; set; }
    /// <summary>The star button to add a favourite on the tile under the mouse (Favourites are also in the right-click menu and the viewer).</summary>
    public bool StarButton { get; set; }
    /// <summary>None, Gold, Solid, Gradient, Holographic.</summary>
    public string Outline { get; set; } = "Gold";
    public string ColourA { get; set; } = "#FFC83D";
    public string ColourB { get; set; } = "#FFF3B0";
    public double Width { get; set; } = 2;
    public double Glow { get; set; } = 0.45;
    /// <summary>The holographic card shine: a band of light that crosses favourites.</summary>
    public string Shine { get; set; } = "None";      // None, Hover (when the mouse is over it), Always (now and then)
    public double ShineEvery { get; set; } = 8;
    /// <summary>Picture details (resolution and size) on the tile under the mouse: None, Corner (top right), Strip (a small strip under the picture).</summary>
    public string Info { get; set; } = "Strip";
    public string InfoBack { get; set; } = "#000000";
    public double InfoOpacity { get; set; } = 0.78;
    public string InfoText { get; set; } = "#FFFFFF";
    public double InfoSize { get; set; } = 10.5;

    public FavLook Copy() => JsonSerializer.Deserialize<FavLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>The video outlines (paused and watched), kept together so they can be saved as one look.</summary>
public sealed class VideoOutlineLook
{
    public OutlineStyle Paused { get; set; } = OutlineStyle.PausedDefault();
    public OutlineStyle Watched { get; set; } = OutlineStyle.WatchedDefault();
}

/// <summary>
/// Looks saved under names of your own, one list per kind ("Buttons", "Logo", "Background", "Folders", "Sets", "Favourites",
/// "Video outlines"), in looks.json in Ginkgo's data folder. Themes keep their own files (see ThemeKit).
/// </summary>
public static class LookLibrary
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "looks.json");
    private static Dictionary<string, Dictionary<string, JsonNode?>>? _all;

    private static Dictionary<string, Dictionary<string, JsonNode?>> All
    {
        get
        {
            if (_all is not null) return _all;
            try
            {
                if (File.Exists(FilePath) && JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, JsonNode?>>>(File.ReadAllText(FilePath)) is { } d)
                    return _all = d;
            }
            catch { }
            return _all = new();
        }
    }

    private static void Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(All, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }

    public static List<string> Names(string kind) => All.TryGetValue(kind, out var d) ? d.Keys.OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase).ToList() : new();
    public static bool Has(string kind, string name) => All.TryGetValue(kind, out var d) && d.ContainsKey(name);

    public static T? Load<T>(string kind, string name) where T : class
    {
        try { return All.TryGetValue(kind, out var d) && d.TryGetValue(name, out var n) && n is not null ? n.Deserialize<T>() : null; }
        catch { return null; }
    }

    public static void Save<T>(string kind, string name, T look)
    {
        if (!All.TryGetValue(kind, out var d)) All[kind] = d = new(StringComparer.CurrentCultureIgnoreCase);
        d[name] = JsonSerializer.SerializeToNode(look);
        Write();
    }

    public static void Rename(string kind, string from, string to)
    {
        if (!All.TryGetValue(kind, out var d) || !d.Remove(from, out var n)) return;
        d[to] = n; Write();
    }

    public static void Delete(string kind, string name)
    {
        if (All.TryGetValue(kind, out var d) && d.Remove(name)) Write();
    }

    /// <summary>Whether a look is the same as the one saved under that name.</summary>
    public static bool Same<T>(string kind, string name, T look)
    {
        try
        {
            return All.TryGetValue(kind, out var d) && d.TryGetValue(name, out var n) && n is not null &&
                   JsonNode.DeepEquals(n, JsonSerializer.SerializeToNode(look));
        }
        catch { return false; }
    }

    /// <summary>Removes every saved look (Reset looks).</summary>
    public static void Reset() { _all = new(); try { File.Delete(FilePath); } catch { } }
}
