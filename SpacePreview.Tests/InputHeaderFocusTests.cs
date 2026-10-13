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

        // A click moved the focus from Sort into the search box.
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

    /// <summary>A read takes a millisecond, and a key can land inside it.</summary>
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

    [Fact]
    public void WatchingAnotherExplorerWindowStartsFromNothing()
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

    [Fact]
    public void AHeaderLeftAloneIsAskedLessOftenAndStillInTime()
    {
        var watch = new Header().Watch();
        var wait = 0;
        for (var read = 0; read < 19; read++)
        {
            wait = watch.Tick(Now);
            Assert.Equal(ExplorerHeaderFocus.IntervalMilliseconds, wait);
        }

        wait = watch.Tick(Now);
        Assert.Equal(ExplorerHeaderFocus.RestingIntervalMilliseconds, wait);
        Assert.True(watch.IsPlain(Explorer));
        Assert.True(ExplorerHeaderFocus.RestingIntervalMilliseconds * 2 < ExplorerHeaderFocus.FreshMilliseconds);

        // A key brings the short wait back.
        watch.KeyWentBy(walks: false);
        Assert.Equal(ExplorerHeaderFocus.IntervalMilliseconds, watch.Tick(Now));
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
                });
            watch.Watch(Explorer);
            return watch;
        }
    }
}
