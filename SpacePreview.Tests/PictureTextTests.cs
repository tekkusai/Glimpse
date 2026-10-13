using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// "Copy text" reads the words in a picture with the engine Windows ships.
/// The tests draw their own pictures. On a PC where Windows has no language
/// its engine can read, they pass without a word: there is nothing to read
/// with, and the menu there offers nothing either.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PictureTextTests
{
    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    [Fact]
    public Task TheWordsInAPictureAreReadLineByLine() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        var text = await PictureText.ReadAsync(Draw("Invoice 2048 for October", "Total due 315 dollars"));

        var lines = text.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("Invoice 2048 for October", lines[0]);
        Assert.Equal("Total due 315 dollars", lines[1]);
    });

    [Fact]
    public Task APictureWithNoWordsReadsAsNothing() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        Assert.Equal(string.Empty, await PictureText.ReadAsync(Draw()));
    });

    // Text photographed sideways is read once the viewer has turned it:
    // the picture is read as it is shown.
    [Fact]
    public Task CopyTextCopiesTheWordsOfThePictureAsItIsShown() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "receipt.png");
        Save(new TransformedBitmap(Draw("Invoice 2048 for October", "Total due 315 dollars"), new RotateTransform(270)), path);
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is not null && window.CanRotateFromKeyboard);
            Assert.Contains("Copy text", MenuLabels(window));

            // Lying on its side, the engine makes little of it. Turned
            // upright by the viewer, it is read.
            Fields.Call(window, "RotateCurrentImage", 1);
            string? copied = null;
            await window.CopyPictureTextAsync(text => copied = text);

            Assert.Equal("Invoice 2048 for October" + Environment.NewLine + "Total due 315 dollars", copied);
            Assert.Equal("Text copied.", Hud(window));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task APictureWithoutWordsSaysSoAndCopiesNothing() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sky.png");
        Save(Draw(), path);
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);

            // The viewer answers for the picture once it has joined the window.
            await WaitUntilAsync(() => Revealed(window) && Fields.Get<object?>(window, "_imageZoom") is ImageViewer);
            var copied = false;
            await window.CopyPictureTextAsync(_ => copied = true);

            Assert.False(copied);
            Assert.Equal("No text found in this picture.", Hud(window));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task OnlyAPictureOffersCopyText() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("notes.txt", "Invoice 2048 for October");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window));

            Assert.DoesNotContain("Copy text", MenuLabels(window));
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    /// <summary>Black lines of text on a white page, or the page alone.</summary>
    private static BitmapSource Draw(params string[] lines)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 300));
            for (var i = 0; i < lines.Length; i++)
            {
                var text = new FormattedText(
                    lines[i],
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    44,
                    Brushes.Black,
                    1.0);
                context.DrawText(text, new Point(40, 50 + i * 100));
            }
        }

        var bitmap = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static void Save(BitmapSource picture, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static string[] MenuLabels(PreviewWindow window)
    {
        var menu = new ContextMenu();
        window.FillContextMenu(menu, selectedText: null);
        return [.. menu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? string.Empty)];
    }

    /// <summary>The words on the viewer's notice.</summary>
    private static string? Hud(PreviewWindow window) =>
        Fields.Get<object?>(window, "_imageZoom") is ImageViewer viewer ? viewer.NoticeText : null;

    private static PreviewWindow Park()
    {
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        return new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
    }

    private static bool Revealed(PreviewWindow window) => Fields.Get<bool>(window, "_loadFinished");

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
