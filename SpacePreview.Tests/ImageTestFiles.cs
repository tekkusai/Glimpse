using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace SpacePreview.Tests;

/// <summary>
/// PNG and BMP files written and read byte by byte, without WPF, so a test
/// can say exactly what a file held before a rotation and what it holds
/// after: every chunk, every header field and every stored sample.
/// </summary>
internal static class ImageTestFiles
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    internal sealed record PngChunk(string Name, byte[] Data);

    /// <summary>
    /// A PNG from stored samples. <paramref name="rows"/> holds the image's
    /// rows end to end, each as PNG packs it, without filter bytes.
    /// </summary>
    public static byte[] Png(
        int width,
        int height,
        byte bitDepth,
        byte colorType,
        byte[] rows,
        IEnumerable<PngChunk>? beforeData = null,
        IEnumerable<PngChunk>? afterData = null,
        byte[]? trailer = null)
    {
        var rowBytes = PngRowBytes(width, bitDepth, colorType);
        Assert.Equal(rowBytes * height, rows.Length);
        var filtered = new byte[(rowBytes + 1) * height];
        for (var y = 0; y < height; y++)
        {
            rows.AsSpan(y * rowBytes, rowBytes).CopyTo(filtered.AsSpan(y * (rowBytes + 1) + 1));
        }

        return WritePng(
            [
                new PngChunk("IHDR", [.. BigEndian(width), .. BigEndian(height), bitDepth, colorType, 0, 0, 0]),
                .. beforeData ?? [],
                new PngChunk("IDAT", Deflate(filtered)),
                .. afterData ?? [],
                new PngChunk("IEND", [])
            ],
            trailer);
    }

    /// <summary>A PNG from its chunks, each given its checksum.</summary>
    public static byte[] WritePng(IEnumerable<PngChunk> chunks, byte[]? trailer = null)
    {
        using var file = new MemoryStream();
        file.Write(PngSignature);
        foreach (var chunk in chunks)
        {
            WriteChunk(file, chunk);
        }

        file.Write(trailer ?? []);
        return file.ToArray();
    }

    /// <summary>Every chunk of a PNG in order, and whatever follows its IEND.</summary>
    public static (List<PngChunk> Chunks, byte[] Trailer) ReadPng(byte[] file)
    {
        Assert.True(file.AsSpan(0, 8).SequenceEqual(PngSignature));
        var chunks = new List<PngChunk>();
        var at = 8;
        while (true)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at));
            var name = Encoding.ASCII.GetString(file, at + 4, 4);
            var data = file.AsSpan(at + 8, length).ToArray();
            Assert.Equal(Crc32(file.AsSpan(at + 4, 4 + length)), BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(at + 8 + length)));
            chunks.Add(new PngChunk(name, data));
            at += 12 + length;
            if (name == "IEND")
            {
                return (chunks, file[at..]);
            }
        }
    }

    /// <summary>A PNG's stored samples, row after row, as the file packs them: inflated and unfiltered, nothing more.</summary>
    public static byte[] PngRows(byte[] file)
    {
        var (chunks, _) = ReadPng(file);
        var header = chunks[0].Data;
        var width = (int)BinaryPrimitives.ReadUInt32BigEndian(header);
        var height = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4));
        Assert.Equal(0, header[12]);
        var rowBytes = PngRowBytes(width, header[8], header[9]);
        var step = Math.Max(1, PngChannels(header[9]) * header[8] / 8);

        using var packed = new MemoryStream(chunks.Where(chunk => chunk.Name == "IDAT").SelectMany(chunk => chunk.Data).ToArray());
        using var inflate = new ZLibStream(packed, CompressionMode.Decompress);
        var filtered = new byte[(rowBytes + 1) * height];
        inflate.ReadExactly(filtered);

        var rows = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            var filter = filtered[y * (rowBytes + 1)];
            var line = filtered.AsSpan(y * (rowBytes + 1) + 1, rowBytes);
            for (var x = 0; x < rowBytes; x++)
            {
                int left = x >= step ? rows[y * rowBytes + x - step] : 0;
                int up = y > 0 ? rows[(y - 1) * rowBytes + x] : 0;
                int upLeft = x >= step && y > 0 ? rows[(y - 1) * rowBytes + x - step] : 0;
                var predicted = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => up,
                    3 => (left + up) / 2,
                    _ => Paeth(left, up, upLeft)
                };
                rows[y * rowBytes + x] = (byte)(line[x] + predicted);
            }
        }

        return rows;
    }

    public static int PngRowBytes(int width, byte bitDepth, byte colorType) =>
        (width * PngChannels(colorType) * bitDepth + 7) / 8;

    public static int PngChannels(byte colorType) => colorType switch
    {
        0 or 3 => 1,
        4 => 2,
        2 => 3,
        _ => 4
    };

    /// <summary>The value of one pixel in rows packed from the high end of each byte, as PNG and BMP both pack them.</summary>
    public static ulong Pixel(ReadOnlySpan<byte> rows, int rowBytes, int bitsPerPixel, int x, int y)
    {
        var row = rows.Slice(y * rowBytes, rowBytes);
        if (bitsPerPixel < 8)
        {
            var bit = x * bitsPerPixel;
            return (ulong)(row[bit / 8] >> (8 - bitsPerPixel - bit % 8)) & ((1UL << bitsPerPixel) - 1);
        }

        ulong value = 0;
        foreach (var part in row.Slice(x * bitsPerPixel / 8, bitsPerPixel / 8))
        {
            value = (value << 8) | part;
        }

        return value;
    }

    /// <summary>Where a pixel of an image turned clockwise by quarter turns came from.</summary>
    public static (int X, int Y) BeforeTurn(int turns, int x, int y, int widthBefore, int heightBefore) => turns switch
    {
        1 => (y, heightBefore - 1 - x),
        2 => (widthBefore - 1 - x, heightBefore - 1 - y),
        3 => (widthBefore - 1 - y, x),
        _ => (x, y)
    };

    /// <summary>
    /// A BMP with plain rows. <paramref name="pixel"/> gives each pixel's
    /// stored bytes (or its value, below eight bits), by its place as seen.
    /// </summary>
    public static byte[] Bmp(
        int width,
        int height,
        int bitsPerPixel,
        Func<int, int, byte[]> pixel,
        int headerSize = 40,
        int compression = 0,
        uint[]? masks = null,
        byte[]? palette = null,
        bool topDown = false,
        byte[]? trailer = null)
    {
        var rowBytes = BmpRowBytes(width, bitsPerPixel);
        var masksAfterHeader = masks is not null && headerSize == 40 ? masks.Length * 4 : 0;
        var pixelOffset = 14 + headerSize + masksAfterHeader + (palette?.Length ?? 0);
        var file = new byte[pixelOffset + rowBytes * height + (trailer?.Length ?? 0)];
        var span = file.AsSpan();
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], pixelOffset);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], headerSize);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], width);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], topDown ? -height : height);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], (short)bitsPerPixel);
        BinaryPrimitives.WriteInt32LittleEndian(span[30..], compression);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], rowBytes * height);
        BinaryPrimitives.WriteInt32LittleEndian(span[38..], 3780);
        BinaryPrimitives.WriteInt32LittleEndian(span[42..], 2835);
        BinaryPrimitives.WriteInt32LittleEndian(span[46..], palette is null ? 0 : palette.Length / 4);
        for (var i = 0; i < (masks?.Length ?? 0); i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[(54 + i * 4)..], masks![i]);
        }

        if (headerSize >= 108)
        {
            // "sRGB": a named color space, nothing stored behind the pixels.
            BinaryPrimitives.WriteUInt32LittleEndian(span[70..], 0x73524742);
        }

        palette?.CopyTo(span[(14 + headerSize + masksAfterHeader)..]);
        for (var y = 0; y < height; y++)
        {
            var row = pixelOffset + rowBytes * (topDown ? y : height - 1 - y);
            for (var x = 0; x < width; x++)
            {
                var stored = pixel(x, y);
                if (bitsPerPixel >= 8)
                {
                    stored.CopyTo(span[(row + x * bitsPerPixel / 8)..]);
                }
                else
                {
                    var bit = x * bitsPerPixel;
                    file[row + bit / 8] |= (byte)(stored[0] << (8 - bitsPerPixel - bit % 8));
                }
            }
        }

        trailer?.CopyTo(span[(pixelOffset + rowBytes * height)..]);
        return file;
    }

    public static int BmpRowBytes(int width, int bitsPerPixel) => (width * bitsPerPixel + 31) / 32 * 4;

    /// <summary>A BMP's rows top first, whichever way the file stores them.</summary>
    public static byte[] BmpRows(byte[] file)
    {
        var span = file.AsSpan();
        var width = BinaryPrimitives.ReadInt32LittleEndian(span[18..]);
        var height = BinaryPrimitives.ReadInt32LittleEndian(span[22..]);
        var rowBytes = BmpRowBytes(width, BinaryPrimitives.ReadInt16LittleEndian(span[28..]));
        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(span[10..]);
        var rows = new byte[rowBytes * Math.Abs(height)];
        for (var y = 0; y < Math.Abs(height); y++)
        {
            var row = pixelOffset + rowBytes * (height < 0 ? y : height - 1 - y);
            span.Slice(row, rowBytes).CopyTo(rows.AsSpan(y * rowBytes));
        }

        return rows;
    }

    public static byte[] Deflate(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var deflate = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return buffer.ToArray();
    }

    public static byte[] BigEndian(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void WriteChunk(Stream file, PngChunk chunk)
    {
        var name = Encoding.ASCII.GetBytes(chunk.Name);
        file.Write(BigEndian(chunk.Data.Length));
        file.Write(name);
        file.Write(chunk.Data);
        var checksum = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, Crc32([.. name, .. chunk.Data]));
        file.Write(checksum);
    }

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

    private static int Paeth(int left, int up, int upLeft)
    {
        var estimate = left + up - upLeft;
        var toLeft = Math.Abs(estimate - left);
        var toUp = Math.Abs(estimate - up);
        var toUpLeft = Math.Abs(estimate - upLeft);
        return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
    }
}
