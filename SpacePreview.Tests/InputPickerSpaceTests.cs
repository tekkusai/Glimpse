using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Space over a file picker's list. A press the hook thread swallowed ends
/// in a preview of the file that was selected at the press. A press that
/// went to the dialog because nothing was verified yet opens that file once
/// the picker has answered, or opens nothing: never a file the user moved to
/// afterwards.
/// </summary>
public sealed class InputPickerSpaceTests
{
    private const int Space = NativeMethods.VK_SPACE;

    /// <summary>
    /// The fault: the hook thread claimed Space on a verified selection, a
    /// selection event marked the cache as being read again before the key's
    /// turn on the dispatcher, and the open was dropped. The user got neither
    /// a preview nor the dialog's own Space.
    /// </summary>
    [Fact]
    public Task ASwallowedSpaceOpensTheFileItWasPressedOnThoughThePickerIsBeingReadAgain() => WithController(async controller =>
    {
        using var picker = TestPicker.Open();
        var pressed = picker.Selected("pressed.txt");
        var lookups = new PickerLookups { Selected = pressed };
        lookups.Attach(Monitor(controller));
        var shown = Shown(controller);
        BringToFront(controller, picker);
        await HookThread.Until(() => Cached(controller, picker) == pressed, "The picker's selection was never verified.");

        // What the hook thread reads at the key: a verified selection.
        Assert.True(HookThread.KeyDown(controller, InList(picker) with { PickerSelection = pressed }), "Space on a verified selection was not swallowed.");

        // Before the key's turn on the dispatcher the picker reports a move.
        lookups.Hold();
        SelectionMoved(controller, picker);
        Assert.Null(Cached(controller, picker));

        await HookThread.Idle();
        Assert.Equal([pressed], shown);
        lookups.Release();
    });

    /// <summary>
    /// The fault: Space waited for a selection that was still being read,
    /// the user clicked another file, and the read that followed opened a
    /// preview of the clicked file. Nobody pressed Space on it.
    /// </summary>
    [Fact]
    public Task ASpaceThatWaitedOpensNothingOnceThePickerHasMoved() => WithController(async controller =>
    {
        using var picker = TestPicker.Open();
        var lookups = new PickerLookups { Selected = picker.Selected("pressed.txt") };
        lookups.Attach(Monitor(controller));
        var shown = Shown(controller);

        // The picker comes forward and its first read has not come back.
        lookups.Hold();
        BringToFront(controller, picker);
        Assert.False(HookThread.KeyDown(controller, InList(picker)), "Space on an unverified selection was swallowed.");
        await HookThread.Idle();
        Assert.NotNull(Waiting(controller).Current);

        // A click on another file: no key, and no mouse observer while no preview is up.
        lookups.Selected = picker.Selected("clicked.txt");
        SelectionMoved(controller, picker);
        lookups.Release();
        await HookThread.Until(() => Cached(controller, picker) == lookups.Selected, "The picker was never read again.");
        await HookThread.Idle();

        Assert.Empty(shown);
        Assert.Null(Waiting(controller).Current);
    });

    /// <summary>
    /// A press that lands while the picker is being read waits for that
    /// read and no other. It was asked for at the picker's last move and
    /// nothing has moved since, so its answer is the file under the press;
    /// asking again would put a second wait behind the first.
    /// </summary>
    [Fact]
    public Task ASpaceThatWaitedOpensTheFileTheReadUnderWayReports() => WithController(async controller =>
    {
        using var picker = TestPicker.Open();
        var pressed = picker.Selected("pressed.txt");
        var lookups = new PickerLookups { Selected = pressed };
        lookups.Attach(Monitor(controller));
        var shown = Shown(controller);

        lookups.Hold();
        BringToFront(controller, picker);
        await HookThread.Until(() => lookups.Started == 1, "The picker was never asked.");
        Assert.False(HookThread.KeyDown(controller, InList(picker)));
        await HookThread.Idle();

        lookups.Release();
        await HookThread.Until(() => shown.Count > 0, "The press that waited never opened its file.");

        Assert.Equal([pressed], shown);
        Assert.Equal(1, lookups.Started);
        Assert.Null(Waiting(controller).Current);
    });

