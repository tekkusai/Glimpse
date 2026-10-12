using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The zoom spec, the header read and the decoder: the parts of the image
/// lane that decide what pixels the user sees and at what scale.
/// </summary>
public sealed class ImageViewerTests
{
    // 3.0.0 kept small images at 1:1: Mark's 32 px favicons were specks in
    // a 640 x 480 window, where 2.0 filled it.
    [Fact]
    public void FitFillsTheViewWithASmallImageAsWithALargeOne()
    {
        Assert.Equal(800.0 / 48, ZoomGestures.Fit(new Size(48, 48), new Size(1200, 800)), 6);
        Assert.Equal(0.25, ZoomGestures.Fit(new Size(4800, 1000), new Size(1200, 800)));
    }

    [Fact]
    public void AGentleFitStaysSmoothAndABigOneOrAZoomShowsPixels()
    {
        // A small screenshot filling the window is filtered, as in 2.0.
        Assert.False(ZoomGestures.ShowsPixels(scale: 1.98, atFit: true));
        // A favicon blown up to fill it keeps square pixels.
        Assert.True(ZoomGestures.ShowsPixels(scale: 13, atFit: true));
        // Zoomed in by hand past 1:1: pixels, as before.
        Assert.True(ZoomGestures.ShowsPixels(scale: 1.5, atFit: false));
        Assert.False(ZoomGestures.ShowsPixels(scale: 0.5, atFit: false));
    }

    [Fact]
    public void DoubleClickGoesFromFitToOneToOneAndBack()
    {
        // A large image fitted at a quarter: double-click shows real pixels.
        Assert.Equal(1, ZoomGestures.DoubleClickTarget(scale: 0.25, fit: 0.25));
        Assert.Equal(0.25, ZoomGestures.DoubleClickTarget(scale: 1, fit: 0.25));
        // An image that fits exactly at 1:1 doubles instead.
        Assert.Equal(2, ZoomGestures.DoubleClickTarget(scale: 1, fit: 1));
        Assert.Equal(1, ZoomGestures.DoubleClickTarget(scale: 3.7, fit: 1));
        // A small image filling the view goes to its real size and back.
        Assert.Equal(1, ZoomGestures.DoubleClickTarget(scale: 13, fit: 13));
        Assert.Equal(13, ZoomGestures.DoubleClickTarget(scale: 1, fit: 13));
    }

    [Fact]
    public void ZoomStaysBetweenFitAndTheMaximum()
    {
        Assert.Equal(0.25, ZoomGestures.Clamp(0.1, fit: 0.25));
        Assert.Equal(ZoomGestures.MaxScale, ZoomGestures.Clamp(500, fit: 0.25));
        // A small image filling the view can still go down to 1:1.
        Assert.Equal(1, ZoomGestures.Clamp(0.5, fit: 13));
    }

    [Fact]
    public void PanIsClampedToTheContentEdges()
    {
        // Smaller than the view: centered, whatever the drag says.
        Assert.Equal(100, ZoomGestures.ClampOffset(-400, content: 800, view: 1000));
        // Larger than the view: an edge never moves inside it.
        Assert.Equal(0, ZoomGestures.ClampOffset(50, content: 3000, view: 1000));
        Assert.Equal(-2000, ZoomGestures.ClampOffset(-2500, content: 3000, view: 1000));
        Assert.Equal(-700, ZoomGestures.ClampOffset(-700, content: 3000, view: 1000));
    }

