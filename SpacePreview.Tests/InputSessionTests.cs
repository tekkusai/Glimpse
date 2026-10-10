using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// One glance's state ends in one place, a held arrow's queued steps never
/// leak into the next glance, every way out asks before dropping signing
/// work, and a glance belongs to the window it came from.
/// </summary>
public sealed class InputSessionTests
{
    [Fact]
    public void HidingDropsQueuedBrowseStepsAndEveryOtherPieceOfTheGlance()
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            try
            {
                Fields.Set(controller, "_previewVisible", true);
                Fields.Set(controller, "_browsePendingDelta", 3);
                Fields.Set(controller, "_multiIndex", 2);
                Fields.Set(controller, "_lastExplorerHwnd", new IntPtr(0x1234));
                Fields.Set(controller, "_previewOwnerRootHwnd", new IntPtr(0x2000));
                Fields.Get<long[]>(controller, "_navigationKeyRefreshTokens")[0x28] = 7;

                Fields.Call(controller, "HidePreview");

                Assert.False(Fields.Get<bool>(controller, "_previewVisible"));
                Assert.Equal(0, Fields.Get<int>(controller, "_browsePendingDelta"));
                Assert.Equal(0, Fields.Get<int>(controller, "_multiIndex"));
                Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(controller, "_lastExplorerHwnd"));
                Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(controller, "_previewOwnerRootHwnd"));
                Assert.Equal(0, Fields.Get<long[]>(controller, "_navigationKeyRefreshTokens")[0x28]);
            }
            finally
            {
                controller.Dispose();
            }
        });
    }

    [Fact]
    public void PinningDropsQueuedBrowseSteps()
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            var window = new PreviewWindow();
            try
            {
                // As if a fullscreen app were in front, so the test never
                // installs a real mouse hook for the pinned window.
                Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
                Fields.Set(controller, "_previewWindow", window);
                Fields.Set(controller, "_previewVisible", true);
                Fields.Set(controller, "_browsePendingDelta", 2);

                Fields.Call(controller, "OnPinToggleRequested", window);

                Assert.True(window.IsPinned);
                Assert.Equal(0, Fields.Get<int>(controller, "_browsePendingDelta"));
            }
            finally
            {
                // Dispose closes the pinned window.
                controller.Dispose();
            }
        });
    }

    [Fact]
    public void HeaderCloseOverSigningWorkAsksInsteadOfHiding()
    {
        WithSigningPreview((controller, window, surface) =>
        {
            Fields.Call(controller, "OnDismissRequested", window);

            Assert.True(Fields.Get<bool>(controller, "_previewVisible"));
            Assert.True(SignMode.SaveChoiceShown(surface));
        });
    }

    [Fact]
    public void BrowsingOverSigningWorkAsksAndQueuesNoStep()
    {
        WithSigningPreview((controller, window, surface) =>
        {
            Fields.Call(controller, "BrowseRelativeAsync", 1, 0L);

            Assert.Equal(0, Fields.Get<int>(controller, "_browsePendingDelta"));
            Assert.True(Fields.Get<bool>(controller, "_previewVisible"));
            Assert.True(SignMode.SaveChoiceShown(surface));
        });
    }

    [Fact]
    public void AnOutsideClickOverSigningWorkAsksInsteadOfHiding()
    {
        WithSigningPreview((controller, window, surface) =>
        {
            // A point no window of this test owns.
            Fields.Call(controller, "OnMouseDown", 0x0201, -32000, -32000);

            Assert.True(Fields.Get<bool>(controller, "_previewVisible"));
            Assert.True(SignMode.SaveChoiceShown(surface));
        });
    }

    [Fact]
    public void ClosingASigningPinnedWindowAsksFirst()
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            var window = new PreviewWindow();
            try
            {
                var surface = SignMode.Enter(window, withWork: true);
                Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows").Add(window);

                Fields.Call(controller, "OnPinnedCloseRequested", window);

                Assert.Contains(window, Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows"));
                Assert.True(SignMode.SaveChoiceShown(surface));
            }
            finally
            {
                Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows").Clear();
                controller.Dispose();
                window.Close();
            }
        });
    }

    [Fact]
    public void AGlanceBelongsToItsOwnerGlimpseAndTheBrowserItOpened()
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            var window = new PreviewWindow();
            using var previewHwnd = HiddenWindow();
            using var unownedDialog = HiddenWindow();

            // A window of another process: the desktop window itself.
            var foreign = GetDesktopWindow();
            var foreignProcess = NativeMethods.GetWindowProcessId(foreign);
            try
            {
                Fields.Set(window, "Hwnd", previewHwnd.Handle);
                Fields.Set(window, "CurrentPath", Path.Combine(TestDataRoot.Path, "glance.png"));
                Fields.Set(controller, "_previewWindow", window);
                Fields.Set(controller, "_previewVisible", true);

                Assert.True(BelongsToGlance(controller, previewHwnd.Handle));
                Assert.False(BelongsToGlance(controller, foreign));

                // Sign mode's stamp import shows its file dialog without an
                // owner. A Glimpse window never ends the glance.
                Assert.True(BelongsToGlance(controller, unownedDialog.Handle));

                // The window the glance came from.
                Fields.Set(controller, "_previewOwnerRootHwnd", foreign);
                Assert.True(BelongsToGlance(controller, foreign));
                Fields.Set(controller, "_previewOwnerRootHwnd", IntPtr.Zero);

                // A print dialog or file chooser from the preview's browser
                // processes.
                Fields.Get<HashSet<uint>>(window, "_webViewProcessIds").Add(foreignProcess);
                Assert.True(BelongsToGlance(controller, foreign));
                Fields.Get<HashSet<uint>>(window, "_webViewProcessIds").Clear();

                // The welcome has no file and no owner to lose.
                Fields.Set(window, "CurrentPath", null);
                Assert.True(BelongsToGlance(controller, foreign));
            }
            finally
            {
                Fields.Set(controller, "_previewWindow", null);
                controller.Dispose();
                window.Close();
            }
        });
    }

    private static bool BelongsToGlance(SpacePreviewController controller, IntPtr root) =>
        (bool)Fields.Call(controller, "BelongsToGlance", root)!;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    /// <summary>An invisible, unowned top-level window in this process.</summary>
    private static HwndSource HiddenWindow() => new(new HwndSourceParameters("Glimpse input test")
    {
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        Width = 1,
        Height = 1
    });

    private static void WithSigningPreview(Action<SpacePreviewController, PreviewWindow, FillSignSurface> test)
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);
            var window = new PreviewWindow();
            try
            {
                var surface = SignMode.Enter(window, withWork: true);
                Fields.Set(controller, "_previewWindow", window);
                Fields.Set(controller, "_previewVisible", true);

                test(controller, window, surface);
            }
            finally
            {
                Fields.Set(controller, "_previewWindow", null);
                controller.Dispose();
                window.Close();
            }
        });
    }
}
