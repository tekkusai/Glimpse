using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using static SpacePreview.Tests.ImageTestFiles;

namespace SpacePreview.Tests;

/// <summary>
/// Every sentence a rotation can show, and that each one gives the true
/// reason: the kind of file, this file's read-only attribute, or what this
/// one file holds (docs/DESIGN.md, Words and Honest words).
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ImageRotationWordsTests
{
    [Fact]
    public void AFileThatWillNotSaveIsToldWhy()
    {
        using var temp = new TempDirectory();
        var photo = WritePicture(temp, "photo.jpg", new JpegBitmapEncoder());
        var locked = WritePicture(temp, "locked.jpg", new JpegBitmapEncoder());
        var sticker = Path.Combine(temp.Path, "sticker.webp");
        File.WriteAllBytes(sticker, "RIFF"u8.ToArray());
        var lockedSticker = Path.Combine(temp.Path, "locked.webp");
        File.WriteAllBytes(lockedSticker, "RIFF"u8.ToArray());

        // A color key: this PNG would come back in another pixel format.
        var keyed = Path.Combine(temp.Path, "keyed.png");
        File.WriteAllBytes(keyed, Png(5, 3, 8, 2, new byte[5 * 3 * 3], [new PngChunk("tRNS", new byte[6])]));
        var lockedKeyed = Path.Combine(temp.Path, "locked.png");
        File.Copy(keyed, lockedKeyed);

        string[] readOnly = [locked, lockedSticker, lockedKeyed];
        foreach (var path in readOnly)
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        try
        {
            Assert.Equal(RotationPersistence.ExifTag, ImageRotationService.Classify(photo));
            Assert.Equal(RotationPersistence.ReadOnly, ImageRotationService.Classify(locked));
            Assert.Equal(RotationPersistence.ViewOnly, ImageRotationService.Classify(sticker));
            Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(keyed));

            // Clearing the attribute would not make these save, so they
            // are not told that it would.
            Assert.Equal(RotationPersistence.ViewOnly, ImageRotationService.Classify(lockedSticker));
            Assert.Equal(RotationPersistence.CannotCarry, ImageRotationService.Classify(lockedKeyed));
        }
        finally
        {
            foreach (var path in readOnly)
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    // 3.0.0 blamed the file type for a read-only JPEG.
    [Fact]
    public Task TheTooltipOfAReadOnlyPictureSaysItIsReadOnly() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = WritePicture(temp, "locked.jpg", new JpegBitmapEncoder());
        File.SetAttributes(path, FileAttributes.ReadOnly);
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => window.CanRotateFromKeyboard
                && window.PreviewContent.Content is Grid
                && ((string)window.RotateLeftButton.ToolTip).Contains("Not saved", StringComparison.Ordinal));

            Assert.Equal("Rotate left (Ctrl+Shift+R). Not saved. This file is read-only.", window.RotateLeftButton.ToolTip);
            Assert.Equal("Rotate right (Ctrl+R). Not saved. This file is read-only.", window.RotateRightButton.ToolTip);
        }
        finally
        {
            window.HidePreview();
            window.Close();
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    public void TheseAreAllTheWordsARotationCanShow()
    {
        Assert.Equal(string.Empty, PreviewWindow.RotateTooltipSuffix(RotationPersistence.ExifTag));
        Assert.Equal(string.Empty, PreviewWindow.RotateTooltipSuffix(RotationPersistence.Reencode));
        Assert.Equal(string.Empty, PreviewWindow.RotateTooltipSuffix(null));
        Assert.Equal(". Not saved for this file type.", PreviewWindow.RotateTooltipSuffix(RotationPersistence.ViewOnly));
        Assert.Equal(". Not saved. This file is read-only.", PreviewWindow.RotateTooltipSuffix(RotationPersistence.ReadOnly));
        Assert.Equal(". Not saved. This file can't store a rotation.", PreviewWindow.RotateTooltipSuffix(RotationPersistence.CannotCarry));

        Assert.Equal(
            new[]
            {
                "Rotation not saved. This file can't store it.",
                "Rotation not saved. Close the app using this file, then rotate again.",
                "Rotation not saved. This file is read-only.",
                "Rotation not saved. Free up disk space, then rotate again.",
                "Rotation not saved. This file can't be changed."
            },
            Failures.Select(result => PreviewWindow.RotationNotSavedNotice(@"C:\photos\IMG_0412.jpg", result)).ToArray());

        Assert.Equal(
            new[]
            {
                "IMG_0412.jpg can't store a rotation.",
                "Close the app using IMG_0412.jpg, then rotate it again.",
                "IMG_0412.jpg is read-only.",
                "Free up disk space, then rotate IMG_0412.jpg again.",
                "IMG_0412.jpg can't be changed."
            },
            Failures.Select(result => PreviewWindow.RotationNotSavedTrayNotice(@"C:\photos\IMG_0412.jpg", result).Text).ToArray());
        Assert.All(
            Failures,
            result => Assert.Equal("Rotation not saved", PreviewWindow.RotationNotSavedTrayNotice(@"C:\photos\IMG_0412.jpg", result).Title));

        // The one save that leaves the picture under another name gives
        // both names, on the image and in the tray, and says what to do.
        const string aside = "The rotated picture was saved as \"IMG_0412.jpg.spvw-rotate.tmp\" in the same folder. Rename it to \"IMG_0412.jpg\".";
        Assert.Equal(aside, PreviewWindow.RotationNotSavedNotice(@"C:\photos\IMG_0412.jpg", RotationSaveResult.SavedAside));
        Assert.Equal(
            ("Rotation saved under another name", aside),
            PreviewWindow.RotationNotSavedTrayNotice(@"C:\photos\IMG_0412.jpg", RotationSaveResult.SavedAside));
    }

    private static RotationSaveResult[] Failures =>
        [RotationSaveResult.CannotCarry, RotationSaveResult.InUse, RotationSaveResult.ReadOnly, RotationSaveResult.DiskFull, RotationSaveResult.Failed];

    private static string WritePicture(TempDirectory temp, string name, BitmapEncoder encoder)
    {
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 8, 96, 96, PixelFormats.Bgr24, null, new byte[16 * 8 * 3], 16 * 3)));
        var path = Path.Combine(temp.Path, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
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
