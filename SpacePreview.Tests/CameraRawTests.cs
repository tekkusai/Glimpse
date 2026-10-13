using System.Buffers.Binary;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A camera's RAW file opens as the photo it is, through Windows' own RAW
/// decoder. The tests that need that decoder write a small DNG of their
/// own, and pass without a word on a PC that has none; the rest hold on
/// every PC. These windows sit on a monitor of the test's own.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CameraRawTests
{
    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    [Theory]
    [InlineData("DSC00412.ARW")]
    [InlineData("IMG_0412.CR3")]
    [InlineData("IMG_0412.cr2")]
    [InlineData("_DSC0412.NEF")]
    [InlineData("DSCF0412.RAF")]
    [InlineData("L1000412.dng")]
    [InlineData("P1000412.RW2")]
    [InlineData("P4120412.ORF")]
    public void ACamerasRawFileIsAnImage(string name)
    {
        Assert.True(FileKinds.IsCameraRaw(name));
        Assert.True(FileKinds.IsImage(name));

        // Its name says what it is, so its first bytes are not asked: they
        // would say TIFF for most of these.
        Assert.False(FileKinds.CanUseSignatureRouting(name));
    }

    [Theory]
    [InlineData("dump.raw")]
    [InlineData("wifi.cap")]
    [InlineData("scan.tif")]
    public void ANameOtherThingsCarryIsNotTakenForARaw(string name) =>
        Assert.False(FileKinds.IsCameraRaw(name));

    // Most RAW formats are TIFF underneath, and a TIFF's turn is saved by
    // writing its orientation tag. A RAW is the negative: never written.
    [Theory]
    [InlineData(".dng")]
    [InlineData(".nef")]
    [InlineData(".cr2")]
    [InlineData(".arw")]
    public void ATurnOfARawIsNeverWrittenToIt(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteTiff(temp, "photo" + extension);
        var before = File.ReadAllBytes(path);

        Assert.Equal(RotationPersistence.ViewOnly, ImageRotationService.Classify(path));
        Assert.Equal(RotationSaveResult.CannotCarry, ImageRotationService.SaveQuarterTurns(path, 1));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // Without the RAW decoder, Windows opens a NEF as the TIFF it is built
    // on and returns the picture stored first, a thumbnail. The file here
    // is that case on any PC: a plain TIFF under a RAW name.
    [Fact]
    public void ARawOnlyAnotherDecoderOpensIsNotShownAsThePhoto()
    {
        using var temp = new TempDirectory();
        var path = WriteTiff(temp, "_DSC0412.nef");

        Assert.Null(ImageHeader.TryRead(path));
        Assert.Null(PreviewWindow.TryDecodeImage(path, null));
    }

    [Fact]
    public Task ARawThatCannotBeReadGetsACardAndNoPicture() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteTiff(temp, "_DSC0412.nef");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window));

            Assert.Null(Viewer(window));
            Assert.DoesNotContain("40×30", window.SubtitleText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public void ARawPhotoIsReadAtItsFullSizeAndKnowsWhichWayIsUp()
    {
        if (MediaCodecs.HasImageDecoder(MediaCodecs.RawContainer) != true)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = WriteDng(temp, "L1000412.dng", 96, 64, orientation: 6);

        var header = ImageHeader.TryRead(path);
        Assert.NotNull(header);
        Assert.Equal((96, 64, 6), (header.StoredWidth, header.StoredHeight, header.Orientation));
        Assert.Equal((64, 96), (header.Width, header.Height));

        var bitmap = PreviewWindow.TryDecodeImage(path, header);
        Assert.NotNull(bitmap);
        Assert.Equal((96, 64), (bitmap.PixelWidth, bitmap.PixelHeight));

        // The file paints its first corner red and its far side white: a
        // decode that returns those is a decode of this file.
        var (red, green, blue) = Pixel(bitmap, 8, 6);
        Assert.True(red > 200 && green < 90 && blue < 90, $"the red corner is {red},{green},{blue}");
        var (farRed, farGreen, farBlue) = Pixel(bitmap, 90, 40);
        Assert.True(farRed > 200 && farGreen > 200 && farBlue > 200, $"the white side is {farRed},{farGreen},{farBlue}");
    }

    [Fact]
    public Task ARawPhotoOpensInTheImageViewerTheWayItWasShot() => WpfApp.RunAsync(async () =>
    {
        if (MediaCodecs.HasImageDecoder(MediaCodecs.RawContainer) != true)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = WriteDng(temp, "L1000412.dng", 96, 64, orientation: 6);
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is not null && window.CanRotateFromKeyboard);

            // Shot with the camera on its side: taller than wide, and the
            // details line says so.
            var source = (BitmapSource)Viewer(window)!.Source;
            Assert.Equal((64, 96), (source.PixelWidth, source.PixelHeight));
            Assert.Contains("64×96", window.SubtitleText.Text, StringComparison.Ordinal);
            Assert.Equal("Rotate right (Ctrl+R). Not saved for this file type.", window.RotateRightButton.ToolTip);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public void SettingsNamesRawOnlyWhenThisPcHasNoDecoderForIt()
    {
        var raw = Assert.Single(MissingPieces.Describe(officeEngine: true, hevcDecoder: true, heifDecoder: true, rawDecoder: false));
        Assert.Equal("Camera RAW photos won't open.", raw.Text);
        Assert.Same(PreviewWindow.RawFix, raw.Fix);
        Assert.Equal("Get RAW support", raw.Fix.Label);
        Assert.Equal("ms-windows-store://pdp/?ProductId=9NCTDW2W1BH8", raw.Fix.Uri);

        // A decoder that is there, and a question that got no answer, list nothing.
        Assert.Empty(MissingPieces.Describe(officeEngine: true, hevcDecoder: true, heifDecoder: true, rawDecoder: true));
        Assert.Empty(MissingPieces.Describe(officeEngine: true, hevcDecoder: true, heifDecoder: true, rawDecoder: null));
    }

    private static string WriteTiff(TempDirectory temp, string name)
    {
        var pixels = new byte[40 * 30 * 3];
        Array.Fill(pixels, (byte)128);
        var source = BitmapSource.Create(40, 30, 96, 96, PixelFormats.Rgb24, null, pixels, 40 * 3);
        var encoder = new TiffBitmapEncoder { Compression = TiffCompressOption.None };
        encoder.Frames.Add(BitmapFrame.Create(source));
        var path = Path.Combine(temp.Path, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>
    /// A DNG as small as one can be: one uncompressed 16-bit Bayer strip,
    /// the tags a RAW decoder needs to turn it into color, and the
    /// orientation the camera would have noted. The picture is a ramp from
    /// black on the left to white on the right, with a red block in its
    /// first corner.
    /// </summary>
    private static string WriteDng(TempDirectory temp, string name, int width, int height, ushort orientation)
    {
        const ushort Byte = 1, Ascii = 2, Short = 3, Long = 4, Rational = 5, SignedRational = 10;

        var pixels = new byte[width * height * 2];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var gray = (ushort)(60000L * x / (width - 1));
                var inCorner = x < width / 4 && y < height / 4;
                var (r, g, b) = inCorner ? ((ushort)60000, (ushort)2000, (ushort)2000) : (gray, gray, gray);
                var value = ((y % 2) * 2 + (x % 2)) switch { 0 => r, 3 => b, _ => g };
                BinaryPrimitives.WriteUInt16LittleEndian(pixels.AsSpan((y * width + x) * 2), value);
            }
        }

        static byte[] Shorts(params ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
            }

            return bytes;
        }

        static byte[] Longs(params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (var i = 0; i < values.Length; i++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), values[i]);
            }

            return bytes;
        }

        static byte[] Text(string text) => [.. System.Text.Encoding.ASCII.GetBytes(text), 0];

        // XYZ to camera: the sRGB matrix, so the camera sees as a screen shows.
        int[] matrix = [32405, -15371, -4985, -9693, 18760, 416, 556, -2040, 10572];
        var colorMatrix = new byte[9 * 8];
        for (var i = 0; i < 9; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(colorMatrix.AsSpan(i * 8), matrix[i]);
            BinaryPrimitives.WriteInt32LittleEndian(colorMatrix.AsSpan(i * 8 + 4), 10000);
        }

        const int StripOffsets = 273;
        (ushort Tag, ushort Kind, uint Count, byte[] Data)[] tags =
        [
            (254, Long, 1, Longs(0)),
            (256, Long, 1, Longs((uint)width)),
            (257, Long, 1, Longs((uint)height)),
            (258, Short, 1, Shorts(16)),
            (259, Short, 1, Shorts(1)),
            (262, Short, 1, Shorts(32803)),
            (271, Ascii, 8, Text("Glimpse")),
            (272, Ascii, 12, Text("Test Camera")),
            (StripOffsets, Long, 1, Longs(0)),
            (274, Short, 1, Shorts(orientation)),
            (277, Short, 1, Shorts(1)),
            (278, Long, 1, Longs((uint)height)),
            (279, Long, 1, Longs((uint)pixels.Length)),
            (284, Short, 1, Shorts(1)),
            (33421, Short, 2, Shorts(2, 2)),
            (33422, Byte, 4, [0, 1, 1, 2]),
            (50706, Byte, 4, [1, 4, 0, 0]),
            (50707, Byte, 4, [1, 1, 0, 0]),
            (50708, Ascii, 20, Text("Glimpse Test Camera")),
            (50714, Short, 1, Shorts(0)),
            (50717, Long, 1, Longs(65535)),
            (50721, SignedRational, 9, colorMatrix),
            (50728, Rational, 3, Longs(1, 1, 1, 1, 1, 1)),
            (50778, Short, 1, Shorts(21)),
        ];

        // The directory, then the values too long for it, then the strip.
        const int DirectoryOffset = 8;
        var extraOffset = DirectoryOffset + 2 + tags.Length * 12 + 4;
        var extra = new MemoryStream();
        var directory = new MemoryStream();
        var stripField = -1L;
        foreach (var (tag, kind, count, data) in tags)
        {
            directory.Write(Shorts(tag, kind));
            directory.Write(Longs(count));
            if (tag == StripOffsets)
            {
                stripField = directory.Position;
            }

            if (data.Length <= 4)
            {
                var field = new byte[4];
                data.CopyTo(field, 0);
                directory.Write(field);
            }
            else
            {
                if (extra.Length % 2 == 1)
                {
                    extra.WriteByte(0);
                }

                directory.Write(Longs((uint)(extraOffset + extra.Length)));
                extra.Write(data);
            }
        }

        if (extra.Length % 2 == 1)
        {
            extra.WriteByte(0);
        }

        var entries = directory.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan((int)stripField), (uint)(extraOffset + extra.Length));

        var path = Path.Combine(temp.Path, name);
        using var file = File.Create(path);
        file.Write([0x49, 0x49, 0x2A, 0x00]);
        file.Write(Longs(DirectoryOffset));
        file.Write(Shorts((ushort)tags.Length));
        file.Write(entries);
        file.Write(Longs(0));
        file.Write(extra.ToArray());
        file.Write(pixels);
        return path;
    }

    private static (byte Red, byte Green, byte Blue) Pixel(BitmapSource bitmap, int x, int y)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return (pixel[2], pixel[1], pixel[0]);
    }

    private static PreviewWindow Park()
    {
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        return new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
    }

    private static bool Revealed(PreviewWindow window) => Fields.Get<bool>(window, "_loadFinished");

    /// <summary>The image of the viewer on screen, once it is there.</summary>
    private static Image? Viewer(PreviewWindow window) =>
        window.PreviewContent.Content is Grid { Children: [Canvas { Children: [Border { Child: Image picture }] }, ..] }
            ? picture
            : null;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }
}
