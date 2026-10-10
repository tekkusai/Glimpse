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

    private static void WritePsd(string path, int width, int height, byte[][] planes, bool packBits, int depth = 8)
    {
        using var writer = new BinaryWriter(File.Create(path));
        void U16(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((ushort)value));
        void U32(int value) => writer.Write(BinaryPrimitives.ReverseEndianness((uint)value));
        writer.Write("8BPS"u8);
        U16(1);
        writer.Write(new byte[6]);
        U16(planes.Length);
        U32(height);
        U32(width);
        U16(depth);
        U16(3);
        U32(0); // color mode data
        U32(0); // image resources
        U32(0); // layer and mask info
        U16(packBits ? 1 : 0);
        if (!packBits)
        {
            foreach (var plane in planes)
            {
                writer.Write(plane);
            }

            return;
        }

        // Literal runs of at most 128 bytes: valid PackBits, if not small.
        var rows = planes.SelectMany(plane => Enumerable.Range(0, height)
            .Select(row => Literal(plane.AsSpan(row * width, width).ToArray()))).ToList();
        foreach (var row in rows)
        {
            U16(row.Length);
        }

        foreach (var row in rows)
        {
            writer.Write(row);
        }
    }

    private static byte[] Literal(byte[] row)
    {
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
