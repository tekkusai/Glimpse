namespace SpacePreview.Services;

/// <summary>
/// What the hook thread reads at a key: the key, the modifiers and the
/// window in front. All of it comes from Windows and none from the UI
/// thread, so it is true at the press however busy the UI thread is.
/// </summary>
internal readonly record struct KeyPress
{
    public int Vk { get; init; }
    public bool Shift { get; init; }
    public bool Ctrl { get; init; }
    public bool Alt { get; init; }
    public bool Win { get; init; }

    /// <summary>A held Space whose first press the keymap already acted on.</summary>
    public bool SpaceRepeat { get; init; }

    /// <summary>The top-level window in front: where the key is going.</summary>
    public IntPtr Root { get; init; }

    /// <summary>That window is Progman or WorkerW.</summary>
    public bool RootIsDesktop { get; init; }

    /// <summary>Its thread has a text caret: a rename or the address bar.</summary>
    public bool RootTextEditing { get; init; }

    /// <summary>It is a file picker whose file list holds focus.</summary>
    public bool RootFileListFocus { get; init; }

    /// <summary>Focus sits in a child window of it: a preview's browser view.</summary>
    public bool RootChildFocus { get; init; }

    /// <summary>What the window in front offers to open. Only a fresh bare Space asks.</summary>
    public ForegroundSelectionContext? Context { get; init; }

    public bool PickerCacheReady { get; init; }
}

/// <summary>One preview window as the keymap reads it.</summary>
internal readonly record struct WindowKeyState
{
    public IntPtr Hwnd { get; init; }
    public bool Browsable { get; init; }
    public bool FillSign { get; init; }
    public bool TextFocus { get; init; }
    public bool FindBarArmed { get; init; }
    public bool GridOpen { get; init; }
    public bool EscapeLayer { get; init; }
    public bool HeaderZoom { get; init; }
    public bool CanRotate { get; init; }
    public bool ContentEngaged { get; init; }
    public bool MediaPlayable { get; init; }

    /// <summary>A click in the PDF viewer's toolbar gave the next Enter to it.</summary>
    public bool EnterYield { get; init; }
}

/// <summary>
/// What the keymap reads that only the UI thread knows. The dispatcher
/// publishes a new one whenever any of it changes and the hook thread
/// decides each key from the newest, so the hook thread never asks the UI
/// thread anything. Nothing in one changes after it is published.
/// </summary>
internal sealed record KeyState
{
    public static readonly KeyState Idle = new();

    /// <summary>The newest key the dispatcher has finished acting on.</summary>
    public long AckedKey { get; init; }

    /// <summary>
    /// Moves when a held Space must not carry over: the hook was out, or a
    /// fullscreen app took the keyboard.
    /// </summary>
    public int SpaceKeyEpoch { get; init; }

    public bool PreviewVisible { get; init; }
    public bool OpenPending { get; init; }
    public SelectionSource Source { get; init; }
    public bool MultiSelection { get; init; }
    public IntPtr OwnerRoot { get; init; }

    /// <summary>The transient preview; all default while none is visible.</summary>
    public WindowKeyState Preview { get; init; }

    public WindowKeyState[] Pinned { get; init; } = [];
}
