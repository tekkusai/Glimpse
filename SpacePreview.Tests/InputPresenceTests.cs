using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// "Glimpse is present only where there are files." The keyboard hook is in
/// while Explorer, the desktop, a file picker or Glimpse itself is in front,
/// and a dialog counts as a file picker only once its own windows say so.
/// A dialog that is not one is looked at again only when it builds the
/// frame that would make it one, and only while it is in front.
/// </summary>
public sealed class InputPresenceTests
{
    // What an Open or Save dialog holds, read from real ones on build 26300:
    // Explorer's frame, and inside it the shell view once the folder has
    // opened. An older-style dialog holds the view alone.
    private const string Frame = "DUIViewWndClassName";
    private const string View = "SHELLDLL_DefView";

    [Fact]
    public void AFilePickerIsADialogThatHoldsAShellViewOrExplorersFrame()
    {
        using var opening = TestWindow.Root("#32770");
        using var openingFrame = opening.Child(Frame);

        using var open = TestWindow.Root("#32770");
        using var openFrame = open.Child(Frame);
        using var openHost = openFrame.Child("DirectUIHWND");
        using var openView = openHost.Child(View);

        using var olderStyle = TestWindow.Root("#32770");
        using var olderView = olderStyle.Child(View);

        using var messageBox = TestWindow.Root("#32770");
        using var message = messageBox.Child("Static");
        using var ok = messageBox.Child("Button");

        using var explorer = TestWindow.Root("CabinetWClass");
        using var explorerView = explorer.Child(View);

        Assert.True(CommonFileDialogSelectionService.IsFilePicker(opening.Handle));
        Assert.True(CommonFileDialogSelectionService.IsFilePicker(open.Handle));
        Assert.True(CommonFileDialogSelectionService.IsFilePicker(olderStyle.Handle));
        Assert.False(CommonFileDialogSelectionService.IsFilePicker(messageBox.Handle));
        Assert.False(CommonFileDialogSelectionService.IsFilePicker(explorer.Handle));
        Assert.False(CommonFileDialogSelectionService.IsFilePicker(IntPtr.Zero));
    }

