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
using System.Windows.Controls.Primitives;
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
    /// <summary>Dev probe only: capture the webview's rendered content via CDP
    /// (RenderTargetBitmap cannot see the browser's HWND surface).</summary>
    internal async Task ProbeWebShotAsync(string outputPng)
    {
        try
        {
            if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
            {
                DiagnosticLog.Write("probe webshot: no webview");
                return;
            }

            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{}");
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data").GetString();
            await File.WriteAllBytesAsync(outputPng, Convert.FromBase64String(data ?? string.Empty));
            DiagnosticLog.Write($"probe webshot -> {outputPng}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"probe webshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Dev probe only: the whole window as the user sees it. WPF
    /// renders the chrome; each visible webview's own pixels, captured over
    /// CDP, are laid over the black hole RenderTargetBitmap leaves for it.</summary>
    internal async Task ProbeCompositeShotAsync(string outputPng)
    {
        UpdateLayout();
        var width = (int)Math.Ceiling(ActualWidth);
        var height = (int)Math.Ceiling(ActualHeight);

        // The window renders 1:1 at its own origin. A VisualBrush would fit
        // the visual's content bounds to the frame instead, and anything
        // laid out past the window edge (a stale overlay after a resize)
        // squashed the whole capture into a corner.
        var chrome = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        chrome.Render(this);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawImage(chrome, new Rect(0, 0, width, height));
            foreach (var webView in FindVisualChildren<WebView2>(this))
            {
                if (!webView.IsVisible || webView.CoreWebView2 is not { } core || webView.ActualWidth < 1)
                {
                    continue;
                }

                // A view released mid-capture never answers; the shot goes on
                // without it at once, so the frame keeps its moment.
                var gone = new TaskCompletionSource();
                DependencyPropertyChangedEventHandler hidden = (_, _) => gone.TrySetResult();
                webView.IsVisibleChanged += hidden;
                try
                {
                    var capture = core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{}");
                    if (await Task.WhenAny(capture, gone.Task, Task.Delay(TimeSpan.FromSeconds(2))) != capture)
                    {
                        DiagnosticLog.Write("probe composite webview skipped: it left mid-capture");
                        continue;
                    }

                    using var document = JsonDocument.Parse(await capture);
                    var bytes = Convert.FromBase64String(document.RootElement.GetProperty("data").GetString() ?? string.Empty);
                    var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
                    var origin = webView.TransformToAncestor(this).Transform(new Point(0, 0));
                    context.PushOpacity(webView.Opacity);
                    context.DrawImage(frame, new Rect(origin, new Size(webView.ActualWidth, webView.ActualHeight)));
                    context.Pop();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Write($"probe composite webview skipped: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    webView.IsVisibleChanged -= hidden;
                }
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        await using var stream = File.Create(outputPng);
        encoder.Save(stream);
        DiagnosticLog.Write($"probe composite -> {outputPng} ({width}x{height})");
    }

    /// <summary>Dev-only: runs script inside the current preview's webview.</summary>
    internal async Task ProbeEvalJsAsync(string script)
    {
        if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: not null } webView)
        {
            DiagnosticLog.Write("probe evaljs: no webview ready");
            return;
        }

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        DiagnosticLog.Write($"probe evaljs -> {result}");
        await Task.Delay(300);
    }

    /// <summary>
    /// Dev probe only: a machine-readable snapshot of the real arranged WPF
    /// tree. Pixel captures find paint failures; this finds clipped controls,
    /// lost hit targets, stale overlays, and missing automation names.
    /// </summary>
    internal string ProbeAuditState(string label)
    {
        UpdateLayout();

        Rect BoundsInHeader(FrameworkElement element)
        {
            var origin = element.TranslatePoint(new Point(0, 0), HeaderBar);
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        bool ButtonOwnsCenter(System.Windows.Controls.Primitives.ButtonBase button)
        {
            // A disabled button takes no input by design; only live ones must own their center.
            if (!button.IsVisible || !button.IsEnabled || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            {
                return true;
            }

            var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), this);
            var node = InputHitTest(point) as DependencyObject;
            while (node is not null)
            {
                if (ReferenceEquals(node, button))
                {
                    return true;
                }

                node = node is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        var namedButtons = new (string Name, System.Windows.Controls.Primitives.ButtonBase Button)[]
        {
            (nameof(BrowsePreviousButton), BrowsePreviousButton),
            (nameof(BrowseNextButton), BrowseNextButton),
            (nameof(GridButton), GridButton),
            (nameof(PlayButton), PlayButton),
            (nameof(ZoomOutButton), ZoomOutButton),
            (nameof(ZoomResetButton), ZoomResetButton),
            (nameof(ZoomInButton), ZoomInButton),
            (nameof(RotateLeftButton), RotateLeftButton),
            (nameof(RotateRightButton), RotateRightButton),
            (nameof(SignButton), SignButton),
            (nameof(OpenButton), OpenButton),
            (nameof(OpenMenuButton), OpenMenuButton),
            (nameof(AlwaysOnTopButton), AlwaysOnTopButton),
            (nameof(PinButton), PinButton),
            (nameof(MaximizeButton), MaximizeButton),
            (nameof(CloseButton), CloseButton)
        };
        var buttonStates = namedButtons
            .Where(item => item.Button.IsVisible)
            .Select(item => new
            {
                item.Name,
                Enabled = item.Button.IsEnabled,
                AutomationName = AutomationProperties.GetName(item.Button),
                HitTarget = ButtonOwnsCenter(item.Button),
                Bounds = BoundsInHeader(item.Button).ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .ToArray();

        var lanes = new (string Name, FrameworkElement Element)[]
        {
            (nameof(BrowseControls), BrowseControls),
            (nameof(FileIconImage), FileIconImage),
            (nameof(IdentityPanel), IdentityPanel),
            (nameof(ContentToolsSlot), ContentToolsSlot),
            (nameof(OpenControl), OpenControl),
            (nameof(WindowActions), WindowActions)
        };
        var arrangedLanes = lanes
            .Where(item => item.Element.Visibility != Visibility.Collapsed && item.Element.ActualWidth > 0)
            .Select(item => (item.Name, Bounds: BoundsInHeader(item.Element)))
            .OrderBy(item => item.Bounds.Left)
            .ToArray();
        var lanesInsideHeader = arrangedLanes.All(item =>
            item.Bounds.Left >= -0.5
            && item.Bounds.Top >= -0.5
            && item.Bounds.Right <= HeaderBar.ActualWidth + 0.5
            && item.Bounds.Bottom <= HeaderBar.ActualHeight + 0.5);
        var lanesDoNotOverlap = true;
        for (var i = 1; i < arrangedLanes.Length; i++)
        {
            if (arrangedLanes[i - 1].Bounds.Right > arrangedLanes[i].Bounds.Left + 0.5)
            {
                lanesDoNotOverlap = false;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            Label = label,
            CurrentName = CurrentPath is null ? null : Path.GetFileName(CurrentPath),
            Window = new { Width = ActualWidth, Height = ActualHeight, State = WindowState.ToString() },
            ContentType = PreviewContent.Content?.GetType().Name,
            LoadFinished = _loadFinished,
            StandIn = StandInLayer.Visibility.ToString(),
            ShownTakesInput = PreviewContent.Content is UIElement { IsHitTestVisible: true },
            Icon = new { Visibility = FileIconImage.Visibility.ToString(), HasSource = FileIconImage.Source is not null },
            FileNameWidth = FileNameText.ActualWidth,
            OpenWidth = OpenControl.ActualWidth,
            LanesInsideHeader = lanesInsideHeader,
            LanesDoNotOverlap = lanesDoNotOverlap,
            AllVisibleButtonsNamed = buttonStates.All(item => !string.IsNullOrWhiteSpace(item.AutomationName)),
            AllVisibleButtonCentersHit = buttonStates.All(item => item.HitTarget),
            Buttons = buttonStates,
            Lanes = arrangedLanes.Select(item => new
            {
                item.Name,
                Bounds = item.Bounds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
        });
    }

    /// <summary>Dev probe only: exercises visible button wiring without launching external apps.</summary>
    internal string ProbeButtonAudit()
    {
        var previous = 0;
        var next = 0;
        var dismissed = 0;
        var closed = 0;
        EventHandler<PreviewBrowseDirection> browse = (_, direction) =>
        {
            if (direction == PreviewBrowseDirection.Previous) previous++;
            else next++;
        };
        EventHandler dismiss = (_, _) => dismissed++;
        EventHandler close = (_, _) => closed++;
        BrowseRequested += browse;
        DismissRequested += dismiss;
        CloseRequested += close;
        try
        {
            SetBrowsePosition(1, 3);
            BrowsePreviousButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            BrowseNextButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            var path = CurrentPath ?? Path.Combine(Path.GetTempPath(), "probe.txt");
            SetMultiSelection([path, path], 0);
            GridButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var gridOpened = IsGridViewOpen;
            GridButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var gridClosed = !IsGridViewOpen;

            CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var pinned = IsPinned;

            // The Always on top toggle only exists pinned: revealed at the
            // user's last choice; unchecking releases Topmost and gains
            // taskbar reachability, rechecking restores the float and
            // becomes the next pin's default.
            var defaultOnTop = Services.Settings.Current.PinnedAlwaysOnTopDefault;
            var alwaysOnTopRevealed = pinned
                && AlwaysOnTopButton.Visibility == Visibility.Visible
                && AlwaysOnTopButton.IsChecked == defaultOnTop
                && Topmost == defaultOnTop
                && ShellPresenceApplied(!defaultOnTop);
            AlwaysOnTopButton.IsChecked = false;
            var alwaysOnTopReleased = !Topmost && ShellPresenceApplied(true);
            AlwaysOnTopButton.IsChecked = true;
            var alwaysOnTopRestored = Topmost && ShellPresenceApplied(false);
            var alwaysOnTopRemembered = Services.Settings.Current.PinnedAlwaysOnTopDefault;

            CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var unpinned = !IsPinned;
            var alwaysOnTopRetired = AlwaysOnTopButton.Visibility == Visibility.Collapsed
                && AlwaysOnTopButton.IsChecked == true
                && Topmost
                && ShellPresenceApplied(false);

            return JsonSerializer.Serialize(new
            {
                Previous = previous,
                Next = next,
                Dismissed = dismissed,
                Closed = closed,
                GridOpened = gridOpened,
                GridClosed = gridClosed,
                Pinned = pinned,
                Unpinned = unpinned,
                AlwaysOnTopRevealed = alwaysOnTopRevealed,
                AlwaysOnTopReleased = alwaysOnTopReleased,
                AlwaysOnTopRestored = alwaysOnTopRestored,
                AlwaysOnTopRemembered = alwaysOnTopRemembered,
                AlwaysOnTopRetired = alwaysOnTopRetired
            });
        }
        finally
        {
            BrowseRequested -= browse;
            DismissRequested -= dismiss;
            CloseRequested -= close;
        }
    }

    /// <summary>Dev probe only: recreates the cross-load stand-in cleanup race.</summary>
    internal Task<bool> ProbeLoadingOverlayRaceAsync() => Task.FromResult(ProbeStandInRace());

    /// <summary>
    /// Dev probe only: "sign" enters sign mode on the current PDF the way the
    /// Sign pill does, "signdate" places today's date near the top of the
    /// page, and "signpress:Label" presses the sign-mode pill with that label
    /// (Signature, Save, Discard, Save a signed copy...).
    /// </summary>
    internal async Task<string> ProbeFillSignAsync(string verb, string label)
    {
        if (verb == "sign")
        {
            await EnterFillSignModeAsync();
        }

        if (_fillSignSurface is not { } surface)
        {
            return "not in sign mode";
        }

        surface.UpdateLayout();
        if (verb == "signdate" && !surface.Place(SignPlacementKind.Date, 0.5, 0.18))
        {
            return "date not placed";
        }

        if (verb == "signpress")
        {
            PressPill(surface, label);
        }

        await Task.Delay(200);
        return $"placements={surface.Placements.Count} page={surface.PageIndex + 1}";
    }

    /// <summary>Presses the button whose label or accessible name is <paramref name="label"/>.</summary>
    private static void PressPill(DependencyObject root, string label) =>
        FindLogicalChildren<ButtonBase>(root)
            .First(button => button.Content as string == label || AutomationProperties.GetName(button) == label)
            .RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindLogicalChildren<T>(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Dev probe only: parked probe windows must never re-place themselves.</summary>
    internal void MarkGeometryUserPinnedForProbe() => _stageFrozenForProbe = true;

    /// <summary>
    /// Tests only: a monitor of the test's choosing, off the desktop. A
    /// window parked on one places and reshapes its stage for real, and
    /// never comes on screen.
    /// </summary>
    internal StageMonitor? ParkedMonitor { get; set; }
}
