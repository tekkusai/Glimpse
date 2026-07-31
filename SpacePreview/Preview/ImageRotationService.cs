using System.IO;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

internal enum RotationPersistence
{
    /// <summary>Rotation is saved by flipping the EXIF orientation tag. Pixels never change.</summary>
    ExifTag,

    /// <summary>Rotation is saved by re-encoding pixels. Lossless formats only.</summary>
    Reencode,

    /// <summary>Rotation shows in the preview but is never written back.</summary>
    ViewOnly
}

/// <summary>
/// Persists quarter-turn rotations without degrading pixels: EXIF orientation
/// for JPEG/TIFF, a pixel-lossless re-encode for PNG/BMP (metadata and color
/// profile carried when the codec round-trips them), and view-only wherever a
/// write would mean lossy re-compression. Every failure falls back silently
/// to view-only; the original file is never at risk.
/// </summary>
internal static class ImageRotationService
{
    public static RotationPersistence Classify(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                return RotationPersistence.ViewOnly;
            }
        }
        catch
        {
            return RotationPersistence.ViewOnly;
        }

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jpe" or ".jfif" or ".tif" or ".tiff" => RotationPersistence.ExifTag,
            ".png" or ".bmp" => RotationPersistence.Reencode,
            _ => RotationPersistence.ViewOnly
        };
    }

    /// <summary>
    /// Applies <paramref name="quarterTurnsCw"/> to the file on disk. Runs on
    /// a worker thread; returns false when the file keeps its stored state.
    /// </summary>
    public static bool TrySaveQuarterTurns(string path, int quarterTurnsCw)
    {
        var turns = ((quarterTurnsCw % 4) + 4) % 4;
        if (turns == 0)
        {
            return true;
        }

        try
        {
            return Classify(path) switch
            {
                RotationPersistence.ExifTag => TrySaveExifOrientation(path, turns),
                RotationPersistence.Reencode => TryReencodeRotated(path, turns),
                _ => false
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"rotate save failed {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static bool TrySaveExifOrientation(string path, int turns)
    {
        var query = ImageOrientation.MetadataQueryFor(path);
        if (query is null)
        {
            return false;
        }

        var target = ImageOrientation.RotateCw(ImageOrientation.Read(path), turns);

        using (var stream = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.Default);
            var writer = decoder.Frames[0].CreateInPlaceBitmapMetadataWriter();
            writer.SetQuery(query, (ushort)target);
            if (writer.TrySave())
            {
                DiagnosticLog.Write($"rotate saved (exif {target}) {path}");
                return true;
            }
        }

        // No room to patch in place. A JPEG with no EXIF block at all can
        // take a spliced minimal segment: a byte-level insert, still zero
        // pixel work. Anything else stays view-only rather than re-encode.
        if (IsJpeg(path) && !HasExifSegment(path) && TrySpliceJpegExifOrientation(path, target))
        {
            DiagnosticLog.Write($"rotate saved (exif spliced {target}) {path}");
            return true;
        }

        DiagnosticLog.Write($"rotate not persisted (no exif room) {path}");
        return false;
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
    /// Inserts a minimal EXIF APP1 segment (orientation only) into a JPEG
    /// that has none. Pure byte splice through a temp file; the original is
    /// replaced only after the copy is complete.
    /// </summary>
    private static bool TrySpliceJpegExifOrientation(string path, int orientation)
    {
        var tempPath = path + ".spvw-rotate.tmp";
        try
        {
            using (var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var target = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
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
                        break;
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

            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"rotate splice failed {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // A stranded temp file is cosmetic; the original is intact.
            }
        }
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

    /// <summary>
    /// Rotates pixels and rewrites the file through a temp copy. Only called
    /// for formats whose PIXEL encoders are lossless. Metadata and color
    /// profiles are carried when the codec round-trips them; when they will
    /// not transfer, the pixels win and the log names the file.
    /// </summary>
    private static bool TryReencodeRotated(string path, int turns)
    {
        BitmapFrame sourceFrame;
        using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            sourceFrame = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnLoad).Frames[0];
        }

        var rotated = ImageOrientation.Apply(sourceFrame, ImageOrientation.FromQuarterTurns(turns));
        if (TryWriteRotated(path, rotated, sourceFrame, preserveMetadata: true))
        {
            DiagnosticLog.Write($"rotate saved (re-encode {turns * 90} deg) {path}");
            return true;
        }

        if (TryWriteRotated(path, rotated, sourceFrame, preserveMetadata: false))
        {
            DiagnosticLog.Write($"rotate saved (re-encode {turns * 90} deg, metadata dropped) {path}");
            return true;
        }

        return false;
    }

    private static bool TryWriteRotated(string path, BitmapSource rotated, BitmapFrame sourceFrame, bool preserveMetadata)
    {
        var tempPath = path + ".spvw-rotate.tmp";
        try
        {
            BitmapEncoder encoder = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => new PngBitmapEncoder(),
                ".bmp" => new BmpBitmapEncoder(),
                _ => throw new NotSupportedException("re-encode rotation is limited to lossless formats")
            };
            encoder.Frames.Add(preserveMetadata
                ? BitmapFrame.Create(rotated, thumbnail: null, sourceFrame.Metadata as BitmapMetadata, sourceFrame.ColorContexts)
                : BitmapFrame.Create(rotated));

            using (var output = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                encoder.Save(output);
            }

            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return true;
        }
        catch (Exception ex) when (preserveMetadata)
        {
            // The pixels-only retry follows; this trade is worth a log line.
            DiagnosticLog.Write($"rotate metadata carry failed {path}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // A stranded temp file is cosmetic; the original is intact.
            }
        }
    }
}
