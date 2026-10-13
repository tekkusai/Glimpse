using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Native;
using SpacePreview.Utilities;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace SpacePreview.Services;

/// <summary>
/// A file on a phone, on a camera or inside a zip folder has no path on
/// disk: Explorer shows it, and nothing can open it by name. Windows itself
/// opens such a file by copying it out first. Glimpse does the same: the
/// copy goes to a folder of its own, read-only, under the file's own name,
/// and the preview shows the copy. Only the newest few are kept, and none
/// outlives the next start.
///
/// The file's owner may be a phone that takes its time, or one that has
/// stopped answering. So every copy is made on a thread of its own, with a
/// deadline, and whoever wants the file waits for it there and nowhere
/// else: never on the thread that reads Explorer's selection for every
/// other preview.
/// </summary>
internal static class BorrowedFiles
{
    /// <summary>
    /// The largest file borrowed. The copy is made before the window can
    /// open, and over a phone's cable a larger one would be seconds of
    /// nothing on screen.
    /// </summary>
    internal const long MaxBytes = 64L * 1024 * 1024;

    /// <summary>
    /// How long a copy may take. The largest file makes it at 3.2 MB a
    /// second, which is slower than a phone's cable. Past this the copy
    /// is given up and whoever waited is told there is no file.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    // The file on screen, the one before it, and room to step back.
    private const int Kept = 4;

    // The copy that is wanted, and those whose owner has stopped answering
    // and will not let their threads go. Past this no copy is started.
    private const int MaxTransfers = 4;

