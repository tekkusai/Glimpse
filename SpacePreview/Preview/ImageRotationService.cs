using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

internal enum RotationPersistence
{
    /// <summary>Rotation is saved by flipping the EXIF orientation tag. Pixels never change.</summary>
    ExifTag,

    /// <summary>Rotation is saved by turning the pixels themselves. Lossless formats only.</summary>
    Reencode,

    /// <summary>Rotation shows in the preview but is never written back: nothing here saves this kind of file.</summary>
    ViewOnly,

    /// <summary>Not written back because the file is read-only.</summary>
    ReadOnly,

    /// <summary>Not written back because this file, unlike most of its kind, would lose something to the turn.</summary>
    CannotCarry
}

/// <summary>
/// What became of a rotation handed to the disk. Anything but
/// <see cref="Saved"/> and <see cref="SavedAside"/> left the file as it was.
/// </summary>
internal enum RotationSaveResult
{
    Saved,

    /// <summary>
    /// The turned picture is whole, but under its working file's name: the
    /// swap lost the picture's own name and the turned copy could not take
    /// it. Nothing is under the old name, and the user has to be told.
    /// </summary>
    SavedAside,

    /// <summary>The file cannot hold a rotation without losing something of its own. Trying again changes nothing.</summary>
    CannotCarry,

    /// <summary>Another program has the file open.</summary>
    InUse,

    ReadOnly,

    DiskFull,

    /// <summary>Anything else: no permission, or the file is gone.</summary>
    Failed
}

/// <summary>
/// Persists quarter-turn rotations without costing the file anything: EXIF
/// orientation for JPEG/TIFF, the pixels themselves turned for PNG/BMP with
/// everything else the file holds carried across byte for byte, and
/// view-only wherever a write would lose something. A write that does not
/// land says which way it failed. A file that is rewritten is built beside
/// the original and swapped in, and through every way that swap can end
/// the picture is still there, under its own name or under one the user is
/// told.
/// </summary>
internal static class ImageRotationService
{
    // How long a save keeps asking for a file another program has open, and
    // how often. Explorer's thumbnailer, the search indexer and antivirus
    // each open a file that was just written and let go within a moment,
    // and nothing says when they have.
    private static readonly TimeSpan InUsePatience = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan InUseRetryStep = TimeSpan.FromMilliseconds(100);

    /// <summary>Whether a turn of such a file is written to disk. Every other answer is a reason it is not.</summary>
    public static bool Saves(RotationPersistence persistence) =>
        persistence is RotationPersistence.ExifTag or RotationPersistence.Reencode;

