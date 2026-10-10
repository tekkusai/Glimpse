using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
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
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.CanvasBg) };

        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var background = System.Drawing.Color.FromArgb(palette.CanvasBg.R, palette.CanvasBg.G, palette.CanvasBg.B);
        // FitH = fit the page to the window's width, the way PDF apps open
        // documents. The viewer's default fits the whole page instead, which
        // renders portrait documents at ~two-thirds size.
        var webView = CreateWebView(
            background,
            () => PdfViewerSource(path),
            $"pdf preview {path}",
            ex => { _ = PdfWebFallbackAsync(path, version, token); },
            configure: core => ConfigurePdfViewer(core, path),
            readinessMode: WebPaintReadinessMode.PdfPlugin);
        Grid.SetColumn(webView, 1);
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);
        grid.Children.Add(webView);
        var railState = new PdfPageRailState(railHost, railColumn, webView, path, version, token) { Surface = grid };
        grid.Tag = railState;
        TrackPdfRailLifetime(grid, railState);
        _ = LoadPdfPageRailAsync(railState);
        return grid;
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
        public PdfPreviewDocument? Document { get; set; }
        public Func<PdfPageRailState, int, bool>? TryHandleJump { get; set; }
        public double RailVerticalOffset { get; set; }
        public bool RailNeedsSelectedPageScroll { get; set; }
        public Dictionary<int, Border> PageTiles { get; } = [];
        public Border? SelectedPageTile { get; set; }
        public ScrollViewer? PageRailScrollViewer { get; set; }
        public DispatcherTimer? PageSyncTimer { get; set; }
        public Task<PdfPageAutomationProbeResult>? PageSyncProbeTask { get; set; }
        public bool PageSyncInFlight { get; set; }
        public bool PageSyncDisabled { get; set; }
        public long PageSyncGeneration { get; set; }
        public string? LastPageSource { get; set; }
        public System.Windows.Automation.AutomationElement? PageAutomationElement { get; set; }
        public TaskCompletionSource<bool> FirstRailReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct PdfPageAutomationProbeResult(
        int Page,
        System.Windows.Automation.AutomationElement? PageElement);

    private static ContentControl BuildPdfPageRailHost()
    {
        return new ContentControl
        {
            Width = 0,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
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
            return;
        }

        var first = document.Pages[0];
        if (first.Width < 1 || first.Height < 1)
        {
            return;
        }

        SetStageFitHint(StageFitHint.PageAspect((double)first.Width / first.Height), state.Version);
    }

    // Direct PDFs use the same short-lived helper as converted Office PDFs.
    // A malformed or incompatible document can crash native PDFium, but it
    // must never take the resident preview process down with it.
    private Task LoadPdfPageRailAsync(PdfPageRailState state) =>
        LoadPdfPageRailOutOfProcessAsync(state);

    private Task LoadPdfPageRailOutOfProcessAsync(PdfPageRailState state) =>
        LoadPdfPageRailCoreAsync(
            state,
            (pageLimit, startPage) => TryRenderPdfThumbnailsOutOfProcessAsync(
                state.Path,
                pageLimit,
                state.Token,
                startPage),
            () => TryRenderPdfThumbnailsOutOfProcessAsync(
                state.Path,
                PdfPageRailMaxPages,
                state.Token),
            "pdf page rail helper");

    private async Task LoadPdfPageRailCoreAsync(
        PdfPageRailState state,
        Func<int, int, Task<PdfPreviewDocument?>> renderRange,
        Func<Task<PdfPreviewDocument?>> renderFallback,
        string diagnosticName)
    {
        try
        {
            var firstPages = await renderRange(PdfPageRailInitialPages, 0);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (firstPages is null || firstPages.TotalPages <= 1)
            {
                state.FirstRailReady.TrySetResult(true);
                HidePdfPageRail(state);
                return;
            }

            state.Document = firstPages;
            HintStageFromRailTruth(state, firstPages);
            RenderPdfPageRail(state);
            state.FirstRailReady.TrySetResult(true);

            if (firstPages.TotalPages <= firstPages.Pages.Count)
            {
                return;
            }

            var remainingPages = await renderRange(
                PdfPageRailMaxPages - firstPages.Pages.Count,
                firstPages.Pages.Count);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (remainingPages is null || remainingPages.TotalPages <= 1)
            {
                var allPages = await renderFallback();
                if (state.Version != _loadVersion
                    || state.Token.IsCancellationRequested
                    || allPages is null
                    || allPages.TotalPages <= 1)
                {
                    return;
                }

                state.Document = allPages;
            }
            else
            {
                state.Document = CombinePdfPreviewDocuments(firstPages, remainingPages);
            }

            RenderPdfPageRail(state);
        }
        catch (OperationCanceledException)
        {
            state.FirstRailReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            state.FirstRailReady.TrySetResult(true);
            DiagnosticLog.Write(
                $"{diagnosticName} failed {state.Path}: {ex.GetType().Name}: {ex.Message}");
            if (state.Version == _loadVersion)
            {
                HidePdfPageRail(state);
            }
        }
    }

    private static PdfPreviewDocument CombinePdfPreviewDocuments(PdfPreviewDocument first, PdfPreviewDocument remaining)
    {
        var pages = new List<PdfPreviewPage>(first.Pages.Count + remaining.Pages.Count);
        pages.AddRange(first.Pages);
        pages.AddRange(remaining.Pages);
        return new PdfPreviewDocument(pages, Math.Max(first.TotalPages, remaining.TotalPages));
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

        var helper = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(helper) || !File.Exists(helper))
        {
            return null;
        }

        var root = Path.GetDirectoryName(cacheDir) ?? AppPaths.InDataRoot("PdfRail");
        Directory.CreateDirectory(root);
        var tempDir = Path.Combine(root, ".render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--render-pdf-thumbnails");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(tempDir);
            startInfo.ArgumentList.Add(pageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(startPage.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // The helper rasters every page while Chromium is loading the
            // same document and the entrance is animating. It has until the
            // reveal gate; it must never win CPU from the frames the user is
            // watching.
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
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                DiagnosticLog.Write($"pdf thumbnail helper exit {process.ExitCode} for {path}");
                return null;
            }

            var rendered = await Task.Run(() => TryReadPdfThumbnailManifest(tempDir), token);
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

    private static PdfPreviewDocument? TryReadPdfThumbnailManifest(string directory)
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

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The helper already ended.
        }
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
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;

        var content = state.IsVisible
            ? BuildPdfPageRail(
                document,
                state.CurrentPage,
                state.RailVerticalOffset,
                offset => state.RailVerticalOffset = offset,
                page =>
                {
                    SetPdfCurrentPageFromViewer(state, page, logChange: false);
                    if (state.TryHandleJump?.Invoke(state, page) == true)
                    {
                        return;
                    }

                    JumpPdfToPage(state, page);
                },
                (page, tile, selected) =>
                {
                    state.PageTiles[page] = tile;
                    if (selected)
                    {
                        state.SelectedPageTile = tile;
                    }
                },
                scroll => state.PageRailScrollViewer = scroll,
                state.RailNeedsSelectedPageScroll,
                tile =>
                {
                    BringPdfPageTileIntoView(state, tile);
                    state.RailNeedsSelectedPageScroll = false;
                },
                () =>
                {
                    state.IsVisible = false;
                    RenderPdfPageRail(state, cloakViewer: true);
                })
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

    private static void AnimateRail(ContentControl host, double targetWidth, FrameworkElement? content)
    {
        host.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (content is not null)
        {
            content.Opacity = 0;
            host.Content = content;
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else if (targetWidth < 1)
        {
            host.Content = null;
        }

        host.Width = targetWidth;
    }

    private void CloakViewerForRailResize(WebView2 webView, long version)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='none';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            return;
        }

        RunAfter(TimeSpan.FromMilliseconds(90), () =>
        {
            if (version != _loadVersion || webView.CoreWebView2 is not { } currentCore)
            {
                return;
            }

            try
            {
                _ = currentCore.ExecuteScriptAsync(
                    "document.documentElement.style.transition='opacity 120ms ease-out';"
                    + "document.documentElement.style.opacity='1';");
            }
            catch
            {
                // The resize itself is still valid; the script is just polish.
            }
        });
    }

    private void HidePdfPageRail(PdfPageRailState state)
    {
        state.PageTiles.Clear();
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;
        state.RailColumn.Width = new GridLength(0);
        AnimateRail(state.Host, 0, null);
    }

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

        var ownsProbe = false;
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

            if (state.PageSyncInFlight
                || state.PageSyncDisabled
                || state.PageSyncProbeTask is { IsCompleted: false })
            {
                return;
            }

            var totalPages = state.Document?.TotalPages ?? 0;
            var hwnd = TryGetPdfAutomationHwnd(state.WebView);
            if (totalPages <= 1 || hwnd == IntPtr.Zero)
            {
                return;
            }

            var generation = state.PageSyncGeneration;
            var cachedElement = state.PageAutomationElement;
            state.PageSyncInFlight = true;
            ownsProbe = true;

            // UI Automation crosses into Chromium's PDF provider and can
            // block while a large document is scrolling or zooming. Never
            // let that provider own the WPF dispatcher.
            var probeTask = Task.Run(() =>
            {
                try
                {
                    return TryReadPdfPageFromAutomation(hwnd, totalPages, cachedElement);
                }
                catch
                {
                    return default;
                }
            });
            state.PageSyncProbeTask = probeTask;

            var timeoutTask = Task.Delay(PdfPageAutomationTimeout);
            if (await Task.WhenAny(probeTask, timeoutTask) != probeTask)
            {
                // UIA calls cannot be cancelled safely. Keep the single
                // worker isolated and stop polling this viewer rather than
                // stacking more blocked probes behind it.
                if (generation == state.PageSyncGeneration
                    && state.Version == _loadVersion
                    && !state.Token.IsCancellationRequested)
                {
                    state.PageSyncDisabled = true;
                    DiagnosticLog.Write(
                        $"pdf page sync disabled after {PdfPageAutomationTimeout.TotalSeconds:0}s timeout");
                }

                return;
            }

            var result = await probeTask;
            if (generation != state.PageSyncGeneration
                || state.Version != _loadVersion
                || state.Token.IsCancellationRequested)
            {
                return;
            }

            state.PageAutomationElement = result.PageElement;
            if (result.Page > 0)
            {
                SetPdfCurrentPageFromViewer(state, result.Page);
            }
        }
        catch
        {
            state.PageAutomationElement = null;
        }
        finally
        {
            if (ownsProbe)
            {
                state.PageSyncInFlight = false;
            }
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

        var palette = Theme.Current;
        if (state.SelectedPageTile is not null)
        {
            ApplyRailTileState(state.SelectedPageTile, palette, selected: false);
        }

        state.SelectedPageTile = null;
        if (state.PageTiles.TryGetValue(target, out var tile))
        {
            state.SelectedPageTile = tile;
            ApplyRailTileState(tile, palette, selected: true);

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

    private static FrameworkElement BuildPdfPageRail(
        PdfPreviewDocument document,
        int selectedPage,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<int> navigate,
        Action<int, Border, bool> registerTile,
        Action<ScrollViewer> registerScroll,
        bool alignSelectedOnLoad,
        Action<FrameworkElement> alignLoadedSelection,
        Action hide)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        FrameworkElement? pageToRevealOnLoad = null;

        var header = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Pages",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        var hideButton = BuildRailChromeButton("\uE76B", "Hide pages", hide);
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var pageNumber = i + 1;
            var page = document.Pages[i];
            var thumbnail = new Image
            {
                Source = page.Bitmap,
                Width = Math.Min(92, page.Width),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(thumbnail, BitmapScalingMode.HighQuality);

            var imageFrame = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Brush(Color.FromArgb(90, palette.Border.R, palette.Border.G, palette.Border.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Child = thumbnail
            };

            var label = new TextBlock
            {
                Text = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary),
                FontSize = 11
            };

            var tile = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(7),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = $"Go to page {pageNumber}",
                Child = new StackPanel { Children = { imageFrame, label } }
            };

            var selected = pageNumber == selectedPage;
            ConfigureRailTile(tile, pageNumber, selected, palette, navigate, registerTile);
            if (selected)
            {
                pageToRevealOnLoad = tile;
            }

            stack.Children.Add(tile);
        }

        if (document.TotalPages > document.Pages.Count)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"First {document.Pages.Count} of {document.TotalPages}",
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 0)
            });
        }

        return BuildRailSurface(
            header,
            stack,
            verticalOffset,
            rememberScroll,
            registerScroll,
            scroll =>
            {
                if (!alignSelectedOnLoad || pageToRevealOnLoad is not { } tile)
                {
                    scroll.ScrollToVerticalOffset(verticalOffset);
                    return;
                }

                // The saved offset is stale when the viewer moved while the
                // rail was hidden; reopen around the current page instead.
                _ = scroll.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() => alignLoadedSelection(tile)));
            });
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
    /// document reload. UI Automation reaches into Chromium's PDF provider and
    /// can block, so it runs off the dispatcher under the same timeout the
    /// page sync uses.
    /// </summary>
    private async Task<bool> LandedOnPdfPageAsync(PdfPageRailState state, int target)
    {
        if (state.Document is not { TotalPages: > 1 } document)
        {
            return true;
        }

        var hwnd = TryGetPdfAutomationHwnd(state.WebView);
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        var element = state.PageAutomationElement;
        var probe = Task.Run(() =>
        {
            try
            {
                return TryReadPdfPageFromAutomation(hwnd, document.TotalPages, element);
            }
            catch
            {
                return default;
            }
        });

        if (await Task.WhenAny(probe, Task.Delay(PdfPageAutomationTimeout)) != probe)
        {
            return true;
        }

        var result = await probe;
        if (result.PageElement is not null)
        {
            state.PageAutomationElement = result.PageElement;
        }

        return result.Page <= 0 || result.Page == target;
    }

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
    /// the old viewer stays visible until the new one has loaded.
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

        var palette = Theme.Current;
        var background = System.Drawing.Color.FromArgb(
            palette.CanvasBg.R,
            palette.CanvasBg.G,
            palette.CanvasBg.B);
        var fresh = CreateWebView(
            background,
            source,
            navigationLabel,
            onFailed: null,
            configure: core => ConfigurePdfViewer(core, pdfPath),
            trackAsPreviewDisposable: false,
            readinessMode: WebPaintReadinessMode.PdfPlugin);
        Grid.SetColumn(fresh, 1);
        fresh.Visibility = Visibility.Hidden;
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
            var readinessMode = PaintReadinessMode(fresh);
            var paintReady = await WaitForWebPaintReadyAsync(
                fresh,
                PdfPluginSettleDelay,
                state.Token,
                readinessMode);
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
                $"{paintLabel} paint gate ready={paintReady} wait={paintClock.Elapsed.TotalMilliseconds:0}ms mode={PaintReadinessModeName(readinessMode)}");
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

    private async Task PdfWebFallbackAsync(string path, long version, CancellationToken token)
    {
        try
        {
            var content = await BuildPdfViaPdfiumAsync(path, token);
            if (version != _loadVersion)
            {
                return;
            }

            SetPreviewContent(content, null, animate: false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer preview; nothing to render.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf fallback failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<FrameworkElement> BuildPdfViaPdfiumAsync(string path, CancellationToken token)
    {
        var pdf = await Task.Run(() => PdfPreviewRenderer.TryRender(path, token), token);
        return pdf is null
            ? BuildErrorPreview("Glimpse can't preview this PDF", "It may be damaged or in use.")
            : BuildPdfPreview(pdf);
    }

    // ---------------------------------------------------------------------
    // Static content builders. All colors come from the active theme.
    // ---------------------------------------------------------------------

    private static FrameworkElement BuildPdfPreview(PdfPreviewDocument document)
    {
        var palette = Theme.Current;
        var stack = new StackPanel
        {
            Margin = new Thickness(0, 22, 0, 22),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];
            var maxWidth = 940.0;
            stack.Children.Add(new Border
            {
                MaxWidth = maxWidth,
                Width = Math.Min(maxWidth, page.Width),
                Margin = new Thickness(24, i == 0 ? 0 : 20, 24, 0),
                Background = Brushes.White,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = palette.IsDark ? 0.24 : 0.16
                },
                Child = new Image
                {
                    Source = page.Bitmap,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                }
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Page {i + 1} of {document.TotalPages}",
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        if (document.TotalPages > document.Pages.Count)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Showing first {document.Pages.Count} pages",
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                Foreground = Theme.Brush(palette.TextSecondary)
            });
        }

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.CanvasBg);
    }
}
