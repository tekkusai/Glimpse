using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Two keys pressed faster than the dispatcher's turn. The hook thread
/// answers the second before the dispatcher has run the first, so it counts
/// in what its own first key is certain to change. In 3.0 the first key had
/// changed it before the second was looked at.
/// </summary>
public sealed class InputQuickKeysTests
{
    private const int Space = NativeMethods.VK_SPACE;
    private const int Enter = NativeMethods.VK_RETURN;
    private const int Esc = NativeMethods.VK_ESCAPE;
    private const int Right = NativeMethods.VK_RIGHT;

    private static readonly IntPtr Owner = new(0x2000);

    /// <summary>
    /// The fault: both presses read "find bar open" and both went to the
    /// content. The first closed the bar, the second did nothing, and the
    /// preview stayed up.
    /// </summary>
    [Fact]
    public Task ASecondEscAfterTheOneThatClosedTheFindBarEndsTheGlance() => WithFindBar(async (controller, window, glance) =>
    {
        Assert.False(HookThread.KeyDown(controller, At(glance, Esc)), "The first Esc is the find bar's.");
        Assert.True(HookThread.KeyDown(controller, At(glance, Esc)), "The second Esc went to the content as well.");
        Assert.True(Visible(controller), "The dispatcher was not meant to have run yet.");

        await HookThread.Idle();
        Assert.False(window.IsEscapeYieldArmed);
        Assert.False(Visible(controller));
    });

    [Fact]
    public Task SpaceAfterTheEscThatClosedTheFindBarEndsTheGlance() => WithFindBar(async (controller, window, glance) =>
    {
        Assert.False(HookThread.KeyDown(controller, At(glance, Esc)));
        Assert.True(HookThread.KeyDown(controller, At(glance, Space)), "Space was typed into a find bar that had just closed.");

        await HookThread.Idle();
        Assert.False(Visible(controller));
    });

    /// <summary>
    /// What the hook thread counts in lasts until the dispatcher has answered
    /// for that key. After that it reads what the dispatcher published.
    /// </summary>
    [Fact]
    public Task OnceTheDispatcherHasAnsweredTheHookThreadReadsItsStateAgain() => WithFindBar(async (controller, window, glance) =>
    {
        Assert.False(HookThread.KeyDown(controller, At(glance, Esc)));
        await HookThread.Idle();
        Assert.False(window.IsEscapeYieldArmed);

        // Ctrl+F opened the bar again; the dispatcher says so.
        Fields.Set(window, "_escapeYieldsToContent", true);
        Fields.Call(controller, "PublishKeyState");
        Assert.False(HookThread.KeyDown(controller, At(glance, Esc)), "An Esc for a find bar opened since was taken for the glance's.");
    });

    private static readonly IntPtr Glance = new(0x100);
    private static readonly IntPtr Pin = new(0x700);

    private static readonly KeyState Showing = new()
    {
        PreviewVisible = true,
        OwnerRoot = Owner,
        Preview = new WindowKeyState { Hwnd = Glance, Browsable = true }
    };

    private static readonly KeyState FindBar = Showing with { Preview = Showing.Preview with { FindBarArmed = true } };

    private static readonly KeyState ToolbarClick = Showing with { Preview = Showing.Preview with { EnterYield = true } };

    private static readonly KeyState PinnedFindBar = new()
    {
        Pinned = [new WindowKeyState { Hwnd = Pin, Browsable = true, FindBarArmed = true }]
    };

    /// <summary>
    /// Published is what the hook thread holds for both keys: the dispatcher
    /// has run neither. Swallow and Action are the answer to the second.
    /// </summary>
    private static readonly (string Promise, KeyPress First, KeyPress Second, KeyState Published, bool Swallow, KeyAction Action)[] Pairs =
    [
        ("Esc, Esc with a find bar open: the second ends the glance", At(Glance, Esc), At(Glance, Esc), FindBar, true, KeyAction.Hide),
        ("Esc, Space with a find bar open: Space ends the glance", At(Glance, Esc), At(Glance, Space), FindBar, true, KeyAction.Hide),
        ("Esc, Enter with a find bar open: Enter opens the file", At(Glance, Esc), At(Glance, Enter), FindBar, true, KeyAction.OpenCurrent),
        ("Esc for a find bar spends a toolbar click with it", At(Glance, Esc), InContent(Enter), FindBar with { Preview = FindBar.Preview with { EnterYield = true } }, true, KeyAction.OpenCurrent),
        ("Enter, Enter after a click in the PDF toolbar: the second opens the file", InContent(Enter), InContent(Enter), ToolbarClick, true, KeyAction.OpenCurrent),
        ("Esc, Esc in a pinned window with a find bar open: the second closes it", At(Pin, Esc), At(Pin, Esc), PinnedFindBar, true, KeyAction.ClosePinned),
        ("Esc that ends the glance, then Right: the arrow is Explorer's", At(Owner, Esc), At(Owner, Right), Showing, false, KeyAction.None),
        ("Enter that hands the file to Explorer, then Right: the arrow is Explorer's", At(Owner, Enter), At(Owner, Right), Showing, false, KeyAction.None),
        ("Space that ends the glance, then Space: it opens again", OverExplorer(), OverExplorer(), Showing, true, KeyAction.Open),
        ("Right, Right: a key that changes nothing leaves the second as it was", At(Owner, Right), At(Owner, Right), Showing, true, KeyAction.Browse),
        ("Esc for one window's find bar is not counted against another's", At(Pin, Esc), At(Glance, Esc), FindBar with { Pinned = PinnedFindBar.Pinned }, false, KeyAction.YieldEscape),
    ];

