using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class MouseHookLifecycleTests
{
    [Theory]
    [InlineData(false, false, 0, false)]
    [InlineData(true, false, 0, false)]
    [InlineData(false, true, 0, false)]
    [InlineData(false, false, 1, false)]
    [InlineData(true, true, 0, true)]
    [InlineData(true, false, 1, true)]
    public void ObserverRunsOnlyWhenInputIsActiveAndAPreviewExists(
        bool inputHooksActive,
        bool previewVisible,
        int pinnedWindowCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            MouseHook.ShouldRun(inputHooksActive, previewVisible, pinnedWindowCount));
    }

    [Fact]
    public void ObserverOwnsADedicatedMessageLoopAndQueuesUiWork()
    {
        var mouseHook = TestSource.Read(@"SpacePreview\Services\MouseHook.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var callback = TestSource.CodeMethod(@"SpacePreview\Services\MouseHook.cs", "HookCallback");
        var hookPolicy = TestSource.CodeMethod(@"SpacePreview\Services\SpacePreviewController.cs", "SyncMouseHook");

        Assert.Contains("new Thread", mouseHook, StringComparison.Ordinal);
        Assert.Contains("GetMessage", mouseHook, StringComparison.Ordinal);
        Assert.Contains("PostThreadMessage", mouseHook, StringComparison.Ordinal);
        Assert.Contains("new MouseHook(QueueMouseDown, IsMouseActivationWindow)", controller, StringComparison.Ordinal);
        Assert.Contains("DispatcherPriority.Input", controller, StringComparison.Ordinal);
        Assert.Contains("MouseHook.ShouldRun", hookPolicy, StringComparison.Ordinal);
        Assert.DoesNotContain("_mouseHook.Start();", TestSource.CodeMethod(
            @"SpacePreview\Services\SpacePreviewController.cs",
            "SetInputHooksActive"), StringComparison.Ordinal);
        Assert.DoesNotContain(".Wait(", callback, StringComparison.Ordinal);
        Assert.DoesNotContain(".Join(", callback, StringComparison.Ordinal);
        Assert.DoesNotContain(".Join(", TestSource.CodeMethod(
            @"SpacePreview\Services\MouseHook.cs",
            "Stop"), StringComparison.Ordinal);
    }
}
