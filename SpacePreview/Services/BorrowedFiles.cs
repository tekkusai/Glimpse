using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Native;
using SpacePreview.Utilities;
using IStream = System.Runtime.InteropServices.ComTypes.IStream;

namespace SpacePreview.Services;

/// <summary>What a file with no path says of itself.</summary>
internal sealed record BorrowedFacts(string Name, string Identity, long? Size, DateTime? Modified);

/// <summary>
/// The owner of a file with no path, as the thread that copies the file
/// sees it. The owner may be a phone that takes its time, or one that has
/// stopped answering, so nothing here is ever called on a thread anything
/// else waits on.
/// </summary>
internal interface IFileOwner : IDisposable
{
    /// <summary>What the file is called, how large it is and when it changed. Null when it is not a file that can be read as a stream.</summary>
    BorrowedFacts? Describe();

    IStream Open();
}

/// <summary>
/// A file on a phone, on a camera or inside a zip folder has no path on
/// disk: Explorer shows it, and nothing can open it by name. Windows itself
/// opens such a file by copying it out first. Glimpse does the same: the
/// copy goes to a folder of its own, read-only, under the file's own name,
/// and the preview shows the copy. Only the newest few are kept, and none
/// outlives the next start.
///
/// Every word with the file's owner is had on a thread of that copy's own:
/// what the file is, and then its bytes. The thread that reads Explorer's
/// selection for every preview waits a moment to hear what the file is,
/// and never for the copy. Whoever wants the file waits for it in
/// <see cref="WhenReady"/>, and the copy has a deadline.
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
    /// How long a copy may take, from the first word with the owner. The
    /// largest file makes it at 3.2 MB a second, which is slower than a
    /// phone's cable. Past this the copy is given up and whoever waited
    /// is told there is no file.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long the shell's thread waits to hear what the file is. An
    /// owner answers that in a few milliseconds. One that has not within
    /// this is treated as having no file to show, and the thread goes back
    /// to the selections it reads for every other preview.
    /// </summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(1);

    // The file on screen, the one before it, and room to step back.
    private const int Kept = 4;

    // The copy that is wanted, and those whose owner has stopped answering
    // and will not let their threads go. Past this no owner is asked.
    private const int MaxCopies = 4;

    private const uint SfgaoStream = 0x00400000;
    private const uint SfgaoFolder = 0x20000000;
    private const uint SigdnParentRelativeEditing = 0x80031001;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly Guid StreamHandler = new("1CEBB3AB-7C10-499A-A417-92CA16C4CB83");
    private static readonly Guid StreamIid = new("0000000C-0000-0000-C000-000000000046");
    private static readonly PropertyKey SizeKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);
    private static readonly PropertyKey ModifiedKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 14);

    // The copies on their way, by where they will be. The lock is also
    // what a copy leaves this list under, and what folders are created,
    // trimmed and cleared under: a request never meets a copy that has
    // just given up, and no folder goes while a copy is headed for it.
    private static readonly Dictionary<string, Copy> Copies = new(StringComparer.OrdinalIgnoreCase);

    // The copy asked for last: the one somebody is waiting for.
    private static string? _wanted;
    private static int _serial;

    // Threads that have not come back, asking or copying.
    private static int _underway;

    public static string Root => AppPaths.InDataRoot("Borrowed");

    /// <summary>How many copies' threads have not come back.</summary>
    internal static int Underway => Volatile.Read(ref _underway);

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
    /// <paramref name="maxBytes"/>, or its owner did not say what it is
    /// in time. Runs on the shell's thread, and takes nothing from the
    /// item there but its ID list, which is the item's own memory.
    /// </summary>
    public static string? TryBorrow(IShellItem item, long maxBytes = MaxBytes)
    {
        try
        {
            return Ask(new ShellOwner(item), maxBytes, Deadline, Patience);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            DiagnosticLog.Write($"borrow: failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    /// <summary>
    /// Asks <paramref name="owner"/> for its file on a thread of that
    /// copy's own, and waits <paramref name="patience"/> to hear what the
    /// file is. The newest request is the one somebody is waiting for: a
    /// copy on its way for another file stops at its next read.
    /// </summary>
    internal static string? Ask(IFileOwner owner, long maxBytes, TimeSpan deadline, TimeSpan patience)
    {
        if (Interlocked.Increment(ref _underway) > MaxCopies)
        {
            Interlocked.Decrement(ref _underway);
            owner.Dispose();
            DiagnosticLog.Write($"borrow: skipped, {MaxCopies} owners have not answered");
            return null;
        }

        var copy = new Copy(owner, maxBytes, deadline);
        copy.Begin();
        return copy.Answer(patience);
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

        Copy? copy;
        lock (Copies)
        {
            Copies.TryGetValue(path!, out copy);
        }

        if (copy is null)
        {
            return await Task.Run(() => File.Exists(path)).ConfigureAwait(false);
        }

        var left = copy.Left;
        return left > TimeSpan.Zero
            && await Task.WhenAny(copy.Done, Task.Delay(left)).ConfigureAwait(false) == copy.Done
            && await copy.Done.ConfigureAwait(false);
    }

    /// <summary>Nobody is waiting for a copy any more: the one on its way stops at its next read.</summary>
    public static void StopWaiting()
    {
        lock (Copies)
        {
            _wanted = null;
        }
    }

    /// <summary>
    /// Removes the copies made before <paramref name="before"/>. Run at
    /// start with the moment the app started: a copy asked for since then,
    /// new or found from the last run, is dated from the asking, and may
    /// be on screen already.
    /// </summary>
    public static void Clear(DateTime before)
    {
        try
        {
            lock (Copies)
            {
                if (!Directory.Exists(Root))
                {
                    return;
                }

                foreach (var entry in new DirectoryInfo(Root).EnumerateDirectories())
                {
                    if (entry.CreationTimeUtc < before && !Awaited(entry.FullName))
                    {
                        Remove(entry.FullName);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Write($"borrow: clear failed: {ex.GetType().Name}");
        }
    }

    /// <summary>One file being asked for and copied, on a thread of its own.</summary>
    private sealed class Copy(IFileOwner owner, long maxBytes, TimeSpan deadline)
    {
        private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _answered = new();
        private readonly Stopwatch _watch = Stopwatch.StartNew();

        // Where the copy is or will be. Set with the answer, under the lock.
        private string? _answer;

        // Whoever asked stopped waiting to hear what the file is.
        private bool _unasked;

        // Set once this copy is the one listed for its file.
        private string? _target;
        private DateTime? _modified;

        /// <summary>Whether the copy arrived. Never completes for an owner that never answers.</summary>
        public Task<bool> Done => _done.Task;

        /// <summary>What is left of the copy's time.</summary>
        public TimeSpan Left => deadline - _watch.Elapsed;

        public void Begin()
        {
            var thread = new Thread(Run) { IsBackground = true, Name = "Glimpse borrow" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        /// <summary>
        /// For the shell's thread: where the copy is or will be, or null
        /// when there will be none. It waits to hear what the file is, and
        /// an owner that has not said by then makes no copy at all.
        /// </summary>
        public string? Answer(TimeSpan patience)
        {
            _answered.Wait(patience);
            lock (Copies)
            {
                _unasked = !_answered.IsSet;
                return _answer;
            }
        }

        private void Run()
        {
            string? working = null;
            var arrived = false;
            try
            {
                if (Place() is { } target)
                {
                    working = $"{target}.{Interlocked.Increment(ref _serial)}.part";
                    if (Read(owner.Open(), working, maxBytes, StillWanted))
                    {
                        File.Move(working, target, overwrite: true);
                        if (_modified is { } when)
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
            }
            catch (Exception ex)
            {
                // Whatever a file's owner throws ends this copy, not the app.
                DiagnosticLog.Write($"borrow: failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            }

            End(working, arrived);
        }

        /// <summary>
        /// Hears from the owner what the file is, and answers whoever
        /// asked: with where the copy is when it is already made or on
        /// its way, and with where it will be when this thread is to make
        /// it. Only then is the path returned.
        /// </summary>
        private string? Place()
        {
            var facts = owner.Describe();
            if (facts is null)
            {
                return null;
            }

            if (facts.Size > maxBytes)
            {
                DiagnosticLog.Write($"borrow: skipped, {facts.Size} bytes is over the limit: {facts.Name}");
                return null;
            }

            var name = SafeName(facts.Name);
            var folder = Path.Combine(Root, Key($"{facts.Identity}|{facts.Size}|{facts.Modified?.Ticks}"));
            var target = Path.Combine(folder, name);
            _modified = facts.Modified;
            lock (Copies)
            {
                if (_unasked)
                {
                    return null;
                }

                _wanted = target;
                if (Copies.ContainsKey(target))
                {
                    Reply(target);
                    return null;
                }

                if (File.Exists(target) && (facts.Size is null || new FileInfo(target).Length == facts.Size))
                {
                    // Asked for again, it is the newest again: it is about
                    // to be on screen, and must not be the one trimmed next.
                    Touch(folder);
                    Reply(target);
                    return null;
                }

                // The folder is made under the lock that lists the copy, so
                // nothing clears it between the two. One left from an
                // earlier run is as new as one made now.
                Directory.CreateDirectory(folder);
                Touch(folder);
                Copies.Add(target, this);
                _target = target;
                Reply(target);
                return target;
            }
        }

        private void Reply(string? target)
        {
            _answer = target;
            _answered.Set();
        }

        /// <summary>
        /// Whether to read on: this is still the file asked for last, and
        /// there is time left. When not, the copy leaves the list here,
        /// under the lock a request takes, so a request for the same file
        /// finds a copy that will go on or starts another.
        /// </summary>
        private bool StillWanted()
        {
            lock (Copies)
            {
                if (Left > TimeSpan.Zero && string.Equals(_wanted, _target, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                Forget();
                return false;
            }
        }

        /// <summary>
        /// The copy is over. It leaves the list, what it did not finish
        /// goes, the folders are trimmed around what it did, whoever asked
        /// or waited is told, and only then is its thread counted as back.
        /// </summary>
        private void End(string? working, bool arrived)
        {
            try
            {
                if (!arrived && working is not null)
                {
                    File.Delete(working);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open somewhere. The next trim or the next start takes it.
            }

            lock (Copies)
            {
                Forget();
                if (!_answered.IsSet)
                {
                    Reply(null);
                }

                if (arrived)
                {
                    Trim(keep: Path.GetDirectoryName(_target)!);
                }
                else if (working is not null)
                {
                    RemoveIfEmpty(Path.GetDirectoryName(working)!);
                }
            }

            try
            {
                owner.Dispose();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"borrow: owner left badly: {ex.GetType().Name}");
            }

            _done.TrySetResult(arrived);
            Interlocked.Decrement(ref _underway);
        }

        private void Forget()
        {
            if (_target is not null && Copies.TryGetValue(_target, out var listed) && ReferenceEquals(listed, this))
            {
                Copies.Remove(_target);
            }
        }
    }

    /// <summary>
    /// A shell item's owner. The item belongs to the thread it was read
    /// on, so what is taken there is its ID list, and the copying thread
    /// makes an item of its own from that for everything it asks.
    /// </summary>
    private sealed class ShellOwner : IFileOwner
    {
        private IntPtr _idList;
        private IShellItem? _item;

        public ShellOwner(IShellItem item)
        {
            var unknown = Marshal.GetIUnknownForObject(item);
            try
            {
                Marshal.ThrowExceptionForHR(SHGetIDListFromObject(unknown, out _idList));
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }

        public BorrowedFacts? Describe()
        {
            var item = Item();
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

            return new BorrowedFacts(name, identity, size, modified);
        }

        public IStream Open()
        {
            var handler = StreamHandler;
            var iid = StreamIid;
            Marshal.ThrowExceptionForHR(Item().BindToHandler(IntPtr.Zero, ref handler, ref iid, out var streamPtr));
            var stream = (IStream)Marshal.GetUniqueObjectForIUnknown(streamPtr);
            Marshal.Release(streamPtr);
            return stream;
        }

        public void Dispose()
        {
            if (_item is not null)
            {
                Marshal.ReleaseComObject(_item);
                _item = null;
            }

            if (_idList != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(_idList);
                _idList = IntPtr.Zero;
            }
        }

        private IShellItem Item()
        {
            if (_item is null)
            {
                var iid = typeof(IShellItem).GUID;
                Marshal.ThrowExceptionForHR(SHCreateItemFromIDList(_idList, ref iid, out var itemPtr));
                _item = (IShellItem)Marshal.GetUniqueObjectForIUnknown(itemPtr);
                Marshal.Release(itemPtr);
            }

            return _item;
        }
    }

    /// <summary>False when the stream holds more than <paramref name="maxBytes"/>, or the copy stopped being wanted.</summary>
    private static bool Read(IStream stream, string working, long maxBytes, Func<bool> wanted)
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

    /// <summary>Whether a copy on its way is headed for <paramref name="folder"/>. Asked under the lock.</summary>
    private static bool Awaited(string folder) =>
        Copies.Keys.Any(target => string.Equals(Path.GetDirectoryName(target), folder, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Keeps the newest copies, whatever a window is showing (it holds its
    /// copy through PreviewWindow.CurrentPath) and whatever a copy is on
    /// its way to; the rest go. Run under the lock.
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
                if (!CacheHousekeeping.IsHeld(entry) && !Awaited(entry.FullName))
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

    /// <summary>The folder of a copy that did not arrive, when nothing else is in it or headed for it. Run under the lock.</summary>
    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (!Awaited(folder) && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
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
