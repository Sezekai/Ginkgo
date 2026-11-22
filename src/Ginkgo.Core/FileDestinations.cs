namespace Ginkgo.Core;

/// <summary>
/// Where the file operations put things. Folders are relative to the library (the IMGs folder), with \ between parts.
/// Content levels follow <see cref="ContentClassifier"/>: an NSFW folder makes NSFW, and a Very folder directly inside an NSFW folder makes Very.
/// Nothing here touches the disk; <paramref name="exists"/> callbacks say what is already there.
/// </summary>
public static class FileDestinations
{
    public const string SortFolder = "Sort";
    public const string FindHigherQuality = "Find Higher Quality";
    public const string Upscale = "Upscale";
    public const string Combine = "Combine - Join Together";
    public const string Average = "Average & LQ";

    /// <summary>The spellings used when a level folder has to be made (an existing one with another spelling is used instead).</summary>
    public static readonly string[] NsfwSpellings = { "NSFW", "[NSFW]", "nsfw" };
    public static readonly string[] VerySpellings = { "Very", "VERY", "[Very]", "[VERY]" };

    public static string[] Parts(string rel) => rel.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
    public static string Join(IEnumerable<string> parts) => string.Join("\\", parts);

    /// <summary>The folder with any level folders at its end taken off ("Anime\NSFW\Very" → "Anime").</summary>
    public static string LevelBase(string rel)
    {
        var p = Parts(rel).ToList();
        while (p.Count > 0)
        {
            string last = p[^1];
            string? parent = p.Count > 1 ? p[^2] : null;
            if (ContentClassifier.IsVeryFolder(last, parent) || ContentClassifier.IsNsfwFolderName(last)) p.RemoveAt(p.Count - 1);
            else break;
        }
        return Join(p);
    }

    /// <summary>
    /// The folder a file in <paramref name="rel"/> goes to so that it has the content level <paramref name="want"/>, in the same place:
    /// the level folders at the end of its folder are changed (made if needed). Null when that can't be done there (it already has
    /// that level, or a folder further up already makes it a higher level).
    /// </summary>
    public static string? ForLevel(string rel, ContentLevel want, Func<string, bool> exists)
    {
        var now = ContentClassifier.ClassifyRelativeFolder(rel);
        if (now == want) return null;
        string b = LevelBase(rel);
        var baseLevel = ContentClassifier.ClassifyRelativeFolder(b);
        if (want < baseLevel) return null;
        return WithLevel(b, want, baseLevel, exists);
    }

    /// <summary><paramref name="baseRel"/> with the level folders that make <paramref name="want"/> added (using spellings already there).</summary>
    public static string WithLevel(string baseRel, ContentLevel want, Func<string, bool> exists) =>
        WithLevel(baseRel, want, ContentClassifier.ClassifyRelativeFolder(baseRel), exists);

    private static string WithLevel(string baseRel, ContentLevel want, ContentLevel baseLevel, Func<string, bool> exists)
    {
        if (want <= baseLevel) return baseRel;
        string nsfw = Pick(baseRel, NsfwSpellings, exists);
        string n = Combine2(baseRel, nsfw);
        if (want == ContentLevel.Nsfw) return n;
        return Combine2(n, Pick(n, VerySpellings, exists));
    }

    private static string Pick(string parent, string[] spellings, Func<string, bool> exists)
    {
        foreach (var s in spellings) if (exists(Combine2(parent, s))) return s;
        return spellings[0];
    }

    private static string Combine2(string a, string b) => a.Length == 0 ? b : a + "\\" + b;

    /// <summary>Sort\Find Higher Quality or Sort\Upscale, then the folder the file came from (with its level folders).</summary>
    public static string SortTarget(string bucket, string rel) => Combine2(Combine2(SortFolder, bucket), rel);

    /// <summary>"Anime Style\NSFW" → "Anime Style - NSFW" (the library's own folder is "Library").</summary>
    public static string CombineName(string rel)
    {
        var p = Parts(rel);
        return p.Length == 0 ? "Library" : string.Join(" - ", p);
    }

    /// <summary>The next free numbered folder for Combine: Sort\Combine - Join Together\Anime Style - NSFW 1, then 2, …</summary>
    public static string CombineTarget(string rel, Func<string, bool> exists)
    {
        string root = Combine2(SortFolder, Combine), name = CombineName(rel);
        for (int i = 1; ; i++)
        {
            string t = Combine2(root, $"{name} {i}");
            if (!exists(t)) return t;
        }
    }

    /// <summary>
    /// Average &amp; LQ (or a folder inside it): the file goes into its level folder there when it is NSFW or Very, so it keeps its rating.
    /// </summary>
    public static string AverageTarget(string sub, ContentLevel level, Func<string, bool> exists)
    {
        string b = sub.Length == 0 ? Average : (sub.StartsWith(Average, StringComparison.OrdinalIgnoreCase) ? sub : Combine2(Average, sub));
        return WithLevel(b, level, exists);
    }

    /// <summary>A name that isn't taken in a folder: "a.jpg" → "a (2).jpg", "a (3).jpg", …</summary>
    public static string FreeName(string fileName, Func<string, bool> taken)
    {
        if (!taken(fileName)) return fileName;
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            string n = $"{stem} ({i}){ext}";
            if (!taken(n)) return n;
        }
    }

    /// <summary>True when every file is in the same folder (Combine needs that).</summary>
    public static bool SameFolder(IEnumerable<string> relFolders) =>
        relFolders.Select(f => f.Replace('/', '\\').TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;

    /// <summary>Whether a folder (relative) is a level folder, by its own name and its parent's.</summary>
    public static bool IsLevelFolder(string rel)
    {
        var p = Parts(rel);
        if (p.Length == 0) return false;
        return ContentClassifier.IsLevelFolder(p[^1], p.Length > 1 ? p[^2] : null) || VerySpellings.Contains(p[^1]);
    }
}
