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

    // How long a state round-trip to our owned PDF viewer may wait.
    private static readonly TimeSpan PdfViewerQueryTimeout = TimeSpan.FromSeconds(2);

    // Upper bound on waiting for page one to paint before revealing the viewer.
    private static readonly TimeSpan PdfFirstPaintCap = TimeSpan.FromMilliseconds(1500);

    internal enum PdfNavigationAction
    {
        Allow,
        Cancel,
        OpenExternally
    }

    /// <summary>
    /// The viewer may load the one file it was opened on, wherever that
    /// file lives: a PDF on a share has a host in its address, and
    /// cancelling it for that showed the error card for every PDF on a NAS
    /// or in WSL. Any other file stays out, on any host.
    /// </summary>
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

        if (!parsed.IsFile)
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

    private static Uri PdfViewerSource(string path, int page = 1) =>
        PdfViewerPage.SourceUri(page);

    private void ConfigurePdfViewer(
        StagedWebView webView,
        CoreWebView2 core,
        string sourcePath,
        PdfPageRailState? railState = null,
        TaskCompletionSource<bool>? readySignal = null,
        Action<string>? onError = null,
        bool allowPasswordReveal = true)
    {
        var source = PdfViewerPage.Attach(
            core,
            sourcePath,
            OpenExternalUri,
            json => HandlePdfViewerMessage(railState, core, json, readySignal, onError, allowPasswordReveal));
        webView.AttachOwnedResource(source);
    }

    /// <summary>
    /// Single error handler for whichever PDF viewer currently owns the stage:
    /// ignores stale or retired viewers and defers replacement while Fill &amp;
    /// Sign holds unsaved work on screen.
    /// </summary>
    private void ReportPdfSurfaceFailure(
        PdfPageRailState? state,
        CoreWebView2? core,
        long version,
        string label,
        Action showFallback)
    {
        if (version != _loadVersion)
        {
            return;
        }

        if (state is not null && core is not null && !ReferenceEquals(state.WebView.CoreWebView2, core))
        {
            return;
        }

        if (_fillSignSurface is not null)
        {
            _deferredWebFailure = () => ReportPdfSurfaceFailure(state, core, version, label, showFallback);
            DiagnosticLog.Write($"{label} failure deferred during fill-sign");
            return;
        }

        showFallback();
    }

    private FrameworkElement BuildPdfWebPreview(string path, long version, CancellationToken token)
    {
        var grid = new Grid().Themed(Panel.BackgroundProperty, "Theme.CanvasBg");

        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);

        PdfPageRailState? railState = null;
        StagedWebView? webView = null;
        void ShowPdfFallback() => SetPreviewContent(BuildPdfErrorPreview(path), null);

        webView = CreateWebView(
            WebSurfaceKind.PdfViewer,
            () => PdfViewerSource(path),
            $"pdf preview {path}",
            _ => ReportPdfSurfaceFailure(railState, webView?.CoreWebView2, version, $"pdf preview {path}", ShowPdfFallback),
            configure: core => ConfigurePdfViewer(
                webView!,
                core,
                path,
                railState,
                railState?.FirstPaintReady,
                _ => ReportPdfSurfaceFailure(railState, core, version, $"pdf preview {path}", ShowPdfFallback)));
        Grid.SetColumn(webView, 1);
        webView.HoldCachePath(path);
        grid.Children.Add(webView);
        railState = new PdfPageRailState(railHost, railColumn, webView, path, version, token) { Surface = grid };
        grid.Tag = railState;
        TrackPdfRailLifetime(grid, railState);
        _ = LoadPdfPageRailOutOfProcessAsync(railState);
        if (!HasBytesAtHand(path))
        {
            _ = HintPdfStageOffThreadAsync(path, version);
        }

        return grid;
    }

    /// <summary>
    /// A file on a share is never read on the dispatcher, so the page shape
    /// that sizes a local PDF's window before it loads is read on the pool
    /// for this one, and lands like any probe that missed its wait.
    /// </summary>
    private async Task HintPdfStageOffThreadAsync(string path, long version)
    {
        var outline = await Task.Run(() => PdfPageSizeProbe.Read(path) ?? CachedPdfOutline(path));
        HintPdfStage(outline, version);
    }

    /// <summary>
    /// What a document the viewer cannot show gets. pdfium never renders a
    /// user's document in this process: it can die in native code, and here
    /// it would take every later preview with it.
    /// </summary>
    private static FrameworkElement BuildPdfErrorPreview(string path)
    {
        var kind = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? "PDF" : "file";
        // The header's own question, so the card and the Open pill name the same app.
        var next = ResolveOpenTarget(path, isDirectory: false).AppName is { } app
            ? $"Open it with {app} instead."
            : "Open it in another app instead.";
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
            return HintPdfStage(probe.Result ?? CachedPdfOutline(pdfPath), version);
        }

        _ = probe.ContinueWith(
            done => Dispatcher.BeginInvoke(new Action(() => HintPdfStage(done.Result ?? CachedPdfOutline(pdfPath), version))),
            TaskScheduler.Default);
        return true;
    }

    private bool HintPdfStage(PdfOutline? outline, long version)
    {
        if (outline is not { WidthPoints: > 0, HeightPoints: > 0 } page)
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

    /// <summary>
    /// The count the rail starts from. A file with its bytes at hand is
    /// asked here, so a known count builds the rail before the viewer lays
    /// out. A share, a mapped drive or an online-only file is asked on the
    /// pool: its metadata alone takes as long as the network does. Tests
    /// stand in for the read to see which thread makes it.
    /// </summary>
    internal static Task<PdfOutline?> ReadPdfRailOutlineAsync(string path, Func<string, PdfOutline?>? read = null) =>
        HasBytesAtHand(path)
            ? Task.FromResult((read ?? KnownPdfOutline)(path))
            : Task.Run(() => read is not null
                ? read(path)
                : PdfPageSizeProbe.Read(path) is { PageCount: > 0 } outline ? outline : CachedPdfOutline(path));

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
                : new PdfOutline(Math.Max(0, manifest.Width), Math.Max(0, manifest.Height), manifest.TotalPages);
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
        grid.Loaded += (_, _) => _activePdfRail = railState;
        grid.Unloaded += (_, _) =>
        {
            CancelPendingPdfQueries(railState);
            if (ReferenceEquals(_activePdfRail, railState))
            {
                _activePdfRail = null;
            }
        };
    }

    private readonly record struct PdfViewerSnapshot(int Page, int TotalPages, bool AtOpening, int Zoom, bool FindOpen);

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

        /// <summary>The page the viewer last reported; 0 before first paint.</summary>
        public int ViewerPage { get; set; }

        /// <summary>True while the viewer has neither scrolled, zoomed, selected text, nor opened find.</summary>
        public bool AtOpening { get; set; } = true;

        /// <summary>
        /// Set by a rail click until the viewer acknowledges the jump ID, so an
        /// in-flight scroll event from before the jump never snaps the rail back.
        /// </summary>
        public int? PendingRailJumpTarget { get; set; }

        public int NextJumpId { get; set; }
        public int? PendingJumpId { get; set; }

        public int NextQueryId { get; set; }
        public Dictionary<int, TaskCompletionSource<PdfViewerSnapshot>> PendingQueries { get; } = [];

        public TaskCompletionSource<bool> FirstRailReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> FirstPaintReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

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
            // A one-page document draws no thumbnail and still names its shape.
            if (document.PageOneAspect > 0 && double.IsFinite(document.PageOneAspect))
            {
                SetStageFitHint(StageFitHint.PageAspect(document.PageOneAspect, singlePage: document.TotalPages == 1), state.Version);
            }

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

        // A local file is asked here, so a known count builds the rail
        // before the viewer lays out. A share is asked on the pool.
        var known = await ReadPdfRailOutlineAsync(path);
        if (!Current())
        {
            return;
        }

        if (known is { } outline)
        {
            ApplyPdfPageCount(state, outline.PageCount, outline.WidthPoints / outline.HeightPoints);

            // One page needs no rail, only its shape. A count cached before
            // 3.0.1 came without one, and the first range below brings it.
            if (outline is { PageCount: <= 1, WidthPoints: > 0, HeightPoints: > 0 })
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
        // The cache's name is made from the file's size and time: a look at
        // the file, which on a share is a trip over the network, so it is
        // made off this thread with the read.
        var cacheDir = await Task.Run(() => PdfRailCacheDirectory(path, pageLimit, startPage), token);
        var cached = await Task.Run(() => TryReadPdfThumbnailManifest(cacheDir), token);

        // A one-page entry cached before 3.0.1 has no page shape: it is made again.
        if (cached is not null && (cached.TotalPages != 1 || cached.PageOneAspect > 0))
        {
            return cached;
        }

        var root = Path.GetDirectoryName(cacheDir) ?? AppPaths.InDataRoot("PdfRail");
        Directory.CreateDirectory(root);
        var tempDir = Path.Combine(root, ".render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            // The helper rasters pages while Chromium is loading the same
            // document and the entrance is animating. It must never win CPU
            // from the frames the user is watching. A helper that hangs is
            // killed at its deadline, not left running under the preview.
            var run = await PdfiumHelper.RunAsync(
                PdfiumHelper.RangeDeadline(pageLimit),
                token,
                ProcessPriorityClass.BelowNormal,
                "--render-pdf-thumbnails",
                path,
                tempDir,
                pageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
                startPage.ToString(System.Globalization.CultureInfo.InvariantCulture));
            token.ThrowIfCancellationRequested();
            if (!run.Started)
            {
                DiagnosticLog.Write("pdf thumbnail helper did not start");
                return null;
            }

            // A manifest is written last, so one that exists is complete even
            // when the helper died on its way out.
            var rendered = await Task.Run(() => TryReadPdfThumbnailManifest(tempDir), token);
            if (run.ExitCode != 0)
            {
                var written = Directory.EnumerateFiles(tempDir, "page-*.png").Count();
                var ending = run.ExitCode is { } exitCode ? $"exit {exitCode}" : "missed its deadline";
                DiagnosticLog.Write(
                    $"pdf thumbnail helper {ending} for {path}: pages {startPage + 1}-{startPage + pageLimit}, "
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
                return new PdfPreviewDocument([], 1, manifest is { Width: > 0, Height: > 0 } ? (double)manifest.Width / manifest.Height : 0);
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
                    state.PendingRailJumpTarget = page;
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

    // ---------------------------------------------------------------------
    // Direct viewer bridge (postMessage). No UI Automation, no toolbar pixel
    // guessing, and no SendKeys.
    // ---------------------------------------------------------------------

    private void HandlePdfViewerMessage(
        PdfPageRailState? state,
        CoreWebView2 core,
        string json,
        TaskCompletionSource<bool>? readySignal,
        Action<string>? onError,
        bool allowPasswordReveal = true)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeProp)
                || typeProp.ValueKind != JsonValueKind.String)
            {
                return;
            }

            var type = typeProp.GetString();
            switch (type)
            {
                case "ready":
                {
                    var pages = ReadInt(root, "pages", 0);
                    var page = ReadInt(root, "page", 1);
                    var rendered = ReadBool(root, "rendered", false);
                    var atOpening = ReadBool(root, "atOpening", true);
                    if (!rendered || pages <= 0)
                    {
                        return;
                    }

                    readySignal?.TrySetResult(true);

                    if (state is not null
                        && state.Version == _loadVersion
                        && ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        state.FirstPaintReady.TrySetResult(true);
                        state.ViewerPage = page;
                        state.AtOpening = atOpening;
                        if (state.Document is null)
                        {
                            ApplyPdfPageCount(state, pages, state.PageAspect);
                        }

                        SetPdfCurrentPageFromViewer(state, page, logChange: false);
                    }
                    break;
                }

                case "password":
                {
                    // Prompt readiness lets the initial preview reveal the password box
                    // immediately, while a staged replacement never promotes an unrendered prompt.
                    if (allowPasswordReveal
                        && state is not null
                        && state.Version == _loadVersion
                        && ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        _pdfTextInputFocused = true;
                        state.FirstPaintReady.TrySetResult(true);
                    }
                    break;
                }

                case "jumpAck":
                {
                    if (state is null
                        || state.Version != _loadVersion
                        || !ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        return;
                    }

                    var id = ReadInt(root, "id", 0);
                    var page = ReadInt(root, "page", state.CurrentPage);
                    state.AtOpening = ReadBool(root, "atOpening", state.AtOpening);
                    if (state.PendingJumpId == id)
                    {
                        state.PendingJumpId = null;
                        state.PendingRailJumpTarget = null;
                        state.ViewerPage = page;
                        SetPdfCurrentPageFromViewer(state, page, logChange: false);
                    }
                    break;
                }

                case "state":
                {
                    if (state is null
                        || state.Version != _loadVersion
                        || !ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        return;
                    }

                    var page = ReadInt(root, "page", state.CurrentPage);
                    var atOpening = ReadBool(root, "atOpening", state.AtOpening);
                    state.AtOpening = atOpening;

                    if (state.PendingJumpId is not null)
                    {
                        return;
                    }

                    var moved = page != state.ViewerPage;
                    state.ViewerPage = page;
                    if (moved)
                    {
                        SetPdfCurrentPageFromViewer(state, page);
                    }
                    break;
                }

                case "stateReply":
                {
                    if (state is null)
                    {
                        return;
                    }

                    var id = ReadInt(root, "id", 0);
                    if (state.PendingQueries.Remove(id, out var pending))
                    {
                        var snapshot = new PdfViewerSnapshot(
                            ReadInt(root, "page", state.CurrentPage),
                            ReadInt(root, "totalPages", state.Document?.TotalPages ?? 0),
                            ReadBool(root, "atOpening", state.AtOpening),
                            ReadInt(root, "zoom", 100),
                            ReadBool(root, "findOpen", false));
                        state.AtOpening = snapshot.AtOpening;
                        pending.TrySetResult(snapshot);
                    }
                    break;
                }

                case "find":
                {
                    if (state is not null
                        && state.Version == _loadVersion
                        && ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        _escapeYieldsToContent = ReadBool(root, "open", false);
                    }
                    break;
                }

                case "inputFocus":
                {
                    if (state is not null
                        && state.Version == _loadVersion
                        && ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        _pdfTextInputFocused = ReadBool(root, "active", false);
                    }
                    break;
                }

                case "openExternal":
                {
                    if (root.TryGetProperty("url", out var urlProp)
                        && urlProp.ValueKind == JsonValueKind.String
                        && urlProp.GetString() is { } url
                        && PdfViewerPage.IsExternalWebUri(url))
                    {
                        OpenExternalUri(url);
                    }
                    break;
                }

                case "printError":
                {
                    var message = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                        ? msgProp.GetString() ?? "print-failed"
                        : "print-failed";
                    DiagnosticLog.Write($"pdf viewer print failed: {message}");
                    break;
                }

                case "error":
                {
                    var message = root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String
                        ? msgProp.GetString() ?? "load-failed"
                        : "load-failed";
                    DiagnosticLog.Write($"pdf viewer load failed: {message}");
                    readySignal?.TrySetResult(false);
                    if (state is not null && ReferenceEquals(state.WebView.CoreWebView2, core))
                    {
                        state.FirstPaintReady.TrySetResult(false);
                    }

                    onError?.Invoke(message);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf viewer message parse failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var value)
            ? value
            : fallback;

    private static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var prop) && prop.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? prop.GetBoolean()
            : fallback;

    private static void CancelPendingPdfQueries(PdfPageRailState state)
    {
        foreach (var pending in state.PendingQueries.Values)
        {
            pending.TrySetCanceled();
        }

        state.PendingQueries.Clear();
    }

    private void SendPdfViewerCommand(object command)
    {
        if (_activePdfRail?.WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(command));
        }
        catch
        {
            // A torn-down view cannot take commands.
        }
    }

    private async Task<PdfViewerSnapshot?> QueryPdfStateAsync(PdfPageRailState state)
    {
        if (state.WebView.CoreWebView2 is not { } core)
        {
            return null;
        }

        var webView = state.WebView;
        var id = ++state.NextQueryId;
        var tcs = new TaskCompletionSource<PdfViewerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        state.PendingQueries[id] = tcs;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "queryState", id }));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
            timeout.CancelAfter(PdfViewerQueryTimeout);
            var snapshot = await tcs.Task.WaitAsync(timeout.Token);
            return ReferenceEquals(state.WebView, webView) ? snapshot : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            state.PendingQueries.Remove(id);
        }
    }

    private async Task<bool> QueryPdfAtOpeningAsync(PdfPageRailState state)
    {
        var snapshot = await QueryPdfStateAsync(state);
        return snapshot is { AtOpening: true };
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

        if (state.PendingJumpId is null
            && await QueryPdfStateAsync(state) is { Page: > 0 } snapshot
            && state.Version == _loadVersion)
        {
            state.ViewerPage = snapshot.Page;
            SetPdfCurrentPageFromViewer(state, snapshot.Page, logChange: false);
            var total = snapshot.TotalPages > 0 ? snapshot.TotalPages : state.Document?.TotalPages ?? 0;
            return (state.CurrentPage, total);
        }

        return (state.CurrentPage, state.Document?.TotalPages ?? 0);
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

    private void JumpPdfToPage(PdfPageRailState state, int page)
    {
        var target = Math.Max(1, page);
        SetPdfCurrentPageFromViewer(state, target, logChange: false);

        if (state.Version != _loadVersion || state.WebView.CoreWebView2 is not { } core)
        {
            state.PendingJumpId = null;
            state.PendingRailJumpTarget = null;
            return;
        }

        var id = ++state.NextJumpId;
        state.PendingJumpId = id;
        state.PendingRailJumpTarget = target;
        try
        {
            core.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "jump", page = target, id }));
            DiagnosticLog.Write($"pdf rail jump -> page {target}");
        }
        catch (Exception ex)
        {
            state.PendingJumpId = null;
            state.PendingRailJumpTarget = null;
            DiagnosticLog.Write($"pdf rail jump failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Keeps the current PDF visible while its owned replacement loads (for
    /// example when the full converted Office document replaces its first
    /// pages). Swaps once the replacement confirms a rendered canvas and
    /// <paramref name="stillWanted"/> still agrees; a replacement that fails
    /// to load or misses its 4 s cap is discarded and the working viewer stays.
    /// After promotion, any later renderer failure routes through
    /// <paramref name="onActiveFailure"/> so the promoted viewer keeps full
    /// error handling.
    /// </summary>
    private void StagePdfViewerReplacement(
        PdfPageRailState state,
        Func<Uri> source,
        string pdfPath,
        string navigationLabel,
        string logLabel,
        Action? onSwapped = null,
        Func<Task<bool>>? stillWanted = null,
        Action? onCalledOff = null,
        Action? onActiveFailure = null)
    {
        if (state.Surface is not { } surface)
        {
            return;
        }

        var finished = false;
        var promoted = false;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StagedWebView? fresh = null;

        bool FreshIsOwned() =>
            fresh is not null
            && state.Version == _loadVersion
            && !state.Token.IsCancellationRequested
            && ReferenceEquals(fresh.Parent, surface);

        void DiscardFresh()
        {
            finished = true;
            if (fresh is not null)
            {
                if (ReferenceEquals(fresh.Parent, surface))
                {
                    surface.Children.Remove(fresh);
                }

                fresh.Dispose();
            }
        }

        void CallOff(string why)
        {
            if (finished)
            {
                return;
            }

            var owned = FreshIsOwned();
            DiscardFresh();
            if (owned)
            {
                DiagnosticLog.Write($"{logLabel} {why}, the viewer stays");
                onCalledOff?.Invoke();
            }
        }

        void HandleReplacementFailure(string why)
        {
            if (promoted)
            {
                if (onActiveFailure is not null)
                {
                    ReportPdfSurfaceFailure(state, fresh?.CoreWebView2, state.Version, navigationLabel, onActiveFailure);
                }

                return;
            }

            CallOff(why);
        }

        void Swap()
        {
            if (finished || fresh is null)
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

            CancelPendingPdfQueries(state);
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
            state.PendingJumpId = null;
            state.PendingRailJumpTarget = null;
            promoted = true;
            onSwapped?.Invoke();
        }

        fresh = CreateWebView(
            WebSurfaceKind.PdfViewer,
            source,
            navigationLabel,
            onFailed: _ => HandleReplacementFailure("failed to load"),
            configure: core => ConfigurePdfViewer(
                fresh!,
                core,
                pdfPath,
                state,
                ready,
                onError: reason => HandleReplacementFailure($"failed to load ({reason})"),
                allowPasswordReveal: false));
        Grid.SetColumn(fresh, 1);
        fresh.HoldCachePath(pdfPath);
        fresh.Visibility = Visibility.Hidden;
        fresh.AllowNavigation();
        surface.Children.Add(fresh);

        var started = Stopwatch.GetTimestamp();
        _ = CompleteReplacementAsync();

        async Task CompleteReplacementAsync()
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(state.Token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(4000));
                var painted = await ready.Task.WaitAsync(timeout.Token);
                if (finished)
                {
                    return;
                }

                if (!painted)
                {
                    CallOff("failed to load");
                    return;
                }

                if (!FreshIsOwned())
                {
                    DiscardFresh();
                    return;
                }

                DiagnosticLog.Write(
                    $"{logLabel} pdf-first-paint wait={Stopwatch.GetElapsedTime(started).TotalMilliseconds:0}ms paint=measured");
                if (stillWanted is null || await stillWanted())
                {
                    Swap();
                }
                else
                {
                    CallOff("called off");
                }
            }
            catch (OperationCanceledException) when (state.Token.IsCancellationRequested)
            {
                DiscardFresh();
            }
            catch (OperationCanceledException)
            {
                CallOff("not loaded in 4 s");
            }
        }
    }
}
