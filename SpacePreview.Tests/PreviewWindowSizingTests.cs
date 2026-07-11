using System.Windows;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The stage takes the content's shape: native pixels for images and video
/// (never inflated past them), page aspect for documents, the floor for
/// audio, and the default stage for everything without an intrinsic shape.
/// A border-drag holds only until the preview closes.
/// </summary>
public sealed class PreviewWindowSizingTests
{
    private static readonly Rect Desktop1080 = new(0, 0, 1920, 1080);
    private static readonly Rect Desktop4K = new(0, 0, 3840, 2160);
    private static readonly Rect Desktop1440 = new(0, 0, 2560, 1440);
    private static readonly Size MinWindow = new(PreviewWindow.StageMinWidth, PreviewWindow.StageMinHeight);
    private const double DocumentToolbarHeight = 40;

    private static Size Fit(StageFitHint hint, Rect workArea, double dpiScale = 1) =>
        StageFit.WindowFor(hint, workArea, dpiScale, documentRailWidth: 132, DocumentToolbarHeight, MinWindow);

    [Fact]
    public void DefaultStageDerivesFromTheWorkArea()
    {
        var size = PreviewWindow.StageSize(new Rect(0, 0, 1600, 1000));

        Assert.Equal(1600 * 0.54, size.Width, precision: 1);
        Assert.Equal(1000 * 0.615, size.Height, precision: 1);
    }

    [Fact]
    public void DefaultStageIsCappedOnLargeMonitors()
    {
        var size = PreviewWindow.StageSize(Desktop4K);

        Assert.Equal(900, size.Width);
        Assert.Equal(675, size.Height);
    }

    [Fact]
    public void DefaultStageNeverDropsBelowTheFunctionalFloor()
    {
        var size = PreviewWindow.StageSize(new Rect(0, 0, 800, 500));

        Assert.Equal(PreviewWindow.StageMinWidth, size.Width);
        Assert.Equal(PreviewWindow.StageMinHeight, size.Height);
    }

    [Fact]
    public void ImagesOpenAtNativePixelsWithChromeAround()
    {
        var size = Fit(StageFitHint.ImagePixels(800, 600), Desktop4K);

        Assert.Equal(800 + StageFit.ChromeWidth, size.Width);
        Assert.Equal(600 + StageFit.ChromeHeight, size.Height);
    }

    [Fact]
    public void PixelContentIsNeverInflatedPastNative()
    {
        // A small image on a huge monitor stays small; the floor is the
        // window minimum, not an upscale.
        var size = Fit(StageFitHint.ImagePixels(200, 150), Desktop4K);

        Assert.Equal(MinWindow, size);
    }

    [Fact]
    public void IntrinsicApexImageSizeOutranksItsTinyCachedThumbnail()
    {
        // Exact field case: the source is a 10000x5334 card, while Explorer
        // supplied only a 256x137 cached first frame. The source dimensions
        // deserve the monitor-bounded image stage; treating the thumbnail as
        // geometry truth collapses the same item to the functional floor.
        var source = Fit(StageFitHint.ImagePixels(10000, 5334), Desktop1440);
        var cachedThumbnail = Fit(StageFitHint.ImagePixels(256, 137), Desktop1440);

        Assert.Equal(Desktop1440.Width * StageFit.ImagePixelBand, source.Width, precision: 1);
        Assert.True(source.Width > MinWindow.Width && source.Height > MinWindow.Height);
        var sourceContent = new Size(
            source.Width - StageFit.ChromeWidth,
            source.Height - StageFit.ChromeHeight);
        Assert.Equal(10000.0 / 5334.0, sourceContent.Width / sourceContent.Height, precision: 2);

        Assert.Equal(MinWindow, cachedThumbnail);
    }

    [Fact]
    public void LargeContentFitsInsideTheBand()
    {
        var size = Fit(StageFitHint.ImagePixels(8000, 6000), Desktop1080);

        Assert.True(size.Width <= Desktop1080.Width * StageFit.ImagePixelBand);
        Assert.True(size.Height <= Desktop1080.Height * StageFit.ImagePixelBand);
        // Aspect survives the fit.
        var content = new Size(size.Width - StageFit.ChromeWidth, size.Height - StageFit.ChromeHeight);
        Assert.Equal(8000.0 / 6000.0, content.Width / content.Height, precision: 2);
    }


