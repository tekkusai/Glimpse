using System.Buffers.Binary;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>Tiny generated Photoshop files, raw and PackBits, 8 and 16 bit.</summary>
public sealed class PsdPreviewLoaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SmallCompositeDecodesExactly(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "small.psd");
        // Red, green, blue planes of a 3 x 2 image.
        byte[][] planes =
        [
            [255, 0, 0, 10, 20, 30],
            [0, 255, 0, 40, 50, 60],
            [0, 0, 255, 70, 80, 90]
        ];
        WritePsd(path, width: 3, height: 2, planes, packBits);

        var bitmap = PsdPreviewLoader.TryLoad(path)!;

        Assert.Equal((3, 2), (bitmap.PixelWidth, bitmap.PixelHeight));
        var pixels = Pixels(bitmap);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 70, 40, 10, 255 }, pixels[12..16]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DownsampledCompositeAveragesInsteadOfSkipping(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "wide.psd");
        // 8200 px wide forces a 3x downsample. Columns alternate black and
        // white, so sampling one pixel per box would show only 0 or 255.
        const int width = 8200;
        const int height = 3;
        var plane = new byte[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)(i % width % 2 == 0 ? 0 : 255);
        }

        WritePsd(path, width, height, [plane, plane, plane], packBits);

        var bitmap = PsdPreviewLoader.TryLoad(path)!;

        Assert.Equal((2734, 1), (bitmap.PixelWidth, bitmap.PixelHeight));
        var pixels = Pixels(bitmap);
        for (var x = 0; x < bitmap.PixelWidth - 1; x++)
        {
            Assert.InRange(pixels[x * 4], 80, 175);
        }
    }

    [Fact]
    public void AskedForMoreTheCompositeComesAtItsOwnSize()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "wide.psd");
        const int width = 8200;
        const int height = 3;
        var plane = new byte[width * height];
        for (var i = 0; i < plane.Length; i++)
        {
            plane[i] = (byte)(i % width % 2 == 0 ? 0 : 255);
        }

        WritePsd(path, width, height, [plane, plane, plane], packBits: true);

        // Every column as the file has it: nothing averaged away.
        var whole = PsdPreviewLoader.TryLoad(path, longSide: width)!;
        Assert.Equal((width, height), (whole.PixelWidth, whole.PixelHeight));
        var pixels = Pixels(whole);
        Assert.Equal(0, pixels[0]);
        Assert.Equal(255, pixels[4]);
        Assert.Equal(0, pixels[8]);

        // Short of its own size it still comes down by a whole number.
        Assert.Equal(4100, PsdPreviewLoader.TryLoad(path, longSide: 5000)!.PixelWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALargeDocumentDecodesLikeAnyOther(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "large.psb");
        byte[][] planes =
        [
            [255, 0, 0, 10, 20, 30],
            [0, 255, 0, 40, 50, 60],
            [0, 0, 255, 70, 80, 90]
        ];

        // Layer data in front of the picture, as a real file has: its
        // length is eight bytes here and four in a PSD.
        WritePsd(path, width: 3, height: 2, planes, packBits, large: true, layerBytes: 37);

        var bitmap = PsdPreviewLoader.TryLoad(path)!;

        Assert.Equal((3, 2), (bitmap.PixelWidth, bitmap.PixelHeight));
        var pixels = Pixels(bitmap);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 70, 40, 10, 255 }, pixels[12..16]);
    }

    [Fact]
    public void AFileThatClaimsMoreThanItHoldsCostsNothing()
    {
        using var temp = new TempDirectory();

        // Twenty-six bytes of header and three empty sections, claiming a
        // 300,000 pixel square with every row packed.
        var claims = Path.Combine(temp.Path, "claims.psb");
        WritePsd(claims, width: 3, height: 2, [new byte[6], new byte[6], new byte[6]], packBits: true, large: true);
        var bytes = File.ReadAllBytes(claims);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), 300_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), 300_000);
        File.WriteAllBytes(claims, bytes);

        // A real picture whose row table says one row is far longer than a
        // packed row can be, with the bytes there to back the claim.
        var longRow = Path.Combine(temp.Path, "long-row.psb");
        WritePsd(longRow, width: 3, height: 2, [new byte[6], new byte[6], new byte[6]], packBits: true, large: true);
        bytes = File.ReadAllBytes(longRow);
        var table = 26 + 4 + 4 + 8 + 2;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(table), 2_000_000);
        File.WriteAllBytes(longRow, [.. bytes, .. new byte[2_000_000]]);

        // The same claim with rows stored as they are: no table to miss.
        var raw = Path.Combine(temp.Path, "raw.psb");
        WritePsd(raw, width: 3, height: 2, [new byte[6], new byte[6], new byte[6]], packBits: false, large: true);
        bytes = File.ReadAllBytes(raw);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), 300_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), 300_000);
        File.WriteAllBytes(raw, bytes);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Null(PsdPreviewLoader.TryLoad(claims));
        Assert.Null(PsdPreviewLoader.TryLoad(longRow));
        Assert.Null(PsdPreviewLoader.TryLoad(raw));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 31)]
    [InlineData(true, 31)]
    public void PackedRowsTooShortToFillTheirRowsAreNotBelieved(bool large, int packedRow)
    {
        using var temp = new TempDirectory();

        // Two thousand pixels a row. PackBits makes 128 bytes of two at
        // most, so 32 bytes are the fewest a row can be. A table of
        // shorter rows claims a picture the file does not hold: with rows
        // of no bytes at all, a 3.6 MB file claimed 300,000 pixels square,
        // and every one of them was visited to bring it down.
        var path = Path.Combine(temp.Path, large ? "short-rows.psb" : "short-rows.psd");
        WritePackedRows(path, large, side: 2000, [.. Enumerable.Repeat((byte)0x80, packedRow)]);

        Assert.Null(PsdPreviewLoader.TryLoad(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFewestBytesThatFillARowAreBelieved(bool large)
    {
        using var temp = new TempDirectory();

        // A flat color as Photoshop packs it: fifteen runs of 128 and one
        // of 80, in 32 bytes.
        byte[] row = [.. Enumerable.Repeat(new byte[] { 0x81, 200 }, 15).SelectMany(run => run), 0xB1, 200];
        var path = Path.Combine(temp.Path, large ? "flat.psb" : "flat.psd");
        WritePackedRows(path, large, side: 2000, row);

        var bitmap = PsdPreviewLoader.TryLoad(path);

        Assert.NotNull(bitmap);
        Assert.Equal((2000, 2000), (bitmap!.PixelWidth, bitmap.PixelHeight));
        var pixels = Pixels(bitmap);
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 200, 200, 200, 255 }, pixels[^4..]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrayscaleIsTheSameValueInEveryColor(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "gray.psd");
        WritePsd(path, width: 2, height: 2, [[0, 128, 255, 7]], packBits, mode: 1);

        Assert.Equal(
            new byte[] { 0, 0, 0, 255, 128, 128, 128, 255, 255, 255, 255, 255, 7, 7, 7, 255 },
            Pixels(PsdPreviewLoader.TryLoad(path)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GrayscaleTakesOneExtraChannelAsTransparency(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "gray-alpha.psd");
        WritePsd(path, width: 2, height: 1, [[10, 20], [255, 0]], packBits, mode: 1);

        Assert.Equal(
            new byte[] { 10, 10, 10, 255, 20, 20, 20, 0 },
            Pixels(PsdPreviewLoader.TryLoad(path)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CmykInksAreMultipliedWithTheKey(bool packBits)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "print.psd");
        // Stored inverted: 255 is no ink. Paper, full cyan, full black,
        // and a mix that rounds down in every channel.
        byte[][] planes =
        [
            [255, 0, 255, 200],
            [255, 255, 255, 100],
            [255, 255, 255, 50],
            [255, 255, 0, 128]
        ];
        WritePsd(path, width: 4, height: 1, planes, packBits, mode: 4);

        Assert.Equal(
            new byte[]
            {
                255, 255, 255, 255,
                255, 255, 0, 255,
                0, 0, 0, 255,
                25, 50, 100, 255
            },
            Pixels(PsdPreviewLoader.TryLoad(path)!));
    }

    [Fact]
    public void ACmykSpotChannelIsNotTransparency()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "spot.psd");
        // A fifth channel of zeros: a cut line or white ink, not holes.
        byte[][] planes = [[255, 255], [255, 255], [255, 255], [255, 255], [0, 0]];
        WritePsd(path, width: 2, height: 1, planes, packBits: true, mode: 4);

        Assert.Equal(
            new byte[] { 255, 255, 255, 255, 255, 255, 255, 255 },
            Pixels(PsdPreviewLoader.TryLoad(path)!));
    }

    [Fact]
    public void ABroughtDownPictureAveragesEveryColorAndItsTransparency()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "wide-alpha.psd");
        // 8200 wide comes down by three. Each channel is flat but for its
        // last box, which holds one column: 8200 = 2733 threes and a one.
        const int width = 8200;
        byte[] Flat(byte value, byte last)
        {
            var plane = Enumerable.Repeat(value, width).ToArray();
            plane[^1] = last;
            return plane;
        }

        WritePsd(path, width, height: 1, [Flat(30, 90), Flat(60, 120), Flat(200, 10), Flat(255, 40)], packBits: true);

        var pixels = Pixels(PsdPreviewLoader.TryLoad(path)!);
        Assert.Equal(2734 * 4, pixels.Length);
        Assert.Equal(new byte[] { 200, 60, 30, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 200, 60, 30, 255 }, pixels[(2732 * 4)..(2733 * 4)]);
        Assert.Equal(new byte[] { 10, 120, 90, 40 }, pixels[(2733 * 4)..]);
    }

    [Fact]
    public void EachPlaneStartsWhereThePlanesBeforeItEnd()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "alpha.psd");
        // Flat rows pack to two bytes and the rest to four, so the planes
        // differ in length and a plane read from a guessed offset decodes
        // its neighbor's bytes.
        byte[][] planes =
        [
            [200, 200, 200, 200, 200, 200],
            [1, 2, 3, 4, 5, 6],
            [9, 9, 9, 7, 8, 9],
            [255, 255, 255, 0, 128, 255]
        ];
        WritePsd(path, width: 3, height: 2, planes, packBits: true);

        var pixels = Pixels(PsdPreviewLoader.TryLoad(path)!);

        Assert.Equal(
            new byte[]
            {
                9, 1, 200, 255, 9, 2, 200, 255, 9, 3, 200, 255,
                7, 4, 200, 0, 8, 5, 200, 128, 9, 6, 200, 255
            },
            pixels);
    }

    [Fact]
    public void SixteenBitRawTakesTheHighByte()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deep.psd");
        byte[][] planes = [[0x12, 0x34], [0x56, 0x78], [0x9A, 0xBC]];
        WritePsd(path, width: 1, height: 1, planes, packBits: false, depth: 16);

        var pixels = Pixels(PsdPreviewLoader.TryLoad(path)!);

        Assert.Equal(new byte[] { 0x9A, 0x56, 0x12, 255 }, pixels);
    }

    [Fact]
    public void TruncatedOrForeignFilesAreNotPreviews()
    {
        using var temp = new TempDirectory();
        var truncated = Path.Combine(temp.Path, "cut.psd");
        WritePsd(truncated, width: 3, height: 2, [new byte[6], new byte[6], new byte[6]], packBits: false);
        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated)[..^4]);
        var foreign = Path.Combine(temp.Path, "foreign.psd");
        File.WriteAllText(foreign, "not a photoshop file at all");

        Assert.Null(PsdPreviewLoader.TryLoad(truncated));
        Assert.Null(PsdPreviewLoader.TryLoad(foreign));
    }

    [Fact]
    public void CancelledDecodeStops()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "small.psd");
        WritePsd(path, width: 3, height: 2, [new byte[6], new byte[6], new byte[6]], packBits: true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => PsdPreviewLoader.TryLoad(path, cancelled.Token));
    }

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    internal static void WritePsd(
        string path,
        int width,
        int height,
        byte[][] planes,
        bool packBits,
        int depth = 8,
        bool large = false,
        int layerBytes = 0,
        int mode = 3)
    {
        using var writer = new BinaryWriter(File.Create(path));
        void U16(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((ushort)value));
        void U32(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((uint)value));
        writer.Write("8BPS"u8);
        U16(large ? 2 : 1);
        writer.Write(new byte[6]);
        U16(planes.Length);
        U32(height);
        U32(width);
        U16(depth);
        U16(mode);
        U32(0); // color mode data
        U32(0); // image resources
        if (large)
        {
            U32(0); // layer and mask info: eight bytes of length in a large document
        }

        U32(layerBytes);
        writer.Write(Enumerable.Repeat((byte)0xEE, layerBytes).ToArray());
        U16(packBits ? 1 : 0);
        if (!packBits)
        {
            foreach (var plane in planes)
            {
                writer.Write(plane);
            }

            return;
        }

        var rows = planes.SelectMany(plane => Enumerable.Range(0, height)
            .Select(row => PackBits(plane.AsSpan(row * width, width).ToArray()))).ToList();
        foreach (var row in rows)
        {
            if (large)
            {
                U32(row.Length);
            }
            else
            {
                U16(row.Length);
            }
        }

        foreach (var row in rows)
        {
            writer.Write(row);
        }
    }

    /// <summary>A square RGB picture whose every row, in every plane, is the same packed bytes.</summary>
    private static void WritePackedRows(string path, bool large, int side, byte[] packedRow)
    {
        using var writer = new BinaryWriter(File.Create(path));
        void U16(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((ushort)value));
        void U32(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((uint)value));
        writer.Write("8BPS"u8);
        U16(large ? 2 : 1);
        writer.Write(new byte[6]);
        U16(3);
        U32(side);
        U32(side);
        U16(8);
        U16(3);
        U32(0);
        U32(0);
        if (large)
        {
            U32(0);
        }

        U32(0);
        U16(1);
        for (var row = 0; row < 3 * side; row++)
        {
            if (large)
            {
                U32(packedRow.Length);
            }
            else
            {
                U16(packedRow.Length);
            }
        }

        for (var row = 0; row < 3 * side; row++)
        {
            writer.Write(packedRow);
        }
    }

    // A row of one value is one repeat run; any other row is literal runs
    // of at most 128 bytes. Valid PackBits, if not the smallest.
    private static byte[] PackBits(byte[] row)
    {
        if (row.Length is > 1 and <= 128 && row.All(value => value == row[0]))
        {
            return [(byte)(1 - row.Length), row[0]];
        }

        var encoded = new List<byte>();
        for (var at = 0; at < row.Length; at += 128)
        {
            var count = Math.Min(128, row.Length - at);
            encoded.Add((byte)(count - 1));
            encoded.AddRange(row.AsSpan(at, count).ToArray());
        }

        return [.. encoded];
    }
}
