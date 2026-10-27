using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Ginkgo.Data;

/// <summary>Gives a file or folder an identity that survives moves and renames (the NTFS file ID on Windows).</summary>
public interface IFileIdProvider
{
    /// <summary>The identity as text, or null if it can't be read (file gone, no permission, a drive that has no IDs).</summary>
    string? GetFileId(string fullPath, bool isDirectory);
}

public static class FileIdProviders
{
    /// <summary>The real NTFS IDs on Windows; nothing elsewhere (moves are then treated as delete + add).</summary>
    public static IFileIdProvider? Default { get; } = OperatingSystem.IsWindows() ? new WindowsFileIds() : null;
}

public sealed class WindowsFileIds : IFileIdProvider
{
    private const uint FILE_SHARE_ALL = 7;                  // read | write | delete: never get in anybody's way
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;   // needed to open a folder
    private const int FileIdInfoClass = 18;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_INFO { public ulong VolumeSerialNumber; public ulong IdLow; public ulong IdHigh; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes; public long CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int cls, out FILE_ID_INFO info, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle h, out BY_HANDLE_FILE_INFORMATION info);

    public string? GetFileId(string fullPath, bool isDirectory)
    {
        try
        {
            string p = fullPath.Length > 240 && !fullPath.StartsWith(@"\\?\") ? @"\\?\" + fullPath : fullPath;
            using var h = CreateFileW(p, 0, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return null;
            if (GetFileInformationByHandleEx(h, FileIdInfoClass, out var info, (uint)Marshal.SizeOf<FILE_ID_INFO>()))
                return $"{info.VolumeSerialNumber:x}-{info.IdHigh:x}{info.IdLow:x16}";
            if (GetFileInformationByHandle(h, out var old))
                return $"{old.VolumeSerialNumber:x}-{old.FileIndexHigh:x8}{old.FileIndexLow:x8}";
            return null;
        }
        catch { return null; }
    }
}

/// <summary>Fills in the IDs of files and folders that were indexed before IDs were tracked (or whose ID couldn't be read at the time).</summary>
public static class FileIdBackfill
{
    /// <param name="progress">(done, total) after every batch.</param>
    public static int Run(IndexDb db, string root, IFileIdProvider? provider, Action<int, int>? progress, CancellationToken ct)
    {
        if (provider is null) return 0;
        int done = 0;
        using var c = db.Open();
        var work = new List<(string table, bool isDir, List<(long id, string path)> rows)>();
        foreach (var (table, isDir) in new[] { ("files", false), ("folders", true) })
        {
            var todo = new List<(long id, string path)>();
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = $"SELECT id, path FROM {table} WHERE file_id IS NULL AND missing_since=0";
                using var r = cmd.ExecuteReader();
                while (r.Read()) todo.Add((r.GetInt64(0), r.GetString(1)));
            }
            todo.Sort((a, b) => string.CompareOrdinal(a.path, b.path));      // folder by folder: the drive moves the least this way
            work.Add((table, isDir, todo));
        }
        int total = work.Sum(w => w.rows.Count);
        progress?.Invoke(0, total);
        foreach (var (table, isDir, todo) in work)
        {
            for (int at = 0; at < todo.Count; at += 1000)
            {
                ct.ThrowIfCancellationRequested();
                var batch = todo.GetRange(at, Math.Min(1000, todo.Count - at));
                var ids = new string?[batch.Count];
                Parallel.For(0, batch.Count, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, i =>
                    ids[i] = provider.GetFileId(isDir ? Path.Combine(root, batch[i].path) : batch[i].path, isDir));
                using var tx = c.BeginTransaction();
                using var up = c.CreateCommand();
                up.Transaction = tx;
                up.CommandText = $"UPDATE {table} SET file_id=$f WHERE id=$id AND file_id IS NULL";
                var pf = up.Parameters.Add("$f", Microsoft.Data.Sqlite.SqliteType.Text);
                var pi = up.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);
                for (int i = 0; i < batch.Count; i++)
                    if (ids[i] is not null) { pf.Value = ids[i]; pi.Value = batch[i].id; up.ExecuteNonQuery(); done++; }
                tx.Commit();
                progress?.Invoke(Math.Min(total, at + batch.Count + (table == "folders" ? work[0].rows.Count : 0)), total);
            }
        }
        return done;
    }
}
