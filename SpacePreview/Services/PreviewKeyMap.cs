using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>Where the keyboard points, seen from the previews.</summary>
internal enum KeyTarget
{
    /// <summary>A window no preview belongs to.</summary>
    Other,

    /// <summary>The transient preview itself.</summary>
    Preview,

    /// <summary>The window the transient preview was opened from.</summary>
    Owner,

    /// <summary>A pinned preview.</summary>
    Pinned
}

/// <summary>What follows a key, run by the controller after the swallow decision.</summary>
internal enum KeyAction
{
    None,
    Open,
    OpenPicker,
    DeferPickerOpen,
    CancelOpen,
    Hide,
    HideForPickerCommit,
    ReturnToPicker,
    OpenCurrent,
    YieldEnter,
    YieldEscape,
    NoteFind,
    ToggleMedia,
    NudgeMedia,
    Browse,
    GridMove,
    GridToggle,
    GridCommit,
    EscapeLayer,
    FillSignEscape,
    ClosePinned,
    Zoom,
    FillScreen,
    Rotate,
    FollowExplorer,
    FollowPicker
}

internal readonly record struct KeyDecision(bool Swallow, KeyAction Action);

/// <summary>
/// Everything the keymap reads, captured once per key. Window facts describe
/// the window the keys are going to: the pinned preview when it has the
/// foreground, otherwise the transient one.
/// </summary>
internal readonly record struct KeyFacts
{
    public int Vk { get; init; }
    public bool Shift { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Win { get; init; }

    /// <summary>A held Space whose first press the keymap already acted on.</summary>
    public bool SpaceRepeat { get; init; }

    public KeyTarget Target { get; init; }

    public bool PreviewVisible { get; init; }
    public bool OpenPending { get; init; }
    public SelectionSource Source { get; init; }
    public bool MultiSelection { get; init; }

    /// <summary>
    /// A text box in the window the key is going to holds the keyboard: a
    /// rename, the address bar, a search box, a picker's name box. Never set
    /// for a preview, whose own text places have their own rules below.
    /// </summary>
    public bool Typing { get; init; }

    /// <summary>The owner picker's file list holds focus (not its name box or buttons).</summary>
    public bool PickerListFocus { get; init; }

    public bool Browsable { get; init; }
    public bool FillSign { get; init; }

    /// <summary>A WPF text box in the window holds focus.</summary>
    public bool TextFocus { get; init; }

    public bool FindBarArmed { get; init; }
    public bool EnterYield { get; init; }
    public bool Media { get; init; }
    public bool ContentEngaged { get; init; }
    public bool ContentOwnsKeyboard { get; init; }
    public bool GridOpen { get; init; }
    public bool EscapeLayer { get; init; }
    public bool HeaderZoom { get; init; }
    public bool CanRotate { get; init; }

    /// <summary>The surface in front offers a selection to open; null otherwise.</summary>
    public ForegroundSelectionContext? Context { get; init; }

    /// <summary>The picker in front has this selection verified; null while it has none.</summary>
    public ExplorerSelection? PickerSelection { get; init; }
}

/// <summary>
/// The keymap is the product. One pure decision per key: swallow it or let
/// it through, and what follows. The hook thread reads the key and the
/// dispatcher publishes the previews' state; the facts are those two set
/// against each other. Nothing here touches a window.
/// </summary>
internal static class PreviewKeyMap
{
    private static readonly KeyDecision Pass = new(false, KeyAction.None);

    /// <summary>Keys the map can claim. Every other key is never examined.</summary>
    internal static bool IsMapped(int vk) =>
        vk is NativeMethods.VK_SPACE
            or NativeMethods.VK_RETURN
            or NativeMethods.VK_ESCAPE
            or NativeMethods.VK_F
            or NativeMethods.VK_F11
            or NativeMethods.VK_R
        || IsNavigation(vk)
        || IsZoomKey(vk);

    internal static bool IsNavigation(int vk) =>
        vk is NativeMethods.VK_LEFT
            or NativeMethods.VK_UP
            or NativeMethods.VK_RIGHT
            or NativeMethods.VK_DOWN
            or NativeMethods.VK_NEXT
            or NativeMethods.VK_PRIOR
            or NativeMethods.VK_HOME
            or NativeMethods.VK_END
            or NativeMethods.VK_DELETE;

    internal static bool IsArrow(int vk) =>
        vk is NativeMethods.VK_LEFT
            or NativeMethods.VK_UP
            or NativeMethods.VK_RIGHT
            or NativeMethods.VK_DOWN;

    internal static bool IsZoomKey(int vk) =>
        vk is NativeMethods.VK_ADD
            or NativeMethods.VK_OEM_PLUS
            or NativeMethods.VK_SUBTRACT
            or NativeMethods.VK_OEM_MINUS
            or NativeMethods.VK_0
            or NativeMethods.VK_NUMPAD0;

    /// <summary>
    /// Desktop windows trade the foreground between Progman and WorkerW, so
    /// either one counts as a desktop owner.
    /// </summary>
    internal static KeyTarget Classify(
        IntPtr foregroundRoot,
        bool pinnedForeground,
        bool previewVisible,
        IntPtr previewHwnd,
        IntPtr ownerRoot,
        bool ownerIsDesktop,
        bool foregroundIsDesktop)
    {
        if (pinnedForeground)
        {
            return KeyTarget.Pinned;
        }

        if (!previewVisible || foregroundRoot == IntPtr.Zero)
        {
            return KeyTarget.Other;
        }

        if (foregroundRoot == previewHwnd)
        {
            return KeyTarget.Preview;
        }

        if (ownerRoot != IntPtr.Zero
            && (foregroundRoot == ownerRoot || (ownerIsDesktop && foregroundIsDesktop)))
        {
            return KeyTarget.Owner;
        }

        return KeyTarget.Other;
    }

    /// <summary>
    /// The facts for one key. The hook thread passes the state the dispatcher
    /// last published; the dispatcher, when the key's turn comes, passes the
    /// state as it is then. Equal inputs give equal facts.
    /// </summary>
    public static KeyFacts Facts(in KeyPress key, KeyState state)
    {
        WindowKeyState? pinned = null;
        foreach (var candidate in state.Pinned)
        {
            if (key.Root != IntPtr.Zero && candidate.Hwnd == key.Root)
            {
                pinned = candidate;
                break;
            }
        }

        var ownerIsDesktop = state.Source == SelectionSource.Desktop;
        var target = Classify(
            key.Root,
            pinned is not null,
            state.PreviewVisible,
            state.Preview.Hwnd,
            state.OwnerRoot,
            ownerIsDesktop,
            ownerIsDesktop && key.Root != state.OwnerRoot && key.RootIsDesktop);
        var space = key.Vk == NativeMethods.VK_SPACE;
        var arrow = IsArrow(key.Vk);
        var facts = new KeyFacts
        {
            Vk = key.Vk,
            Shift = key.Shift,
            Ctrl = key.Ctrl,
            Alt = key.Alt,
            Win = key.Win,
            SpaceRepeat = key.SpaceRepeat,
            Target = target,
            PreviewVisible = state.PreviewVisible,
            OpenPending = state.OpenPending,
            Source = state.Source,
            MultiSelection = state.MultiSelection,
            Typing = key.RootTyping && target is not (KeyTarget.Preview or KeyTarget.Pinned),
            PickerListFocus = target == KeyTarget.Owner
                && state.Source == SelectionSource.CommonFileDialog
                && (space || IsNavigation(key.Vk))
                && key.RootFileListFocus,
            Context = key.Context,
            PickerSelection = key.PickerSelection
        };

        // Window facts describe the window the keys are going to: the pinned
        // preview in front, otherwise the transient one.
        if ((pinned ?? (state.PreviewVisible ? state.Preview : null)) is not { } window)
        {
            return facts;
        }

        return facts with
        {
            Browsable = window.Browsable,
            FillSign = window.FillSign,
            TextFocus = window.TextFocus,
            FindBarArmed = window.FindBarArmed,
            GridOpen = window.GridOpen,
            EscapeLayer = window.EscapeLayer,
            HeaderZoom = window.HeaderZoom,
            CanRotate = window.CanRotate,
            ContentEngaged = window.ContentEngaged,
            Media = (space || arrow) && window.MediaPlayable,
            EnterYield = key.Vk == NativeMethods.VK_RETURN
                && target == KeyTarget.Preview
                && window.EnterYield
                && key.RootChildFocus,
            ContentOwnsKeyboard = arrow && target == KeyTarget.Preview && key.RootChildFocus
        };
    }

    /// <summary>
    /// The hook thread answered Windows from the published state, which can
    /// be a turn behind. The dispatcher routes the same key again with the
    /// state as it is, and this says what to run. The swallow is already
    /// done and cannot be taken back.
    /// </summary>
    public static KeyAction Settle(KeyDecision hook, KeyDecision now)
    {
        // Nothing was stale, or it changed nothing Windows was told.
        if (hook.Swallow == now.Swallow)
        {
            return now.Action;
        }

        // Swallowed for something that is no longer true. The app the key
        // belonged to cannot be given it back; running the old meaning
        // would act on a window that has moved on.
        if (hook.Swallow)
        {
            return KeyAction.None;
        }

        // The key went to the app. An arrow Explorer has already acted on is
        // followed, never stepped a second time. Anything else keeps the
        // meaning that went with the pass.
        return now.Action is KeyAction.Browse or KeyAction.GridMove
            ? KeyAction.FollowExplorer
            : hook.Action;
    }

    /// <summary>
    /// The published state as it will be once the dispatcher has run
    /// <paramref name="action"/> for a key that went to <paramref name="root"/>.
    /// The hook thread answers a key before the dispatcher has run the one
    /// before it, so it counts in what that one is certain to change: a
    /// glance it ends, a find bar or a toolbar click it spends. The same
    /// state comes back when the action changes nothing the keymap reads,
    /// or nothing that is certain.
    /// </summary>
    public static KeyState After(KeyState state, IntPtr root, KeyAction action)
    {
        switch (action)
        {
            case KeyAction.Hide:
            case KeyAction.HideForPickerCommit:
            case KeyAction.ReturnToPicker:
                return state.PreviewVisible ? state with { PreviewVisible = false } : state;
            case KeyAction.YieldEscape:
                return Spend(state, root, findBar: true);
            case KeyAction.YieldEnter:
            case KeyAction.OpenCurrent:
                return Spend(state, root, findBar: false);
            default:
                return state;
        }
    }

    // The window is picked as Facts picks it: the pinned preview the key
    // went to, otherwise the glance.
    private static KeyState Spend(KeyState state, IntPtr root, bool findBar)
    {
        for (var i = 0; i < state.Pinned.Length; i++)
        {
            if (root == IntPtr.Zero || state.Pinned[i].Hwnd != root)
            {
                continue;
            }

            if (!Armed(state.Pinned[i], findBar))
            {
                return state;
            }

            var pinned = (WindowKeyState[])state.Pinned.Clone();
            pinned[i] = Spent(pinned[i], findBar);
            return state with { Pinned = pinned };
        }

        return state.PreviewVisible && Armed(state.Preview, findBar)
            ? state with { Preview = Spent(state.Preview, findBar) }
            : state;
    }

    // Esc for a find bar spends the bar and the toolbar click with it; an
    // Enter spends the click alone.
    private static bool Armed(WindowKeyState window, bool findBar) =>
        window.EnterYield || (findBar && window.FindBarArmed);

    private static WindowKeyState Spent(WindowKeyState window, bool findBar) =>
        window with { FindBarArmed = window.FindBarArmed && !findBar, EnterYield = false };

    public static KeyDecision Route(in KeyFacts f)
    {
        // When the user is typing, Glimpse is not listening. Every key is
        // the text box's, and one that ends the typing (Esc on a rename)
        // leaves the next one to the glance: innermost thing first.
        if (f.Typing)
        {
            return Pass;
        }

        // Esc with Ctrl, Alt or Win is the shell's: Task Manager, Start,
        // window cycling. A swallowed down would cost the user a second press.
        if (f.Vk == NativeMethods.VK_ESCAPE && (f.Ctrl || f.Alt || f.Win))
        {
            return Pass;
        }

        if (f.Target == KeyTarget.Pinned)
        {
            return RoutePinned(f);
        }

        return f.PreviewVisible ? RouteVisible(f) : RouteIdle(f);
    }

    // Keys typed into a pinned preview belong to it. Esc is the universal
    // way out, and a pinned player keeps Space and the arrows.
    private static KeyDecision RoutePinned(in KeyFacts f)
    {
        var bare = NoModifiers(f);
        if (f.Vk == NativeMethods.VK_ESCAPE)
        {
            if (f.FillSign)
            {
                return f.TextFocus ? Pass : Claim(KeyAction.FillSignEscape);
            }

            return f.FindBarArmed ? Let(KeyAction.YieldEscape) : Claim(KeyAction.ClosePinned);
        }

        if (f.Vk == NativeMethods.VK_F && f.Ctrl)
        {
            return Let(KeyAction.NoteFind);
        }

        if (f.FillSign || !f.Media || !bare)
        {
            return Pass;
        }

        if (f.Vk == NativeMethods.VK_SPACE)
        {
            return Claim(f.SpaceRepeat ? KeyAction.None : KeyAction.ToggleMedia);
        }

        return IsArrow(f.Vk) ? Claim(KeyAction.NudgeMedia) : Pass;
    }

    private static KeyDecision RouteIdle(in KeyFacts f)
    {
        if (f.Vk == NativeMethods.VK_ESCAPE && f.OpenPending)
        {
            return Let(KeyAction.CancelOpen);
        }

        if (f.Vk == NativeMethods.VK_SPACE && NoModifiers(f))
        {
            return f.SpaceRepeat ? Pass : OpenFromForeground(f);
        }

        return Pass;
    }

    private static KeyDecision RouteVisible(in KeyFacts f)
    {
        var vk = f.Vk;
        var bare = NoModifiers(f);
        var ours = f.Target is KeyTarget.Preview or KeyTarget.Owner;

        // Sign mode: the user is placing ink. Space, Enter and the arrows
        // belong to the page and its date editor, and Esc walks the mode's
        // own layers, save choice first.
        if (f.FillSign)
        {
            if (vk == NativeMethods.VK_ESCAPE)
            {
                if (f.Target == KeyTarget.Preview && f.TextFocus)
                {
                    return Pass;
                }

                return ours ? Claim(KeyAction.FillSignEscape) : Pass;
            }

            if (vk is NativeMethods.VK_SPACE or NativeMethods.VK_RETURN || IsNavigation(vk))
            {
                return Pass;
            }
        }

        // Ctrl+F opens the PDF viewer's find bar; Esc must close that bar
        // before it closes the preview.
        if (vk == NativeMethods.VK_F && f.Ctrl)
        {
            return f.Target == KeyTarget.Preview ? Let(KeyAction.NoteFind) : Pass;
        }

        // The shortcuts the tooltips name work from the glance too, except
        // rotation: it rewrites the file, so it needs the preview in front,
        // and in Explorer Ctrl+R stays Refresh.
        if (ours)
        {
            if (vk == NativeMethods.VK_F11 && bare)
            {
                return Claim(KeyAction.FillScreen);
            }

            if (vk == NativeMethods.VK_R
                && f.Target == KeyTarget.Preview
                && f.Ctrl && !f.Alt && !f.Win && f.CanRotate)
            {
                return Claim(KeyAction.Rotate);
            }

            if (IsZoomKey(vk) && f.Ctrl && !f.Alt && f.HeaderZoom)
            {
                return Claim(KeyAction.Zoom);
            }
        }

        if (vk == NativeMethods.VK_ESCAPE)
        {
            if (!ours)
            {
                return Pass;
            }

            if (f.Target == KeyTarget.Preview && f.FindBarArmed)
            {
                return Let(KeyAction.YieldEscape);
            }

            // The index sheet and a running slideshow absorb Esc first.
            return Claim(f.EscapeLayer ? KeyAction.EscapeLayer : KeyAction.Hide);
        }

        if (vk == NativeMethods.VK_RETURN)
        {
            return ours ? RouteEnter(f, bare) : Pass;
        }

        if (IsNavigation(vk))
        {
            return ours ? RouteNavigation(f, bare) : Pass;
        }

        if (vk == NativeMethods.VK_SPACE && bare)
        {
            return RouteSpace(f, ours);
        }

        return Pass;
    }

    private static KeyDecision RouteEnter(in KeyFacts f, bool bare)
    {
        // The picker owns commit. With its list in front the original Enter
        // reaches its Open button; from the preview, focus goes back to it
        // instead of launching a second app behind the modal dialog.
        if (f.Source == SelectionSource.CommonFileDialog)
        {
            return f.Target == KeyTarget.Owner
                ? Let(KeyAction.HideForPickerCommit)
                : Claim(KeyAction.ReturnToPicker);
        }

        // Ctrl+Enter toggles the index sheet for a multi-selection. Explorer
        // must not see it: it would open every selected file.
        if (f.Ctrl)
        {
            return f.MultiSelection ? Claim(KeyAction.GridToggle) : Pass;
        }

        if (f.GridOpen)
        {
            return Claim(KeyAction.GridCommit);
        }

        // Quick Look: opening the file ends the glance, and Explorer still
        // receives Enter and launches it.
        if (f.Target == KeyTarget.Owner)
        {
            return Let(KeyAction.Hide);
        }

        // Enter opens the file in its default app even when the browser
        // holds the keyboard. The armed find bar means "next match", and a
        // click in the PDF toolbar means "go to this page".
        if (!bare || f.FindBarArmed)
        {
            return Pass;
        }

        return f.EnterYield ? Let(KeyAction.YieldEnter) : Claim(KeyAction.OpenCurrent);
    }

    private static KeyDecision RouteNavigation(in KeyFacts f, bool bare)
    {
        // The picker owns its arrows, Home, End, Page and Delete. A quiet
        // refresh follows the native move.
        if (f.Source == SelectionSource.CommonFileDialog)
        {
            return f.Target == KeyTarget.Owner && f.PickerListFocus
                ? Let(KeyAction.FollowPicker)
                : Pass;
        }

        if (IsArrow(f.Vk) && bare)
        {
            if (f.GridOpen)
            {
                return Claim(KeyAction.GridMove);
            }

            var vertical = f.Vk is NativeMethods.VK_UP or NativeMethods.VK_DOWN;
            if (f.Target == KeyTarget.Preview)
            {
                // Left and right always browse. Up and down yield to content
                // the user clicked into: a page scrolls, a player sets volume.
                var contentKeepsArrow = f.TextFocus
                    || (vertical && f.ContentEngaged && f.ContentOwnsKeyboard);
                if (f.Browsable && !contentKeepsArrow)
                {
                    return Claim(KeyAction.Browse);
                }

                if (vertical && f.Media && f.ContentEngaged)
                {
                    return Claim(KeyAction.NudgeMedia);
                }
            }
            else if (!vertical)
            {
                // Explorer's own left/right depends on the view mode, so the
                // glance steps through the view itself.
                return Claim(KeyAction.Browse);
            }
        }

        // A key the preview declines stays with its content. In Explorer
        // the native move happens and the glance follows it.
        return f.Target == KeyTarget.Preview ? Pass : Let(KeyAction.FollowExplorer);
    }

    private static KeyDecision RouteSpace(in KeyFacts f, bool ours)
    {
        // Space in another window that offers a selection moves the glance
        // there.
        if (!ours)
        {
            return f.SpaceRepeat ? Pass : OpenFromForeground(f);
        }

        // A picker's name box or buttons keep their Space.
        if (f.Source == SelectionSource.CommonFileDialog
            && f.Target == KeyTarget.Owner
            && !f.PickerListFocus)
        {
            return Pass;
        }

        // Holding Space never machine-guns a toggle. Its repeats stay away
        // from Explorer and a player; a find box keeps its spaces.
        if (f.SpaceRepeat)
        {
            return f.Target == KeyTarget.Owner || f.Media ? Claim(KeyAction.None) : Pass;
        }

        // Space ends the glance even after a click into the content. A
        // player keeps it as play/pause, an armed find bar as a space.
        if (f.Target == KeyTarget.Preview)
        {
            if (f.Media)
            {
                return Claim(KeyAction.ToggleMedia);
            }

            if (f.FindBarArmed)
            {
                return Pass;
            }
        }

        return Claim(KeyAction.Hide);
    }

    private static KeyDecision OpenFromForeground(in KeyFacts f)
    {
        if (f.Context is not { } context)
        {
            return Pass;
        }

        // The hook never waits for UI Automation and never swallows Space on
        // an unverified picker selection: it passes and remembers the press.
        if (context.IsFileDialog)
        {
            return f.PickerSelection is not null ? Claim(KeyAction.OpenPicker) : Let(KeyAction.DeferPickerOpen);
        }

        return Claim(KeyAction.Open);
    }

    /// <summary>
    /// A navigation key that edits Explorer's selected set, so the glance
    /// carries the set in view order. Shift grows or shrinks a range
    /// (Ctrl+Shift too); Ctrl alone only moves focus.
    /// </summary>
    internal static bool EditsSelection(in KeyFacts f) =>
        f.Shift && !f.Alt && !f.Win && f.Vk != NativeMethods.VK_DELETE;

    private static bool NoModifiers(in KeyFacts f) => !f.Shift && !f.Ctrl && !f.Alt && !f.Win;

    private static KeyDecision Claim(KeyAction action) => new(true, action);

    private static KeyDecision Let(KeyAction action) => new(false, action);
}
