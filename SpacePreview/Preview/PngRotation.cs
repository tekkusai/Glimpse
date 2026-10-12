using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// Turns a PNG's pixels and nothing else. WPF re-encodes the image data,
/// which is lossless, and every other chunk the file holds goes across byte
/// for byte, in its place: text, the color profile, gamma, EXIF, whatever a
/// tool stored there. WPF's encoder is not trusted with those: it rewrites
/// international text, adds color chunks the file never had and re-maps a
/// small palette. A file it cannot write back in its own pixel format, an
/// animation, and a file holding a chunk that must not outlive the pixels it
/// describes all stay view-only.
/// </summary>
internal static class PngRotation
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    // Chunks a quarter turn leaves true as they stand: they describe color,
    // the palette or the file, not where a pixel sits. pHYs is here because
    // it is rewritten with its two axes swapped.
    private static readonly HashSet<string> SurvivesATurn =
    [
        "PLTE", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "cICP", "mDCV", "cLLI", "sBIT", "bKGD", "hIST", "sPLT",
        "tEXt", "zTXt", "iTXt", "tIME", "eXIf", "pHYs"
    ];

    private static readonly SearchValues<byte> Letters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    /// <summary>One chunk: a length, a four-letter name, that many bytes and a checksum.</summary>
    private sealed record Chunk(string Name, long Start, int Length)
    {
        public long Size => 12L + Length;

        public long End => Start + Size;
    }

    /// <summary>
    /// Whether a rotation of this file can be saved, as far as the chunks in
    /// front of its pixels say. A save reads the rest and may still refuse.
    /// </summary>
    public static bool CanCarry(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadChunks(stream, toEnd: false) is { } chunks && Refusal(stream, chunks) is null;
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
            using (var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (ReadChunks(source, toEnd: true) is not { } chunks)
                {
                    return Refused("not a readable png", path);
                }

                if (Refusal(source, chunks) is { } refusal)
                {
                    return Refused(refusal, path);
                }

                source.Position = 0;
                var frame = BitmapDecoder.Create(
                    source,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad).Frames[0];
                var rotated = ImageOrientation.Apply(frame, ImageOrientation.FromQuarterTurns(turns));

                // Only the image data is wanted from the encoder, so it gets
                // the pixels alone: no metadata to rewrite or to choke on.
                using var encoded = new MemoryStream();
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(WithFullPalette(rotated)));
                encoder.Save(encoded);
                var pixels = ReadChunks(encoded, toEnd: true);
                if (pixels is null || !SamePixelFormat(ReadData(source, chunks[0]), ReadData(encoded, pixels[0])))
                {
                    return Refused("pixel format would change", path);
                }

                using (var target = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    Splice(source, chunks, encoded, pixels, target, swapAxes: turns % 2 == 1);
                }

                // The file is the user's only copy. It is replaced only by
                // one that decodes to the same pixels, bit for bit.
                if (!DecodesTo(tempPath, rotated))
                {
                    return Refused("pixels would change", path);
                }
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

    private static RotationSaveResult Refused(string reason, string path)
    {
        DiagnosticLog.Write($"rotate not persisted (png: {reason}) {path}");
        return RotationSaveResult.CannotCarry;
    }

    /// <summary>
    /// The file's chunks in order, up to its first IDAT or, for a save, to
    /// its IEND. Null when the file is not a well-formed PNG.
    /// </summary>
    private static List<Chunk>? ReadChunks(Stream stream, bool toEnd)
    {
        Span<byte> head = stackalloc byte[8];
        stream.Position = 0;
        if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length || !head.SequenceEqual(Signature))
        {
            return null;
        }

        var chunks = new List<Chunk>();
        var position = (long)Signature.Length;
        while (true)
        {
            stream.Position = position;
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(head);
            var name = head[4..];
            if (length > int.MaxValue || name.ContainsAnyExcept(Letters))
            {
                return null;
            }

            var chunk = new Chunk(Encoding.ASCII.GetString(name), position, (int)length);
            if (chunk.End > stream.Length)
            {
                return null;
            }

            chunks.Add(chunk);
            if (chunk.Name == "IEND" || (!toEnd && chunk.Name == "IDAT"))
            {
                return chunks;
            }

            position = chunk.End;
        }
    }

    /// <summary>What this file would lose to a rotation, or null when it would lose nothing.</summary>
    private static string? Refusal(Stream stream, IReadOnlyList<Chunk> chunks)
    {
        if (chunks[0] is not { Name: "IHDR", Length: 13 })
        {
            return "no header";
        }

        var colorType = ReadData(stream, chunks[0])[9];
        foreach (var chunk in chunks)
        {
            switch (chunk.Name)
            {
                case "IHDR" or "IDAT" or "IEND":
                    continue;
                case "acTL" or "fcTL" or "fdAT":
                    return "animated";
                // A color key on a gray or true-color image decodes into an
                // alpha channel, and WPF has no keyed format to write back.
                case "tRNS" when colorType != 3:
                    return "color-keyed transparency";
            }

            // An upper-case first letter is a chunk the image cannot be
            // read without; an upper-case last letter is one PNG says an
            // editor must drop once the pixels change. Unknown, either one
            // would be lost or left wrong.
            if (!SurvivesATurn.Contains(chunk.Name) && (char.IsAsciiLetterUpper(chunk.Name[0]) || char.IsAsciiLetterUpper(chunk.Name[3])))
            {
                return $"chunk {chunk.Name}";
            }
        }

        // WPF has no gray-with-alpha format either: it would come back RGBA.
        return colorType == 4 ? "gray with alpha" : null;
    }

    /// <summary>Bit depth and color type, the two header fields that say how a pixel is stored.</summary>
    private static bool SamePixelFormat(byte[] sourceHeader, byte[] encodedHeader) =>
        encodedHeader.Length == 13 && sourceHeader[8] == encodedHeader[8] && sourceHeader[9] == encodedHeader[9];

    /// <summary>
    /// The encoder shrinks an indexed image to the fewest bits its palette
    /// needs and matches colors to do it, which moves pixels between
    /// entries. A palette filled out to the format's size keeps every index
    /// where it was; the file's own palette chunk is the one written.
    /// </summary>
    private static BitmapSource WithFullPalette(BitmapSource bitmap)
    {
        var indexed = bitmap.Format == PixelFormats.Indexed1 || bitmap.Format == PixelFormats.Indexed2
            || bitmap.Format == PixelFormats.Indexed4 || bitmap.Format == PixelFormats.Indexed8;
        var size = 1 << bitmap.Format.BitsPerPixel;
        if (!indexed || bitmap.Palette is not { } palette || palette.Colors.Count >= size)
        {
            return bitmap;
        }

        var colors = palette.Colors.Concat(Enumerable.Repeat(Colors.Black, size - palette.Colors.Count)).ToList();
        var stride = Stride(bitmap);
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        return BitmapSource.Create(
            bitmap.PixelWidth,
            bitmap.PixelHeight,
            bitmap.DpiX,
            bitmap.DpiY,
            bitmap.Format,
            new BitmapPalette(colors),
            pixels,
            stride);
    }

    private static int Stride(BitmapSource bitmap) => (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;

    /// <summary>
    /// The new file is the old one with its header and image data replaced
    /// and its resolution turned. Every other byte is copied, checksums
    /// included, and stays where it was.
    /// </summary>
    private static void Splice(Stream source, List<Chunk> chunks, Stream encoded, List<Chunk> pixels, Stream target, bool swapAxes)
    {
        target.Write(Signature);
        var wroteImageData = false;
        foreach (var chunk in chunks)
        {
            switch (chunk.Name)
            {
                case "IHDR":
                    Copy(encoded, pixels[0].Start, pixels[0].Size, target);
                    break;
                case "IDAT":
                    if (!wroteImageData)
                    {
                        foreach (var data in pixels.Where(candidate => candidate.Name == "IDAT"))
                        {
                            Copy(encoded, data.Start, data.Size, target);
                        }

                        wroteImageData = true;
                    }

                    break;
                case "pHYs" when swapAxes && chunk.Length == 9:
                    WriteTurnedResolution(ReadData(source, chunk), target);
                    break;
                default:
                    Copy(source, chunk.Start, chunk.Size, target);
                    break;
            }
        }

        // Whatever a tool left after the image's end is still the user's.
        Copy(source, chunks[^1].End, source.Length - chunks[^1].End, target);
    }

    /// <summary>pHYs is pixels per unit across, then down, then the unit. A quarter turn swaps the first two.</summary>
    private static void WriteTurnedResolution(byte[] data, Stream target)
    {
        Span<byte> chunk = stackalloc byte[4 + 4 + 9 + 4];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, 9);
        "pHYs"u8.CopyTo(chunk[4..]);
        data.AsSpan(4, 4).CopyTo(chunk[8..]);
        data.AsSpan(0, 4).CopyTo(chunk[12..]);
        chunk[16] = data[8];
        BinaryPrimitives.WriteUInt32BigEndian(chunk[17..], Crc32(chunk[4..17]));
        target.Write(chunk);
    }

    /// <summary>PNG's checksum, over a chunk's name and data.</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] ReadData(Stream stream, Chunk chunk)
    {
        var data = new byte[chunk.Length];
        stream.Position = chunk.Start + 8;
        stream.ReadExactly(data);
        return data;
    }

    private static void Copy(Stream source, long start, long count, Stream target)
    {
        var buffer = new byte[(int)Math.Min(count, 81920)];
        source.Position = start;
        while (count > 0)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(count, buffer.Length));
            if (read <= 0)
            {
                throw new EndOfStreamException();
            }

            target.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static bool DecodesTo(string candidatePath, BitmapSource expected)
    {
        using var stream = File.Open(candidatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        if (actual.Format != expected.Format
            || actual.PixelWidth != expected.PixelWidth
            || actual.PixelHeight != expected.PixelHeight
            || !(actual.Palette?.Colors ?? []).SequenceEqual(expected.Palette?.Colors ?? []))
        {
            return false;
        }

        // In bands, so a large image is never held a third and fourth time.
        var stride = Stride(expected);
        var rows = Math.Max(1, (4 << 20) / stride);
        var actualBand = new byte[stride * rows];
        var expectedBand = new byte[stride * rows];
        for (var top = 0; top < expected.PixelHeight; top += rows)
        {
            var band = new Int32Rect(0, top, expected.PixelWidth, Math.Min(rows, expected.PixelHeight - top));
            actual.CopyPixels(band, actualBand, stride, 0);
            expected.CopyPixels(band, expectedBand, stride, 0);
            var bytes = stride * band.Height;
            if (!actualBand.AsSpan(0, bytes).SequenceEqual(expectedBand.AsSpan(0, bytes)))
            {
                return false;
            }
        }

        return true;
    }
}
