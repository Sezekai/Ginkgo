namespace Ginkgo.Core;

public enum MediaKind : byte { Image = 0, Gif = 1, Video = 2 }

public static class MediaTypes
{
    private static readonly Dictionary<string, MediaKind> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".jpg"] = MediaKind.Image, [".jpeg"] = MediaKind.Image, [".jfif"] = MediaKind.Image,
        [".png"] = MediaKind.Image, [".webp"] = MediaKind.Image, [".avif"] = MediaKind.Image,
        [".gif"] = MediaKind.Gif,
        [".mp4"] = MediaKind.Video, [".webm"] = MediaKind.Video, [".mkv"] = MediaKind.Video,
        [".avi"] = MediaKind.Video, [".mov"] = MediaKind.Video, [".m4v"] = MediaKind.Video,
        [".wmv"] = MediaKind.Video, [".flv"] = MediaKind.Video,
    };

    /// <summary>Extension must include the leading dot.</summary>
    public static bool TryGetKind(string extension, out MediaKind kind) => Map.TryGetValue(extension, out kind);

    public static readonly string[] DefaultExcludedTopFolders =
        { "Doujinshi", "Sort", "sfw", "Anthologies, Art Books & Collections", "Grabber" };
}
