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
/// </summary>
internal static class BorrowedFiles
{
    /// <summary>
    /// The largest file borrowed. The copy is made before the window can
    /// open, and over a phone's cable a larger one would be seconds of
    /// nothing on screen.
    /// </summary>
    internal const long MaxBytes = 64L * 1024 * 1024;

    // The file on screen, the one before it, and room to step back.
    private const int Kept = 4;

    private const uint SfgaoStream = 0x00400000;
    private const uint SfgaoFolder = 0x20000000;
    private const uint SigdnParentRelativeEditing = 0x80031001;
    private const uint SigdnDesktopAbsoluteParsing = 0x80028000;

    private static readonly Guid StreamHandler = new("1CEBB3AB-7C10-499A-A417-92CA16C4CB83");
    private static readonly Guid StreamIid = new("0000000C-0000-0000-C000-000000000046");
    private static readonly PropertyKey SizeKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 12);
    private static readonly PropertyKey ModifiedKey = new(new Guid("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 14);

    public static string Root => AppPaths.InDataRoot("Borrowed");

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
    /// The path of a copy of <paramref name="item"/>, or null when it is not
    /// a file that can be read as a stream, is larger than
    /// <paramref name="maxBytes"/>, or could not be copied. Crosses into
    /// the item's owner, so it runs on the shell's thread.
    /// </summary>
    public static string? TryBorrow(IShellItem item, long maxBytes = MaxBytes)
    {
        string? folder = null;
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

            folder = Path.Combine(Root, Key($"{identity}|{size}|{modified?.Ticks}"));
            var target = Path.Combine(folder, name);
            if (File.Exists(target) && (size is null || new FileInfo(target).Length == size))
            {
                return target;
            }

            var watch = Stopwatch.StartNew();
            Directory.CreateDirectory(folder);
            var working = target + ".part";
            if (!Copy(item, working, maxBytes))
            {
                DiagnosticLog.Write($"borrow: gave up past the limit: {name}");
                Remove(folder);
                return null;
            }

            File.Move(working, target, overwrite: true);
            if (modified is { } when)
            {
                File.SetLastWriteTimeUtc(target, when);
            }

            // The copy is not the user's file: nothing may be saved into it.
            File.SetAttributes(target, FileAttributes.ReadOnly);
            DiagnosticLog.Write($"borrow: copied {new FileInfo(target).Length} bytes in {watch.ElapsedMilliseconds} ms: {name}");
            Trim(keep: folder);
            return target;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            DiagnosticLog.Write($"borrow: failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
            if (folder is not null)
            {
                Remove(folder);
            }

            return null;
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

    private static bool Copy(IShellItem item, string working, long maxBytes)
    {
        var handler = StreamHandler;
        var iid = StreamIid;
        Marshal.ThrowExceptionForHR(item.BindToHandler(IntPtr.Zero, ref handler, ref iid, out var streamPtr));
        var stream = (IStream)Marshal.GetUniqueObjectForIUnknown(streamPtr);
        Marshal.Release(streamPtr);
        var read = Marshal.AllocCoTaskMem(sizeof(int));
        try
        {
            using var file = new FileStream(working, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[1024 * 1024];
            long total = 0;
            while (true)
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
        }
        finally
        {
            Marshal.FreeCoTaskMem(read);
            Marshal.ReleaseComObject(stream);
        }
    }

    /// <summary>
    /// Keeps the newest copies and whatever a window is showing (it holds
    /// its copy through PreviewWindow.CurrentPath); the rest go.
    /// </summary>
    private static void Trim(string keep)
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
}
