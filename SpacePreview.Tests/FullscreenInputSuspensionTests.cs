using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class FullscreenInputSuspensionTests
{
    private const int AppProcessId = 100;
    private const int OtherProcessId = 200;

    private static readonly NativeMethods.RECT Monitor = new(0, 0, 1920, 1080);
    private static readonly NativeMethods.RECT Fullscreen = new(0, 0, 1920, 1080);

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    [InlineData("CabinetWClass")]
    [InlineData("ExploreWClass")]
    [InlineData("XamlExplorerHostIslandWindow")]
    [InlineData("Shell_TrayWnd")]
    [InlineData("Shell_SecondaryTrayWnd")]
    public void ShellAndExplorerWindowsNeverSuspendInput(string className)
    {
        var window = Snapshot(className, OtherProcessId, style: 0, Fullscreen);

        Assert.False(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    [Fact]
    public void OwnWindowsNeverSuspendInput()
    {
        var window = Snapshot("SpacePreview", AppProcessId, style: 0, Fullscreen);

        Assert.False(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    [Fact]
    public void MaximizedCaptionedWindowsDoNotSuspendInput()
    {
        var window = Snapshot(
            "ApplicationFrameWindow",
            OtherProcessId,
            NativeMethods.WS_CAPTION,
            Fullscreen);

        Assert.False(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    [Fact]
    public void BorderlessMonitorSizedWindowsSuspendInput()
    {
        var window = Snapshot("Chrome_WidgetWin_1", OtherProcessId, style: 0, Fullscreen);

        Assert.True(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    [Fact]
    public void BorderlessWindowsWithinToleranceSuspendInput()
    {
        var window = Snapshot("GameWindow", OtherProcessId, style: 0, new NativeMethods.RECT(-2, 1, 1922, 1078));

        Assert.True(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    [Fact]
    public void BorderlessWindowsOutsideToleranceDoNotSuspendInput()
    {
        var window = Snapshot("GameWindow", OtherProcessId, style: 0, new NativeMethods.RECT(3, 0, 1920, 1080));

        Assert.False(FullscreenInputSuspension.ShouldSuspendForWindow(window));
    }

    /// <summary>
    /// A game going full screen in its own window sends no foreground event.
    /// With a pin open, the window in front is looked at again: pins leave
    /// the top layer and the mouse hook comes out, and both come back when
    /// the game is a window again.
    /// </summary>
    [Fact]
    public Task AGameGoingFullScreenInItsOwnWindowIsSeenWhileAPinIsOpen() => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
        var suspension = Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension");
        var mouse = Fields.Get<MouseHook>(controller, "_mouseHook");
        var observer = Fields.Get<uint>(mouse, "_threadId");
        var recheck = Fields.Get<DispatcherTimer>(controller, "_fullscreenRecheckTimer");
        var pin = new PreviewWindow();

        // A hidden window of this process plays the game in front, and the
        // mouse hook is asked for but never installed.
        using var game = new HwndSource(new HwndSourceParameters("Glimpse fullscreen test")
        {
            WindowStyle = unchecked((int)0x80000000) // WS_POPUP, no caption
        });
        Fields.Set(suspension, "_currentProcessId", -1);
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => game.Handle));
        Fields.Set(mouse, "_threadId", 0u);
        try
        {
            var monitor = MonitorOf(game.Handle);
            Place(game.Handle, new NativeMethods.RECT(monitor.Left + 100, monitor.Top + 100, monitor.Left + 900, monitor.Top + 700));

            Fields.Call(controller, "OnPinToggleRequested", pin);
            Assert.True(recheck.IsEnabled);
            Assert.True(Fields.Get<bool>(mouse, "_wanted"));

            Place(game.Handle, monitor);
            await WaitUntilAsync(() => suspension.IsSuspended);
            Assert.True(suspension.IsSuspended);
            Assert.True(Fields.Get<bool>(pin, "_topmostSuppressed"));
            Assert.False(Fields.Get<bool>(mouse, "_wanted"));

            Place(game.Handle, new NativeMethods.RECT(monitor.Left + 100, monitor.Top + 100, monitor.Left + 900, monitor.Top + 700));
            await WaitUntilAsync(() => !suspension.IsSuspended);
            Assert.False(suspension.IsSuspended);
            Assert.False(Fields.Get<bool>(pin, "_topmostSuppressed"));
            Assert.True(Fields.Get<bool>(mouse, "_wanted"));

            // The last pin closed, nothing is looked at.
            Fields.Call(controller, "OnPinnedCloseRequested", pin);
            Assert.False(recheck.IsEnabled);
            Assert.False(Fields.Get<bool>(mouse, "_wanted"));
        }
        finally
        {
            Fields.Set(mouse, "_threadId", observer);
            controller.Dispose();
            pin.Close();
        }
    });

    private static NativeMethods.RECT MonitorOf(IntPtr hwnd)
    {
        var info = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        Assert.True(NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST), ref info));
        return info.rcMonitor;
    }

    private static void Place(IntPtr hwnd, NativeMethods.RECT rect) =>
        Assert.True(NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            rect.Left,
            rect.Top,
            rect.Width,
            rect.Height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    private static FullscreenInputSuspension.WindowSnapshot Snapshot(
        string className,
        int processId,
        long style,
        NativeMethods.RECT windowRect) =>
        new(className, processId, AppProcessId, style, windowRect, Monitor);
}
