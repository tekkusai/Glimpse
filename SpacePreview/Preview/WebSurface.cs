using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// How a web preview arrives. A page Glimpse writes paints its own canvas
/// and dissolves itself in over whatever stands under it, so it attaches
/// visible on a transparent view. A document (the PDF viewer, a saved web
/// page) is opaque: it stages hidden at its real size and appears in one cut.
/// </summary>
internal enum WebSurfaceKind
{
    Page,
    Document
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
    public StagedWebView()
    {
        CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (args.IsSuccess)
            {
                PushBoundsWhileHidden();
            }
        };
        SizeChanged += (_, _) => PushBoundsWhileHidden();
    }

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
/// browser warm for as long as its preview window lives.
/// </summary>
internal sealed class WarmBrowser : IDisposable
{
    private CoreWebView2Controller? _controller;
    private bool _started;
    private bool _disposed;

    public async void Start(IntPtr parentHwnd)
    {
        if (_started || _disposed || parentHwnd == IntPtr.Zero)
        {
            return;
        }

        _started = true;
        try
        {
            var environment = await WebViewEnvironment.GetAsync();
            var controller = await environment.CreateCoreWebView2ControllerAsync(parentHwnd);
            if (_disposed)
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

    public void Dispose()
    {
        _disposed = true;
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
