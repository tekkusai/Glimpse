using SpacePreview.Native;
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

    private static FullscreenInputSuspension.WindowSnapshot Snapshot(
        string className,
        int processId,
        long style,
        NativeMethods.RECT windowRect) =>
        new(className, processId, AppProcessId, style, windowRect, Monitor);
}
