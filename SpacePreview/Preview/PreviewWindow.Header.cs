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
    /// <summary>
    /// The rotate control follows the content: visible only while the native
    /// image viewer is up, and honest about persistence in its tooltip.
    /// </summary>
    private void HideContentActions()
    {
        RotateLeftButton.Visibility = Visibility.Collapsed;
        RotateRightButton.Visibility = Visibility.Collapsed;
        ContentActions.Visibility = Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private void SharePath(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (ShareService.ShareFile(path, hwnd))
        {
            SuppressOutsideDismissFor(TimeSpan.FromSeconds(30));
            DiagnosticLog.Write($"share opened {path}");
            return;
        }

        DiagnosticLog.Write($"share declined {path}");
    }

    private void SetBrowseControlsVisible(bool visible)
    {
        BrowseControls.Visibility = visible && !_isFileDialogMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void DetachBrowseContext(bool restoreSlideshowSize = true)
    {
        SetBrowseControlsVisible(false);
        _multiSelectionPaths = null;
        _multiSelectionIndex = 0;
        CloseGridView();
        StopSlideshow(restoreSlideshowSize);
        GridButton.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        SetBrowsePosition(-1, 0);
    }

    /// <summary>
    /// Chevrons rest at the folder's ends. An unknown position (probe, stale
    /// shell) keeps both live; the shell answers again on the next step.
    /// </summary>
    public void SetBrowsePosition(int index, int count)
    {
        var known = index >= 0 && count > 0;
        BrowsePreviousButton.IsEnabled = !known || index > 0;
        BrowseNextButton.IsEnabled = !known || index < count - 1;
    }

    // ---------------------------------------------------------------------
    // Header: icon, open-with, maximize, drag.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Uses the header zoom controls for a document webview. It drives the
    /// same browser zoom as Ctrl+wheel, and the label follows both.
    /// </summary>
    private void ShowZoomControls(WebView2 webView)
    {
        _zoomWebView = webView;
        webView.ZoomFactorChanged += OnZoomFactorChanged;
        UpdateZoomLabel();
        ZoomControls.Visibility = Visibility.Visible;
        UpdateContentToolsSlotVisibility();
    }

    private void HideZoomControls()
    {
        if (_zoomWebView is not null)
        {
            _zoomWebView.ZoomFactorChanged -= OnZoomFactorChanged;
            _zoomWebView = null;
        }

        UpdateZoomLabel();
        ZoomControls.Visibility = Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private void UpdateContentToolsSlotVisibility()
    {
        ContentToolsSlot.Visibility =
            ZoomControls.Visibility == Visibility.Visible
            || ContentActions.Visibility == Visibility.Visible
            || _fillSignPill?.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Hidden;
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e) => UpdateZoomLabel();

    private void UpdateZoomLabel()
    {
        var factor = _zoomWebView?.ZoomFactor ?? 1;
        ZoomResetButton.Content = $"{Math.Round(factor * 100)}%";
        ZoomOutButton.IsEnabled = _zoomWebView is not null && CanAdjustZoom(factor, -1);
        ZoomInButton.IsEnabled = _zoomWebView is not null && CanAdjustZoom(factor, 1);
        AutomationProperties.SetName(ZoomResetButton, $"Reset zoom, currently {Math.Round(factor * 100)} percent");
    }

    private void ResetZoom()
    {
        if (_zoomWebView is null)
        {
            return;
        }

        _zoomWebView.ZoomFactor = 1.0;
        UpdateZoomLabel();
        DiagnosticLog.Write("zoom reset -> 1");
    }

    // The browser's own zoom stops (what Ctrl+wheel walks). The buttons walk
    // the same ladder, so both controls always agree and the label reads in
    // familiar levels instead of drifting to 196%.
    private static readonly double[] ZoomLevels =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    internal static bool IsZoomInKey(int vk) => vk is NativeMethods.VK_ADD or NativeMethods.VK_OEM_PLUS;

    internal static bool IsZoomOutKey(int vk) => vk is NativeMethods.VK_SUBTRACT or NativeMethods.VK_OEM_MINUS;

    internal static bool IsZoomResetKey(int vk) => vk is NativeMethods.VK_0 or NativeMethods.VK_NUMPAD0;

    internal static double NextZoomLevel(double current, int direction)
    {
        if (!double.IsFinite(current))
        {
            current = 1;
        }

        if (direction > 0)
        {
            foreach (var level in ZoomLevels)
            {
                if (level > current + 0.001)
                {
                    return level;
                }
            }
        }
        else if (direction < 0)
        {
            for (var i = ZoomLevels.Length - 1; i >= 0; i--)
            {
                if (ZoomLevels[i] < current - 0.001)
                {
                    return ZoomLevels[i];
                }
            }
        }

        return Math.Clamp(current, ZoomLevels[0], ZoomLevels[^1]);
    }

    internal static bool CanAdjustZoom(double current, int direction) =>
        Math.Abs(NextZoomLevel(current, direction) - current) > 0.001;

    private void AdjustZoom(int direction)
    {
        if (_zoomWebView is null)
        {
            DiagnosticLog.Write("zoom adjust ignored: no webview");
            return;
        }

        var target = NextZoomLevel(_zoomWebView.ZoomFactor, direction);

        _zoomWebView.ZoomFactor = target;

        // The control's ZoomFactorChanged covers Ctrl+wheel; programmatic
        // sets do not raise it, so the label follows directly here.
        UpdateZoomLabel();
        DiagnosticLog.Write($"zoom adjust -> {target:0.##}");
    }

    private async Task UpdateFileIconAsync(string path, long version, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(() => ShellThumbnailProvider.TryGetIcon(path, 48), token);
            if (version != _loadVersion)
            {
                return;
            }

            FileIconImage.Source = icon;
            FileIconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // A newer preview owns the shell lane now.
        }
        catch
        {
            // Icon is decoration; the preview must not care.
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    private static string GetOpenLabel(string path)
    {
        if (Directory.Exists(path))
        {
            return "Open";
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return "Open";
        }

        // Queried fresh per preview: it is one sub-millisecond registry read,
        // and the user can change a default app while the app stays resident.
        var name = NativeMethods.TryGetFriendlyAppName(extension);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Open";
        }

        // With no association, Windows offers its app picker as the
        // "app" - and "Open with Pick an app" is not useful. Name the
        // action for what it is.
        if (name.Equals("Pick an app", StringComparison.OrdinalIgnoreCase))
        {
            return "Pick an app";
        }

        return $"Open with {(name.Length > 26 ? name[..25].TrimEnd() + "…" : name)}";
    }

    internal void OpenCurrent()
    {
        if (_isFileDialogMode)
        {
            DiagnosticLog.Write("open suppressed while file dialog owns commit");
            return;
        }

        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clicking into the preview hands it keyboard focus, so Ctrl+C and the
    /// viewers' own shortcuts land where the user is looking. Only a click in
    /// the CONTENT hands the keyboard to the content: a click on header chrome
    /// (chevron, pin, zoom) activates the window but keeps the keys, or
    /// stepping to a video would give every arrow key to the player.
    /// </summary>
    public void ActivateForInteraction(int screenX, int screenY)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            Activate();
        }
        catch
        {
            // Activation is best-effort; the click itself still lands.
        }

        // A click on web content lands in the browser process, so Windows may
        // deny the host the foreground and leave it with Explorer - where the
        // next Ctrl+C copies Explorer's selected file instead of the
        // highlighted text. The user clicked this window; make it true.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(hwnd);
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != hwnd)
            {
                DiagnosticLog.Write($"activate: foreground stayed 0x{foreground.ToInt64():X} (ours 0x{hwnd.ToInt64():X})");
            }
        }

        // Web-backed content (PDF viewer, docs) needs real keyboard focus in
        // the browser for its own Ctrl+C/Ctrl+F/arrow handling - the PDF
        // plugin's selection is invisible to the WPF side. Don't steal focus
        // from a text preview the user clicked into, and only do it when the
        // click actually landed on the content, not the header.
        bool clickedContent;
        double contentY;
        try
        {
            var y = PointFromScreen(new Point(screenX, screenY)).Y;
            clickedContent = y >= HeaderHeight;
            contentY = y - HeaderHeight;
        }
        catch
        {
            // A window mid-teardown cannot transform; treat as content.
            clickedContent = true;
            contentY = double.MaxValue;
        }

        if (!clickedContent)
        {
            // The browser child can retain Win32 focus after a content click.
            // A later header action means arrows belong to preview browsing
            // again, even though that stale child still reports focus.
            _userEngagedContent = false;
            _lastContentClickInViewerChrome = false;
            return;
        }

        // Only content clicks enable the content-specific key exceptions.
        // A click in the embedded viewer's own toolbar strip (page box,
        // find) is the one place typed keys are the viewer's words.
        _userEngagedContent = true;
        UpdateFillSignPill();
        _lastContentClickInViewerChrome = contentY < ViewerChromeBandHeight;
        if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
            && FindVisualChild<WebView2>(PreviewContent) is { } webView)
        {
            webView.Focus();
        }
    }

    private async Task CopyWebViewSelectionAsync(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                if (IsPdfViewerSource(core.Source ?? string.Empty))
                {
                    // The PDF plugin's selection is invisible to the DOM, so
                    // hand the copy to the viewer itself through the browser's
                    // input pipe. With nothing selected Chromium copies
                    // nothing: over a document, Ctrl+C means "copy the text".
                    DiagnosticLog.Write("ctrl+c forwarded into pdf viewer");
                    await ForwardCtrlCAsync(core);
                    return;
                }

                var json = await core.ExecuteScriptAsync("window.getSelection().toString()");
                var text = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrEmpty(text))
                {
                    SetClipboard(() => Clipboard.SetText(text), "copy web selection");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"webview selection copy failed: {ex.GetType().Name}: {ex.Message}");
        }

        CopyCurrentFile();
    }

    private static Task ForwardCtrlCAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core) =>
        DispatchModifiedKeyAsync(core, 67, "KeyC", "c", modifiers: 2);

    private static async Task DispatchPlainKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, int virtualKey, string code, string key)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    private static async Task DispatchMouseClickAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, double x, double y)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var sx = x.ToString("0.###", invariant);
        var sy = y.ToString("0.###", invariant);
        var down = $"{{\"type\":\"mousePressed\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":1,\"pointerType\":\"mouse\"}}";
        var up = $"{{\"type\":\"mouseReleased\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":0,\"pointerType\":\"mouse\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", down);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", up);
    }

    private static async Task DispatchModifiedKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, int virtualKey, string code, string key, int modifiers)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    /// <summary>
    /// The clipboard throws transiently when another process holds it open;
    /// for an app that supports Ctrl+C, one silent retry is the
    /// difference between trusted and flaky.
    /// </summary>
    private static void SetClipboard(Action write, string diagnosticLabel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 2)
                {
                    DiagnosticLog.Write($"{diagnosticLabel} failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                Thread.Sleep(35);
            }
        }
    }

    private void CopyCurrentFile()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetFileDropList(new StringCollection { path }), $"copy file {path}");
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetText(path), $"copy path {path}");
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PreviewContextMenu") };
        Theme.ApplyResources(menu);
        TrackOwnedPopup(menu);

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header, Style = (Style)FindResource("PreviewMenuItem") };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Open", OpenCurrent));
        menu.Items.Add(Item("Copy", CopyCurrentFile));
        menu.Items.Add(Item("Copy path", CopyCurrentPath));
        _contextMenu = menu;
        RootChrome.ContextMenu = menu;
    }

    private void ApplyHeaderSubtitle(string path, string? shortcutTarget, int selectedCount)
    {
        SubtitleText.Inlines.Clear();
        if (shortcutTarget is null)
        {
            SubtitleText.Text = BuildHeaderSubtitle(path, null, selectedCount);
            return;
        }

        // The target is a link: click reveals it in its containing folder.
        var link = new Hyperlink(new Run($"Shortcut → {shortcutTarget}"))
        {
            Foreground = Theme.Brush(Theme.Current.TextSecondary),
            TextDecorations = null,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "Show target in Explorer"
        };
        link.Click += (_, _) => RevealInExplorer(shortcutTarget);
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        SubtitleText.Inlines.Add(link);

        if (selectedCount > 1)
        {
            SubtitleText.Inlines.Add(new Run($" · {selectedCount} selected"));
        }
    }

    private void RevealInExplorer(string target)
    {
        if (ExplorerRevealService.TryShow(target))
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string BuildHeaderSubtitle(string path, string? shortcutTarget, int selectedCount)
    {
        string subtitle;
        if (shortcutTarget is not null)
        {
            subtitle = $"Shortcut → {shortcutTarget}";
        }
        else if ((FileKinds.IsImage(path) || FileKinds.IsPhotoshop(path)) && TryGetImagePixelSize(path, out var pixelSize))
        {
            try
            {
                var file = new FileInfo(path);
                subtitle = $"{FileKinds.FriendlyKind(path)} · {(int)pixelSize.Width}×{(int)pixelSize.Height} · {FileKinds.FormatSize(file.Length)} · Modified {file.LastWriteTime:g}";
            }
            catch
            {
                subtitle = FileKinds.BuildSubtitle(path);
            }
        }
        else
        {
            subtitle = FileKinds.BuildSubtitle(path);
        }

        if (selectedCount > 1)
        {
            subtitle += $" · {selectedCount} selected";
        }

        return subtitle;
    }

    private static string? TryResolveShortcutTarget(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = ((dynamic)shell).CreateShortcut(path);
            var target = ((dynamic)shortcut!).TargetPath as string;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            target = Environment.ExpandEnvironmentVariables(target);
            if (!IsSafeLocalPreviewTarget(target))
            {
                return null;
            }

            return File.Exists(target) || Directory.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        try
        {
            if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private void UpdateOpenControl(string label, IReadOnlyList<(string Label, Action Action)> secondaryActions)
    {
        var palette = Theme.Current;
        var hasMenu = secondaryActions.Count > 0;

        // A Grid, not a StackPanel: when the header runs out of width the
        // label column shrinks and ellipsizes while the divider and chevron
        // keep their place. A StackPanel measures children unconstrained, so
        // it clips mid-glyph and pushes the chevron out of the pill entirely.
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var container = new Border
        {
            Width = MeasureOpenPillWidth(label, hasMenu),
            CornerRadius = Metrics.PillCorner,
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = panel
        };

        var openHalf = new Border
        {
            CornerRadius = hasMenu ? Metrics.PillLeftCorner : new CornerRadius(Metrics.RadiusPill - 1),
            Padding = new Thickness(
                Metrics.PillPadding.Left,
                Metrics.PillPadding.Top,
                hasMenu ? Metrics.PillInnerPadding : Metrics.PillPadding.Right,
                Metrics.PillPadding.Bottom),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Open (Enter)",
            Child = new TextBlock
            {
                Text = label,
                FontSize = Metrics.FontLabel,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = hasMenu ? HeaderOpenLabelWithMenuMaxWidth : HeaderOpenLabelMaxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextPrimary)
            }
        };
        AutomationProperties.SetName(openHalf, label);
        AutomationProperties.SetHelpText(openHalf, "Open the previewed item");
        openHalf.MouseEnter += (_, _) => openHalf.Background = Theme.Brush(palette.HoverBg);
        openHalf.MouseLeave += (_, _) => openHalf.Background = Brushes.Transparent;
        openHalf.MouseLeftButtonDown += (_, e) => e.Handled = true;
        openHalf.MouseLeftButtonUp += (_, e) =>
        {
            OpenCurrent();
            e.Handled = true;
        };
        Grid.SetColumn(openHalf, 0);
        panel.Children.Add(openHalf);

        if (hasMenu)
        {
            var divider = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 5, 0, 5),
                Background = Theme.Brush(palette.RaisedBorder)
            };
            Grid.SetColumn(divider, 1);
            panel.Children.Add(divider);

            var menuHalf = new Border
            {
                CornerRadius = Metrics.PillRightCorner,
                Padding = new Thickness(9, Metrics.PillPadding.Top, Metrics.PillInnerPadding, Metrics.PillPadding.Bottom),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "More ways to open",
                Child = new TextBlock
                {
                    Text = "\uE70D",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Theme.Brush(palette.TextSecondary)
                }
            };
            AutomationProperties.SetName(menuHalf, "More open actions");
            menuHalf.MouseEnter += (_, _) => menuHalf.Background = Theme.Brush(palette.HoverBg);
            menuHalf.MouseLeave += (_, _) => menuHalf.Background = Brushes.Transparent;
            menuHalf.MouseLeftButtonDown += (_, e) => e.Handled = true;
            menuHalf.MouseLeftButtonUp += (_, e) =>
            {
                ShowOpenActionsMenu(container, secondaryActions);
                e.Handled = true;
            };
            Grid.SetColumn(menuHalf, 2);
            panel.Children.Add(menuHalf);
        }

        OpenControl.Content = container;

        // Swapping Content while the window is hidden (Space-close, then
        // Space-open on another file) leaves the header Grid's cached measure
        // for this column at the previous pill's width; the new, wider pill
        // then renders clipped into the old slot. Explicitly dirty the
        // control's measure so the header re-measures on the next pass.
        OpenControl.InvalidateMeasure();
    }

    private static double MeasureOpenPillWidth(string label, bool hasMenu)
    {
        var labelCap = hasMenu ? HeaderOpenLabelWithMenuMaxWidth : HeaderOpenLabelMaxWidth;
        var textWidth = Math.Clamp(label.Length * 6.4, 34, labelCap);
        var chromeWidth = hasMenu ? 54 : 28;
        var minimum = hasMenu ? HeaderOpenMenuMinWidth : HeaderOpenMinWidth;
        return Math.Clamp(textWidth + chromeWidth, minimum, HeaderOpenMaxWidth);
    }

    private void ShowOpenActionsMenu(FrameworkElement anchor, IReadOnlyList<(string Label, Action Action)> actions)
    {
        // The outside-click dismissal closes the menu on mouse DOWN; without
        // this guard the mouse UP on the chevron instantly reopens it, so
        // clicking the chevron a second time could fail to collapse the flyout.
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("PreviewContextMenu"),
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom
        };

        // Own the placement outright: right edge flush with the pill, one gap
        // below it. Post-open offset nudging conflicts with WPF's own edge
        // adjustment and loses.
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + Metrics.FlyoutGap),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
        ];

        Theme.ApplyResources(menu);

        foreach (var (label, action) in actions)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("PreviewMenuItem") };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        TrackOwnedPopup(menu);
        menu.Closed += (_, _) => _flyoutClosedAt = DateTime.UtcNow;
        menu.IsOpen = true;
    }

    private void TrackOwnedPopup(ContextMenu menu)
    {
        menu.Opened += (_, _) => RegisterOwnedPopup(menu);
        menu.Closed += (_, _) => UnregisterOwnedPopup(menu);
    }

    private void RegisterOwnedPopup(ContextMenu menu)
    {
        Capture();
        Dispatcher.BeginInvoke(Capture, DispatcherPriority.Loaded);

        void Capture()
        {
            var hwnd = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            lock (_popupSync)
            {
                _ownedPopupHandles[menu] = hwnd;
            }
        }
    }

    private void UnregisterOwnedPopup(ContextMenu menu)
    {
        lock (_popupSync)
        {
            _ownedPopupHandles.Remove(menu);
        }
    }

    private void RememberWebViewProcesses(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        var processIds = new List<uint>();
        if (core.BrowserProcessId != 0)
        {
            processIds.Add(core.BrowserProcessId);
        }

        try
        {
            foreach (var processInfo in core.Environment.GetProcessInfos())
            {
                if (processInfo.ProcessId > 0)
                {
                    processIds.Add((uint)processInfo.ProcessId);
                }
            }
        }
        catch
        {
            // The browser process id above is enough for normal menu routing.
        }

        if (processIds.Count == 0)
        {
            return;
        }

        lock (_popupSync)
        {
            foreach (var processId in processIds)
            {
                _webViewProcessIds.Add(processId);
            }
        }
    }

    private void ArmWebContextMenu(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        RememberWebViewProcesses(core);
        Interlocked.Exchange(ref _webContextMenuArmedUntilTicks, DateTime.UtcNow.AddSeconds(20).Ticks);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPinned)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void BrowsePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Previous);
    }

    private void BrowseNextButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Next);
    }

    /// <summary>
    /// At the pin cap the button stays until a slot frees up; the tooltip
    /// says how to free one. The unpin control remains available.
    /// </summary>
    public void SetPinLimitReached(bool reached)
    {
        if (IsPinned)
        {
            return;
        }

        PinButton.IsEnabled = !reached;
        PinButton.ToolTip = reached
            ? "Up to 6 previews can stay pinned. Unpin one to pin this."
            : "Pin (stays open)";
    }
}