    [Fact]
    public void HeaderAppliesTheExifTurnToTheDisplayedSize()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "portrait.jpg");
        SaveJpeg(path, Gray(64, 48));
        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(path, 1));

        var header = ImageHeader.TryRead(path)!;

        Assert.Equal((64, 48), (header.StoredWidth, header.StoredHeight));
        Assert.Equal(6, header.Orientation);
        Assert.Equal((48, 64), (header.Width, header.Height));
        Assert.True(header.IsJpeg);
    }

    [Fact]
    public void HeaderIsReadAgainWhenTheFileChanges()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "changing.png");
        SavePng(path, Gray(10, 10));
        Assert.Equal(10, ImageHeader.TryRead(path)!.Width);

        SavePng(path, Gray(30, 20));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal((30, 20), (ImageHeader.TryRead(path)!.Width, ImageHeader.TryRead(path)!.Height));
    }

    [Fact]
    public void IconsShowTheirLargestFrame()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "app.ico");
        WriteIcon(path, 16, 48, 32);

        Assert.Equal((48, 48), (ImageHeader.TryRead(path)!.Width, ImageHeader.TryRead(path)!.Height));
        var decoded = PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path));
        Assert.Equal((48, 48), (decoded!.PixelWidth, decoded.PixelHeight));
    }

    [Fact]
    public void JpegDecodesStraightToTheSizeAsked()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "wide.jpg");
        SaveJpeg(path, Gray(6000, 400));
        var header = ImageHeader.TryRead(path);

        Assert.Equal(4096, PreviewWindow.TryDecodeImage(path, header)!.PixelWidth);
        Assert.Equal(1200, PreviewWindow.TryDecodeImage(path, header, longSide: 1200)!.PixelWidth);
    }

    [Fact]
    public void TransparencyIsReadFromThePixelFormat()
    {
        Assert.True(ImageViewer.HasAlpha(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8)));
        Assert.False(ImageViewer.HasAlpha(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgr32, null, new byte[16], 8)));
        var palette = new BitmapPalette([Colors.Black, Color.FromArgb(0, 255, 255, 255)]);
        Assert.True(ImageViewer.HasAlpha(BitmapSource.Create(2, 2, 96, 96, PixelFormats.Indexed8, palette, new byte[4], 2)));
    }

    [Fact]
    public void GifTransparencyComesFromItsGraphicControlBlock()
    {
        using var temp = new TempDirectory();
        var opaque = Path.Combine(temp.Path, "opaque.gif");
        var clear = Path.Combine(temp.Path, "clear.gif");
        File.WriteAllBytes(opaque, [.. "GIF89a"u8, 0x21, 0xF9, 0x04, 0x00, 0, 0, 0, 0, 0x3B]);
        File.WriteAllBytes(clear, [.. "GIF89a"u8, 0x21, 0xF9, 0x04, 0x01, 0, 0, 0, 0, 0x3B]);

        Assert.False(PreviewWindow.GifHasTransparency(opaque));
        Assert.True(PreviewWindow.GifHasTransparency(clear));
    }

    [Fact]
    public void TheWebImagePageUsesTheSameGestureNumbers()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "logo.svg");
        File.WriteAllText(path, """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"><rect width="120" height="40"/></svg>""");

        var html = File.ReadAllText(ImagePage.Build(path).LocalPath);

        Assert.Contains("const STEP = 1.15, MAX = 32,", html, StringComparison.Ordinal);
        Assert.Equal(ZoomGestures.WheelStep, 1.15);
        Assert.Equal(ZoomGestures.MaxScale, 32);
        Assert.Contains("HINT_W = 120, HINT_H = 40", html, StringComparison.Ordinal);
        Assert.Contains("class=\"checker\"", html, StringComparison.Ordinal);
    }

    private static BitmapSource Gray(int width, int height) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);

    private static void SaveJpeg(string path, BitmapSource bitmap) => Save(path, new JpegBitmapEncoder(), bitmap);

    private static void SavePng(string path, BitmapSource bitmap) => Save(path, new PngBitmapEncoder(), bitmap);

    private static void Save(string path, BitmapEncoder encoder, BitmapSource bitmap)
    {
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>An .ico whose frames are PNG images, as Windows Vista and later write them.</summary>
    private static void WriteIcon(string path, params int[] sizes)
    {
        var images = sizes.Select(size =>
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(
                size, size, 96, 96, PixelFormats.Bgra32, null, new byte[size * size * 4], size * 4)));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }).ToArray();

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)sizes[i]);
            writer.Write((byte)sizes[i]);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images)
        {
            writer.Write(image);
        }
    }
}
