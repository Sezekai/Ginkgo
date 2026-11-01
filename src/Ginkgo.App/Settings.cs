using System.IO;
using System.Text.Json;

namespace Ginkgo.App;

/// <summary>Everything the person has chosen. Saved as a small file next to the index; resetting it never touches the library.</summary>
public sealed class AppSettings
{
    public string LibraryRoot { get; set; } = @"E:\Pictures\IMGs";
    public string CacheDir { get; set; } = @"E:\GinkgoCache";
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

    // GPU grid (see the GPU grid build brief). Off by default; the GIF settings apply only while it is on.
    /// <summary>"Current" (the strip renderer) or "GPU".</summary>
    public string Renderer { get; set; } = "Current";
    /// <summary>Times in a row Ginkgo did not close cleanly while the GPU grid was on; at 2 it starts with Current.</summary>
    public int GpuUncleanExits { get; set; }
    public bool GifAnimate { get; set; } = true;
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

    /// <summary>Folders whose OWN files are hidden (paths relative to the library). A folder's subfolders are separate entries.</summary>
    public List<string> OffFolders { get; set; } = new();
    /// <summary>1 = old rule (an off folder also hid everything inside it); 2 = each folder stands alone.</summary>
    public int FolderRulesVersion { get; set; } = 2;
    public string FolderSort { get; set; } = "Name";
    public bool FolderSortReverse { get; set; }
    /// <summary>true = rows of equal height; false = equal-width columns.</summary>
    public bool RowsLayout { get; set; }
    public bool BarHidden { get; set; }
    /// <summary>The bottom bar is hidden too (second press of H).</summary>
    public bool StatusHidden { get; set; }
    /// <summary>How the buttons look (shape, colours, outline, glow).</summary>
    public ButtonLook Look { get; set; } = new();

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
    public int LookAheadWindows { get; set; } = 1;
    public bool FadeSharpening { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "settings.json");
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
                if (!text.Contains("MemoryRulesVersion")) { if (s.CacheProfile == 0) s.CacheProfile = AutoProfile; s.MemoryRulesVersion = 1; }
                return s;
            }
        }
        catch { }
        return new();
    }

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
}
