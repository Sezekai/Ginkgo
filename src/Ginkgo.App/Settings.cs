using System.IO;
using System.Text.Json;

namespace Ginkgo.App;

/// <summary>Everything the person has chosen. Saved as a small file next to the index; resetting it never touches the library.</summary>
public sealed class AppSettings
{
    public string LibraryRoot { get; set; } = @"E:\Pictures\IMGs";
    public string CacheDir { get; set; } = @"E:\GinkgoCache";

    // content levels: General is always shown; these are the two global switches
    public bool ShowNsfw { get; set; }
    public bool ShowVery { get; set; }

    // what kinds of file are shown
    public bool ShowImages { get; set; } = true;
    public bool ShowGifs { get; set; } = true;

    /// <summary>Folders switched off in Select folders (paths relative to the library). A folder inside an off folder is off too.</summary>
    public List<string> OffFolders { get; set; } = new();

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
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
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
