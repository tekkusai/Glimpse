using System.IO;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// Keeps the on-disk document cache from growing without bound in an app that
/// stays resident for weeks. Runs once per launch, off the UI thread: entries
/// unused for two weeks go first, then the oldest entries until the cache is
/// back under its size cap. Conversion cache hits refresh an entry's clock.
/// </summary>
internal static class CacheHousekeeping
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);
    private const long MaxTotalBytes = 300L * 1024 * 1024;

    public static void Run()
    {
        try
        {
            var appRoot = AppPaths.DataRoot;

            var deleted = Prune(new DirectoryInfo(Path.Combine(appRoot, "DocumentCache")));
            deleted += Prune(new DirectoryInfo(Path.Combine(appRoot, "OfficeMedia")));
            deleted += Prune(new DirectoryInfo(Path.Combine(appRoot, "PdfRail")));

            if (deleted > 0)
            {
                DiagnosticLog.Write($"cache housekeeping removed {deleted} entries");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"cache housekeeping failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int Prune(DirectoryInfo root)
    {
        if (!root.Exists)
        {
            return 0;
        }

        var now = DateTime.UtcNow;
        var deleted = 0;
        var survivors = new List<(DirectoryInfo Dir, long Bytes)>();

        foreach (var entry in root.GetDirectories())
        {
            if (now - entry.LastWriteTimeUtc > MaxAge)
            {
                if (TryDelete(entry))
                {
                    deleted++;
                }

                continue;
            }

            survivors.Add((entry, MeasureBytes(entry)));
        }

        var total = survivors.Sum(s => s.Bytes);
        foreach (var (dir, bytes) in survivors.OrderBy(s => s.Dir.LastWriteTimeUtc))
        {
            if (total <= MaxTotalBytes)
            {
                break;
            }

            if (TryDelete(dir))
            {
                deleted++;
                total -= bytes;
            }
        }

        return deleted;
    }

    private static long MeasureBytes(DirectoryInfo directory)
    {
        try
        {
            return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
            return true;
        }
        catch
        {
            // An entry backing a live preview stays until the next launch.
            return false;
        }
    }
}
