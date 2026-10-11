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
    /// The only Topmost derivation. Transient previews always float (the
    /// quick-look contract); a pinned window floats while its Always on top
    /// toggle is on; fullscreen suppression outranks both.
    /// </summary>
    private bool EffectiveTopmost => (!IsPinned || _pinnedAlwaysOnTop) && !_topmostSuppressed;

    /// <summary>
    /// A pinned window the user took off the top layer is an ordinary window,
    /// and an ordinary window must be recoverable from the taskbar and
    /// Alt+Tab when buried. Floating and transient previews stay out of both.
    /// Fullscreen suppression is temporary, not a choice, so it never adds an
    /// entry.
    /// </summary>
    private bool TaskbarReachable => IsPinned && !_pinnedAlwaysOnTop;

    private void ApplyTopmost()
    {
        Topmost = EffectiveTopmost;
        SyncShellPresence();
    }

    // Last taskbar-tab state actually handed to the shell.
    private bool _taskbarTabShown;

    /// <summary>
    /// Shell presence without touching WPF's ShowInTaskbar at runtime:
    /// flipping that property hides and re-shows the frame, a visible flash
    /// on a live window. The extended style bits govern Alt+Tab (TOOLWINDOW
    /// excludes, APPWINDOW forces inclusion past the hidden owner WPF gives
    /// non-taskbar windows), and ITaskbarList moves the taskbar button.
    /// </summary>
    private void SyncShellPresence()
    {
        if (Hwnd == IntPtr.Zero)
        {
            return;
        }

        var reachable = TaskbarReachable;
        var exStyle = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var desired = reachable
            ? (exStyle & ~NativeMethods.WS_EX_TOOLWINDOW) | NativeMethods.WS_EX_APPWINDOW
            : (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        if (desired != exStyle)
        {
            NativeMethods.SetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desired));
        }

        if (reachable == _taskbarTabShown)
        {
            return;
        }

        try
        {
            NativeMethods.SetTaskbarTab(Hwnd, reachable);
            _taskbarTabShown = reachable;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"taskbar tab sync failed {exception.Message}");
        }
    }

    /// <summary>Probe: the shell-presence state matches the derivation.</summary>
    private bool ShellPresenceApplied(bool reachable)
    {
        if (TaskbarReachable != reachable)
        {
            return false;
        }

        if (Hwnd == IntPtr.Zero)
        {
            return true;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        var appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        return toolWindow == !reachable && appWindow == reachable && _taskbarTabShown == reachable;
    }

    public void SetTopmostSuppressed(bool suppressed)
    {
        _topmostSuppressed = suppressed;
        ApplyTopmost();
    }

    public bool ContainsScreenPoint(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    public bool ContainsOwnedPopupScreenPoint(int x, int y, IntPtr hitRoot)
    {
        if (hitRoot == IntPtr.Zero)
        {
            return false;
        }

        lock (_popupSync)
        {
            if (_ownedPopupHandles.ContainsValue(hitRoot))
            {
                return true;
            }
        }

        if (NativeMethods.GetAncestor(hitRoot, NativeMethods.GA_ROOTOWNER) == Hwnd)
        {
            return true;
        }

        var armedUntil = Interlocked.Read(ref _webContextMenuArmedUntilTicks);
        if (armedUntil < DateTime.UtcNow.Ticks || !NativeMethods.IsLikelyWebViewContextMenuWindow(hitRoot))
        {
            return false;
        }

        var processId = NativeMethods.GetWindowProcessId(hitRoot);
        lock (_popupSync)
        {
            return _webViewProcessIds.Contains(processId);
        }
    }

    public bool OwnsForegroundRoot(IntPtr root)
    {
        if (root == IntPtr.Zero)
        {
            return false;
        }

        if (root == Hwnd || NativeMethods.GetAncestor(root, NativeMethods.GA_ROOTOWNER) == Hwnd)
        {
            return true;
        }

        lock (_popupSync)
        {
            if (_ownedPopupHandles.ContainsValue(root))
            {
                return true;
            }

            var armedUntil = Interlocked.Read(ref _webContextMenuArmedUntilTicks);
            return armedUntil >= DateTime.UtcNow.Ticks
                && NativeMethods.IsLikelyWebViewContextMenuWindow(root)
                && _webViewProcessIds.Contains(NativeMethods.GetWindowProcessId(root));
        }
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Position is session-local by design: the stage recenters on the
        // next fresh open. Size is the only geometry a drag can redefine,
        // and the border-resize hook owns that.
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// One maximize system: the OS's. Snap assist, F11, the header button,
    /// and a video's fullscreen request all drive WindowState, so no
    /// private flag can ever disagree with what is on screen, and the OS
    /// owns the restore bounds. WM_GETMINMAXINFO keeps a maximized
    /// borderless window on the work area instead of over the taskbar.
    /// </summary>
    private bool IsStageMaximized => WindowState == WindowState.Maximized;

    private void ToggleMaximize()
    {
        WindowState = IsStageMaximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void RestoreFromNativeMinimize()
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        WindowState = WindowState.Normal;
        DiagnosticLog.Write("stage restored from native minimize");
    }

    private void SyncNativeWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // However the window got here, a dragged item size no longer
            // applies.
            _itemStageSize = null;
            DiagnosticLog.Write("stage maximized");
        }
        else
        {
            _contentDrivenMaximize = false;
            DiagnosticLog.Write($"stage {(WindowState == WindowState.Minimized ? "minimized" : "restored")}");
        }

        UpdateMaximizeChrome();

        // Idempotent insurance: state changes are the one runtime moment the
        // shell re-evaluates a window, so re-assert the derived presence.
        SyncShellPresence();
    }

    private void UpdateMaximizeChrome()
    {
        MaximizeButton.Content = IsStageMaximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = IsStageMaximized ? "Restore size (F11)" : "Fill screen (F11)";
        AutomationProperties.SetName(MaximizeButton, IsStageMaximized ? "Restore preview size" : "Fill screen");
    }

    private static Rect GetWorkAreaDips(IntPtr hwnd)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var dpi = hwnd == IntPtr.Zero ? 96 : NativeMethods.GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                var scale = 96.0 / dpi;
                return new Rect(
                    info.rcWork.Left * scale,
                    info.rcWork.Top * scale,
                    info.rcWork.Width * scale,
                    info.rcWork.Height * scale);
            }
        }
        catch
        {
            // Fall back to primary work area below.
        }

        return SystemParameters.WorkArea;
    }

    private static Rect? TryGetWindowRectDips(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = 96.0 / dpi;
        return new Rect(
            rect.Left * scale,
            rect.Top * scale,
            rect.Width * scale,
            rect.Height * scale);
    }

    private void ApplyWindowNativeStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Hwnd = hwnd;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        SyncShellPresence();

        // A border-drag holds the stage size for the rest of this open;
        // the next open returns to content rules. WM_SIZING fires only for
        // interactive border resizes (never for layout or DPI changes), and
        // WM_EXITSIZEMOVE commits the final size once the user lets go.
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
        WindowFrame.Apply(hwnd);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            // A borderless window maximizes over the taskbar by default;
            // cap it to the work area so maximized still means "fill the
            // desktop", the same promise the button has always made.
            CapMaximizedBoundsToWorkArea(hwnd, lParam, ref handled);
        }
        else if (msg == WM_SIZING)
        {
            _userResizingStage = true;
        }
        else if (msg == WM_EXITSIZEMOVE && _userResizingStage)
        {
            _userResizingStage = false;
            if (WindowState == WindowState.Normal)
            {
                _itemStageSize = new Size(Width, Height);
                DiagnosticLog.Write($"stage resized to {Width:0}x{Height:0} (held for this item)");
            }
            else
            {
                _itemStageSize = null;
                DiagnosticLog.Write("stage resize ignored after native snap/maximize");
            }
        }

        return IntPtr.Zero;
    }

    private static void CapMaximizedBoundsToWorkArea(IntPtr hwnd, IntPtr minMaxInfo, ref bool handled)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var limits = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(minMaxInfo);
        limits.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        limits.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        limits.ptMaxSize.X = info.rcWork.Width;
        limits.ptMaxSize.Y = info.rcWork.Height;
        System.Runtime.InteropServices.Marshal.StructureToPtr(limits, minMaxInfo, fDeleteOld: false);
        handled = true;
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (PinToggleRequested is null)
        {
            // Standalone (probe): no controller to arbitrate, toggle directly.
            SetPinned(!IsPinned);
            return;
        }

        PinToggleRequested.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;

        // Fresh default at every pin-state change: the next pin starts from
        // the user's chosen default, and a window retiring to transient never
        // inherits a pinned window's per-window choice. (Transients float
        // regardless; the bit only matters while pinned.)
        AlwaysOnTopButton.IsChecked = Services.Settings.Current.PinnedAlwaysOnTopDefault;
        SetAlwaysOnTopVisible(pinned);
        ApplyTopmost();

        PinButton.IsEnabled = true;
        PinButton.Content = pinned ? UnpinGlyph : PinGlyph;
        PinButton.ToolTip = pinned ? "Unpin" : "Pin (stays open)";
        AutomationProperties.SetName(PinButton, pinned ? "Unpin preview" : "Pin preview");
        CloseButton.ToolTip = pinned ? "Close (Esc)" : "Close (Esc or Space)";
        if (pinned)
        {
            PinButton.SetResourceReference(ForegroundProperty, "Theme.Accent");
        }
        else
        {
            // Back to the style's themed foreground.
            PinButton.ClearValue(ForegroundProperty);
        }

        DiagnosticLog.Write($"pin state {(pinned ? "pinned" : "unpinned")} path={CurrentPath}");
        if (pinned)
        {
            // A pinned window leaves browse mode; the set surfaces go with it.
            DetachBrowseContext(restoreSlideshowSize: true);
        }

        SetBrowseControlsVisible(CurrentPath is not null && !pinned && !_isFileDialogMode);
    }

    private void AlwaysOnTopButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _pinnedAlwaysOnTop = AlwaysOnTopButton.IsChecked == true;
        ApplyTopmost();
        if (IsPinned)
        {
            DiagnosticLog.Write($"always-on-top {(_pinnedAlwaysOnTop ? "on" : "off")} path={CurrentPath}");
        }
    }

    /// <summary>
    /// Opacity-only reveal into space the pin action just recomposed; the
    /// window controls to its right never move. Honors the Windows animation
    /// preference by snapping instead of fading.
    /// </summary>
    private void SetAlwaysOnTopVisible(bool visible)
    {
        if (visible == (AlwaysOnTopButton.Visibility == Visibility.Visible))
        {
            return;
        }

        AlwaysOnTopButton.BeginAnimation(OpacityProperty, null);
        if (!visible)
        {
            AlwaysOnTopButton.Visibility = Visibility.Collapsed;
            AlwaysOnTopButton.Opacity = 0;
            return;
        }

        AlwaysOnTopButton.Visibility = Visibility.Visible;
        if (!Motion.Enabled)
        {
            AlwaysOnTopButton.Opacity = 1;
            return;
        }

        AlwaysOnTopButton.Opacity = 0;
        AlwaysOnTopButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }
}
