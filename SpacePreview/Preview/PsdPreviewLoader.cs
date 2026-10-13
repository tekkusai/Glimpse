using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Decodes the flattened composite a Photoshop file carries at its end, the
/// same image Photoshop shows in its own thumbnails. Covers the shapes real
/// files take: 8-bit RGB, grayscale, and CMYK (naive ink conversion, close
/// enough for a preview), raw or RLE, plus raw 16-bit taken at 8. Any legal
/// PSD size decodes row-streamed and integer-downsampled, so a 10800x7200
/// print file costs a 4K preview's memory, not its own. Each output pixel is
/// the average of its box of document pixels, so a downsampled preview is
/// smooth, not aliased. The color planes decode in parallel. A large
/// document (PSB) is the same file with wider counts. Lab and 32-bit fall
/// back to the file card rather than guess.
/// </summary>
internal static class PsdPreviewLoader
{
    /// <summary>PSD (version 1) caps each side at 30,000 pixels, PSB (version 2) at 300,000.</summary>
    private const int MaxDocumentDimension = 30_000;
    private const int MaxLargeDocumentDimension = 300_000;

    private const int MaxOutputDimension = 4096;

    /// <summary>
    /// The composite, brought down by a whole number until its long side
    /// is at most <paramref name="longSide"/>. The preview opens at the
    /// default; zoomed in past that, the window asks again for more.
    /// </summary>
    public static BitmapSource? TryLoad(string path, CancellationToken token = default, int longSide = MaxOutputDimension)
    {
        try
        {
            using var stream = OpenRead(path);
            return Decode(stream, path, Math.Max(1, longSide), token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"psd preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Header-only read for window sizing and the subtitle.</summary>
    public static bool TryReadPixelSize(string path, out Size size)
    {
        size = default;
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[26];
            if (stream.Read(header) != header.Length || !header[..4].SequenceEqual("8BPS"u8))
            {
                return false;
            }

            var height = ReadUInt32(header[14..]);
            var width = ReadUInt32(header[18..]);
            if (width == 0 || height == 0 || width > 300_000 || height > 300_000)
            {
                return false;
            }

            size = new Size(width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private enum PsdColor
    {
        Grayscale,
        Rgb,
        Cmyk
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static BitmapSource? Decode(Stream stream, string path, int longSide, CancellationToken token)
    {
        Span<byte> header = stackalloc byte[26];
        stream.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8))
        {
            return null;
        }

        var version = ReadUInt16(header[4..]);
        var channels = (int)ReadUInt16(header[12..]);
        var height = checked((int)ReadUInt32(header[14..]));
        var width = checked((int)ReadUInt32(header[18..]));
        var depth = ReadUInt16(header[22..]);
        var mode = ReadUInt16(header[24..]);

        // Version 2 is PSB, what Photoshop writes once a document passes
        // 30,000 pixels a side or 2 GB. It differs in two counts: the layer
        // section's length is eight bytes, and a packed row's is four.
        if (version is not (1 or 2))
        {
            DiagnosticLog.Write($"psd preview skipped {path}: version {version}");
            return null;
        }

        var large = version == 2;
        var side = large ? MaxLargeDocumentDimension : MaxDocumentDimension;
        if (width <= 0 || height <= 0 || width > side || height > side)
        {
            DiagnosticLog.Write($"psd preview skipped {path}: {width}x{height} outside PSD bounds");
            return null;
        }

        PsdColor color;
        int colorChannels;
        switch (mode)
        {
            case 1:
                color = PsdColor.Grayscale;
                colorChannels = 1;
                break;
            case 3:
                color = PsdColor.Rgb;
                colorChannels = 3;
                break;
            case 4:
                color = PsdColor.Cmyk;
                colorChannels = 4;
                break;
            default:
                DiagnosticLog.Write($"psd preview skipped {path}: color mode {mode}");
                return null;
        }

        if (depth is not (8 or 16))
        {
            DiagnosticLog.Write($"psd preview skipped {path}: depth {depth}");
            return null;
        }

        // The format caps channels at 56; a corrupt header claiming more
        // would size the RLE row table into the gigabytes before failing.
        if (channels < colorChannels || channels > 64)
        {
            DiagnosticLog.Write($"psd preview skipped {path}: {channels} channels for mode {mode}");
            return null;
        }

        // Color mode data, image resources, then layer info; the composite
        // image data section follows them.
        SkipSection(stream, wide: false);
        SkipSection(stream, wide: false);
        SkipSection(stream, wide: large);

        Span<byte> two = stackalloc byte[2];
        stream.ReadExactly(two);
        var compression = ReadUInt16(two);

        // Rows and columns shrink by an integer factor while decoding, so
        // plane memory is bounded by the preview, not the document.
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)longSide));
        var outWidth = (width + factor - 1) / factor;
        var outHeight = (height + factor - 1) / factor;
        var planeBytes = checked(outWidth * outHeight);

        // RGB and grayscale treat one extra channel as transparency, like
        // every common PSD reader. CMYK extras are almost always spot
        // channels (cut lines, white ink) - reading one as alpha would punch
        // holes in print files, so CMYK previews stay opaque.
        var planesWanted = color == PsdColor.Cmyk
            ? colorChannels
            : Math.Min(channels, colorChannels + 1);
        var planes = new byte[planesWanted][];

        if (compression is not (0 or 1) || (compression == 1 && depth != 8))
        {
            DiagnosticLog.Write($"psd preview skipped {path}: compression {compression} at depth {depth}");
            return null;
        }

        // Big-endian 16-bit samples: the high byte is the 8-bit value.
        var bytesPerSample = depth / 8;
        var rowBytes = (long)width * bytesPerSample;
        // PackBits puts a row-length table for every channel row first. A
        // packed row is never longer than twice the row it holds, whatever
        // wrote it, and a table that says otherwise is not believed: it
        // would size the buffer every plane reads into.
        var rowCounts = compression == 1 ? ReadRowCounts(stream, (long)channels * height, large ? 4 : 2) : null;
        var longestRow = rowCounts is null ? 0 : rowCounts.Max();
        if (longestRow > rowBytes * 2 + 2)
        {
            DiagnosticLog.Write($"psd preview skipped {path}: a packed row of {longestRow} bytes for a row of {rowBytes}");
            return null;
        }

        // Each plane is one run of rows in the file, so the planes decode
        // side by side, each from its own stream.
        var planeStarts = new long[planesWanted];
        planeStarts[0] = stream.Position;
        for (var plane = 1; plane < planesWanted; plane++)
        {
            planeStarts[plane] = planeStarts[plane - 1] + PlaneLength(rowCounts, plane - 1, height, rowBytes);
        }

        // What the header claims has to be in the file before anything is
        // set aside for it.
        if (planeStarts[^1] + PlaneLength(rowCounts, planesWanted - 1, height, rowBytes) > stream.Length)
        {
            DiagnosticLog.Write($"psd preview skipped {path}: the file ends before its {width}x{height} image does");
            return null;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        byte[] DecodePlane(int plane)
        {
            using var source = OpenRead(path);
            source.Position = planeStarts[plane];
            var rowBuffer = new byte[rowBytes];
            var compressed = new byte[longestRow];
            var sums = new int[outWidth];
            var target = new byte[planeBytes];
            for (var row = 0; row < height; row++)
            {
                if ((row & 255) == 0)
                {
                    token.ThrowIfCancellationRequested();
                }

                if (rowCounts is null)
                {
                    source.ReadExactly(rowBuffer);
                }
                else
                {
                    var length = rowCounts[plane * height + row];
                    source.ReadExactly(compressed, 0, length);
                    Array.Clear(rowBuffer);
                    DecodePackBits(compressed.AsSpan(0, length), rowBuffer);
                }

                AddRowToBoxes(rowBuffer, bytesPerSample, width, factor, sums);
                var rowsInBox = row % factor + 1;
                if (rowsInBox == factor || row == height - 1)
                {
                    WriteBoxAverages(sums, target.AsSpan(row / factor * outWidth, outWidth), width, factor, rowsInBox);
                    Array.Clear(sums);
                }
            }

            return target;
        }

        Parallel.For(0, planesWanted, new ParallelOptions { CancellationToken = token }, plane => planes[plane] = DecodePlane(plane));

        var alpha = planesWanted > colorChannels ? planes[colorChannels] : null;
        var pixels = new byte[(long)planeBytes * 4];
        switch (color)
        {
            case PsdColor.Grayscale:
                for (var i = 0; i < planeBytes; i++)
                {
                    var value = planes[0][i];
                    pixels[i * 4] = value;
                    pixels[i * 4 + 1] = value;
                    pixels[i * 4 + 2] = value;
                    pixels[i * 4 + 3] = alpha?[i] ?? 255;
                }

                break;

            case PsdColor.Rgb:
                for (var i = 0; i < planeBytes; i++)
                {
                    pixels[i * 4] = planes[2][i];
                    pixels[i * 4 + 1] = planes[1][i];
                    pixels[i * 4 + 2] = planes[0][i];
                    pixels[i * 4 + 3] = alpha?[i] ?? 255;
                }

                break;

            case PsdColor.Cmyk:
                // PSD stores CMYK inverted (255 = no ink), so the classic
                // uncalibrated conversion is a straight multiply with the
                // inverted key. Profile-true color needs the embedded ICC;
                // this stays within preview honesty.
                for (var i = 0; i < planeBytes; i++)
                {
                    var key = planes[3][i];
                    pixels[i * 4] = (byte)(planes[2][i] * key / 255);
                    pixels[i * 4 + 1] = (byte)(planes[1][i] * key / 255);
                    pixels[i * 4 + 2] = (byte)(planes[0][i] * key / 255);
                    pixels[i * 4 + 3] = alpha?[i] ?? 255;
                }

                break;
        }

        var bitmap = BitmapSource.Create(outWidth, outHeight, 96, 96, PixelFormats.Bgra32, null, pixels, outWidth * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static FileStream OpenRead(string path) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize: 1 << 16,
        FileOptions.SequentialScan);

    private static long PlaneLength(int[]? rowCounts, int plane, int height, long rowBytes)
    {
        if (rowCounts is null)
        {
            return height * rowBytes;
        }

        var length = 0L;
        foreach (var count in rowCounts.AsSpan(plane * height, height))
        {
            length += count;
        }

        return length;
    }

    private static int[] ReadRowCounts(Stream stream, long rows, int countBytes)
    {
        // The table is in the file or the file is not a picture: nothing
        // is set aside for a table a short file only claims.
        if (rows * countBytes > stream.Length - stream.Position)
        {
            throw new EndOfStreamException();
        }

        var table = new byte[rows * countBytes];
        stream.ReadExactly(table);
        var counts = new int[rows];
        for (var i = 0; i < counts.Length; i++)
        {
            counts[i] = countBytes == 2
                ? ReadUInt16(table.AsSpan(i * 2))
                : checked((int)ReadUInt32(table.AsSpan(i * 4)));
        }

        return counts;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void AddRowToBoxes(byte[] row, int bytesPerSample, int width, int factor, int[] sums)
    {
        for (var box = 0; box < sums.Length; box++)
        {
            var end = Math.Min(width, (box + 1) * factor);
            var sum = 0;
            for (var x = box * factor; x < end; x++)
            {
                sum += row[x * bytesPerSample];
            }

            sums[box] += sum;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void WriteBoxAverages(int[] sums, Span<byte> target, int width, int factor, int rows)
    {
        for (var box = 0; box < sums.Length; box++)
        {
            var pixels = (Math.Min(width, (box + 1) * factor) - box * factor) * rows;
            target[box] = (byte)((sums[box] + pixels / 2) / pixels);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void DecodePackBits(ReadOnlySpan<byte> source, Span<byte> target)
    {
        var read = 0;
        var write = 0;
        while (read < source.Length && write < target.Length)
        {
            var control = (sbyte)source[read++];
            if (control >= 0)
            {
                var count = Math.Min(control + 1, Math.Min(target.Length - write, source.Length - read));
                source.Slice(read, count).CopyTo(target[write..]);
                read += count;
                write += count;
            }
            else if (control != -128)
            {
                if (read >= source.Length)
                {
                    break;
                }

                var value = source[read++];
                var count = Math.Min(1 - control, target.Length - write);
                target.Slice(write, count).Fill(value);
                write += count;
            }
        }
    }

    private static void SkipSection(Stream stream, bool wide)
    {
        Span<byte> length = stackalloc byte[wide ? 8 : 4];
        stream.ReadExactly(length);
        var bytes = wide
            ? checked((long)(((ulong)ReadUInt32(length) << 32) | ReadUInt32(length[4..])))
            : ReadUInt32(length);
        stream.Seek(bytes, SeekOrigin.Current);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes) => (ushort)((bytes[0] << 8) | bytes[1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}