    public static TheoryData<string> PairPromises => new(Pairs.Select(row => row.Promise));

    [Theory]
    [MemberData(nameof(PairPromises))]
    public void TheSecondOfTwoQuickKeysIsAnsweredAsIfTheFirstHadRun(string promise)
    {
        var row = Pairs.Single(candidate => candidate.Promise == promise);

        var first = PreviewKeyMap.Route(PreviewKeyMap.Facts(row.First, row.Published));
        var ahead = PreviewKeyMap.After(row.Published, row.First.Root, first.Action);
        var second = PreviewKeyMap.Route(PreviewKeyMap.Facts(row.Second, ahead));

        Assert.True(
            second == new KeyDecision(row.Swallow, row.Action),
            $"{promise}: expected swallow={row.Swallow} action={row.Action}, got swallow={second.Swallow} action={second.Action} (first={first.Action})");
    }

    /// <summary>
    /// The fault: Ctrl+F was not counted in, so a key pressed before the
    /// dispatcher had noted it was judged with the bar closed, taken by
    /// the hook, and by its turn belonged to a bar that never got it:
    /// an Esc to close the bar again, an Enter to repeat the last search.
    /// These are the keys whose answer the bar changes.
    /// </summary>
    [Theory]
    [InlineData(Esc)]
    [InlineData(Space)]
    [InlineData(Enter)]
    public void AKeyRightAfterCtrlFInAPdfIsAnsweredWithTheFindBarOpen(int vk)
    {
        var pdf = Showing with { Preview = Showing.Preview with { TakesFind = true } };
        var open = pdf with { Preview = pdf.Preview with { FindBarArmed = true } };

        var find = PreviewKeyMap.Route(PreviewKeyMap.Facts(At(Glance, 'F') with { Ctrl = true }, pdf));
        Assert.Equal(new KeyDecision(false, KeyAction.NoteFind), find);
        var ahead = PreviewKeyMap.After(pdf, Glance, find.Action);

        var withBarOpen = PreviewKeyMap.Route(PreviewKeyMap.Facts(At(Glance, vk), open));
        Assert.NotEqual(withBarOpen, PreviewKeyMap.Route(PreviewKeyMap.Facts(At(Glance, vk), pdf)));
        Assert.Equal(withBarOpen, PreviewKeyMap.Route(PreviewKeyMap.Facts(At(Glance, vk), ahead)));
    }

    [Fact]
    public void EscRightAfterCtrlFInAPdfGoesToTheFindBar()
    {
        var pdf = Showing with { Preview = Showing.Preview with { TakesFind = true } };

        var ahead = PreviewKeyMap.After(pdf, Glance, KeyAction.NoteFind);

        Assert.Equal(
            new KeyDecision(false, KeyAction.YieldEscape),
            PreviewKeyMap.Route(PreviewKeyMap.Facts(At(Glance, Esc), ahead)));
    }

    [Fact]
    public void CtrlFOverContentWithNoFindBarChangesNothing()
    {
        Assert.Same(Showing, PreviewKeyMap.After(Showing, Glance, KeyAction.NoteFind));

        // One window's Ctrl+F is not another's.
        var pinnedPdf = Showing with
        {
            Pinned = [new WindowKeyState { Hwnd = Pin, Browsable = true, TakesFind = true }]
        };
        var ahead = PreviewKeyMap.After(pinnedPdf, Pin, KeyAction.NoteFind);
        Assert.True(ahead.Pinned[0].FindBarArmed);
        Assert.False(ahead.Preview.FindBarArmed);
        Assert.False(pinnedPdf.Pinned[0].FindBarArmed);
    }

