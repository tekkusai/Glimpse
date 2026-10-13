using System.IO;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>One entry on a disk as it was when asked: a file or a folder, how large, when written.</summary>
internal sealed record DiskEntry(bool IsDirectory, long Length, DateTime ModifiedUtc);

/// <summary>
/// What the disk says of the item a preview is for: whether it is there,
/// whether it is a folder, its size and time, and where a shortcut points.
/// Asked once for each item, and everything the window needs to know of the
/// item before its content loads is answered from here.
///
/// A disk of this PC answers at once and is asked on the window's thread, as
/// it always was. A network takes as long as it takes, and one that has
/// stopped answering takes most of a minute to say so, so it is asked on
/// another thread. The window waits <see cref="Patience"/> for that answer
/// and goes on with the item's name when it has none.
/// </summary>
internal sealed record ItemFacts(DiskEntry? Item, string? ShortcutTarget, DiskEntry? Content)
{
    /// <summary>
    /// How long the window waits to hear from a network what an item is. A
    /// share that is there answers in a few milliseconds, and the header
    /// then opens complete, as it does for a file on disk.
    /// </summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(100);

    /// <summary>Nothing is known of the item but its name: it is not there, or its disk has not answered yet.</summary>
    public static readonly ItemFacts Unknown = new(null, null, null);

    // The last question put to each share. A share is asked one question at
    // a time: one that has stopped answering holds one thread for as long
    // as Windows takes to give up on it, however many of its files are
    // stepped through meanwhile.
    private static readonly Dictionary<string, Task> Asked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the preview shows is a folder.</summary>
    public bool IsDirectory => Content is { IsDirectory: true };

    /// <summary>What the preview shows is a file, and it is there.</summary>
    public bool IsFile => Content is { IsDirectory: false };

    /// <summary>
    /// Asks the disk about <paramref name="path"/>. The task is complete on
    /// return when nothing had to be asked of a network. A question nobody
    /// waits for any more, by <paramref name="token"/>, is not put.
    /// Tests stand in for the disk with <paramref name="ask"/>.
    /// </summary>
    public static async Task<ItemFacts> ReadAsync(string path, CancellationToken token, Func<string, DiskEntry?>? ask = null)
    {
        ask ??= Entry;
        var item = await AskAsync(path, token, ask).ConfigureAwait(false);
        if (item is not { IsDirectory: false } || !FileKinds.IsShortcut(path))
        {
            return new ItemFacts(item, null, item);
        }

        // A shortcut previews what it points at, like Quick Look does for
        // aliases. One that points at nothing is previewed as itself. This
        // line runs on the window's thread only when the shortcut is on a
        // disk of this PC, and reads the shortcut there as it always did.
        var target = PreviewWindow.ReadShortcutTarget(path);
        var content = target is null ? null : await AskAsync(target, token, ask).ConfigureAwait(false);
        return content is null ? new ItemFacts(item, null, item) : new ItemFacts(item, target, content);
    }

    /// <summary>
    /// Whether asking about <paramref name="path"/> goes over a network.
    /// The path's shape cannot say: a mapped share has a drive letter too.
    /// </summary>
    internal static bool OnANetwork(string path)
    {
        if (!PreviewWindow.LooksLikeLocalDrivePath(path))
        {
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path);
            return root is null || new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>The entry at <paramref name="path"/>. Null when there is none, or it cannot be asked about.</summary>
    internal static DiskEntry? Entry(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                return new DiskEntry(false, file.Length, file.LastWriteTimeUtc);
            }

            var folder = new DirectoryInfo(path);
            return folder.Exists ? new DiskEntry(true, 0, folder.LastWriteTimeUtc) : null;
        }
        catch
        {
            return null;
        }
    }

    private static Task<DiskEntry?> AskAsync(string path, CancellationToken token, Func<string, DiskEntry?> ask)
    {
        if (!OnANetwork(path))
        {
            return Task.FromResult(ask(path));
        }

        DiskEntry? Answer() => token.IsCancellationRequested ? null : ask(path);
        var share = Path.GetPathRoot(path) is { Length: > 0 } root ? root : path;
        lock (Asked)
        {
            var asked = Asked.TryGetValue(share, out var before) && !before.IsCompleted
                ? before.ContinueWith(_ => Answer(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default)
                : Task.Run(Answer);
            Asked[share] = asked;
            return asked;
        }
    }
}
