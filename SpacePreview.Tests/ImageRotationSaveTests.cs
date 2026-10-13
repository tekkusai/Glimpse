using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A rotation is either on disk or visibly not. Glimpse's own decode never
/// keeps its own save out of the file, and a save that still fails (another
/// program holds the file, it went read-only, it is gone) turns the view
/// back and says why: over the image while its preview is up, and to the
/// tray once the preview has closed or moved on.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRotationSaveTests
{
    private static bool Saves(string path, int turns) =>
        ImageRotationService.SaveQuarterTurns(path, turns) == RotationSaveResult.Saved;

    // 3.0.0 gave WPF the path, and WPF opened it against writers for the
    // whole decode. A giant image decodes in full 420 ms after it opens, so
    // rotating and arrowing away in that time lost the rotation.
    [Fact]
    public async Task ADecodeLeavesTheFileOpenToAWriter()
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "large.png", 3000, 2000);
        var header = ImageHeader.TryRead(path);
        var stop = false;
        var decoding = Task.Run(() =>
        {
            var decoded = 0;
            while (!Volatile.Read(ref stop))
            {
                Assert.NotNull(PreviewWindow.TryDecodeImage(path, header));
                decoded++;
            }

            return decoded;
        });

        var refused = 0;
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 400)
        {
            try
            {
                // The open a rotation's save makes.
                using var writer = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            }
            catch (IOException)
            {
                refused++;
            }
        }

        Volatile.Write(ref stop, true);
        Assert.True(await decoding > 0);
        Assert.Equal(0, refused);
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public async Task ARotationSavedWhileItsOwnDecodeRunsReachesTheDisk(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo" + extension, 1600, 1000);
        var stop = false;
        var decoding = Task.Run(() =>
        {
            while (!Volatile.Read(ref stop))
            {
                PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path));
            }
        });

        try
        {
            // The first JPEG save splices a segment in and swaps the file;
            // the rest patch it in place. PNG and BMP swap every time.
            for (var turn = 1; turn <= 4; turn++)
            {
                Assert.True(Saves(path, 1), $"turn {turn}");
                var header = ImageHeader.TryRead(path)!;
                Assert.Equal(turn % 2 == 1 ? (1000, 1600) : (1600, 1000), (header.Width, header.Height));
            }
        }
        finally
        {
            Volatile.Write(ref stop, true);
            await decoding;
        }
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public void ASaveSaysTheFileIsInUseAndLeavesItAlone(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "held" + extension, 16, 8);
        var before = File.ReadAllBytes(path);

        // Another program reading the file, and not sharing it with writers.
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(RotationSaveResult.InUse, ImageRotationService.SaveQuarterTurns(path, 1));
        }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void ASaveSaysWhenTheFileIsGoneOrCannotStoreARotation()
    {
        using var temp = new TempDirectory();
        var gone = WriteImage(temp, "gone.jpg", 16, 8);
        File.Delete(gone);
        var sticker = Path.Combine(temp.Path, "sticker.webp");
        File.WriteAllBytes(sticker, "RIFF"u8.ToArray());

        Assert.Equal(RotationSaveResult.Failed, ImageRotationService.SaveQuarterTurns(gone, 1));
        Assert.Equal(RotationSaveResult.CannotCarry, ImageRotationService.SaveQuarterTurns(sticker, 1));
    }

    [Fact]
    public Task AFailedSaveTurnsTheViewBackAndTheNextOneLands() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo.jpg", 16, 8);
        var session = Open(path);
        var views = 0;
        session.ViewChanged += () => views++;
        var outcome = new TaskCompletionSource<RotationSaveResult>();
        session.NotSaved += result => outcome.TrySetResult(result);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Rotate(1);
            Assert.Equal((8, 16), Size(session));
            Assert.Equal(RotationSaveResult.InUse, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        // The view is the file again, and it was told so.
        Assert.Equal((16, 8), Size(session));
        Assert.Equal(2, views);
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(path));
        Assert.Equal(RotationPersistence.ExifTag, session.Persistence);

        // "Then rotate again": with the file free, it lands.
        session.Rotate(1);
        await WaitUntilAsync(() => ImageOrientation.Read(path) == 6);
        Assert.Equal(6, ImageOrientation.Read(path));
        Assert.Equal((8, 16), Size(session));
    });

    [Fact]
    public Task ASaveThatFailsAfterThePreviewLeftMovesNoView() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo.jpg", 16, 8);
        var session = Open(path);
        var views = 0;
        session.ViewChanged += () => views++;
        var outcome = new TaskCompletionSource<RotationSaveResult>();
        session.NotSaved += result => outcome.TrySetResult(result);

        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            session.Rotate(1);
            session.Commit();
            Assert.Equal(RotationSaveResult.InUse, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        // Its viewer may be fading out under the next item: it is not
        // turned back in front of the user.
        Assert.True(session.HasLeft);
        Assert.Equal(1, views);
        Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(path));
    });

    [Fact]
    public Task AFileFoundUnableToStoreARotationGoesViewOnlyAndKeepsItsTurn() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "edited.jpg", 16, 8);

        // An EXIF block the codec cannot patch and the splice must not
        // double: nothing says so until a save is tried.
        var original = File.ReadAllBytes(path);
        byte[] brokenExif = [0xFF, 0xE1, 0x00, 0x10, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0, 0, 0xDE, 0xAD, 0xBE, 0xEF, 0xDE, 0xAD];
        File.WriteAllBytes(path, [.. original.AsSpan(0, 2), .. brokenExif, .. original.AsSpan(2)]);
        var bytes = File.ReadAllBytes(path);
        var session = Open(path);
        Assert.Equal(RotationPersistence.ExifTag, session.Persistence);
        var outcome = new TaskCompletionSource<RotationSaveResult>();
        session.NotSaved += result => outcome.TrySetResult(result);

        session.Rotate(1);

        Assert.Equal(RotationSaveResult.CannotCarry, await outcome.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(RotationPersistence.CannotCarry, session.Persistence);
        Assert.Equal((8, 16), Size(session));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    });

    [Fact]
    public Task SavesOfOneFileFromTwoPreviewsLandOneAfterTheOther() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "shared.png", 1200, 800);
        var first = Open(path);
        var second = Open(path);

        first.Rotate(1);
        second.Rotate(1);
        first.Commit();
        second.Commit();
        await ImageRotationSession.WhenSaved(path);

        // Both quarter turns are in the file. Run together, the second
        // save would have found the first one's work in its way.
        Assert.Equal((1200, 800), (ImageHeader.TryRead(path)!.Width, ImageHeader.TryRead(path)!.Height));
        Assert.Equal(ViewOf(path, turns: 0), ViewOf(WriteImage(temp, "upright.png", 1200, 800), turns: 2));
    });

    // A header read that lands while a rotation is being written fails for
    // a moment. Kept, that failure hid the file's orientation until another
    // image was opened, and the photo showed on its side.
    [Fact]
    public void AHeaderReadThatFoundTheFileBusyIsNotKept()
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "busy.jpg", 16, 8);

        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(ImageHeader.TryRead(path));
        }

        Assert.Equal(16, ImageHeader.TryRead(path)!.Width);
    }

    [Fact]
    public void ADecodeReadsTheFileAsItIsNow()
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "changing.png", 10, 10);
        Assert.Equal(10, PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path))!.PixelWidth);

        // Written again under the same name: no cache of the old pixels
        // answers for it.
        WriteImage(temp, "changing.png", 30, 20);

        Assert.Equal(30, PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path))!.PixelWidth);
    }

    [Fact]
    public Task InThePreviewAFailedSaveTurnsThePictureBackAndSaysWhy() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 320, 200);
        var notice = PreviewWindow.RotationNotSavedNotice(path, RotationSaveResult.InUse);
        var window = OpenStage();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => window.CanRotateFromKeyboard && Viewer(window) is not null);

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                window.RotateFromKeyboard(1);
                Assert.Equal((200, 320), Shown(window));
                await WaitUntilAsync(() => Viewer(window)!.Value.Readout.Text == notice);
            }

            Assert.Equal(notice, Viewer(window)!.Value.Readout.Text);
            Assert.Equal((320, 200), Shown(window));
            Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(path));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task OnceThePreviewHasMovedOnAFailedSaveIsForTheTray() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 320, 200);
        var next = WriteImage(temp, "IMG_0413.jpg", 320, 200);
        var told = new TaskCompletionSource<(string Title, string Text)>();
        void OnUnseen(string title, string text) => told.TrySetResult((title, text));
        PreviewWindow.RotationNotSavedUnseen += OnUnseen;
        var window = OpenStage();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => window.CanRotateFromKeyboard && Viewer(window) is not null);

            using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                // Rotate, then arrow to the next photo before the save.
                window.RotateFromKeyboard(1);
                window.ShowPreview(next, IntPtr.Zero);
                var (title, text) = await told.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(PreviewWindow.RotationNotSavedTrayNotice(path, RotationSaveResult.InUse), (title, text));
                Assert.Contains("IMG_0412.jpg", text, StringComparison.Ordinal);
            }

            Assert.Equal(ImageOrientation.Normal, ImageOrientation.Read(path));
        }
        finally
        {
            PreviewWindow.RotationNotSavedUnseen -= OnUnseen;
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public void EveryFailureHasItsOwnWords()
    {
        const string photo = @"C:\photos\IMG_0412.jpg";
        var failures = Enum.GetValues<RotationSaveResult>().Where(result => result != RotationSaveResult.Saved).ToArray();
        var onScreen = failures.Select(result => PreviewWindow.RotationNotSavedNotice(photo, result)).ToArray();
        var inTray = failures.Select(result => PreviewWindow.RotationNotSavedTrayNotice(photo, result).Text).ToArray();

        Assert.Equal(failures.Length, onScreen.Distinct().Count());
        Assert.Equal(failures.Length, inTray.Distinct().Count());

        // "Not saved" is said of every save that left the file as it was,
        // and never of the one that saved the picture under another name.
        Assert.All(
            failures.Zip(onScreen),
            said => Assert.Equal(
                said.First != RotationSaveResult.SavedAside,
                said.Second.StartsWith("Rotation not saved. ", StringComparison.Ordinal)));
        Assert.All(inTray, sentence => Assert.Contains("IMG_0412.jpg", sentence, StringComparison.Ordinal));

        // docs/DESIGN.md, Words: straight quotes, no em dashes.
        Assert.All(onScreen.Concat(inTray), sentence => Assert.DoesNotContain(sentence, character => character is '\u2014' or '\u2019' or '\u201C' or '\u201D'));

        // A name long enough to push the sentence out of the notice is cut in the middle.
        var longName = new string('a', 80) + ".jpg";
        var cut = PreviewWindow.RotationNotSavedTrayNotice(Path.Combine(@"C:\photos", longName), RotationSaveResult.ReadOnly).Text;
        Assert.DoesNotContain(longName, cut, StringComparison.Ordinal);
        Assert.Contains("\u2026", cut, StringComparison.Ordinal);
        Assert.EndsWith("aaa.jpg is read-only.", cut, StringComparison.Ordinal);
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

    /// <summary>The image and the readout of the viewer on screen, once it is there.</summary>
    private static (Image Picture, TextBlock Readout)? Viewer(PreviewWindow window) =>
        window.PreviewContent.Content is Grid { Children: [Canvas { Children: [Border { Child: Image picture }] }, Border { Child: TextBlock readout }, ..] }
            ? (picture, readout)
            : null;

    private static (int Width, int Height) Shown(PreviewWindow window)
    {
        var source = (BitmapSource)Viewer(window)!.Value.Picture.Source;
        return (source.PixelWidth, source.PixelHeight);
    }

    private static byte[] ViewOf(string path, int turns)
    {
        var shown = new FormatConvertedBitmap(
            ImageOrientation.Apply(PreviewWindow.TryDecodeImage(path, ImageHeader.TryRead(path))!, ImageOrientation.FromQuarterTurns(turns)),
            PixelFormats.Bgra32,
            null,
            0);
        var pixels = new byte[shown.PixelWidth * shown.PixelHeight * 4];
        shown.CopyPixels(pixels, shown.PixelWidth * 4, 0);
        return pixels;
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
