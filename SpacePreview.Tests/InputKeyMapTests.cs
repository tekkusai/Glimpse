using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The keymap is the product. Each row is a promise from the keyboard model:
/// given where the keys go and what the preview is doing, does Glimpse take
/// the key, and what follows.
/// </summary>
public sealed class InputKeyMapTests
{
    private const int Space = NativeMethods.VK_SPACE;
    private const int Enter = NativeMethods.VK_RETURN;
    private const int Esc = NativeMethods.VK_ESCAPE;
    private const int Left = NativeMethods.VK_LEFT;
    private const int Right = NativeMethods.VK_RIGHT;
    private const int Up = NativeMethods.VK_UP;
    private const int Down = NativeMethods.VK_DOWN;
    private const int Home = NativeMethods.VK_HOME;

    private static readonly ForegroundSelectionContext ExplorerWindow =
        new(new IntPtr(0x2000), SelectionSource.Explorer, "CabinetWClass");

    private static readonly ForegroundSelectionContext OtherExplorerWindow =
        new(new IntPtr(0x3000), SelectionSource.Explorer, "CabinetWClass");

    private static readonly ForegroundSelectionContext Desktop =
        new(new IntPtr(0x4000), SelectionSource.Desktop, "Progman");

    private static readonly ForegroundSelectionContext Picker =
        new(new IntPtr(0x5000), SelectionSource.CommonFileDialog, "#32770");

