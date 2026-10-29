using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class MouseHookLifecycleTests
{
    // Explorer needs button changes before a key can trust its header's
    // focus. Other apps need no observer unless a preview is on screen.
    [Theory]
    [InlineData(false, false, 0, false, false)]
    [InlineData(false, false, 0, true, true)]
    [InlineData(false, true, 0, false, true)]
    [InlineData(false, false, 1, false, true)]
    [InlineData(true, true, 0, false, false)]
    [InlineData(true, false, 1, false, false)]
    [InlineData(true, false, 0, true, false)]
    public void ObserverRunsForExplorerOrAPreviewOutsideFullscreen(
        bool suspended,
        bool previewVisible,
        int pinnedWindowCount,
        bool explorerForeground,
        bool expected)
    {
        Assert.Equal(
            expected,
            MouseHook.ShouldRun(suspended, previewVisible, pinnedWindowCount, explorerForeground));
    }
}
