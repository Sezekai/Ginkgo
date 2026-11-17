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
    public double Size { get; set; } = 40;
    /// <summary>The logo spreads over the title bar and the top bar together (the bar's own size in full screen).</summary>
    public bool Big { get; set; } = true;
    /// <summary>A rounded tile behind the logo ("" colour = the theme's field colour).</summary>
    public bool Tile { get; set; }
    public string TileColour { get; set; } = "";
    /// <summary>The box behind the logo: "Square" (rounded corners as set), "Circle" (round, like the section switch).</summary>
    public string TileShape { get; set; } = "Square";
    /// <summary>"Colour" (TileColour, or the theme's field colour), "Gradient" (TileColour to TileColourB), "Accent", "Buttons" (the section buttons'
    /// colours), "Switch" (the same as the section switch's back).</summary>
    public string TileFill { get; set; } = "Colour";
    public string TileColourB { get; set; } = "#C9A8FF";
    public double TileRadius { get; set; } = 0.24;
    /// <summary>Room between the box's edge and the logo, as a share of its size.</summary>
    public double TilePadding { get; set; } = 0.14;
    public string TileOutline { get; set; } = "";
    public double TileOutlineWidth { get; set; } = 1;

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
    /// <summary>"Window": the bars' background is spread over the whole window and the bars show their part of it. "Bars": the
    /// whole background is fitted into the bars themselves (the top bar and the bottom bar), so its pattern is closer together.</summary>
    public string BarsFit { get; set; } = "Window";

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

/// <summary>The pictures' scroll bar: it shows when the mouse comes near the window's right edge. Every scroll bar is see-through
/// with an outline; this one can also glow, pulse or have a shine run along it so it is easy to find.</summary>
public sealed class ScrollLook
{
    public double Width { get; set; } = 14;
    /// <summary>How long the handle is: it grows with the picture size, times this.</summary>
    public double Length { get; set; } = 1;
    /// <summary>None, Glow, Pulse (a glow that breathes), Shine (a light runs along it), PulseShine.</summary>
    public string Effect { get; set; } = "Pulse";
    /// <summary>"" = the theme's accent.</summary>
    public string Colour { get; set; } = "";
    /// <summary>How much the handle is filled in (0 = just the outline).</summary>
    public double Fill { get; set; } = 0.08;
    /// <summary>How near the right edge the mouse must be for it to show (a share of the window's width).</summary>
    public double Reach { get; set; } = 0.05;
    public double OutlineWidth { get; set; } = 1.5;

    public ScrollLook Copy() => JsonSerializer.Deserialize<ScrollLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>The top bar's own layout: where and how big the logo, the section switch and the section's name are, the logo's
/// small place in the title bar while the bar is hidden, and the colours of the switch's icons and the text. Sizes of 0 mean
/// "fit the bars".</summary>
public sealed class TopBarLook
{
    // the big logo (over the title bar and the top bar)
    public double LogoSize { get; set; }
    public double LogoX { get; set; } = 20;
    public double LogoY { get; set; } = 7;
    // the logo while the top bar is hidden: small, in the title bar
    public double SmallSize { get; set; }
    public double SmallX { get; set; } = 10;
    public double SmallY { get; set; }
    /// <summary>How long the logo takes to shrink or grow, in milliseconds.</summary>
    public double Speed { get; set; } = 280;
    // the section switch
    public double SwitchHeight { get; set; } = 44;
    public double SwitchGap { get; set; } = 14;
    public double SwitchY { get; set; }
    public double IconScale { get; set; } = 1;
    /// <summary>"Round" (a pill) or "Square" (corners rounded by SwitchRadius, a share of its height).</summary>
    public string SwitchShape { get; set; } = "Round";
    public double SwitchRadius { get; set; } = 0.25;
    /// <summary>How wide each icon's space is, against the switch's height.</summary>
    public double SwitchWidth { get; set; } = 1;
    /// <summary>The logo the same height as the switch, beside it in the top bar.</summary>
    public bool MatchLogo { get; set; }
    public string IconColour { get; set; } = "";
    public string IconHoverColour { get; set; } = "";
    public string IconOnColour { get; set; } = "";
    // the section's name
    public double TitleSize { get; set; } = 24;
    public double TitleGap { get; set; } = 14;
    public double TitleY { get; set; }
    public bool TitleBold { get; set; } = true;
    public string TitleColour { get; set; } = "";
    // the window's name ("Ginkgo") in the title bar
    public double NameSize { get; set; } = 12;
    public double NameGap { get; set; } = 12;
    public string NameColour { get; set; } = "";

