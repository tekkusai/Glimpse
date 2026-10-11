using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The idle trim is what gives back a hidden glance's window, its parked
/// browser and its memory. Pins hold it off, so the last pin closing has to
/// bring it back.
/// </summary>
public sealed class IdleTrimTests
{
    [Fact]
    public void ClosingTheLastPinSchedulesTheTrim()
    {
        WithPinnedWindow((controller, pinned) =>
        {
            Fields.Call(controller, "OnPinnedCloseRequested", pinned);

            Assert.True(TrimScheduled(controller));
        });
    }

    [Fact]
    public void ClosingAPinUnderAVisibleGlanceLeavesTheTrimToTheHide()
    {
        WithPinnedWindow((controller, pinned) =>
        {
            Fields.Set(controller, "_previewVisible", true);

            Fields.Call(controller, "OnPinnedCloseRequested", pinned);

            Assert.False(TrimScheduled(controller));
        });
    }

    [Fact]
    public void ClosingOneOfTwoPinsLeavesTheTrimOff()
    {
        WithPinnedWindow((controller, pinned) =>
        {
            var other = new PreviewWindow();
            Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows").Add(other);

            Fields.Call(controller, "OnPinnedCloseRequested", pinned);

            Assert.False(TrimScheduled(controller));
        });
    }

    private static bool TrimScheduled(SpacePreviewController controller) =>
        Fields.Get<DispatcherTimer>(controller, "_idleTrimTimer").IsEnabled;

    private static void WithPinnedWindow(Action<SpacePreviewController, PreviewWindow> test)
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            var pinned = new PreviewWindow();
            try
            {
                // As if a fullscreen app were in front, so the test never
                // installs a real mouse hook.
                Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
                pinned.SetPinned(true);
                Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows").Add(pinned);

                test(controller, pinned);
            }
            finally
            {
                // Dispose closes what is still pinned and stops the trim.
                controller.Dispose();
                pinned.Close();
            }
        });
    }
}