    [Fact]
    public void StillImagesUseAQuieterBandThanVideo()
    {
        var image = Fit(StageFitHint.ImagePixels(1866, 1602), Desktop1440);
        var video = Fit(StageFitHint.Pixels(1866, 1602), Desktop1440);

        Assert.True(image.Width < video.Width);
        Assert.True(image.Height < video.Height);
        Assert.Equal(Desktop1440.Height * StageFit.ImagePixelBand, image.Height, precision: 0);
        Assert.Equal(Desktop1440.Height * StageFit.PixelBand, video.Height, precision: 0);
    }

    [Fact]
    public void PortraitVideoStandsVertical()
    {
        // 1080x1920 phone video on a 1080p desktop: height-bound, clearly
        // taller than wide even after the width floor.
        var size = Fit(StageFitHint.Pixels(1080, 1920), Desktop1080);

        Assert.True(size.Height > size.Width);
        Assert.Equal(Desktop1080.Height * StageFit.PixelBand, size.Height, precision: 0);
    }

    [Fact]
    public void DpiScaleKeepsNativePixelsHonest()
    {
        // 2000px at 200% scale is 1000 DIPs of real estate, not 2000.
        var size = Fit(StageFitHint.ImagePixels(2000, 1200), Desktop4K, dpiScale: 2);

        Assert.Equal(1000 + StageFit.ChromeWidth, size.Width);
        Assert.Equal(600 + StageFit.ChromeHeight, size.Height);
    }

    [Fact]
    public void PortraitPagesTakeGenerousHeightAtTheirAspect()
    {
        // US Letter: height-bound, window reads as a page beside its rail.
        var size = Fit(StageFitHint.PageAspect(8.5 / 11), Desktop1080);

        var pageHeight = size.Height - StageFit.ChromeHeight;
        var pageWidth = size.Width - StageFit.ChromeWidth - 132;
        var visiblePageHeight = pageHeight - DocumentToolbarHeight;
        Assert.Equal(Desktop1080.Height * StageFit.Band - StageFit.ChromeHeight, visiblePageHeight, precision: 0);
        Assert.Equal(8.5 / 11, pageWidth / visiblePageHeight, precision: 2);
    }

    [Fact]
    public void LandscapeDecksOpenLandscape()
    {
        var size = Fit(StageFitHint.PageAspect(16.0 / 9.0), Desktop1080);

        Assert.True(size.Width > size.Height);
        Assert.True(size.Width <= Desktop1080.Width * StageFit.Band);
    }

    [Fact]
    public void LandscapePagesUseTheTighterBand()
    {
        // A 16:9 deck at the portrait band reads as accidental-fullscreen;
        // the landscape band leaves visible, deliberate margins.
        var size = Fit(StageFitHint.PageAspect(16.0 / 9.0), Desktop1080);

        Assert.True(size.Width <= Desktop1080.Width * StageFit.LandscapeBand + StageFit.ChromeWidth + 132);
        var pageWidth = size.Width - StageFit.ChromeWidth - 132;
        var pageHeight = size.Height - StageFit.ChromeHeight - DocumentToolbarHeight;
        Assert.Equal(16.0 / 9.0, pageWidth / pageHeight, precision: 2);
    }

    [Fact]
    public void PortraitPagesKeepTheGenerousBand()
    {
        // The tighter band is for landscape only; a letter page still takes
        // its full height.
        var size = Fit(StageFitHint.PageAspect(8.5 / 11), Desktop1080);

        Assert.Equal(
            Desktop1080.Height * StageFit.Band - StageFit.ChromeHeight,
            size.Height - StageFit.ChromeHeight - DocumentToolbarHeight,
            precision: 0);
    }

