using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Decodes the flattened composite a Photoshop file carries at its end, the
/// same image Photoshop shows in its own thumbnails. Covers the shapes real
/// files take: 8-bit RGB, grayscale, and CMYK (naive ink conversion, close
/// enough for a preview), raw or RLE, plus raw 16-bit taken at 8. Documents
/// of any legal PSD size decode row-streamed and integer-downsampled, so a
/// 10800x7200 print file costs a 4K-preview's memory, not its own. Exotic
/// documents (Lab, 32-bit, PSB) fall back to the file card rather than guess.
/// </summary>
internal static class PsdPreviewLoader
{
    /// <summary>PSD (version 1) caps each side at 30,000 pixels.</summary>
    private const int MaxDocumentDimension = 30_000;

    private const int MaxOutputDimension = 4096;

    public static BitmapSource? TryLoad(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Decode(stream, path);
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

    private static BitmapSource? Decode(Stream stream, string path)
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

        // Version 2 is PSB; its section sizes are 64-bit. Rare enough that the
        // card fallback is the correct fallback.
        if (version != 1)
        {
            DiagnosticLog.Write($"psd preview skipped {path}: PSB large document");
            return null;
        }

        if (width <= 0 || height <= 0 || width > MaxDocumentDimension || height > MaxDocumentDimension)
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
        SkipSection(stream);
        SkipSection(stream);
        SkipSection(stream);

        Span<byte> two = stackalloc byte[2];
        stream.ReadExactly(two);
        var compression = ReadUInt16(two);

        // Rows and columns are sampled by an integer factor while decoding,
        // so plane memory is bounded by the preview, not the document.
        var factor = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MaxOutputDimension));
        var outWidth = (width + factor - 1) / factor;
        var outHeight = (height + factor - 1) / factor;
        var planeBytes = outWidth * outHeight;

        // RGB and grayscale treat one extra channel as transparency, like
        // every common PSD reader. CMYK extras are almost always spot
        // channels (cut lines, white ink) - reading one as alpha would punch
        // holes in print files, so CMYK previews stay opaque.
        var planesWanted = color == PsdColor.Cmyk
            ? colorChannels
            : Math.Min(channels, colorChannels + 1);
        var planes = new byte[planesWanted][];

        if (compression == 0)
        {
            var bytesPerSample = depth / 8;
            var rowBuffer = new byte[(long)width * bytesPerSample];
            for (var plane = 0; plane < planesWanted; plane++)
            {
                planes[plane] = new byte[planeBytes];
                for (var row = 0; row < height; row++)
                {
                    if (row % factor != 0)
                    {
                        stream.Seek(rowBuffer.Length, SeekOrigin.Current);
                        continue;
                    }

                    stream.ReadExactly(rowBuffer);
                    var target = planes[plane].AsSpan(row / factor * outWidth, outWidth);
                    for (var x = 0; x < outWidth; x++)
                    {
                        // Big-endian 16-bit: the high byte is the 8-bit value.
                        target[x] = rowBuffer[(long)x * factor * bytesPerSample];
                    }
                }
            }
        }
        else if (compression == 1 && depth == 8)
        {
            // PackBits. A row-length table for every channel row comes first;
            // it also lets skipped rows be seeked past instead of decoded.
            var rowCounts = new ushort[channels * height];
            var table = new byte[rowCounts.Length * 2];
            stream.ReadExactly(table);
            for (var i = 0; i < rowCounts.Length; i++)
            {
                rowCounts[i] = ReadUInt16(table.AsSpan(i * 2));
            }

            var compressed = new byte[rowCounts.Max()];
            var rowBuffer = new byte[width];
            for (var plane = 0; plane < planesWanted; plane++)
            {
                planes[plane] = new byte[planeBytes];
                for (var row = 0; row < height; row++)
                {
                    var length = rowCounts[plane * height + row];
                    if (row % factor != 0)
                    {
                        stream.Seek(length, SeekOrigin.Current);
                        continue;
                    }

                    stream.ReadExactly(compressed, 0, length);
                    Array.Clear(rowBuffer);
                    DecodePackBits(compressed.AsSpan(0, length), rowBuffer);
                    var target = planes[plane].AsSpan(row / factor * outWidth, outWidth);
                    for (var x = 0; x < outWidth; x++)
                    {
                        target[x] = rowBuffer[x * factor];
                    }
                }
            }
        }
        else
        {
            DiagnosticLog.Write($"psd preview skipped {path}: compression {compression} at depth {depth}");
            return null;
        }

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

    private static void SkipSection(Stream stream)
    {
        Span<byte> four = stackalloc byte[4];
        stream.ReadExactly(four);
        var length = ReadUInt32(four);
        stream.Seek(length, SeekOrigin.Current);
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes) => (ushort)((bytes[0] << 8) | bytes[1]);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes) =>
        ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
}
