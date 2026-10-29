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
    /// <summary>Read the thumbnail files once in the background so Windows keeps them in its file cache (instant lookups afterwards).</summary>
    public bool WarmCache { get; set; } = true;

    // content levels. NSFW and Very are never remembered: the app always starts with them off.
    public bool ShowGeneral { get; set; } = true;
    [System.Text.Json.Serialization.JsonIgnore] public bool ShowNsfw { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool ShowVery { get; set; }

    // what kinds of file are shown
    public bool ShowImages { get; set; } = true;
    public bool ShowGifs { get; set; } = true;

    /// <summary>Folders whose OWN files are hidden (paths relative to the library). A folder's subfolders are separate entries.</summary>
    public List<string> OffFolders { get; set; } = new();
    /// <summary>1 = old rule (an off folder also hid everything inside it); 2 = each folder stands alone.</summary>
    public int FolderRulesVersion { get; set; } = 2;
    public string FolderSort { get; set; } = "Name";
    public bool FolderSortReverse { get; set; }
    /// <summary>true = rows of equal height; false = equal-width columns.</summary>
    public bool RowsLayout { get; set; }
    public bool BarHidden { get; set; }

    public string Sort { get; set; } = "FirstSeen";
    public float TileSize { get; set; } = 220;

    // speed and memory
    public int CacheProfile { get; set; }      // 0 = smallest ... 5 = largest
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
    };
}
