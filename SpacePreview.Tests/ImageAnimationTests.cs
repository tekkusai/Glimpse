using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using static SpacePreview.Tests.ImageTestFiles;

namespace SpacePreview.Tests;

/// <summary>
/// An animated PNG or WebP says so in its first bytes. WPF would show its
/// first frame and stop, so the header finds it and it plays on the browser
/// image page, as a GIF does.
/// </summary>
public sealed class ImageAnimationTests
{
    [Fact]
    public void AnAnimatedPngIsToldFromItsHeader()
    {
        using var temp = new TempDirectory();
        var moving = Path.Combine(temp.Path, "moving.png");
        File.WriteAllBytes(moving, ImageRotationCarryTests.AnimatedPng());

        var header = ImageHeader.TryRead(moving)!;

        Assert.True(header.IsAnimated);
        Assert.True(header.HasAlpha);
        Assert.Equal((5, 3), (header.Width, header.Height));
    }

    [Fact]
    public void AStillPngIsNotAnAnimation()
    {
        using var temp = new TempDirectory();
        var still = Path.Combine(temp.Path, "still.png");
        var late = Path.Combine(temp.Path, "late.png");
        File.WriteAllBytes(still, Png(5, 3, 8, 2, new byte[45], [new PngChunk("tEXt", [.. "Comment"u8, 0, .. "acTL"u8])]));

        // The chunk only counts in front of the pixels; behind them it is
        // not an animation and a browser would not play it.
        File.WriteAllBytes(late, Png(5, 3, 8, 2, new byte[45], afterData: [new PngChunk("acTL", [.. BigEndian(2), .. BigEndian(0)])]));

        Assert.False(ImageHeader.TryRead(still)!.IsAnimated);
        Assert.False(ImageHeader.TryRead(still)!.HasAlpha);
        Assert.False(ImageHeader.TryRead(late)!.IsAnimated);
    }

    [Theory]
    // Gray with alpha and RGBA carry it in every pixel; an indexed or
    // keyed image declares it in a tRNS chunk.
    [InlineData(4, 2, false, true)]
    [InlineData(6, 4, false, true)]
    [InlineData(3, 1, true, true)]
    [InlineData(3, 1, false, false)]
    [InlineData(2, 3, false, false)]
    public void APngSaysWhetherItIsTransparent(byte colorType, int bytesPerPixel, bool keyed, bool transparent)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "art.png");
        var chunks = new List<PngChunk>();
        if (colorType == 3)
        {
            chunks.Add(new PngChunk("PLTE", [0, 0, 0, 255, 255, 255]));
        }

        if (keyed)
        {
            chunks.Add(new PngChunk("tRNS", [0]));
        }

        File.WriteAllBytes(path, Png(5, 3, 8, colorType, new byte[15 * bytesPerPixel], chunks));

        Assert.Equal(transparent, ImageHeader.TryRead(path)!.HasAlpha);
    }

    [Theory]
    [InlineData(0x02, true, false)]
    [InlineData(0x12, true, true)]
    [InlineData(0x10, false, true)]
    [InlineData(0x00, false, false)]
    public void AnExtendedWebpSaysWhetherItMovesAndHowBigItIs(byte flags, bool animated, bool transparent)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sticker.webp");
        File.WriteAllBytes(path, ExtendedWebp(flags, width: 400, height: 300));

        // Read from the file's own bytes: no codec has to be installed.
        var header = ImageHeader.TryRead(path)!;

        Assert.Equal(animated, header.IsAnimated);
        Assert.Equal(transparent, header.HasAlpha);
        Assert.Equal((400, 300), (header.Width, header.Height));
    }

    [Fact]
    public void AnimationIsReadFromTheBytesWhateverTheFileIsCalled()
    {
        using var temp = new TempDirectory();
        var misnamed = Path.Combine(temp.Path, "saved from chat.jpg");
        File.WriteAllBytes(misnamed, ImageRotationCarryTests.AnimatedPng());

        Assert.True(ImageHeader.TryRead(misnamed)!.IsAnimated);
    }

    [Fact]
    public void OtherFormatsStillReadThroughTheDecoder()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "photo.jpg");
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(64, 48, 96, 96, PixelFormats.Gray8, null, new byte[64 * 48], 64)));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        var header = ImageHeader.TryRead(path)!;

        Assert.Equal((64, 48, true, false, false), (header.Width, header.Height, header.IsJpeg, header.IsAnimated, header.HasAlpha));
    }

    [Fact]
    public void TheBrowserPageGivesATransparentAnimationItsCheckerboard()
    {
        using var temp = new TempDirectory();
        var clear = Path.Combine(temp.Path, "clear.png");
        var solid = Path.Combine(temp.Path, "solid.webp");
        File.WriteAllBytes(clear, ImageRotationCarryTests.AnimatedPng());
        File.WriteAllBytes(solid, ExtendedWebp(0x02, width: 400, height: 300));

        Assert.Contains("class=\"checker\"", File.ReadAllText(ImagePage.Build(clear).LocalPath), StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"checker\"", File.ReadAllText(ImagePage.Build(solid).LocalPath), StringComparison.Ordinal);
    }

    /// <summary>The first thirty bytes of an extended WebP: the RIFF header and its VP8X chunk.</summary>
    private static byte[] ExtendedWebp(byte flags, int width, int height) =>
    [
        .. "RIFF"u8, 22, 0, 0, 0, .. "WEBP"u8,
        .. "VP8X"u8, 10, 0, 0, 0,
        flags, 0, 0, 0,
        (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16),
        (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)
    ];
}
