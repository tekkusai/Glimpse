using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The idle trim is what gives back a hidden glance's window, its parked
/// browser and its memory. A pin holds off only the process-wide part, the
/// collection and the page-out, so the last pin closing has to bring that
/// back.
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

    [Fact]
    public void HidingTheGlanceUnderAPinSchedulesTheTrim()
    {
        WithPinnedWindow((controller, _) =>
        {
            Fields.Set(controller, "_previewWindow", new PreviewWindow());
            Fields.Set(controller, "_previewVisible", true);

            Fields.Call(controller, "HidePreview");

            Assert.True(TrimScheduled(controller));
        });
    }

    [Fact]
    public void TheTrimUnderAPinLetsTheHiddenGlanceAndItsBrowserGoButCollectsNothing()
    {
        WithPinnedWindow((controller, _) =>
        {
            var glance = new PreviewWindow { Left = -30000, Top = 0 };
            new WindowInteropHelper(glance).EnsureHandle();
            var browser = Fields.Get<object>(glance, "_warmBrowser");
            Fields.Call(browser, "Start", glance.Hwnd);
            Fields.Set(controller, "_previewWindow", glance);

            Fields.Call(controller, "RunIdleTrim");

            Assert.Null(Fields.Get<PreviewWindow?>(controller, "_previewWindow"));
            Assert.False(Fields.Get<bool>(browser, "_started"));

            // The pinned window is in use: no pause across the process and
            // no page-out under it.
            Assert.Equal(0, Fields.Get<int>(controller, "_idleTrimInFlight"));
        });
    }

    [Fact]
    public void TheTrimsCollectionIsAFullBlockingCompaction()
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            try
            {
                var before = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;

                Fields.Call(controller, "RunIdleTrim");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (Fields.Get<int>(controller, "_idleTrimInFlight") != 0)
                {
                    Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the trim.");
                    Thread.Sleep(10);
                }

                // A collection the GC may decline mostly does nothing in an
                // idle tray; this one always runs, whole and compacting.
                var last = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                Assert.True(last.Index > before);
                Assert.True(last.Compacted);
            }
            finally
            {
                controller.Dispose();
            }
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
