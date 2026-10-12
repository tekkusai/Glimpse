using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class ImageRotationTests
{
    [Theory]
    [InlineData(1, 6)]
    [InlineData(6, 3)]
    [InlineData(3, 8)]
    [InlineData(8, 1)]
    public void ClockwiseWalksTheUprightCycle(int from, int expected)
    {
        Assert.Equal(expected, ImageOrientation.RotateCw(from, 1));
    }

    [Theory]
    [InlineData(2, 7)]
    [InlineData(7, 4)]
    [InlineData(4, 5)]
    [InlineData(5, 2)]
    public void ClockwiseWalksTheMirroredCycle(int from, int expected)
    {
        Assert.Equal(expected, ImageOrientation.RotateCw(from, 1));
    }

    [Fact]
    public void FourTurnsAreTheIdentityForEveryOrientation()
    {
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            Assert.Equal(orientation, ImageOrientation.RotateCw(orientation, 4));
        }
    }

    [Fact]
    public void CounterClockwiseUndoesClockwise()
    {
        for (var orientation = 1; orientation <= 8; orientation++)
        {
            Assert.Equal(orientation, ImageOrientation.RotateCw(ImageOrientation.RotateCw(orientation, 1), -1));
        }
    }

    [Fact]
    public void InvalidOrientationsNormalizeInsteadOfThrowing()
    {
        Assert.Equal(6, ImageOrientation.RotateCw(0, 1));
        Assert.Equal(6, ImageOrientation.RotateCw(99, 1));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(6, true)]
    [InlineData(7, true)]
    [InlineData(8, true)]
    public void ApplySwapsAxesExactlyForTheRotatedStates(int orientation, bool swapped)
    {
        var source = MakeBitmap(width: 4, height: 2);

        var oriented = ImageOrientation.Apply(source, orientation);

        Assert.Equal(swapped ? 2 : 4, oriented.PixelWidth);
        Assert.Equal(swapped ? 4 : 2, oriented.PixelHeight);
    }

    [Fact]
    public void OrientationReadsAsNormalWhenAbsent()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "plain.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(8, 8), path);

        Assert.Equal(1, ImageOrientation.Read(path));
    }

    [Fact]
    public void JpegRotationPersistsThroughSpliceThenInPlacePatch()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "photo.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(16, 8), path);
        var originalBytes = File.ReadAllBytes(path).Length;

        // The WPF JPEG encoder writes no EXIF, so the first save exercises
        // the byte-splice; the second finds the spliced tag and patches it.
        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 1));
        Assert.Equal(6, ImageOrientation.Read(path));

        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 1));
        Assert.Equal(3, ImageOrientation.Read(path));

        // Metadata-only writes must not touch pixel data size in any
        // meaningful way, and the file must still decode.
        var decoded = DecodePixels(path, out var width, out var height);
        Assert.Equal(16, width);
        Assert.Equal(8, height);
        Assert.NotEmpty(decoded);
        Assert.InRange(File.ReadAllBytes(path).Length, originalBytes, originalBytes + 64);
    }

    [Fact]
    public void PngRotationRewritesPixelsLosslessly()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "two.png");

        // Two known pixels side by side: blue then red.
        var source = BitmapSource.Create(
            2, 1, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, 8);
        SaveBitmap(new PngBitmapEncoder(), source, path);

        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 1));

        // A quarter turn clockwise stands the pair up: blue above red.
        var pixels = DecodePixels(path, out var width, out var height);
        Assert.Equal(1, width);
        Assert.Equal(2, height);
        Assert.Equal(255, pixels[0]);
        Assert.Equal(0, pixels[2]);
        Assert.Equal(0, pixels[4]);
        Assert.Equal(255, pixels[6]);
    }

    [Fact]
    public void FillBytesBeforeMarkersSurviveTheSplice()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "fill.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(16, 8), path);

        // JPEG permits runs of 0xFF fill bytes in front of any marker; a
        // camera or editor that pads this way must not defeat the splice.
        var bytes = File.ReadAllBytes(path).ToList();
        bytes.Insert(2, 0xFF);
        File.WriteAllBytes(path, [.. bytes]);

        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 1));
        Assert.Equal(6, ImageOrientation.Read(path));

        var decoded = DecodePixels(path, out var width, out var height);
        Assert.Equal(16, width);
        Assert.Equal(8, height);
        Assert.NotEmpty(decoded);
    }

    [Fact]
    public void MalformedExifSegmentIsNeverDuplicated()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken-exif.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(8, 8), path);

        // An APP1 that says Exif but carries garbage occupies the one
        // allowed Exif slot even though metadata queries cannot read it.
        // The save must refuse rather than splice a second segment.
        var original = File.ReadAllBytes(path);
        var garbageExif = new byte[]
        {
            0xFF, 0xE1, 0x00, 0x10,
            (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD
        };
        var patched = new byte[original.Length + garbageExif.Length];
        original.AsSpan(0, 2).CopyTo(patched);
        garbageExif.CopyTo(patched, 2);
        original.AsSpan(2).CopyTo(patched.AsSpan(2 + garbageExif.Length));
        File.WriteAllBytes(path, patched);

        Assert.Equal(RotationSaveResult.CannotCarry, ImageRotationService.SaveQuarterTurns(path, 1));
        Assert.Equal(patched, File.ReadAllBytes(path));
    }

    [Fact]
    public void ReadOnlyFilesStayViewOnly()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "locked.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(8, 8), path);
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            Assert.Equal(RotationPersistence.ViewOnly, ImageRotationService.Classify(path));
            Assert.Equal(RotationSaveResult.ReadOnly, ImageRotationService.SaveQuarterTurns(path, 1));
            Assert.Equal(1, ImageOrientation.Read(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void LossyFormatsWithoutExifStayViewOnly()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sticker.webp");
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46]);

        Assert.Equal(RotationPersistence.ViewOnly, ImageRotationService.Classify(path));
    }

    [Fact]
    public void NetZeroTurnsWriteNothing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "still.jpg");
        SaveBitmap(new JpegBitmapEncoder(), MakeBitmap(8, 8), path);
        var stamp = File.GetLastWriteTimeUtc(path);

        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 4));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(path));
    }

    private static BitmapSource MakeBitmap(int width, int height)
    {
        var stride = width * 4;
        return BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null,
            new byte[stride * height], stride);
    }

    private static void SaveBitmap(BitmapEncoder encoder, BitmapSource source, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static byte[] DecodePixels(string path, out int width, out int height)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.OnLoad).Frames[0];
        width = frame.PixelWidth;
        height = frame.PixelHeight;
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }
}
