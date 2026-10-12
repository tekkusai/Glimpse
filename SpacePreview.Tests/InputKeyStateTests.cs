using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The hook thread decides each key from two things: what it read from
/// Windows at the press, and the state the dispatcher last published. The
/// first table is that decision. The second is what happens when the
/// published state was behind: the dispatcher routes the key again with the
/// state as it is, and Settle says what runs. A swallowed key never has a
/// stale meaning run on it, and a key that passed is never also run as a
/// claim.
/// </summary>
public sealed class InputKeyStateTests
{
    private const int Space = NativeMethods.VK_SPACE;
    private const int Enter = NativeMethods.VK_RETURN;
    private const int Esc = NativeMethods.VK_ESCAPE;
    private const int Left = NativeMethods.VK_LEFT;
    private const int Right = NativeMethods.VK_RIGHT;
    private const int Down = NativeMethods.VK_DOWN;

    private static readonly IntPtr Owner = new(0x2000);
    private static readonly IntPtr OtherExplorer = new(0x3000);
    private static readonly IntPtr WorkerW = new(0x4100);
    private static readonly IntPtr Dialog = new(0x5000);
    private static readonly IntPtr Glance = new(0x100);
    private static readonly IntPtr Pin = new(0x700);
    private static readonly IntPtr Stranger = new(0x9000);

    private static readonly KeyState Idle = KeyState.Idle;

    private static readonly KeyState Opening = new() { OpenPending = true };

    private static readonly KeyState Showing = new()
    {
        PreviewVisible = true,
        OwnerRoot = Owner,
        Preview = new WindowKeyState { Hwnd = Glance, Browsable = true }
    };

    private static readonly KeyState PickerShowing = Showing with
    {
        OwnerRoot = Dialog,
        Source = SelectionSource.CommonFileDialog
    };

    private static readonly KeyState Pinned = new()
    {
        Pinned = [new WindowKeyState { Hwnd = Pin, Browsable = true }]
    };