    /// <summary>
    /// With no read under way and nothing verified (the last read found
    /// nothing it could resolve), the press asks for one.
    /// </summary>
    [Fact]
    public Task ASpaceWithNoReadUnderWayAsksForOne() => WithController(async controller =>
    {
        using var picker = TestPicker.Open();
        var lookups = new PickerLookups();
        lookups.Attach(Monitor(controller));
        var shown = Shown(controller);
        var completed = 0;
        Monitor(controller).RefreshCompleted += (_, _, _) => completed++;
        BringToFront(controller, picker);
        await HookThread.Until(() => completed == 1, "The first read never came back.");

        var pressed = picker.Selected("pressed.txt");
        lookups.Selected = pressed;
        Assert.False(HookThread.KeyDown(controller, InList(picker)));
        await HookThread.Until(() => shown.Count > 0, "The press never opened its file.");

        Assert.Equal([pressed], shown);
        Assert.Equal(2, lookups.Started);
    });

    /// <summary>
    /// A picker that reports nothing selected has nothing to preview. The
    /// press went to the dialog; it is not kept for whatever is selected next.
    /// </summary>
    [Fact]
    public Task ASpaceThatWaitedIsSpentWhenThePickerReportsNothingSelected() => WithController(async controller =>
    {
        using var picker = TestPicker.Open();
        var lookups = new PickerLookups();
        lookups.Attach(Monitor(controller));
        var shown = Shown(controller);
        var completed = 0;
        Monitor(controller).RefreshCompleted += (_, _, _) => completed++;

        lookups.Hold();
        BringToFront(controller, picker);
        Assert.False(HookThread.KeyDown(controller, InList(picker)));
        await HookThread.Idle();
        lookups.Release();
        await HookThread.Until(() => completed > 0);
        Assert.Null(Waiting(controller).Current);

        // The next file the picker reports is not the press's.
        lookups.Selected = picker.Selected("later.txt");
        SelectionMoved(controller, picker);
        await HookThread.Until(() => Cached(controller, picker) == lookups.Selected);
        await HookThread.Idle();
        Assert.Empty(shown);
    });

    /// <summary>A bare Space with the picker's file list in front, as the hook thread reads it.</summary>
    private static KeyPress InList(TestPicker picker) => new()
    {
        Vk = Space,
        Root = picker.Handle,
        RootFileListFocus = true,
        Context = picker.Context
    };

    private static CommonFileDialogSelectionMonitor Monitor(SpacePreviewController controller) =>
        Fields.Get<CommonFileDialogSelectionMonitor>(controller, "_commonFileDialogMonitor");

    private static DeferredFileDialogPreviewIntentSlot Waiting(SpacePreviewController controller) =>
        Fields.Get<DeferredFileDialogPreviewIntentSlot>(controller, "_pendingFileDialogPreview");

    private static ExplorerSelection? Cached(SpacePreviewController controller, TestPicker picker) =>
        Monitor(controller).TryGetCachedSelection(picker.Handle, out var selection) ? selection : null;

    /// <summary>Stands in for the preview window going up, and keeps what was asked for.</summary>
    private static List<ExplorerSelection> Shown(SpacePreviewController controller)
    {
        var shown = new List<ExplorerSelection>();
        Fields.Set(controller, "_showPreview", (Action<ExplorerSelection, long>)((selection, _) => shown.Add(selection)));
        return shown;
    }

    /// <summary>The picker takes the foreground, as the foreground watch reports it.</summary>
    private static void BringToFront(SpacePreviewController controller, TestPicker picker)
    {
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => picker.Handle));
        Fields.Call(controller, "OnForeground", picker.Handle);
    }

    /// <summary>The picker's selection event, as Windows delivers it to the monitor.</summary>
    private static void SelectionMoved(SpacePreviewController controller, TestPicker picker) =>
        Fields.Call(
            Monitor(controller),
            "OnObjectChanged",
            IntPtr.Zero,
            NativeMethods.EVENT_OBJECT_FOCUS + 1,
            picker.List,
            0,
            0,
            0u,
            0u);

    private static Task WithController(Func<SpacePreviewController, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // The test's windows stand in for another app's. No real hook goes
        // in: the keyboard hook is the fake one and no preview goes up, so
        // the mouse observer is never asked for. Nothing asks about rights.
        new FakeHookDelivery().Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        Fields.Set(controller, "_processId", 0u);
        Fields.Set(controller, "_foregroundHook", new IntPtr(1));
        Fields.Set(controller, "_readAccessWarning", (Func<IntPtr, PreviewAccessWarningInfo?>)(_ => null));
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
