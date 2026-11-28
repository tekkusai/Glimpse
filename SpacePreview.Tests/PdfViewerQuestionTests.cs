using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Glimpse's owned PDF viewer answers position queries directly over
/// postMessage without UI Automation or Chromium accessibility trees.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PdfViewerQuestionTests
{
    [Fact]
    public Task AReaderWhoOnlyUsedTheWheelIsFoundOnTheirPage() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 12);
        await viewer.WheelAsync(4000);
        Assert.False(viewer.Window.UserEngagedContent);
        Assert.False(viewer.Window.ContentOwnsKeyboard());

        var (page, total) = await viewer.ReadPositionAsync();

        var shown = await viewer.PageInputAsync();
        Assert.True(shown > 1, "The wheel never moved the viewer.");
        Assert.Equal(shown, page);
        Assert.Equal(12, total);
    });

    [Fact]
    public Task ViewerNeverFetchesExternalResources()
    {
        Assert.True(PdfViewerPage.IsAllowedResource("https://glimpse-pdf.local/viewer.html?page=1"));
        Assert.True(PdfViewerPage.IsAllowedResource("https://glimpse-pdf.local/document.pdf"));
        Assert.True(PdfViewerPage.IsAllowedResource("https://glimpse-pdf.local/pdf.min.mjs"));
        Assert.True(PdfViewerPage.IsAllowedResource("data:image/png;base64,AAAA"));
        Assert.True(PdfViewerPage.IsAllowedResource("blob:https://glimpse-pdf.local/1234"));
        Assert.False(PdfViewerPage.IsAllowedResource("https://example.com/tracker.png"));
        Assert.False(PdfViewerPage.IsAllowedResource("http://127.0.0.1:8080/leak"));
        Assert.False(PdfViewerPage.IsAllowedResource("file:///C:/Windows/win.ini"));
        Assert.False(PdfViewerPage.IsAllowedResource("file://nas/share/secret.pdf"));
        return Task.CompletedTask;
    }

    [Fact]
    public void NoUiAutomationWalkExistsInPdfViewer()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.Pdf.cs");
        Assert.DoesNotContain("TreeScope.Descendants", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationElement", source, StringComparison.Ordinal);
    }

    [Fact]
    public Task LiveWebViewBlocksForeignFetchAndReportsZoomedOutRailJumpCleanly() => WpfApp.RunAsync(async () =>
    {
        using var viewer = await Viewer.ShowAsync(pages: 3, width: 400, height: 300);
        var core = Fields.Get<WebView2>(viewer.Rail, "WebView").CoreWebView2;

        // Foreign network fetch inside WebView2 must be intercepted by our
        // WebResourceRequested handler with synthetic 403 Blocked (not merely
        // dropped by the dead proxy).
        int? observedStatus = null;
        string? observedReason = null;
        core.WebResourceResponseReceived += (_, args) =>
        {
            if (args.Request.Uri.Equals("https://example.com/blocked.png", StringComparison.OrdinalIgnoreCase))
            {
                observedStatus = args.Response.StatusCode;
                observedReason = args.Response.ReasonPhrase;
            }
        };

        await core.ExecuteScriptAsync(
            "window.__fetchResult = 'pending'; fetch('https://example.com/blocked.png', { mode: 'no-cors' }).then(() => { window.__fetchResult = 'done'; }).catch(() => { window.__fetchResult = 'blocked'; });");
        var fetchDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (observedStatus is null)
        {
            Assert.True(DateTime.UtcNow < fetchDeadline, "Foreign fetch never received synthetic 403.");
            await Task.Delay(25);
        }

        Assert.Equal(403, observedStatus);
        Assert.Equal("Blocked", observedReason);

        // Zoom out to minimum scale so all 3 landscape pages fit together in the viewport,
        // then jump to page 3: jumpAck must clear PendingJumpId and preserve page 3 instead of snapping back.
        await core.ExecuteScriptAsync("for (let i = 0; i < 16; i++) document.getElementById('zoomOut').click();");
        await Task.Delay(120);
        var geometry = (await core.ExecuteScriptAsync("`${document.documentElement.scrollHeight}:${window.innerHeight}`")).Trim('"');
        var parts = geometry.Split(':');
        Assert.True(
            int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) <= int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) + 2,
            $"Expected zoomed-out 3-page PDF to fit in viewport, got scrollHeight:innerHeight = {geometry}");

        Fields.Call(viewer.Window, "JumpPdfToPage", viewer.Rail, 3);
        await Task.Delay(150);

        Assert.Null(Fields.Get<int?>(viewer.Rail, "PendingJumpId"));
        var (page, total) = await viewer.ReadPositionAsync();
        Assert.Equal(3, page);
        Assert.Equal(3, total);
    });

    private sealed class Viewer : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public PreviewWindow Window { get; private set; } = null!;
        public object Rail { get; private set; } = null!;

        public static async Task<Viewer> ShowAsync(int pages, int width = 612, int height = 792)
        {
            var viewer = new Viewer();
            var path = Path.Combine(viewer._temp.Path, "pages.pdf");
            FillSignBehaviorTests.WritePdf(path, width, height, pages);
            viewer.Window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
            viewer.Window.MarkGeometryUserPinnedForProbe();
            viewer.Window.ShowPreview(path, IntPtr.Zero);
            viewer.Rail = await UntilAsync(
                () => Fields.Get<bool>(viewer.Window, "_loadFinished") ? Fields.Get<object?>(viewer.Window, "_activePdfRail") : null,
                "The PDF never showed.");

            if (Fields.Get<object?>(viewer.Rail, "Document") is null)
            {
                Fields.Set(viewer.Rail, "Document", new PdfPreviewDocument([], pages));
            }

            return viewer;
        }

        public Task WheelAsync(int distance) =>
            PdfViewerWheel.ScrollAsync(Fields.Get<WebView2>(Rail, "WebView").CoreWebView2, distance);

        public async Task<(int Page, int TotalPages)> ReadPositionAsync()
        {
            var read = (Task)Fields.Call(Window, "ReadPdfPositionAsync")!;
            await read;
            return ((int Page, int TotalPages))read.GetType().GetProperty("Result")!.GetValue(read)!;
        }

        public async Task<int> PageInputAsync()
        {
            var webView = Fields.Get<WebView2>(Rail, "WebView");
            var raw = await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('pageInput').value");
            return int.Parse(raw.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            Window.Close();
            _temp.Dispose();
        }
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
}
