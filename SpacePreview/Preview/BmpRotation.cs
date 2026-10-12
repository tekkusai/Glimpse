using System.Buffers.Binary;
using System.IO;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Turns a BMP by moving its pixels, byte for byte. The headers, the bit
/// masks and the palette go across as they are, with only the two sizes and
/// the two resolutions swapped, so nothing a writer put there is lost: an
/// alpha mask, a color space, the spare byte of a 32-bit pixel. WPF's
/// encoder keeps none of the first two and re-maps a small palette.
/// Run-length data and an embedded color profile cannot be moved this way,
/// and leave the file view-only.
/// </summary>
internal static class BmpRotation
{
    // The file header, then the smallest and the largest info header this
    // code reads: BITMAPINFOHEADER and BITMAPV5HEADER.
    private const int FileHeaderSize = 14;
    private const int InfoHeaderSize = 40;
    private const int V5HeaderSize = 124;

    /// <summary>Where a BMP's pixels are and how they are packed. Height is positive; a top-down file says so.</summary>
    private sealed record Layout(int Width, int Height, bool TopDown, int BitsPerPixel, int PixelOffset)
    {
        /// <summary>Rows are padded to four bytes.</summary>
        public long RowSize => ((long)Width * BitsPerPixel + 31) / 32 * 4;

        public long PixelEnd => PixelOffset + RowSize * Height;
    }

    /// <summary>Whether a rotation of this file can be saved, from its headers.</summary>
    public static bool CanCarry(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var head = new byte[FileHeaderSize + V5HeaderSize];
            var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return stream.Length < Array.MaxLength && ReadLayout(head.AsSpan(0, read), stream.Length) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static RotationSaveResult Save(string path, int turns)
    {
        var tempPath = ImageRotationService.TempPathFor(path);
        try
        {
            // Kept open, and so closed to writers, until the turned copy
            // is on disk: the copy is of the file as it is, not as it was.
            using (var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length >= Array.MaxLength)
                {
                    return Refused(path);
                }

                var file = new byte[source.Length];
                source.ReadExactly(file);
                if (ReadLayout(file, file.Length) is not { } layout || Turn(file, layout, turns) is not { } turned)
                {
                    return Refused(path);
                }

                File.WriteAllBytes(tempPath, turned);
            }

            File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            DiagnosticLog.Write($"rotate saved (re-encode {turns * 90} deg) {path}");
            return RotationSaveResult.Saved;
        }
        finally
        {
            ImageRotationService.DeleteTemp(tempPath);
        }
    }

    private static RotationSaveResult Refused(string path)
    {
        DiagnosticLog.Write($"rotate not persisted (bmp: layout cannot be moved) {path}");
        return RotationSaveResult.CannotCarry;
    }