    /// <summary>
    /// The fault in 3.0.0: every dialog was served, so the hook sat over
    /// message boxes, installers and dialog-based apps.
    /// </summary>
    [Fact]
    public Task TheKeyboardHookIsOutOverADialogWithNoFilesInIt() => WithController(controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        using var explorer = TestWindow.Root("CabinetWClass");
        using var messageBox = TestWindow.Root("#32770");
        using var message = messageBox.Child("Static");
        using var ok = messageBox.Child("Button");
        messageBox.Show();
        using var picker = TestWindow.Root("#32770");
        using var frame = picker.Child(Frame);
        picker.Show();

        Fields.Call(controller, "OnForeground", explorer.Handle);
        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook was never installed over Explorer.");

        Fields.Call(controller, "OnForeground", messageBox.Handle);
        Assert.True(windows.Removed.Wait(TimeSpan.FromSeconds(5)), "The hook stayed in over a dialog with no files in it.");

        Fields.Call(controller, "OnForeground", picker.Handle);
        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook did not come in for a file picker.");
        return Task.CompletedTask;
    });

    /// <summary>
    /// The fault in 3.0.0: a foreground event names the window an app
    /// brought forward, and apps raise it for windows that never reach the
    /// front. On one PC 37 of 82 events did. One of those while Explorer was
    /// in front took the hook out, and Space did nothing there until the
    /// user left and came back.
    /// </summary>
    [Fact]
    public Task AForegroundEventForAWindowThatIsNotInFrontChangesNothing() => WithController(controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        using var explorer = TestWindow.Root("CabinetWClass");
        using var elsewhere = TestWindow.Root("GlimpseTestSomeOtherApp");
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => explorer.Handle));

        RaiseForegroundEvent(controller, explorer.Handle);
        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook was never installed over Explorer.");

        RaiseForegroundEvent(controller, elsewhere.Handle);
        Assert.True(
            Fields.Get<bool>(controller, "_keyboardHookWanted"),
            "An event for a window that is not in front took the hook out from under Explorer.");

        // With nothing in front, the event is all there is to go on.
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => IntPtr.Zero));
        RaiseForegroundEvent(controller, elsewhere.Handle);
        Assert.False(Fields.Get<bool>(controller, "_keyboardHookWanted"));
        return Task.CompletedTask;
    });

    private static void RaiseForegroundEvent(SpacePreviewController controller, IntPtr window) =>
        Fields.Call(
            controller,
            "OnForegroundChanged",
            IntPtr.Zero,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            window,
            0,
            0,
            0u,
            0u);

    /// <summary>
    /// A dialog takes the foreground while it is still hidden and holds only
    /// its template's controls; an Open dialog builds its frame after that.
    /// 3.0.0 bridged the gap by looking again every 500 ms, at every dialog.
    /// The frame being created is the signal. A real Open dialog on this PC
    /// created 46 other windows first; none of those is a reason to look.
    /// </summary>
    [Fact]
    public Task AFilePickerStillBeingBuiltIsServedFromTheMomentItHasItsFrame() => WithController(async controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        var monitor = PickerMonitor(controller);
        var looks = 0;
        monitor.DialogBuilt += _ => looks++;
        using var picker = TestWindow.Root("#32770");
        using var nameBox = picker.Child("Static");
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => picker.Handle));

        Fields.Call(controller, "OnForeground", picker.Handle);
        Assert.False(monitor.IsWatching(picker.Handle));
        Assert.False(windows.Installed.IsSet, "The hook was in over a dialog nothing had shown to be a file picker.");

        using var button = picker.Child("Button");
        using var edit = picker.Child("Edit");
        await Task.Delay(100);
        Assert.Equal(0, looks);

        using var frame = picker.Child(Frame);
        await Until(() => monitor.IsWatching(picker.Handle));

        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook did not come in when the picker had its frame.");
        Assert.Equal(1, looks);
        Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));
    });

    /// <summary>
    /// The fault: a dialog on screen with no frame yet was judged once and
    /// not looked at again while it stayed in front. Nothing in Windows
    /// makes a picker build its frame before it shows itself; the order is
    /// the app's. A real GetOpenFileName dialog whose hook shows it, run on
    /// this PC: foreground, show, and only then SHELLDLL_DefView.
    /// </summary>
    [Fact]
    public Task AFilePickerThatBuildsItsViewAfterItIsOnScreenIsServedOnceItHasOne() => WithController(async controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        using var picker = TestWindow.Root("#32770");
        using var nameBox = picker.Child("Static");
        picker.Show();
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => picker.Handle));

        Fields.Call(controller, "OnForeground", picker.Handle);
        Assert.False(PickerMonitor(controller).IsWatching(picker.Handle));
        Assert.False(windows.Installed.IsSet, "The hook was in over a dialog nothing had shown to be a file picker.");

        using var view = picker.Child(View);
        await Until(() => PickerMonitor(controller).IsWatching(picker.Handle));

        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook did not come in when the picker had its view.");
    });

    /// <summary>
    /// A message box, an installer page, a dialog-based app: listened to
    /// for a frame and nothing else, and only while it is in front.
    /// </summary>
    [Fact]
    public Task ADialogWithNoFilesIsListenedToForAFrameAndOnlyWhileItIsInFront() => WithController(async controller =>
    {
        var monitor = PickerMonitor(controller);
        using var dialog = TestWindow.Root("#32770");
        using var message = dialog.Child("Static");
        using var elsewhere = TestWindow.Root("GlimpseTestSomeOtherApp");

        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => dialog.Handle));
        Fields.Call(controller, "OnForeground", dialog.Handle);
        Assert.False(monitor.IsWatching(dialog.Handle));
        Assert.NotEqual(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));
        Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_objectHook"));

        // It leaves the front: nothing is left listening, and what it builds
        // from here on is looked at when it comes back.
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => elsewhere.Handle));
        Fields.Call(controller, "OnForeground", elsewhere.Handle);
        Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));
        using var frame = dialog.Child(Frame);
        await Task.Delay(100);
        Assert.False(monitor.IsWatching(dialog.Handle));

        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => dialog.Handle));
        Fields.Call(controller, "OnForeground", dialog.Handle);
        Assert.True(monitor.IsWatching(dialog.Handle));
        Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));
    });

    /// <summary>
    /// A window of Glimpse's own in front is the dialog leaving the front
    /// too: a pinned preview that is clicked, the settings window. The
    /// picker monitor is not told about Glimpse's windows, so the listening
    /// ends here.
    /// </summary>
    [Fact]
    public Task ADialogWithNoFilesIsNotListenedToBehindAWindowOfGlimpsesOwn() => WithController(controller =>
    {
        var monitor = PickerMonitor(controller);
        using var dialog = TestWindow.Root("#32770");
        using var message = dialog.Child("Static");
        using var own = TestWindow.Root("GlimpseTestOwnWindow");

        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => dialog.Handle));
        Fields.Call(controller, "OnForeground", dialog.Handle);
        Assert.NotEqual(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));

        // The test's windows are this process's: from here they are Glimpse's own.
        Fields.Set(controller, "_processId", (uint)Environment.ProcessId);
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => own.Handle));
        Fields.Call(controller, "OnForeground", own.Handle);

        Assert.Equal(IntPtr.Zero, Fields.Get<IntPtr>(monitor, "_buildHook"));
        return Task.CompletedTask;
    });

    /// <summary>
    /// 3.0.0 looked at an unproven dialog again every 500 ms for as long as
    /// it stayed in front. Nothing in the picker monitor runs on a clock now.
    /// </summary>
    [Fact]
    public void NothingInThePickerMonitorRunsOnATimer()
    {
        var timers = typeof(CommonFileDialogSelectionMonitor)
            .GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(field => typeof(DispatcherTimer).IsAssignableFrom(field.FieldType)
                || typeof(Timer).IsAssignableFrom(field.FieldType)
                || typeof(System.Timers.Timer).IsAssignableFrom(field.FieldType))
            .Select(field => field.Name);

        Assert.Empty(timers);
    }

    /// <summary>
    /// A bare Space where nothing offers a selection used to start a task
    /// that opened the process token of the app in front. Windows never
    /// calls this hook while an elevated window has the keyboard, so the
    /// check could not find what it looked for. The key runs nothing.
    /// </summary>
    [Fact]
    public void NoKeyAsksWhetherTheAppInFrontIsElevated()
    {
        Assert.DoesNotContain("CheckElevation", Enum.GetNames<KeyAction>());
    }

    /// <summary>
    /// The access notice is the one thing Glimpse does for a picker it
    /// cannot read. It starts when the picker comes to the front, which
    /// Windows reports whatever the app's rights are, and needs no key.
    /// </summary>
    [Fact]
    public Task AnElevatedAppsPickerIsNamedWhenItComesToTheFront() => WithController(async controller =>
    {
        AllowAccessNotice(true);
        var elevated = new PreviewAccessWarningInfo("ADMINAPP", "AdminApp", 42, PreviewAccessWarningKind.ConfirmedElevationMismatch);
        var asked = new List<IntPtr>();
        Fields.Set(controller, "_readAccessWarning", (Func<IntPtr, PreviewAccessWarningInfo?>)(window =>
        {
            lock (asked)
            {
                asked.Add(window);
            }

            return elevated;
        }));
        var warnings = new List<PreviewAccessWarningInfo>();
        controller.PreviewAccessWarningDetected += warnings.Add;
        using var messageBox = TestWindow.Root("#32770");
        using var message = messageBox.Child("Static");
        messageBox.Show();
        using var picker = TestWindow.Root("#32770");
        using var frame = picker.Child(Frame);
        picker.Show();

        // The same app's message box is not a picker: nothing is asked of it.
        Fields.Call(controller, "OnForeground", messageBox.Handle);
        Fields.Call(controller, "OnForeground", picker.Handle);
        await Until(() => warnings.Count > 0);
        Assert.Equal([elevated], warnings);

        // The picker coming forward again is the same picker.
        Fields.Call(controller, "OnForeground", messageBox.Handle);
        Fields.Call(controller, "OnForeground", picker.Handle);
        await Task.Delay(150);
        Assert.Single(warnings);
        lock (asked)
        {
            Assert.Equal([picker.Handle], asked);
        }
    });

    [Fact]
    public Task APickerIsNotAskedAboutOnceTheNoticeHasBeenClosedForGood() => WithController(async controller =>
    {
        AllowAccessNotice(false);
        try
        {
            var asked = 0;
            Fields.Set(controller, "_readAccessWarning", (Func<IntPtr, PreviewAccessWarningInfo?>)(_ =>
            {
                Interlocked.Increment(ref asked);
                return null;
            }));
            using var picker = TestWindow.Root("#32770");
            using var frame = picker.Child(Frame);
            picker.Show();

            Fields.Call(controller, "OnForeground", picker.Handle);
            await Task.Delay(150);

            Assert.True(PickerMonitor(controller).IsWatching(picker.Handle));
            Assert.Equal(0, Volatile.Read(ref asked));
        }
        finally
        {
            AllowAccessNotice(true);
        }
    });

    private static CommonFileDialogSelectionMonitor PickerMonitor(SpacePreviewController controller) =>
        Fields.Get<CommonFileDialogSelectionMonitor>(controller, "_commonFileDialogMonitor");

    private static void AllowAccessNotice(bool allowed) =>
        Settings.Update(settings => settings with
        {
            PreviewAccessWarningLifetimeShows = 0,
            PreviewAccessWarningLastShownUtc = null,
            PreviewAccessWarningDismissedForever = !allowed
        });

    /// <summary>Lets the dispatcher pump, so window events arrive, until the condition holds.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.ElapsedMilliseconds < 5000, "The event never arrived.");
            await Task.Delay(10);
        }
    }

    private static Task WithController(Func<SpacePreviewController, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // The test's windows stand in for another app's, and a foreground
        // watch is in place, so a window Glimpse does not serve takes the
        // hook out. The keyboard hook is only ever a fake one: a picker
        // coming forward asks for the hook, and no test may put a real one
        // on this PC's keyboard. A test that watches the hook attaches its own.
        new FakeHookDelivery().Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        Fields.Set(controller, "_processId", 0u);
        Fields.Set(controller, "_foregroundHook", new IntPtr(1));
        try
        {
            await test(controller);
        }
        finally
        {
            Fields.Set(controller, "_foregroundHook", IntPtr.Zero);
            controller.Dispose();
        }
    });
}
