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
    // ---------------------------------------------------------------------
    // WebView2-backed previews share one environment (see WebViewEnvironment).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Untrusted HTML gets Quick Look semantics: no scripts, no network.
    /// One rule (<see cref="IsAllowedUntrustedHtmlResource"/>) judges the
    /// page's navigations, every frame's, and every request any frame
    /// makes: an image, a stylesheet or a tracking pixel must not phone
    /// home from anywhere in the page. The browser's dead proxy
    /// (WebViewEnvironment) stands under all of it.
    /// <paramref name="pagePath"/> is the saved page being shown, and
    /// matters only when it sits on a share.
    /// </summary>
    internal static void HardenForUntrustedHtml(CoreWebView2 core, string? pagePath = null)
    {
        var shareFolder = ShareFolderOf(pagePath);
        core.Settings.IsScriptEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Uri, shareFolder))
            {
                args.Cancel = true;
            }
        };
        core.FrameNavigationStarting += (_, args) =>
        {
            // A srcdoc frame goes too: sandboxed, its requests reach no
            // WebResourceRequested filter, so it must not load at all.
            if (!IsAllowedUntrustedHtmlResource(args.Uri, shareFolder))
            {
                args.Cancel = true;
                DiagnosticLog.Write($"webview blocked frame {DiagnosticLog.DescribeUri(args.Uri)}");
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            DiagnosticLog.Write($"webview blocked popup {DiagnosticLog.DescribeUri(args.Uri)}");
        };
        // The two-argument filter misses every sandboxed frame's requests.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Request.Uri, shareFolder))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"webview blocked remote resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    /// <summary>
    /// What a saved page may load or navigate to: data: URIs and files. A
    /// page on a local disk reads local disks and no share, because a share
    /// is a network request to a host the page chose. A page on a share
    /// (<paramref name="shareFolder"/>, from <see cref="ShareFolderOf"/>)
    /// reads its own folder tree and nothing else, not even a local disk:
    /// previewing it asks its server only for what sits beside it.
    /// </summary>
    internal static bool IsAllowedUntrustedHtmlResource(string uri, string? shareFolder = null)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!parsed.IsFile)
        {
            return false;
        }

        if (shareFolder is not null)
        {
            try
            {
                // GetFullPath folds "..", and cannot climb out of \\server\share.
                return parsed.IsUnc
                    && Path.GetFullPath(parsed.LocalPath).StartsWith(shareFolder, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return !parsed.LocalPath.StartsWith(@"\\", StringComparison.Ordinal);
        }

        return parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The folder tree a saved page on a share may read: its own folder,
    /// ending in a separator. Null for a page on a local disk.
    /// </summary>
    internal static string? ShareFolderOf(string? pagePath)
    {
        try
        {
            return Uri.TryCreate(pagePath, UriKind.Absolute, out var page)
                   && page.IsUnc
                   && Path.GetDirectoryName(Path.GetFullPath(page.LocalPath)) is { Length: > 0 } folder
                ? folder.TrimEnd('\\') + '\\'
                : null;
        }
        catch
        {
            return null;
        }
    }

    internal static bool IsSafeLocalPreviewTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (LooksLikeLocalDrivePath(path))
        {
            return true;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var parsed))
        {
            return parsed.IsFile && IsLocalFileUri(parsed) && LooksLikeLocalDrivePath(LocalPathFromFileUri(parsed));
        }

        return LooksLikeLocalDrivePath(path);
    }

    private static bool IsLocalFileUri(Uri uri)
    {
        if (!uri.IsFile)
        {
            return false;
        }

        return string.IsNullOrEmpty(uri.Host) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalPathFromFileUri(Uri uri)
    {
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            return path.Replace('/', '\\');
        }

        return uri.LocalPath;
    }

    private static bool LooksLikeLocalDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return root is { Length: >= 3 }
            && char.IsLetter(root[0])
            && root[1] == ':'
            && (root[2] == '\\' || root[2] == '/');
    }

    internal enum GeneratedHtmlNavigationAction
    {
        Allow,
        Cancel,
        OpenExternally
    }

    internal static bool IsAllowedGeneratedHtmlResource(string htmlPath, string? resourceHost, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resourceHost)
            && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && parsed.Host.Equals(resourceHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!parsed.IsFile || !IsLocalFileUri(parsed))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(htmlPath),
                Path.GetFullPath(LocalPathFromFileUri(parsed)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static GeneratedHtmlNavigationAction DecideGeneratedHtmlNavigation(string htmlPath, string? resourceHost, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return GeneratedHtmlNavigationAction.Cancel;
        }

        if (parsed.IsFile && IsLocalFileUri(parsed))
        {
            try
            {
                // A heading link is the same page with a fragment.
                var page = new Uri(parsed.GetLeftPart(UriPartial.Path));
                return string.Equals(
                    Path.GetFullPath(htmlPath),
                    Path.GetFullPath(LocalPathFromFileUri(page)),
                    StringComparison.OrdinalIgnoreCase)
                    ? GeneratedHtmlNavigationAction.Allow
                    : GeneratedHtmlNavigationAction.Cancel;
            }
            catch
            {
                return GeneratedHtmlNavigationAction.Cancel;
            }
        }

        if ((parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !parsed.Host.Equals(resourceHost ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return GeneratedHtmlNavigationAction.OpenExternally;
        }

        return GeneratedHtmlNavigationAction.Cancel;
    }

    internal static void HardenForGeneratedHtml(CoreWebView2 core, GeneratedHtmlDocument document)
    {
        core.Settings.IsScriptEnabled = false;
        if (!string.IsNullOrWhiteSpace(document.ResourceHost)
            && !string.IsNullOrWhiteSpace(document.ResourceFolder))
        {
            core.SetVirtualHostNameToFolderMapping(
                document.ResourceHost,
                document.ResourceFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        core.NavigationStarting += (_, args) =>
        {
            switch (DecideGeneratedHtmlNavigation(document.HtmlPath, document.ResourceHost, args.Uri))
            {
                case GeneratedHtmlNavigationAction.Allow:
                    break;
                case GeneratedHtmlNavigationAction.OpenExternally:
                    args.Cancel = true;
                    if (args.IsUserInitiated)
                    {
                        OpenExternalUri(args.Uri);
                    }
                    break;
                default:
                    args.Cancel = true;
                    break;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated
                && DecideGeneratedHtmlNavigation(document.HtmlPath, document.ResourceHost, args.Uri) == GeneratedHtmlNavigationAction.OpenExternally)
            {
                OpenExternalUri(args.Uri);
            }
        };
        // Glimpse writes no frames, so one that appears is not its own.
        core.FrameNavigationStarting += (_, args) =>
        {
            args.Cancel = true;
            DiagnosticLog.Write($"generated html blocked frame {DiagnosticLog.DescribeUri(args.Uri)}");
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedGeneratedHtmlResource(document.HtmlPath, document.ResourceHost, args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"generated html blocked resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    /// <summary>
    /// A window that has shown a page keeps its browser for the next glance.
    /// A load starts it before its page exists: by name before the file is
    /// read, code once a grammar takes it, anything else in CreateWebView.
    /// An item that is not a page starts it once it shows, if its folder
    /// holds one. A pinned window never serves another glance.
    /// </summary>
    private void WarmBrowserForNextGlance()
    {
        if (!IsPinned)
        {
            _warmBrowser.Start(Hwnd);
        }
    }

    // A few thousand names or a fifth of a second, whichever ends first.
    private const int PageScanEntries = 5000;
    private static readonly TimeSpan PageScanTime = TimeSpan.FromMilliseconds(200);

    // The folder or set last asked about, its answer, and a scan still asking.
    private object? _pageScope;
    private bool _pageScopeHoldsPage;
    private CancellationTokenSource? _pageScan;

    /// <summary>
    /// A window that has shown only photos runs no browser, but the next
    /// arrow may land on a page. Once an item that is not a page shows, one
    /// scan off the UI thread asks whether its folder holds one (a
    /// multi-selection asks its set), and the browser starts if it does.
    /// The answer keeps until the folder changes; a hide or close stops the
    /// scan. One scan at a time, so a share that hangs holds one thread.
    /// </summary>
    private async void WarmBrowserForPagesNearby()
    {
        if (IsPinned || _pageScan is not null || BrowseScope() is not { } scope)
        {
            return;
        }

        if (Equals(scope, _pageScope))
        {
            if (_pageScopeHoldsPage)
            {
                WarmBrowserForNextGlance();
            }

            return;
        }

        using var scan = new CancellationTokenSource();
        _pageScan = scan;
        _pageScope = null;
        var holds = await Task.Run(() => HoldsPage(scope, scan.Token));
        _pageScan = null;
        if (scan.IsCancellationRequested)
        {
            return;
        }

        _pageScope = scope;
        _pageScopeHoldsPage = holds;

        // A window that moved on to another folder asks again at its next reveal.
        if (holds && Equals(scope, BrowseScope()))
        {
            WarmBrowserForNextGlance();
        }
    }

    /// <summary>Where the next arrow goes: the selected set, else the shown item's folder.</summary>
    private object? BrowseScope() =>
        CurrentPath is { } path ? _multiSelectionPaths ?? (object?)Path.GetDirectoryName(path) : null;

    /// <summary>
    /// Whether a page opens among the first few thousand files. A folder
    /// that can't be listed holds none.
    /// </summary>
    internal static bool HoldsPage(object scope, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var seen = 0;
        try
        {
            var paths = scope as IReadOnlyList<string> ?? Directory.EnumerateFiles((string)scope, "*", new EnumerationOptions());
            foreach (var path in paths)
            {
                if (FileKinds.OpensAsPage(path))
                {
                    return true;
                }

                if (++seen == PageScanEntries || clock.Elapsed > PageScanTime || token.IsCancellationRequested)
                {
                    return false;
                }
            }
        }
        catch
        {
            // A virtual folder, a zip's inside, a share gone away: no page.
        }

        return false;
    }

    private StagedWebView CreateWebView(
        WebSurfaceKind kind,
        Func<Uri> getSource,
        string diagnosticLabel,
        Action<Exception>? onFailed = null,
        Action<Microsoft.Web.WebView2.Core.CoreWebView2>? configure = null)
    {
        var requestVersion = _loadVersion;
        var requestToken = _loadCancellation?.Token ?? CancellationToken.None;
        var failureReported = 0;
        var initializationStarted = 0;
        var webView = new StagedWebView(kind);
        WarmBrowserForNextGlance();

        bool IsCurrentRequest() =>
            !requestToken.IsCancellationRequested && requestVersion == _loadVersion;

        void ReportFailure(Exception exception)
        {
            if (!IsCurrentRequest())
            {
                return;
            }

            // The PDF viewer is deliberately parked while Fill & Sign owns
            // the stage. Never replace that surface (and its unsaved work)
            // because the hidden browser failed; apply the fallback after
            // the user exits the mode instead.
            if (_fillSignSurface is not null && onFailed is not null)
            {
                _deferredWebFailure = () => ReportFailure(exception);
                DiagnosticLog.Write($"{diagnosticLabel} failure deferred during fill-sign");
                return;
            }

            if (Interlocked.Exchange(ref failureReported, 1) != 0)
            {
                return;
            }

            DiagnosticLog.Write($"{diagnosticLabel} failed: {exception.GetType().Name}: {exception.Message}");
            onFailed?.Invoke(exception);
        }

        var sourceAssigned = false;
        var initialNavigationPending = true;
        webView.NavigationCompleted += (_, args) =>
        {
            if (!sourceAssigned || !initialNavigationPending)
            {
                return;
            }

            initialNavigationPending = false;
            if (!args.IsSuccess)
            {
                ReportFailure(new InvalidOperationException($"navigation failed ({args.WebErrorStatus})"));
            }
        };

        webView.Loaded += async (_, _) =>
        {
            try
            {
                if (!IsCurrentRequest())
                {
                    return;
                }

                // Loaded can fire again when a PDF is parked for Fill & Sign
                // and then restored, or when a view staged during the beat
                // moves into place. Reconfiguring would duplicate every
                // handler and reset the viewer back to its original source.
                if (Interlocked.CompareExchange(ref initializationStarted, 1, 0) != 0)
                {
                    return;
                }

                // The source is built off the UI thread while the engine
                // starts: media pages read shell metadata and artwork.
                var source = Task.Run(getSource);
                var environment = await WebViewEnvironment.GetAsync();
                if (!IsCurrentRequest())
                {
                    return;
                }

                await webView.EnsureCoreWebView2Async(environment);
                webView.EngineReady = Stopwatch.GetTimestamp();
                if (!IsCurrentRequest())
                {
                    return;
                }

                // Right-click shows the preview's own menu; the PDF viewer
                // keeps Chromium's (see OnWebContextMenuRequested).
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                WebViewTheme.Apply(webView.CoreWebView2);
                RememberWebViewProcesses(webView.CoreWebView2);
                webView.CoreWebView2.ContextMenuRequested += (_, args) => OnWebContextMenuRequested(webView, args);
                webView.CoreWebView2.ProcessFailed += (_, args) =>
                {
                    if (ProcessFailureLosesPage(args.ProcessFailedKind, kind))
                    {
                        ReportFailure(new InvalidOperationException($"browser process failed ({args.ProcessFailedKind})"));
                    }
                    else
                    {
                        DiagnosticLog.Write($"browser process failed, page kept ({args.ProcessFailedKind}): {diagnosticLabel}");
                    }
                };
                configure?.Invoke(webView.CoreWebView2);
                var uri = await source;
                await webView.MayNavigate;
                if (!IsCurrentRequest())
                {
                    return;
                }

                sourceAssigned = true;
                webView.NavigationStarted = Stopwatch.GetTimestamp();
                webView.Source = uri;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        };

        return webView;
    }

    /// <summary>
    /// Whether a failed browser process took the page with it. The browser
    /// starts a new GPU or utility process by itself and an unresponsive
    /// renderer may answer again, so a driver reset must not turn a pinned
    /// preview into an error card. A frame's process takes only that frame,
    /// except in the PDF viewer, whose document may be drawn in one: there
    /// it stays the failure it was.
    /// </summary>
    internal static bool ProcessFailureLosesPage(CoreWebView2ProcessFailedKind failure, WebSurfaceKind surface) => failure switch
    {
        CoreWebView2ProcessFailedKind.BrowserProcessExited or CoreWebView2ProcessFailedKind.RenderProcessExited => true,
        CoreWebView2ProcessFailedKind.FrameRenderProcessExited => surface == WebSurfaceKind.PdfViewer,
        _ => false
    };

    /// <summary>
    /// A saved page is somebody else's document: opaque on white like a
    /// browser, staged hidden and shown in one cut once laid out.
    /// </summary>
    private FrameworkElement BuildHtmlPreview(string path)
    {
        var grid = new Grid();
        grid.Children.Add(CreateWebView(
            WebSurfaceKind.SavedPage,
            () => new Uri(path, UriKind.Absolute),
            $"html preview {path}",
            ex => _ = ShowTextOrFileFallbackAsync(path),
            configure: core =>
            {
                HardenForUntrustedHtml(core, path);
                NormalizeHtmlPageScrolling(core);
            }));
        return grid;
    }

    private FrameworkElement BuildGeneratedHtmlPreview(GeneratedHtmlDocument document, string label)
    {
        // Transparent, so the page dissolves in over whatever stands under it.
        var grid = new Grid();
        grid.Children.Add(CreateWebView(
            WebSurfaceKind.Page,
            () => document.Source,
            label,
            ex => _ = ShowTextOrFileFallbackAsync(document.SourcePath),
            configure: core => HardenForGeneratedHtml(core, document)));
        return grid;
    }

    /// <summary>
    /// Saved pages scroll arbitrarily, and with their scripts off their own
    /// scroll logic is dead. After each load: unlock the root only when
    /// content is trapped and nothing else already scrolls it (otherwise a
    /// phantom root bar appears over the real one), collapse an html+body
    /// double scroller only when both really scroll, style every bar through
    /// the standard scrollbar properties (they override a page's own styling
    /// where the webkit pseudo-elements do not), and follow the page's actual
    /// paint so dark pages get dark bars. ExecuteScriptAsync ignores the
    /// page-script setting, so this still runs on hardened previews.
    /// </summary>
    private static void NormalizeHtmlPageScrolling(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        const string script = """
            (() => {
              const style = document.createElement('style');
              style.textContent = '*{scrollbar-width:thin!important;scrollbar-color:rgba(128,128,128,.6) transparent!important}';
              (document.head || document.documentElement).appendChild(style);

              const de = document.documentElement;
              const body = document.body;

              // Viewport-sized scrollers, per axis: the page's own way of
              // reaching its content.
              let scrollerX = null;
              let scrollerY = null;
              if (body) {
                const all = body.querySelectorAll('*');
                const count = Math.min(all.length, 600);
                for (let i = 0; i < count; i++) {
                  const el = all[i];
                  const cs = getComputedStyle(el);
                  const scrollsY = (cs.overflowY === 'auto' || cs.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 2;
                  const scrollsX = (cs.overflowX === 'auto' || cs.overflowX === 'scroll') && el.scrollWidth > el.clientWidth + 2;
                  if (!scrollsY && !scrollsX) continue;
                  const box = el.getBoundingClientRect();
                  if (box.width < window.innerWidth * 0.8 || box.height < window.innerHeight * 0.8) continue;
                  // A scroller whose own bar sits outside the viewport (a
                  // layout wider or taller than the window) cannot be used;
                  // release that axis so the content flows and the ROOT
                  // scrolls it, with the bar at the viewport edge.
                  if (scrollsY) {
                    if (box.right > window.innerWidth + 8) {
                      el.style.setProperty('overflow-y', 'visible', 'important');
                    } else if (!scrollerY) {
                      scrollerY = el;
                    }
                  }
                  if (scrollsX) {
                    if (box.bottom > window.innerHeight + 8) {
                      el.style.setProperty('overflow-x', 'visible', 'important');
                    } else if (!scrollerX) {
                      scrollerX = el;
                    }
                  }
                  if (scrollerX && scrollerY) break;
                }
              }

              // Trapped content is unlocked per axis: a hidden root under-
              // reports its size, so the body measures too. Only when no
              // viewport scroller already handles that axis.
              const deStyle = getComputedStyle(de);
              const bodyStyle = body ? getComputedStyle(body) : null;
              const contentWidth = Math.max(de.scrollWidth, body ? body.scrollWidth : 0);
              const contentHeight = Math.max(de.scrollHeight, body ? body.scrollHeight : 0);

              if (contentWidth > de.clientWidth + 2 && !scrollerX) {
                if (deStyle.overflowX === 'hidden') de.style.setProperty('overflow-x', 'auto', 'important');
                if (bodyStyle && bodyStyle.overflowX === 'hidden') body.style.setProperty('overflow-x', 'visible', 'important');
              }
              if (contentHeight > de.clientHeight + 2 && !scrollerY) {
                if (deStyle.overflowY === 'hidden') de.style.setProperty('overflow-y', 'auto', 'important');
                if (bodyStyle && bodyStyle.overflowY === 'hidden') body.style.setProperty('overflow-y', 'visible', 'important');
              }

              // html and body both scrolling = two bars for one page; body yields.
              if (body) {
                const deNow = getComputedStyle(de);
                const bodyNow = getComputedStyle(body);
                const deScrolls = deNow.overflowY === 'auto' || deNow.overflowY === 'scroll';
                const bodyScrolls = (bodyNow.overflowY === 'auto' || bodyNow.overflowY === 'scroll')
                  && body.scrollHeight > body.clientHeight + 2;
                if (deScrolls && bodyScrolls) {
                  body.style.setProperty('overflow', 'visible', 'important');
                }
              }

              // Dead page scripts leave layout slop: a root that overflows by
              // a sliver next to a real full-viewport scroller produces a
              // phantom bar with a few pixels of travel, drawn over the real
              // one. Clip the sliver; keep real root content.
              const rangeY = de.scrollHeight - de.clientHeight;
              if (scrollerY && rangeY > 0 && rangeY <= 32 && getComputedStyle(de).overflowY !== 'hidden') {
                de.style.setProperty('overflow-y', 'hidden', 'important');
              }
              const rangeX = de.scrollWidth - de.clientWidth;
              if (scrollerX && rangeX > 0 && rangeX <= 32 && getComputedStyle(de).overflowX !== 'hidden') {
                de.style.setProperty('overflow-x', 'hidden', 'important');
              }

              // Native bars follow the declared color scheme; apps paint their
              // darkness on wrappers as often as on the body, so look there too.
              const opaque = el => {
                if (!el) return null;
                const c = getComputedStyle(el).backgroundColor;
                const m = c && c.match(/rgba?\((\d+)[, ]+(\d+)[, ]+(\d+)(?:[, ]+([\d.]+))?\)/);
                if (!m) return null;
                const alpha = m[4] === undefined ? 1 : parseFloat(m[4]);
                return alpha > 0.5 ? [+m[1], +m[2], +m[3]] : null;
              };
              const rgb = opaque(body) || opaque(de) || opaque(scrollerY)
                || opaque(body ? body.firstElementChild : null);
              if (rgb) {
                const luminance = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2];
                de.style.colorScheme = luminance < 100 ? 'dark' : 'light';
              }
            })();
            """;

        core.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(script);
            }
            catch
            {
                // A page that refuses normalization still previews as-is.
            }
        };
    }

    private static void OpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // The shell owns this handoff; the preview stays put either way.
        }
    }
}
