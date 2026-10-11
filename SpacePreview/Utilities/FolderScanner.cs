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
    /// <summary>Totals are a floor: the budget ran out or a subfolder refused to open.</summary>
    public bool IsLowerBound => Stopped || SkippedFolders > 0;
}

/// <summary>
/// Sizes a folder honestly within a budget. The top level is listed first,
/// by name, so rows exist before any size does; then each subfolder is
/// walked in turn. Whatever the budget does not reach stays unknown rather
/// than pretending to be small. No UI types: the card renders the result.
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

    public static FolderScan Scan(
        string path,
        int entryBudget,
        TimeSpan timeBudget,
        Action<FolderScan>? progress,
        CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var lastReport = Stopwatch.StartNew();
        var entries = 0;
        var stopped = false;
        var skipped = 0;
        long bytes = 0;
        var files = 0;
        var folders = 0;

        bool OverBudget()
        {
            if (++entries <= entryBudget && clock.Elapsed < timeBudget)
            {
                return false;
            }

            stopped = true;
            return true;
        }

        var children = new List<FolderChild>();
        var listedEveryChild = true;
        try
        {
            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", OneLevel))
            {
                token.ThrowIfCancellationRequested();
                if (OverBudget())
                {
                    listedEveryChild = false;
                    break;
                }

                if (entry is FileInfo file)
                {
                    files++;
                    bytes += file.Length;
                    children.Add(new FolderChild(file.Name, IsDirectory: false, IsLink: false, file.Length, Partial: false));
                }
                else
                {
                    folders++;
                    var isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    children.Add(new FolderChild(entry.Name, IsDirectory: true, isLink, Bytes: null, Partial: false));
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

        children.Sort(static (a, b) => a.IsDirectory != b.IsDirectory
            ? (a.IsDirectory ? -1 : 1)
            : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

        FolderScan Snapshot() => new(children.ToArray(), bytes, files, folders, stopped, listedEveryChild, skipped, RootUnreadable: false);

        progress?.Invoke(Snapshot());
        lastReport.Restart();

        var pending = new Stack<string>();
        for (var index = 0; index < children.Count && !stopped; index++)
        {
            var child = children[index];

            // A junction is followed by explicit recursion even when
            // enumeration skips reparse points, and one pointing up the tree
            // would walk forever. It is listed, never entered.
            if (!child.IsDirectory || child.IsLink)
            {
                continue;
            }

            long childBytes = 0;
            var childSkipped = 0;
            pending.Clear();
            pending.Push(Path.Combine(path, child.Name));
            while (pending.Count > 0 && !stopped)
            {
                var current = pending.Pop();
                try
                {
                    foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos("*", OneLevel))
                    {
                        token.ThrowIfCancellationRequested();
                        if (OverBudget())
                        {
                            break;
                        }

                        if (entry is FileInfo file)
                        {
                            files++;
                            childBytes += file.Length;
                        }
                        else
                        {
                            folders++;
                            if ((entry.Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                pending.Push(entry.FullName);
                            }
                        }
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    childSkipped++;
                }
            }

            skipped += childSkipped;
            bytes += childBytes;
            children[index] = child with { Bytes = childBytes, Partial = stopped || childSkipped > 0 };

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

    private static bool IsReadFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or SecurityException;
}