    /// <summary>
    /// How a turn of this file would be saved, or why it would not. A PNG is
    /// read as far as its pixels, which <paramref name="token"/> calls off.
    /// </summary>
    public static RotationPersistence Classify(string path, CancellationToken token = default)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return RotationPersistence.ViewOnly;
            }

            // PNG and BMP are judged by what this file holds, not by its
            // name, so the tooltip can say "not saved" before the first click.
            var persistence = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tif" or ".tiff" => RotationPersistence.ExifTag,
                ".png" => PngRotation.CanCarry(path, token) ? RotationPersistence.Reencode : RotationPersistence.CannotCarry,
                ".bmp" => BmpRotation.CanCarry(path) ? RotationPersistence.Reencode : RotationPersistence.CannotCarry,
                _ => RotationPersistence.ViewOnly
            };

            // Read-only is the reason only for a file that would otherwise
            // save. Clearing the attribute would not make a WebP save.
            return Saves(persistence) && (info.Attributes & FileAttributes.ReadOnly) != 0
                ? RotationPersistence.ReadOnly
                : persistence;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return RotationPersistence.ViewOnly;
        }
    }

    /// <summary>
    /// Applies <paramref name="quarterTurnsCw"/> to the file on disk. Runs on
    /// a worker thread, and may hold it for <see cref="InUsePatience"/>.
    /// </summary>
    public static RotationSaveResult SaveQuarterTurns(string path, int quarterTurnsCw) =>
        SaveQuarterTurns(path, quarterTurnsCw, ReplaceFile);

    /// <summary>
    /// <paramref name="replace"/> is the call that swaps a rewritten file
    /// in: Windows' own, or a test's stand-in for the ways it can fail.
    /// </summary>
    internal static RotationSaveResult SaveQuarterTurns(string path, int quarterTurnsCw, Action<string, string> replace)
    {
        var turns = ((quarterTurnsCw % 4) + 4) % 4;
        if (turns == 0)
        {
            return RotationSaveResult.Saved;
        }

        // A sharing violation, and only that, is worth asking again: the
        // whole save runs again, so it turns the file as it is by then.
        long firstRefusal = 0;
        while (true)
        {
            try
            {
                var result = SaveOnce(path, turns, replace);
                if (firstRefusal != 0)
                {
                    DiagnosticLog.Write(
                        $"rotate save waited {Stopwatch.GetElapsedTime(firstRefusal).TotalMilliseconds:0} ms for a file in use {path}");
                }

                return result;
            }
            catch (IOException ex) when (IsSharingViolation(ex) && StillPatient(ref firstRefusal))
            {
                Thread.Sleep(InUseRetryStep);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"rotate save failed {path}: {ex.GetType().Name}: {ex.Message}");
                return FailureFor(ex);
            }
        }
    }

    private static bool IsSharingViolation(IOException ex) => (ex.HResult & 0xFFFF) == 32;

    /// <summary>Whether a refused save may ask again. The clock starts at the first refusal.</summary>
    private static bool StillPatient(ref long firstRefusal)
    {
        if (firstRefusal == 0)
        {
            firstRefusal = Stopwatch.GetTimestamp();
        }

        return Stopwatch.GetElapsedTime(firstRefusal) < InUsePatience;
    }

    private static RotationSaveResult SaveOnce(string path, int turns, Action<string, string> replace)
    {
        // A file made read-only or removed since it opened is a failed
        // save, not a format that cannot be saved.
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            DiagnosticLog.Write($"rotate save failed (file gone) {path}");
            return RotationSaveResult.Failed;
        }

        if (info.IsReadOnly)
        {
            DiagnosticLog.Write($"rotate save failed (read-only) {path}");
            return RotationSaveResult.ReadOnly;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tif" or ".tiff" => SaveExifOrientation(path, turns, replace),
            ".png" => Rewrite(path, $"re-encode {turns * 90} deg", replace, temp => PngRotation.Write(path, turns, temp)),
            ".bmp" => Rewrite(path, $"re-encode {turns * 90} deg", replace, temp => BmpRotation.Write(path, turns, temp)),
            _ => RotationSaveResult.CannotCarry
        };
    }

    /// <summary>
    /// Saves by building the turned file beside the original and putting it
    /// in the original's place. <paramref name="write"/> builds it, and says
    /// whether what it built is the picture. Every format that is rewritten
    /// comes through here, so the swap and what follows a failed one are
    /// written once. The working file goes however this ends, unless it has
    /// become the only copy of the picture.
    /// </summary>
    private static RotationSaveResult Rewrite(string path, string how, Action<string, string> replace, Func<string, bool> write)
    {
        var tempPath = TempPathFor(path);
        try
        {
            if (!write(tempPath))
            {
                return RotationSaveResult.CannotCarry;
            }

            var result = SwapIn(tempPath, path, replace);
            if (result == RotationSaveResult.Saved)
            {
                DiagnosticLog.Write($"rotate saved ({how}) {path}");
            }

            return result;
        }
        finally
        {
            DeleteTemp(tempPath, path);
        }
    }

    private static void ReplaceFile(string tempPath, string path) =>
        File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);

    /// <summary>
    /// Puts the finished working file in the picture's place. A replace can
    /// fail with the picture's own name already gone: Windows documents it
    /// for ReplaceFile (ERROR_UNABLE_TO_MOVE_REPLACEMENT with no backup
    /// name), and a file deleted in the moment between the write and the
    /// swap leaves the same thing. The working file is then the only copy
    /// of the picture. It takes the picture's name, and that is the save,
    /// not a failure. If it cannot, it stays where it is and the result
    /// says so.
    /// </summary>
    private static RotationSaveResult SwapIn(string tempPath, string path, Action<string, string> replace)
    {
        try
        {
            replace(tempPath, path);
            return RotationSaveResult.Saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && !IsThere(path) && File.Exists(tempPath))
        {
            DiagnosticLog.Write($"rotate replace lost the original ({ex.GetType().Name} 0x{ex.HResult:X8}) {path}");
        }

        // A name held by a file on its way out frees when the last program
        // lets go of it, and nothing says when: it gets the patience a file
        // in use gets.
        long firstRefusal = 0;
        while (true)
        {
            try
            {
                // A move never lands on a file: if one has the name again,
                // this fails and nothing is overwritten.
                File.Move(tempPath, path);
                DiagnosticLog.Write($"rotate turned copy took the picture's name {path}");
                return RotationSaveResult.Saved;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (StillPatient(ref firstRefusal))
                {
                    Thread.Sleep(InUseRetryStep);
                    continue;
                }

                // A file has the name again, so the picture is there, or the
                // turned copy itself is gone: either way there is no copy
                // to name, and this is a save that failed like any other.
                if (IsThere(path) || !File.Exists(tempPath))
                {
                    throw;
                }

                DiagnosticLog.Write($"rotate left the turned copy beside the picture ({ex.GetType().Name} 0x{ex.HResult:X8}) {tempPath}");
                return RotationSaveResult.SavedAside;
            }
        }
    }

    /// <summary>
    /// Whether the picture is under its own name and staying there. A file
    /// being deleted while a program still has it open keeps its name until
    /// that program lets go, and File.Exists says yes to it. It cannot be
    /// opened, and that is what tells the two apart. A file another program
    /// has to itself is there.
    /// </summary>
    internal static bool IsThere(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The Win32 error inside an I/O failure names what the user can do about it.</summary>
    private static RotationSaveResult FailureFor(Exception ex) => ex switch
    {
        IOException io => (io.HResult & 0xFFFF) switch
        {
            // Sharing violation, lock violation.
            32 or 33 => RotationSaveResult.InUse,
            // Handle disk full, disk full.
            39 or 112 => RotationSaveResult.DiskFull,
            // Write-protected media.
            19 => RotationSaveResult.ReadOnly,
            _ => RotationSaveResult.Failed
        },
        UnauthorizedAccessException or OutOfMemoryException => RotationSaveResult.Failed,
        // The codec refused what this file holds. A retry meets the same file.
        _ => RotationSaveResult.CannotCarry
    };

    private static RotationSaveResult SaveExifOrientation(string path, int turns, Action<string, string> replace)
    {
        var query = ImageOrientation.MetadataQueryFor(path);
        if (query is null)
        {
            return RotationSaveResult.CannotCarry;
        }

        int target;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var frame = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.Default).Frames[0];

            // Read through the handle that writes. A separate read that
            // found the file busy would answer "upright", and the turn
            // would be added to the wrong start.
            target = ImageOrientation.RotateCw(ImageOrientation.ReadFrom(frame, path), turns);
            var writer = frame.CreateInPlaceBitmapMetadataWriter();
            writer.SetQuery(query, (ushort)target);
            if (writer.TrySave())
            {
                DiagnosticLog.Write($"rotate saved (exif {target}) {path}");
                return RotationSaveResult.Saved;
            }
        }

        // No room to patch in place. A JPEG with no EXIF block at all can
        // take a spliced minimal segment: a byte-level insert, still zero
        // pixel work. Anything else stays view-only rather than re-encode.
        if (IsJpeg(path) && !HasExifSegment(path))
        {
            var spliced = Rewrite(path, $"exif spliced {target}", replace, temp => TrySpliceJpegExifOrientation(path, target, temp));
            if (spliced != RotationSaveResult.CannotCarry)
            {
                return spliced;
            }
        }

        DiagnosticLog.Write($"rotate not persisted (no exif room) {path}");
        return RotationSaveResult.CannotCarry;
    }

    private static bool IsJpeg(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".jpe" or ".jfif";

    /// <summary>
    /// Byte-level check for an EXIF APP1 segment. The splice's invariant is
    /// byte-level too: a malformed EXIF block that WPF cannot parse still
    /// occupies the one allowed Exif APP1 slot, so metadata queries are not
    /// a safe gate. Any anomaly reads as present, which blocks the splice.
    /// </summary>
    private static bool HasExifSegment(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> soi = stackalloc byte[2];
            stream.ReadExactly(soi);
            if (soi[0] != 0xFF || soi[1] != 0xD8)
            {
                return true;
            }

            Span<byte> lengthBytes = stackalloc byte[2];
            Span<byte> probe = stackalloc byte[6];
            while (TryReadMarker(stream, out var marker, out _))
            {
                // Entropy data or a standalone marker ends the header walk;
                // EXIF cannot appear past this point.
                if (marker is 0x01 or 0xD9 or 0xDA or (>= 0xD0 and <= 0xD7))
                {
                    return false;
                }

                stream.ReadExactly(lengthBytes);
                var remaining = ((lengthBytes[0] << 8) | lengthBytes[1]) - 2;
                if (remaining < 0)
                {
                    return true;
                }

                if (marker == 0xE1 && remaining >= probe.Length)
                {
                    stream.ReadExactly(probe);
                    if (probe.SequenceEqual("Exif\0\0"u8))
                    {
                        return true;
                    }

                    remaining -= probe.Length;
                }

                stream.Seek(remaining, SeekOrigin.Current);
            }

            return true;
        }
        catch
        {
            // Assume present: the splice must only run on files known to
            // carry no EXIF, or two APP1 Exif segments could result.
            return true;
        }
    }

    /// <summary>
    /// Reads the next JPEG marker, consuming the fill bytes the format
    /// allows in front of it (any run of 0xFF). Reports how many fills were
    /// eaten so a copying caller can reproduce the stream byte for byte.
    /// </summary>
    private static bool TryReadMarker(Stream stream, out byte marker, out int fillBytes)
    {
        marker = 0;
        fillBytes = 0;
        if (stream.ReadByte() != 0xFF)
        {
            return false;
        }

        int next;
        while ((next = stream.ReadByte()) == 0xFF)
        {
            fillBytes++;
        }

        if (next < 0)
        {
            return false;
        }

        marker = (byte)next;
        return true;
    }

    private static void WriteMarker(Stream target, byte marker, int fillBytes)
    {
        for (var i = 0; i <= fillBytes; i++)
        {
            target.WriteByte(0xFF);
        }

        target.WriteByte(marker);
    }

    private static void CopyExactly(Stream source, Stream target, int count)
    {
        var buffer = new byte[Math.Min(count, 81920)];
        while (count > 0)
        {
            var read = source.Read(buffer, 0, Math.Min(count, buffer.Length));
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    /// <summary>
    /// Writes the JPEG to <paramref name="tempPath"/> with a minimal EXIF
    /// APP1 segment (orientation only) inserted: a pure byte splice of a
    /// file that has none. False means the file's structure will not take
    /// the splice; a disk or sharing failure throws, so the caller can name
    /// it.
    /// </summary>
    private static bool TrySpliceJpegExifOrientation(string path, int orientation, string tempPath)
    {
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var target = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        Span<byte> soi = stackalloc byte[2];
        source.ReadExactly(soi);
        if (soi[0] != 0xFF || soi[1] != 0xD8)
        {
            return false;
        }

        target.Write(soi);

        // JFIF convention keeps APP0 first; copy any leading APP0
        // segments through, then splice the EXIF APP1 before whatever
        // comes next. Fill bytes ride along with their marker so the
        // copied stream stays byte-identical around the insert.
        Span<byte> lengthBytes = stackalloc byte[2];
        while (true)
        {
            if (!TryReadMarker(source, out var marker, out var fillBytes))
            {
                return false;
            }

            if (marker != 0xE0)
            {
                target.Write(BuildExifApp1((ushort)orientation));
                WriteMarker(target, marker, fillBytes);
                source.CopyTo(target);
                return true;
            }

            WriteMarker(target, marker, fillBytes);
            source.ReadExactly(lengthBytes);
            var length = (lengthBytes[0] << 8) | lengthBytes[1];
            if (length < 2)
            {
                return false;
            }

            target.Write(lengthBytes);
            CopyExactly(source, target, length - 2);
        }
    }

    /// <summary>Where a rewritten file is built before it replaces the original, beside it so the swap stays on one volume.</summary>
    internal static string TempPathFor(string path) => path + ".spvw-rotate.tmp";

    /// <summary>The formats a save rewrites through that file: PNG and BMP always, a JPEG when its tag is spliced in.</summary>
    internal static bool SavesThroughATemp(string path) =>
        IsJpeg(path) || Path.GetExtension(path).ToLowerInvariant() is ".png" or ".bmp";

    /// <summary>
    /// Removes a save's working file, but only while the picture is known
    /// to be there under its own name: when it is not, the working file is
    /// the only copy, and stays. True when there was a file and it is gone.
    /// </summary>
    internal static bool DeleteTemp(string tempPath, string path)
    {
        try
        {
            if (File.Exists(tempPath) && IsThere(path))
            {
                File.Delete(tempPath);
                return true;
            }
        }
        catch
        {
            // A stranded working file is cosmetic; the picture is intact.
        }

        return false;
    }

    /// <summary>
    /// A minimal EXIF APP1: little-endian TIFF header and one IFD holding
    /// only the orientation tag (0x0112).
    /// </summary>
    private static byte[] BuildExifApp1(ushort orientation)
    {
        // Payload: "Exif\0\0" (6) + TIFF header (8) + IFD (2 + 12 + 4) = 32.
        const int payloadLength = 32;
        var segment = new byte[2 + 2 + payloadLength];
        var i = 0;
        segment[i++] = 0xFF;
        segment[i++] = 0xE1;
        segment[i++] = 0x00;
        segment[i++] = payloadLength + 2;

        // Identifier.
        segment[i++] = (byte)'E';
        segment[i++] = (byte)'x';
        segment[i++] = (byte)'i';
        segment[i++] = (byte)'f';
        segment[i++] = 0x00;
        segment[i++] = 0x00;

        // TIFF header: little-endian, magic 42, first IFD at offset 8.
        segment[i++] = (byte)'I';
        segment[i++] = (byte)'I';
        segment[i++] = 0x2A;
        segment[i++] = 0x00;
        segment[i++] = 0x08;
        segment[i++] = 0x00;
        segment[i++] = 0x00;
        segment[i++] = 0x00;

        // IFD0: one entry.
        segment[i++] = 0x01;
        segment[i++] = 0x00;

        // Tag 0x0112, type SHORT (3), count 1, value inline.
        segment[i++] = 0x12;
        segment[i++] = 0x01;
        segment[i++] = 0x03;
        segment[i++] = 0x00;
        segment[i++] = 0x01;
        segment[i++] = 0x00;
        segment[i++] = 0x00;
        segment[i++] = 0x00;
        segment[i++] = (byte)(orientation & 0xFF);
        segment[i++] = (byte)(orientation >> 8);
        segment[i++] = 0x00;
        segment[i++] = 0x00;

        // Next IFD: none.
        segment[i++] = 0x00;
        segment[i++] = 0x00;
        segment[i++] = 0x00;
        segment[i] = 0x00;

        return segment;
    }
}