    private static readonly (string Promise, KeyPress Press, KeyState State, bool Swallow, KeyAction Action)[] Decisions =
    [
        ("Space on Explorer with nothing open opens", At(Owner, Space), Idle, true, KeyAction.Open),
        ("Space where nothing offers a selection passes", At(Stranger, Space), Idle, false, KeyAction.CheckElevation),
        ("Space in a picker with a verified selection opens it", InPicker(Space) with { PickerCacheReady = true }, Idle, true, KeyAction.OpenPicker),
        ("Space in a picker without one passes and is remembered", InPicker(Space), Idle, false, KeyAction.DeferPickerOpen),
        ("Esc during an open lookup cancels it and still reaches the app", At(Owner, Esc), Opening, false, KeyAction.CancelOpen),
        ("An arrow with nothing open is Explorer's", At(Owner, Right), Idle, false, KeyAction.None),

        ("Space over the glance's owner ends the glance", At(Owner, Space), Showing, true, KeyAction.Hide),
        ("Space in the glance ends it", At(Glance, Space), Showing, true, KeyAction.Hide),
        ("A rename in the owner keeps Space", At(Owner, Space) with { RootTextEditing = true }, Showing, false, KeyAction.None),
        ("Right over the owner browses", At(Owner, Right), Showing, true, KeyAction.Browse),
        ("Left in a rename moves the caret", At(Owner, Left) with { RootTextEditing = true }, Showing, false, KeyAction.FollowExplorer),
        ("Down over the owner stays Explorer's and the glance follows", At(Owner, Down), Showing, false, KeyAction.FollowExplorer),
        ("Down in the glance browses", At(Glance, Down), Showing, true, KeyAction.Browse),
        ("Down scrolls content the user clicked into", At(Glance, Down) with { RootChildFocus = true }, Window(engaged: true), false, KeyAction.None),
        ("Down browses when the click is old and focus is back in the chrome", At(Glance, Down), Window(engaged: true), true, KeyAction.Browse),
        ("A text box in the glance keeps the arrows", At(Glance, Left), Window(textFocus: true), false, KeyAction.None),
        ("Sign mode keeps Space with the page", At(Glance, Space), Window(fillSign: true), false, KeyAction.None),
        ("Sign mode keeps Space from Explorer too", At(Owner, Space), Window(fillSign: true), false, KeyAction.None),
        ("An armed find bar takes Esc first", At(Glance, Esc), Window(findBar: true), false, KeyAction.YieldEscape),
        ("A player keeps Space as play/pause", At(Glance, Space), Window(media: true), true, KeyAction.ToggleMedia),
        ("Enter in the glance opens the file", At(Glance, Enter), Showing, true, KeyAction.OpenCurrent),
        ("Enter after a click in the PDF toolbar goes to the page", At(Glance, Enter) with { RootChildFocus = true }, Window(enterYield: true), false, KeyAction.YieldEnter),
        ("Enter opens the file once focus has left the PDF toolbar", At(Glance, Enter), Window(enterYield: true), true, KeyAction.OpenCurrent),
        ("Enter over the owner ends the glance and Explorer opens the file", At(Owner, Enter), Showing, false, KeyAction.Hide),
        ("Ctrl+Enter toggles the sheet and never reaches Explorer", At(Owner, Enter) with { Ctrl = true }, Showing with { MultiSelection = true }, true, KeyAction.GridToggle),
        ("Esc closes the index sheet first", At(Owner, Esc), Window(grid: true), true, KeyAction.EscapeLayer),

        ("A key racing a foreground change goes where the foreground went: another Explorer window takes the glance", At(OtherExplorer, Space), Showing, true, KeyAction.Open),
        ("A key racing a foreground change goes where the foreground went: a stranger keeps its Space", At(Stranger, Space), Showing, false, KeyAction.CheckElevation),
        ("A key racing a foreground change goes where the foreground went: a stranger keeps its arrows", At(Stranger, Right), Showing, false, KeyAction.None),
        ("A key racing a foreground change goes where the foreground went: a stranger keeps its Esc", At(Stranger, Esc), Showing, false, KeyAction.None),

        ("Either desktop window owns a desktop glance", At(WorkerW, Space) with { RootIsDesktop = true }, Showing with { Source = SelectionSource.Desktop }, true, KeyAction.Hide),
        ("A desktop window is a stranger to an Explorer glance", At(WorkerW, Right) with { RootIsDesktop = true }, Showing, false, KeyAction.None),

        ("The picker owns its arrows and the glance follows", InPicker(Down) with { RootFileListFocus = true }, PickerShowing, false, KeyAction.FollowPicker),
        ("Space in a picker's list ends the glance", InPicker(Space) with { RootFileListFocus = true }, PickerShowing, true, KeyAction.Hide),
        ("A picker's name box keeps Space", InPicker(Space), PickerShowing, false, KeyAction.None),
        ("The picker owns commit", InPicker(Enter), PickerShowing, false, KeyAction.HideForPickerCommit),

        ("Esc closes the pinned window in front", At(Pin, Esc), Pinned, true, KeyAction.ClosePinned),
        ("Space in a pinned document is the document's", At(Pin, Space), Pinned, false, KeyAction.None),
        ("A pinned window in front answers for itself, not for the glance behind it", At(Pin, Space), Showing with { Pinned = [new WindowKeyState { Hwnd = Pin, MediaPlayable = true }] }, true, KeyAction.ToggleMedia),
        ("A pinned window behind leaves the glance its keys", At(Owner, Right), Showing with { Pinned = Pinned.Pinned }, true, KeyAction.Browse),
    ];

    public static TheoryData<string> DecisionPromises => new(Decisions.Select(row => row.Promise));