    [Fact]
    public void RailTruthShapesDocumentsTheProbeCannotParse()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        // Both thin rail loaders share this core, so page truth is applied
        // exactly once before the reveal gate opens.
        var railCore = TestSource.MethodFrom(windowSource, "private async Task LoadPdfPageRailCoreAsync");
        var railHintCalls = railCore.Split("HintStageFromRailTruth(state, firstPages)").Length - 1;
        Assert.Equal(1, railHintCalls);
        Assert.Contains("SetStageFitHint(StageFitHint.PageAspect((double)first.Width / first.Height), state.Version)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedPageAspectsAreClampedToSanePaper()
    {
        var absurd = Fit(StageFitHint.PageAspect(500), Desktop1080);
        var broken = Fit(StageFitHint.PageAspect(double.NaN), Desktop1080);

        Assert.True(absurd.Width <= Desktop1080.Width * StageFit.Band);
        Assert.True(broken.Width >= MinWindow.Width && broken.Height >= MinWindow.Height);
    }

    [Fact]
    public void CompactContentTakesTheFloor()
    {
        var size = Fit(StageFitHint.Compact(), Desktop4K);

        Assert.Equal(MinWindow, size);
    }

    [Fact]
    public void HysteresisOnlyAbsorbsRoundingChurn()
    {
        // Meaningfully different shapes must be honored; the dead zone
        // exists for DPI rounding, nothing a user could ever perceive.
        Assert.True(StageFit.Hysteresis <= 4);
    }

    [Fact]
    public void BorderResizeHoldsForTheItemOnly()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        // The drag commits to in-memory state, never to disk, and dies at
        // the next item: resizing is local intent, not a browsing mode.
        Assert.Contains("_itemStageSize = new Size(Width, Height)", windowSource, StringComparison.Ordinal);
        Assert.Contains("// A dragged size is local intent: it lives exactly as long as the", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StageWidth = width", windowSource, StringComparison.Ordinal);
        Assert.Null(typeof(Settings).GetProperty("StageWidth"));
        Assert.Null(typeof(Settings).GetProperty("StageHeight"));

        // The resize also dragged the window off center; the next item
        // retakes the middle of the work area. A window the user merely
        // MOVED still stays where they put it.
        Assert.Contains("_recenterStage = true;", windowSource, StringComparison.Ordinal);
        Assert.Contains("var recentered = StagePlacement.CenterAndClamp", windowSource, StringComparison.Ordinal);
        Assert.Contains("var left = recenter ? recentered.Left : Left + (Width - target.Width) / 2", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PickerStageCentersOnItsDialogAndStaysOnTheWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1040);
        var dialog = new Rect(360, 180, 1200, 720);

        var placed = StagePlacement.CenterAndClamp(new Size(800, 600), workArea, dialog);

        Assert.Equal(dialog.Left + (dialog.Width - placed.Width) / 2, placed.Left);
        Assert.Equal(dialog.Top + (dialog.Height - placed.Height) / 2, placed.Top);
        Assert.True(workArea.Contains(placed));
    }

    [Fact]
    public void PickerStageClampsWhenItsDialogTouchesAWorkAreaEdge()
    {
        var workArea = new Rect(100, 50, 1000, 700);
        var dialog = new Rect(20, -80, 500, 300);

        var placed = StagePlacement.CenterAndClamp(new Size(700, 500), workArea, dialog);

        Assert.Equal(workArea.Left, placed.Left);
        Assert.Equal(workArea.Top, placed.Top);
        Assert.Equal(700, placed.Width);
        Assert.Equal(500, placed.Height);
    }

    [Fact]
    public void SpreadsheetsEarnTheBroadWorkspace()
    {
        // Sheets convert to HTML, so no page probe can shape them; the
        // hint asks for the workspace directly.
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        Assert.Contains("FileKinds.IsSpreadsheet(contentPath)", windowSource, StringComparison.Ordinal);
        Assert.Contains("StageFitHint.Sheet()", windowSource, StringComparison.Ordinal);

        // The workspace is broad on a laptop, capped on a huge monitor,
        // and always bigger than the generic default stage.
        var workArea = new Rect(0, 0, 2560, 1400);
        var sheet = StageFit.WindowFor(
            StageFitHint.Sheet(), workArea, 1, documentRailWidth: 132, DocumentToolbarHeight, MinWindow);
        Assert.Equal(1200, sheet.Width, 1);
        Assert.Equal(900, sheet.Height, 1);

        var generic = PreviewWindow.StageSize(workArea);
        Assert.True(sheet.Width > generic.Width);
        Assert.True(sheet.Height > generic.Height);

        var laptop = new Rect(0, 0, 1280, 720);
        var laptopSheet = StageFit.WindowFor(
            StageFitHint.Sheet(), laptop, 1, documentRailWidth: 132, DocumentToolbarHeight, MinWindow);
        Assert.Equal(1280 * StageFit.SheetBand, laptopSheet.Width, 1);
    }

    [Fact]
    public void MaximizeHasExactlyOneMechanism()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        // WindowState is the single truth: snap assist, F11, the header
        // button, and video fullscreen all drive it, so the restore button
        // works no matter who maximized, and no private flag can wedge the
        // window fullscreen until relaunch.
        Assert.Contains("private bool IsStageMaximized => WindowState == WindowState.Maximized;", windowSource, StringComparison.Ordinal);
        Assert.Contains("WindowState = IsStageMaximized ? WindowState.Normal : WindowState.Maximized;", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("private bool _isMaximized", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("_restoreRect", windowSource, StringComparison.Ordinal);

        // A maximized borderless window must respect the taskbar.
        Assert.Contains("CapMaximizedBoundsToWorkArea(hwnd, lParam, ref handled)", windowSource, StringComparison.Ordinal);
        Assert.Contains("limits.ptMaxSize.X = info.rcWork.Width;", windowSource, StringComparison.Ordinal);

        // Closing always leaves a normal stage behind, and a native
        // minimize restores at the next Space press.
        Assert.Contains("WindowState = WindowState.Normal;\n        _contentDrivenMaximize = false;", windowSource.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("StateChanged += (_, _) => SyncNativeWindowState()", windowSource, StringComparison.Ordinal);
        Assert.Contains("RestoreFromNativeMinimize();", windowSource, StringComparison.Ordinal);
        Assert.Contains("stage resize ignored after native snap/maximize", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StageFitHintsDieAtReveal()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        // One rule governs lateness: unrevealed items may still shape the
        // stage; after the reveal the stage holds until the next item.
        var setter = windowSource[windowSource.IndexOf("internal void SetStageFitHint", StringComparison.Ordinal)..];
        setter = setter[..setter.IndexOf("private", StringComparison.Ordinal)];
        Assert.Contains("_loadFinished", setter, StringComparison.Ordinal);
        Assert.Contains("version != _loadVersion", setter, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultStageContentRetargetsBeforeSlowCardBuilds()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("UsesDefaultStageWithoutProbe(contentPath)", windowSource, StringComparison.Ordinal);
        Assert.Contains("RetargetDefaultStageDuringBlankBeat(version)", windowSource, StringComparison.Ordinal);
        Assert.Contains("var stageMoved = defaultStageWithoutProbe && !outgoingIsWeb && RetargetStageToFit()", windowSource, StringComparison.Ordinal);
        Assert.Contains("ShowContentSwapScrim(instant: stageMoved)", windowSource, StringComparison.Ordinal);
        Assert.Contains("Directory.Exists(path)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfAndConvertedDocumentsFeedPageShapeHints()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");

        Assert.Contains("PdfPageSizeProbe.TryReadFirstPageSize(contentPath", windowSource, StringComparison.Ordinal);
        Assert.Contains("PdfPageSizeProbe.TryReadFirstPageSize(initialPath", windowSource, StringComparison.Ordinal);
        Assert.Contains("OfficePageSizeProbe.TryReadFirstPageAspect(contentPath", windowSource, StringComparison.Ordinal);
        Assert.Contains("Mp4CodecSniffer.TryRead(contentPath)", windowSource, StringComparison.Ordinal);
        Assert.Contains("StageFitHint.PageAspect(pageWidth / pageHeight)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void DecodedImageHintCannotOverwriteIntrinsicHeaderTruth()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var methodStart = windowSource.IndexOf(
            "private async Task<FrameworkElement> BuildPreviewContentAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = windowSource.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = windowSource[methodStart..methodEnd];

        // A local image was already probed from its exact source header before
        // decoding. The decoded frame may be only Explorer's tiny cache entry,
        // so it supplies a fallback hint only when no hint exists for this load.
        const string decodedHint = "StageFitHint.ImagePixels(session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight)";
        Assert.Equal(1, method.Split(decodedHint, StringSplitOptions.None).Length - 1);
        Assert.Matches(
            @"if\s*\(_stageFitVersion\s*!=\s*version\)\s*\{\s*SetStageFitHint\(\s*StageFitHint\.ImagePixels\(\s*session\.CurrentBitmap\.PixelWidth,\s*session\.CurrentBitmap\.PixelHeight\),\s*version\);\s*\}",
            method);
    }

    [Fact]
    public void DecodedPsdCompositeHintCannotOverwriteIntrinsicCanvasTruth()
    {
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var methodStart = windowSource.IndexOf(
            "private async Task<FrameworkElement> BuildPreviewContentAsync(",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0);
        var methodEnd = windowSource.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart);
        var method = windowSource[methodStart..methodEnd];

        // The PSD header already supplied the exact canvas dimensions. Its
        // decoded composite is intentionally capped at 4096px, so it may only
        // supply fallback geometry when the source probe produced no hint.
        const string compositeHint = "StageFitHint.ImagePixels(composite.PixelWidth, composite.PixelHeight)";
        Assert.Equal(1, method.Split(compositeHint, StringSplitOptions.None).Length - 1);
        Assert.Matches(
            @"if\s*\(_stageFitVersion\s*!=\s*version\)\s*\{\s*SetStageFitHint\(\s*StageFitHint\.ImagePixels\(\s*composite\.PixelWidth,\s*composite\.PixelHeight\),\s*version\);\s*\}",
            method);
    }
}
