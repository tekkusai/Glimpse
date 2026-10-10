using System.IO;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// Keeps regenerable files from growing without bound in an app that stays
/// resident for weeks. One rule for every cache folder, files and folders
/// alike: entries untouched for two weeks go, then the oldest until the
/// folder is back under its cap. Anything written in the last ten minutes
/// stays, because a live preview may be reading it. Runs a minute after
/// launch and every six hours after, on the thread pool.
/// </summary>
internal static class CacheHousekeeping
{
    /// <summary>Every regenerable folder under the data root. One not listed here grows forever.</summary>
    internal static readonly string[] Folders = ["DocumentCache", "OfficeMedia", "PdfRail", "MediaArtwork"];

    /// <summary>Folders earlier releases wrote and nothing reads any more.</summary>
    private static readonly string[] Retired = ["AppPackageHtml"];

    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private static readonly TimeSpan RecentlyUsed = TimeSpan.FromMinutes(10);
    private const long MaxFolderBytes = 300L * 1024 * 1024;

    private static Timer? _schedule;

    public static void Start() =>
        _schedule ??= new Timer(_ => Run(AppPaths.DataRoot, DateTime.UtcNow), null, TimeSpan.FromMinutes(1), TimeSpan.FromHours(6));

    /// <summary>Prunes every cache folder under <paramref name="root"/>; returns how many entries went.</summary>
    internal static int Run(string root, DateTime nowUtc)
    {
        try
        {
            var deleted = Retired
                .Select(name => new DirectoryInfo(Path.Combine(root, name)))
                .Count(folder => folder.Exists && TryDelete(folder));
            foreach (var name in Folders)
            {
                deleted += Prune(new DirectoryInfo(Path.Combine(root, name)), nowUtc);
            }

            if (deleted > 0)
            {
                DiagnosticLog.Write($"cache housekeeping removed {deleted} entries");
            }

            return deleted;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"cache housekeeping failed: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private static int Prune(DirectoryInfo folder, DateTime nowUtc)
    {
        if (!folder.Exists)
        {
            return 0;
        }

        var deleted = 0;
        var survivors = new List<(FileSystemInfo Entry, long Bytes)>();
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            if (nowUtc - entry.LastWriteTimeUtc > MaxAge && TryDelete(entry))
            {
                deleted++;
                continue;
            }

            survivors.Add((entry, MeasureBytes(entry)));
        }

        var total = survivors.Sum(item => item.Bytes);
        foreach (var (entry, bytes) in survivors.OrderBy(item => item.Entry.LastWriteTimeUtc))
        {
            if (total <= MaxFolderBytes)
            {
                break;
            }

            if (nowUtc - entry.LastWriteTimeUtc > RecentlyUsed && TryDelete(entry))
            {
                deleted++;
                total -= bytes;
            }
        }

        return deleted;
    }

    private static long MeasureBytes(FileSystemInfo entry)
    {
        try
        {
            return entry is FileInfo file
                ? file.Length
                : ((DirectoryInfo)entry).EnumerateFiles("*", SearchOption.AllDirectories).Sum(item => item.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDelete(FileSystemInfo entry)
    {
        try
        {
            if (entry is DirectoryInfo directory)
            {
                directory.Delete(recursive: true);
            }
            else
            {
                entry.Delete();
            }

            return true;
        }
        catch
        {
            // An entry backing a live preview stays until the next pass.
            return false;
        }
    }
}
