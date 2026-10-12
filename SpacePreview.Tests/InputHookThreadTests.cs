using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The keyboard hook thread answers Windows from the state the dispatcher
/// published and never waits for it. What a key means is run later on the
/// dispatcher, from the state as it is then. Each test here is one way the
/// two can be out of step, and what happens to the key.
/// </summary>
public sealed class InputHookThreadTests
{
    private const int Space = NativeMethods.VK_SPACE;
    private const int Esc = NativeMethods.VK_ESCAPE;
    private const int Right = NativeMethods.VK_RIGHT;
    private const int Down = NativeMethods.VK_DOWN;

    private static readonly IntPtr Owner = new(0x2000);

    private static readonly ForegroundSelectionContext Explorer =
        new(Owner, SelectionSource.Explorer, "CabinetWClass");

    /// <summary>
    /// The fix's reason to exist, through the real hook thread: with the UI
    /// thread busy, Space over a glance's owner is swallowed at once, and the
    /// glance ends when the UI thread gets to it.
    /// </summary>
    [Fact]
    public Task ASwallowedKeyIsAnsweredWhileTheDispatcherIsBusyAndItsMeaningFollows() => WithController(async controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        ShowGlance(controller);

        // A served window comes to the front: the hook is asked for.
        Fields.Call(controller, "SyncKeyboardHook", Owner, (uint)Environment.ProcessId);
        Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook was never installed.");
        Assert.NotEqual(Environment.CurrentManagedThreadId, windows.InstallThread);

        // No await from here to the assert: the dispatcher runs this test
        // body and nothing else.
        ReadsAs(controller, At(Owner, Space));
        var swallowed = OffDispatcher(() => windows.Deliver(NativeMethods.WM_KEYDOWN, Space, timeoutMilliseconds: 3000));

        Assert.True(swallowed, "Space over the owner was not swallowed while the UI thread was busy.");
        Assert.True(Visible(controller), "The glance ended before the dispatcher ran.");

        await Idle();
        Assert.False(Visible(controller));
    });

    /// <summary>
    /// "An arrow pressed in the same instant the preview closes", when a key
    /// closed it. The hook thread claimed that key itself, so it does not
    /// wait to be told: the arrow goes to Explorer as it would have in 3.0.
    /// </summary>
    [Fact]
    public Task AfterTheKeyThatEndsTheGlanceTheNextArrowIsExplorers() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        ShowGlance(controller);

        Assert.True(KeyDown(controller, At(Owner, Esc)));
        Assert.False(KeyDown(controller, At(Owner, Right)), "The arrow was swallowed for a glance its own Esc had ended.");
        Assert.True(Visible(controller), "The dispatcher was not meant to have run yet.");

        await Idle();
        Assert.False(Visible(controller));
        Assert.Equal(0, steps());
    });

    /// <summary>
    /// The same instant when something else closes it: a click or a lost
    /// owner whose turn on the dispatcher comes before the key's. The hook
    /// thread could not know, so the arrow is swallowed. It is then dropped:
    /// never run against a glance that is gone.
    /// </summary>
    [Fact]
    public Task AnArrowSwallowedForAGlanceTheDispatcherClosedFirstIsDropped() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        ShowGlance(controller);

        Assert.True(KeyDown(controller, At(Owner, Right)));
        Fields.Call(controller, "HidePreview");

        await Idle();
        Assert.Equal(0, steps());
        Assert.Equal(0, Fields.Get<int>(controller, "_browsePendingDelta"));
        Assert.False(Fields.Get<KeyState>(controller, "_keyState").PreviewVisible);
    });

    [Fact]
    public Task AnArrowClaimedOverALiveGlanceStepsTheShellOnce() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        var window = new PreviewWindow();
        Fields.Set(window, "CurrentPath", Path.Combine(TestDataRoot.Path, "glance.png"));
        ShowGlance(controller, window);

        Assert.True(KeyDown(controller, At(Owner, Right)));
        Assert.Equal(0, steps());

        await Idle();
        Assert.Equal(1, steps());
    });

    /// <summary>
    /// "A Space pressed while a hide is in flight." Both presses are
    /// swallowed; the second one opens again, as two presses always did.
    /// </summary>
    [Fact]
    public Task ASpacePressedWhileAHideIsInFlightOpensAgain() => WithController(async controller =>
    {
        ShowGlance(controller);

        Assert.True(KeyDown(controller, At(Owner, Space), time: 1000));
        KeyUp(controller, Space);
        Assert.True(KeyDown(controller, At(Owner, Space), time: 1040));

        // The dispatcher runs the two keys, and not yet what they queued.
        await Dispatcher.Yield(DispatcherPriority.Send);
        Assert.False(Visible(controller));
        Assert.True(Fields.Get<bool>(controller, "_openPending"));
        Assert.Equal(1, Fields.Get<int>(controller, "_openRequestGeneration"));

        // The queued lookup is called off here: the test has no Explorer.
        Fields.Set(controller, "_openRequestGeneration", 2);
    });

    /// <summary>
    /// Building a glance's window keeps the dispatcher busy for tens of
    /// milliseconds. It says so first, so a key pressed in that stretch is
    /// the glance's, as it was when the key waited for the build.
    /// </summary>
    [Fact]
    public Task TheHookThreadIsToldOfAGlanceBeforeItsWindowIsBuilt() => WithController(controller =>
    {
        using var temp = new TempDirectory();
        using var owner = HiddenWindow();
        var selection = new ExplorerSelection(WritePng(temp.Path), owner.Handle, SelectedPaths: ["a.png", "b.png"]);

        // Parked off screen and frozen, like the probe.
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_previewOwnerRootHwnd", owner.Handle);

        KeyState? told = null;
        var visibleByThen = true;
        window.IsVisibleChanged += (_, _) =>
        {
            if (told is null)
            {
                told = Fields.Get<KeyState?>(controller, "_keyState");
                visibleByThen = Visible(controller);
            }
        };

        Fields.Call(controller, "ShowPreview", selection, 0L);

        Assert.NotNull(told);
        Assert.False(visibleByThen, "The window was shown after the controller counted the glance as up.");
        Assert.True(told.PreviewVisible);
        Assert.Equal(owner.Handle, told.OwnerRoot);
        Assert.True(told.Preview.Browsable);
        Assert.True(told.MultiSelection, "Ctrl+Enter pressed during the build would open every selected file in Explorer.");

        // Shown or refused, what the hook thread holds afterwards is the truth.
        Assert.Equal(Visible(controller), Fields.Get<KeyState>(controller, "_keyState").PreviewVisible);
        return Task.CompletedTask;
    });

    [Fact]
    public Task AnArrowPressedWhileTheWindowIsBuiltBrowsesOnceItIsUp() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        var window = Landing(controller);

        Assert.True(KeyDown(controller, At(Owner, Right)));

        // The build completes.
        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_previewVisible", true);
        await Idle();
        Assert.Equal(1, steps());
    });

    [Fact]
    public Task EscPressedWhileTheWindowIsBuiltEndsTheGlanceOnceItIsUp() => WithController(async controller =>
    {
        var window = Landing(controller);

        Assert.True(KeyDown(controller, At(Owner, Esc)));

        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_previewVisible", true);
        await Idle();
        Assert.False(Visible(controller));
    });

    /// <summary>
    /// The window can fail to show. The key the hook thread swallowed for it
    /// is dropped, and the next one is Explorer's.
    /// </summary>
    [Fact]
    public Task AKeySwallowedForAGlanceThatFailedToShowIsDropped() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        Landing(controller);

        Assert.True(KeyDown(controller, At(Owner, Right)));
        Fields.Call(controller, "HidePreview");
        Assert.False(KeyDown(controller, At(Owner, Right)));

        await Idle();
        Assert.Equal(0, steps());
    });

    /// <summary>
    /// Until the build is announced the hook thread sees an open on its way.
    /// Esc passes there, as Esc during an open always did, and cancels it.
    /// </summary>
    [Fact]
    public Task EscPressedDuringAnOpenLookupCancelsIt() => WithController(async controller =>
    {
        Fields.Set(controller, "_openPending", true);
        Fields.Set(controller, "_openRequestGeneration", 1);
        Fields.Call(controller, "PublishKeyState");

        Assert.False(KeyDown(controller, At(Owner, Esc)));

        await Idle();
        Assert.False(Fields.Get<bool>(controller, "_openPending"));
        Assert.Equal(2, Fields.Get<int>(controller, "_openRequestGeneration"));
        Assert.False(Fields.Get<KeyState>(controller, "_keyState").OpenPending);
    });

    /// <summary>
    /// An arrow that went to Explorer although a glance was up by the time
    /// the dispatcher got to it. Explorer has moved its selection; the
    /// glance follows the move and never steps a second time.
    /// </summary>
    [Fact]
    public Task AnArrowThatReachedExplorerWhileTheGlanceOpenedIsFollowed() => WithController(async controller =>
    {
        var steps = CountBrowseSteps(controller);
        Fields.Set(controller, "_previewVisible", true);
        Fields.Set(controller, "_previewOwnerRootHwnd", Owner);

        Assert.False(KeyDown(controller, At(Owner, Right)));
        await Idle();

        Assert.Equal(0, steps());
        Assert.Equal(1, Fields.Get<long>(controller, "_navigationRefreshGeneration"));
        Assert.Equal(1, Fields.Get<long[]>(controller, "_navigationKeyRefreshTokens")[Right]);
    });

    /// <summary>
    /// Sign mode began after the last publish. The hook thread swallows
    /// Space as a dismissal; the dispatcher knows better and runs nothing.
    /// Its answer then reaches the hook thread, which had counted the glance
    /// as ended, and the glance's keys are the glance's again.
    /// </summary>
    [Fact]
    public Task AKeySwallowedOnOldNewsRunsNothingAndTheNextOneIsRight() => WithController(async controller =>
    {
        var window = new PreviewWindow();
        Fields.Set(window, "CurrentPath", Path.Combine(TestDataRoot.Path, "unsigned.pdf"));
        ShowGlance(controller, window);
        var surface = SignMode.Enter(window, withWork: true);

        Assert.True(KeyDown(controller, At(Owner, Space)));
        Assert.False(KeyDown(controller, At(Owner, Esc)), "Until the dispatcher answers, the hook thread counts the glance as ended.");

        await Idle();
        Assert.True(Visible(controller));
        Assert.False(SignMode.SaveChoiceShown(surface), "The swallowed Space was run as a dismissal over signing work.");

        Assert.True(KeyDown(controller, At(Owner, Esc)));
        await Idle();
        Assert.True(SignMode.SaveChoiceShown(surface));
    });

    /// <summary>
    /// A picker Space that waits for its selection is dropped by any other
    /// key. The hook thread sees that key before the dispatcher has armed
    /// the wait, so the dispatcher checks for it once it has.
    /// </summary>
    [Fact]
    public Task APickerSpaceStillWaitingIsDroppedByAKeyPressedBeforeItWasArmed() => WithController(async controller =>
    {
        using var dialog = HiddenWindow();
        var press = At(dialog.Handle, Space) with
        {
            Context = new ForegroundSelectionContext(dialog.Handle, SelectionSource.CommonFileDialog, "#32770")
        };
        var slot = Fields.Get<DeferredFileDialogPreviewIntentSlot>(controller, "_pendingFileDialogPreview");

        Assert.False(KeyDown(controller, press));
        KeyUp(controller, Space);
        await Idle();
        Assert.NotNull(slot.Current);

        Assert.False(KeyDown(controller, press));
        Assert.False(KeyDown(controller, new KeyPress { Vk = 0x41, Root = dialog.Handle }));
        await Idle();
        Assert.Null(slot.Current);
    });

    /// <summary>
    /// "The first key after the hook is reinstalled." Keys went unseen while
    /// the hook was out, so a Space still held is a new press.
    /// </summary>
    [Fact]
    public Task TheFirstSpaceAfterTheHookWasOutIsAFreshPress() => WithController(controller =>
    {
        var windows = new FakeHookDelivery();
        windows.Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));

        // A foreground watch, so a window Glimpse does not serve takes the hook out.
        Fields.Set(controller, "_foregroundHook", new IntPtr(1));
        try
        {
            Fields.Call(controller, "SyncKeyboardHook", Owner, (uint)Environment.ProcessId);
            Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(KeyDown(controller, At(Owner, Space), time: 1000));
            Assert.False(KeyDown(controller, At(Owner, Space), time: 1200), "A held Space opened twice.");

            Fields.Call(controller, "SyncKeyboardHook", IntPtr.Zero, 0u);
            Assert.True(windows.Removed.Wait(TimeSpan.FromSeconds(5)), "The hook stayed in under a window Glimpse does not serve.");

            Fields.Call(controller, "SyncKeyboardHook", Owner, (uint)Environment.ProcessId);
            Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook did not come back.");
            Assert.Equal(2, windows.InstallCount);
            Assert.True(KeyDown(controller, At(Owner, Space), time: 1400));
        }
        finally
        {
            Fields.Set(controller, "_foregroundHook", IntPtr.Zero);
        }

        return Task.CompletedTask;
    });

    /// <summary>
    /// The latch is the hook thread's own. A key-up releases it with the
    /// dispatcher busy and with a fullscreen app in front; key bookkeeping
    /// comes before anything that could turn the key away.
    /// </summary>
    [Fact]
    public Task AKeyUpReleasesTheLatchWithoutTheDispatcherAndUnderAFullscreenApp() => WithController(controller =>
    {
        Assert.True(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension").IsSuspended);

        Assert.True(KeyDown(controller, At(Owner, Space), time: 1000));
        KeyUp(controller, Space);
        Assert.True(KeyDown(controller, At(Owner, Space), time: 1050), "The second press was taken for the first one held.");
        return Task.CompletedTask;
    });

    [Fact]
    public Task ANavigationKeyUpSpendsItsTokenOnTheDispatcher() => WithController(async controller =>
    {
        ShowGlance(controller);
        var tokens = Fields.Get<long[]>(controller, "_navigationKeyRefreshTokens");

        Assert.False(KeyDown(controller, At(Owner, Down)));
        await Idle();
        Assert.Equal(1, tokens[Down]);

        KeyUp(controller, Down);
        Assert.Equal(1, tokens[Down]);
        await Idle();
        Assert.Equal(0, tokens[Down]);
    });

    /// <summary>
    /// A preview changes without telling the controller: sign mode, a find
    /// bar, a text box taking focus. While the hook is in and a preview is
    /// up, the published state follows at the end of the dispatcher's turn.
    /// </summary>
    [Fact]
    public Task WindowStateTheControllerIsNeverToldReachesTheHookThread() => WithController(async controller =>
    {
        new FakeHookDelivery().Attach(Fields.Get<KeyboardHook>(controller, "_keyboardHook"));
        var window = new PreviewWindow();
        Fields.Set(window, "CurrentPath", Path.Combine(TestDataRoot.Path, "unsigned.pdf"));
        ShowGlance(controller, window);
        Fields.Call(controller, "SyncKeyboardHook", Owner, (uint)Environment.ProcessId);
        Assert.False(Fields.Get<KeyState>(controller, "_keyState").Preview.FillSign);

        SignMode.Enter(window, withWork: false);
        Assert.False(Fields.Get<KeyState>(controller, "_keyState").Preview.FillSign);

        // The dispatcher runs out of work.
        var waited = Stopwatch.StartNew();
        while (!Fields.Get<KeyState>(controller, "_keyState").Preview.FillSign && waited.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(20);
        }

        Assert.True(Fields.Get<KeyState>(controller, "_keyState").Preview.FillSign);

        // With the glance gone nothing is watched: no cost while idle.
        Fields.Call(controller, "HidePreview");
        Assert.False(Fields.Get<bool>(controller, "_keyStateWatched"));
    });

    /// <summary>
    /// A window mid-teardown can refuse a read. The hook thread then lets
    /// every key through, as the hook always failed open, until the state
    /// reads again.
    /// </summary>
    [Fact]
    public Task WhenAWindowCannotBeReadEveryKeyPasses() => WithController(async controller =>
    {
        var pinned = Fields.Get<List<PreviewWindow>>(controller, "_pinnedWindows");
        pinned.Add(null!);
        Fields.Call(controller, "PublishKeyState");
        Assert.Null(Fields.Get<KeyState?>(controller, "_keyState"));

        Assert.False(KeyDown(controller, At(Owner, Space)));
        await Idle();
        Assert.Equal(0, Fields.Get<int>(controller, "_openRequestGeneration"));

        pinned.Clear();
        Fields.Call(controller, "PublishKeyState");
        Assert.True(KeyDown(controller, At(Owner, Space), time: 5000));
    });

    private static KeyPress At(IntPtr root, int vk) => new()
    {
        Vk = vk,
        Root = root,
        Context = vk == Space && root == Owner ? Explorer : null
    };

    /// <summary>What the hook thread will read at the next key, in place of Windows.</summary>
    private static void ReadsAs(SpacePreviewController controller, KeyPress press) =>
        Fields.Set(controller, "_readKey", (Func<int, bool, KeyPress>)((_, repeat) => press with { SpaceRepeat = repeat }));

    /// <summary>
    /// A key-down as the hook thread delivers it: from a thread that is not
    /// the dispatcher's, while the dispatcher runs the test and nothing else.
    /// </summary>
    private static bool KeyDown(SpacePreviewController controller, KeyPress press, uint time = 1000)
    {
        ReadsAs(controller, press);
        return OffDispatcher(() => (bool)Fields.Call(controller, "OnKeyDown", press.Vk, time, 0L)!);
    }

    private static void KeyUp(SpacePreviewController controller, int vk) =>
        OffDispatcher(() => Fields.Call(controller, "OnKeyUp", vk));

    /// <summary>Runs the call on another thread and spins for it: the dispatcher pumps nothing meanwhile.</summary>
    private static T OffDispatcher<T>(Func<T> call)
    {
        var task = Task.Run(call);
        var waited = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(waited.ElapsedMilliseconds < 5000, "The hook thread's work waited for the dispatcher.");
            Thread.SpinWait(100);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the dispatcher run everything the keys posted.</summary>
    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static bool Visible(SpacePreviewController controller) =>
        Fields.Get<bool>(controller, "_previewVisible");

    /// <summary>A glance over <see cref="Owner"/>, as a Space press leaves it, and published.</summary>
    private static void ShowGlance(SpacePreviewController controller, PreviewWindow? window = null)
    {
        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_previewVisible", true);
        Fields.Set(controller, "_previewOwnerRootHwnd", Owner);
        Fields.Set(controller, "_lastExplorerHwnd", Owner);
        Fields.Call(controller, "PublishKeyState");
    }

    /// <summary>
    /// The dispatcher has started building a glance over <see cref="Owner"/>
    /// and said so; the window is not up yet. Returns the window to finish with.
    /// </summary>
    private static PreviewWindow Landing(SpacePreviewController controller)
    {
        var window = new PreviewWindow();
        var path = Path.Combine(TestDataRoot.Path, "glance.png");
        Fields.Set(window, "CurrentPath", path);
        Fields.Set(controller, "_previewOwnerRootHwnd", Owner);
        Fields.Set(controller, "_lastExplorerHwnd", Owner);
        Fields.Call(controller, "PublishLanding", new ExplorerSelection(path, Owner), false);
        Assert.False(Visible(controller));
        return window;
    }

    private static string WritePng(string directory)
    {
        var pixels = Enumerable.Repeat((byte)0x80, 64 * 48 * 4).ToArray();
        var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var path = Path.Combine(directory, "glance.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    /// <summary>Stands in for the shell's browse and counts the steps asked of it.</summary>
    private static Func<int> CountBrowseSteps(SpacePreviewController controller)
    {
        var steps = 0;
        Fields.Set(controller, "_browseRelative", (Func<IntPtr, bool, int, Task<ExplorerSelection?>>)((_, _, delta) =>
        {
            steps += Math.Abs(delta);
            return Task.FromResult<ExplorerSelection?>(null);
        }));
        return () => steps;
    }

    /// <summary>An invisible, unowned top-level window in this process.</summary>
    private static HwndSource HiddenWindow() => new(new HwndSourceParameters("Glimpse input test")
    {
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        Width = 1,
        Height = 1
    });

    private static Task WithController(Func<SpacePreviewController, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // As if a fullscreen app were in front, so no test installs a real
        // mouse hook. The keyboard hook is only ever the fake one.
        Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
        try
        {
            await test(controller);
        }
        finally
        {
            // Dispose closes the glance.
            controller.Dispose();
        }
    });
}