    // the title bar
    public double TitleHeight { get; set; } = 29;
    /// <summary>"Line": a line under the title bar, starting after the logo. "None": no line.</summary>
    public string Separator { get; set; } = "Line";
    public double LineGap { get; set; } = 8;
    public string LineColour { get; set; } = "";
    // the window name's own nudges: with the whole bar showing, and with only the title bar (while scrolling)
    public double NameX { get; set; }
    public double NameY { get; set; }
    public double SmallNameX { get; set; }
    public double SmallNameY { get; set; }
    // the window buttons (minimise, maximise, close)
    public bool CaptionOutline { get; set; }
    public string CaptionOutlineColour { get; set; } = "";
    public double CaptionOutlineWidth { get; set; } = 1;
    public double CaptionRadius { get; set; } = 6;
    public double CaptionWidth { get; set; } = 42;
    /// <summary>0: the title bar's full height.</summary>
    public double CaptionHeight { get; set; }
    public double CaptionIcon { get; set; } = 9;
    /// <summary>Space between the buttons and the window's top-right corner.</summary>
    public double CaptionInset { get; set; }
    // the controls under it
    public double ControlsY { get; set; }
    public double SpaceAbove { get; set; } = 10;
    public double SpaceBelow { get; set; } = 10;

    public TopBarLook Copy() => JsonSerializer.Deserialize<TopBarLook>(JsonSerializer.Serialize(this))!;
}

/// <summary>One part of the app's text: its font ("" = the theme's font) and size (0 = its usual size).</summary>
public sealed class FontPick
{
    public string Font { get; set; } = "";
    public double Size { get; set; }
}

/// <summary>A font and size for each part of the app separately (Appearance → Text). Anything left alone follows the theme.</summary>
public sealed class FontsLook
{
    public Dictionary<string, FontPick> Parts { get; set; } = new();

    public FontPick Get(string key) => Parts.TryGetValue(key, out var p) ? p : Parts[key] = new FontPick();

    /// <summary>The parts, in the order the page lists them: key, name, usual size, smallest, largest, a note.</summary>
    public static readonly (string key, string name, double usual, double min, double max, string note)[] All =
    {
        ("Section", "Section name (beside the switch)", 24, 10, 48, ""),
        ("Window", "Window name (\"Ginkgo\" in the title bar)", 12, 8, 24, ""),
        ("Buttons", "Button text", 0, 8, 20, "The top bar's buttons and every other button."),
        ("Switches", "Folder switches", 0, 8, 20, "The folder names in the folder pop-outs."),
        ("Popouts", "Pop-outs", 0, 8, 20, "Text in the folder, filter, views and slideshow pop-outs."),
        ("Path", "Browse's folder path", 0, 8, 22, ""),
        ("Grid", "Labels on the pictures", 11, 7, 18, "Set names, folder names and the size strip under a picture."),
        ("Status", "Status bar", 0, 8, 18, ""),
        ("Pages", "Appearance and Settings", 0, 9, 18, ""),
        ("Viewer", "Viewer text", 12, 8, 22, "The picture's details and the messages that show over it."),
        ("Menus", "Right-click menus", 0, 8, 20, ""),
        ("Tooltips", "Tooltips", 0, 8, 18, ""),
    };

    public FontsLook Copy() => JsonSerializer.Deserialize<FontsLook>(JsonSerializer.Serialize(this))!;
}