    [Fact]
    public void EveryPairIsStatedOnce()
    {
        Assert.Equal(Pairs.Length, Pairs.Select(row => row.Promise).Distinct().Count());
    }

    /// <summary>
    /// Only what a key is certain to change is counted in. Everything else
    /// the dispatcher settles when the key's turn comes, as before. A key
    /// that changes nothing gives the same state back, so the hook thread
    /// keeps nothing for it.
    /// </summary>
    [Fact]
    public void OnlyAGlanceEndedAndAYieldSpentAreCountedIn()
    {
        var everything = Showing with
        {
            Preview = Showing.Preview with { FindBarArmed = true, EnterYield = true, GridOpen = true, EscapeLayer = true }
        };
        KeyAction[] counted =
        [
            KeyAction.Hide, KeyAction.HideForPickerCommit, KeyAction.ReturnToPicker,
            KeyAction.YieldEscape, KeyAction.YieldEnter, KeyAction.OpenCurrent
        ];

        foreach (var action in Enum.GetValues<KeyAction>())
        {
            var after = PreviewKeyMap.After(everything, Glance, action);
            Assert.True(
                counted.Contains(action) != ReferenceEquals(after, everything),
                $"{action} {(counted.Contains(action) ? "changed nothing" : "was counted in")}.");
        }

        // Spent once is spent: nothing more to keep for the same key again.
        foreach (var action in counted)
        {
            var once = PreviewKeyMap.After(everything, Glance, action);
            Assert.Same(once, PreviewKeyMap.After(once, Glance, action));
        }
    }

    [Fact]
    public void WhatIsCountedInIsExactlyWhatThatKeySpends()
    {
        var both = Showing with { Preview = Showing.Preview with { FindBarArmed = true, EnterYield = true } };

        var escaped = PreviewKeyMap.After(both, Glance, KeyAction.YieldEscape);
        Assert.False(escaped.Preview.FindBarArmed);
        Assert.False(escaped.Preview.EnterYield);
        Assert.True(escaped.PreviewVisible);

        var entered = PreviewKeyMap.After(both, Glance, KeyAction.YieldEnter);
        Assert.True(entered.Preview.FindBarArmed);
        Assert.False(entered.Preview.EnterYield);

        var hidden = PreviewKeyMap.After(both, Owner, KeyAction.Hide);
        Assert.False(hidden.PreviewVisible);
        Assert.Equal(both.Preview, hidden.Preview);

        // A pinned window in front answers for itself, and what was
        // published is never written to.
        var pinned = both with { Pinned = PinnedFindBar.Pinned };
        var pinnedEscaped = PreviewKeyMap.After(pinned, Pin, KeyAction.YieldEscape);
        Assert.False(pinnedEscaped.Pinned[0].FindBarArmed);
        Assert.True(pinnedEscaped.Preview.FindBarArmed);
        Assert.True(PinnedFindBar.Pinned[0].FindBarArmed);
    }

    private static KeyPress At(IntPtr root, int vk) => new() { Vk = vk, Root = root };

    /// <summary>A key in the glance with the keyboard in its browser view.</summary>
    private static KeyPress InContent(int vk) => new() { Vk = vk, Root = Glance, RootChildFocus = true };

    private static KeyPress OverExplorer() => new()
    {
        Vk = Space,
        Root = Owner,
        Context = new ForegroundSelectionContext(Owner, SelectionSource.Explorer, "CabinetWClass")
    };

    private static bool Visible(SpacePreviewController controller) =>
        Fields.Get<bool>(controller, "_previewVisible");

    /// <summary>A glance in front with a PDF's find bar open, and published.</summary>
    private static Task WithFindBar(Func<SpacePreviewController, PreviewWindow, IntPtr, Task> test) => WpfApp.RunAsync(async () =>
    {
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // As if a fullscreen app were in front, so no test installs a real
        // mouse hook. No keyboard hook is asked for.
        Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
        using var glance = TestWindow.Root("GlimpseTestGlance");
        var window = new PreviewWindow();
        try
        {
            Fields.Set(window, "CurrentPath", Path.Combine(TestDataRoot.Path, "glance.pdf"));
            Fields.Set(window, "Hwnd", glance.Handle);
            Fields.Set(window, "_escapeYieldsToContent", true);
            Fields.Set(controller, "_previewWindow", window);
            Fields.Set(controller, "_previewVisible", true);
            Fields.Set(controller, "_previewOwnerRootHwnd", Owner);
            Fields.Set(controller, "_lastExplorerHwnd", Owner);
            Fields.Call(controller, "PublishKeyState");

            await test(controller, window, glance.Handle);
        }
        finally
        {
            // Dispose closes the glance.
            controller.Dispose();
        }
    });
}
