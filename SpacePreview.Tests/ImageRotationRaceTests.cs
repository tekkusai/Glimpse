using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A rotation outlasts what happens around it: another program holding the
/// file for a moment, a full decode still on its way, the window closing
/// and the app ending with a write in flight.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRotationRaceTests
{
    // Explorer's thumbnailer, the search indexer and antivirus all open a
    // file that was just written, as readers that keep writers out, and let
    // go a moment later. A save that tried once called that "in use".
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public async Task AFileHeldForAMomentIsWaitedFor(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "held" + extension, 16, 8);
        var holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var released = Task.Delay(300).ContinueWith(_ => holder.Dispose());

        var result = ImageRotationService.SaveQuarterTurns(path, 1);

        await released;
        Assert.Equal(RotationSaveResult.Saved, result);
        Assert.Equal((8, 16), (ImageHeader.TryRead(path)!.Width, ImageHeader.TryRead(path)!.Height));
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
    }

    [Fact]
    public Task ARotationThatWaitedForTheFileRaisesNoNotice() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 16, 8);
        var session = Open(path);
        var notSaved = new List<RotationSaveResult>();
        session.NotSaved += notSaved.Add;

        var holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        session.Rotate(1);
        session.Commit();
        await Task.Delay(300);
        holder.Dispose();
        await ImageRotationSession.WhenSaved(path);

        // Long enough for a failure to have come back to this thread.
        await Task.Delay(200);
        Assert.Empty(notSaved);
        Assert.Equal(6, ImageOrientation.Read(path));
        Assert.Equal((8, 16), Size(session));
    });

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public void AFileStillHeldAfterTheWaitIsInUse(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "held" + extension, 16, 8);
        var before = File.ReadAllBytes(path);
        var clock = Stopwatch.StartNew();

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(RotationSaveResult.InUse, ImageRotationService.SaveQuarterTurns(path, 1));
        }

        // It kept asking for about a second and a half, and no longer.
        Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(4));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
    }

    // Only a file another program has open is worth asking for again. A
    // locked range, a read-only file and a missing one answer at once.
    [Fact]
    public void NoOtherFailureIsWaitedOn()
    {
        using var temp = new TempDirectory();
        var locked = WriteImage(temp, "locked.bmp", 16, 8);
        var readOnly = WriteImage(temp, "readonly.png", 16, 8);
        var gone = Path.Combine(temp.Path, "gone.jpg");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        try
        {
            var clock = Stopwatch.StartNew();
            using (var holder = File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                holder.Lock(0, holder.Length);
                Assert.Equal(RotationSaveResult.InUse, ImageRotationService.SaveQuarterTurns(locked, 1));
            }

            Assert.Equal(RotationSaveResult.ReadOnly, ImageRotationService.SaveQuarterTurns(readOnly, 1));
            Assert.Equal(RotationSaveResult.Failed, ImageRotationService.SaveQuarterTurns(gone, 1));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
        }
    }

    // A giant image opens on a small first frame and decodes in full a
    // beat later. 3.0.0 handed a turn to the disk one second after it was
    // made, whether or not that decode had landed, and from then on refused
    // the decode: the picture stayed a first frame until it was reopened.
    [Fact]
    public Task ATurnMadeOnAFirstFrameStillReachesFullQuality() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "panorama.jpg", 6000, 2000);
        var window = OpenStage();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => window.CanRotateFromKeyboard && Viewer(window) is not null);
            Assert.True(Shown(window).Width < 4096, "the image opened on a first frame");

            // The write comes due while the full decode is still to land:
            // this thread is busy past the write's delay, so both are owed
            // when it frees.
            window.RotateFromKeyboard(1);
            Thread.Sleep(1300);

            await WaitUntilAsync(() => ImageOrientation.Read(path) == 6 && Shown(window).Height == 4096);
            Assert.Equal(6, ImageOrientation.Read(path));
            Assert.Equal((1365, 4096), Shown(window));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    // The rule the window test above rests on, without its timing: while
    // the full decode is out, the turn stays off the disk, and it follows
    // the decode however that ends.
    [Fact]
    public Task ATurnIsWrittenOnceTheFullDecodeHasLanded() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "giant.png", 1200, 800);
        var header = ImageHeader.TryRead(path);
        var full = PreviewWindow.TryDecodeImage(path, header)!;
        var session = new ImageRotationSession(
            path,
            PreviewWindow.TryDecodeImage(path, header, longSide: 300)!,
            ImageOrientation.Normal,
            ImageRotationService.Classify(path));
        var decode = new TaskCompletionSource();
        session.FullDecode = decode.Task;

        session.Rotate(1);
        Assert.Equal((200, 300), Size(session));

        // Well past the write's delay, and nothing has gone to the disk.
        await Task.Delay(1500);
        Assert.True(ImageRotationSession.WhenSaved(path).IsCompleted);
        Assert.Equal(1200, ImageHeader.TryRead(path)!.Width);

        Assert.True(session.TryUpgradeDecoded(full, ImageOrientation.Normal));
        decode.SetResult();
        await WaitUntilAsync(() => ImageHeader.TryRead(path)?.Width == 800);

        Assert.Equal(800, ImageHeader.TryRead(path)!.Width);
        Assert.Equal((800, 1200), Size(session));
    });

    [Fact]
    public Task ADecodeThatIsGivenUpDoesNotKeepTheTurnFromTheDisk() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "giant.jpg", 16, 8);
        var session = Open(path);
        session.FullDecode = Task.FromCanceled(new CancellationToken(canceled: true));

        session.Rotate(1);
        await WaitUntilAsync(() => ImageOrientation.Read(path) == 6);

        Assert.Equal(6, ImageOrientation.Read(path));
    });

    // The app's end closes its windows without hiding them. 3.0.0 left a
    // turn made in the last second on a timer the app never came back to.
    [Fact]
    public Task AWindowThatClosesHandsItsTurnToTheDisk() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 320, 200);
        var window = OpenStage();
        window.ShowPreview(path, IntPtr.Zero);
        await WaitUntilAsync(() => window.CanRotateFromKeyboard && Viewer(window) is not null);

        window.RotateFromKeyboard(1);
        window.Close();

        var handedOver = ImageRotationSession.WhenSaved(path);
        Assert.True(!handedOver.IsCompleted || ImageOrientation.Read(path) == 6, "the close handed the turn to the disk");
        await handedOver;
        Assert.Equal(6, ImageOrientation.Read(path));
    });

    // A PNG or BMP save builds the new file beside the old one. Ended in
    // that moment, the app left "<name>.spvw-rotate.tmp" in the user's folder.
    [Fact]
    public Task TheAppsEndWaitsForARotationStillBeingWritten() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "scan.png", 1200, 800);
        var session = Open(path);

        // Kept in flight for a moment: the file is held, and the save asks again.
        var holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        session.Rotate(1);
        session.Commit();
        var saving = ImageRotationSession.WhenSaved(path);
        Assert.False(saving.IsCompleted);
        var released = Task.Delay(400).ContinueWith(_ => holder.Dispose());

        Exit();

        Assert.True(saving.IsCompleted, "the exit waited for the save");
        await released;
        Assert.Equal(800, ImageHeader.TryRead(path)!.Width);
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
    });

    [Fact]
    public Task TheWaitAtTheAppsEndIsBounded() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 16, 8);
        var session = Open(path);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Rotate(1);
            session.Commit();
            Assert.False(ImageRotationSession.WaitForSaves(TimeSpan.FromMilliseconds(100)));
        }

        Assert.True(ImageRotationSession.WaitForSaves(TimeSpan.FromSeconds(10)));
        await ImageRotationSession.WhenSaved(path);
    });

    [Theory]
    [InlineData(".png", true)]
    [InlineData(".bmp", true)]
    [InlineData(".jpg", true)]
    // A TIFF is patched in place and never has a working file, so a file
    // of that name beside one is not Glimpse's to remove.
    [InlineData(".tif", false)]
    public Task OpeningAPictureRemovesTheWorkingFileAnInterruptedSaveLeft(string extension, bool removed) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "scan" + extension, 320, 200);
        var before = File.ReadAllBytes(path);
        var stranded = ImageRotationService.TempPathFor(path);
        File.WriteAllBytes(stranded, [1, 2, 3]);
        var window = OpenStage();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Viewer(window) is not null);

            // The removal is queued for the file as a save would be.
            await ImageRotationSession.WhenSaved(path);
            Assert.NotNull(Viewer(window));
            Assert.Equal(!removed, File.Exists(stranded));
            Assert.Equal(before, File.ReadAllBytes(path));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task TheWorkingFileOfASaveInFlightIsLeftAlone() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 16, 8);
        var working = ImageRotationService.TempPathFor(path);
        var session = Open(path);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Rotate(1);
            session.Commit();
            File.WriteAllBytes(working, [1, 2, 3]);

            // Another preview opens the same picture while it is being saved.
            ImageRotationSession.RemoveStrandedTemp(path);
            Assert.True(File.Exists(working));
        }

        await ImageRotationSession.WhenSaved(path);
        ImageRotationSession.RemoveStrandedTemp(path);
        await ImageRotationSession.WhenSaved(path);
        Assert.False(File.Exists(working));
        Assert.Equal(6, ImageOrientation.Read(path));
    });

    // The window reads an image's header once, to shape the stage, and
    // hands it to the decode. A header read while a turn was still being
    // written describes the file as it was: handed on, the picture opened
    // unturned over a file that had turned.
    [Fact]
    public Task APictureShownAgainWhileItsTurnIsWrittenOpensAsTheFileIs() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 320, 200);
        var window = OpenStage();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => window.CanRotateFromKeyboard && Viewer(window) is not null);
            window.RotateFromKeyboard(1);
            var first = Viewer(window);

            // Shown again with the turn pending: the write starts, held up
            // for a moment, as the stage reads the header. It lands while
            // this thread is busy, before the decode asks.
            var holder = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            window.ShowPreview(path, IntPtr.Zero);
            holder.Dispose();
            Thread.Sleep(400);
            Assert.Equal(6, ImageOrientation.Read(path));

            await WaitUntilAsync(() => Viewer(window) is { } second && !ReferenceEquals(second, first));
            Assert.NotSame(first, Viewer(window));
            Assert.Equal((200, 320), Shown(window));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    /// <summary>
    /// Runs the app's exit on an App that never started, as AppEndTests
    /// does: this process already has its Application.
    /// </summary>
    private static void Exit()
    {
        var app = (App)RuntimeHelpers.GetUninitializedObject(typeof(App));
        var args = (ExitEventArgs)Activator.CreateInstance(
            typeof(ExitEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [0],
            null)!;
        Fields.Call(app, "OnExit", args);
    }

    private static ImageRotationSession Open(string path)
    {
        var header = ImageHeader.TryRead(path);
        return new ImageRotationSession(
            path,
            PreviewWindow.TryDecodeImage(path, header)!,
            header?.Orientation ?? ImageOrientation.Normal,
            ImageRotationService.Classify(path));
    }

    private static (int Width, int Height) Size(ImageRotationSession session) =>
        (session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight);

    /// <summary>The image of the viewer on screen, once it is there.</summary>
    private static Image? Viewer(PreviewWindow window) =>
        window.PreviewContent.Content is Grid { Children: [Canvas { Children: [Border { Child: Image picture }] }, ..] }
            ? picture
            : null;

    private static (int Width, int Height) Shown(PreviewWindow window)
    {
        var source = (BitmapSource)Viewer(window)!.Source;
        return (source.PixelWidth, source.PixelHeight);
    }

    /// <summary>A picture with a different color in every corner, so a turn shows.</summary>
    private static string WriteImage(TempDirectory temp, string name, int width, int height)
    {
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var at = (y * width + x) * 3;
                pixels[at] = (byte)(x * 255 / width);
                pixels[at + 1] = (byte)(y * 255 / height);
                pixels[at + 2] = (byte)((x + y) % 251);
            }
        }

        BitmapEncoder encoder = Path.GetExtension(name) switch
        {
            ".jpg" => new JpegBitmapEncoder(),
            ".bmp" => new BmpBitmapEncoder(),
            ".tif" => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder()
        };
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr24, null, pixels, width * 3)));
        var path = Path.Combine(temp.Path, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static PreviewWindow OpenStage()
    {
        // Parked off screen and frozen, like the probe.
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        return window;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
