using SpacePreview.Preview;

using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePreview.Tests;

public sealed class PreviewChromeRegressionTests
{
    [Fact]
    public void FolderRowHoverReusesTheFolderArtworkSlot()
    {
        var hoverSource = ReadSource(@"SpacePreview\Preview\FolderHoverThumbnail.cs");
        var thumbnailSource = ReadSource(@"SpacePreview\Preview\ShellThumbnailProvider.cs");
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.DoesNotContain("Popup", hoverSource, StringComparison.Ordinal);
        Assert.DoesNotContain("PlacementTarget", hoverSource, StringComparison.Ordinal);
        Assert.Contains("new FolderHoverThumbnail((isDirectory, shellImage) =>", windowSource, StringComparison.Ordinal);
        Assert.Contains("BuildHoverArtwork(folderArtworkData, hoveredArtworkIsDirectory, hoveredArtwork)", windowSource, StringComparison.Ordinal);
        Assert.Contains("if (isDirectory)", windowSource, StringComparison.Ordinal);
        Assert.Contains("MatchFolderArtworkBounds(shellImage.Bitmap, restingFolder)", windowSource, StringComparison.Ordinal);
        Assert.Contains("if (shellImage.IsThumbnail)", windowSource, StringComparison.Ordinal);
        Assert.Contains("card.Loaded += (_, _) => _folderHover = hover", windowSource, StringComparison.Ordinal);
        Assert.Contains("FolderArtworkSlotWidth", windowSource, StringComparison.Ordinal);
        Assert.Contains("BuildFolderArtwork", windowSource, StringComparison.Ordinal);
        Assert.Contains("visualHost.Content", windowSource, StringComparison.Ordinal);
        Assert.Contains("}, (int)FolderArtworkSlotWidth);", windowSource, StringComparison.Ordinal);
        Assert.Contains("FolderHoverThumbnail(Action<bool, ShellImage?> show, int folderIconPixels)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("if (!isDirectory)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("ShellThumbnailProvider.TryGetThumbnail(path, ThumbPixels)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("ShellThumbnailProvider.NormalizeFolderImage(", hoverSource, StringComparison.Ordinal);
        Assert.Contains("ShellThumbnailProvider.TryGetThumbnail(path, ThumbPixels, _folderIconPixels)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("TryGetIcon(path, fallbackIconPixels)", thumbnailSource, StringComparison.Ordinal);
        Assert.Contains("BuildFileCardVisual(data, FolderArtworkSlotWidth, FolderArtworkSlotHeight, 184, 184)", windowSource, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(140)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(80)", hoverSource, StringComparison.Ordinal);
        Assert.Contains("_hideDelay.Stop();", ReadMethod(hoverSource, "public void OnRowEnter"), StringComparison.Ordinal);
        Assert.Contains("_hideDelay.Start();", ReadMethod(hoverSource, "public void OnRowLeave"), StringComparison.Ordinal);
        Assert.Contains("private const int WarmAhead = 2;", hoverSource, StringComparison.Ordinal);
        Assert.Contains("private readonly Queue<string> _warmQueue", hoverSource, StringComparison.Ordinal);
        Assert.Contains("if (_hoverReady && _pendingPath is { } hovered", hoverSource, StringComparison.Ordinal);
        Assert.Contains("_folderHover?.SetWarmPaths(children", windowSource, StringComparison.Ordinal);
        Assert.Contains("_warmQueue.Clear();", ReadMethod(hoverSource, "public void Reset"), StringComparison.Ordinal);
        Assert.Contains("_warmQueued.Clear();", ReadMethod(hoverSource, "public void Reset"), StringComparison.Ordinal);
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
    public void FileCardsUseStableLayoutLanes()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var body = ReadMethod(windowSource, "private static FrameworkElement BuildFileCard");

        Assert.Contains("FileCardVisualSlotHeight", windowSource, StringComparison.Ordinal);
        Assert.Contains("FileCardTitleSlotHeight", windowSource, StringComparison.Ordinal);
        Assert.Contains("FileCardInfoSlotHeight", windowSource, StringComparison.Ordinal);
        Assert.Contains("FileCardFooterSlotHeight", windowSource, StringComparison.Ordinal);
        Assert.Contains("private const double FileCardIconSide = 148;", windowSource, StringComparison.Ordinal);
        Assert.Contains("BuildFileCardVisual(data, FolderArtworkSlotWidth, FolderArtworkSlotHeight, 184, 184)", windowSource, StringComparison.Ordinal);
        Assert.Contains("new Grid", body, StringComparison.Ordinal);
        Assert.Contains("card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardVisualSlotHeight) })", body, StringComparison.Ordinal);
        Assert.Contains("card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardInfoSlotHeight) })", body, StringComparison.Ordinal);
        Assert.Contains("BuildFileCardVisual(data, FileCardVisualSlotWidth, FileCardVisualSlotHeight, FileCardIconSide, FileCardIconSide)", body, StringComparison.Ordinal);
        Assert.Contains("footer is null ? 0 : FileCardFooterSlotHeight", body, StringComparison.Ordinal);
        Assert.Contains("surface.VerticalContentAlignment = VerticalAlignment.Center", body, StringComparison.Ordinal);
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
        Assert.Contains("private const double EpubChapterRailWidth = 168;", windowSource, StringComparison.Ordinal);
        Assert.Contains("getElementById('ch-{spineIndex}')?.scrollIntoView", windowSource, StringComparison.Ordinal);
        Assert.Contains("TrackEpubRailLifetime(grid, railState);", windowSource, StringComparison.Ordinal);

        // Rail resizes reuse the shared viewer cloak so hide/show never flashes.
        Assert.Contains("CloakViewerForRailResize(state.WebView, state.Version);", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.RailVerticalOffset", renderBody, StringComparison.Ordinal);
        Assert.Contains("BuildRailSurface", railBody, StringComparison.Ordinal);
        Assert.Contains("scroll.ScrollToVerticalOffset(verticalOffset)", railSurfaceBody, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(scroll, 1)", railSurfaceBody, StringComparison.Ordinal);

        // Anchor landings keep a breath of space above the chapter.
        Assert.Contains("scroll-margin-top", themeSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfDocumentsUseOwnedPageRailInsteadOfBrokenNativeSidebar()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var appSource = ReadSource(@"SpacePreview\App.xaml.cs");
        var upgradeBody = ReadMethod(windowSource, "private void UpgradeConvertedOfficePreview");
        var stagedJumpBody = ReadMethod(windowSource, "private void StagePdfViewerJump");
        var stagedReplacementBody = ReadMethod(windowSource, "private void StagePdfViewerReplacement");
        var inProcessRailBody = ReadMethod(windowSource, "private Task LoadPdfPageRailAsync");
        var officeRailBody = ReadMethod(windowSource, "private Task LoadPdfPageRailOutOfProcessAsync");

        // The native toolbar keeps the zoom pair, page box, search, and print:
        // a fixed set that fits at every stage width. Hiding Search collapses
        // the ENTIRE strip (page box included), which kills the instant native
        // jump - it must stay visible.
        Assert.Contains("CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs", windowSource, StringComparison.Ordinal);
        Assert.Contains("CoreWebView2PdfToolbarItems.FullScreen | CoreWebView2PdfToolbarItems.MoreSettings", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2PdfToolbarItems.Print", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2PdfToolbarItems.PageSelector", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2PdfToolbarItems.Search", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CoreWebView2PdfToolbarItems.ZoomIn", windowSource, StringComparison.Ordinal);

        // The collapsed rail must reclaim document space. It keeps only a
        // small handle, and the native viewer is briefly cloaked while its
        // internal toolbar/scrollbar settles.
        Assert.Contains("state.RailColumn.Width = new GridLength(targetWidth)", windowSource, StringComparison.Ordinal);
        Assert.Contains("var targetWidth = state.IsVisible ? PdfPageRailWidth : PdfPageRailCollapsedWidth", windowSource, StringComparison.Ordinal);
        Assert.Contains("CloakViewerForRailResize(state.WebView, state.Version)", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyPdfRailWidthUnderHeldFrameAsync", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png", windowSource, StringComparison.Ordinal);
        Assert.Contains("PdfPageRailInitialPages = 10", windowSource, StringComparison.Ordinal);
        Assert.Contains("LoadPdfPageRailOutOfProcessAsync(state)", inProcessRailBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPreviewRenderer.TryRenderThumbnails", inProcessRailBody, StringComparison.Ordinal);
        Assert.Contains("TryRenderPdfThumbnailsOutOfProcessAsync", officeRailBody, StringComparison.Ordinal);
        Assert.Contains("AnimateRail(state.Host, targetWidth, content)", windowSource, StringComparison.Ordinal);
        Assert.Contains("BuildCollapsedRail(document.TotalPages, \"Show pages\"", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.TryHandleJump?.Invoke(state, page) == true", windowSource, StringComparison.Ordinal);
        Assert.Contains("JumpPdfToPage(state, page)", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.RailVerticalOffset", windowSource, StringComparison.Ordinal);
        Assert.Contains("scroll.ScrollToVerticalOffset(verticalOffset)", windowSource, StringComparison.Ordinal);
        Assert.Contains("Grid.SetRow(scroll, 1)", windowSource, StringComparison.Ordinal);
        Assert.Contains("host.Width = targetWidth;", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("new DoubleAnimation(current, targetWidth", windowSource, StringComparison.Ordinal);

        // Initial document reveal waits for the first rail decision, so the
        // user does not see the PDF surface appear and then slide sideways
        // when thumbnails arrive.
        Assert.Contains("FirstRailReady", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("WebRevealSettleDelay", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationCompleted += (_, _) => RunAfter(TimeSpan.FromMilliseconds(220)", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationCompleted += (_, _) => RunAfter(TimeSpan.FromMilliseconds(240)", windowSource, StringComparison.Ordinal);
        Assert.Contains("StagePdfViewerReplacement(", upgradeBody, StringComparison.Ordinal);
        Assert.Contains("StagePdfViewerReplacement(", stagedJumpBody, StringComparison.Ordinal);
        Assert.Contains("var paintAttemptStarted = false;", stagedReplacementBody, StringComparison.Ordinal);
        Assert.Contains("paintAttemptStarted = true;", stagedReplacementBody, StringComparison.Ordinal);
        Assert.Contains("if (!paintAttemptStarted)", stagedReplacementBody, StringComparison.Ordinal);
        Assert.Contains("DiscardFresh();", stagedReplacementBody, StringComparison.Ordinal);
        Assert.Contains("await WaitForDocumentPaintAsync", stagedReplacementBody, StringComparison.Ordinal);
        Assert.True(
            stagedReplacementBody.IndexOf("paintAttemptStarted = true;", StringComparison.Ordinal)
            < stagedReplacementBody.IndexOf("await WaitForDocumentPaintAsync", StringComparison.Ordinal));

        // The Chromium PDF viewer honors #page= only at document load, so a
        // jump must be a REAL load: cache-busting query, never a runtime
        // hash update (which the viewer silently ignores).
        Assert.DoesNotContain("location.hash", windowSource, StringComparison.Ordinal);
        Assert.Contains("?jump={target}#page={target}&view=FitH", windowSource, StringComparison.Ordinal);

        // Rail clicks use the PDF viewer's own page box path: click the
        // native field, select its value, write the target, and press Enter.
        // That jumps without a reload. The staged viewer remains fallback
        // only.
        Assert.Contains("JumpPdfWithNativePageBoxAsync(state.WebView, core, target)", windowSource, StringComparison.Ordinal);
        Assert.Contains("DispatchMouseClickAsync(core, x, 20)", windowSource, StringComparison.Ordinal);
        Assert.Contains("DispatchModifiedKeyAsync(core, 65, \"KeyA\", \"a\", modifiers: 2)", windowSource, StringComparison.Ordinal);
        Assert.Contains("\"Input.insertText\"", windowSource, StringComparison.Ordinal);
        Assert.Contains("StartPdfPageSync(railState)", windowSource, StringComparison.Ordinal);
        Assert.Contains("SyncPdfPageFromViewer", windowSource, StringComparison.Ordinal);
        Assert.Contains("TryReadPdfPageFromAutomation", windowSource, StringComparison.Ordinal);
        var syncStart = windowSource.IndexOf(
            "private async void SyncPdfPageFromViewer",
            StringComparison.Ordinal);
        Assert.True(syncStart >= 0);
        var syncEnd = windowSource.IndexOf(
            "private void SetPdfCurrentPageFromViewer",
            syncStart,
            StringComparison.Ordinal);
        Assert.True(syncEnd > syncStart);
        var pageSync = windowSource[syncStart..syncEnd];
        Assert.Contains("var probeTask = Task.Run(() =>", pageSync, StringComparison.Ordinal);
        Assert.Contains("TryReadPdfPageFromAutomation(hwnd, totalPages, cachedElement)", pageSync, StringComparison.Ordinal);
        Assert.Contains("Task.WhenAny(probeTask, timeoutTask)", pageSync, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomationElement.FromHandle", pageSync, StringComparison.Ordinal);
        Assert.Contains("PdfPageAutomationTimeout", windowSource, StringComparison.Ordinal);
        Assert.Contains("PageSyncGeneration", windowSource, StringComparison.Ordinal);
        Assert.Contains("PageSyncDisabled", windowSource, StringComparison.Ordinal);
        Assert.Contains("AutomationElement.FromHandle", windowSource, StringComparison.Ordinal);
        Assert.Contains("ValuePattern.Pattern", windowSource, StringComparison.Ordinal);
        Assert.Contains("TryReadPdfPageFromSource", windowSource, StringComparison.Ordinal);
        Assert.Contains("TryReadPdfAutomationPageValue", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetPdfCurrentPageFromViewer", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetPdfCurrentPageFromViewer(state, page, logChange: false)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(state.SelectedPageTile, currentTile)", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.JumpInFlight", windowSource, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(650)", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.PageTiles[page]", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.PageRailScrollViewer = scroll", windowSource, StringComparison.Ordinal);
        Assert.Contains("BringPdfPageTileIntoView(state, tile)", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.RailNeedsSelectedPageScroll", windowSource, StringComparison.Ordinal);
        Assert.Contains("pageToRevealOnLoad", windowSource, StringComparison.Ordinal);
        Assert.Contains("alignLoadedSelection(tile)", windowSource, StringComparison.Ordinal);
        Assert.Contains("var topBand = scroll.ViewportHeight * 0.25", windowSource, StringComparison.Ordinal);
        Assert.Contains("var targetTop = scroll.ViewportHeight / 3", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Border? selected = null", windowSource, StringComparison.Ordinal);
        Assert.Contains("SetEpubCurrentChapter(state, spineIndex)", windowSource, StringComparison.Ordinal);
        Assert.Contains("state.ChapterTiles[index] = tile", windowSource, StringComparison.Ordinal);
        // The fallback stages a fresh viewer and swaps; it never scripts the
        // PDF page or reloads in place. Converted Office documents get the
        // same rail, but their thumbnails render in a helper process so
        // pdfium cannot take the resident process down.
        Assert.Contains("trackAsPreviewDisposable: false", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddScriptToExecuteOnDocumentCreatedAsync", windowSource, StringComparison.Ordinal);
        Assert.Contains("LoadPdfPageRailOutOfProcessAsync(railState)", windowSource, StringComparison.Ordinal);
        Assert.Contains("--render-pdf-thumbnails", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaPlayFailuresBecomeARealPreviewState()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var toggleBody = ReadMethod(windowSource, "public bool TryToggleMediaPlayback");
        var messageBody = ReadMethod(windowSource, "private void OnMediaMessage");
        var mediaHtmlBody = ReadMethod(windowSource, "private static Uri WriteMediaHtml");

        Assert.Contains("window.glimpseToggleMedia", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("AbortError", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("NotAllowedError", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("document.body.classList.add('blocked')", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("window.chrome.webview.postMessage({ error: media.error ? media.error.code : 'play', reason })", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("media.addEventListener('error', () => reportPlayFailure", mediaHtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("media.play().catch(() => {})", mediaHtmlBody, StringComparison.Ordinal);
        Assert.Contains("window.glimpseToggleMedia", toggleBody, StringComparison.Ordinal);
        Assert.Contains("JsonValueKind.Number", messageBody, StringComparison.Ordinal);
        // The card names the outcome and gives codec-specific help only
        // after the media engine fails and the container confirms it.
        Assert.Contains("Glimpse can't play this file", windowSource, StringComparison.Ordinal);
        Assert.Contains("Mp4CodecSniffer.TryRead(path)", messageBody, StringComparison.Ordinal);
        Assert.Contains("BuildMediaFailurePreview(path, codecs)", messageBody, StringComparison.Ordinal);
        Assert.Contains("MediaCodecStoreAction(codecs)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7", windowSource, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V", windowSource, StringComparison.Ordinal);
        Assert.Contains("UseShellExecute = true", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageDecodeFailuresOfferCodecHelpOnlyForHeif()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var previewBody = ReadMethod(windowSource, "private async Task<FrameworkElement> BuildPreviewContentAsync");

        Assert.Contains("FileSignatures.Detect(contentPath)", windowSource, StringComparison.Ordinal);
        Assert.Contains("signature == FileSignatureKind.Heif", previewBody, StringComparison.Ordinal);
        Assert.Contains("BuildHeifFailurePreview(path)", previewBody, StringComparison.Ordinal);
        Assert.Contains("heif brand=", previewBody, StringComparison.Ordinal);
        Assert.Contains("HasHevcCodedHeifBrand(path)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7", windowSource, StringComparison.Ordinal);
        Assert.Contains("ms-windows-store://pdp/?ProductId=9PMMSR1CGPWG", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaSkipButtonsUseDirectionalGlyphs()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("|v20", windowSource, StringComparison.Ordinal);

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
    public void ShareLivesInTheOpenMenuNotTheHeaderRow()
    {
        var xaml = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml");
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.DoesNotContain("ShareButton", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ShareButton_Click", windowSource, StringComparison.Ordinal);
        Assert.Contains("(\"Share\", () => SharePath(sharePath))", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void ShareManagerUsesTheWinRtProjection()
    {
        var source = ReadSource(@"SpacePreview\Services\ShareService.cs");

        Assert.Contains("MarshalInspectable<DataTransferManager>.FromAbi(manager)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("(DataTransferManager)Marshal.GetObjectForIUnknown(manager)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void HeaderMotionDoesNotReflowDuringGeometryChanges()
    {
        var xaml = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml");
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var geometryBody = ReadMethod(windowSource, "private void ApplyStageGeometry");

        Assert.DoesNotContain("Width=\"216\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContentToolsSlot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"104\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ZoomControls\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContentActions\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Visibility=\"Hidden\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Width=\"60\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RotateLeftButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RotateRightButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"{TemplateBinding FontFamily}\"", xaml, StringComparison.Ordinal);
        // One Fluent rotate glyph, mirrored for the opposite direction, so
        // the pair stays inside the header's icon family.
        Assert.Contains("Content=\"&#xE7AD;\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MirroredPillIconButton", xaml, StringComparison.Ordinal);
        Assert.Contains("ScaleTransform ScaleX=\"-1\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MeasureOpenPillWidth", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ShouldHoldSwapGeometry", windowSource, StringComparison.Ordinal);
        Assert.Contains("UpdateContentToolsSlotVisibility", windowSource, StringComparison.Ordinal);
        Assert.Contains("ContentToolsSlot.Visibility", windowSource, StringComparison.Ordinal);
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
    public void BrowsingSkipsShellItemsThatDoNotResolveToFiles()
    {
        var source = ReadSource(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var body = ReadMethod(source, "public static ExplorerSelection? TryBrowseRelative");
        var shellInterop = ReadSource(@"SpacePreview\Native\ShellComInterop.cs");

        Assert.Contains("var direction = Math.Sign(delta);", body, StringComparison.Ordinal);
        Assert.Contains("while (direction != 0)", body, StringComparison.Ordinal);
        Assert.Contains("FolderViewNavigator.TrySelectRelative(entry, direction)", body, StringComparison.Ordinal);
        Assert.Contains("selection = TryReadSelection(entry, targetHwnd);", body, StringComparison.Ordinal);
        Assert.Contains("skipped non-previewable shell item", body, StringComparison.Ordinal);
        Assert.Contains("TryReadSelectionFromView", source, StringComparison.Ordinal);
        Assert.Contains("FolderViewNavigator.TryGetSelectedPaths", source, StringComparison.Ordinal);
        Assert.Contains("RestoreStart();", body, StringComparison.Ordinal);
        Assert.Contains("public static bool TrySelectIndex", shellInterop, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedWindowsDetachExplorerBrowseState()
    {
        var controllerSource = ReadSource(@"SpacePreview\Services\SpacePreviewController.cs");
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var pinBody = ReadMethod(controllerSource, "private void OnPinToggleRequested");

        Assert.Contains("internal void DetachBrowseContext(bool restoreSlideshowSize = true)", windowSource, StringComparison.Ordinal);
        Assert.Contains("window.DetachBrowseContext();", pinBody, StringComparison.Ordinal);
        Assert.Contains("_lastExplorerHwnd = IntPtr.Zero;", pinBody, StringComparison.Ordinal);
        Assert.Contains("_multiSelection = null;", pinBody, StringComparison.Ordinal);
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

    [Fact]
    public void PdfRailRangeRenderingFallsBackToFullRender()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var coreBody = ReadMethod(windowSource, "private async Task LoadPdfPageRailCoreAsync");
        var directPdfBody = ReadMethod(windowSource, "private Task LoadPdfPageRailAsync");
        var outOfProcessBody = ReadMethod(windowSource, "private Task LoadPdfPageRailOutOfProcessAsync");

        Assert.Contains("var firstPages = await renderRange(PdfPageRailInitialPages, 0)", coreBody, StringComparison.Ordinal);
        Assert.Contains("PdfPageRailMaxPages - firstPages.Pages.Count", coreBody, StringComparison.Ordinal);
        Assert.Contains("var allPages = await renderFallback()", coreBody, StringComparison.Ordinal);
        Assert.Contains("CombinePdfPreviewDocuments(firstPages, remainingPages)", coreBody, StringComparison.Ordinal);
        Assert.Contains("state.Version != _loadVersion", coreBody, StringComparison.Ordinal);
        Assert.Contains("state.Token.IsCancellationRequested", coreBody, StringComparison.Ordinal);
        Assert.Contains("LoadPdfPageRailOutOfProcessAsync(state)", directPdfBody, StringComparison.Ordinal);
        Assert.Contains("LoadPdfPageRailCoreAsync", outOfProcessBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PdfPreviewRenderer.TryRenderThumbnails", directPdfBody, StringComparison.Ordinal);
        Assert.Contains("TryRenderPdfThumbnailsOutOfProcessAsync", outOfProcessBody, StringComparison.Ordinal);
        Assert.Contains("PdfPageRailMaxPages", outOfProcessBody, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfPageStateCanBeReadFromViewerSource()
    {
        Assert.True(PreviewWindow.TryReadPdfPageFromSource("file:///C:/docs/sample.pdf#page=7&view=FitH", out var page));
        Assert.Equal(7, page);
        Assert.False(PreviewWindow.TryReadPdfPageFromSource("file:///C:/docs/sample.pdf#view=FitH", out _));
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

    [Fact]
    public void EnterYieldsOnceToNativePdfInputs()
    {
        var windowSource = ReadSource(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var controllerSource = ReadSource(@"SpacePreview\Services\SpacePreviewController.cs");
        var enterBody = ReadMethod(controllerSource, "private bool OnKeyDown");

        Assert.Contains("public bool ConsumeEnterYieldToContent()", windowSource, StringComparison.Ordinal);
        Assert.Contains("ContentOwnsKeyboard()", windowSource, StringComparison.Ordinal);
        Assert.Contains("IsPdfViewerSource(source)", windowSource, StringComparison.Ordinal);
        Assert.Contains("_lastContentClickInViewerChrome = false;", windowSource, StringComparison.Ordinal);
        Assert.Contains("ConsumeEnterYieldToContent()", enterBody, StringComparison.Ordinal);
        Assert.Contains("return false;", enterBody, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficeNeighborPreconversionUsesTheExistingConversionLane()
    {
        var controllerSource = ReadSource(@"SpacePreview\Services\SpacePreviewController.cs");
        var converterSource = ReadSource(@"SpacePreview\Preview\OfficeDocumentPdfConverter.cs");
        var shellSource = ReadSource(@"SpacePreview\Native\ShellComInterop.cs");

        Assert.Contains("CancelOfficeNeighborPreconversion();", controllerSource, StringComparison.Ordinal);
        Assert.Contains("ContentReady +=", controllerSource, StringComparison.Ordinal);
        Assert.Contains("ExplorerSelectionService.TryGetNeighborPaths", controllerSource, StringComparison.Ordinal);
        Assert.Contains("OfficeDocumentPdfConverter.TryConvert(path, token, background: true)", controllerSource, StringComparison.Ordinal);
        Assert.Contains("ConversionLane.Wait(cancellationToken)", converterSource, StringComparison.Ordinal);
        Assert.Contains("ProcessPriorityClass.BelowNormal", converterSource, StringComparison.Ordinal);
        Assert.Contains("TryGetNeighborPaths(object window, int currentIndex, int maxNeighbors)", shellSource, StringComparison.Ordinal);
    }

    private static string ReadSource(string relativePath) => TestSource.Read(relativePath);

    private static string ReadMethod(string source, string methodSignature) =>
        TestSource.MethodFrom(source, methodSignature);
}
