using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Alt+F4, the taskbar's Close and Quit are ways out like Esc and the X.
/// They go through the same gates, never drop signing work without asking,
/// and never leave the controller holding a dead window.
/// </summary>
public sealed class InputCloseTests
{
    private const uint WmSysCommand = 0x0112;
    private const uint WmClose = 0x0010;
    private const int ScClose = 0xF060;

    [Fact]
    public void AltF4OnASigningGlanceAsksAndKeepsTheWindow()
    {
        WithManagedWindow(pinned: false, withWork: true, (controller, window, hwnd, surface) =>
        {
            AltF4(hwnd);

            Assert.True(NativeMethods.IsWindow(hwnd));
            Assert.Same(window, Fields.Get<PreviewWindow>(controller, "_previewWindow"));
            Assert.True(Fields.Get<bool>(controller, "_previewVisible"));
            Assert.True(SignMode.SaveChoiceShown(surface!));
        });
    }

    [Fact]
    public void AltF4OnAGlanceEndsItAndKeepsTheWindowForTheNextSpace()
    {
        WithManagedWindow(pinned: false, withWork: false, (controller, window, hwnd, _) =>
        {
            AltF4(hwnd);

            Assert.True(NativeMethods.IsWindow(hwnd));
            Assert.Same(window, Fields.Get<PreviewWindow>(controller, "_previewWindow"));
            Assert.False(Fields.Get<bool>(controller, "_previewVisible"));
        });
    }

    [Fact]
    public void AnotherAppsCloseOnASigningGlanceAsksToo()
    {
        WithManagedWindow(pinned: false, withWork: true, (controller, window, hwnd, surface) =>
        {
            NativeMethods.SendMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            Pump();

            Assert.True(NativeMethods.IsWindow(hwnd));
            Assert.True(Fields.Get<bool>(controller, "_previewVisible"));
            Assert.True(SignMode.SaveChoiceShown(surface!));
        });
    }

    [Fact]
    public void AltF4OnASigningPinnedWindowAsksAndKeepsIt()
    {
        WithManagedWindow(pinned: true, withWork: true, (controller, window, hwnd, surface) =>
        {
            AltF4(hwnd);

            Assert.True(NativeMethods.IsWindow(hwnd));
            Assert.Contains(window, Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows"));
            Assert.True(SignMode.SaveChoiceShown(surface!));
        });
    }

    [Fact]
    public void AltF4ClosesAPinnedWindowAndFreesItsSlot()
    {
        WithManagedWindow(pinned: true, withWork: false, (controller, window, hwnd, _) =>
        {
            AltF4(hwnd);

            Assert.False(NativeMethods.IsWindow(hwnd));
            Assert.DoesNotContain(window, Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows"));
        });
    }

    [Fact]
    public void AWindowClosedPastTheGateIsLetGo()
    {
        // App shutdown closes windows and ignores the cancel. Whatever does
        // that, the next Space must not show a closed window.
        WithManagedWindow(pinned: false, withWork: false, (controller, window, hwnd, _) =>
        {
            CloseIgnoringCancel(window);

            Assert.False(NativeMethods.IsWindow(hwnd));
            Assert.Null(Fields.Get<PreviewWindow?>(controller, "_previewWindow"));
            Assert.False(Fields.Get<bool>(controller, "_previewVisible"));
        });

        WithManagedWindow(pinned: true, withWork: false, (controller, window, hwnd, _) =>
        {
            CloseIgnoringCancel(window);

            Assert.DoesNotContain(window, Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows"));
        });
    }

    [Fact]
    public void QuitOverASigningPinnedWindowAsksAndStays()
    {
        WithManagedWindow(pinned: true, withWork: true, (controller, window, hwnd, surface) =>
        {
            Assert.False(controller.CanQuit());
            Assert.True(SignMode.SaveChoiceShown(surface!));
            Assert.Contains(window, Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows"));
        });
    }

    [Fact]
    public void QuitOverASigningGlanceAsksAndStays()
    {
        WithManagedWindow(pinned: false, withWork: true, (controller, window, hwnd, surface) =>
        {
            Assert.False(controller.CanQuit());
            Assert.True(SignMode.SaveChoiceShown(surface!));
        });
    }

    [Fact]
    public void QuitWithNothingToSaveGoesAhead()
    {
        WithManagedWindow(pinned: true, withWork: false, (controller, _, _, _) =>
        {
            Assert.True(controller.CanQuit());
        });
    }

    /// <summary>
    /// A window built by the controller, the way a Space press builds one,
    /// with a native handle but never shown.
    /// </summary>
    private static void WithManagedWindow(
        bool pinned,
        bool withWork,
        Action<SpacePreviewController, PreviewWindow, IntPtr, FillSignSurface?> test)
    {
        Sta.Run(() =>
        {
            var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

            // As if a fullscreen app were in front, so the test never
            // installs a real mouse hook for the window.
            Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
            var window = (PreviewWindow)Fields.Call(controller, "CreatePreviewWindow")!;
            try
            {
                var surface = withWork ? SignMode.Enter(window, withWork: true) : null;
                if (pinned)
                {
                    window.SetPinned(true);

                    // Floating keeps it off the user's taskbar.
                    window.AlwaysOnTopButton.IsChecked = true;
                    Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows").Add(window);
                }
                else
                {
                    Fields.Set(controller, "_previewWindow", window);
                    Fields.Set(controller, "_previewVisible", true);
                }

                var hwnd = new WindowInteropHelper(window).EnsureHandle();
                test(controller, window, hwnd, surface);
            }
            finally
            {
                // Dispose is the controller's own close.
                controller.Dispose();
            }
        });
    }

    /// <summary>Alt+F4 reaches the window as SC_CLOSE, which becomes WM_CLOSE.</summary>
    private static void AltF4(IntPtr hwnd)
    {
        NativeMethods.SendMessage(hwnd, WmSysCommand, new IntPtr(ScClose), IntPtr.Zero);
        Pump();
    }

    /// <summary>Runs what the close queued for later on this thread.</summary>
    private static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

    /// <summary>What Application.Shutdown does to each open window.</summary>
    private static void CloseIgnoringCancel(Window window)
    {
        typeof(Window)
            .GetMethod("InternalClose", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(window, [true, true]);
        Pump();
    }
}
