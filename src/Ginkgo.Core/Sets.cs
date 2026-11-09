namespace Ginkgo.Core;

/// <summary>
/// Connected Art: each folder of related images inside the Connected Art folder is a set (NSFW and Very folders in between don't
/// count, so Connected Art\NSFW\Very\Story is the set "Story"). Files lying directly in Connected Art are ordinary files.
/// </summary>
public static class ConnectedArt
{
    public const string FolderName = "Connected Art";

    /// <summary>The set a file belongs to, from its folder relative to the library: the set's own folder and name, or null.</summary>
    public static (string key, string name)? SetOf(string relFolder)
    {
        if (string.IsNullOrEmpty(relFolder)) return null;
        var parts = relFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals(FolderName, StringComparison.OrdinalIgnoreCase)) return null;
        string? prev = parts[0];
        for (int k = 1; k < parts.Length; k++)
        {
            if (ContentClassifier.IsLevelFolder(parts[k], prev)) { prev = parts[k]; continue; }
            return (string.Join('\\', parts, 0, k + 1), parts[k]);
        }
        return null;
    }
}

public static class LevelOwner
{
    /// <summary>
    /// The folder whose NSFW / Very switch a file follows: the folder holding the NSFW folder it is in (as Browse shows paths).
    /// "" for the library itself; null for a General file.
    /// </summary>
    public static string? Of(string relFolder)
    {
        if (string.IsNullOrEmpty(relFolder)) return null;
        var parts = relFolder.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k < parts.Length; k++)
            if (ContentClassifier.IsNsfwFolderName(parts[k])) return FolderPaths.Display(string.Join('\\', parts, 0, k));
        return null;
    }
}

/// <summary>File names in the order people expect: "page 2" before "page 10", case ignored.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            char ca = a[i], cb = b[j];
            if (char.IsDigit(ca) && char.IsDigit(cb))
            {
                int si = i, sj = j;
                while (si < a.Length && a[si] == '0') si++;
                while (sj < b.Length && b[sj] == '0') sj++;
                int ei = si, ej = sj;
                while (ei < a.Length && char.IsDigit(a[ei])) ei++;
                while (ej < b.Length && char.IsDigit(b[ej])) ej++;
                int la = ei - si, lb = ej - sj;
                if (la != lb) return la.CompareTo(lb);
                for (int k = 0; k < la; k++) if (a[si + k] != b[sj + k]) return a[si + k].CompareTo(b[sj + k]);
                int za = si - i, zb = sj - j;                     // same number: fewer leading zeros first
                if (za != zb) return za.CompareTo(zb);
                i = ei; j = ej;
                continue;
            }
            int c = char.ToUpperInvariant(ca).CompareTo(char.ToUpperInvariant(cb));
            if (c != 0) return c;
            i++; j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
