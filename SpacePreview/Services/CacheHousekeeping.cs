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

    // Loose files, one per page or artwork: the pages Glimpse writes and the
    // album art they show. Each folder gets its own cap. AppPackageHtml is
    // retired; listing it lets its last pages age out.
    internal static readonly string[] PageFolders =
        ["MarkdownHtml", "CodeHtml", "EpubHtml", "MediaHtml", "ImageHtml", "MediaArtwork", "AppPackageHtml"];
    private const long MaxPageFolderBytes = 64L * 1024 * 1024;

    // Pages keep being written while the app stays resident, so writers ask
    // for another pass once this much time has gone by.
    private static readonly TimeSpan PageInterval = TimeSpan.FromHours(6);
    private static long _lastRunTicks;
    private static int _running;

    public static void Run()
    {
        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _lastRunTicks, DateTime.UtcNow.Ticks);
            var appRoot = AppPaths.DataRoot;

            var deleted = Prune(new DirectoryInfo(Path.Combine(appRoot, "DocumentCache")));
            deleted += Prune(new DirectoryInfo(Path.Combine(appRoot, "OfficeMedia")));
            deleted += Prune(new DirectoryInfo(Path.Combine(appRoot, "PdfRail")));
            foreach (var folder in PageFolders)
            {
                deleted += PruneFiles(new DirectoryInfo(Path.Combine(appRoot, folder)), DateTime.UtcNow, MaxPageFolderBytes);
            }

            if (deleted > 0)
            {
                DiagnosticLog.Write($"cache housekeeping removed {deleted} entries");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"cache housekeeping failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Starts a pass off the calling thread when the last one is old enough.</summary>
    public static void RunIfDue()
    {
        var last = Interlocked.Read(ref _lastRunTicks);
        if (DateTime.UtcNow.Ticks - last < PageInterval.Ticks
            || Interlocked.CompareExchange(ref _lastRunTicks, DateTime.UtcNow.Ticks, last) != last)
        {
            return;
        }

        _ = Task.Run(Run);
    }

    internal static int PruneFiles(DirectoryInfo root, DateTime now, long maxBytes)
    {
        if (!root.Exists)
        {
            return 0;
        }

        var deleted = 0;
        var survivors = new List<FileInfo>();
        foreach (var file in root.EnumerateFiles())
        {
            if (now - file.LastWriteTimeUtc > MaxAge)
            {
                if (TryDelete(file))
                {
                    deleted++;
                }

                continue;
            }

            survivors.Add(file);
        }

        var total = survivors.Sum(file => file.Length);
        foreach (var file in survivors.OrderBy(file => file.LastWriteTimeUtc))
        {
            if (total <= maxBytes)
            {
                break;
            }

            var bytes = file.Length;
            if (TryDelete(file))
            {
                deleted++;
                total -= bytes;
            }
        }

        return deleted;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch
        {
            // A page or image a live preview holds open stays until the next pass.
            return false;
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