    [Theory]
    [MemberData(nameof(DecisionPromises))]
    public void TheHookThreadDecidesFromThePublishedState(string promise)
    {
        var row = Decisions.Single(candidate => candidate.Promise == promise);

        var decision = PreviewKeyMap.Route(PreviewKeyMap.Facts(row.Press, row.State));

        Assert.True(
            decision == new KeyDecision(row.Swallow, row.Action),
            $"{promise}: expected swallow={row.Swallow} action={row.Action}, got swallow={decision.Swallow} action={decision.Action}");
    }

    /// <summary>
    /// Published is what the hook thread held at the press; Now is what the
    /// dispatcher holds when the key's turn comes. Swallow is what Windows
    /// was told, Runs is what the dispatcher then does.
    /// </summary>
    private static readonly (string Promise, KeyPress Press, KeyState Published, KeyState Now, bool Swallow, KeyAction Runs)[] Races =
    [
        // A glance that ended since the last publish.
        ("A Space pressed while a hide is in flight opens again", At(Owner, Space), Showing, Idle, true, KeyAction.Open),
        ("An Esc pressed while a hide is in flight is spent", At(Owner, Esc), Showing, Idle, true, KeyAction.None),
        ("An arrow pressed as the glance closes under it runs on nothing", At(Owner, Right), Showing, Idle, true, KeyAction.None),
        ("Enter pressed as the glance closes is Explorer's, as it was", At(Owner, Enter), Showing, Idle, false, KeyAction.None),
        ("Down pressed as the glance closes is Explorer's, as it was", At(Owner, Down), Showing, Idle, false, KeyAction.None),
        ("Enter in a glance that closed under it opens nothing", At(Glance, Enter), Showing, Idle, true, KeyAction.None),
        ("F11 for a glance that closed fills nothing", At(Owner, NativeMethods.VK_F11), Showing, Idle, true, KeyAction.None),

        // A glance that is up by the key's turn although the hook thread saw
        // only an open on its way. The build is announced before it starts,
        // so this is the instant between the lookup's answer and that.
        ("A second Space as the glance lands closes it, as two presses did", At(Owner, Space), Opening, Showing, true, KeyAction.Hide),
        ("Esc that passed as the glance landed only cancels", At(Owner, Esc), Opening, Showing, false, KeyAction.CancelOpen),
        ("Right that reached Explorer as the glance landed is followed, never stepped twice", At(Owner, Right), Opening, Showing, false, KeyAction.FollowExplorer),
        ("Down that reached Explorer as the glance landed is followed", At(Owner, Down), Opening, Showing, false, KeyAction.FollowExplorer),
        ("Enter that reached Explorer as the glance landed ends the glance", At(Owner, Enter), Opening, Showing, false, KeyAction.Hide),
        ("F11 that reached Explorer as the glance landed is not run twice", At(Owner, NativeMethods.VK_F11), Opening, Showing, false, KeyAction.None),

        // A window that changed on its own since the last publish.
        ("Space swallowed before sign mode was published dismisses nothing", At(Glance, Space), Showing, Window(fillSign: true), true, KeyAction.None),
        ("Right swallowed before sign mode was published browses nothing", At(Owner, Right), Showing, Window(fillSign: true), true, KeyAction.None),
        ("Esc swallowed before the find bar was published dismisses nothing", At(Glance, Esc), Showing, Window(findBar: true), true, KeyAction.None),
        ("Down swallowed before a click into the content was published browses nothing", At(Glance, Down) with { RootChildFocus = true }, Showing, Window(engaged: true), true, KeyAction.None),
        ("Enter swallowed before a toolbar click was published opens nothing", At(Glance, Enter) with { RootChildFocus = true }, Showing, Window(enterYield: true), true, KeyAction.None),
        ("Space is play/pause as soon as the dispatcher knows the player is up", At(Glance, Space), Showing, Window(media: true), true, KeyAction.ToggleMedia),
        ("Esc closes the sheet the dispatcher knows is open", At(Owner, Esc), Showing, Window(grid: true), true, KeyAction.EscapeLayer),
        ("Enter that reached Explorer still ends the glance when the sheet opened since", At(Owner, Enter), Showing, Window(grid: true), false, KeyAction.Hide),
        ("Down that reached Explorer under a sheet opened since is followed, not moved in the sheet", At(Owner, Down), Showing, Window(grid: true), false, KeyAction.FollowExplorer),
        ("Space that reached the page after sign mode ended dismisses nothing", At(Glance, Space), Window(fillSign: true), Showing, false, KeyAction.None),

        // A pinned window that closed, or a glance that was pinned, since.
        ("Esc in a pinned window that closed since closes nothing else", At(Pin, Esc), Pinned, Idle, true, KeyAction.None),
        ("Space swallowed for a glance pinned since dismisses nothing", At(Glance, Space), Showing, Idle with { Pinned = [Showing.Preview] }, true, KeyAction.None),
    ];

