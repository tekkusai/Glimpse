using SpacePreview.Preview;
using SpacePreview.Utilities;

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePreview.Tests;

public sealed class PreviewChromeRegressionTests
{
    /// <summary>
    /// The hovered row's thumbnail takes the folder card's own art slot. A
    /// popup window would steal activation from Explorer.
    /// </summary>
    [Fact]
    public void FolderRowHoverReusesTheFolderArtworkSlot()
    {
        var hoverSource = ReadSource(@"SpacePreview\Preview\FolderHoverThumbnail.cs");

        Assert.DoesNotContain("Popup", hoverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacementTarget", hoverSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rail jumps pages by clicking the viewer's page box at a measured
    /// offset, so the visible toolbar set is load-bearing. Showing Fit moved
    /// the box out from under that click and the following Ctrl+A selected the
    /// whole document instead of the page number.
    /// </summary>
    [Fact]
    public void ThePdfToolbarSetStaysPinnedToTheRailsJumpCoordinate()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var configure = ReadMethod(windowSource, "private static void ConfigurePdfViewer");

        foreach (var hidden in new[] { "Save", "SaveAs", "Rotate", "FitPage", "PageLayout", "Bookmarks", "FullScreen", "MoreSettings" })
        {
            Assert.Contains($"CoreWebView2PdfToolbarItems.{hidden}", configure, StringComparison.Ordinal);
        }

        var jump = ReadMethod(windowSource, "private static async Task JumpPdfWithNativePageBoxAsync");
        Assert.Contains("webView.ActualWidth / 2 + 15", jump, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two jump sequences cannot overlap, but a click during one used to be
    /// discarded while the rail had already moved its highlight, leaving the
    /// strip pointing at a page the document never went to.
    /// </summary>
    [Fact]
    public void ARailClickDuringAJumpIsHeldRatherThanDropped()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var jump = ReadMethod(windowSource, "private async Task JumpPdfToPageAsync");

        Assert.Contains("state.PendingJumpPage = page;", jump, StringComparison.Ordinal);
        Assert.Contains("state.PendingJumpPage = null;", jump, StringComparison.Ordinal);
        Assert.Contains("JumpPdfToPage(state, pending);", jump, StringComparison.Ordinal);
    }

    [Fact]
    public void FolderHoverArtworkMatchesTheRestingRasterBounds()
    {
        var artwork = BitmapSource.Create(
            224,
            176,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[224 * 176 * 4],
            224 * 4);
        var restingFolder = BitmapSource.Create(
            246,
            193,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[246 * 193 * 4],
            246 * 4);

        var matched = PreviewWindow.MatchFolderArtworkBounds(artwork, restingFolder);

        Assert.Equal(restingFolder.PixelWidth, matched.PixelWidth);
        Assert.Equal(restingFolder.PixelHeight, matched.PixelHeight);
    }

    [Fact]
    public void FolderHoverArtworkUsesTheRestingFolderEdgeAlpha()
    {
        const int width = 4;
        const int height = 4;
        var hoverPixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
        var restingPixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();

        for (var y = 0; y < height; y++)
        {
            restingPixels[((y * width) + width - 1) * 4 + 3] = 64;
        }

        for (var x = 0; x < width; x++)
        {
            restingPixels[(((height - 1) * width) + x) * 4 + 3] = 32;
        }

        var artwork = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, hoverPixels, width * 4);
        var restingFolder = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, restingPixels, width * 4);

        var matched = PreviewWindow.MatchFolderArtworkBounds(artwork, restingFolder);
        var matchedPixels = new byte[width * height * 4];
        matched.CopyPixels(matchedPixels, width * 4, 0);

        Assert.Equal(255, matchedPixels[((1 * width) + 1) * 4 + 3]);
        Assert.Equal(64, matchedPixels[((1 * width) + width - 1) * 4 + 3]);
        Assert.Equal(32, matchedPixels[(((height - 1) * width) + 1) * 4 + 3]);
    }

    [Fact]
    public void EpubChapterRailNavigatesAnchorsWithinOneDocument()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var epubSource = ReadSource(@"SpacePreview\Preview\EpubPreviewLoader.cs");
        var themeSource = ReadSource(@"SpacePreview\Preview\GeneratedHtmlTheme.cs");
        var renderBody = ReadMethod(windowSource, "private void RenderEpubChapterRail");
        var railBody = ReadMethod(windowSource, "private static FrameworkElement BuildEpubChapterRail");
        var railSurfaceBody = ReadMethod(windowSource, "private static FrameworkElement BuildRailSurface");

