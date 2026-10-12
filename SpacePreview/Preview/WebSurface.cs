using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// How a web preview arrives. A page Glimpse writes paints its own canvas
/// and dissolves itself in over whatever stands under it, so it shows at
/// once on a transparent view. The PDF viewer and a saved web page are
/// opaque: they stage hidden at their real size and appear in one cut.
/// </summary>
internal enum WebSurfaceKind
{
    Page,
    PdfViewer,
    SavedPage
}

/// <summary>
/// WPF's WebView2 tells its browser where it is only while it is visible,
/// so a view staged under a hidden parent lays out at whatever size its
/// controller started with, then refits when it appears: the PDF viewer's
/// double-size flash. This view also pushes its real device-pixel bounds
/// while hidden, so a staged document lays out once, at the size it shows at.
/// </summary>
internal sealed class StagedWebView : WebView2
{
    private readonly TaskCompletionSource _mayNavigate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StagedWebView(WebSurfaceKind kind)
    {
        Kind = kind;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        // A saved page with no background of its own reads as a white page.
        // Everything else paints its own, and shows what stands under it
        // until it does.
        DefaultBackgroundColor = kind == WebSurfaceKind.SavedPage
            ? System.Drawing.Color.White
            : System.Drawing.Color.Transparent;
        CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                PushBoundsWhileHidden();
            }
        };
        SizeChanged += (_, _) => PushBoundsWhileHidden();
        NavigationCompleted += (_, _) => NavigationCompletedAt = Stopwatch.GetTimestamp();
    }

    public WebSurfaceKind Kind { get; }

    /// <summary>
    /// Completes when the view may load its source. A page staged while the
    /// old item leaves starts its engine but waits for this, so its own
    /// fade-in plays where it can be seen.
    /// </summary>
    public Task MayNavigate => _mayNavigate.Task;

    public void AllowNavigation() => _mayNavigate.TrySetResult();

    /// <summary>Stopwatch timestamps for the load breakdown.</summary>
    public long? EngineReady { get; set; }

    public long? NavigationStarted { get; set; }

    public long? NavigationCompletedAt { get; private set; }

    /// <summary>
    /// The same rectangle HwndHost computes when visible: the element's
    /// box in the window's client area, in device pixels, edges rounded.
    /// The base call moves the hidden child HWND without showing it.
    /// </summary>
    public void PushBoundsWhileHidden()
    {
        if (IsVisible
            || CoreWebView2 is null
            || PresentationSource.FromVisual(this) is not { CompositionTarget: { RootVisual: { } root } target }
            || RenderSize.Width < 1
            || RenderSize.Height < 1)
        {
            return;
        }

        var box = TransformToAncestor(root).TransformBounds(new Rect(RenderSize));
        var device = target.TransformToDevice;
        var topLeft = device.Transform(box.TopLeft);
        var bottomRight = device.Transform(box.BottomRight);
        var left = Math.Round(topLeft.X);
        var top = Math.Round(topLeft.Y);
        OnWindowPositionChanged(new Rect(
            left,
            top,
            Math.Max(1, Math.Round(bottomRight.X) - left),
            Math.Max(1, Math.Round(bottomRight.Y) - top)));
    }
}

/// <summary>
/// WebView2 shuts its browser process down when the last controller closes,
/// so without help every Space press after a hide pays a cold browser start
/// (100 to 250 ms). One parked, invisible about:blank controller keeps the
/// browser warm while its window may serve another glance. It costs six
/// processes and about 80 MB, so it starts with the window's first web
/// surface, or once an item shows in a folder that holds a page, and stops
/// when the window is pinned or closed.
/// </summary>
internal sealed class WarmBrowser
{
    private CoreWebView2Controller? _controller;
    private bool _started;
    private int _generation;

    public async void Start(IntPtr parentHwnd)
    {
        if (_started || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        _started = true;
        var generation = _generation;
        try
        {
            var environment = await WebViewEnvironment.GetAsync();
            if (generation != _generation)
            {
                return;
            }

            var controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
            if (generation != _generation)
            {
                controller.Close();
                return;
            }

            controller.IsVisible = false;
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 0, 0);
            WebViewTheme.Apply(controller.CoreWebView2);
            controller.CoreWebView2.Settings.IsScriptEnabled = false;
            controller.CoreWebView2.Navigate("about:blank");
            _controller = controller;
            DiagnosticLog.Write("webview browser parked warm");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"webview warm park failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Lets the browser go; a later Start parks a new controller.</summary>
    public void Stop()
    {
        _generation++;
        _started = false;
        try
        {
            _controller?.Close();
        }
        catch
        {
            // A window already torn down took its child with it.
        }

        _controller = null;
    }
}

internal static class WebViewTheme
{
    /// <summary>
    /// The profile's color scheme follows the app theme, not Windows: the
    /// PDF viewer's toolbar, converted sheets and any page that honors
    /// prefers-color-scheme then match the window around them.
    /// </summary>
    public static void Apply(CoreWebView2 core)
    {
        try
        {
            core.Profile.PreferredColorScheme = Theme.Current.IsDark
                ? CoreWebView2PreferredColorScheme.Dark
                : CoreWebView2PreferredColorScheme.Light;
        }
        catch
        {
            // An older runtime keeps following Windows.
        }
    }
}