    private static readonly (string Promise, KeyFacts Facts, bool Swallow, KeyAction Action)[] Rows =
    [
        // Opening.
        ("Space on Explorer opens the selection", Idle(Space, ExplorerWindow), true, KeyAction.Open),
        ("Space on the desktop opens the selection", Idle(Space, Desktop), true, KeyAction.Open),
        ("Space in a picker with a verified selection opens it", Idle(Space, Picker) with { PickerCacheReady = true }, true, KeyAction.OpenPicker),
        ("Space in a picker without one passes and remembers the press", Idle(Space, Picker), false, KeyAction.DeferPickerOpen),
        ("Space where nothing offers a selection passes", Idle(Space, null), false, KeyAction.CheckElevation),
        ("Shift+Space never opens", Idle(Space, ExplorerWindow) with { Shift = true }, false, KeyAction.None),
        ("A held Space opens once", Idle(Space, ExplorerWindow) with { SpaceRepeat = true }, false, KeyAction.None),
        ("Esc during an open lookup cancels it and still reaches the app", Idle(Esc, ExplorerWindow) with { OpenPending = true }, false, KeyAction.CancelOpen),
        ("Enter with nothing open is Explorer's", Idle(Enter, ExplorerWindow), false, KeyAction.None),

        // Space while a glance is up.
        ("Space in Explorer ends the glance", Owner(Space), true, KeyAction.Hide),
        ("Space ends the glance even after a click into the content", OnPreview(Space) with { ContentEngaged = true, ContentOwnsKeyboard = true }, true, KeyAction.Hide),
        ("A player keeps Space as play/pause", OnPreview(Space) with { Media = true }, true, KeyAction.ToggleMedia),
        ("A held Space over a player does not machine-gun the toggle", OnPreview(Space) with { Media = true, SpaceRepeat = true }, true, KeyAction.None),
        ("A held Space stays away from Explorer", Owner(Space) with { SpaceRepeat = true }, true, KeyAction.None),
        ("An armed find bar keeps its spaces", OnPreview(Space) with { FindBarArmed = true }, false, KeyAction.None),
        ("A rename in Explorer keeps Space", Owner(Space) with { OwnerTextEditing = true }, false, KeyAction.None),
        ("A picker's name box keeps Space", PickerOwner(Space) with { PickerListFocus = false }, false, KeyAction.None),
        ("Space in a picker's list ends the glance", PickerOwner(Space), true, KeyAction.Hide),
        ("Space in another Explorer window moves the glance there", Elsewhere(Space, OtherExplorerWindow), true, KeyAction.Open),
        ("Space in an unrelated app passes", Elsewhere(Space, null), false, KeyAction.CheckElevation),

        // Esc.
        ("Esc in Explorer ends the glance", Owner(Esc), true, KeyAction.Hide),
        ("Esc in the preview ends the glance", OnPreview(Esc), true, KeyAction.Hide),
        ("Esc closes the index sheet or slideshow first", Owner(Esc) with { EscapeLayer = true }, true, KeyAction.EscapeLayer),
        ("An armed find bar takes Esc first", OnPreview(Esc) with { FindBarArmed = true }, false, KeyAction.YieldEscape),
        ("Esc in an unrelated app is left alone", Elsewhere(Esc, null), false, KeyAction.None),

        // Enter.
        ("Enter in Explorer ends the glance and Explorer opens the file", Owner(Enter), false, KeyAction.Hide),
        ("Enter in the preview opens the file even when the browser holds the keyboard", OnPreview(Enter) with { ContentEngaged = true, ContentOwnsKeyboard = true }, true, KeyAction.OpenCurrent),
        ("Enter after a click in the PDF toolbar goes to the page", OnPreview(Enter) with { EnterYield = true }, false, KeyAction.YieldEnter),
        ("An armed find bar keeps Enter as next match", OnPreview(Enter) with { FindBarArmed = true }, false, KeyAction.None),
        ("Shift+Enter in the preview is the content's", OnPreview(Enter) with { Shift = true }, false, KeyAction.None),
        ("The picker owns commit", PickerOwner(Enter), false, KeyAction.HideForPickerCommit),
        ("Enter in a picker's preview goes back to the dialog", PickerOwner(Enter) with { Target = KeyTarget.Preview }, true, KeyAction.ReturnToPicker),
        ("Ctrl+Enter toggles the sheet and never reaches Explorer", Owner(Enter) with { Ctrl = true, MultiSelection = true }, true, KeyAction.GridToggle),
        ("Ctrl+Enter toggles the sheet from the preview", OnPreview(Enter) with { Ctrl = true, MultiSelection = true }, true, KeyAction.GridToggle),
        ("Enter commits the sheet's cursor", Owner(Enter) with { GridOpen = true }, true, KeyAction.GridCommit),

        // Arrows and the other navigation keys.
        ("Left browses from Explorer", Owner(Left), true, KeyAction.Browse),
        ("Right browses from Explorer", Owner(Right), true, KeyAction.Browse),
        ("Down stays native in Explorer and the glance follows", Owner(Down), false, KeyAction.FollowExplorer),
        ("Home stays native in Explorer and the glance follows", Owner(Home), false, KeyAction.FollowExplorer),
        ("Left in a rename moves the caret", Owner(Left) with { OwnerTextEditing = true }, false, KeyAction.FollowExplorer),
        ("Down browses from the preview", OnPreview(Down), true, KeyAction.Browse),
        ("Down scrolls content the user clicked into", OnPreview(Down) with { ContentEngaged = true, ContentOwnsKeyboard = true }, false, KeyAction.None),
        ("Right browses even over clicked content", OnPreview(Right) with { ContentEngaged = true, ContentOwnsKeyboard = true }, true, KeyAction.Browse),
        ("A text box in the preview keeps the arrows", OnPreview(Left) with { TextFocus = true }, false, KeyAction.None),
        ("A clicked player takes Up as volume", OnPreview(Up) with { Media = true, ContentEngaged = true, ContentOwnsKeyboard = true }, true, KeyAction.NudgeMedia),
        ("Home in the preview is the content's", OnPreview(Home), false, KeyAction.None),
        ("Nothing to browse leaves the arrows alone", OnPreview(Left) with { Browsable = false }, false, KeyAction.None),
        ("The sheet captures all four arrows", Owner(Down) with { GridOpen = true }, true, KeyAction.GridMove),
        ("The picker owns its arrows and the glance follows", PickerOwner(Down), false, KeyAction.FollowPicker),
        ("Arrows in a picker's name box are left alone", PickerOwner(Down) with { PickerListFocus = false }, false, KeyAction.None),
        ("Arrows in an unrelated app are left alone", Elsewhere(Left, null), false, KeyAction.None),

        // Sign mode: placed ink is never browsed or dismissed away by a key.
        ("Sign mode: Space reaches the page", OnPreview(Space) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Enter reaches the date editor", OnPreview(Enter) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Left does not browse", OnPreview(Left) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Right in Explorer does not browse", Owner(Right) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Down in Explorer is not followed", Owner(Down) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Space in Explorer does not dismiss", Owner(Space) with { FillSign = true }, false, KeyAction.None),
        ("Sign mode: Esc walks the mode's layers", OnPreview(Esc) with { FillSign = true }, true, KeyAction.FillSignEscape),
        ("Sign mode: Esc from Explorer asks too", Owner(Esc) with { FillSign = true }, true, KeyAction.FillSignEscape),
        ("Sign mode: Esc in the date editor reaches the editor", OnPreview(Esc) with { FillSign = true, TextFocus = true }, false, KeyAction.None),

        // The shortcuts the tooltips name work from the glance.
        ("F11 fills the screen from Explorer", Owner(NativeMethods.VK_F11), true, KeyAction.FillScreen),
        ("F11 fills the screen from the preview", OnPreview(NativeMethods.VK_F11), true, KeyAction.FillScreen),
        ("Ctrl+R rotates an image from Explorer", Owner(NativeMethods.VK_R) with { Ctrl = true, CanRotate = true }, true, KeyAction.Rotate),
        ("Ctrl+Shift+R rotates back", Owner(NativeMethods.VK_R) with { Ctrl = true, Shift = true, CanRotate = true }, true, KeyAction.Rotate),
        ("Ctrl+R with nothing to rotate stays Explorer's refresh", Owner(NativeMethods.VK_R) with { Ctrl = true }, false, KeyAction.None),
        ("Ctrl+Plus zooms from Explorer", Owner(NativeMethods.VK_OEM_PLUS) with { Ctrl = true, HeaderZoom = true }, true, KeyAction.Zoom),
        ("Ctrl+0 resets zoom from the preview", OnPreview(NativeMethods.VK_0) with { Ctrl = true, HeaderZoom = true }, true, KeyAction.Zoom),
        ("Ctrl+Plus with nothing to zoom is left alone", Owner(NativeMethods.VK_OEM_PLUS) with { Ctrl = true }, false, KeyAction.None),
        ("F11 in an unrelated app is left alone", Elsewhere(NativeMethods.VK_F11, null), false, KeyAction.None),
        ("Ctrl+F in the preview arms the find bar's Esc", OnPreview(NativeMethods.VK_F) with { Ctrl = true }, false, KeyAction.NoteFind),

        // Pinned windows keep their keys, except the way out.
        ("Esc closes the focused pinned window", Pinned(Esc), true, KeyAction.ClosePinned),
        ("A pinned find bar takes Esc first", Pinned(Esc) with { FindBarArmed = true }, false, KeyAction.YieldEscape),
        ("A pinned player keeps Space", Pinned(Space) with { Media = true }, true, KeyAction.ToggleMedia),
        ("Space in a pinned document is the document's", Pinned(Space), false, KeyAction.None),
        ("A pinned player seeks with Left", Pinned(Left) with { Media = true }, true, KeyAction.NudgeMedia),
        ("Enter in a pinned window is the window's", Pinned(Enter), false, KeyAction.None),
        ("Esc in a signing pinned window asks before it closes", Pinned(Esc) with { FillSign = true }, true, KeyAction.FillSignEscape),
        ("Space in a signing pinned window reaches the page", Pinned(Space) with { FillSign = true, Media = true }, false, KeyAction.None),
    ];

    public static TheoryData<string> Promises => new(Rows.Select(row => row.Promise));

    [Theory]
    [MemberData(nameof(Promises))]
    public void KeymapKeepsItsPromises(string promise)
    {
        var row = Rows.Single(candidate => candidate.Promise == promise);

        var decision = PreviewKeyMap.Route(row.Facts);

        Assert.True(
            decision == new KeyDecision(row.Swallow, row.Action),
            $"{promise}: expected swallow={row.Swallow} action={row.Action}, got swallow={decision.Swallow} action={decision.Action}");
    }

    [Fact]
    public void EveryPromiseIsStatedOnce()
    {
        Assert.Equal(Rows.Length, Rows.Select(row => row.Promise).Distinct().Count());
    }

    [Fact]
    public void CopyIsNeverExamined()
    {
        // Ctrl+C reaches whichever window is in front: Explorer copies the
        // file it shows, a clicked preview copies its own content.
        Assert.False(PreviewKeyMap.IsMapped(0x43));
        Assert.False(PreviewKeyMap.IsMapped(0x41));
    }

    [Theory]
    [InlineData(true, false, false, Down, true)]
    [InlineData(true, true, false, Down, true)]
    [InlineData(false, true, false, Down, false)]
    [InlineData(true, false, true, Down, false)]
    [InlineData(true, false, false, NativeMethods.VK_DELETE, false)]
    public void OnlyShiftEditsExplorersSelection(bool shift, bool ctrl, bool alt, int vk, bool expected)
    {
        var facts = new KeyFacts { Vk = vk, Shift = shift, Ctrl = ctrl, Alt = alt };

        Assert.Equal(expected, PreviewKeyMap.EditsSelection(facts));
    }

    [Theory]
    [InlineData(0x100, false, true, "Preview")]
    [InlineData(0x200, false, true, "Owner")]
    [InlineData(0x300, false, true, "Other")]
    [InlineData(0x300, true, true, "Pinned")]
    [InlineData(0x300, true, false, "Pinned")]
    [InlineData(0x200, false, false, "Other")]
    [InlineData(0x0, false, true, "Other")]
    public void KeysGoToThePreviewItsOwnerAPinnedWindowOrElsewhere(
        int foreground,
        bool pinned,
        bool visible,
        string expected)
    {
        var target = PreviewKeyMap.Classify(
            new IntPtr(foreground),
            pinned,
            visible,
            previewHwnd: new IntPtr(0x100),
            ownerRoot: new IntPtr(0x200),
            ownerIsDesktop: false,
            foregroundIsDesktop: false);

        Assert.Equal(Enum.Parse<KeyTarget>(expected), target);
    }

    [Fact]
    public void EitherDesktopWindowOwnsADesktopGlance()
    {
        // Progman and WorkerW trade the foreground; a click on the desktop
        // must not turn the glance's owner into a stranger.
        Assert.Equal(KeyTarget.Owner, PreviewKeyMap.Classify(
            new IntPtr(0x300), false, true, new IntPtr(0x100), new IntPtr(0x200), ownerIsDesktop: true, foregroundIsDesktop: true));
        Assert.Equal(KeyTarget.Other, PreviewKeyMap.Classify(
            new IntPtr(0x300), false, true, new IntPtr(0x100), new IntPtr(0x200), ownerIsDesktop: false, foregroundIsDesktop: true));
    }

    private static KeyFacts Idle(int vk, ForegroundSelectionContext? context) =>
        new() { Vk = vk, Context = context };

    private static KeyFacts Owner(int vk) => new()
    {
        Vk = vk,
        PreviewVisible = true,
        Target = KeyTarget.Owner,
        Source = SelectionSource.Explorer,
        Browsable = true
    };

    private static KeyFacts OnPreview(int vk) => Owner(vk) with { Target = KeyTarget.Preview };

    private static KeyFacts PickerOwner(int vk) =>
        Owner(vk) with { Source = SelectionSource.CommonFileDialog, PickerListFocus = true };

    private static KeyFacts Elsewhere(int vk, ForegroundSelectionContext? context) =>
        Owner(vk) with { Target = KeyTarget.Other, Context = context };

    private static KeyFacts Pinned(int vk) => new() { Vk = vk, Target = KeyTarget.Pinned };
}