        // The book stays one continuous document; the rail scrolls to anchors.
        Assert.Contains("<section id=\\\"ch-", epubSource, StringComparison.Ordinal);
        Assert.Contains("return BuildEpubWebPreview(epub, path, token);", windowSource, StringComparison.Ordinal);
        Assert.Contains("getElementById('ch-{spineIndex}')?.scrollIntoView", windowSource, StringComparison.Ordinal);

        // Rail resizes reuse the shared viewer cloak so hide/show never flashes.
        Assert.Contains("CloakViewerForRailResize(state.WebView, state.Version);", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.RailVerticalOffset", renderBody, StringComparison.Ordinal);
        Assert.Contains("BuildRailSurface", railBody, StringComparison.Ordinal);
        Assert.Contains("scroll.ScrollToVerticalOffset(verticalOffset)", railSurfaceBody, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(scroll, 1)", railSurfaceBody, StringComparison.Ordinal);

        // Anchor landings keep a breath of space above the chapter.
        Assert.Contains("scroll-margin-top", themeSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// The PDF traps no behavior test can reach, because they live inside the
    /// Chromium viewer. Hiding Search collapses the whole toolbar strip, page
    /// box included, and the rail's instant jump types into that box. The
    /// viewer honours #page only at document load, so a staged jump is a real
    /// load with a cache-busting query; a runtime location.hash does nothing.
    /// </summary>
    [Fact]
    public void PdfViewerTrapsStayGuarded()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var stagedReplacementBody = ReadMethod(windowSource, "private void StagePdfViewerReplacement");

        foreach (var visible in new[] { "Search", "Print", "ZoomIn", "ZoomOut", "PageSelector" })
        {
            Assert.DoesNotContain($"CoreWebView2PdfToolbarItems.{visible}", windowSource, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("location.hash", windowSource, StringComparison.Ordinal);
        Assert.Contains("?jump={target}#page={target}&view=FitH", windowSource, StringComparison.Ordinal);

        // Navigation and the 4 s escape share one owner: the escape stands
        // down once the paint wait has started, so a slow paint is never
        // swapped in half-drawn and then swapped again.
        Assert.True(
            stagedReplacementBody.IndexOf("paintAttemptStarted = true;", StringComparison.Ordinal)
            < stagedReplacementBody.IndexOf("await WaitForDocumentPaintAsync", StringComparison.Ordinal));
        Assert.Contains("if (!paintAttemptStarted)", stagedReplacementBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaPlayFailuresBecomeARealPreviewState()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var toggleBody = ReadMethod(windowSource, "public bool TryToggleMediaPlayback");
        var messageBody = ReadMethod(windowSource, "private void OnMediaMessage");

        // The player reports an engine failure to the host, which owns the
        // failure card; the page never keeps a dead player of its own.
        foreach (var page in new[] { MediaPlayerPage.Page(isAudio: true), MediaPlayerPage.Page(isAudio: false) })
        {
            Assert.Contains("window.glimpseToggleMedia =", page, StringComparison.Ordinal);
            Assert.Contains("media.addEventListener('error', () => post({ error:", page, StringComparison.Ordinal);
        }

        Assert.Contains("window.glimpseToggleMedia", toggleBody, StringComparison.Ordinal);
        Assert.Contains("JsonValueKind.Number", messageBody, StringComparison.Ordinal);
        // The card names the outcome and gives codec-specific help only
        // after the media engine fails and the container confirms it.
        Assert.Contains("Can't play this file", windowSource, StringComparison.Ordinal);
        var failureBody = ReadMethod(windowSource, "private async Task ShowMediaFailureAsync");
        Assert.Contains("ShowMediaFailureAsync(path, detail, reason)", messageBody, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => Mp4CodecSniffer.TryRead(path))", failureBody, StringComparison.Ordinal);
        Assert.Contains("BuildMediaFailurePreview(path, codecs)", failureBody, StringComparison.Ordinal);
        Assert.Contains("MediaCodecStoreAction(codecs)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7", windowSource, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageDecodeFailuresOfferCodecHelpOnlyForHeif()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var previewBody = ReadMethod(windowSource, "private async Task<FrameworkElement> BuildPreviewContentAsync");

        // What the card then says is CardsBehaviorTests' job.
        Assert.Contains("FileSignatures.Detect(contentPath)", windowSource, StringComparison.Ordinal);
        Assert.Contains("signature == FileSignatureKind.Heif", previewBody, StringComparison.Ordinal);
        Assert.Contains("BuildHeifFailurePreview(", previewBody, StringComparison.Ordinal);
        Assert.Contains("FileSignatures.ClassifyHeifFailure(path)", previewBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSkipButtonsUseDirectionalGlyphs()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\MediaPlayerPage.cs");

        // The skip glyphs are true mirrors on a circle CONCENTRIC with the
        // "15" label (center 12,12.45 r 7). The old art fit a lopsided arc
        // whose center sat 1.9 units above the text, which is why the label
        // always looked crammed against the bottom of the ring.
        Assert.Contains("M 7.99 6.72 A 7 7 0 1 1 5.31 10.40", windowSource, StringComparison.Ordinal);
        Assert.Contains("M 16.01 6.72 A 7 7 0 1 0 18.69 10.40", windowSource, StringComparison.Ordinal);
        Assert.Contains("M 4.55 6.6 L 8.6 3.7 L 8.7 9.2 Z", windowSource, StringComparison.Ordinal);
        Assert.Contains("M 19.45 6.6 L 15.4 3.7 L 15.3 9.2 Z", windowSource, StringComparison.Ordinal);
        Assert.Contains("y=\"12.45\" text-anchor=\"middle\" dominant-baseline=\"central\" font-size=\"7.1\"", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("transform=\"translate(0 1.1)\"", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("scale(-1,1) translate(-24,0)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void UserFacingIdentityIsGlimpse()
    {
        var project = ReadSource(@"SpacePreview\SpacePreview.csproj");
        var installer = ReadSource(@"SpacePreview\installer\Glimpse.iss");
        var tray = ReadSource(@"SpacePreview\Services\TrayIcon.cs");
        var log = ReadSource(@"SpacePreview\Services\DiagnosticLog.cs");
        var startup = ReadSource(@"SpacePreview\Services\StartupRegistration.cs");

        Assert.Contains("<AssemblyName>Glimpse</AssemblyName>", project, StringComparison.Ordinal);
        Assert.Contains("<Product>Glimpse</Product>", project, StringComparison.Ordinal);
        Assert.Contains("#define MyAppName \"Glimpse\"", installer, StringComparison.Ordinal);
        Assert.Contains("#define MyAppExeName \"Glimpse.exe\"", installer, StringComparison.Ordinal);
        Assert.Contains("OutputBaseFilename=Glimpse-Setup-{#MyAppVersion}", installer, StringComparison.Ordinal);
        Assert.Contains("szTip = Branding.Name", tray, StringComparison.Ordinal);
        Assert.Contains("Branding.LogFileName", log, StringComparison.Ordinal);
        Assert.Contains("key?.SetValue(Branding.Name", startup, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareManagerUsesTheWinRtProjection()
    {
        var source = ReadSource(@"SpacePreview\Services\ShareService.cs");

        Assert.Contains("MarshalInspectable<DataTransferManager>.FromAbi(manager)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(DataTransferManager)Marshal.GetObjectForIUnknown(manager)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WebRevealAndGeometryKeepTheirOrdering()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var geometryBody = ReadMethod(windowSource, "private void ApplyStageGeometry");

        Assert.DoesNotContain("ShouldHoldSwapGeometry", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetGeometry(", geometryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("CompositionTarget.Rendering", geometryBody, StringComparison.Ordinal);
        Assert.DoesNotContain("SetGeometryNative", windowSource, StringComparison.Ordinal);
        Assert.Contains("TrySetWindowRect", windowSource, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.SetWindowPos", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SheetTabsNavigateWithoutLateOpacityCommands()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var tabBarBody = ReadMethod(windowSource, "private static FrameworkElement BuildSheetTabBar");

        Assert.Contains(
            "webView.Source = new Uri(sheets[index].Path, UriKind.Absolute);",
            tabBarBody,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteScriptAsync", tabBarBody, StringComparison.Ordinal);
        Assert.DoesNotContain("opacity", tabBarBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfThumbnailRailsDoNotDecodeCacheManifestsOnTheUiThread()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var body = ReadMethod(windowSource, "private static async Task<PdfPreviewDocument?> TryRenderPdfThumbnailsOutOfProcessAsync");

        Assert.Contains("Task.Run(() => TryReadPdfThumbnailManifest(cacheDir), token)", body, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() => TryReadPdfThumbnailManifest(tempDir), token)", body, StringComparison.Ordinal);
        Assert.Contains("startPage", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("7", 51, 7)]
    [InlineData(" 12 ", 51, 12)]
    [InlineData("52", 51, 0)]
    [InlineData("page 7", 51, 0)]
    public void PdfPageStateCanBeReadFromAutomationValue(string value, int totalPages, int expected)
    {
        var parsed = PreviewWindow.TryReadPdfAutomationPageValue(value, totalPages, out var page);
        Assert.Equal(expected > 0, parsed);
        Assert.Equal(expected, page);
    }

    private static string ReadSource(string relativePath) => TestSource.Read(relativePath);

    private static string ReadMethod(string source, string methodSignature) =>
        TestSource.MethodFrom(source, methodSignature);
}
