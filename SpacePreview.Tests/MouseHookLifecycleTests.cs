using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class MouseHookLifecycleTests
{
    // A permanent low-level mouse hook would put Glimpse in the path of every
    // mouse report on the PC; it runs only while a preview is on screen, and
    // never under a fullscreen game or video.
    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(false, true, 0, true)]
    [InlineData(false, false, 1, true)]
    [InlineData(true, true, 0, false)]
    [InlineData(true, false, 1, false)]
    public void ObserverRunsOnlyWhileAPreviewIsOnScreenOutsideFullscreen(
        bool suspended,
        bool previewVisible,
        int pinnedWindowCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            MouseHook.ShouldRun(suspended, previewVisible, pinnedWindowCount));
    }
}