    public static TheoryData<string> RacePromises => new(Races.Select(row => row.Promise));

    [Theory]
    [MemberData(nameof(RacePromises))]
    public void AKeyDecidedOnStateThatWasBehindIsSettledWithoutASecondMeaning(string promise)
    {
        var row = Races.Single(candidate => candidate.Promise == promise);

        var hook = PreviewKeyMap.Route(PreviewKeyMap.Facts(row.Press, row.Published));
        var now = PreviewKeyMap.Route(PreviewKeyMap.Facts(row.Press, row.Now));
        var runs = PreviewKeyMap.Settle(hook, now);

        Assert.True(
            hook.Swallow == row.Swallow && runs == row.Runs,
            $"{promise}: expected swallow={row.Swallow} runs={row.Runs}, got swallow={hook.Swallow} runs={runs} (hook={hook.Action} now={now.Action})");
    }

    [Fact]
    public void EveryPromiseIsStatedOnce()
    {
        Assert.Equal(Decisions.Length, Decisions.Select(row => row.Promise).Distinct().Count());
        Assert.Equal(Races.Length, Races.Select(row => row.Promise).Distinct().Count());
    }

    /// <summary>
    /// Whatever the two decisions are: with nothing stale the dispatcher's
    /// meaning runs, which is 3.0's behavior; a swallowed key the keymap now
    /// lets through runs nothing; a key that passed never runs a claim.
    /// </summary>
    [Fact]
    public void SettleNeverRunsAClaimOnAKeyThatPassedNorAStaleMeaningOnOneThatDidNot()
    {
        var actions = Enum.GetValues<KeyAction>();
        foreach (var hookAction in actions)
        {
            foreach (var nowAction in actions)
            {
                Assert.Equal(nowAction, PreviewKeyMap.Settle(new(true, hookAction), new(true, nowAction)));
                Assert.Equal(nowAction, PreviewKeyMap.Settle(new(false, hookAction), new(false, nowAction)));
                Assert.Equal(KeyAction.None, PreviewKeyMap.Settle(new(true, hookAction), new(false, nowAction)));

                var passed = PreviewKeyMap.Settle(new(false, hookAction), new(true, nowAction));
                Assert.Equal(
                    nowAction is KeyAction.Browse or KeyAction.GridMove ? KeyAction.FollowExplorer : hookAction,
                    passed);
            }
        }
    }

