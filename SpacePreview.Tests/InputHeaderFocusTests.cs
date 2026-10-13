using System.Windows.Automation;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Explorer's header is one window, and Windows cannot say whether the
/// search box or the Sort button in it has the keyboard. UI Automation can.
/// The header is asked off the key path and the hook thread reads the last
/// answer; these are the rules for when that answer may be acted on.
///
/// What a live Explorer window reported, build 26300:
/// the search box and the address bar being edited are an Edit with the
/// text pattern; after Esc in the search box nothing in the header has the
/// focus and the header's own window answers as a Pane; a breadcrumb is a
/// SplitButton; New, Up, Refresh and the rest are a Button; a tab is a
/// TabItem; an open Sort or View menu is a MenuItem or a Menu.
/// </summary>
public sealed class InputHeaderFocusTests
{
    private static readonly IntPtr Explorer = new(0x2000);
    private static readonly IntPtr Other = new(0x3000);

    [Fact]
    public void OnlyWhatWasSeenToTakeNoTextIsPlain()
    {
        Assert.True(ExplorerHeaderFocus.TakesNoText(ControlType.Button, "AppBarButton", false));
        Assert.True(ExplorerHeaderFocus.TakesNoText(ControlType.SplitButton, "FileExplorerExtensions.FirstCrumbStackPanelControl", false));
        Assert.True(ExplorerHeaderFocus.TakesNoText(ControlType.TabItem, "ListViewItem", false));
        Assert.True(ExplorerHeaderFocus.TakesNoText(ControlType.Pane, "InputSiteWindowClass", false));

        // The fault this exists to prevent: a Space taken from a text box.
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Edit, "TextBox", true));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Edit, "TextBox", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Button, "AppBarButton", true));

        // A menu's keys are the menu's.
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.MenuItem, "ToggleMenuFlyoutItem", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Menu, "MenuFlyout", false));

        // Nothing is guessed about what was never seen there.
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Pane, "ScrollViewer", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Pane, null, false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.ComboBox, "ComboBox", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Document, "RichEditBox", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Custom, "", false));
        Assert.False(ExplorerHeaderFocus.TakesNoText(ControlType.Group, "", false));
    }

    [Fact]
    public void TwoReadsWithNoKeyBetweenAreTrusted()
    {
        var header = new Header();
        var watch = header.Watch();
        Assert.False(watch.IsPlain(Explorer));

        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer), "One read was trusted.");

        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
        Assert.False(watch.IsPlain(Other));
        Assert.False(watch.IsPlain(IntPtr.Zero));
    }

    [Fact]
    public void ATextBoxIsNeverPlainAndEndsWhatWasTrusted()
    {
        var header = new Header { Plain = false };
        var watch = header.Watch();
        Read(watch, 5);
        Assert.False(watch.IsPlain(Explorer));

        header.Plain = true;
        Read(watch, 2);
        Assert.True(watch.IsPlain(Explorer));

        // A click moved the focus from Sort into the search box. No key
        // said so: the next look does.
        header.Plain = false;
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));

        // Back on a button, it takes two reads again.
        header.Plain = true;
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// The key may have been Ctrl+E. Nothing read before it is known any
    /// more, and the hook thread says so at once, not at the next look.
    /// </summary>
    [Fact]
    public void AKeyThatReachedTheAppForgetsWhatWasRead()
    {
        var watch = new Header().Watch();
        Read(watch, 2);
        Assert.True(watch.IsPlain(Explorer));

        watch.KeyWentBy(walks: false);
        Assert.False(watch.IsPlain(Explorer));

        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    [Fact]
    public void AKeyBetweenTwoReadsStartsTheCountAgain()
    {
        var watch = new Header().Watch();
        watch.Tick(Now);
        watch.KeyWentBy(walks: false);

        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer), "A read from before the key was counted with one after it.");
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// A read takes a millisecond, and a key can land inside it. That read
    /// saw the header as it was before the key.
    /// </summary>
    [Fact]
    public void AKeyDuringAReadDiscardsThatRead()
    {
        var header = new Header();
        var watch = header.Watch();
        watch.Tick(Now);

        header.DuringRead = () => watch.KeyWentBy(walks: false);
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));

        header.DuringRead = null;
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// The watcher can be stopped between its last check and its write
    /// while the hook thread lets Ctrl+E through and then meets a Space.
    /// An answer carries the key count it was read under, so one published
    /// late is good for no later key.
    /// </summary>
    [Fact]
    public void AnAnswerIsGoodOnlyForTheKeyCountItWasReadUnder()
    {
        var watch = new Header().Watch();
        Read(watch, 2);
        Assert.True(watch.IsPlain(Explorer));

        // The count moves and nothing else is told: no look runs.
        for (var key = 0; key < 3; key++)
        {
            watch.KeyWentBy(walks: false);
            Assert.False(watch.IsPlain(Explorer));
        }
    }

    [Fact]
    public void LeavingTheHeaderForgetsAndNothingIsAskedOutsideIt()
    {
        var header = new Header();
        var watch = header.Watch();
        Read(watch, 2);
        var reads = header.Reads;

        header.In = false;
        Read(watch, 3);
        Assert.False(watch.IsPlain(Explorer));
        Assert.Equal(reads, header.Reads);

        header.In = true;
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// The read asks Windows for whatever has the focus. With another
    /// window in front that is another app's, and it is never asked.
    /// </summary>
    [Fact]
    public void AnotherWindowInFrontForgetsAndIsNeverAsked()
    {
        var header = new Header();
        var watch = header.Watch();
        Read(watch, 2);
        var reads = header.Reads;

        header.Front = Other;
        Read(watch, 3);
        Assert.False(watch.IsPlain(Explorer));
        Assert.False(watch.IsPlain(Other));
        Assert.Equal(reads, header.Reads);

        // And with no Explorer window to watch at all.
        header.Front = Explorer;
        watch.Watch(IntPtr.Zero);
        Read(watch, 3);
        Assert.False(watch.IsPlain(Explorer));
        Assert.Equal(reads, header.Reads);
    }

    /// <summary>
    /// The user brought another Explorer window forward while the read was
    /// out. The answer is that window's, and both share one process.
    /// </summary>
    [Fact]
    public void AReadThatEndsWithAnotherWindowInFrontDoesNotCount()
    {
        var header = new Header();
        var watch = header.Watch();
        watch.Tick(Now);

        header.DuringRead = () => header.Front = Other;
        watch.Tick(Now);
        header.DuringRead = null;
        header.Front = Explorer;

        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    [Fact]
    public void WatchingAnotherWindowEndsWhatWasTrustedOfThisOne()
    {
        var header = new Header();
        var watch = header.Watch();
        Read(watch, 2);

        header.Front = Other;
        watch.Watch(Other);
        Assert.False(watch.IsPlain(Explorer));
        Assert.False(watch.IsPlain(Other));

        watch.Tick(Now);
        Assert.False(watch.IsPlain(Other), "A read of one window was counted for another.");
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Other));
        Assert.False(watch.IsPlain(Explorer));

        // There and back, before any look: what was trusted of the first
        // window when it left the front is not trusted on its return.
        watch.Watch(Explorer);
        watch.Watch(Other);
        Assert.False(watch.IsPlain(Other));
        Read(watch, 2);
        Assert.True(watch.IsPlain(Other));
    }

    /// <summary>
    /// A change of window arms the timer while a look can still be out with
    /// Explorer. A second look must not run beside it: the older one would
    /// publish over the newer one's refusal.
    /// </summary>
    [Fact]
    public void ALookStillOutIsNotJoinedByAnother()
    {
        var header = new Header();
        var watch = header.Watch();

        header.DuringRead = () =>
        {
            header.DuringRead = null;
            watch.Tick(Now);
        };
        watch.Tick(Now);

        Assert.Equal(1, header.Reads);
    }

    /// <summary>
    /// A watcher held up inside a read (Explorer busy) confirms nothing
    /// meanwhile, so what it last said stops counting.
    /// </summary>
    [Fact]
    public void AnAnswerRunsOut()
    {
        var watch = new Header().Watch();
        var old = Now - ExplorerHeaderFocus.FreshMilliseconds - 500;
        watch.Tick(old);
        watch.Tick(old);
        Assert.False(watch.IsPlain(Explorer));

        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// The interval is how long a click from a button into the search box
    /// can go unseen, so while the user is at the PC every look asks.
    /// </summary>
    [Fact]
    public void WhileTheUserIsAtItEveryLookAsks()
    {
        var header = new Header();
        var watch = header.Watch();

        Read(watch, 100);

        Assert.Equal(100, header.Reads);
        Assert.True(watch.IsPlain(Explorer));
        Assert.True(ExplorerHeaderFocus.IntervalMilliseconds * 3 < ExplorerHeaderFocus.FreshMilliseconds);
    }

    /// <summary>
    /// The cost this exists for: twenty reads a second took 2 % of a core
    /// in Explorer for as long as the keyboard sat on a tab. The focus
    /// moves when the user does something, so with hands off the PC the
    /// last answer stands and Explorer is not asked.
    /// </summary>
    [Fact]
    public void OnceHandsAreOffExplorerIsNotAskedAgain()
    {
        var header = new Header();
        header.HandsOff();
        var watch = header.Watch();

        Read(watch, 100);

        Assert.Equal(ExplorerHeaderFocus.ReadsToTrust, header.Reads);
        Assert.True(watch.IsPlain(Explorer));

        // Each look still says the answer stands. Without one it runs out.
        Thread.Sleep((int)ExplorerHeaderFocus.FreshMilliseconds + 50);
        Assert.False(watch.IsPlain(Explorer));
        watch.Tick(Now);
        Assert.True(watch.IsPlain(Explorer), "The answer ran out while nobody touched anything.");
        Assert.Equal(ExplorerHeaderFocus.ReadsToTrust, header.Reads);

        // The same with the keyboard in the search box: one read says so.
        var typing = new Header { Plain = false };
        typing.HandsOff();
        Read(typing.Watch(), 100);
        Assert.Equal(1, typing.Reads);
    }

    [Fact]
    public void ATouchAfterTheQuietAsksAgain()
    {
        var header = new Header();
        header.HandsOff();
        var watch = header.Watch();
        Read(watch, 10);
        var reads = header.Reads;

        // A click from the tab into the search box.
        header.Touch();
        header.Plain = false;
        watch.Tick(Now);

        Assert.Equal(reads + 1, header.Reads);
        Assert.False(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// A click can move the focus a moment after it lands. A read made
    /// while the user was still at it settles nothing: the asking goes on
    /// until one is made in the quiet.
    /// </summary>
    [Fact]
    public void AReadMadeBeforeTheQuietDoesNotEndTheAsking()
    {
        var header = new Header();
        header.Touch();
        var watch = header.Watch();
        var touched = Now;

        watch.Tick(touched + 50);
        watch.Tick(touched + 100);
        watch.Tick(touched + 150);
        Assert.Equal(3, header.Reads);

        watch.Tick(touched + ExplorerHeaderFocus.QuietMilliseconds + 50);
        Assert.Equal(4, header.Reads);
        watch.Tick(touched + ExplorerHeaderFocus.QuietMilliseconds + 100);
        watch.Tick(touched + ExplorerHeaderFocus.QuietMilliseconds + 150);
        Assert.Equal(4, header.Reads);
    }

    /// <summary>A program can bring an Explorer window forward with nobody at the PC. It is still read twice.</summary>
    [Fact]
    public void AWindowThatComesForwardInTheQuietIsReadUntilTrusted()
    {
        var header = new Header();
        header.HandsOff();
        var watch = header.Watch();
        Read(watch, 10);

        header.Front = Other;
        watch.Watch(Other);
        Read(watch, 10);

        Assert.True(watch.IsPlain(Other));
        Assert.Equal(ExplorerHeaderFocus.ReadsToTrust * 2, header.Reads);
    }

    /// <summary>
    /// Someone who Tabs through the header means its buttons, and Space
    /// presses the one in focus. 3.0.0 took that Space for a preview.
    /// </summary>
    [Fact]
    public void ATabWalkLeavesTheHeaderItsSpaceUntilTheFocusLeaves()
    {
        var header = new Header();
        var watch = header.Watch();
        Read(watch, 2);

        watch.KeyWentBy(walks: true);
        var reads = header.Reads;
        Read(watch, 5);
        Assert.False(watch.IsPlain(Explorer));
        Assert.Equal(reads, header.Reads);

        // A click on a file ends the walk; a click on Sort after it is a click.
        header.In = false;
        watch.Tick(Now + ExplorerHeaderFocus.WalkMilliseconds + 1);
        header.In = true;
        Read(watch, 2);
        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>The Tab that carries the focus into the header is pressed outside it.</summary>
    [Fact]
    public void ATabPressedJustBeforeTheFocusArrivesIsAWalkToo()
    {
        var header = new Header { In = false };
        var watch = header.Watch();

        watch.KeyWentBy(walks: true);
        watch.Tick(Now);
        header.In = true;
        Read(watch, 5);

        Assert.False(watch.IsPlain(Explorer));
        Assert.Equal(0, header.Reads);
    }

    /// <summary>A Tab pressed in the file list a while ago says nothing about a click on a tab now.</summary>
    [Fact]
    public void AnOldTabOutsideTheHeaderIsNoWalk()
    {
        var header = new Header { In = false };
        var watch = header.Watch();

        watch.KeyWentBy(walks: true);
        watch.Tick(Now + ExplorerHeaderFocus.WalkMilliseconds + 1);
        header.In = true;
        Read(watch, 2);

        Assert.True(watch.IsPlain(Explorer));
    }

    /// <summary>
    /// The hook sees a Tab in a file picker or in a preview too. Nothing
    /// looks at the header while those are in front, so that Tab would
    /// stand as a walk for good, and Space after a click on a tab would
    /// give no preview until a file was clicked.
    /// </summary>
    [Fact]
    public void ATabPressedInAnotherWindowIsNoWalkHere()
    {
        var header = new Header();
        var watch = header.Watch();

        watch.Watch(IntPtr.Zero);
        watch.KeyWentBy(walks: true);
        watch.Watch(Explorer);
        Read(watch, 2);

        Assert.True(watch.IsPlain(Explorer));
    }

    private static long Now => Environment.TickCount64;

    private static void Read(ExplorerHeaderFocus watch, int times)
    {
        for (var i = 0; i < times; i++)
        {
            watch.Tick(Now);
        }
    }

    /// <summary>An Explorer window in front with the keyboard in its header, on a button.</summary>
    private sealed class Header
    {
        public IntPtr Front { get; set; } = Explorer;
        public bool In { get; set; } = true;
        public bool Plain { get; set; } = true;
        public int Reads { get; private set; }
        public Action? DuringRead { get; set; }

        // The stamp of the last key, click or mouse move. Until a test
        // says otherwise the user is at the PC and it is always now.
        private uint? _input;

        /// <summary>Nothing pressed, clicked or moved for five seconds, and nothing from here on.</summary>
        public void HandsOff() => _input = unchecked((uint)Environment.TickCount64 - 5000);

        /// <summary>One key, click or mouse move, now.</summary>
        public void Touch() => _input = unchecked((uint)Environment.TickCount64);

        public ExplorerHeaderFocus Watch()
        {
            var watch = new ExplorerHeaderFocus(
                () => Front,
                _ => In,
                _ =>
                {
                    Reads++;
                    DuringRead?.Invoke();
                    return Plain;
                },
                () => _input ?? unchecked((uint)Environment.TickCount64));
            watch.Watch(Explorer);
            return watch;
        }
    }
}
