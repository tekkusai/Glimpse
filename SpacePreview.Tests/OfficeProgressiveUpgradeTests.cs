using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A big deck shows its first slides while the rest converts, then swaps to
/// the whole deck in place, at the slide the reader is on. A wheel alone
/// never tells the rail, so the viewer is asked; a slide the reader asked
/// for before it arrived still wins.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class OfficeProgressiveUpgradeTests
{
    [Fact]
    public Task TheWholeDeckOpensAtTheSlideTheViewerShows() => WpfApp.RunAsync(async () =>
        Assert.Equal(3, await UpgradePageAsync(askedPage: null)));

    [Fact]
    public Task ASlideAskedForBeforeItArrivedWinsOverTheViewer() => WpfApp.RunAsync(async () =>
        Assert.Equal(9, await UpgradePageAsync(askedPage: 9)));

    /// <summary>
    /// The first slides' viewer stands on slide 3 while the rail still says
    /// 1, as after scrolling by wheel. The rest lands, and the replacement's
    /// address names the slide it opens at.
    /// </summary>
    private static async Task<int> UpgradePageAsync(int? askedPage)
    {
        using var temp = new TempDirectory();
        var partialPath = Path.Combine(temp.Path, "first slides.pdf");
        var fullPath = Path.Combine(temp.Path, "whole deck.pdf");
        FillSignBehaviorTests.WritePdf(partialPath, 400, 300, pages: 6);
        FillSignBehaviorTests.WritePdf(fullPath, 400, 300, pages: 12);
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(partialPath, IntPtr.Zero);
            var rail = await UntilAsync(
                () => Fields.Get<bool>(window, "_loadFinished") ? Fields.Get<object?>(window, "_activePdfRail") : null,
                "The PDF never showed.");

            // The rail's thumbnails come from a helper this host cannot start.
            if (Fields.Get<object?>(rail, "Document") is null)
            {
                Fields.Set(rail, "Document", new PdfPreviewDocument([], 6));
            }

            var first = Fields.Get<WebView2>(rail, "WebView");
            Fields.Call(window, "StagePdfViewerJump", rail, 3);
            var viewer = await UntilAsync(
                () => Fields.Get<WebView2>(rail, "WebView") is var shown && !ReferenceEquals(shown, first) ? shown : null,
                "The viewer never moved to slide 3.");
            await UntilAsync(
                async () => await ReadPageAsync(window) == 3
                    && Equals(Fields.Get<string?>(rail, "LastPageSource"), viewer.CoreWebView2?.Source),
                "The viewer never said slide 3.");

            // Wheel scrolling leaves no sign of reading, so the rail keeps 1.
            Fields.Set(rail, "Reading", false);
            await UntilAsync(() => !Fields.Get<bool>(rail, "PageSyncInFlight"), "Page sync never settled.");
            Fields.Set(rail, "CurrentPage", 1);

            var job = new OfficeConversionJob("progressive test", Path.Combine(temp.Path, "deck.pptx"), OfficeKind.Presentation);
            var partial = new ConvertedOfficeDocument(partialPath, PartialPages: 6, ExpectedPages: 12);
            Fields.Call(window, "FollowProgressiveConversion", partial, job, rail, new Border());
            if (askedPage is { } asked)
            {
                Assert.True((bool)Fields.Get<Delegate>(rail, "TryHandleJump").DynamicInvoke(rail, asked)!);
            }

            Assert.Equal(1, Fields.Get<int>(rail, "CurrentPage"));
            job.PublishComplete(new ConvertedOfficeDocument(fullPath));

            var surface = Fields.Get<Grid>(rail, "Surface");
            var whole = new Uri(fullPath).AbsoluteUri;
            var replacement = await UntilAsync(
                () => surface.Children.OfType<WebView2>()
                    .FirstOrDefault(view => view.Source?.AbsoluteUri.StartsWith(whole, StringComparison.OrdinalIgnoreCase) == true),
                "The whole deck never staged.");
            return int.Parse(
                System.Text.RegularExpressions.Regex.Match(replacement.Source.Fragment, @"page=(\d+)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task<int> ReadPageAsync(PreviewWindow window)
    {
        var read = (Task)Fields.Call(window, "ReadPdfPositionAsync")!;
        await read;
        return (((int Page, int TotalPages))read.GetType().GetProperty("Result")!.GetValue(read)!).Page;
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

    private static async Task UntilAsync(Func<bool> condition, string failure) =>
        await UntilAsync(() => condition() ? string.Empty : null, failure);

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100);
        }
    }
}