    /// <summary>
    /// The layout of a BMP whose pixels sit uncompressed in whole rows, or
    /// null for anything else: an OS/2 header, run-length or embedded JPEG
    /// and PNG data, a color profile stored after the pixels.
    /// </summary>
    private static Layout? ReadLayout(ReadOnlySpan<byte> file, long fileLength)
    {
        if (file.Length < FileHeaderSize + InfoHeaderSize || file[0] != 'B' || file[1] != 'M')
        {
            return null;
        }

        var pixelOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[10..]);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(file[14..]);
        var width = BinaryPrimitives.ReadInt32LittleEndian(file[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(file[22..]);
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(file[26..]);
        var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(file[28..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(file[30..]);
        if (headerSize is < InfoHeaderSize or > V5HeaderSize
            || width <= 0
            || height is 0 or int.MinValue
            || planes != 1
            || bitsPerPixel is not (1 or 4 or 8 or 16 or 24 or 32)
            // Plain rows, with or without bit masks. Everything else is compressed.
            || compression is not (0 or 3 or 6)
            || pixelOffset < FileHeaderSize + headerSize
            || pixelOffset > fileLength)
        {
            return null;
        }

        // A version 5 header can point at a color profile stored behind the
        // pixels, by an offset this move would leave wrong.
        if (headerSize == V5HeaderSize)
        {
            if (file.Length < FileHeaderSize + V5HeaderSize)
            {
                return null;
            }

            var colorSpace = BinaryPrimitives.ReadUInt32LittleEndian(file[(FileHeaderSize + 56)..]);
            if (colorSpace is 0x4D424544 or 0x4C494E4B)
            {
                return null;
            }
        }

        var layout = new Layout(width, Math.Abs(height), TopDown: height < 0, bitsPerPixel, (int)pixelOffset);
        return layout.PixelEnd <= fileLength ? layout : null;
    }

    /// <summary>The whole file with its pixels turned clockwise, or null when the turned rows would not fit an array.</summary>
    private static byte[]? Turn(byte[] file, Layout before, int turns)
    {
        var swap = turns % 2 == 1;
        var after = before with { Width = swap ? before.Height : before.Width, Height = swap ? before.Width : before.Height };
        var tail = file.Length - before.PixelEnd;
        if (after.PixelEnd + tail >= Array.MaxLength)
        {
            return null;
        }

        // Everything in front of the pixels and anything a tool left behind
        // them is copied as it is. Row padding in between starts as zero.
        var turned = new byte[after.PixelEnd + tail];
        file.AsSpan(0, before.PixelOffset).CopyTo(turned);
        file.AsSpan((int)before.PixelEnd).CopyTo(turned.AsSpan((int)after.PixelEnd));

        var head = turned.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(head[2..], turned.Length);
        BinaryPrimitives.WriteInt32LittleEndian(head[18..], after.Width);
        BinaryPrimitives.WriteInt32LittleEndian(head[22..], after.TopDown ? -after.Height : after.Height);

        // The image size field may be zero for plain rows; one that is set
        // stays right.
        if (BinaryPrimitives.ReadUInt32LittleEndian(head[34..]) != 0)
        {
            BinaryPrimitives.WriteInt32LittleEndian(head[34..], (int)(after.PixelEnd - after.PixelOffset));
        }

        if (swap)
        {
            // Pixels per meter, across then down.
            var across = BinaryPrimitives.ReadInt32LittleEndian(head[38..]);
            BinaryPrimitives.WriteInt32LittleEndian(head[38..], BinaryPrimitives.ReadInt32LittleEndian(head[42..]));
            BinaryPrimitives.WriteInt32LittleEndian(head[42..], across);
        }

        for (var y = 0; y < after.Height; y++)
        {
            var toRow = RowStart(after, y);
            for (var x = 0; x < after.Width; x++)
            {
                // Where this pixel was before the turn, counted from the
                // top left as the image is seen.
                var (fromX, fromY) = turns switch
                {
                    1 => (y, before.Height - 1 - x),
                    2 => (before.Width - 1 - x, before.Height - 1 - y),
                    _ => (before.Width - 1 - y, x)
                };
                MovePixel(file, RowStart(before, fromY), fromX, turned, toRow, x, before.BitsPerPixel);
            }
        }

        return turned;
    }

    /// <summary>A BMP stores its bottom row first unless it says it is top-down.</summary>
    private static int RowStart(Layout layout, int y) =>
        (int)(layout.PixelOffset + layout.RowSize * (layout.TopDown ? y : layout.Height - 1 - y));

    private static void MovePixel(byte[] source, int fromRow, int fromX, byte[] target, int toRow, int toX, int bitsPerPixel)
    {
        if (bitsPerPixel >= 8)
        {
            var size = bitsPerPixel / 8;
            source.AsSpan(fromRow + fromX * size, size).CopyTo(target.AsSpan(toRow + toX * size));
            return;
        }

        // One or four bits a pixel, packed from the high end of each byte.
        var fromBit = fromX * bitsPerPixel;
        var toBit = toX * bitsPerPixel;
        var value = (source[fromRow + fromBit / 8] >> (8 - bitsPerPixel - fromBit % 8)) & ((1 << bitsPerPixel) - 1);
        target[toRow + toBit / 8] |= (byte)(value << (8 - bitsPerPixel - toBit % 8));
    }
}
