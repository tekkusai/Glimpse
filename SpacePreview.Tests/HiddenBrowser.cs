using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace SpacePreview.Tests;

/// <summary>
/// A real browser for a test, on a window that is never shown. Tests that
/// use it run on the WPF test application's thread. A view nobody sees is
/// never laid out, so a page loaded here must not wait on layout: an
/// object or embed element starts its fetch after layout and holds the
/// page's load back until then, which here is forever.
/// </summary>
internal static class HiddenBrowser
{
    private static readonly Lazy<Task<CoreWebView2Environment>> Direct = new(() => CoreWebView2Environment.CreateAsync(
        null,
        Path.Combine(TestDataRoot.RunRoot, "WebView2-direct"),
        new CoreWebView2EnvironmentOptions()));

    /// <summary>
    /// A browser started with no arguments of ours, so nothing stands under
    /// a page's handlers: a request they miss really leaves.
    /// </summary>
    public static Task<CoreWebView2Environment> DirectEnvironment => Direct.Value;

    /// <summary>
    /// Loads a page and answers <paramref name="whenLoaded"/> while it is
    /// still open.
    /// </summary>
    public static async Task<T> LoadAsync<T>(
        CoreWebView2Environment environment,
        Uri page,
        Action<CoreWebView2> configure,
        Func<CoreWebView2, Task<T>> whenLoaded)
    {
        // A browser exits a moment after its last view closes. A view made
        // in that moment lands on the leaving browser and never loads.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (environment.GetProcessInfos().Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "An earlier browser never exited.");
            await Task.Delay(50);
        }

        var window = new Window { Left = -30000, Top = 0, Width = 800, Height = 600, ShowInTaskbar = false };
        var controller = await environment.CreateCoreWebView2ControllerAsync(new WindowInteropHelper(window).EnsureHandle());
        try
        {
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 800, 600);
            var core = controller.CoreWebView2;
            configure(core);
            await NavigateAsync(core, page);
            return await whenLoaded(core);
        }
        finally
        {
            controller.Close();
            window.Close();
        }
    }

    /// <summary>Navigates and returns once the page, its frames and its pictures have loaded or failed.</summary>
    public static async Task NavigateAsync(CoreWebView2 core, Uri page)
    {
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnLoaded(object? sender, CoreWebView2NavigationCompletedEventArgs args) => loaded.TrySetResult();
        core.NavigationCompleted += OnLoaded;
        try
        {
            core.Navigate(page.AbsoluteUri);
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        finally
        {
            core.NavigationCompleted -= OnLoaded;
        }
    }
}
