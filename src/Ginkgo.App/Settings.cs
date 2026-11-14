using System.IO;
using System.Text.Json;

namespace Ginkgo.App;

/// <summary>Everything the person has chosen. Saved as a small file next to the index; resetting it never touches the library.</summary>
public sealed class AppSettings
{
    public string LibraryRoot { get; set; } = @"E:\Pictures\IMGs";
    public string CacheDir { get; set; } = @"E:\GinkgoCache";
    /// <summary>The Favourites folder ("" = the folder named Favourites in the library).</summary>
    public string FavouritesFolder { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string FavRoot => string.IsNullOrWhiteSpace(FavouritesFolder) ? System.IO.Path.Combine(LibraryRoot, "Favourites") : FavouritesFolder;
    /// <summary>The small thumbnails are kept here, on the computer's own (fast) drive: a hard drive manages only a few dozen random lookups a second, which is what a shuffled grid needs.</summary>
    public string FastCacheDir { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "fast");
    public bool UseFastCache { get; set; }
    /// <summary>Keep the thumbnail files in memory (instant lookups), using free memory and leaving a reserve for other programs.</summary>
    public bool WarmCache { get; set; } = true;
    /// <summary>The most memory the thumbnails may use, in GB (0 = automatic: whatever is free, less a reserve).</summary>
    public double ThumbMemoryCapGB { get; set; }
    /// <summary>How many GIFs play at once in the grid (0 = all on screen).</summary>
    public int GifLimit { get; set; } = 20;
    /// <summary>The graphics card's memory for thumbnails: "Off", "Overflow" (when memory is full) or "GpuOnly".</summary>
    public string ThumbVramMode { get; set; } = "Overflow";
    /// <summary>The most graphics memory for thumbnails, in GB (0 = automatic: Windows' allowance).</summary>
    public double ThumbVramCapGB { get; set; }
    public bool ThumbVramOnBattery { get; set; }
    /// <summary>Keep the GIF versions (GPU grid) in memory / on the card too, and the most they may take (GB, 0 = automatic).</summary>
    public bool GifWarm { get; set; } = true;
    public double GifWarmCapGB { get; set; }
    /// <summary>The most the extra-large GIF versions may take on the drive (GB, 0 = no limit). The oldest go first.</summary>
    public double GifBigCapGB { get; set; }

    // GPU grid (see the GPU grid build brief). Off by default; the GIF settings apply only while it is on.
    /// <summary>"GPU" (the default since the GPU grid passed its tests) or "Current" (the strip renderer, kept as a fallback).</summary>
    public string Renderer { get; set; } = "GPU";        // the GPU grid (falls back to Current, with the reason, when the check fails)
    /// <summary>Times in a row Ginkgo did not close cleanly while the GPU grid was on; at 2 it starts with Current.</summary>
    public int GpuUncleanExits { get; set; }
    public bool GifAnimate { get; set; } = true;
    /// <summary>GPU grid: decoded pictures are kept only on the card (memory holds just what's waiting to be uploaded).</summary>
    public bool GpuCardOnly { get; set; } = true;
    public double GifLengthSec { get; set; } = 5;
    public bool GifCrossFade { get; set; } = true;
    public double GifCrossFadeSec { get; set; } = 0.3;
    /// <summary>0 = automatic (the governor), else a fixed rate.</summary>
    public int GifFps { get; set; }

    // content levels. NSFW and Very are never remembered: the app always starts with them off.
    public bool ShowGeneral { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public bool ShowNsfw { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ShowVery { get; set; }

    // what kinds of file are shown
    public bool ShowImages { get; set; } = true;
    public bool ShowGifs { get; set; } = true;
    public bool ShowVideos { get; set; } = true;

    /// <summary>
    /// Folders whose OWN files are hidden (paths relative to the library, NSFW and Very folders merged into the folder above, as
    /// Browse shows them). A folder's subfolders are separate entries.
    /// </summary>
    public List<string> OffFolders { get; set; } = new();
    /// <summary>1 = old rule (an off folder also hid everything inside it); 2 = each folder stands alone; 3 = as Browse shows folders.</summary>
    public int FolderRulesVersion { get; set; } = 3;
    /// <summary>Folders whose NSFW / Very switch is off (the folder holding the NSFW folder).</summary>
    public List<string> NsfwOff { get; set; } = new();
    public List<string> VeryOff { get; set; } = new();
    /// <summary>Subfolders shown as their own row in Select folders, with their own switch (their parent's switch leaves them alone).</summary>
    public List<string> Standalone { get; set; } = new();
    /// <summary>Folders kept at the bottom of Select folders: off unless switched on one by one, and left out of All on / All off.</summary>
    public List<string> RarelyUsed { get; set; } = new();

    // ---- filters and order (Home and Favourites; Browse keeps its own order per folder) ----
    public bool SortReverse { get; set; }
    public FilterSet Filters { get; set; } = new();
    /// <summary>Connected Art: sets with more images than this show as a stacked tile; sort sets by their first image for every sort.</summary>
    public int SetLimit { get; set; } = 5;
    public bool SetsByFirst { get; set; }
    /// <summary>Show a file once when the same file is in several of the folders shown.</summary>
    public bool HideCopies { get; set; } = true;
    public string FolderSort { get; set; } = "Name";
    public bool FolderSortReverse { get; set; }
    /// <summary>true = rows of equal height; false = equal-width columns.</summary>
    public bool RowsLayout { get; set; }
    /// <summary>The Grid layout (square cells of one size), which wins over <see cref="RowsLayout"/>; in it, pictures fit inside their cell or are cropped to fill it.</summary>
    public bool GridLayout { get; set; }
    public bool GridFit { get; set; }
    /// <summary>Space between tiles, in screen units.</summary>
    public float Spacing { get; set; } = 3;
    /// <summary>Size and dimensions in the corner of the tile under the mouse.</summary>
    public bool HoverDetails { get; set; } = true;

    // ---- slideshow ----
    public double SlideSeconds { get; set; } = 5;
    public bool SlideShuffle { get; set; }
    public bool SlideLoop { get; set; } = true;
    /// <summary>A video plays to its end before the slideshow moves on (otherwise it gets the same time as a picture).</summary>
    public bool SlideVideosToEnd { get; set; } = true;

    // ---- appearance, first run, library folders, things done at the next start ----
    public AppTheme Theme { get; set; } = new();
    /// <summary>Folders left out of the app: top-level names, or deeper paths ("Average & LQ\Real\Photos").</summary>
    public List<string> ExcludedFolders { get; set; } = Ginkgo.Core.MediaTypes.DefaultExcludedTopFolders.ToList();
    /// <summary>Done when Ginkgo next starts (before the index opens): "index" (rebuild it), "thumbnails" (make them again).</summary>
    public List<string> PendingResets { get; set; } = new();
    /// <summary>Move the cache to this folder when Ginkgo next starts ("" = nothing to do).</summary>
    public string PendingCacheMove { get; set; } = "";
    /// <summary>The library's old folder, when it was pointed somewhere new: the index's paths are moved at the next start.</summary>
    public string PendingRelocateFrom { get; set; } = "";

    // ---- Tools ----
    public string Tool { get; set; } = "Duplicates";
    public bool DupNear { get; set; }
    public int DupBits { get; set; } = 6;
    public double DupMinMB { get; set; }
    public bool DupVideos { get; set; } = true;
    public bool DupGifs { get; set; } = true;
    public string DupOrder { get; set; } = "Copies";
    /// <summary>Where copies count: Folder (the same folder only, not its subfolders), Top (within each top-level folder),
    /// All (anywhere in the library), Apart (only copies that sit in different folders).</summary>
    public string DupScope { get; set; } = "Top";
    public Ginkgo.Data.LowResRule LowRes { get; set; } = new();
    public List<Ginkgo.Data.LowResRule> LowResPresets { get; set; } = new();

    /// <summary>Each section's default view ("Home", "Browse", "Folder", "Favourites"), saved with Save as default.</summary>
    public Dictionary<string, ViewState> Defaults { get; set; } = new();
    public bool BarHidden { get; set; }
    /// <summary>The bottom bar is hidden too (second press of H).</summary>
    public bool StatusHidden { get; set; }

    // ---- videos (Quick Toggles) ----
    /// <summary>Hover previews play with sound (at the saved volume).</summary>
    public bool PreviewSound { get; set; }
    /// <summary>Resting the mouse on a video plays a preview.</summary>
    public bool HoverPreviews { get; set; } = true;
    /// <summary>Videos carry on from where they were left (paused, or closed part way); off: always from the start.</summary>
    public bool ResumeVideos { get; set; } = true;
    /// <summary>How many videos may play in the grid at once (1 to 5); starting one more pauses the one started longest ago.</summary>
    public int VideosAtOnce { get; set; } = 1;
    /// <summary>Videos clicked in the grid start without sound (the speaker on the tile turns it on).</summary>
    public bool GridStartMuted { get; set; }
    /// <summary>Outline round a paused video in the grid.</summary>
    public OutlineStyle PausedOutline { get; set; } = OutlineStyle.PausedDefault();
    /// <summary>Outline round a watched video in the grid.</summary>
    public OutlineStyle WatchedOutline { get; set; } = OutlineStyle.WatchedDefault();
    /// <summary>What counts as watched: "End" (played to 90% or more) or "Started" (any of it played).</summary>
    public string WatchedRule { get; set; } = "End";
    /// <summary>"Session": outlines and the Paused label are for videos watched / paused since Ginkgo opened. "Always": remembered between sessions.</summary>
    public string OutlineMemory { get; set; } = "Session";
    /// <summary>How the buttons look (shape, colours, outline, glow).</summary>
    public ButtonLook Look { get; set; } = new();
    /// <summary>The logo, the backgrounds, the folder pop-outs, how sets and favourites are marked (Appearance).</summary>
    public LogoLook Logo { get; set; } = new();
    public SceneLook Scenes { get; set; } = new();
    public FolderLook Folders { get; set; } = new();
    public SetLook SetStyle { get; set; } = new();
    public FavLook FavStyle { get; set; } = new();
    public ScrollLook ScrollBars { get; set; } = new();
    public TopBarLook TopBarStyle { get; set; } = new();
    public FontsLook Fonts { get; set; } = new();
    /// <summary>For each kind of look, the saved look it was last loaded from or saved to ("" = none).</summary>
    public Dictionary<string, string> LookNames { get; set; } = new();
    /// <summary>The slideshow: play GIFs through to the end before the next picture.</summary>
    public bool SlideGifsToEnd { get; set; } = true;

    // Browse
    public bool BrowseOwnOnly { get; set; }
    /// <summary>Folders shown as a grid of equal book-cover tiles (true) or in the picture layout (false).</summary>
    public bool BrowseCoverGrid { get; set; } = true;
    /// <summary>Rounded corners on covers, and how round (in screen units).</summary>
    public bool CoverRounded { get; set; } = true;
    public int CoverRadius { get; set; } = 8;
    /// <summary>Sort choices remembered for each folder ("" = the top of Browse).</summary>
    public Dictionary<string, BrowsePrefs> BrowseFolders { get; set; } = new();
    public List<string> BrowseDetails { get; set; } = new() { "Images", "Gifs", "Videos", "Size" };

    public string Sort { get; set; } = "FirstSeen";
    public float TileSize { get; set; } = 220;

    // speed and memory
    public int CacheProfile { get; set; } = AutoProfile;     // 0 = smallest ... 5 = largest, 6 = automatic
    public const int AutoProfile = 6;
    /// <summary>1 = memory choices were moved to "Automatic" once.</summary>
    public int MemoryRulesVersion { get; set; } = 1;
    public int FontsVersion { get; set; } = 1;
    public int LookAheadWindows { get; set; } = 1;
    public bool FadeSharpening { get; set; } = true;

    /// <summary>True once settings have been saved on this computer (false: first run).</summary>
    public static bool Exists => File.Exists(FilePath);

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "settings.json");
    /// <summary>Every copy into Favourites is recorded here.</summary>
    public static string FavouritesLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "favourites-log.txt");
    public static string IndexPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "index.db");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string text = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(text) ?? new();
                if (!text.Contains("FolderRulesVersion")) s.FolderRulesVersion = 1;
                // sorts are now a field plus a direction
                (s.Sort, bool r) = SortTag(s.Sort); if (r) s.SortReverse = true;
                foreach (var bp in s.BrowseFolders.Values) { (bp.FileSort, bool br) = SortTag(bp.FileSort); if (br) bp.FileReverse = true; }
                if (!text.Contains("MemoryRulesVersion")) { if (s.CacheProfile == 0) s.CacheProfile = AutoProfile; s.MemoryRulesVersion = 1; }
                // Ginkgo now brings its own fonts: Inter is the default (a font picked by hand stays)
                if (!text.Contains("FontsVersion")) { if (s.Theme.Font is "Segoe UI" or "") s.Theme.Font = "Inter"; s.FontsVersion = 1; }
                return s;
            }
        }
        catch { }
        return new();
    }

    /// <summary>Old sort names: "SizeDesc" is Size, "SizeAsc" is Size reversed, "Largest" is Megapixels.</summary>
    public static (string sort, bool reverse) SortTag(string old) => old switch
    {
        "SizeDesc" => ("Size", false), "SizeAsc" => ("Size", true), "Largest" => ("Megapixels", false), _ => (old, false),
    };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(FilePath + ".tmp", FilePath, true);
        }
        catch { }
    }

    /// <summary>Picture-cache and compressed-cache budgets in MB for each profile.</summary>
    public static readonly (string name, int decoded, int encoded)[] CacheProfiles =
    {
        ("Smallest (96 / 48 MB)", 96, 48), ("Small (128 / 64 MB)", 128, 64), ("Medium (200 / 150 MB)", 200, 150),
        ("Large (320 / 200 MB)", 320, 200), ("Larger (512 / 256 MB)", 512, 256), ("Largest (768 / 384 MB)", 768, 384),
        ("Automatic (uses free memory)", -1, -1),
    };
}

public sealed class BrowsePrefs
{
    public string FolderSort { get; set; } = "Name";
    public bool FolderReverse { get; set; }
    public string FileSort { get; set; } = "Name";
    public bool FileReverse { get; set; }
}
