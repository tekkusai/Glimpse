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
    /// Navigation blocking alone is not enough; subresources (an image, a
    /// stylesheet, a tracking pixel) must not phone home either. Local data
    /// stays allowed: file content and data: URIs. UNC-shaped file URLs are
    /// blocked because they are network requests, not local files.
    /// </summary>
    private static void HardenForUntrustedHtml(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        core.Settings.IsScriptEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Uri))
            {
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            DiagnosticLog.Write($"webview blocked popup {DiagnosticLog.DescribeUri(args.Uri)}");
        };
        core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"webview blocked remote resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    internal static bool IsAllowedUntrustedHtmlResource(string uri)
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

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return !parsed.LocalPath.StartsWith(@"\\", StringComparison.Ordinal);
        }

        return parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
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
                return string.Equals(
                    Path.GetFullPath(htmlPath),
                    Path.GetFullPath(LocalPathFromFileUri(parsed)),
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

    private static void HardenForGeneratedHtml(CoreWebView2 core, GeneratedHtmlDocument document)
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
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedGeneratedHtmlResource(document.HtmlPath, document.ResourceHost, args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"generated html blocked resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    private enum WebPaintReadinessMode
    {
        AnimationFrames,
        StaticLayout,
        PdfPlugin
    }

    private static WebPaintReadinessMode PaintReadinessMode(WebView2 webView) =>
        webView.Tag is WebPaintReadinessMode mode ? mode : WebPaintReadinessMode.AnimationFrames;

    private static string PaintReadinessModeName(WebPaintReadinessMode mode) => mode switch
    {
        WebPaintReadinessMode.StaticLayout => "layout",
        WebPaintReadinessMode.PdfPlugin => "pdf-plugin",
        _ => "frames"
    };

    private WebView2 CreateWebView(
        System.Drawing.Color background,
        Func<Uri> getSource,
        string diagnosticLabel,
        Action<Exception>? onFailed = null,
        Action<Microsoft.Web.WebView2.Core.CoreWebView2>? configure = null,
        bool trackAsPreviewDisposable = true,
        WebPaintReadinessMode readinessMode = WebPaintReadinessMode.AnimationFrames)
    {
        var requestVersion = _loadVersion;
        var requestToken = _loadCancellation?.Token ?? CancellationToken.None;
        var failureReported = 0;
        var initializationStarted = 0;
        var webView = new WebView2
        {
            DefaultBackgroundColor = background,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = readinessMode
        };
        if (trackAsPreviewDisposable)
        {
            _nextDisposable = webView;
        }

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
                // and then restored. Reconfiguring would duplicate every
                // handler and reset the viewer back to its original source.
                if (Interlocked.CompareExchange(ref initializationStarted, 1, 0) != 0)
                {
                    return;
                }

                var environment = await WebViewEnvironment.GetAsync();
                if (!IsCurrentRequest())
                {
                    return;
                }

                await webView.EnsureCoreWebView2Async(environment);
                if (!IsCurrentRequest())
                {
                    return;
                }

                // Right-click shows the preview's own menu; the PDF viewer
                // keeps Chromium's (see OnWebContextMenuRequested).
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                RememberWebViewProcesses(webView.CoreWebView2);
                webView.CoreWebView2.ContextMenuRequested += (_, args) => OnWebContextMenuRequested(webView.CoreWebView2, args);
                webView.CoreWebView2.ProcessFailed += (_, args) =>
                    ReportFailure(new InvalidOperationException($"browser process failed ({args.ProcessFailedKind})"));
                configure?.Invoke(webView.CoreWebView2);
                if (!IsCurrentRequest())
                {
                    return;
                }

                var source = getSource();
                sourceAssigned = true;
                webView.Source = source;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        };

        return webView;
    }

    private FrameworkElement BuildHtmlPreview(string path)
    {
        var grid = new Grid { Background = Brushes.White };
        grid.Children.Add(CreateWebView(
            System.Drawing.Color.White,
            () => new Uri(path, UriKind.Absolute),
            $"html preview {path}",
            ex => _ = ShowTextOrFileFallbackAsync(path),
            configure: core =>
            {
                HardenForUntrustedHtml(core);
                NormalizeHtmlPageScrolling(core);
            },
            readinessMode: WebPaintReadinessMode.StaticLayout));
        return grid;
    }

    private FrameworkElement BuildGeneratedHtmlPreview(GeneratedHtmlDocument document, string label)
    {
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.CanvasBg) };
        grid.Children.Add(CreateWebView(
            document.Background,
            () => document.Source,
            label,
            ex => _ = ShowTextOrFileFallbackAsync(document.SourcePath),
            configure: core => HardenForGeneratedHtml(core, document),
            readinessMode: WebPaintReadinessMode.StaticLayout));
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
              style.textContent = '*{scrollbar-width:thin!important;scrollbar-color:rgba(128,134,148,.65) transparent!important}';
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

    /// <summary>
    /// One temp file per source: rapid A-B-A scrubbing must not read a file
    /// another preview just overwrote. The directory is pruned when it grows.
    /// </summary>
    internal static string TempHtmlPath(string folder, string sourcePath)
    {
        var htmlDir = AppPaths.InDataRoot(folder);
        Directory.CreateDirectory(htmlDir);
        try
        {
            PruneTempHtmlDirectory(htmlDir);
        }
        catch
        {
            // Pruning is best-effort housekeeping.
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant())))[..16];
        return Path.Combine(htmlDir, hash + ".html");
    }

    internal static void PruneTempHtmlDirectory(string htmlDir)
    {
        const int maxFiles = 128;
        var keepNewerThan = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        var files = new DirectoryInfo(htmlDir)
            .EnumerateFiles("*.html")
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var extraCount = files.Count - maxFiles;
        if (extraCount <= 0)
        {
            return;
        }

        foreach (var file in files)
        {
            if (extraCount <= 0)
            {
                return;
            }

            if (file.LastWriteTimeUtc > keepNewerThan)
            {
                continue;
            }

            try
            {
                file.Delete();
                extraCount--;
            }
            catch
            {
                // A file still in use by a live preview stays.
            }
        }
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