    /// <summary>
    /// The keymap's facts are gated as they were when the controller read
    /// them from the window at each key: a read only counts for the keys and
    /// the target it was made for.
    /// </summary>
    [Fact]
    public void FactsCountEachReadOnlyForTheKeysAndTargetItBelongsTo()
    {
        var everything = new KeyPress
        {
            RootTextEditing = true,
            RootFileListFocus = true,
            RootChildFocus = true
        };
        var player = Window(media: true, enterYield: true);

        // A player matters to Space and the arrows only.
        Assert.True(Facts(everything with { Vk = Space, Root = Glance }, player).Media);
        Assert.True(Facts(everything with { Vk = Down, Root = Glance }, player).Media);
        Assert.False(Facts(everything with { Vk = Enter, Root = Glance }, player).Media);

        // The toolbar click matters to Enter in the glance only.
        Assert.True(Facts(everything with { Vk = Enter, Root = Glance }, player).EnterYield);
        Assert.False(Facts(everything with { Vk = Enter, Root = Owner }, player).EnterYield);
        Assert.False(Facts(everything with { Vk = Space, Root = Glance }, player).EnterYield);

        // Focus in the browser view matters to arrows in the glance only.
        Assert.True(Facts(everything with { Vk = Down, Root = Glance }, player).ContentOwnsKeyboard);
        Assert.False(Facts(everything with { Vk = Down, Root = Owner }, player).ContentOwnsKeyboard);
        Assert.False(Facts(everything with { Vk = Space, Root = Glance }, player).ContentOwnsKeyboard);

        // A caret matters in the owner, to Space and the arrows.
        Assert.True(Facts(everything with { Vk = Left, Root = Owner }, Showing).OwnerTextEditing);
        Assert.False(Facts(everything with { Vk = Enter, Root = Owner }, Showing).OwnerTextEditing);
        Assert.False(Facts(everything with { Vk = Left, Root = Glance }, Showing).OwnerTextEditing);
        Assert.False(Facts(everything with { Vk = Left, Root = Stranger }, Showing).OwnerTextEditing);

        // List focus matters in a picker that owns the glance.
        Assert.True(Facts(everything with { Vk = Down, Root = Dialog }, PickerShowing).PickerListFocus);
        Assert.False(Facts(everything with { Vk = Down, Root = Owner }, Showing).PickerListFocus);
        Assert.False(Facts(everything with { Vk = Esc, Root = Dialog }, PickerShowing).PickerListFocus);

        // No preview, no window facts; the welcome has no file to browse.
        Assert.False(Facts(everything with { Vk = Down, Root = Glance }, Idle with { Preview = player.Preview }).Browsable);
        Assert.Equal(KeyTarget.Other, Facts(everything with { Vk = Down, Root = Glance }, Idle with { Preview = player.Preview }).Target);
        Assert.False(Facts(everything with { Vk = Down, Root = Owner }, Showing with { Preview = default }).Browsable);
    }

    private static KeyFacts Facts(KeyPress press, KeyState state) => PreviewKeyMap.Facts(press, state);

    /// <summary>
    /// A bare key as the hook thread reads it. A fresh Space asks what the
    /// window in front offers: Explorer's windows a selection, the rest nothing.
    /// </summary>
    private static KeyPress At(IntPtr root, int vk) => new()
    {
        Vk = vk,
        Root = root,
        Context = vk == Space && (root == Owner || root == OtherExplorer)
            ? new ForegroundSelectionContext(root, SelectionSource.Explorer, "CabinetWClass")
            : null
    };

    private static KeyPress InPicker(int vk) => new()
    {
        Vk = vk,
        Root = Dialog,
        Context = vk == Space
            ? new ForegroundSelectionContext(Dialog, SelectionSource.CommonFileDialog, "#32770")
            : null
    };

    /// <summary>The glance over <see cref="Owner"/>, with its window in one of the states a preview gets into.</summary>
    private static KeyState Window(
        bool fillSign = false,
        bool textFocus = false,
        bool findBar = false,
        bool grid = false,
        bool engaged = false,
        bool media = false,
        bool enterYield = false) =>
        Showing with
        {
            Preview = new WindowKeyState
            {
                Hwnd = Glance,
                Browsable = true,
                FillSign = fillSign,
                TextFocus = textFocus,
                FindBarArmed = findBar,
                GridOpen = grid,
                EscapeLayer = grid,
                ContentEngaged = engaged,
                MediaPlayable = media,
                EnterYield = enterYield
            }
        };
}
