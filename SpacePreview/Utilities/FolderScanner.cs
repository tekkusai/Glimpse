using System.Diagnostics;
using System.IO;
using System.Security;

namespace SpacePreview.Utilities;

/// <summary>
/// One top-level entry of a scanned folder. <see cref="Bytes"/> is null until
/// the scan reaches it; <see cref="Partial"/> means the number is a floor.
/// </summary>
internal sealed record FolderChild(string Name, bool IsDirectory, bool IsLink, long? Bytes, bool Partial);

/// <summary>What a folder scan knows so far, or at its end.</summary>
internal sealed record FolderScan(
    IReadOnlyList<FolderChild> Children,
    long Bytes,
    int Files,
    int Folders,
    bool Stopped,
    bool ListedEveryChild,
    int SkippedFolders,
    bool RootUnreadable)
{
    /// <summary>Totals are a floor: the clock ran out or a subfolder refused to open.</summary>
    public bool IsLowerBound => Stopped || SkippedFolders > 0;
}

/// <summary>
/// Sizes a folder honestly within its clock. The top level is listed first,
/// by name, so rows exist before any size does; then each subfolder is
/// walked in turn. Whatever the clock does not reach stays unknown rather
/// than pretending to be small. It remembers a fixed number of rows and
/// one open listing per level of depth, however many entries it counts.
/// No UI types: the card renders the result.
/// </summary>
internal static class FolderScanner
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    // Hidden and system entries are real bytes on disk; the default options
    // would skip them. Reparse points are handled by hand below.
    private static readonly EnumerationOptions OneLevel = new()
    {
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false
    };

    /// <param name="maxChildren">
    /// Top-level rows to remember. Entries past them are still counted and
    /// sized, so the totals stay whole; only the rows stop.
    /// </param>
    public static FolderScan Scan(
        string path,
        int maxChildren,
        TimeSpan timeBudget,
        Action<FolderScan>? progress,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        return Scan(path, maxChildren, () => clock.Elapsed >= timeBudget, progress, token);
    }

    /// <param name="outOfTime">Asked once for every entry read. The first true ends the scan.</param>
    internal static FolderScan Scan(
        string path,
        int maxChildren,
        Func<bool> outOfTime,
        Action<FolderScan>? progress,
        CancellationToken token)
    {
        var lastReport = Stopwatch.StartNew();
        var stopped = false;
        var skipped = 0;
        long bytes = 0;
        var files = 0;
        var folders = 0;

        bool OutOfTime()
        {
            token.ThrowIfCancellationRequested();
            stopped = stopped || outOfTime();
            return stopped;
        }

        // Depth first, with each level's listing held open while the level
        // below is read. Memory follows the tree's depth and not its width:
        // a folder of a million subfolders never queues a million paths.
        var open = new Stack<IEnumerator<FileSystemInfo>>();

        void Enter(string directory)
        {
            try
            {
                open.Push(new DirectoryInfo(directory).EnumerateFileSystemInfos("*", OneLevel).GetEnumerator());
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                skipped++;
            }
        }

        long Walk(string directory)
        {
            long total = 0;
            try
            {
                Enter(directory);
                while (open.Count > 0)
                {
                    FileSystemInfo entry;
                    try
                    {
                        if (!open.Peek().MoveNext())
                        {
                            open.Pop().Dispose();
                            continue;
                        }

                        entry = open.Peek().Current;
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        skipped++;
                        open.Pop().Dispose();
                        continue;
                    }

                    if (OutOfTime())
                    {
                        break;
                    }

                    if (entry is FileInfo file)
                    {
                        files++;
                        total += file.Length;
                    }
                    else
                    {
                        folders++;

                        // A junction is followed by explicit recursion even
                        // when enumeration skips reparse points, and one
                        // pointing up the tree would walk forever. It is
                        // counted, never entered.
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                        {
                            Enter(entry.FullName);
                        }
                    }
                }
            }
            finally
            {
                while (open.Count > 0)
                {
                    open.Pop().Dispose();
                }
            }

            return total;
        }

        var children = new List<FolderChild>();
        var listedEveryChild = true;
        var rowsReported = false;

        FolderScan Snapshot() => new(children.ToArray(), bytes, files, folders, stopped, listedEveryChild, skipped, RootUnreadable: false);

        // The rows go out once, by name, when no more will join them.
        void ReportRows()
        {
            children.Sort(FoldersThenByName);
            progress?.Invoke(Snapshot());
            lastReport.Restart();
            rowsReported = true;
        }

        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", OneLevel))
            {
                if (OutOfTime())
                {
                    listedEveryChild = false;
                    break;
                }

                // Past the rows that are kept, entries are only counted: the
                // rows are shown first, then the rest is sized as it is read.
                var listed = children.Count < maxChildren;
                if (!listed && listedEveryChild)
                {
                    listedEveryChild = false;
                    ReportRows();
                }

                if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                    if (listed)
                    {
                        children.Add(new FolderChild(file.Name, IsDirectory: false, IsLink: false, file.Length, Partial: false));
                    }
                }
                else
                {
                    folders++;
                    var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    if (listed)
                    {
                        children.Add(new FolderChild(entry.Name, IsDirectory: true, isLink, Bytes: null, Partial: false));
                    }
                    else if (!isLink)
                    {
                        bytes += Walk(entry.FullName);
                    }
                }
            }
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            if (children.Count == 0)
            {
                return new FolderScan([], 0, 0, 0, Stopped: false, ListedEveryChild: false, SkippedFolders: 0, RootUnreadable: true);
            }

            // The listing broke off midway: what was read stands, as a floor.
            skipped++;
            listedEveryChild = false;
        }

        // A junction is listed, never entered (see Walk).
        static bool Walkable(FolderChild child) => child.IsDirectory && !child.IsLink;

        if (!rowsReported)
        {
            // Rows by name are for a walk still to come. With nothing left
            // to walk the listing is the result, and nothing goes ahead of it.
            if (!stopped && children.Exists(Walkable))
            {
                ReportRows();
            }
            else
            {
                children.Sort(FoldersThenByName);
            }
        }

        for (var index = 0; index < children.Count && !stopped; index++)
        {
            var child = children[index];
            if (!Walkable(child))
            {
                continue;
            }

            var skippedBefore = skipped;
            var childBytes = Walk(Path.Combine(path, child.Name));
            bytes += childBytes;
            children[index] = child with { Bytes = childBytes, Partial = stopped || skipped > skippedBefore };

            if (lastReport.Elapsed >= ProgressInterval)
            {
                progress?.Invoke(Snapshot());
                lastReport.Restart();
            }
        }

        return Snapshot();
    }

    /// <summary>The card's fact line for a finished scan.</summary>
    public static string Describe(FolderScan scan)
    {
        if (scan.RootUnreadable)
        {
            return "Can't read this folder";
        }

        if (scan.Children.Count == 0 && !scan.IsLowerBound)
        {
            return "Empty folder";
        }

        var parts = new List<string> { FileKinds.FormatSize(scan.Bytes) };
        if (scan.Folders > 0)
        {
            parts.Add(Count(scan.Folders, "folder", "folders"));
        }

        if (scan.Files > 0)
        {
            parts.Add(Count(scan.Files, "file", "files"));
        }

        var text = string.Join(" · ", parts);
        return scan.IsLowerBound ? $"At least {text}" : text;
    }

    /// <summary>The fact line while the walk is still running.</summary>
    public static string DescribeProgress(FolderScan scan) =>
        $"Calculating… {FileKinds.FormatSize(scan.Bytes)} so far";

    /// <summary>A row's size: blank until scanned, a floor when cut short.</summary>
    public static string DescribeChild(FolderChild child)
    {
        if (child.IsLink)
        {
            return "Link";
        }

        if (child.Bytes is not { } size)
        {
            return string.Empty;
        }

        return child.Partial ? $"At least {FileKinds.FormatSize(size)}" : FileKinds.FormatSize(size);
    }

    public static string Count(long count, string one, string many) =>
        $"{count:N0} {(count == 1 ? one : many)}";

    private static int FoldersThenByName(FolderChild a, FolderChild b) => a.IsDirectory != b.IsDirectory
        ? (a.IsDirectory ? -1 : 1)
        : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);

    private static bool IsReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or SecurityException;
}
