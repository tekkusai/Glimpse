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
/// describes all stay view-only. The chunks are read one at a time and
/// never kept, so a file with millions of them costs what its bytes cost to
/// read.
/// </summary>
internal static class PngRotation
{
    private static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];

    // Chunks a quarter turn leaves true as they stand: they describe color,
    // the palette or the file, not where a pixel sits. pHYs is here because
    // it is rewritten with its two axes swapped.
    private static readonly HashSet<uint> SurvivesATurn =
    [
        .. new[]
        {
            "PLTE", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "cICP", "mDCV", "cLLI", "sBIT", "bKGD", "hIST", "sPLT",
            "tEXt", "zTXt", "iTXt", "tIME", "eXIf", "pHYs"
        }.Select(name => BinaryPrimitives.ReadUInt32BigEndian(Encoding.ASCII.GetBytes(name)))
    ];

    private static readonly SearchValues<byte> Letters =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"u8);

    /// <summary>One chunk: a length, a four-letter name, that many bytes and a checksum.</summary>
    private readonly record struct Chunk(uint Name, long Start, int Length)
    {
        public long Size => 12L + Length;

        public long End => Start + Size;

        public bool Is(ReadOnlySpan<byte> name) => Name == BinaryPrimitives.ReadUInt32BigEndian(name);

        /// <summary>The name as its four letters, for the log.</summary>
        public string Text => string.Create(4, Name, (letters, name) =>
        {
            for (var i = 0; i < 4; i++)
            {
                letters[i] = (char)(byte)(name >> (24 - 8 * i));
            }
        });
    }

    /// <summary>
    /// Steps through a PNG's chunks from a place in its stream, and keeps
    /// nothing behind it. A step fails where the file ends or stops being a
    /// PNG: a name that is not four letters, or a length that runs past the
    /// end.
    /// </summary>
    private sealed class ChunkReader(Stream stream, long position)
    {
        // Asked once: a file open to writers answers this from the disk
        // every time.
        private readonly long _length = stream.Length;
        private long _next = position;

        public bool TryNext(out Chunk chunk)
        {
            Span<byte> head = stackalloc byte[8];
            chunk = default;
            stream.Position = _next;
            if (stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) < head.Length)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(head);
            var name = head[4..];
            if (length > int.MaxValue || name.ContainsAnyExcept(Letters))
            {
                return false;
            }

            var read = new Chunk(BinaryPrimitives.ReadUInt32BigEndian(name), _next, (int)length);
            if (read.End > _length)
            {
                return false;
            }

            chunk = read;
            _next = read.End;
            return true;
        }
    }

    /// <summary>
    /// What a PNG says in front of its pixels: its size, whether it is an
    /// animation, whether a tRNS chunk gives it transparency, and what a
    /// rotation would lose among those chunks. <see cref="Complete"/> is
    /// false when the walk stopped at its budget before the pixels: the
    /// rest is then not known yet, and nothing here says "no".
    /// </summary>
    internal readonly record struct Front(
        int Width,
        int Height,
        byte ColorType,
        bool IsAnimated,
        bool HasTransparency,
        string? Refusal,
        bool Complete);

    /// <summary>
    /// The one walk of the chunks in front of a PNG's pixels, at most
    /// <paramref name="budget"/> of them. The header asks it whether the
    /// file moves and the rotation rule asks it whether a turn can be
    /// saved, so the two cannot disagree. Each chunk is looked at and left.
    /// Null when the stream does not open as a PNG.
    /// </summary>
    internal static Front? ReadFront(Stream stream, int budget = int.MaxValue, CancellationToken token = default)
    {
        // Signature, the IHDR's length and name, then width, height, bit
        // depth and color type. A file cut short still has the size it
        // declared.
        Span<byte> start = stackalloc byte[26];
        stream.Position = 0;
        var read = stream.ReadAtLeast(start, start.Length, throwOnEndOfStream: false);
        if (read < 24 || !start[..8].SequenceEqual(Signature) || !start[12..16].SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(start[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(start[20..24]);
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            return null;
        }

        var headerLength = BinaryPrimitives.ReadUInt32BigEndian(start[8..12]);
        var colorType = read == start.Length ? start[25] : (byte)0;
        var refusal = read == start.Length && headerLength == 13 ? null : "no header";
        var isAnimated = false;
        var hasTransparency = false;
        var complete = false;
        var reader = new ChunkReader(stream, Signature.Length + 12L + headerLength);
        for (var walked = 0; walked < budget && !complete; walked++)
        {
            token.ThrowIfCancellationRequested();
            if (!reader.TryNext(out var chunk))
            {
                // Cut short or broken: there is nothing more to learn.
                refusal ??= "not a readable png";
                complete = true;
            }
            else if (chunk.Is("IDAT"u8) || chunk.Is("IEND"u8))
            {
                complete = true;
            }
            else
            {
                isAnimated |= chunk.Is("acTL"u8);
                hasTransparency |= chunk.Is("tRNS"u8);
                refusal ??= Loss(chunk, colorType);
            }
        }

        // WPF has no gray-with-alpha format: it would come back RGBA.
        refusal ??= colorType == 4 ? "gray with alpha" : null;
        return new Front((int)width, (int)height, colorType, isAnimated, hasTransparency, refusal, complete);
    }

    /// <summary>
    /// Whether a rotation of this file can be saved, as far as the chunks in
    /// front of its pixels say. A save reads the rest and may still refuse.
    /// </summary>
    public static bool CanCarry(string path, CancellationToken token = default)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return ReadFront(stream, token: token) is { Refusal: null };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the turned picture at <paramref name="tempPath"/>. False when
    /// the file would lose something to the turn, and nothing written there
    /// is then worth keeping.
    /// </summary>
    public static bool Write(string path, int turns, string tempPath)
    {
        using var source = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Refusal(source) is { } refusal)
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
        if (!SamePixelFormat(source, encoded))
        {
            return Refused("pixel format would change", path);
        }

        using (var target = File.Open(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            Splice(source, encoded, target, swapAxes: turns % 2 == 1);
        }

        // The file is the user's only copy. It is replaced only by
        // one that decodes to the same pixels, bit for bit.
        return DecodesTo(tempPath, rotated) || Refused("pixels would change", path);
    }

    private static bool Refused(string reason, string path)
    {
        DiagnosticLog.Write($"rotate not persisted (png: {reason}) {path}");
        return false;
    }

    /// <summary>
    /// What this file would lose to a rotation, with every chunk up to its
    /// IEND read, or null when it would lose nothing.
    /// </summary>
    private static string? Refusal(Stream stream)
    {
        Span<byte> signature = stackalloc byte[8];
        stream.Position = 0;
        var reader = new ChunkReader(stream, Signature.Length);
        if (stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false) < signature.Length
            || !signature.SequenceEqual(Signature)
            || !reader.TryNext(out var header))
        {
            return "not a readable png";
        }

        if (!header.Is("IHDR"u8) || header.Length != 13)
        {
            return "no header";
        }

        var colorType = ReadData(stream, header)[9];
        while (reader.TryNext(out var chunk))
        {
            if (Loss(chunk, colorType) is { } loss)
            {
                return loss;
            }

            if (chunk.Is("IEND"u8))
            {
                // WPF has no gray-with-alpha format either: it would come back RGBA.
                return colorType == 4 ? "gray with alpha" : null;
            }
        }

        return "not a readable png";
    }

    /// <summary>What a rotation would cost the file for this one chunk, or null when it costs nothing.</summary>
    private static string? Loss(Chunk chunk, byte colorType)
    {
        if (chunk.Is("IHDR"u8) || chunk.Is("IDAT"u8) || chunk.Is("IEND"u8))
        {
            return null;
        }

        if (chunk.Is("acTL"u8) || chunk.Is("fcTL"u8) || chunk.Is("fdAT"u8))
        {
            return "animated";
        }

        // A color key on a gray or true-color image decodes into an
        // alpha channel, and WPF has no keyed format to write back.
        if (chunk.Is("tRNS"u8) && colorType != 3)
        {
            return "color-keyed transparency";
        }

        // An upper-case first letter is a chunk the image cannot be
        // read without; an upper-case last letter is one PNG says an
        // editor must drop once the pixels change. Unknown, either one
        // would be lost or left wrong.
        var unknown = !SurvivesATurn.Contains(chunk.Name)
            && (char.IsAsciiLetterUpper((char)(chunk.Name >> 24)) || char.IsAsciiLetterUpper((char)(byte)chunk.Name));
        return unknown ? $"chunk {chunk.Text}" : null;
    }

    /// <summary>
    /// Bit depth and color type, the two header fields that say how a pixel
    /// is stored. Both files open with a 13-byte IHDR, so the two bytes sit
    /// at the same place in each.
    /// </summary>
    private static bool SamePixelFormat(Stream source, Stream encoded)
    {
        Span<byte> stored = stackalloc byte[2];
        Span<byte> written = stackalloc byte[2];
        if (!new ChunkReader(encoded, Signature.Length).TryNext(out var header) || !header.Is("IHDR"u8) || header.Length != 13)
        {
            return false;
        }

        source.Position = encoded.Position = header.Start + 8 + 8;
        source.ReadExactly(stored);
        encoded.ReadExactly(written);
        return stored.SequenceEqual(written);
    }

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
    /// included, and stays where it was: the stretch between two replaced
    /// chunks goes across in one piece, however many chunks it holds.
    /// </summary>
    private static void Splice(Stream source, Stream encoded, Stream target, bool swapAxes)
    {
        target.Write(Signature);

        // Source bytes before this are in the target already, or were replaced.
        var copied = (long)Signature.Length;
        var wroteImageData = false;
        var reader = new ChunkReader(source, copied);
        while (reader.TryNext(out var chunk) && !chunk.Is("IEND"u8))
        {
            var turnsResolution = swapAxes && chunk.Is("pHYs"u8) && chunk.Length == 9;
            if (!chunk.Is("IHDR"u8) && !chunk.Is("IDAT"u8) && !turnsResolution)
            {
                continue;
            }

            Copy(source, copied, chunk.Start - copied, target);
            copied = chunk.End;
            if (turnsResolution)
            {
                WriteTurnedResolution(ReadData(source, chunk), target);
            }
            else if (chunk.Is("IHDR"u8))
            {
                Copy(encoded, Signature.Length, chunk.Size, target);
            }
            else if (!wroteImageData)
            {
                CopyImageData(encoded, target);
                wroteImageData = true;
            }
        }

        // The chunks behind the pixels, and whatever a tool left after the
        // image's end, are still the user's.
        Copy(source, copied, source.Length - copied, target);
    }

    /// <summary>The encoder's image data, each run of it in one piece.</summary>
    private static void CopyImageData(Stream encoded, Stream target)
    {
        long start = 0;
        long end = 0;
        var reader = new ChunkReader(encoded, Signature.Length);
        while (reader.TryNext(out var chunk) && !chunk.Is("IEND"u8))
        {
            if (!chunk.Is("IDAT"u8))
            {
                continue;
            }

            if (chunk.Start != end)
            {
                Copy(encoded, start, end - start, target);
                start = chunk.Start;
            }

            end = chunk.End;
        }

        Copy(encoded, start, end - start, target);
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

        // In bands, so a large image is never held a third and fourth time,
        // and no taller than the image, so a small one costs a small buffer.
        var stride = Stride(expected);
        var rows = Math.Min(expected.PixelHeight, Math.Max(1, (4 << 20) / stride));
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
