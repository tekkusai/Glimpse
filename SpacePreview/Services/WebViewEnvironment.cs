using System.IO;
using Microsoft.Web.WebView2.Core;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// One CoreWebView2Environment for the whole app, so previews share one
/// browser and one profile. Holding the environment does not keep the
/// browser running: it exits when its last controller closes. The preview
/// window's WarmBrowser is what keeps it warm between previews.
/// </summary>
internal static class WebViewEnvironment
{
    // Autoplay: media previews must start without a click (inert elsewhere).
    // The proxy is a closed port on this machine and nothing goes around
    // it, loopback included, so no page has a route to a server. The
    // previews' own request handlers cannot promise that: a preconnect is
    // no request, a frame opens its socket before it can be cancelled, and
    // a request still waiting for its handler goes out when the view
    // closes. Every preview is a local file, which no proxy touches.
    private const string BrowserArguments =
        "--autoplay-policy=no-user-gesture-required"
        + " --proxy-server=http://127.0.0.1:9"
        + " --proxy-bypass-list=<-loopback>";

    private static readonly Lazy<Task<CoreWebView2Environment>> Instance = new(CreateAsync);

    public static Task<CoreWebView2Environment> GetAsync() => Instance.Value;

    public static async Task WarmAsync()
    {
        try
        {
            await GetAsync();
            DiagnosticLog.Write("webview environment ready");
        }
        catch (Exception ex)
        {
            // A concrete preview will surface the same failure in context.
            DiagnosticLog.Write($"webview warmup failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Task<CoreWebView2Environment> CreateAsync()
    {
        var userDataFolder = AppPaths.InDataRoot("WebView2");
        Directory.CreateDirectory(userDataFolder);

        return CoreWebView2Environment.CreateAsync(null, userDataFolder, new CoreWebView2EnvironmentOptions(BrowserArguments));
    }
}
