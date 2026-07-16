using System.IO;

namespace Obsync.App.Services;

/// <summary>
/// Size arithmetic for the Settings → Network &amp; storage card and the state-database diagnostic.
/// All methods are best-effort: entries that vanish or deny access mid-walk are skipped, never thrown,
/// because storage health must not fail on a workspace being rewritten by a running sync.
/// </summary>
public static class StorageUsage
{
    /// <summary>Total size of all files under <paramref name="path"/>, or 0 when it does not exist.</summary>
    public static long DirectorySizeBytes(string path)
    {
        if (!Directory.Exists(path))
        {
            return 0;
        }

        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception)
                    {
                        // Deleted or locked mid-walk — skip it.
                    }
                }

                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    pending.Push(child);
                }
            }
            catch (Exception)
            {
                // Inaccessible directory — skip the subtree.
            }
        }

        return total;
    }

    /// <summary>The file's size, or null when it does not exist or cannot be read.</summary>
    public static long? FileSizeBytes(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? file.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Free space on the drive holding <paramref name="path"/>, or null when unknown.</summary>
    public static long? FreeDiskBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Human-readable size: "812 B", "3.4 KB", "1.2 GB".</summary>
    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
