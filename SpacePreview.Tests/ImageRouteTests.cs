using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Utilities;
using static SpacePreview.Tests.ImageTestFiles;

namespace SpacePreview.Tests;

/// <summary>
/// What the window itself builds for an image file: which viewer it goes
/// to, which pixels it shows and whether they were decoded again. Each
/// test asks the window's own router, on a window that is never shown and
/// never gets a handle, so no browser starts.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRouteTests
{
    // WPF draws the first frame of an animation and stops. A GIF went to
    // the browser page by its name; an animated PNG or WebP says so only in
    // its bytes, and has to be sent the same way.
    [Fact]
    public Task EverythingThatMovesIsSentTheWayAGifGoes() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        string Write(string name, byte[] bytes)
        {
            var path = Path.Combine(temp.Path, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        var window = new PreviewWindow();
        try
        {
            Assert.Equal("page", await RouteAsync(window, Write("loop.gif", "GIF89a"u8.ToArray())));
            Assert.Equal("page", await RouteAsync(window, Write("moving.png", ImageRotationCarryTests.AnimatedPng())));
            Assert.Equal("page", await RouteAsync(window, Write("sticker.webp", ExtendedWebp(0x02, width: 400, height: 300))));

            // A file with no extension is known by its signature, as a GIF is.
            Assert.Equal("page", await RouteAsync(window, Write("sticker", ExtendedWebp(0x02, width: 400, height: 300))));

            // A still picture stays with the native viewer.
            Assert.Equal("viewer", await RouteAsync(window, Write("still.png", Png(5, 3, 8, 2, new byte[45]))));

            // More chunks in front of the animation than the window reads
            // on its way up: the rest of the answer is found before the
            // picture is built, and it still plays.
            Assert.Equal("page", await RouteAsync(window, Write("annotated.png", ImageRotationCarryTests.AnimatedPng(textChunksInFront: 100))));
        }
        finally
        {
            window.Close();
        }

        // A name that says text is believed: the file is never opened as an image to check.
        var notes = Write("notes.txt", ImageRotationCarryTests.AnimatedPng());
        Assert.False(await PreviewWindow.IsAnimatedImageAsync(notes, FileSignatureKind.Png, useSignatureRouting: false, default));
    });

    // A second preview of a file whose rotation is still on its way to disk
    // waits for it. Opened at once, it showed the picture as it was a
    // moment before, over a file that had turned.
    [Fact]
    public Task APreviewOfAFileStillBeingSavedWaitsAndOpensOnTheSavedFile() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "scan.png", 1200, 800);
        var header = ImageHeader.TryRead(path);
        var session = new ImageRotationSession(
            path,
            PreviewWindow.TryDecodeImage(path, header)!,
            ImageOrientation.Normal,
            ImageRotationService.Classify(path));
        var window = new PreviewWindow();
        try
        {
            // Kept in flight: another program has the file, and the save asks again.
            var holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            session.Rotate(1);
            session.Commit();
            Assert.False(ImageRotationSession.WhenSaved(path).IsCompleted);

            var opening = BuildAsync(window, path);
            await Task.Delay(300);
            Assert.False(opening.IsCompleted, "the preview waited for the save");

            holder.Dispose();
            Assert.Equal((800, 1200), Size(Picture(await opening.WaitAsync(TimeSpan.FromSeconds(10)))));
        }
        finally
        {
            window.Close();
        }
    });

    // The window keeps the last decode and hands it back for the same file.
    // The file's size and time are what make it the same: a picture written
    // again under its name is decoded again, even while the old pixels are
    // still alive in the picture before it.
    [Fact]
    public Task APictureShownAgainIsNotDecodedAgainAndAChangedOneIs() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "scan.png", 600, 400);
        var window = new PreviewWindow();
        try
        {
            var first = Picture(await BuildAsync(window, path));
            Assert.Same(first, Picture(await BuildAsync(window, path)));

            // Another program writes the file again: the same size in
            // pixels, other pixels.
            WriteImage(temp, "scan.png", 600, 400, shade: 90);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            var changed = Picture(await BuildAsync(window, path));

            Assert.NotSame(first, changed);
            Assert.NotEqual(Pixels(first), Pixels(changed));
            Assert.Equal(Pixels(PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path))!), Pixels(changed));
            GC.KeepAlive(first);
        }
        finally
        {
            window.Close();
        }
    });

    // A turn is on its way to the file from the moment it is made, so the
    // decode kept from before it answers for nothing.
    [Fact]
    public Task ATurnedPictureIsDecodedAgainTheNextTimeItOpens() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "keyed.png");

        // A color key: this PNG turns in the view only, so its file never changes.
        File.WriteAllBytes(path, Png(5, 3, 8, 2, new byte[5 * 3 * 3], [new PngChunk("tRNS", new byte[6])]));
        var window = new PreviewWindow();
        try
        {
            var first = Picture(await BuildAsync(window, path));
            window.RotateFromKeyboard(1);
            var bytes = File.ReadAllBytes(path);

            var again = Picture(await BuildAsync(window, path));

            Assert.NotSame(first, again);
            Assert.Equal((5, 3), Size(again));
            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            window.Close();
        }
    });

    // Only artwork Explorer already holds may stand in for a giant image
    // while it decodes. Nobody has thumbnailed this file, so nothing is
    // extracted for it: a PNG cannot decode small, and decodes itself.
    [Fact]
    public Task AGiantPictureNobodyHasThumbnailedOpensOnItsOwnPixels() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var scan = WriteImage(temp, "scan.png", 5000, 800);
        var window = new PreviewWindow();
        try
        {
            // The decode cap, not a thumbnail's size.
            var shown = Picture(await BuildAsync(window, scan));
            Assert.Equal(4096, shown.PixelWidth);
            Assert.InRange(shown.PixelHeight, 654, 656);
        }
        finally
        {
            window.Close();
        }
    });

    // A giant image shows first on a frame smaller than itself. The stage
    // takes the shape its header names and never the frame's, or a wide
    // panorama would open in a window shaped for its stand-in.
    [Fact]
    public Task TheStageTakesAnImagesShapeFromItsHeaderAndNeverFromADecodedFrame() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "panorama.jpg", 6000, 1000);
        using var load = new CancellationTokenSource();
        var window = new PreviewWindow();
        try
        {
            var shown = Picture(await BuildAsync(window, path, load.Token));
            var shape = Fields.Get<StageFitHint?>(window, "_stageFitHint")!.Value;

            Assert.True(shown.PixelWidth < 6000, "the frame is smaller than the image");
            Assert.Equal((StageFitHint.FitKind.ImagePixels, 6000.0, 1000.0), (shape.Kind, shape.Width, shape.Height));
        }
        finally
        {
            // The full decode a giant image owes is called off with the load.
            load.Cancel();
            window.Close();
        }
    });

    private static async Task<string> RouteAsync(PreviewWindow window, string path)
    {
        var content = await BuildAsync(window, path);
        try
        {
            return content switch
            {
                Grid { Children: [StagedWebView] } => "page",
                Grid { Children: [Canvas { Children: [Border { Child: Image }] }, ..] } => "viewer",
                _ => content.GetType().Name
            };
        }
        finally
        {
            ((content as Grid)?.Children.OfType<StagedWebView>().SingleOrDefault())?.Dispose();
        }
    }

    /// <summary>The content the window's own router builds for a file, as a load of this window would.</summary>
    internal static Task<FrameworkElement> BuildAsync(PreviewWindow window, string path, CancellationToken token = default) =>
        (Task<FrameworkElement>)Fields.Call(
            window,
            "BuildPreviewContentAsync",
            path,
            FileSignatures.Detect(path),
            FileKinds.CanUseSignatureRouting(path),
            0L,
            token)!;

    /// <summary>The bitmap in the native viewer.</summary>
    internal static BitmapSource Picture(FrameworkElement content) =>
        content is Grid { Children: [Canvas { Children: [Border { Child: Image picture }] }, ..] }
            ? (BitmapSource)picture.Source
            : throw new Xunit.Sdk.XunitException($"Not the image viewer: {content.GetType().Name}");

    private static (int Width, int Height) Size(BitmapSource bitmap) => (bitmap.PixelWidth, bitmap.PixelHeight);

    private static byte[] Pixels(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
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

    /// <summary>A picture with a different color in every corner, so a turn shows.</summary>
    private static string WriteImage(TempDirectory temp, string name, int width, int height, int shade = 0)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 3;
                pixels[at] = (byte)(x * 255 / width);
                pixels[at + 1] = (byte)(y * 255 / height);
                pixels[at + 2] = (byte)((x + y + shade) % 251);
            }
        }

        BitmapEncoder encoder = Path.GetExtension(name) == ".jpg" ? new JpegBitmapEncoder() : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3)));
        var path = Path.Combine(temp.Path, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
