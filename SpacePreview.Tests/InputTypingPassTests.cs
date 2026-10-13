using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Text boxes keep their keys. Only an Enter that ends the owner's glance
/// is posted to the dispatcher. What the hook thread keeps for itself still runs.
/// </summary>
public sealed class InputTypingPassTests
{
    private const int Space = NativeMethods.VK_SPACE;

    private static readonly IntPtr Owner = new(0x2000);

    private static readonly ForegroundSelectionContext Explorer =
        new(Owner, SelectionSource.Explorer, "CabinetWClass");

    /// <summary>
    /// The fault: F, R, Space, the arrows, Delete, Enter and Esc are keys the
    /// keymap can claim, so each one typed into a rename or a search box
    /// posted a callback that captured window state and routed it again.
    /// </summary>
    [Fact]
    public Task AKeyTypedIntoATextBoxPostsNothing() => WithController(controller =>
    {
        // Counts what is posted to this dispatcher from here on, from any thread.
        var posts = 0;
        DispatcherHookEventHandler count = (_, _) => Interlocked.Increment(ref posts);
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationPosted += count;
        try
        {
            foreach (var vk in new[]
                     {
                         NativeMethods.VK_F, NativeMethods.VK_R, Space, NativeMethods.VK_LEFT, NativeMethods.VK_DELETE,
                         NativeMethods.VK_HOME, NativeMethods.VK_RETURN, NativeMethods.VK_ESCAPE
                     })
            {
                Assert.False(HookThread.KeyDown(controller, Typed(vk)));
                HookThread.KeyUp(controller, vk);
                Assert.True(Volatile.Read(ref posts) == 0, $"Key 0x{vk:X} typed into a text box posted to the dispatcher.");
            }

            // Outside a text box the same key takes its turn on the dispatcher.
            HookThread.KeyDown(controller, At(NativeMethods.VK_LEFT));
            Assert.Equal(1, Volatile.Read(ref posts));
            HookThread.KeyUp(controller, NativeMethods.VK_LEFT);
            Assert.Equal(2, Volatile.Read(ref posts));
        }
        finally
        {
            hooks.OperationPosted -= count;
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task EnterInARenamePostsAndEndsTheGlanceButALetterPostsNothing() => WithController(async controller =>
    {
        Fields.Set(controller, "_previewVisible", true);
        Fields.Set(controller, "_previewOwnerRootHwnd", Owner);
        Fields.Call(controller, "PublishKeyState");

        var posts = 0;
        DispatcherHookEventHandler count = (_, _) => Interlocked.Increment(ref posts);
        var hooks = Dispatcher.CurrentDispatcher.Hooks;
        hooks.OperationPosted += count;
        try
        {
            Assert.False(HookThread.KeyDown(controller, Typed(NativeMethods.VK_F)));
            HookThread.KeyUp(controller, NativeMethods.VK_F);
            Assert.Equal(0, Volatile.Read(ref posts));
            Assert.False(controller.NoPreviewOpen);

            Assert.False(HookThread.KeyDown(controller, Typed(NativeMethods.VK_RETURN)));
            Assert.Equal(1, Volatile.Read(ref posts));
            Assert.False(controller.NoPreviewOpen);
        }
        finally
        {
            hooks.OperationPosted -= count;
        }

        await HookThread.Idle();
        Assert.True(controller.NoPreviewOpen);
    });

    /// <summary>
    /// The fault: Esc in Explorer's header leaves the keyboard in the
    /// header, so with every key there counted as typing, a glance opened
    /// before Ctrl+L or a click on Sort could not be closed by Esc again
    /// until a file was clicked. 3.0.0 closed it.
    /// </summary>
    [Fact]
    public Task EscInTheHeaderIsTakenAndEndsTheGlance() => WithController(async controller =>
    {
        Fields.Set(controller, "_previewVisible", true);
        Fields.Set(controller, "_previewOwnerRootHwnd", Owner);
        Fields.Call(controller, "PublishKeyState");

        Assert.False(HookThread.KeyDown(controller, Typed(NativeMethods.VK_ESCAPE)), "Esc in a rename was taken.");
        HookThread.KeyUp(controller, NativeMethods.VK_ESCAPE);
        await HookThread.Idle();
        Assert.False(controller.NoPreviewOpen);

        Assert.True(HookThread.KeyDown(controller, Typed(NativeMethods.VK_ESCAPE) with { RootHeaderFocus = true }));
        await HookThread.Idle();
        Assert.True(controller.NoPreviewOpen);
    });

    /// <summary>
    /// What was read of the header holds only until a key reaches
    /// Explorer: that key may have moved the focus into the search box.
    /// A key Glimpse takes never arrives there and changes nothing.
    /// </summary>
    [Fact]
    public Task AKeyThatReachesExplorerForgetsTheHeaderAndOneGlimpseTakesDoesNot() => WithController(controller =>
    {
        var header = new ExplorerHeaderFocus(() => Owner, _ => true, _ => true);
        Fields.Set(controller, "_headerFocus", header);
        header.Watch(Owner);

        Trust(header);
        Assert.True(HookThread.KeyDown(controller, At(Space)), "Space over the file list was not taken.");
        HookThread.KeyUp(controller, Space);
        Assert.True(header.IsPlain(Owner), "A key Glimpse took forgot the header.");

        // A letter: never examined, always typed.
        Assert.False(HookThread.KeyDown(controller, new KeyPress { Vk = 'A', Root = Owner }));
        Assert.False(header.IsPlain(Owner), "A letter that reached Explorer left the header trusted.");

        // An arrow with nothing open: examined, and Explorer's.
        Trust(header);
        Assert.False(HookThread.KeyDown(controller, At(NativeMethods.VK_LEFT)));
        Assert.False(header.IsPlain(Owner), "An arrow that reached Explorer left the header trusted.");
        return Task.CompletedTask;
    });

    private static void Trust(ExplorerHeaderFocus header)
    {
        for (var read = 0; read < ExplorerHeaderFocus.ReadsToTrust; read++)
        {
            header.Tick(Environment.TickCount64);
        }

        Assert.True(header.IsPlain(Owner));
    }

    /// <summary>
    /// The latch that tells a held Space from a new press is the hook
    /// thread's own, and it follows every Space, typed or not. Without it a
    /// Space still held when focus leaves a text box would open a preview.
    /// </summary>
    [Fact]
    public Task ASpaceHeldThroughATextBoxIsStillTheSamePress() => WithController(controller =>
    {
        Fields.Set(controller, "_spaceKey", new SpaceKeyLatch(repeatWindowMilliseconds: 500));

        Assert.True(HookThread.KeyDown(controller, At(Space), time: 1000));
        Assert.False(HookThread.KeyDown(controller, Typed(Space), time: 1400));
        Assert.False(HookThread.KeyDown(controller, Typed(Space), time: 1800));
        Assert.False(HookThread.KeyDown(controller, At(Space), time: 2200), "A held Space opened twice.");

        HookThread.KeyUp(controller, Space);
        Assert.True(HookThread.KeyDown(controller, At(Space), time: 2600));
        return Task.CompletedTask;
    });

    /// <summary>
    /// A picker Space that waits for its selection is ended by any other
    /// key, and a letter typed into the picker's name box is one.
    /// </summary>
    [Fact]
    public Task AKeyTypedIntoATextBoxStillEndsAPickerSpaceThatWaits() => WithController(controller =>
    {
        var slot = Fields.Get<DeferredFileDialogPreviewIntentSlot>(controller, "_pendingFileDialogPreview");
        slot.Arm(Owner, requestGeneration: 1, armedAtTick: Environment.TickCount64);

        Assert.False(HookThread.KeyDown(controller, Typed(NativeMethods.VK_F)));

        Assert.Null(slot.Current);
        return Task.CompletedTask;
    });

    /// <summary>A key over Explorer's file list, as the hook thread reads it.</summary>
    private static KeyPress At(int vk) => new()
    {
        Vk = vk,
        Root = Owner,
        Context = vk == Space ? Explorer : null
    };

    /// <summary>The same key with a rename box holding the keyboard.</summary>
    private static KeyPress Typed(int vk) => new() { Vk = vk, Root = Owner, RootTyping = true };

    private static Task WithController(Func<SpacePreviewController, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // As if a fullscreen app were in front, so no test installs a real
        // mouse hook. No keyboard hook is asked for.
        Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
        try
        {
            await test(controller);
        }
        finally
        {
            controller.Dispose();
        }
    });
}
