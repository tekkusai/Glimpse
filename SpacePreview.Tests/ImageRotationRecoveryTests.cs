using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A save builds the turned picture beside the original and swaps it in.
/// Whatever happens to the swap, the picture is never lost: the working
/// file goes only while the original is known to be there, and when it is
/// the only copy it takes the picture's name or stays and is named. The
/// swap itself is Windows' ReplaceFile; where a test needs it to fail a
/// certain way, a stand-in fails that way and leaves the files as Windows
/// would.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRotationRecoveryTests
{
    private const int UnableToMoveReplacement = unchecked((int)0x80070498);

    // Windows documents one failure of ReplaceFile, with no backup name
    // given, that leaves the replaced file gone and the new one still
    // under its working name. 3.0.1 deleted the working file after every
    // failed save, and with it the picture.
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public void AReplaceThatLosesTheOriginalLeavesTheTurnedPictureUnderItsName(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo" + extension, 16, 8);
        var turned = TurnedTwin(temp, path);

        var result = ImageRotationService.SaveQuarterTurns(path, 1, (working, original) =>
        {
            File.Delete(original);
            throw new IOException("Unable to move the replacement file to the file to be replaced.", UnableToMoveReplacement);
        });

        Assert.Equal(RotationSaveResult.Saved, result);
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
        Assert.Equal(turned, File.ReadAllBytes(path));
    }

    // The same failure with the picture's name still taken: the original
    // is on its way out, and a program that has it open has not let go.
    // The turned copy is the only picture there will be, so it stays, and
    // the result names it.
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public async Task ATurnedCopyThatCannotTakeThePicturesNameIsKeptAndNamed(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo" + extension, 16, 8);
        var working = ImageRotationService.TempPathFor(path);
        var turned = TurnedTwin(temp, path);
        DoomedFile? doomed = null;
        var clock = Stopwatch.StartNew();
        try
        {
            var result = ImageRotationService.SaveQuarterTurns(path, 1, (_, original) =>
            {
                doomed = new DoomedFile(original);
                throw new IOException("Unable to move the replacement file to the file to be replaced.", UnableToMoveReplacement);
            });

            // It asked for the name for about a second and a half first.
            Assert.Equal(RotationSaveResult.SavedAside, result);
            Assert.InRange(clock.Elapsed, TimeSpan.FromSeconds(1.4), TimeSpan.FromSeconds(6));
            Assert.Equal(turned, File.ReadAllBytes(working));
        }
        finally
        {
            doomed?.Dispose();
        }

        // The original is gone now, as it was always going to be. Nothing
        // that tidies up after a save takes the copy that is left.
        Assert.False(File.Exists(path));
        ImageRotationSession.RemoveStrandedTemp(path);
        await ImageRotationSession.WhenSaved(path);
        Assert.Equal(new[] { working }, Directory.GetFiles(temp.Path));
        Assert.Equal(turned, File.ReadAllBytes(working));
        Assert.Contains(
            $"\"{Path.GetFileName(working)}\"",
            PreviewWindow.RotationNotSavedNotice(path, RotationSaveResult.SavedAside),
            StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnedCopyTakesThePicturesNameAsSoonAsItIsFree()
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo.png", 16, 8);
        var turned = TurnedTwin(temp, path);
        Task? released = null;

        var result = ImageRotationService.SaveQuarterTurns(path, 1, (_, original) =>
        {
            // The program holding the doomed original lets go a moment later.
            var doomed = new DoomedFile(original);
            released = Task.Delay(300).ContinueWith(_ => doomed.Dispose());
            throw new UnauthorizedAccessException("Access to the path is denied.");
        });

        Assert.Equal(RotationSaveResult.Saved, result);
        Assert.NotNull(released);
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
        Assert.Equal(turned, File.ReadAllBytes(path));
    }

    // What this PC does with that failure (the working file on another
    // volume provokes it for real): both files keep their names. Then the
    // picture is safe where it is, and the save has simply failed.
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".bmp")]
    public void AReplaceThatFailsWithTheOriginalInPlaceIsAnOrdinaryFailedSave(string extension)
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "photo" + extension, 16, 8);
        var before = File.ReadAllBytes(path);

        var result = ImageRotationService.SaveQuarterTurns(
            path,
            1,
            (_, _) => throw new IOException("Unable to move the replacement file to the file to be replaced.", UnableToMoveReplacement));

        Assert.Equal(RotationSaveResult.Failed, result);
        Assert.Equal(new[] { path }, Directory.GetFiles(temp.Path));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // The picture is as the view shows it, under another name. The view
    // does not turn back, and the user is told the name where it lasts:
    // on the image, and in the tray although the preview is still up.
    [Fact]
    public Task APictureSavedUnderAnotherNameIsSaidOnTheImageAndInTheTray() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "IMG_0412.jpg", 320, 200);
        var told = new List<(string Title, string Text)>();
        void OnTold(string title, string text) => told.Add((title, text));
        PreviewWindow.RotationNotSavedUnseen += OnTold;

        // Never shown and never given a handle.
        var window = new PreviewWindow();
        try
        {
            var content = await ImageRouteTests.BuildAsync(window, path);
            var session = Fields.Get<ImageRotationSession>(window, "_rotationSession");
            var views = 0;
            session.ViewChanged += () => views++;

            // The turn is made, and its save comes back having left the
            // picture under the working file's name.
            session.Rotate(1);
            Fields.Call(session, "OnSaved", 1, RotationSaveResult.SavedAside);

            var sentence = "The rotated picture was saved as \"IMG_0412.jpg.spvw-rotate.tmp\" in the same folder. Rename it to \"IMG_0412.jpg\".";
            Assert.Equal((200, 320), (session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight));
            Assert.Equal(1, views);
            Assert.Equal(sentence, ((TextBlock)((Border)((Grid)content).Children[1]).Child).Text);
            Assert.Equal(new[] { ("Rotation saved under another name", sentence) }, told);
        }
        finally
        {
            PreviewWindow.RotationNotSavedUnseen -= OnTold;

            // Closing hands the turn to the disk for real: it lands before the folder goes.
            window.Close();
            await ImageRotationSession.WhenSaved(path);
        }
    });

    // A file being deleted while a program still has it open keeps its
    // name, and File.Exists says yes to it, until that program lets go.
    // Cleaning up "because the original is there" then deleted the only
    // copy that would be left.
    [Fact]
    public async Task OpeningAPictureLeavesAWorkingFileThatIsTheOnlyCopy()
    {
        using var temp = new TempDirectory();
        var path = WriteImage(temp, "scan.png", 16, 8);
        var working = ImageRotationService.TempPathFor(path);
        File.Copy(path, working);

        using (new DoomedFile(path))
        {
            Assert.True(File.Exists(path), "Windows still lists the picture");

            ImageRotationSession.RemoveStrandedTemp(path);
            await ImageRotationSession.WhenSaved(path);

            Assert.True(File.Exists(working));
        }

        Assert.False(File.Exists(path));
        Assert.True(File.Exists(working));
    }

    /// <summary>What the file holds once it has been turned the ordinary way.</summary>
    private static byte[] TurnedTwin(TempDirectory temp, string path)
    {
        var twin = Path.Combine(temp.Path, "twin" + Path.GetExtension(path));
        File.Copy(path, twin);
        Assert.Equal(RotationSaveResult.Saved, ImageRotationService.SaveQuarterTurns(twin, 1));
        var turned = File.ReadAllBytes(twin);
        File.Delete(twin);
        return turned;
    }

    /// <summary>A picture with a different color in every corner, so a turn shows.</summary>
    internal static string WriteImage(TempDirectory temp, string name, int width, int height)
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
}

/// <summary>
/// A file that is being deleted while a program still has it open, the way
/// Windows did it before deletes took effect at once and still does on
/// FAT, exFAT and network folders: the name stays in the folder and
/// File.Exists says yes, nothing can open it, and it is gone the moment
/// the last handle closes. Disposing closes that handle.
/// </summary>
internal sealed class DoomedFile : IDisposable
{
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfo = 4;

    private readonly FileStream _keeper;

    public DoomedFile(string path)
    {
        _keeper = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var marker = CreateFileW(path, Delete, ShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        Assert.False(marker.IsInvalid, "opened for delete");
        byte delete = 1;
        Assert.True(SetFileInformationByHandle(marker, FileDispositionInfo, ref delete, 1), "marked for delete");
    }

    public void Dispose() => _keeper.Dispose();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, int size);
}
