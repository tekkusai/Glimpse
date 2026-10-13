using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A file on a phone or in a zip folder is shown from a copy, and the copy
/// may take seconds to arrive. Each test here is something that happens in
/// those seconds, and what is on screen once the copy has come or failed.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BorrowedArrivalTests
{
    private const int Esc = NativeMethods.VK_ESCAPE;

    [Fact]
    public Task ACopyOnItsWayIsShownWhenItArrives() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;

        glance.Follow(copy);
        Assert.Equal(glance.First, glance.Window.CurrentPath);

        owner.End();
        await glance.Shows(copy);
    });

    [Fact]
    public Task AFileShownMeanwhileIsNotReplacedByTheLateCopy() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        glance.Follow(copy);

        // The reader clicks a file on disk while the phone's is coming.
        glance.Follow(glance.Second);
        Assert.Equal(glance.Second, glance.Window.CurrentPath);

        owner.End();
        Assert.True(await Owners.Ready(copy));
        await glance.StillShows(glance.Second);
    });

    /// <summary>
    /// The copy arrives while the dispatcher is busy with the keys that
    /// end the glance and open another. Its news is behind theirs in the
    /// queue, and by its turn the glance it was for is over.
    /// </summary>
    [Fact]
    public Task AGlanceOpenedAfterTheOneThatAskedIsNotReplacedByTheLateCopy() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        glance.Follow(copy);

        // No await from here to the last of the keys: the dispatcher runs
        // this test body and nothing else, as it would run the keys.
        owner.End();
        Assert.True(SpinWait.SpinUntil(() => File.Exists(copy) && BorrowedFiles.Underway == 0, TimeSpan.FromSeconds(5)));
        Fields.Call(glance.Controller, "ResetSession");

        await glance.StillShows(glance.First);
    });

    [Fact]
    public Task ACopyThatDoesNotArriveLeavesTheGlanceAsItIs() => WithGlance(async glance =>
    {
        var copy = Owners.Ask("gone.txt", new OwnerStream(4000, () => throw new IOException("unplugged")))!;
        glance.Follow(copy);

        Assert.False(await Owners.Ready(copy));
        await glance.StillShows(glance.First);
        Assert.True(glance.Visible, "A file that could not be copied is not an empty selection.");
    });

    /// <summary>
    /// The arrow keys are not held up by a copy. Two steps: the first lands
    /// on a phone's file that is slow to come, and the second is taken at
    /// once and lands on a file that is there.
    /// </summary>
    [Fact]
    public Task AnArrowPressedWhileACopyComesStepsTheShellAtOnce() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        var landings = new Queue<string>([copy, glance.Second]);
        var steps = 0;
        Fields.Set(glance.Controller, "_browseRelative", (Func<IntPtr, bool, int, Task<ExplorerSelection?>>)((_, _, _) =>
        {
            steps++;
            return Task.FromResult<ExplorerSelection?>(new ExplorerSelection(landings.Dequeue(), glance.Owner));
        }));

        Fields.Call(glance.Controller, "BrowseRelativeAsync", 1, 0L);
        await Idle();
        Assert.Equal(1, steps);
        Assert.False(Fields.Get<bool>(glance.Controller, "_browseInFlight"), "The step is still waiting for its copy.");
        Assert.Equal(glance.First, glance.Window.CurrentPath);

        Fields.Call(glance.Controller, "BrowseRelativeAsync", 1, 0L);
        await Idle();
        Assert.Equal(2, steps);
        Assert.Equal(glance.Second, glance.Window.CurrentPath);

        owner.End();
        Assert.True(await Owners.Ready(copy));
        await glance.StillShows(glance.Second);
    });

    /// <summary>
    /// One step onto a phone's file that is slow to come, and one step
    /// back. The shell says the selection is the file on screen, so nothing
    /// is shown anew, and the copy still coming is for nothing.
    /// </summary>
    [Fact]
    public Task AStepBackToTheFileOnScreenEndsTheWaitForTheCopy() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        var landings = new Queue<string>([copy, glance.First]);
        Fields.Set(glance.Controller, "_browseRelative", (Func<IntPtr, bool, int, Task<ExplorerSelection?>>)((_, _, _) =>
            Task.FromResult<ExplorerSelection?>(new ExplorerSelection(landings.Dequeue(), glance.Owner))));

        Fields.Call(glance.Controller, "BrowseRelativeAsync", 1, 0L);
        await Idle();
        Fields.Call(glance.Controller, "BrowseRelativeAsync", -1, 0L);
        await Idle();
        Assert.Empty(landings);

        owner.End();
        Assert.True(await Owners.Ready(copy));
        await glance.StillShows(glance.First);
    });

    /// <summary>
    /// The glance is closed while a long file is still coming from the
    /// phone. Nobody is waiting for it, and the phone is not read on.
    /// </summary>
    [Fact]
    public Task AGlanceThatEndsStopsTheCopyItWaitedFor() => WithGlance(async glance =>
    {
        var stream = new OwnerStream(200_000, () => Thread.Sleep(20));
        var copy = Owners.Ask("long.txt", stream)!;
        glance.Follow(copy);

        Fields.Call(glance.Controller, "HidePreview");

        Assert.False(await Owners.Ready(copy));
        await Owners.Settled();
        Assert.InRange(stream.Reads, 0, 100);
        Assert.False(glance.Visible);
    });

    /// <summary>
    /// The reader clicks the phone's file and then the file on screen
    /// again. Explorer says the selection is what is shown, so nothing is
    /// shown anew, and the copy still coming is for nothing.
    /// </summary>
    [Fact]
    public Task ExplorerBackOnTheFileOnScreenEndsTheWaitForTheCopy() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        var selected = copy;
        Fields.Set(glance.Controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) =>
            Task.FromResult<ExplorerSelection?>(new ExplorerSelection(selected, glance.Owner))));

        glance.Refresh(1);
        await Idle();
        Assert.Equal(glance.First, glance.Window.CurrentPath);

        selected = glance.First;
        glance.Refresh(2);
        await Idle();

        owner.End();
        Assert.True(await Owners.Ready(copy));
        await glance.StillShows(glance.First);
    });

    /// <summary>
    /// Explorer is read again while the copy comes, as it is at every key
    /// release, and says what it said: the phone's file. The glance goes on
    /// waiting for it.
    /// </summary>
    [Fact]
    public Task ExplorerStillOnTheFileThatIsComingKeepsTheWait() => WithGlance(async glance =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;
        Fields.Set(glance.Controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) =>
            Task.FromResult<ExplorerSelection?>(new ExplorerSelection(copy, glance.Owner))));

        glance.Refresh(1);
        await Idle();
        glance.Refresh(2);
        await Idle();
        Assert.Equal(glance.First, glance.Window.CurrentPath);

        owner.End();
        await glance.Shows(copy);
    });

    /// <summary>
    /// Space on a phone's file. Until its copy has come the open is still
    /// on its way, which is what lets Esc call it off, and Esc stops the
    /// copy as well.
    /// </summary>
    [Fact]
    public Task AnOpenWaitsForItsCopyAndEscCallsBothOff() => WithController(async controller =>
    {
        using var owner = new Silence();
        var stream = new OwnerStream(200_000, owner.Wait);
        var copy = Owners.Ask("late.txt", stream)!;
        Fields.Set(controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) =>
            Task.FromResult<ExplorerSelection?>(new ExplorerSelection(copy, Root))));

        // Space, as its key runs it: the open is queued and the hook thread told.
        Fields.Call(controller, "QueueOpen", new ForegroundSelectionContext(Root, SelectionSource.Explorer, "CabinetWClass"), 0L);
        Fields.Call(controller, "PublishKeyState");
        await Idle();
        Assert.True(Fields.Get<bool>(controller, "_openPending"), "The open ended before its copy came.");

        Assert.False(KeyDown(controller, new KeyPress { Vk = Esc, Root = Root }));
        await Idle();
        Assert.False(Fields.Get<bool>(controller, "_openPending"));

        owner.End();
        Assert.False(await Owners.Ready(copy));
        await Owners.Settled();
        Assert.InRange(stream.Reads, 1, 2);
        Assert.False(Fields.Get<bool>(controller, "_previewVisible"));
    });

    [Fact]
    public Task AnOpenShowsItsFileWhenTheCopyHasCome() => WithOpen(async (controller, window, open) =>
    {
        using var owner = new Silence();
        var copy = Owners.Ask("late.txt", new OwnerStream(4000, owner.Wait))!;

        open(copy);
        await Idle();
        Assert.Null(window.CurrentPath);

        owner.End();
        Assert.True(await Owners.Ready(copy));
        await Settle();
        Assert.Equal(copy, window.CurrentPath);
        Assert.True(Fields.Get<bool>(controller, "_previewVisible"), "The glance did not come up.");
        Assert.False(Fields.Get<bool>(controller, "_openPending"));
    });

    [Fact]
    public Task AnOpenWhoseCopyDoesNotArriveIsOver() => WithOpen(async (controller, window, open) =>
    {
        var copy = Owners.Ask("gone.txt", new OwnerStream(4000, () => throw new IOException("unplugged")))!;

        open(copy);
        Assert.False(await Owners.Ready(copy));
        await Settle();

        Assert.Null(window.CurrentPath);
        Assert.False(Fields.Get<bool>(controller, "_previewVisible"));
        Assert.False(Fields.Get<bool>(controller, "_openPending"));
        Assert.False(Fields.Get<KeyState>(controller, "_keyState").OpenPending);
    });

    private static readonly IntPtr Root = new(0x2000);

    /// <summary>A glance that is up over a hidden window, showing a file on disk.</summary>
    private sealed record Glance(SpacePreviewController Controller, PreviewWindow Window, IntPtr Owner, string First, string Second)
    {
        public bool Visible => Fields.Get<bool>(Controller, "_previewVisible");

        /// <summary>Explorer's selection has moved to <paramref name="path"/>, and the glance is told.</summary>
        public void Follow(string path) =>
            Fields.Call(Controller, "ShowPreview", new ExplorerSelection(path, Owner), 0L);

        /// <summary>Explorer is read again, as after a click in it or a key released.</summary>
        public void Refresh(long generation)
        {
            var request = typeof(SpacePreviewController).GetNestedType("SelectionRefreshRequest", System.Reflection.BindingFlags.NonPublic)!;
            Fields.Call(Controller, "RequestSelectionRefresh", Activator.CreateInstance(request, generation, false, 0L));
        }

        public async Task Shows(string path)
        {
            var waited = Stopwatch.StartNew();
            while (!string.Equals(Window.CurrentPath, path, StringComparison.OrdinalIgnoreCase) && waited.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(10);
            }

            Assert.Equal(path, Window.CurrentPath);
        }

        public async Task StillShows(string path)
        {
            await Settle();
            Assert.Equal(path, Window.CurrentPath);
        }
    }

    /// <summary>Time for a copy's news to cross to the dispatcher, and for the dispatcher to act on it.</summary>
    private static async Task Settle()
    {
        await Task.Delay(200);
        await Idle();
    }

    private static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    /// <summary>A key-down as the hook thread delivers it, from a thread that is not the dispatcher's.</summary>
    private static bool KeyDown(SpacePreviewController controller, KeyPress press)
    {
        Fields.Set(controller, "_readKey", (Func<int, bool, KeyPress>)((_, repeat) => press with { SpaceRepeat = repeat }));
        var task = Task.Run(() => (bool)Fields.Call(controller, "OnKeyDown", press.Vk, 1000u, 0L)!);
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, TimeSpan.FromSeconds(5)), "The hook thread's work waited for the dispatcher.");
        return task.GetAwaiter().GetResult();
    }

    private static Task WithGlance(Func<Glance, Task> test) => WithController(async controller =>
    {
        using var temp = new TempDirectory();
        using var owner = Hidden();

        // Parked off screen and frozen, like the probe.
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_previewOwnerRootHwnd", owner.Handle);

        var glance = new Glance(controller, window, owner.Handle, temp.CreateFile("first.txt"), temp.CreateFile("second.txt"));
        glance.Follow(glance.First);
        Assert.True(glance.Visible, "The glance did not come up.");
        Assert.Equal(glance.First, window.CurrentPath);

        await test(glance);
    });

    /// <summary>
    /// No glance is up. A hidden window is in front, the preview window is
    /// parked off screen, and Space is pressed on the file handed to the
    /// test's last argument.
    /// </summary>
    private static Task WithOpen(Func<SpacePreviewController, PreviewWindow, Action<string>, Task> test) => WithController(async controller =>
    {
        using var front = Hidden();
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        Fields.Set(controller, "_previewWindow", window);
        Fields.Set(controller, "_foregroundRoot", (Func<IntPtr>)(() => front.Handle));

        await test(controller, window, path =>
        {
            Fields.Set(controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) =>
                Task.FromResult<ExplorerSelection?>(new ExplorerSelection(path, front.Handle))));
            Fields.Call(controller, "QueueOpen", new ForegroundSelectionContext(front.Handle, SelectionSource.Explorer, "CabinetWClass"), 0L);
            Fields.Call(controller, "PublishKeyState");
        });
    });

    /// <summary>An invisible, unowned top-level window in this process.</summary>
    private static HwndSource Hidden() => new(new HwndSourceParameters("Glimpse arrival test")
    {
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        Width = 1,
        Height = 1
    });

    private static Task WithController(Func<SpacePreviewController, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // As if a fullscreen app were in front, so no test installs a real
        // mouse hook.
        Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
        try
        {
            await test(controller);
        }
        finally
        {
            // Dispose closes the glance. A test that failed has let its
            // owners go by now, and their copies end before the next test.
            controller.Dispose();
            SpinWait.SpinUntil(() => BorrowedFiles.Underway == 0, TimeSpan.FromSeconds(10));
        }
    });
}
