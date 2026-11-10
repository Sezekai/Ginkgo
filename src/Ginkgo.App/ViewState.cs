using System.IO;
using System.Text.Json;

namespace Ginkgo.App;

/// <summary>
/// A whole view as it was: section and folder, the folder choices and content switches, filters, order and how the grid looks.
/// Saved searches keep all of it. Defaults keep how a section looks and sorts (folder choices and switches are left as they are).
/// A null field leaves that part of the view as it is.
/// </summary>
public sealed class ViewState
{
    public string Name { get; set; } = "";
    public string Section { get; set; } = "Home";
    public string Folder { get; set; } = "";

    // what is shown
    public bool? General { get; set; }
    public bool? Nsfw { get; set; }
    public bool? Very { get; set; }
    public bool? Images { get; set; }
    public bool? Gifs { get; set; }
    public bool? Videos { get; set; }
    public List<string>? OffFolders { get; set; }
    public List<string>? NsfwOff { get; set; }
    public List<string>? VeryOff { get; set; }
    public List<string>? Standalone { get; set; }
    public List<string>? RarelyUsed { get; set; }
    public string? Search { get; set; }
    public FilterSet? Filters { get; set; }

    // order
    public string? Sort { get; set; }
    public bool? Reverse { get; set; }
    public int? RandomSeed { get; set; }

    // how it looks
    public string? Layout { get; set; }          // "Masonry", "Rows" or "Grid"
    public bool? GridFit { get; set; }
    public float? TileSize { get; set; }
    public float? Spacing { get; set; }

    // Browse
    public List<string>? BrowseDetails { get; set; }
    public bool? CoverGrid { get; set; }
    public bool? OwnOnly { get; set; }
    public string? FolderSort { get; set; }
    public bool? FolderReverse { get; set; }

    // a section's place in this session (not kept in saved searches or defaults)
    public double? Offset { get; set; }
    public List<string>? OpenSets { get; set; }

    public ViewState Copy() => JsonSerializer.Deserialize<ViewState>(JsonSerializer.Serialize(this))!;

    /// <summary>The section a default belongs to: Home, Browse (the folders at the top), Folder (inside a folder) or Favourites.</summary>
    public static string DefaultKey(string section, string folder) => section == "Browse" ? (folder.Length == 0 ? "Browse" : "Folder") : section;

    /// <summary>Ginkgo's own defaults: newest first in columns for Home and Favourites; folders by name in Browse; inside a folder, A to Z.</summary>
    public static ViewState BuiltIn(string key) => key switch
    {
        "Browse" => new ViewState { Section = "Browse", FolderSort = "Name", FolderReverse = false, Search = "", Filters = new(), Images = true, Gifs = true, Videos = true },
        "Folder" => new ViewState { Section = "Browse", Sort = "Name", Reverse = false, Search = "", Filters = new(), Images = true, Gifs = true, Videos = true, Layout = "Masonry", Spacing = 3 },
        _ => new ViewState { Section = key, Sort = "FirstSeen", Reverse = false, Search = "", Filters = new(), Images = true, Gifs = true, Videos = true, Layout = "Masonry", Spacing = 3 },
    };
}

/// <summary>The saved searches, in their own file in Ginkgo's data folder (in the order the person put them).</summary>
public static class SavedSearches
{
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ginkgo", "saved-searches.json");

    public static List<ViewState> Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<List<ViewState>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public static void Save(List<ViewState> list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { }
    }
}
