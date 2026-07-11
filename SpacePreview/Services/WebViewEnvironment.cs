using System.IO;
using Microsoft.Web.WebView2.Core;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// One CoreWebView2Environment for the whole app. Creating an environment per preview
/// paid the browser-process handshake on every Space press; sharing it makes repeat
/// previews land on a warm runtime.
/// </summary>
internal static class WebViewEnvironment
{
    private static readonly Lazy<Task<CoreWebView2Environment>> Instance = new(CreateAsync);

    public static Task<CoreWebView2Environment> GetAsync() => Instance.Value;

    public static async Task WarmAsync()
    {
        try
        {
            await GetAsync();
            DiagnosticLog.Write("webview environment warm");
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

        // Autoplay: media previews must start without a click (inert elsewhere).
        // Scrollbar style is chosen per view (CoreWebView2ControllerOptions):
        // overlay bars for HTML pages, classic
        // grabbable bars where the user navigates documents (PDF viewer).
        var options = new CoreWebView2EnvironmentOptions(
            "--autoplay-policy=no-user-gesture-required");
        return CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
    }
}
