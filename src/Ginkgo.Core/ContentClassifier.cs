namespace Ginkgo.Core;

/// <summary>
/// Decides a file's content level from the names of the folders above it.
/// Only the exact spellings in the spec count; anything else is an ordinary folder.
/// </summary>
public static class ContentClassifier
{
    private static readonly HashSet<string> NsfwNames = new(StringComparer.Ordinal)
    {
        "NSFW", "[NSFW]", "nsfw",
    };

    private static readonly HashSet<string> VeryNames = new(StringComparer.Ordinal)
    {
        "Very", "VERY", "[Very]", "[VERY]",
    };

    public static bool IsNsfwFolderName(string name) => NsfwNames.Contains(name);

    /// <summary>A Very-named folder only means Very when its parent is an NSFW folder.</summary>
    public static bool IsVeryFolder(string name, string? parentName) =>
        VeryNames.Contains(name) && parentName is not null && NsfwNames.Contains(parentName);

    /// <summary>
    /// True for folders that act as content levels (and so are not shown as folder tiles):
    /// every NSFW folder, and a Very folder directly inside an NSFW folder.
    /// </summary>
    public static bool IsLevelFolder(string name, string? parentName) =>
        IsNsfwFolderName(name) || IsVeryFolder(name, parentName);

    /// <summary>
    /// Level of a file given the folder names from the library root down to the file's folder
    /// (the root itself and the file name are not included).
    /// </summary>
    public static ContentLevel Classify(IReadOnlyList<string> folderSegments)
    {
        var level = ContentLevel.General;
        string? previous = null;
        foreach (var segment in folderSegments)
        {
            if (IsVeryFolder(segment, previous))
            {
                level = ContentLevel.Very; // Very is the highest level and is inherited by everything below it.
            }
            else if (level == ContentLevel.General && IsNsfwFolderName(segment))
            {
                level = ContentLevel.Nsfw;
            }
            previous = segment;
        }
        return level;
    }

    /// <summary>Convenience overload for a path relative to the library root, using / or \ separators.</summary>
    public static ContentLevel ClassifyRelativeFolder(string relativeFolderPath)
    {
        if (string.IsNullOrEmpty(relativeFolderPath)) return ContentLevel.General;
        var parts = relativeFolderPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        return Classify(parts);
    }
}
