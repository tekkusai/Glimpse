using System.Buffers.Binary;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using static SpacePreview.Tests.ImageTestFiles;

namespace SpacePreview.Tests;

/// <summary>
/// Rotating a PNG or a BMP rewrites the user's file, so it must cost the
/// file nothing: the pixels turn exactly, in the format they were stored
/// in, and everything else the file held is still there, byte for byte. A
/// file that cannot be rewritten that way is left alone.
/// </summary>
public sealed class ImageRotationCarryTests
{
    private static bool Saves(string path, int turns) =>
        ImageRotationService.SaveQuarterTurns(path, turns) == RotationSaveResult.Saved;

    private static bool Refuses(string path, int turns) =>
        ImageRotationService.SaveQuarterTurns(path, turns) == RotationSaveResult.CannotCarry;

    [Fact]
    public void APngKeepsEveryChunkARotationDoesNotOwn()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "rich.png");
        var profile = Enumerable.Range(0, 300).Select(i => (byte)(i * 7)).ToArray();
        var xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:exif=\"http://ns.adobe.com/exif/1.0/\"><exif:UserComment>Screenshot</exif:UserComment>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta>";
        PngChunk[] before =
        [
            new("gAMA", BigEndian(45455)),
            new("cHRM", [.. BigEndian(31270), .. BigEndian(32900), .. BigEndian(64000), .. BigEndian(33000), .. BigEndian(30000), .. BigEndian(60000), .. BigEndian(15000), .. BigEndian(6000)]),
            new("iCCP", [.. "Display"u8, 0, 0, .. Deflate(profile)]),
            new("pHYs", [.. BigEndian(3780), .. BigEndian(2835), 1]),
            new("tIME", [0x07, 0xEA, 10, 12, 9, 30, 0]),
            new("bKGD", [0, 255, 0, 128, 0, 64]),
            new("tEXt", [.. "Software"u8, 0, .. "A scanner"u8]),
            new("tEXt", [.. "parameters"u8, 0, .. "a prompt, steps: 20, seed: 7"u8]),
            new("zTXt", [.. "Comment"u8, 0, 0, .. Deflate("a compressed comment"u8.ToArray())]),
            new("iTXt", [.. "Title"u8, 0, 0, 0, .. "de"u8, 0, .. "Titel"u8, 0, .. Encoding.UTF8.GetBytes("Über den Wolken")]),
            new("iTXt", [.. "XML:com.adobe.xmp"u8, 0, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(xmp)]),
            new("eXIf", [.. "II"u8, 0x2A, 0, 8, 0, 0, 0, 1, 0, 0x12, 0x01, 3, 0, 1, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0]),
            new("prVt", [1, 2, 3, 4])
        ];
        PngChunk[] after = [new("tEXt", [.. "Source"u8, 0, .. "written after the pixels"u8])];
        var rows = RandomRows(6, 4, bitsPerPixel: 32, seed: 4);
        File.WriteAllBytes(path, Png(6, 4, 8, 6, rows, before, after, trailer: "left by a tool"u8.ToArray()));
        var source = ReadPng(File.ReadAllBytes(path));

        Assert.Equal(RotationPersistence.Reencode, ImageRotationService.Classify(path));
        Assert.True(Saves(path, 1));

        var turned = ReadPng(File.ReadAllBytes(path));
        Assert.Equal(source.Trailer, turned.Trailer);
        Assert.Equal(Names(source.Chunks), Names(turned.Chunks));
        foreach (var (was, now) in WithoutImageData(source.Chunks).Zip(WithoutImageData(turned.Chunks)))
        {
            switch (was.Name)
            {
                case "IHDR":
                    Assert.Equal((byte[])[.. BigEndian(4), .. BigEndian(6), 8, 6, 0, 0, 0], now.Data);
                    break;
                case "pHYs":
                    Assert.Equal((byte[])[.. BigEndian(2835), .. BigEndian(3780), 1], now.Data);
                    break;
                default:
                    Assert.Equal(was.Data, now.Data);
                    break;
            }
        }

        AssertTurned(rows, 6, 4, PngRows(File.ReadAllBytes(path)), bitsPerPixel: 32, turns: 1, (w, _) => w * 4);
    }

    [Fact]
    public void ARotatedPngGainsNoChunkItNeverHad()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bare.png");
        File.WriteAllBytes(path, Png(5, 3, 8, 2, RandomRows(5, 3, bitsPerPixel: 24, seed: 2)));

        Assert.True(Saves(path, 1));

        // No color space the file never claimed, no resolution it never had.
        Assert.Equal(new[] { "IHDR", "IDAT", "IEND" }, Names(ReadPng(File.ReadAllBytes(path)).Chunks));
    }

    [Theory]
    [InlineData(8, 2, 0)]
    [InlineData(8, 6, 0)]
    [InlineData(8, 0, 0)]
    [InlineData(16, 0, 0)]
    [InlineData(16, 2, 0)]
    [InlineData(16, 6, 0)]
    [InlineData(4, 0, 0)]
    [InlineData(2, 0, 0)]
    [InlineData(1, 0, 0)]
    // Indexed, with fewer colors than the bit depth could hold: WPF's own
    // encoder shrinks these and moves pixels between palette entries.
    [InlineData(8, 3, 5)]
    [InlineData(8, 3, 200)]
    [InlineData(4, 3, 5)]
    [InlineData(4, 3, 2)]
    [InlineData(2, 3, 3)]
    [InlineData(1, 3, 2)]
    public void APngTurnsExactlyInTheFormatItWasStoredIn(byte bitDepth, byte colorType, int paletteColors)
    {
        using var temp = new TempDirectory();
        const int width = 5, height = 3;
        var bitsPerPixel = PngChannels(colorType) * bitDepth;
        var rows = RandomRows(width, height, bitsPerPixel, seed: bitDepth * 10 + colorType, below: paletteColors);
        PngChunk[] palette = paletteColors == 0
            ? []
            :
            [
                new("PLTE", Enumerable.Range(0, paletteColors * 3).Select(i => (byte)(i * 37 + 11)).ToArray()),
                // Half see-through, then clear, then the rest opaque.
                new("tRNS", [128, 0])
            ];

        for (var turns = 1; turns <= 3; turns++)
        {
            var path = Path.Combine(temp.Path, $"format-{turns}.png");
            File.WriteAllBytes(path, Png(width, height, bitDepth, colorType, rows, palette));

            Assert.True(Saves(path, turns));

            var file = File.ReadAllBytes(path);
            var chunks = ReadPng(file).Chunks;
            Assert.Equal((bitDepth, colorType), (chunks[0].Data[8], chunks[0].Data[9]));
            Assert.Equal(palette.Select(chunk => chunk.Data), chunks.Where(chunk => chunk.Name is "PLTE" or "tRNS").Select(chunk => chunk.Data));
            AssertTurned(rows, width, height, PngRows(file), bitsPerPixel, turns, (w, _) => PngRowBytes(w, bitDepth, colorType));
        }
    }

    [Fact]
    public void FourQuarterTurnsBringAPngBackToWhatItWas()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "round.png");
        var rows = RandomRows(7, 5, bitsPerPixel: 24, seed: 9);
        PngChunk[] text = [new("pHYs", [.. BigEndian(3780), .. BigEndian(2835), 1]), new("tEXt", [.. "Comment"u8, 0, .. "kept"u8])];
        File.WriteAllBytes(path, Png(7, 5, 8, 2, rows, text));
        var before = WithoutImageData(ReadPng(File.ReadAllBytes(path)).Chunks).Select(chunk => (chunk.Name, Convert.ToHexString(chunk.Data)));

        for (var turn = 0; turn < 4; turn++)
        {
            Assert.True(Saves(path, 1));
        }

        var file = File.ReadAllBytes(path);
        Assert.Equal(before, WithoutImageData(ReadPng(file).Chunks).Select(chunk => (chunk.Name, Convert.ToHexString(chunk.Data))));
        Assert.Equal(rows, PngRows(file));
    }

    [Fact]
    public void AnInterlacedPngTurnsAndKeepsItsText()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "interlaced.png");
        var pixels = new byte[9 * 7 * 3];
        new Random(3).NextBytes(pixels);
        var encoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.On };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(9, 7, 96, 96, PixelFormats.Bgr24, null, pixels, 27)));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var (chunks, _) = ReadPng(File.ReadAllBytes(path));
        Assert.Equal(1, chunks[0].Data[12]);
        chunks.Insert(1, new PngChunk("tEXt", [.. "Comment"u8, 0, .. "kept"u8]));
        File.WriteAllBytes(path, WritePng(chunks));
        var view = ViewPixels(path, turns: 1);

        Assert.True(Saves(path, 1));

        Assert.Equal(view, ViewPixels(path, turns: 0));
        Assert.Contains(ReadPng(File.ReadAllBytes(path)).Chunks, chunk => chunk.Name == "tEXt" && chunk.Data.AsSpan().EndsWith("kept"u8));
    }

    // Gray with alpha, and a color key on gray or true color: WPF decodes
    // both into an alpha channel and can only write RGBA back.
    [Theory]
    [InlineData(8, 4, 0)]
    [InlineData(16, 4, 0)]
    [InlineData(8, 0, 2)]
    [InlineData(8, 2, 6)]
    public void APngThatWouldComeBackInAnotherFormatIsLeftAlone(byte bitDepth, byte colorType, int colorKeyBytes)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "kept.png");
        var rows = RandomRows(5, 3, PngChannels(colorType) * bitDepth, seed: 6);
        PngChunk[] key = colorKeyBytes == 0 ? [] : [new("tRNS", new byte[colorKeyBytes])];
        var file = Png(5, 3, bitDepth, colorType, rows, key);
        File.WriteAllBytes(path, file);

        Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(path));
        Assert.True(Refuses(path, 1));
        Assert.Equal(file, File.ReadAllBytes(path));
    }

    [Fact]
    public void AnAnimatedPngIsNeverRewritten()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "moving.png");
        var file = AnimatedPng();
        File.WriteAllBytes(path, file);

        Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(path));
        Assert.True(Refuses(path, 1));
        Assert.Equal(file, File.ReadAllBytes(path));
    }

    // A private chunk whose last letter is upper case depends on the
    // pixels; one whose first letter is upper case is needed to read them.
    // Glimpse knows neither, so it can neither turn them nor drop them.
    [Theory]
    [InlineData("prVT", true)]
    [InlineData("PRvt", true)]
    [InlineData("prVT", false)]
    public void AChunkGlimpseCannotCarryKeepsThePngAsItIs(string name, bool beforePixels)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "private.png");
        PngChunk[] chunk = [new(name, [1, 2, 3])];
        var file = Png(5, 3, 8, 2, RandomRows(5, 3, bitsPerPixel: 24, seed: 8), beforePixels ? chunk : null, beforePixels ? null : chunk);
        File.WriteAllBytes(path, file);

        // Only the chunks in front of the pixels are read when a file opens.
        Assert.Equal(beforePixels ? RotationPersistence.CannotCarry : RotationPersistence.Reencode, ImageRotationService.Classify(path));
        Assert.True(Refuses(path, 1));
        Assert.Equal(file, File.ReadAllBytes(path));
    }

    [Fact]
    public void ATurnedPngLooksAsTheViewDid()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "seen.png");
        File.WriteAllBytes(path, Png(6, 4, 8, 6, RandomRows(6, 4, bitsPerPixel: 32, seed: 12)));

        for (var turns = 1; turns <= 3; turns++)
        {
            var view = ViewPixels(path, turns);
            Assert.True(Saves(path, turns));
            Assert.Equal(view, ViewPixels(path, turns: 0));
        }
    }

    public static TheoryData<string> BmpKinds => new(BmpSamples.Keys);

    private static readonly Dictionary<string, Func<byte[]>> BmpSamples = new()
    {
        ["24 bit"] = () => Bmp(5, 3, 24, Stored(3, seed: 1), trailer: "left by a tool"u8.ToArray()),
        ["24 bit, top row first"] = () => Bmp(5, 3, 24, Stored(3, seed: 2), topDown: true),
        ["32 bit with a spare byte in use"] = () => Bmp(5, 3, 32, Stored(4, seed: 3)),
        ["32 bit with an alpha mask in a version 5 header"] = () => Bmp(5, 3, 32, Stored(4, seed: 4), headerSize: 124, compression: 3, masks: [0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000]),
        ["16 bit 5-6-5"] = () => Bmp(5, 3, 16, Stored(2, seed: 5), compression: 3, masks: [0xF800, 0x07E0, 0x001F]),
        ["8 bit, five colors"] = () => Bmp(5, 3, 8, Stored(1, seed: 6, below: 5), palette: Palette(5)),
        ["4 bit, five colors"] = () => Bmp(5, 3, 4, Stored(1, seed: 7, below: 5), palette: Palette(5)),
        ["1 bit"] = () => Bmp(37, 3, 1, Stored(1, seed: 8, below: 2), palette: Palette(2))
    };

    [Theory]
    [MemberData(nameof(BmpKinds))]
    public void ABmpTurnsByMovingItsPixelsAndKeepsItsHeaders(string kind)
    {
        using var temp = new TempDirectory();
        var before = BmpSamples[kind]();
        var width = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(18));
        var storedHeight = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(22));
        var height = Math.Abs(storedHeight);
        var bitsPerPixel = BinaryPrimitives.ReadInt16LittleEndian(before.AsSpan(28));
        var pixelOffset = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(10));
        var trailer = before[(pixelOffset + BmpRowBytes(width, bitsPerPixel) * height)..];

        for (var turns = 1; turns <= 3; turns++)
        {
            var path = Path.Combine(temp.Path, $"turn-{turns}.bmp");
            File.WriteAllBytes(path, before);
            var swap = turns % 2 == 1;
            var (turnedWidth, turnedHeight) = swap ? (height, width) : (width, height);

            Assert.Equal(RotationPersistence.Reencode, ImageRotationService.Classify(path));
            Assert.True(Saves(path, turns));

            var after = File.ReadAllBytes(path);

            // The headers, masks and palette, with only the sizes and the
            // resolution changed.
            var expected = before[..pixelOffset];
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(2), after.Length);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(18), turnedWidth);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(22), storedHeight < 0 ? -turnedHeight : turnedHeight);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(34), BmpRowBytes(turnedWidth, bitsPerPixel) * turnedHeight);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(38), swap ? 2835 : 3780);
            BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(42), swap ? 3780 : 2835);
            Assert.Equal(expected, after[..pixelOffset]);
            Assert.Equal(trailer, after[(pixelOffset + BmpRowBytes(turnedWidth, bitsPerPixel) * turnedHeight)..]);

            AssertTurned(BmpRows(before), width, height, BmpRows(after), bitsPerPixel, turns, (w, _) => BmpRowBytes(w, bitsPerPixel));
        }
    }

    [Fact]
    public void ATurnedBmpLooksAsTheViewDid()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "seen.bmp");
        File.WriteAllBytes(path, Bmp(6, 4, 24, Stored(3, seed: 14)));

        for (var turns = 1; turns <= 3; turns++)
        {
            var view = ViewPixels(path, turns);
            Assert.True(Saves(path, turns));
            Assert.Equal(view, ViewPixels(path, turns: 0));
        }
    }

    [Theory]
    // Run-length data, a color profile stored behind the pixels, and the
    // old OS/2 header: none can be turned by moving pixels.
    [InlineData(40, 1, 0u)]
    [InlineData(124, 0, 0x4D424544u)]
    [InlineData(12, 0, 0u)]
    public void ABmpWhosePixelsCannotBeMovedIsLeftAlone(int headerSize, int compression, uint colorSpace)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "kept.bmp");
        var file = Bmp(5, 3, 8, Stored(1, seed: 9, below: 5), headerSize: Math.Max(40, headerSize), compression: compression, palette: Palette(5));
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(14), headerSize);
        if (colorSpace != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(70), colorSpace);
        }

        File.WriteAllBytes(path, file);

        Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(path));
        Assert.True(Refuses(path, 1));
        Assert.Equal(file, File.ReadAllBytes(path));
    }

    /// <summary>Every pixel of <paramref name="turned"/> holds exactly what its source pixel held.</summary>
    private static void AssertTurned(byte[] rows, int width, int height, byte[] turned, int bitsPerPixel, int turns, Func<int, int, int> rowBytes)
    {
        var (turnedWidth, turnedHeight) = turns % 2 == 1 ? (height, width) : (width, height);
        var sourceRowBytes = rowBytes(width, height);
        var turnedRowBytes = rowBytes(turnedWidth, turnedHeight);
        Assert.Equal(turnedRowBytes * turnedHeight, turned.Length);
        for (var y = 0; y < turnedHeight; y++)
        {
            for (var x = 0; x < turnedWidth; x++)
            {
                var from = BeforeTurn(turns, x, y, width, height);
                Assert.Equal(
                    Pixel(rows, sourceRowBytes, bitsPerPixel, from.X, from.Y),
                    Pixel(turned, turnedRowBytes, bitsPerPixel, x, y));
            }
        }
    }

    /// <summary>Rows of random pixels, each below <paramref name="below"/> when the image is indexed.</summary>
    private static byte[] RandomRows(int width, int height, int bitsPerPixel, int seed, int below = 0)
    {
        var random = new Random(seed);
        var rowBytes = (width * bitsPerPixel + 7) / 8;
        var rows = new byte[rowBytes * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bitsPerPixel >= 8)
                {
                    for (var part = 0; part < bitsPerPixel / 8; part++)
                    {
                        rows[y * rowBytes + x * bitsPerPixel / 8 + part] = (byte)random.Next(below == 0 ? 256 : below);
                    }
                }
                else
                {
                    var value = random.Next(below == 0 ? 1 << bitsPerPixel : below);
                    var bit = x * bitsPerPixel;
                    rows[y * rowBytes + bit / 8] |= (byte)(value << (8 - bitsPerPixel - bit % 8));
                }
            }
        }

        return rows;
    }

    private static Func<int, int, byte[]> Stored(int bytes, int seed, int below = 256)
    {
        var random = new Random(seed);
        return (_, _) => Enumerable.Range(0, bytes).Select(_ => (byte)random.Next(below)).ToArray();
    }

    private static byte[] Palette(int colors) => Enumerable.Range(0, colors * 4).Select(i => i % 4 == 3 ? (byte)0 : (byte)(i * 29 + 5)).ToArray();

    /// <summary>The chunk names, with a run of image data counted once: an encoder may split it as it likes.</summary>
    private static List<string> Names(List<PngChunk> chunks)
    {
        var names = new List<string>();
        foreach (var chunk in chunks)
        {
            if (chunk.Name != "IDAT" || names.LastOrDefault() != "IDAT")
            {
                names.Add(chunk.Name);
            }
        }

        return names;
    }

    private static IEnumerable<PngChunk> WithoutImageData(List<PngChunk> chunks) => chunks.Where(chunk => chunk.Name != "IDAT");

    internal static byte[] AnimatedPng()
    {
        // One frame control and the default image, then a second frame.
        byte[] control = [.. BigEndian(0), .. BigEndian(5), .. BigEndian(3), .. BigEndian(0), .. BigEndian(0), 0, 1, 0, 10, 0, 0];
        return Png(
            5,
            3,
            8,
            6,
            RandomRows(5, 3, bitsPerPixel: 32, seed: 5),
            [new PngChunk("acTL", [.. BigEndian(2), .. BigEndian(0)]), new PngChunk("fcTL", control)],
            [new PngChunk("fcTL", [.. BigEndian(1), .. control.AsSpan(4)]), new PngChunk("fdAT", [.. BigEndian(2), .. Deflate(new byte[3 * 21])])]);
    }

    /// <summary>What the preview shows for this file after <paramref name="turns"/> clockwise quarter turns of the view.</summary>
    private static byte[] ViewPixels(string path, int turns)
    {
        var decoded = PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path))!;
        var shown = new FormatConvertedBitmap(
            ImageOrientation.Apply(decoded, ImageOrientation.FromQuarterTurns(turns)), PixelFormats.Bgra32, null, 0);
        var pixels = new byte[shown.PixelWidth * shown.PixelHeight * 4];
        shown.CopyPixels(pixels, shown.PixelWidth * 4, 0);
        return [.. BitConverter.GetBytes(shown.PixelWidth), .. pixels];
    }
}