    private const uint SfgaoStream = 0x00400000;
    private const uint SfgaoFolder = 0x20000000;
    private const uint SigdnParentRelativeEditing = 0x80031001;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly Guid StreamHandler = new("1CEBB3AB-7C10-499A-A417-92CA16C4CB83");
    private static readonly Guid StreamIid = new("0000000C-0000-0000-C000-000000000046");
    private static readonly PropertyKey SizeKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);
    private static readonly PropertyKey ModifiedKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 14);

    // The copies on their way, by where they will be. The lock is also
    // what a copy leaves this list under, and what the folders are trimmed
    // under, so a request never meets a copy that has just given up or a
    // folder that is just being cleared.
    private static readonly Dictionary<string, Transfer> Transfers = new(StringComparer.OrdinalIgnoreCase);

    // The copy asked for last: the one somebody is waiting for.
    private static string? _wanted;
    private static int _serial;

    public static string Root => AppPaths.InDataRoot("Borrowed");

    /// <summary>How many copies are on their way, or stuck there.</summary>
    internal static int Underway
    {
        get
        {
            lock (Transfers)
            {
                return Transfers.Count;
            }
        }
    }

    /// <summary>Whether <paramref name="path"/> is a copy made here and not the user's own file.</summary>
    public static bool IsBorrowed(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            return Path.GetFullPath(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the copy of <paramref name="item"/> is, or will be once it
    /// has arrived: <see cref="WhenReady"/> says when. Null when the item
    /// is not a file that can be read as a stream, is larger than
    /// <paramref name="maxBytes"/>, or no copy could be started. Runs on
    /// the shell's thread and asks the item's owner only what the file is
    /// called and how large it is. The copying happens elsewhere.
    /// </summary>
    public static string? TryBorrow(IShellItem item, long maxBytes = MaxBytes)
    {
        try
        {
            if (item.GetAttributes(SfgaoStream | SfgaoFolder, out var attributes) < 0
                || (attributes & SfgaoStream) == 0
                || (attributes & SfgaoFolder) != 0)
            {
                return null;
            }

            var name = SafeName(DisplayName(item, SigdnParentRelativeEditing));
            var identity = DisplayName(item, SigdnDesktopAbsoluteParsing) ?? name;
            long? size = null;
            DateTime? modified = null;
            if (item is IShellItem2 details)
            {
                var sizeKey = SizeKey;
                if (details.GetUInt64(ref sizeKey, out var bytes) >= 0)
                {
                    size = (long)bytes;
                }

                var modifiedKey = ModifiedKey;
                if (details.GetFileTime(ref modifiedKey, out var fileTime) >= 0 && fileTime > 0)
                {
                    modified = DateTime.FromFileTimeUtc(fileTime);
                }
            }

            if (size > maxBytes)
            {
                DiagnosticLog.Write($"borrow: skipped, {size} bytes is over the limit: {name}");
                return null;
            }

            var target = Path.Combine(Root, Key($"{identity}|{size}|{modified?.Ticks}"), name);
            return Start(target, size, modified, maxBytes, Deadline, () => StreamOf(item));
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            DiagnosticLog.Write($"borrow: failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    /// <summary>
    /// Starts the copy to <paramref name="target"/>, or joins the one
    /// already on its way there, or finds it made. <paramref name="source"/>
    /// is asked on this thread for a way to open the file's stream on
    /// another. The newest request is the one somebody is waiting for: a
    /// copy on its way for another file stops at its next read.
    /// </summary>
    internal static string? Start(string target, long? size, DateTime? modified, long maxBytes, TimeSpan deadline, Func<Func<IStream>> source)
    {
        Transfer transfer;
        lock (Transfers)
        {
            _wanted = target;
            if (Transfers.ContainsKey(target))
            {
                return target;
            }

            if (File.Exists(target) && (size is null || new FileInfo(target).Length == size))
            {
                // Asked for again, it is the newest again: it is about to
                // be on screen, and must not be the one trimmed next.
                Touch(Path.GetDirectoryName(target)!);
                return target;
            }

            if (Transfers.Count >= MaxTransfers)
            {
                DiagnosticLog.Write($"borrow: skipped, {Transfers.Count} copies have not come back: {Path.GetFileName(target)}");
                return null;
            }

            transfer = new Transfer(target, modified, maxBytes, deadline);
            Transfers.Add(target, transfer);
        }

        // The owner is asked outside the lock: it is somebody else's code.
        try
        {
            transfer.Begin(source());
            return target;
        }
        catch
        {
            transfer.End(arrived: false);
            throw;
        }
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> is there to be shown.
    /// At once for a file that is not a copy made here. For a copy still
    /// on its way, when it has arrived; false when it could not be made
    /// or its deadline has passed. This is where a copy is waited for.
    /// </summary>
    public static async Task<bool> WhenReady(string? path)
    {
        if (!IsBorrowed(path))
        {
            return true;
        }

        Transfer? transfer;
        lock (Transfers)
        {
            Transfers.TryGetValue(path!, out transfer);
        }

        if (transfer is null)
        {
            return await Task.Run(() => File.Exists(path)).ConfigureAwait(false);
        }

        var left = transfer.Left;
        return left > TimeSpan.Zero
            && await Task.WhenAny(transfer.Done, Task.Delay(left)).ConfigureAwait(false) == transfer.Done
            && await transfer.Done.ConfigureAwait(false);
    }

    /// <summary>Nobody is waiting for a copy any more: the one on its way stops at its next read.</summary>
    public static void StopWaiting()
    {
        lock (Transfers)
        {
            _wanted = null;
        }
    }

    /// <summary>Removes every copy. Run at start, when no preview can be showing one.</summary>
    public static void Clear()
    {
        try
        {
            if (Directory.Exists(Root))
            {
                foreach (var entry in Directory.EnumerateDirectories(Root))
                {
                    Remove(entry);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"borrow: clear failed: {ex.GetType().Name}");
        }
    }

    /// <summary>One copy on its way, on a thread of its own.</summary>
    private sealed class Transfer(string target, DateTime? modified, long maxBytes, TimeSpan deadline)
    {
        private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        /// <summary>Whether the copy arrived. Never completes for an owner that never answers.</summary>
        public Task<bool> Done => _done.Task;

        /// <summary>What is left of the copy's time.</summary>
        public TimeSpan Left => deadline - _watch.Elapsed;

        public void Begin(Func<IStream> open)
        {
            var thread = new Thread(() => Run(open)) { IsBackground = true, Name = "Glimpse borrow" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        private void Run(Func<IStream> open)
        {
            var folder = Path.GetDirectoryName(target)!;
            var working = $"{target}.{Interlocked.Increment(ref _serial)}.part";
            var arrived = false;
            try
            {
                Directory.CreateDirectory(folder);
                if (Copy(open(), working, maxBytes, StillWanted))
                {
                    File.Move(working, target, overwrite: true);
                    if (modified is { } when)
                    {
                        File.SetLastWriteTimeUtc(target, when);
                    }

                    // The copy is not the user's file: nothing may be saved into it.
                    File.SetAttributes(target, FileAttributes.ReadOnly);
                    arrived = true;
                    DiagnosticLog.Write($"borrow: copied {new FileInfo(target).Length} bytes in {_watch.ElapsedMilliseconds} ms: {Path.GetFileName(target)}");
                }
                else
                {
                    DiagnosticLog.Write($"borrow: gave up after {_watch.ElapsedMilliseconds} ms: {Path.GetFileName(target)}");
                }
            }
            catch (Exception ex)
            {
                // Whatever a file's owner throws ends this copy, not the app.
                DiagnosticLog.Write($"borrow: failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }

            if (!arrived)
            {
                Discard(working, folder);
            }

            End(arrived);
        }

        /// <summary>
        /// Whether to read on: this is still the file asked for last, and
        /// there is time left. When not, the copy leaves the list here,
        /// under the lock a request takes, so a request for the same file
        /// finds a copy that will go on or starts another.
        /// </summary>
        private bool StillWanted()
        {
            lock (Transfers)
            {
                if (Left > TimeSpan.Zero && string.Equals(_wanted, target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                Forget();
                return false;
            }
        }

        /// <summary>The copy is over: off the list, the folders trimmed around a new one, and the waiting told.</summary>
        public void End(bool arrived)
        {
            lock (Transfers)
            {
                Forget();
                if (arrived)
                {
                    Trim(keep: Path.GetDirectoryName(target)!);
                }
            }

            _done.TrySetResult(arrived);
        }

        private void Forget()
        {
            if (Transfers.TryGetValue(target, out var listed) && ReferenceEquals(listed, this))
            {
                Transfers.Remove(target);
            }
        }
    }

    /// <summary>
    /// A way to open the item's stream on another thread. The item belongs
    /// to the thread it was read on, so what crosses is its ID list, and
    /// the other thread makes an item of its own from that.
    /// </summary>
    private static Func<IStream> StreamOf(IShellItem item)
    {
        var unknown = Marshal.GetIUnknownForObject(item);
        IntPtr idList;
        try
        {
            Marshal.ThrowExceptionForHR(SHGetIDListFromObject(unknown, out idList));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        return () =>
        {
            IShellItem? own = null;
            try
            {
                var itemIid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(idList, ref itemIid, out var itemPtr));
                own = (IShellItem)Marshal.GetUniqueObjectForIUnknown(itemPtr);
                Marshal.Release(itemPtr);
                var handler = StreamHandler;
                var iid = StreamIid;
                Marshal.ThrowExceptionForHR(own.BindToHandler(IntPtr.Zero, ref handler, ref iid, out var streamPtr));
                var stream = (IStream)Marshal.GetUniqueObjectForIUnknown(streamPtr);
                Marshal.Release(streamPtr);
                return stream;
            }
            finally
            {
                Marshal.FreeCoTaskMem(idList);
                if (own is not null)
                {
                    Marshal.ReleaseComObject(own);
                }
            }
        };
    }

    /// <summary>False when the stream holds more than <paramref name="maxBytes"/>, or the copy stopped being wanted.</summary>
    private static bool Copy(IStream stream, string working, long maxBytes, Func<bool> wanted)
    {
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            using var file = new FileStream(working, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (wanted())
            {
                Marshal.WriteInt32(read, 0);
                stream.Read(buffer, buffer.Length, read);
                var count = Marshal.ReadInt32(read);
                if (count <= 0)
                {
                    return true;
                }

                // A size the owner did not give, or gave wrong, is held to
                // the same limit as it arrives.
                total += count;
                if (total > maxBytes)
                {
                    return false;
                }

                file.Write(buffer, 0, count);
            }

            return false;
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            if (Marshal.IsComObject(stream))
            {
                Marshal.ReleaseComObject(stream);
            }
        }
    }

    /// <summary>
    /// Keeps the newest copies and whatever a window is showing (it holds
    /// its copy through PreviewWindow.CurrentPath); the rest go.
    /// </summary>
    private static void Trim(string keep)
    {
        try
        {
            var others = new DirectoryInfo(Root).EnumerateDirectories()
                .Where(entry => !string.Equals(entry.FullName, keep, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(entry => entry.CreationTimeUtc)
                .Skip(Kept - 1);
            foreach (var entry in others)
            {
                if (!CacheHousekeeping.IsHeld(entry))
                {
                    Remove(entry.FullName);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"borrow: trim failed: {ex.GetType().Name}");
        }
    }

    /// <summary>What a copy that did not arrive leaves: its own working file, and its folder when that held nothing else.</summary>
    private static void Discard(string working, string folder)
    {
        try
        {
            File.Delete(working);
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere. The next trim or the next start takes it.
        }
    }

    private static void Touch(string folder)
    {
        try
        {
            Directory.SetCreationTimeUtc(folder, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // It is only less new than it should be.
        }
    }

    private static void Remove(string folder)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere. The next trim or the next start takes it.
        }
    }

    private static string? DisplayName(IShellItem item, uint form)
    {
        if (item.GetDisplayName(form, out var namePtr) != 0 || namePtr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(namePtr);
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePtr);
        }
    }

    /// <summary>A name from outside, made safe to be a file name here: no folders, no reserved characters.</summary>
    internal static string SafeName(string? name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. (name ?? string.Empty).Select(c => invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "file" : cleaned.Length > 120 ? cleaned[^120..] : cleaned;
    }

    private static string Key(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToUpperInvariant())))[..16];

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHGetIDListFromObject(IntPtr unknown, out IntPtr idList);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHCreateItemFromIDList(IntPtr idList, ref Guid riid, out IntPtr item);
}
