using System.Globalization;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The owned PDF viewer jumps by direct postMessage, so right-to-left display
/// languages and narrow or wide stages all jump accurately without aiming at
/// toolbar pixels.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PdfPageBoxAimTests
{
    [Theory]
    [InlineData("ar-SA", 480)]
    [InlineData("ar-SA", 1400)]
    [InlineData("he-IL", 480)]
    [InlineData("he-IL", 1400)]
    public Task ARightToLeftViewerTakesTheJumpDirectly(string language, double width) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "pages.pdf");
        FillSignBehaviorTests.WritePdf(path, 612, 792, 120);

        var culture = CultureInfo.CurrentUICulture;
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = width, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(language);
            window.ShowPreview(path, IntPtr.Zero);
            var rail = await UntilAsync(
                () => Fields.Get<bool>(window, "_loadFinished") ? Fields.Get<object?>(window, "_activePdfRail") : null,
                "The PDF never showed.");

            Fields.Call(window, "JumpPdfToPage", rail, 5);
            await UntilAsync(async () => (await ReadPositionAsync(window)).Page == 5, "Viewer never reached page 5.");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Fields.Call(window, "JumpPdfToPage", rail, 97);
            await UntilAsync(async () => (await ReadPositionAsync(window)).Page == 97, "Viewer never reached page 97.");
        }
        finally
        {
            CultureInfo.CurrentUICulture = culture;
            window.Close();
        }
    });

    private static async Task<(int Page, int TotalPages)> ReadPositionAsync(PreviewWindow window)
    {
        var read = (Task)Fields.Call(window, "ReadPdfPositionAsync")!;
        await read;
        return ((int Page, int TotalPages))read.GetType().GetProperty("Result")!.GetValue(read)!;
    }

    private static async Task<T> UntilAsync<T>(Func<T?> value, string failure)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (value() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(20);
        }
    }

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(30);
        }
    }
}
