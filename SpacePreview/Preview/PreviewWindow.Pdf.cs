using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    // How long the dispatcher may wait on the page-index probe, which reads
    // in about a millisecond once warm. A browse swap is on screen, so it
    // waits only a few milliseconds and a later hint lands in its blank
    // beat. A fresh open has nothing on screen yet: waiting for the page's
    // shape beats opening at the default size and jumping.
    private static readonly TimeSpan PdfProbeWait = TimeSpan.FromMilliseconds(8);
    private static readonly TimeSpan PdfProbeFirstFrameWait = TimeSpan.FromMilliseconds(60);

    // A page in the rail: the sheet is this wide, its height follows the page.
    private const double PdfRailSheetWidth = 92;

    internal enum PdfNavigationAction
    {
        Allow,
        Cancel,
        OpenExternally
    }

    internal static PdfNavigationAction DecidePdfNavigation(string sourcePath, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return PdfNavigationAction.Cancel;
        }

        if (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return PdfNavigationAction.OpenExternally;
        }

        if (!parsed.IsFile || !IsLocalFileUri(parsed))
        {
            return PdfNavigationAction.Cancel;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(LocalPathFromFileUri(parsed)),
                StringComparison.OrdinalIgnoreCase)
                ? PdfNavigationAction.Allow
                : PdfNavigationAction.Cancel;
        }
        catch
        {
            return PdfNavigationAction.Cancel;
        }
    }

    // FitH is what keeps the page filling the stage when the window resizes:
    // without it the viewer holds whatever zoom it opened at, so maximizing
    // just adds empty canvas around an unchanged page.
    private static Uri PdfViewerSource(string path, int page = 1) =>
        new(new Uri(path, UriKind.Absolute).AbsoluteUri + $"#page={Math.Max(1, page)}&view=FitH");

    private static void ConfigurePdfViewer(CoreWebView2 core, string sourcePath)
    {
        try
        {
            // The toolbar keeps the zoom pair, the page box, search, and
            // print - a fixed set that fits at every stage width, so the
            // responsive overflow never rearranges it and nothing appears or
            // disappears when the rail resizes the viewer. Hiding more is not
            // an option: removing Search collapses the entire strip, page box
            // included, and the page box is the one native affordance our
            // rail drives for instant jumps.
            //
            // This exact set is load-bearing. JumpPdfWithNativePageBoxAsync
            // clicks the page box at a measured offset from the centre of the
            // viewer, so ANY change here moves the box out from under that
            // click, and the Ctrl+A that follows lands on the document and
            // selects the whole thing instead. Adding Fit did exactly that.
            core.Settings.HiddenPdfToolbarItems |=
                CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs |
                CoreWebView2PdfToolbarItems.Rotate |
                CoreWebView2PdfToolbarItems.FitPage | CoreWebView2PdfToolbarItems.PageLayout |
                CoreWebView2PdfToolbarItems.Bookmarks |
                CoreWebView2PdfToolbarItems.FullScreen | CoreWebView2PdfToolbarItems.MoreSettings;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf toolbar settings unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        // The PDF viewer owns same-file fragments and query changes: its
        // sidebar, page box, and thumbnails all jump by navigating the loaded
        // document to a new in-document target. Other files stay out.
        core.NavigationStarting += (_, args) =>
        {
            switch (DecidePdfNavigation(sourcePath, args.Uri))
            {
                case PdfNavigationAction.Allow:
                    break;
                case PdfNavigationAction.OpenExternally:
                    args.Cancel = true;
                    OpenExternalUri(args.Uri);
                    break;
                default:
                    args.Cancel = true;
                    break;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (DecidePdfNavigation(sourcePath, args.Uri) == PdfNavigationAction.OpenExternally)
            {
                OpenExternalUri(args.Uri);
            }
        };
    }

    private FrameworkElement BuildPdfWebPreview(string path, long version, CancellationToken token)
    {
        var grid = new Grid().Themed(Panel.BackgroundProperty, "Theme.CanvasBg");

        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // FitH = fit the page to the window's width, the way PDF apps open
        // documents. The viewer's default fits the whole page instead, which
        // renders portrait documents at ~two-thirds size.
        var webView = CreateWebView(
            WebSurfaceKind.PdfViewer,
            () => PdfViewerSource(path),
            $"pdf preview {path}",
            _ =>
            {
                if (version == _loadVersion)
                {
                    SetPreviewContent(BuildPdfErrorPreview(path), null);
                }
            },
            configure: core => ConfigurePdfViewer(core, path));
        Grid.SetColumn(webView, 1);
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);
        grid.Children.Add(webView);
        var railState = new PdfPageRailState(railHost, railColumn, webView, path, version, token) { Surface = grid };
        grid.Tag = railState;
        TrackPdfRailLifetime(grid, railState);
        _ = LoadPdfPageRailOutOfProcessAsync(railState);
        return grid;
    }

    /// <summary>
    /// What a document the viewer cannot show gets. pdfium never renders a
    /// user's document in this process: it can die in native code, and here
    /// it would take every later preview with it.
    /// </summary>
    private static FrameworkElement BuildPdfErrorPreview(string path)
    {
        var kind = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "file";
        var extension = Path.GetExtension(path);
        var app = string.IsNullOrEmpty(extension) ? null : NativeMethods.TryGetFriendlyAppName(extension);
        var next = string.IsNullOrWhiteSpace(app) || app.Equals("Pick an app", StringComparison.OrdinalIgnoreCase)
            ? "Open it in another app instead."
            : $"Open it with {app} instead.";
        return BuildErrorPreview($"{Branding.Name} can't preview this {kind}", next);
    }

    /// <summary>
    /// The stage takes page one's shape before anything renders. A probe
    /// that misses its wait hints when it lands, and a hint still retakes
    /// the stage while the document is unseen. The rail cache answers for
    /// documents the probe cannot read.
    /// </summary>
    private bool TryHintPdfStage(string pdfPath, long version)
    {
        var probe = PdfPageSizeProbe.ReadAsync(pdfPath);
        if (probe.Wait(IsVisible ? PdfProbeWait : PdfProbeFirstFrameWait))
        {
            return HintPdfStage(pdfPath, probe.Result, version);
        }

        _ = probe.ContinueWith(
            done => Dispatcher.BeginInvoke(new Action(() => HintPdfStage(pdfPath, done.Result, version))),
            TaskScheduler.Default);
        return true;
    }

    private bool HintPdfStage(string pdfPath, PdfOutline? probed, long version)
    {
        if ((probed is { WidthPoints: > 0, HeightPoints: > 0 } ? probed : CachedPdfOutline(pdfPath))
            is not { WidthPoints: > 0, HeightPoints: > 0 } page)
        {
            return false;
        }

        SetStageFitHint(StageFitHint.PageAspect(page.WidthPoints / page.HeightPoints, singlePage: page.PageCount == 1), version);
        return true;
    }

    /// <summary>
    /// The page count and page one's shape when they are already known: the
    /// probe the stage fit started, or the rail cache of an earlier open,
    /// which also covers documents the probe cannot read.
    /// </summary>
    private static PdfOutline? KnownPdfOutline(string path)
    {
        var probe = PdfPageSizeProbe.ReadAsync(path);
        return probe.Wait(PdfProbeWait) && probe.Result is { PageCount: > 0 } outline
            ? outline
            : CachedPdfOutline(path);
    }

    /// <summary>The first rail range's manifest: a few hundred bytes of JSON, no images.</summary>
    private static PdfOutline? CachedPdfOutline(string path)
    {
        try
        {
            var manifestPath = Path.Combine(PdfRailCacheDirectory(path, PdfPageRailInitialPages, 0), "manifest.json");
            if (!File.Exists(manifestPath)
                || JsonSerializer.Deserialize<PdfThumbnailManifest>(File.ReadAllText(manifestPath)) is not { TotalPages: > 0 } manifest)
            {
                return null;
            }

            return manifest.Pages.Count > 0
                ? new PdfOutline(manifest.Pages[0].Width, manifest.Pages[0].Height, manifest.TotalPages)
                : new PdfOutline(0, 0, manifest.TotalPages);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The probe hook must bind at reveal, not at build: SetPreviewContent
    /// clears the OLD content after the new one is built, so a build-time
    /// assignment gets nulled immediately (the folder-hover lesson).
    /// </summary>
    private void TrackPdfRailLifetime(Grid grid, PdfPageRailState railState)
    {
        grid.Loaded += (_, _) =>
        {
            _activePdfRail = railState;
            StartPdfPageSync(railState);
        };
        grid.Unloaded += (_, _) =>
        {
            StopPdfPageSync(railState);
            if (ReferenceEquals(_activePdfRail, railState))
            {
                _activePdfRail = null;
            }
        };
    }

    private sealed class PdfPageRailState(
        ContentControl host,
        ColumnDefinition railColumn,
        WebView2 webView,
        string path,
        long version,
        CancellationToken token)
    {
        public ContentControl Host { get; } = host;
        public ColumnDefinition RailColumn { get; } = railColumn;
        public WebView2 WebView { get; set; } = webView;
        public Grid? Surface { get; set; }
        public bool JumpInFlight { get; set; }

        /// <summary>The page asked for while a jump was mid-sequence. Last one wins.</summary>
        public int? PendingJumpPage { get; set; }

        public int CurrentPage { get; set; } = 1;
        public string Path { get; set; } = path;
        public long Version { get; } = version;
        public CancellationToken Token { get; } = token;
        public bool IsVisible { get; set; } = true;

        /// <summary>
        /// The page count, and the thumbnails rendered so far from page one
        /// on. Null until the count is known; set to null to load anew.
        /// </summary>
        public PdfPreviewDocument? Document { get; set; }

        /// <summary>Page one's width over height: the shape of every page not yet rendered.</summary>
        public double PageAspect { get; set; } = 612.0 / 792.0;

        /// <summary>Each load bumps this, so ranges from an older load are dropped.</summary>
        public long LoadGeneration { get; set; }

        public Func<PdfPageRailState, int, bool>? TryHandleJump { get; set; }
        public double RailVerticalOffset { get; set; }
        public bool RailNeedsSelectedPageScroll { get; set; }
        public Dictionary<int, Border> PageTiles { get; } = [];
        public Dictionary<int, Border> PageSheets { get; } = [];
        public Border? SelectedPageTile { get; set; }
        public ScrollViewer? PageRailScrollViewer { get; set; }
        public DispatcherTimer? PageSyncTimer { get; set; }
        public bool PageSyncInFlight { get; set; }
        public bool PageSyncDisabled { get; set; }
        public long PageSyncGeneration { get; set; }

        /// <summary>Latched when the user shows they are reading; page sync asks the viewer only after.</summary>
        public bool Reading { get; set; }

        public string? LastPageSource { get; set; }
        public System.Windows.Automation.AutomationElement? PageAutomationElement { get; set; }
        public TaskCompletionSource<bool> FirstRailReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct PdfPageAutomationProbeResult(
        int Page,
        System.Windows.Automation.AutomationElement? PageElement);

    /// <summary>
    /// The rail is the truth the parser cannot always reach: the first page
    /// thumbnail is rendered display-oriented by the document engine, so its
    /// aspect IS the page's shape. This fires before the reveal gate opens, so
    /// a document whose header defeated the probe still reveals page-shaped;
    /// for probed documents the hint agrees and the hysteresis absorbs it. A
    /// hint that loses the race is dropped by the after-reveal rule.
    /// </summary>
    private void HintStageFromRailTruth(PdfPageRailState state, PdfPreviewDocument document)
    {
        if (document.Pages.Count == 0)
        {
            return;
        }

        var first = document.Pages[0];
        if (first.Width < 1 || first.Height < 1)
        {
            return;
        }

        SetStageFitHint(StageFitHint.PageAspect((double)first.Width / first.Height, singlePage: document.TotalPages == 1), state.Version);
    }

    /// <summary>
    /// The page rail. Thumbnails render in short helper processes: pdfium
    /// can crash in native code on an ordinary document, and only the helper
    /// dies. When the page count is already known the rail is built here,
    /// before the viewer lays out, so its column never reflows the page and
    /// the reveal never waits on it; the ranges then fill it in place. A
    /// document nothing has counted yet waits for its first range instead.
    /// </summary>
    private async Task LoadPdfPageRailOutOfProcessAsync(PdfPageRailState state)
    {
        var generation = ++state.LoadGeneration;
        var path = state.Path;
        bool Current() =>
            generation == state.LoadGeneration
            && state.Version == _loadVersion
            && !state.Token.IsCancellationRequested;

        if (KnownPdfOutline(path) is { } outline)
        {
            ApplyPdfPageCount(state, outline.PageCount, outline.WidthPoints / outline.HeightPoints);
            if (outline.PageCount <= 1)
            {
                return;
            }
        }

        try
        {
            foreach (var (start, limit) in PdfRailRanges(() => state.Document?.TotalPages ?? 0))
            {
                var rendered = await TryRenderPdfThumbnailsOutOfProcessAsync(path, limit, state.Token, start);
                if (!Current())
                {
                    return;
                }

                if (rendered is null)
                {
                    // Pages a helper could not render stay as ghost pages.
                    if (state.Document is null)
                    {
                        HidePdfPageRail(state);
                        state.FirstRailReady.TrySetResult(true);
                    }

                    return;
                }

                if (start == 0)
                {
                    HintStageFromRailTruth(state, rendered);
                }

                if (state.Document?.TotalPages != rendered.TotalPages)
                {
                    var first = rendered.Pages.Count > 0 ? rendered.Pages[0] : null;
                    ApplyPdfPageCount(
                        state,
                        rendered.TotalPages,
                        first is null ? state.PageAspect : (double)first.Width / first.Height);
                    if (rendered.TotalPages <= 1)
                    {
                        return;
                    }
                }

                FillPdfPageRail(state, start, rendered.Pages);
            }
        }
        catch (OperationCanceledException)
        {
            state.FirstRailReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            state.FirstRailReady.TrySetResult(true);
            DiagnosticLog.Write($"pdf page rail failed {path}: {ex.GetType().Name}: {ex.Message}");
            if (Current() && state.Document is null)
            {
                HidePdfPageRail(state);
            }
        }
    }

    /// <summary>
    /// The helper calls for one document: ten pages first so the visible
    /// tiles fill fast, then ranges of at most a helper call each, up to the
    /// rail's cap. Each range is cached on its own, so a reopen renders
    /// nothing it already has. The plan reads the count as each range lands.
    /// </summary>
    internal static IEnumerable<(int Start, int Limit)> PdfRailRanges(Func<int> totalPages)
    {
        var start = 0;
        var limit = PdfPageRailInitialPages;
        while (true)
        {
            yield return (start, limit);
            start += limit;
            var end = Math.Min(totalPages(), PdfPageRailMaxPages);
            if (start >= end)
            {
                yield break;
            }

            limit = Math.Min(PdfPreviewRenderer.MaxThumbnailPagesPerCall, end - start);
        }
    }

    /// <summary>
    /// Decides the rail for a known page count: none for one page, otherwise
    /// every page up to the cap, as ghost pages at page one's shape until
    /// their thumbnails land. A rail that arrives after the reveal cloaks
    /// the viewer while its column takes the room.
    /// </summary>
    private void ApplyPdfPageCount(PdfPageRailState state, int totalPages, double pageAspect)
    {
        state.Document = new PdfPreviewDocument([], totalPages);
        ShowHeaderFact(FolderScanner.Count(totalPages, "page", "pages"), state.Version);
        if (double.IsFinite(pageAspect) && pageAspect > 0)
        {
            state.PageAspect = Math.Clamp(pageAspect, 0.2, 5);
        }

        if (totalPages <= 1)
        {
            HidePdfPageRail(state);
        }
        else
        {
            var width = state.IsVisible ? PdfPageRailWidth : PdfPageRailCollapsedWidth;
            var widthChanges = Math.Abs(state.RailColumn.Width.Value - width) > 0.5;
            RenderPdfPageRail(state, cloakViewer: widthChanges && _loadFinished && state.Version == _loadVersion);
        }

        state.FirstRailReady.TrySetResult(true);
    }

    /// <summary>A landed range turns its ghost pages into thumbnails, in place.</summary>
    private void FillPdfPageRail(PdfPageRailState state, int start, IReadOnlyList<PdfPreviewPage> pages)
    {
        if (state.Document is not { } document || start != document.Pages.Count)
        {
            return;
        }

        state.Document = document with { Pages = [.. document.Pages, .. pages] };
        for (var i = 0; i < pages.Count; i++)
        {
            if (state.PageSheets.TryGetValue(start + i + 1, out var sheet))
            {
                ShowPdfThumbnail(sheet, pages[i], animate: true);
            }
        }
    }

    private static async Task<PdfPreviewDocument?> TryRenderPdfThumbnailsOutOfProcessAsync(
        string path,
        int pageLimit,
        CancellationToken token,
        int startPage = 0)
    {
        var cacheDir = PdfRailCacheDirectory(path, pageLimit, startPage);
        var cached = await Task.Run(() => TryReadPdfThumbnailManifest(cacheDir), token);
        if (cached is not null)
        {
            return cached;
        }

        var root = Path.GetDirectoryName(cacheDir) ?? AppPaths.InDataRoot("PdfRail");
        Directory.CreateDirectory(root);
        var tempDir = Path.Combine(root, ".render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            using var process = PdfiumHelper.Start(
                "--render-pdf-thumbnails",
                path,
                tempDir,
                pageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                startPage.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (process is null)
            {
                return null;
            }

            // The helper rasters pages while Chromium is loading the same
            // document and the entrance is animating. It must never win CPU
            // from the frames the user is watching.
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // A helper that already exited keeps its default priority.
            }

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                PdfiumHelper.Kill(process);
                throw;
            }

            // A manifest is written last, so one that exists is complete even
            // when the helper died on its way out.
            var rendered = await Task.Run(() => TryReadPdfThumbnailManifest(tempDir), token);
            if (process.ExitCode != 0)
            {
                var written = Directory.EnumerateFiles(tempDir, "page-*.png").Count();
                DiagnosticLog.Write(
                    $"pdf thumbnail helper exit {process.ExitCode} for {path}: pages {startPage + 1}-{startPage + pageLimit}, "
                    + (rendered is null ? $"stopped at page {startPage + written + 1}" : "manifest complete"));
            }

            if (rendered is null)
            {
                return null;
            }

            try
            {
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, recursive: true);
                }

                Directory.Move(tempDir, cacheDir);
            }
            catch
            {
                // The live rail already loaded the images; cache reuse is a bonus.
            }

            return rendered;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static string PdfRailCacheDirectory(string path, int pageLimit, int startPage)
    {
        var info = new FileInfo(path);
        var identity = $"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{startPage}|{pageLimit}|v2";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return Path.Combine(
            AppPaths.DataRoot,
            "PdfRail",
            hash);
    }

    /// <summary>
    /// Reads a rendered range. Every page file must sit inside the range's own
    /// folder: a tampered or half-written manifest never points the rail
    /// anywhere else.
    /// </summary>
    internal static PdfPreviewDocument? TryReadPdfThumbnailManifest(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<PdfThumbnailManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.TotalPages <= 0)
            {
                return null;
            }

            if (manifest.TotalPages == 1)
            {
                return new PdfPreviewDocument([], 1);
            }

            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var pages = new List<PdfPreviewPage>(manifest.Pages.Count);
            foreach (var page in manifest.Pages.OrderBy(page => page.Number))
            {
                var filePath = Path.GetFullPath(Path.Combine(directory, page.File));
                if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                {
                    return null;
                }

                pages.Add(new PdfPreviewPage(LoadBitmapFromFile(filePath), page.Width, page.Height));
            }

            return new PdfPreviewDocument(pages, manifest.TotalPages);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource LoadBitmapFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }


    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // A cache cleanup miss is harmless.
        }
    }

    /// <summary>
    /// The rail owns only the space it is actually using. Collapsed means a
    /// small handle, not a fake empty sidebar.
    /// </summary>
    private void RenderPdfPageRail(PdfPageRailState state, bool cloakViewer = false)
    {
        if (state.Document is not { TotalPages: > 1 } document)
        {
            HidePdfPageRail(state);
            return;
        }

        var targetWidth = state.IsVisible ? PdfPageRailWidth : PdfPageRailCollapsedWidth;
        state.RailColumn.Width = new GridLength(targetWidth);
        state.PageTiles.Clear();
        state.PageSheets.Clear();
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;

        var content = state.IsVisible
            ? BuildPdfPageRail(state, document)
            : BuildCollapsedRail(document.TotalPages, "Show pages", () =>
            {
                state.IsVisible = true;
                RenderPdfPageRail(state, cloakViewer: true);
            });

        if (cloakViewer)
        {
            CloakViewerForRailResize(state.WebView, state.Version);
        }

        AnimateRail(state.Host, targetWidth, content);
    }

    private void HidePdfPageRail(PdfPageRailState state)
    {
        state.PageTiles.Clear();
        state.PageSheets.Clear();
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;
        state.RailColumn.Width = new GridLength(0);
        AnimateRail(state.Host, 0, null);
    }

    /// <summary>
    /// Every page up to the cap, built once. A page whose thumbnail has not
    /// landed is a ghost at page one's shape; it keeps its place and fills
    /// in where it stands, so the strip never rebuilds while it loads.
    /// </summary>
    private FrameworkElement BuildPdfPageRail(PdfPageRailState state, PdfPreviewDocument document)
    {
        // 131 px inside the hairline, less the 7 px scrollbar column, less
        // these margins and a tile's 1 px border and 6 px padding, leaves 98:
        // room for a 92 px sheet. A wider sheet is clipped at its sides.
        var stack = new StackPanel { Margin = new Thickness(8, 0, 4, 12) };
        FrameworkElement? pageToRevealOnLoad = null;

        var header = new Grid { Margin = new Thickness(18, 12, 11, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Pages",
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = Metrics.FontCaption
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary"));
        var hideButton = BuildRailChromeButton("", "Hide pages", () =>
        {
            state.IsVisible = false;
            RenderPdfPageRail(state, cloakViewer: true);
        });
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        var shown = Math.Min(document.TotalPages, PdfPageRailMaxPages);
        for (var pageNumber = 1; pageNumber <= shown; pageNumber++)
        {
            var sheet = new Border
            {
                Width = PdfRailSheetWidth,
                Height = PdfRailSheetWidth / state.PageAspect,
                HorizontalAlignment = HorizontalAlignment.Center,
                CornerRadius = Metrics.SurfaceCorner,
                BorderThickness = new Thickness(1),
                Child = new Image { Stretch = Stretch.Fill, SnapsToDevicePixels = true }
            }.Themed(Border.BorderBrushProperty, "Theme.RaisedBorder").Themed(Border.BackgroundProperty, "Theme.RaisedBg");
            RenderOptions.SetBitmapScalingMode(sheet.Child, BitmapScalingMode.HighQuality);
            if (pageNumber <= document.Pages.Count)
            {
                ShowPdfThumbnail(sheet, document.Pages[pageNumber - 1], animate: false);
            }

            var tile = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(6, 6, 6, 4),
                Margin = new Thickness(0, 0, 0, 4),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new StackPanel
                {
                    Children =
                    {
                        sheet,
                        new TextBlock
                        {
                            Text = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            Margin = new Thickness(0, 4, 0, 0),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            FontSize = Metrics.FontCaption
                        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary")
                    }
                }
            };
            System.Windows.Automation.AutomationProperties.SetName(tile, $"Page {pageNumber}");

            var selected = pageNumber == state.CurrentPage;
            ConfigureRailTile(
                tile,
                pageNumber,
                selected,
                page =>
                {
                    MarkPdfReading(state, "rail click");
                    SetPdfCurrentPageFromViewer(state, page, logChange: false);
                    if (state.TryHandleJump?.Invoke(state, page) == true)
                    {
                        return;
                    }

                    JumpPdfToPage(state, page);
                },
                (page, registered, isSelected) =>
                {
                    state.PageTiles[page] = registered;
                    if (isSelected)
                    {
                        state.SelectedPageTile = registered;
                    }
                });
            state.PageSheets[pageNumber] = sheet;
            if (selected)
            {
                pageToRevealOnLoad = tile;
            }

            stack.Children.Add(tile);
        }

        if (document.TotalPages > shown)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"First {shown} of {document.TotalPages} pages",
                TextAlignment = TextAlignment.Center,
                FontSize = Metrics.FontCaption,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 0)
            }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary"));
        }

        var surface = BuildRailSurface(
            header,
            stack,
            state.RailVerticalOffset,
            offset => state.RailVerticalOffset = offset,
            scroll => state.PageRailScrollViewer = scroll,
            scroll =>
            {
                if (!state.RailNeedsSelectedPageScroll || pageToRevealOnLoad is not { } tile)
                {
                    scroll.ScrollToVerticalOffset(state.RailVerticalOffset);
                    return;
                }

                // The saved offset is stale when the viewer moved while the
                // rail was hidden; reopen around the current page instead.
                _ = scroll.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() =>
                    {
                        BringPdfPageTileIntoView(state, tile);
                        state.RailNeedsSelectedPageScroll = false;
                    }));
            });

        // Looking at the rail is reading: it is worth keeping its highlight true.
        surface.MouseEnter += (_, _) => MarkPdfReading(state, "rail");
        return surface;
    }

    /// <summary>A sheet takes its thumbnail and that page's own shape; ghosts dissolve into pages.</summary>
    private static void ShowPdfThumbnail(Border sheet, PdfPreviewPage page, bool animate)
    {
        if (sheet.Child is not Image image || page.Width < 1 || page.Height < 1)
        {
            return;
        }

        sheet.Height = Math.Round(PdfRailSheetWidth * page.Height / page.Width);
        var inner = new Rect(0, 0, sheet.Width - 2, sheet.Height - 2);
        image.Clip = new RectangleGeometry(inner, Metrics.RadiusSurface - 1, Metrics.RadiusSurface - 1);
        image.Source = page.Bitmap;
        if (animate && Motion.Enabled)
        {
            image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    /// <summary>
    /// Page sync watches the viewer's address from the start: its own page
    /// box and links jump by fragment, which costs nothing to read. Asking
    /// the viewer through UI Automation turns on Chromium's accessibility
    /// tree, seconds of renderer work on a long document, so that waits until
    /// the user shows they are reading: a click in the document, the keyboard
    /// in it, the rail, or Fill &amp; Sign.
    /// </summary>
    private void StartPdfPageSync(PdfPageRailState state)
    {
        if (state.PageSyncTimer is not null)
        {
            return;
        }

        state.PageSyncDisabled = false;
        state.LastPageSource = state.WebView.CoreWebView2?.Source;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) => SyncPdfPageFromViewer(state);
        state.PageSyncTimer = timer;
        timer.Start();
    }

    private static void StopPdfPageSync(PdfPageRailState state)
    {
        state.PageSyncTimer?.Stop();
        state.PageSyncTimer = null;
        state.PageSyncGeneration++;
        state.PageAutomationElement = null;
    }

    private static void MarkPdfReading(PdfPageRailState state, string reason)
    {
        if (state.Reading)
        {
            return;
        }

        state.Reading = true;
        DiagnosticLog.Write($"pdf page sync reading ({reason})");
    }

    private async void SyncPdfPageFromViewer(PdfPageRailState state)
    {
        if (state.JumpInFlight)
        {
            return;
        }

        if (state.Token.IsCancellationRequested || state.Version != _loadVersion || state.WebView.CoreWebView2 is not { } core)
        {
            StopPdfPageSync(state);
            return;
        }

        try
        {
            if (!string.Equals(core.Source, state.LastPageSource, StringComparison.Ordinal))
            {
                state.LastPageSource = core.Source;
                if (TryReadPdfPageFromSource(core.Source, out var sourcePage))
                {
                    SetPdfCurrentPageFromViewer(state, sourcePage);
                    return;
                }
            }

            if (!state.Reading)
            {
                if (_userEngagedContent)
                {
                    MarkPdfReading(state, "content click");
                }
                else if (ContentOwnsKeyboard())
                {
                    MarkPdfReading(state, "keyboard");
                }
                else
                {
                    return;
                }
            }

            if (state.PageSyncInFlight || state.PageSyncDisabled)
            {
                return;
            }

            var generation = state.PageSyncGeneration;
            state.PageSyncInFlight = true;
            try
            {
                var result = await ProbePdfPageAsync(state);
                if (generation != state.PageSyncGeneration
                    || state.Version != _loadVersion
                    || state.Token.IsCancellationRequested)
                {
                    return;
                }

                if (result is null)
                {
                    // UIA calls cannot be cancelled safely. Keep the single
                    // worker isolated and stop polling this viewer rather than
                    // stacking more blocked probes behind it.
                    state.PageSyncDisabled = true;
                    DiagnosticLog.Write(
                        $"pdf page sync disabled after {PdfPageAutomationTimeout.TotalSeconds:0}s timeout");
                    return;
                }

                if (result.Value.Page > 0)
                {
                    SetPdfCurrentPageFromViewer(state, result.Value.Page);
                }
            }
            finally
            {
                state.PageSyncInFlight = false;
            }
        }
        catch
        {
            state.PageAutomationElement = null;
        }
    }

    /// <summary>
    /// Asks the viewer's own page box which page is showing. UI Automation
    /// crosses into Chromium's PDF provider and can block, so the question
    /// runs off the dispatcher and an answer that takes longer than the
    /// timeout is null. Page 0 means the viewer answered without a page.
    /// </summary>
    private async Task<PdfPageAutomationProbeResult?> ProbePdfPageAsync(PdfPageRailState state)
    {
        var totalPages = state.Document?.TotalPages ?? 0;
        var hwnd = TryGetPdfAutomationHwnd(state.WebView);
        if (totalPages <= 1 || hwnd == IntPtr.Zero)
        {
            return default(PdfPageAutomationProbeResult);
        }

        var element = state.PageAutomationElement;
        var probe = Task.Run(() =>
        {
            try
            {
                return TryReadPdfPageFromAutomation(hwnd, totalPages, element);
            }
            catch
            {
                return default;
            }
        });

        if (await Task.WhenAny(probe, Task.Delay(PdfPageAutomationTimeout)) != probe)
        {
            return null;
        }

        var result = await probe;
        if (result.PageElement is not null)
        {
            state.PageAutomationElement = result.PageElement;
        }

        return result;
    }

    /// <summary>
    /// Fill &amp; Sign's entry point: the one-based page the reader is on and
    /// the page count (0 when unknown).
    /// </summary>
    private async Task<(int Page, int TotalPages)> ReadPdfPositionAsync()
    {
        if (_activePdfRail is not { } state)
        {
            return (1, 0);
        }

        MarkPdfReading(state, "fill and sign");
        await ReadPdfPageFromViewerAsync(state);
        return (state.CurrentPage, state.Document?.TotalPages ?? 0);
    }

    /// <summary>
    /// Asks the viewer which page the reader is on, now. Page sync waits for
    /// signs of reading, so a document scrolled by wheel alone has a stale
    /// CurrentPage. A viewer that cannot answer leaves it as it was.
    /// </summary>
    private async Task ReadPdfPageFromViewerAsync(PdfPageRailState state)
    {
        if (await ProbePdfPageAsync(state) is { Page: > 0 } result && state.Version == _loadVersion)
        {
            SetPdfCurrentPageFromViewer(state, result.Page, logChange: false);
        }
    }

    private void SetPdfCurrentPageFromViewer(
        PdfPageRailState state,
        int page,
        bool logChange = true)
    {
        var target = Math.Max(1, page);
        if (state.Document is { TotalPages: > 0 } document)
        {
            target = Math.Min(target, document.TotalPages);
        }

        if (target == state.CurrentPage
            && state.SelectedPageTile is not null
            && state.PageTiles.TryGetValue(target, out var currentTile)
            && ReferenceEquals(state.SelectedPageTile, currentTile))
        {
            return;
        }

        var previousPage = state.CurrentPage;
        state.CurrentPage = target;
        if (logChange && target != previousPage)
        {
            DiagnosticLog.Write($"pdf rail sync -> page {target}");
        }
        if (!state.IsVisible || state.PageTiles.Count == 0)
        {
            if (target != previousPage)
            {
                state.RailNeedsSelectedPageScroll = true;
            }
            return;
        }

        if (state.SelectedPageTile is not null)
        {
            ApplyRailTileState(state.SelectedPageTile, selected: false);
        }

        state.SelectedPageTile = null;
        if (state.PageTiles.TryGetValue(target, out var tile))
        {
            state.SelectedPageTile = tile;
            ApplyRailTileState(tile, selected: true);

            // A selection nobody can see reads as no selection at all. Keep
            // the highlighted tile in view when the document moves it - but
            // a pointer over the rail owns that scroll, never fight the hand.
            if (!state.Host.IsMouseOver)
            {
                BringPdfPageTileIntoView(state, tile);
            }

            state.RailNeedsSelectedPageScroll = false;
        }
        else
        {
            state.RailNeedsSelectedPageScroll = true;
        }
    }

    private static void BringPdfPageTileIntoView(PdfPageRailState state, FrameworkElement tile)
    {
        var scroll = state.PageRailScrollViewer;
        if (scroll is null || !scroll.IsLoaded || !tile.IsLoaded)
        {
            tile.BringIntoView();
            return;
        }

        try
        {
            var bounds = tile.TransformToAncestor(scroll).TransformBounds(
                new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
            var topBand = scroll.ViewportHeight * 0.25;
            var bottomBand = scroll.ViewportHeight * 0.75;
            if (bounds.Top >= topBand && bounds.Bottom <= bottomBand)
            {
                return;
            }

            var targetTop = scroll.ViewportHeight / 3;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + bounds.Top - targetTop);
        }
        catch
        {
            tile.BringIntoView();
        }
    }

    private static PdfPageAutomationProbeResult TryReadPdfPageFromAutomation(
        IntPtr hwnd,
        int totalPages,
        System.Windows.Automation.AutomationElement? cachedElement)
    {
        if (totalPages <= 1)
        {
            return default;
        }

        if (TryReadPdfPageFromAutomationElement(cachedElement, totalPages, out var page))
        {
            return new PdfPageAutomationProbeResult(page, cachedElement);
        }

        var root = TryCreatePdfAutomationRoot(hwnd);
        if (root is null)
        {
            return default;
        }

        var condition = new System.Windows.Automation.OrCondition(
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Edit),
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Spinner));

        System.Windows.Automation.AutomationElementCollection candidates;
        try
        {
            candidates = root.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
        }
        catch
        {
            return default;
        }

        foreach (System.Windows.Automation.AutomationElement candidate in candidates)
        {
            if (TryReadPdfPageFromAutomationElement(candidate, totalPages, out page))
            {
                return new PdfPageAutomationProbeResult(page, candidate);
            }
        }

        return default;
    }

    private IntPtr TryGetPdfAutomationHwnd(WebView2 webView)
    {
        try
        {
            return PresentationSource.FromVisual(webView) is HwndSource source
                ? source.Handle
                : new WindowInteropHelper(this).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static System.Windows.Automation.AutomationElement? TryCreatePdfAutomationRoot(IntPtr hwnd)
    {
        try
        {
            return hwnd == IntPtr.Zero
                ? null
                : System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadPdfPageFromAutomationElement(
        System.Windows.Automation.AutomationElement? element,
        int totalPages,
        out int page)
    {
        page = 0;
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.TryGetCurrentPattern(
                    System.Windows.Automation.ValuePattern.Pattern,
                    out var valuePattern)
                && valuePattern is System.Windows.Automation.ValuePattern value
                && TryReadPdfAutomationPageValue(value.Current.Value, totalPages, out page))
            {
                return true;
            }

            if (element.TryGetCurrentPattern(
                    System.Windows.Automation.RangeValuePattern.Pattern,
                    out var rangePattern)
                && rangePattern is System.Windows.Automation.RangeValuePattern range)
            {
                var current = range.Current.Value;
                if (current >= 1
                    && current <= totalPages
                    && Math.Abs(current - Math.Round(current)) < 0.01)
                {
                    page = (int)Math.Round(current);
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static bool TryReadPdfAutomationPageValue(string? value, int totalPages, out int page)
    {
        page = 0;
        if (totalPages <= 1 || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 1
            && number <= totalPages)
        {
            page = number;
            return true;
        }

        return false;
    }

    internal static bool TryReadPdfPageFromSource(string? source, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals("page", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && value > 0)
            {
                page = value;
                return true;
            }
        }

        return false;
    }

    private void JumpPdfToPage(PdfPageRailState state, int page) =>
        _ = JumpPdfToPageAsync(state, page);

    /// <summary>
    /// The PDF viewer's own page box jumps instantly, so our rail clicks the
    /// box and types the target page instead of reloading the document. If
    /// WebView stops honoring that path, we fall back to the staged reload
    /// path that kept the previous page visible.
    /// </summary>
    private async Task JumpPdfToPageAsync(PdfPageRailState state, int page)
    {
        if (state.JumpInFlight)
        {
            // The jump types into the viewer's page box, so two sequences
            // cannot overlap without typing over each other. Hold the click
            // rather than dropping it: the rail has already moved its
            // highlight there, and a rail pointing at a page the document
            // never went to is worse than a wait.
            state.PendingJumpPage = page;
            return;
        }

        state.JumpInFlight = true;
        var target = Math.Max(1, page);
        state.CurrentPage = target;
        try
        {
            if (state.Version != _loadVersion || state.WebView.CoreWebView2 is not { } core)
            {
                return;
            }

            // The synthetic click aims at where the page box sits rather than
            // at the box itself, so it can miss for reasons this code cannot
            // see: a runtime that moves the toolbar, a narrow stage, a page
            // count wide enough to change the control's width. A timeout keeps
            // a wedged call from holding the lock, and the viewer is asked
            // afterwards where it actually landed.
            await JumpPdfWithNativePageBoxAsync(state.WebView, core, target)
                .WaitAsync(PdfPageAutomationTimeout);

            if (await LandedOnPdfPageAsync(state, target))
            {
                DiagnosticLog.Write($"pdf rail native jump -> page {target}");
            }
            else
            {
                DiagnosticLog.Write($"pdf rail native jump missed page {target}, staging reload");
                StagePdfViewerJump(state, target);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf rail native jump failed: {ex.GetType().Name}: {ex.Message}");
            StagePdfViewerJump(state, target);
        }
        finally
        {
            RunAfter(TimeSpan.FromMilliseconds(650), () =>
            {
                state.JumpInFlight = false;
                if (state.PendingJumpPage is { } pending)
                {
                    state.PendingJumpPage = null;
                    JumpPdfToPage(state, pending);
                }
            });
        }
    }

    /// <summary>
    /// Asks the viewer, through its own page control, whether the jump landed.
    /// Unknown counts as landed: the probe is a check on the jump, not a
    /// second gate, and a viewer that will not answer must not cost the user a
    /// document reload.
    /// </summary>
    private async Task<bool> LandedOnPdfPageAsync(PdfPageRailState state, int target) =>
        await ProbePdfPageAsync(state) is not { Page: > 0 } result || result.Page == target;

    /// <summary>
    /// Clicks the viewer's own page box and types the target. The offset is
    /// measured against the toolbar set ConfigurePdfViewer keeps: show or hide
    /// one more item and the box slides out from under this click, after which
    /// the Ctrl+A selects the document instead of the box. Change one, retest
    /// the other.
    /// </summary>
    private static async Task JumpPdfWithNativePageBoxAsync(WebView2 webView, CoreWebView2 core, int page)
    {
        var x = Math.Max(160, webView.ActualWidth / 2 + 15);
        await DispatchMouseClickAsync(core, x, 20);
        await Task.Delay(35);
        await DispatchModifiedKeyAsync(core, 65, "KeyA", "a", modifiers: 2);
        await Task.Delay(20);
        await core.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text = page.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        await DispatchPlainKeyAsync(core, 13, "Enter", "Enter");
    }

    /// <summary>
    /// Fallback only. It is slower, but it never leaves a blank page behind:
    /// the old viewer stays visible until the new one has loaded. The viewer
    /// honours #page only at document load, so this is a real load with a
    /// cache-busting query, never a runtime hash change.
    /// </summary>
    private void StagePdfViewerJump(PdfPageRailState state, int target)
    {
        var uri = new Uri(
            $"{new Uri(state.Path, UriKind.Absolute).AbsoluteUri}?jump={target}#page={target}&view=FitH",
            UriKind.Absolute);
        StagePdfViewerReplacement(
            state,
            () => uri,
            state.Path,
            $"pdf jump {state.Path} -> {target}",
            "pdf jump");
    }

    /// <summary>Keeps the current PDF visible until its owned replacement has painted.</summary>
    private void StagePdfViewerReplacement(
        PdfPageRailState state,
        Func<Uri> source,
        string pdfPath,
        string navigationLabel,
        string paintLabel,
        Action? onSwapped = null)
    {
        if (state.Surface is not { } surface)
        {
            return;
        }

        var fresh = CreateWebView(
            WebSurfaceKind.PdfViewer,
            source,
            navigationLabel,
            onFailed: null,
            configure: core => ConfigurePdfViewer(core, pdfPath),
            trackAsPreviewDisposable: false);
        Grid.SetColumn(fresh, 1);
        fresh.Visibility = Visibility.Hidden;
        fresh.AllowNavigation();
        surface.Children.Add(fresh);

        var finished = false;
        bool FreshIsOwned() =>
            state.Version == _loadVersion
            && !state.Token.IsCancellationRequested
            && ReferenceEquals(fresh.Parent, surface);

        void DiscardFresh()
        {
            finished = true;
            if (ReferenceEquals(fresh.Parent, surface))
            {
                surface.Children.Remove(fresh);
            }

            fresh.Dispose();
        }

        void Swap()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            var old = state.WebView;
            if (state.Version != _loadVersion || !ReferenceEquals(old.Parent, surface))
            {
                DiscardFresh();
                return;
            }

            fresh.Visibility = Visibility.Visible;
            fresh.UpdateLayout();
            old.Visibility = Visibility.Collapsed;
            surface.Children.Remove(old);
            if (ReferenceEquals(_currentDisposable, old))
            {
                _currentDisposable = fresh;
            }

            old.Dispose();
            state.WebView = fresh;
            state.PageSyncGeneration++;
            state.PageSyncDisabled = false;
            state.PageAutomationElement = null;
            onSwapped?.Invoke();
        }

        // Navigation and the 4 s escape share one owner: only one of them
        // may start the paint wait, and the escape stands down once it has.
        var paintAttemptStarted = false;
        fresh.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess || finished || paintAttemptStarted)
            {
                return;
            }

            if (!FreshIsOwned())
            {
                DiscardFresh();
                return;
            }

            paintAttemptStarted = true;
            var paintClock = Stopwatch.StartNew();
            var paintReady = await WaitForDocumentPaintAsync(fresh, state.Token);
            if (finished)
            {
                return;
            }

            if (!FreshIsOwned())
            {
                DiscardFresh();
                return;
            }

            DiagnosticLog.Write(
                $"{paintLabel} paint gate ready={paintReady} wait={paintClock.Elapsed.TotalMilliseconds:0}ms");
            Swap();
        };
        RunAfter(TimeSpan.FromMilliseconds(4000), () =>
        {
            if (!paintAttemptStarted)
            {
                Swap();
            }
        });
    }
}
