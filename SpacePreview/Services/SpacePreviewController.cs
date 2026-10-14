using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal sealed class SpacePreviewController : IDisposable
{
    private readonly record struct SelectionRefreshRequest(
        long Generation,
        bool IncludeMultiSelectionPaths,
        long InputStamp);

    private readonly record struct SelectionReleaseVerification(
        SelectionRefreshRequest Request,
        long StartedGenerationAtRelease);

    /// <summary>A key the hook thread has answered, on its way to the dispatcher.</summary>
    private readonly record struct RoutedKey(
        long Number,
        KeyPress Press,
        KeyDecision Decision,
        long Stamp,
        long OtherKeyDowns);

    private const int MaxPinnedPreviews = 6;
    private const int MaxObservedAccessDialogs = 64;
    private const int DeferredFileDialogPreviewLifetimeMilliseconds = 1200;
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(45);

    // A foreground flicker (a toast, a menu opening) must not end a glance.
    private static readonly TimeSpan OwnerLossDebounce = TimeSpan.FromMilliseconds(100);

    // A game can go full screen in its own window, and no foreground event
    // says so. While a pin is open, the window in front is looked at again.
    private static readonly TimeSpan FullscreenRecheckInterval = TimeSpan.FromMilliseconds(500);

    // A moment after a preview shows, the Office files beside it in the
    // folder convert in the background, so arrowing onto a big deck shows it
    // whole. 2.0 did this; it ends with the glance.
    private const int OfficeNeighborLimit = 4;
    private static readonly TimeSpan OfficeNeighborDelay = TimeSpan.FromMilliseconds(900);

    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _keyboardHook;
    private readonly MouseHook _mouseHook;
    private readonly FullscreenInputSuspension _fullscreenInputSuspension;
    private readonly CommonFileDialogSelectionMonitor _commonFileDialogMonitor;
    private readonly ExplorerHeaderFocus _headerFocus = new();
    private readonly NativeMethods.WinEventProc _foregroundChanged;
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly DispatcherTimer _ownerLossTimer;
    private readonly DispatcherTimer _fullscreenRecheckTimer;

    internal delegate Task<ExplorerSelection?> SelectionLookup(
        string origin,
        SelectionSource source,
        IntPtr hwnd,
        bool includeMultiSelectionPaths,
        bool useForegroundWhenHwndMissing = false);

    // Tests stand in for the window in front, for the shell's selection
    // and its browse, for what the hook thread reads at a key, for the
    // rights of another app and for the preview window going up.
    private readonly Func<IntPtr> _foregroundRoot = NativeMethods.GetForegroundRoot;
    private readonly SelectionLookup _lookUpSelection = RunSelectionLookupAsync;
    private readonly Func<int, bool, KeyPress> _readKey;
    private readonly Action<ExplorerSelection, long> _showPreview;
    private readonly Func<IntPtr, bool, int, Task<ExplorerSelection?>> _browseRelative =
        (hwnd, isDesktop, delta) => StaTask.Run(() => ExplorerSelectionService.TryBrowseRelative(hwnd, isDesktop, delta));
    private readonly Func<IntPtr, PreviewAccessWarningInfo?> _readAccessWarning =
        window => ProcessIntegrityService.TryGetPreviewAccessWarning(window, out var warning) ? warning : null;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private readonly List<PreviewWindow> _pinnedWindows = [];
    private IntPtr _foregroundHook;
    private IntPtr[] _mouseActivationWindows = [];
    private PreviewWindow? _previewWindow;
    private PreviewWindow? _closingWindow;
    private IntPtr _lastExplorerHwnd;
    private IntPtr _previewOwnerRootHwnd;
    private SelectionSource _lastSelectionSource = SelectionSource.Explorer;
    private volatile bool _previewVisible;
    private ExplorerSelection? _officeNeighborSelection;
    private CancellationTokenSource? _officeNeighbors;

    // The hook thread and the dispatcher share no lock. The dispatcher
    // publishes _keyState whole and the hook thread reads it; the hook
    // thread posts each key and counts the others in _otherKeyDowns.
    private KeyState? _keyState = KeyState.Idle;
    private long _otherKeyDowns;

    // Hook thread only. _keysAhead holds the keys this thread answered that
    // change what the next key means, until the dispatcher has acted on
    // them; _downPosted marks the keys whose press went to the dispatcher.
    private readonly SpaceKeyLatch _spaceKey = new(SpaceKeyLatch.RepeatWindowFor(SystemParameters.KeyboardDelay));
    private readonly List<(long Number, IntPtr Root, KeyAction Action)> _keysAhead = [];
    private readonly bool[] _downPosted = new bool[256];
    private int _spaceKeyEpochSeen;
    private long _keyNumber;

    // Dispatcher only.
    private readonly HashSet<Type> _reportedKeyStateFailures = [];
    private bool _keyboardHookWanted;
    private bool _keyStateWatched;
    private int _spaceKeyEpoch;
    private long _ackedKey;
    private bool _refreshInFlight;
    private SelectionRefreshRequest? _queuedRefreshRequest;
    private readonly long[] _navigationKeyRefreshTokens = new long[256];
    private long _navigationRefreshGeneration;
    private long _lastStartedRefreshGeneration;
    private long _lastChangedRefreshGeneration;
    private SelectionReleaseVerification? _pendingKeyUpVerification;
    private bool _browseInFlight;
    private int _browsePendingDelta;
    private long _browseInputStamp;

    // The last key or click that moved a picker's selection; its settled
    // read shows the new file.
    private long _pickerInputStamp;
    private IReadOnlyList<string>? _multiSelection;
    private int _multiSelectionSelectedCount;
    private int _multiIndex;
    private int _openRequestGeneration;
    private bool _openPending;

    // Moves with every file a glance is told to show, every word from
    // Explorer on what is selected now, and every glance that ends. A file
    // whose copy is still on its way is shown when it arrives only if
    // this has not moved since.
    private int _arrival;
    private readonly DeferredFileDialogPreviewIntentSlot _pendingFileDialogPreview = new();
    private readonly ConcurrentDictionary<long, byte> _elevationChecksInFlight = new();
    private readonly HashSet<(long Hwnd, uint ProcessId)> _observedAccessDialogs = [];
    private int _idleTrimInFlight;
    private bool _disposed;

    public event Action<PreviewAccessWarningInfo>? PreviewAccessWarningDetected;

    /// <summary>No glance on screen and no pinned window: nothing of Glimpse the user could be in.</summary>
    internal bool NoPreviewOpen =>
        !_disposed
        && !_previewVisible
        && _pinnedWindows.Count == 0;

    internal bool InputSuspendedForFullscreen => _fullscreenInputSuspension.IsSuspended;

    public SpacePreviewController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _readKey = ReadKey;
        _showPreview = ShowPreview;
        _keyboardHook = new KeyboardHook(OnKeyDown, OnKeyUp);
        _mouseHook = new MouseHook(QueueMouseDown, IsMouseActivationWindow);
        _fullscreenInputSuspension = new FullscreenInputSuspension();
        _fullscreenInputSuspension.SuspensionChanged += OnFullscreenSuspensionChanged;
        _commonFileDialogMonitor = new CommonFileDialogSelectionMonitor(dispatcher);
        _commonFileDialogMonitor.SelectionChanged += OnCommonFileDialogSelectionChanged;
        _commonFileDialogMonitor.RefreshCompleted += OnCommonFileDialogRefreshCompleted;
        _commonFileDialogMonitor.DialogObserved += OnCommonFileDialogObserved;
        _commonFileDialogMonitor.DialogBuilt += OnCommonFileDialogBuilt;
        _foregroundChanged = OnForegroundChanged;
        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = IdleTrimDelay
        };
        _idleTrimTimer.Tick += (_, _) => RunIdleTrim();
        _ownerLossTimer = new DispatcherTimer(DispatcherPriority.Input, dispatcher)
        {
            Interval = OwnerLossDebounce
        };
        _ownerLossTimer.Tick += (_, _) => OnOwnerLossTimer();
        _fullscreenRecheckTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = FullscreenRecheckInterval
        };
        _fullscreenRecheckTimer.Tick += (_, _) => RecheckFullscreen();
    }

    public void Start()
    {
        // One foreground watch drives everything that depends on the window
        // in front: the keyboard hook's presence, fullscreen suspension, the
        // picker monitor and the glance's owner rule. WinEvents arrive on
        // this thread.
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundChanged,
            0,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("foreground watch unavailable; keyboard hook stays installed");
        }

        OnForeground(NativeMethods.GetForegroundRoot());
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            return;
        }

        // The event says a window was brought forward, not that it got
        // there: apps raise it for windows that never reach the front, and
        // one of those took the hook out from under Explorer. Windows is
        // asked what is in front. Only when it has no answer is the event
        // taken at its word.
        var root = _foregroundRoot();
        OnForeground(root != IntPtr.Zero ? root : NativeMethods.GetRoot(hwnd));
    }

    private void OnForeground(IntPtr root)
    {
        if (_disposed)
        {
            return;
        }

        var processId = NativeMethods.GetWindowProcessId(root);
        _fullscreenInputSuspension.Observe(root);
        if (processId != _processId)
        {
            _commonFileDialogMonitor.ObserveForeground(root);
        }
        else
        {
            // A window of Glimpse's own is in front, so no dialog is. A
            // picker keeps its watch for the glance over it; a dialog that
            // was no picker is looked at again when it comes back.
            _commonFileDialogMonitor.StopWatchingBuild();
        }

        SyncKeyboardHook(root, processId);
        ApplyOwnerRule(root);
    }

    /// <summary>
    /// A dialog that took the foreground before it was built has its frame
    /// now. No foreground event says so; to Glimpse it is the same moment.
    /// </summary>
    private void OnCommonFileDialogBuilt(IntPtr dialogRoot)
    {
        if (!_disposed && _foregroundRoot() == dialogRoot)
        {
            OnForeground(dialogRoot);
        }
    }

    /// <summary>
    /// The hook stays installed only while a surface Glimpse serves is in
    /// front, so every other app's keys never wait on this process: Explorer,
    /// the desktop, a dialog the picker monitor has confirmed, or Glimpse.
    /// </summary>
    private void SyncKeyboardHook(IntPtr root, uint processId)
    {
        var wanted = _foregroundHook == IntPtr.Zero
            || (root != IntPtr.Zero
                && (processId == _processId
                    || PreviewInputPolicy.IsServedSurfaceClass(NativeMethods.GetClassNameString(root))
                    || _commonFileDialogMonitor.IsWatching(root)));
        if (wanted != _keyboardHookWanted)
        {
            // Keys went unseen while the hook was out: no latch or key-up
            // verification carries across.
            _keyboardHookWanted = wanted;
            _spaceKeyEpoch++;
            if (!wanted)
            {
                ClearNavigationRefreshTokens();
                ClearPendingFileDialogPreview();
            }
        }

        // Published before the hook thread can see a key. The hook is asked
        // for on every served window, not only the first: the install runs
        // on the hook thread, and one Windows refused is tried again by the
        // next ask. One Windows dropped comes back as it always did: the
        // next switch away takes the dead handle out, the switch back
        // installs a new one.
        SyncKeyState();
        _headerFocus.Watch(
            wanted && PreviewInputPolicy.IsExplorerWindowClass(NativeMethods.GetClassNameString(root))
                ? root
                : IntPtr.Zero);
        if (wanted)
        {
            _keyboardHook.Start();
        }
        else
        {
            _keyboardHook.Stop();
        }
    }

    private void OnFullscreenSuspensionChanged(bool suspended)
    {
        if (suspended)
        {
            _spaceKeyEpoch++;
            ClearNavigationRefreshTokens();
            ClearPendingFileDialogPreview();
        }

        foreach (var pinned in _pinnedWindows)
        {
            pinned.SetTopmostSuppressed(suspended);
        }

        if (suspended && _previewVisible)
        {
            DiagnosticLog.Write("fullscreen -> hide");
            RequestHide();
        }

        SyncHooks();
    }

    // Same window, new shape. No window in front (a lock, a switch between
    // windows) says nothing about the last one.
    private void RecheckFullscreen()
    {
        var root = _foregroundRoot();
        if (!_disposed && root != IntPtr.Zero)
        {
            _fullscreenInputSuspension.Observe(root);
        }
    }

    // A glance belongs to the window it came from. When the foreground goes
    // anywhere else (Alt+Tab, Win+D, the owner closing) the glance ends.
    private void ApplyOwnerRule(IntPtr root)
    {
        if (BelongsToGlance(root))
        {
            _ownerLossTimer.Stop();
            return;
        }

        if (!_ownerLossTimer.IsEnabled)
        {
            _ownerLossTimer.Start();
        }
    }

    private void OnOwnerLossTimer()
    {
        _ownerLossTimer.Stop();
        var root = NativeMethods.GetForegroundRoot();
        if (root == IntPtr.Zero
            || BelongsToGlance(root)
            || _previewWindow?.IsOutsideDismissSuppressed == true)
        {
            return;
        }

        DiagnosticLog.Write($"foreground left the glance -> hide class={NativeMethods.GetClassNameString(root)}");
        RequestHide();
    }

    /// <summary>
    /// True when nothing needs ending: no glance (the welcome counts as
    /// none), or the foreground is any Glimpse window, the glance's owner,
    /// or a window the preview opened in another process (the browser's
    /// print dialog or file chooser). Glimpse's own windows count whoever
    /// owns them: a file dialog shown without an owner still belongs. The
    /// share sheet is covered by the outside-dismiss grace the window sets
    /// when it opens it.
    /// </summary>
    private bool BelongsToGlance(IntPtr root)
    {
        if (!_previewVisible || _previewWindow is not { CurrentPath: not null } window)
        {
            return true;
        }

        return NativeMethods.GetWindowProcessId(root) == _processId
            || TargetOf(root, pinnedForeground: false) != KeyTarget.Other
            || window.OwnsForegroundRoot(root)
            || window.IsWebViewWindow(root);
    }

    private KeyTarget TargetOf(IntPtr root, bool pinnedForeground)
    {
        var ownerIsDesktop = _lastSelectionSource == SelectionSource.Desktop;
        return PreviewKeyMap.Classify(
            root,
            pinnedForeground,
            _previewVisible,
            _previewWindow?.Hwnd ?? IntPtr.Zero,
            _previewOwnerRootHwnd,
            ownerIsDesktop,
            ownerIsDesktop
                && root != _previewOwnerRootHwnd
                && PreviewInputPolicy.IsDesktopWindowClass(NativeMethods.GetClassNameString(root)));
    }

    /// <summary>What is on screen changed: both hook threads get the new picture.</summary>
    private void SyncHooks()
    {
        var windows = new List<IntPtr>(_pinnedWindows.Count + 1);
        if (_previewVisible && _previewWindow?.Hwnd is { } activeHwnd && activeHwnd != IntPtr.Zero)
        {
            windows.Add(activeHwnd);
        }

        foreach (var pinned in _pinnedWindows)
        {
            if (pinned.Hwnd != IntPtr.Zero)
            {
                windows.Add(pinned.Hwnd);
            }
        }

        Volatile.Write(ref _mouseActivationWindows, windows.ToArray());

        // A glance goes with a foreground change; a pin stays through one.
        // With no pin open, nothing runs.
        _fullscreenRecheckTimer.IsEnabled = _pinnedWindows.Count > 0;
        if (MouseHook.ShouldRun(_fullscreenInputSuspension.IsSuspended, _previewVisible, _pinnedWindows.Count))
        {
            _mouseHook.Start();
        }
        else
        {
            _mouseHook.Stop();
        }

        SyncKeyState();
    }

    /// <summary>
    /// Publishes what the keyboard hook thread decides from, and keeps it
    /// current. The controller publishes at each change it makes. A preview
    /// also changes without telling it (sign mode, a find bar, a text box
    /// taking focus), so while the hook is in and a preview is up, the state
    /// goes out again each time the dispatcher runs out of work.
    /// </summary>
    private void SyncKeyState()
    {
        var watch = !_disposed
            && _keyboardHookWanted
            && (_previewVisible || _pinnedWindows.Count > 0);
        if (watch != _keyStateWatched)
        {
            _keyStateWatched = watch;
            if (watch)
            {
                _dispatcher.Hooks.DispatcherInactive += OnDispatcherInactive;
            }
            else
            {
                _dispatcher.Hooks.DispatcherInactive -= OnDispatcherInactive;
            }
        }

        PublishKeyState();
    }

    private void OnDispatcherInactive(object? sender, EventArgs e) => PublishKeyState();

    private void PublishKeyState() => Volatile.Write(ref _keyState, CaptureKeyState());

    /// <summary>
    /// The state the keymap reads, as it is now. Null when a window could not
    /// be read; the hook thread then lets every key through.
    /// </summary>
    private KeyState? CaptureKeyState()
    {
        try
        {
            var pinned = new WindowKeyState[_pinnedWindows.Count];
            for (var i = 0; i < pinned.Length; i++)
            {
                pinned[i] = ReadWindowKeyState(_pinnedWindows[i]);
            }

            return new KeyState
            {
                AckedKey = _ackedKey,
                SpaceKeyEpoch = _spaceKeyEpoch,
                PreviewVisible = _previewVisible,
                OpenPending = _openPending,
                Source = _lastSelectionSource,
                MultiSelection = _multiSelection is { Count: > 1 },
                OwnerRoot = _previewOwnerRootHwnd,
                Preview = _previewVisible && _previewWindow is { } glance ? ReadWindowKeyState(glance) : default,
                Pinned = pinned
            };
        }
        catch (Exception ex)
        {
            // A window mid-teardown can refuse a read. Fail open rather than
            // decide keys on a guess, and say so once per kind.
            if (_reportedKeyStateFailures.Add(ex.GetType()))
            {
                DiagnosticLog.Write($"keyboard state read failed: {ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }

    private static WindowKeyState ReadWindowKeyState(PreviewWindow window) => new()
    {
        Hwnd = window.Hwnd,
        Browsable = window.CurrentPath is not null,
        FillSign = window.IsFillSignModeActive,
        TextFocus = window.HasTextFocus,
        FindBarArmed = window.IsEscapeYieldArmed,
        TakesFind = window.TakesFindShortcut,
        GridOpen = window.IsGridViewOpen,
        EscapeLayer = window.IsGridViewOpen || window.IsSlideshowActive,
        HeaderZoom = window.HasHeaderZoom,
        CanRotate = window.CanRotateFromKeyboard,
        ContentEngaged = window.UserEngagedContent,
        MediaPlayable = window.IsMediaPlayable,
        EnterYield = window.EnterYieldsToContent
    };

    // Read on the mouse observer thread.
    private bool IsMouseActivationWindow(IntPtr hwnd)
    {
        foreach (var candidate in Volatile.Read(ref _mouseActivationWindows))
        {
            if (candidate == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Keyboard hook thread: every key on the PC waits here while the hook is
    /// in. It reads the keyboard and the window in front, asks the keymap
    /// with the state the dispatcher published, and answers Windows. It
    /// touches no window and never waits for the dispatcher; what the key
    /// means is settled there, in <see cref="OnKeyRouted"/>.
    /// </summary>
    private bool OnKeyDown(int vk, uint time, long stamp)
    {
        if (vk != NativeMethods.VK_SPACE)
        {
            // Any other key ends a picker Space that still waits for its
            // selection. The count covers one the dispatcher has yet to arm.
            Volatile.Write(ref _otherKeyDowns, _otherKeyDowns + 1);
            ClearPendingFileDialogPreview();
        }

        if (!PreviewKeyMap.IsMapped(vk))
        {
            // It reaches the app, and may move the focus in Explorer's
            // header. A bare Tab or F6 walks it there.
            _headerFocus.KeyWentBy(
                vk is NativeMethods.VK_TAB or NativeMethods.VK_F6
                && !NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
                && !NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
                && !NativeMethods.IsKeyDown(NativeMethods.VK_LWIN)
                && !NativeMethods.IsKeyDown(NativeMethods.VK_RWIN));
            return false;
        }

        var space = vk == NativeMethods.VK_SPACE;
        if (Volatile.Read(ref _keyState) is not { } state)
        {
            // The dispatcher could not read its windows: fail open.
            if (space)
            {
                _spaceKey.Press(time, acted: false);
            }

            _headerFocus.KeyWentBy(walks: false);
            return false;
        }

        if (state.SpaceKeyEpoch != _spaceKeyEpochSeen)
        {
            _spaceKeyEpochSeen = state.SpaceKeyEpoch;
            _spaceKey.Reset();
        }

        var press = _readKey(vk, space && _spaceKey.IsRepeat(time));
        if (press.Context is { IsFileDialog: true } context && press.PickerSelection is null)
        {
            press = press with { PickerRefresh = _commonFileDialogMonitor.LatestRefresh(context.Hwnd) };
        }

        // The dispatcher has yet to act on keys this thread already
        // answered, and the published state does not show them. What they
        // are certain to change counts from here: the arrow after the key
        // that ended the glance is Explorer's, and the Esc after the one
        // that closed a find bar ends the glance.
        if (_keysAhead.Count > 0)
        {
            state = Ahead(state);
        }

        var facts = PreviewKeyMap.Facts(press, state);
        var decision = PreviewKeyMap.Route(facts);
        if (space)
        {
            _spaceKey.Press(time, acted: decision.Swallow || decision.Action != KeyAction.None);
        }

        // A key Glimpse takes never reaches the header. Any other may
        // move the focus there.
        if (!decision.Swallow)
        {
            _headerFocus.KeyWentBy(walks: false);
        }

        // A key typed into a text box is the text box's and means nothing
        // to a preview, whatever the dispatcher knows by its turn, so
        // nothing is posted for it. The one that does mean something, the
        // Enter that ends the owner's glance, carries an action and goes
        // on. What this thread keeps for itself is done by here: the Space
        // latch above, and at the top the end of a picker Space that still
        // waited.
        if (facts.Typing && decision.Action == KeyAction.None)
        {
            _downPosted[vk] = false;
            return false;
        }

        var number = ++_keyNumber;
        if (!ReferenceEquals(PreviewKeyMap.After(state, press.Root, decision.Action), state))
        {
            _keysAhead.Add((number, press.Root, decision.Action));
        }

        // Ahead of everything queued, in key order: the same place the key
        // had when the hook ran on the dispatcher.
        var routed = new RoutedKey(number, press, decision, stamp, _otherKeyDowns);
        _downPosted[vk] = true;
        _dispatcher.BeginInvoke(() => OnKeyRouted(routed), DispatcherPriority.Send);
        return decision.Swallow;
    }

    /// <summary>
    /// Keyboard hook thread. The published state with this thread's own
    /// keys counted in, for as long as the dispatcher has not answered them.
    /// </summary>
    private KeyState Ahead(KeyState state)
    {
        var kept = 0;
        for (var i = 0; i < _keysAhead.Count; i++)
        {
            var key = _keysAhead[i];
            if (key.Number <= state.AckedKey)
            {
                continue;
            }

            _keysAhead[kept++] = key;
            state = PreviewKeyMap.After(state, key.Root, key.Action);
        }

        _keysAhead.RemoveRange(kept, _keysAhead.Count - kept);
        return state;
    }

    /// <summary>
    /// Keyboard hook thread. Only reads of what Windows itself holds: the
    /// modifier keys and the window in front, with its focus.
    /// </summary>
    private KeyPress ReadKey(int vk, bool spaceRepeat)
    {
        var root = NativeMethods.GetForegroundRoot();
        var space = vk == NativeMethods.VK_SPACE;
        var arrow = PreviewKeyMap.IsArrow(vk);
        var typing = ExplorerSelectionService.IsTyping(root, out var header);
        var press = new KeyPress
        {
            Vk = vk,
            Shift = NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT),
            Ctrl = NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL),
            Alt = NativeMethods.IsKeyDown(NativeMethods.VK_MENU),
            Win = NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN),
            SpaceRepeat = spaceRepeat,
            Root = root,
            RootIsDesktop = PreviewInputPolicy.IsDesktopWindowClass(NativeMethods.GetClassNameString(root)),
            // The header is typing until it was read as taking no text.
            RootTyping = typing && !(header && _headerFocus.IsPlain(root)),
            RootHeaderFocus = header,
            RootFileListFocus = (space || PreviewKeyMap.IsNavigation(vk))
                && CommonFileDialogSelectionService.HasFileListFocus(root),
            RootChildFocus = (arrow || vk == NativeMethods.VK_RETURN) && HasChildFocus(root)
        };

        // Only a fresh Space that could open something asks what is in front.
        if (!space || spaceRepeat || press.RootTyping || press.Shift || press.Ctrl || press.Alt || press.Win)
        {
            return press;
        }

        var context = ExplorerSelectionService.TryGetForegroundContext();
        return press with
        {
            Context = context,
            PickerSelection = context is { IsFileDialog: true }
                && _commonFileDialogMonitor.TryGetCachedSelection(context.Hwnd, out var cached)
                    ? cached
                    : null
        };
    }

    /// <summary>
    /// Focus sits in a child window of <paramref name="root"/>: for a
    /// preview, its browser view and not the WPF chrome. WPF cannot answer
    /// this for a windowed WebView2, so Win32 is asked.
    /// </summary>
    private static bool HasChildFocus(IntPtr root)
    {
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        return root != IntPtr.Zero
            && NativeMethods.GetGUIThreadInfo(0, ref info)
            && info.hwndFocus != IntPtr.Zero
            && info.hwndFocus != root
            && NativeMethods.GetAncestor(info.hwndFocus, NativeMethods.GA_ROOT) == root;
    }

    /// <summary>
    /// The dispatcher's half of a key, in key order. The hook thread has
    /// told Windows whether the key is swallowed, from state that can be a
    /// turn old. The keymap is asked again with the state as it is now, so
    /// a key means what it meant when the hook ran here, and
    /// <see cref="PreviewKeyMap.Settle"/> covers the keys the two disagree on.
    /// </summary>
    private void OnKeyRouted(RoutedKey key)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // A new key-down supersedes any earlier native refresh for this
            // physical key; only a down that reaches Explorer installs a
            // fresh token for key-up verification.
            var vk = key.Press.Vk;
            _navigationKeyRefreshTokens[vk] = 0;
            if (CaptureKeyState() is not { } state)
            {
                return;
            }

            var facts = PreviewKeyMap.Facts(key.Press, state);
            var now = PreviewKeyMap.Route(facts);
            var action = PreviewKeyMap.Settle(key.Decision, now);
            if (now.Swallow != key.Decision.Swallow)
            {
                DiagnosticLog.Write(
                    $"key vk=0x{vk:X} {(key.Decision.Swallow ? "swallowed" : "passed")} on stale state hook={key.Decision.Action} now={now.Action} -> {action}");
            }

            Perform(action, facts, FindPinnedWindow(key.Press.Root) ?? (_previewVisible ? _previewWindow : null), key.Stamp);

            // A key typed after this Space found no intent to cancel yet.
            if (action == KeyAction.DeferPickerOpen && Volatile.Read(ref _otherKeyDowns) != key.OtherKeyDowns)
            {
                ClearPendingFileDialogPreview();
            }
        }
        finally
        {
            // Told to the hook thread whatever happened: it holds the glance
            // for ended until it hears about the key that ended it.
            _ackedKey = Math.Max(_ackedKey, key.Number);
            PublishKeyState();
        }
    }

    /// <summary>Dispatcher. Runs what a key means; the swallow is long done.</summary>
    private void Perform(KeyAction action, in KeyFacts facts, PreviewWindow? window, long stamp)
    {
        var vk = facts.Vk;
        switch (action)
        {
            case KeyAction.None:
                return;
            case KeyAction.Open:
                QueueOpen(facts.Context!, stamp);
                return;
            case KeyAction.OpenPicker:
                QueuePickerOpen(facts.Context!, facts.PickerSelection!, stamp);
                return;
            case KeyAction.DeferPickerOpen:
                DeferPickerOpen(facts.Context!, stamp, facts.PickerRefresh);
                return;
            case KeyAction.CancelOpen:
                _openRequestGeneration++;
                _openPending = false;
                BorrowedFiles.StopWaiting();
                DiagnosticLog.Write("escape -> open cancelled");
                return;
            case KeyAction.Hide:
                DiagnosticLog.Write(vk switch
                {
                    NativeMethods.VK_ESCAPE => "escape -> hide",
                    NativeMethods.VK_RETURN => "enter -> hide, explorer opens",
                    _ => "space -> hide"
                });
                RequestHide();
                return;
            case KeyAction.HideForPickerCommit:
                DiagnosticLog.Write("enter -> hide, file dialog confirms");
                RequestHide();
                return;
            case KeyAction.ReturnToPicker:
                DiagnosticLog.Write("enter -> return to file dialog");
                HidePreviewAndRestoreOwner(_previewOwnerRootHwnd);
                return;
            case KeyAction.OpenCurrent:
                window?.ConsumeEnterYieldToContent();
                DiagnosticLog.Write("enter -> open current");
                window?.OpenCurrent();
                return;
            case KeyAction.YieldEnter:
                window?.ConsumeEnterYieldToContent();
                return;
            case KeyAction.YieldEscape:
                window?.ConsumeEscapeYield();
                DiagnosticLog.Write("escape -> content find UI");
                return;
            case KeyAction.NoteFind:
                window?.NoteFindShortcut();
                return;
            case KeyAction.ToggleMedia:
                window?.TryToggleMediaPlayback();
                return;
            case KeyAction.NudgeMedia:
                window?.TryNudgeMediaFromArrow(vk);
                return;
            case KeyAction.Browse:
                BrowseRelativeAsync(vk is NativeMethods.VK_LEFT or NativeMethods.VK_UP ? -1 : 1, stamp);
                return;
            case KeyAction.GridMove:
                window?.MoveGridCursor(
                    vk switch { NativeMethods.VK_LEFT => -1, NativeMethods.VK_RIGHT => 1, _ => 0 },
                    vk switch { NativeMethods.VK_UP => -1, NativeMethods.VK_DOWN => 1, _ => 0 });
                return;
            case KeyAction.GridToggle:
                window?.ToggleGridView();
                return;
            case KeyAction.GridCommit:
                window?.CommitGridCursor();
                return;
            case KeyAction.EscapeLayer:
                DiagnosticLog.Write("escape -> selection surface");
                window?.TryConsumeEscapeLayer();
                return;
            case KeyAction.FillSignEscape:
                DiagnosticLog.Write("escape -> fill-sign layer");
                window?.RequestFillSignEscape();
                return;
            case KeyAction.ClosePinned:
                DiagnosticLog.Write("escape -> close focused pinned preview");
                if (window is not null)
                {
                    OnPinnedCloseRequested(window);
                }

                return;
            case KeyAction.Zoom:
                window?.TryAdjustZoomFromShortcut(vk);
                return;
            case KeyAction.FillScreen:
                window?.ToggleFillScreen();
                return;
            case KeyAction.Rotate:
                window?.RotateFromKeyboard(facts.Shift ? -1 : 1);
                return;
            case KeyAction.FollowExplorer:
                FollowExplorer(vk, PreviewKeyMap.EditsSelection(facts), stamp);
                return;
            case KeyAction.FollowPicker:
                _pickerInputStamp = stamp;
                _commonFileDialogMonitor.RequestRefresh(_previewOwnerRootHwnd, settleMilliseconds: 55);
                return;
        }
    }

    // Explorer handles the key; the glance follows on key-down so holding an
    // arrow scrubs live instead of waiting for release.
    private void FollowExplorer(int vk, bool editsSelection, long stamp)
    {
        var generation = ++_navigationRefreshGeneration;
        _navigationKeyRefreshTokens[vk] = editsSelection ? -generation : generation;
        var request = new SelectionRefreshRequest(generation, editsSelection, stamp);
        _dispatcher.BeginInvoke(
            () => RequestSelectionRefresh(request),
            DispatcherPriority.Input);
    }

    /// <summary>Keyboard hook thread. A key-up is never swallowed.</summary>
    private void OnKeyUp(int vk)
    {
        if (vk == NativeMethods.VK_SPACE)
        {
            _spaceKey.Release();
            return;
        }

        // Only a navigation key can owe the glance a look at what its
        // release committed, and only one whose press went to the
        // dispatcher: a key typed into a text box left nothing to look at.
        if (PreviewKeyMap.IsNavigation(vk) && _downPosted[vk])
        {
            _downPosted[vk] = false;
            var root = NativeMethods.GetForegroundRoot();
            _dispatcher.BeginInvoke(() => OnNavigationKeyUp(vk, root), DispatcherPriority.Send);
        }
    }

    private void OnNavigationKeyUp(int vk, IntPtr root)
    {
        var refreshToken = _navigationKeyRefreshTokens[vk];
        _navigationKeyRefreshTokens[vk] = 0;
        if (_disposed || !_previewVisible || refreshToken == 0)
        {
            return;
        }

        if (TargetOf(root, FindPinnedWindow(root) is not null) is not (KeyTarget.Preview or KeyTarget.Owner))
        {
            return;
        }

        // Most Explorer views commit on key-down. Keep the release
        // verification only for the views that really did not move until the
        // key came up.
        var request = new SelectionRefreshRequest(
            Math.Abs(refreshToken),
            IncludeMultiSelectionPaths: refreshToken < 0,
            InputStamp: 0);
        var verification = new SelectionReleaseVerification(
            request,
            StartedGenerationAtRelease: _lastStartedRefreshGeneration);
        _dispatcher.BeginInvoke(
            () => RequestKeyUpSelectionVerification(verification),
            DispatcherPriority.Input);
    }

    private void QueueMouseDown(int message, int screenX, int screenY)
    {
        var stamp = Stopwatch.GetTimestamp();
        _dispatcher.BeginInvoke(
            () => OnMouseDown(message, screenX, screenY, stamp),
            DispatcherPriority.Input);
    }

    private void OnMouseDown(int message, int screenX, int screenY, long stamp)
    {
        ClearPendingFileDialogPreview();

        if (_fullscreenInputSuspension.IsSuspended)
        {
            return;
        }

        if (!_previewVisible && _pinnedWindows.Count == 0)
        {
            return;
        }

        var hit = NativeMethods.GetRootWindowAt(screenX, screenY);
        var target = FindOwnedWindowAt(screenX, screenY, hit);
        if (target is not null)
        {
            // The observer thread already took the foreground synchronously
            // while Windows still grants activation for this click. This is a
            // harmless fallback for a window published during the same input.
            var hwnd = target.Hwnd;
            if (hwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() != hwnd)
            {
                NativeMethods.SetForegroundWindow(hwnd);
            }

            // Clicking into the preview focuses it so Ctrl+C and viewer
            // shortcuts reach the content the user is interacting with. The
            // click changes whose keys the arrows and Enter are.
            _dispatcher.BeginInvoke(() =>
            {
                target.ActivateForInteraction(screenX, screenY);
                PublishKeyState();
            });
            return;
        }

        if (FindOwnedPopupAt(screenX, screenY, hit) is not null)
        {
            return;
        }

        // Clicking another file in an upload/open dialog changes the glance;
        // it does not end it. The click reaches the picker unchanged, then a
        // settled read updates the preview from the new native selection.
        if (_previewVisible
            && _lastSelectionSource == SelectionSource.CommonFileDialog
            && hit == _previewOwnerRootHwnd)
        {
            _pickerInputStamp = stamp;
            _commonFileDialogMonitor.RequestRefresh(_previewOwnerRootHwnd, settleMilliseconds: 60);
            return;
        }

        var outsideDismissSuppressed = _previewWindow?.IsOutsideDismissSuppressed == true;
        if (PreviewMouseDismissalPolicy.ShouldHideActivePreview(
            _previewVisible,
            hitOwnedWindow: false,
            hitOwnedPopup: false,
            outsideDismissSuppressed: outsideDismissSuppressed))
        {
            DiagnosticLog.Write($"outside click -> hide x={screenX} y={screenY}");
            RequestHide();
        }
    }

    /// <summary>
    /// Resolves which of our windows (active preview or pinned) actually sits
    /// under the point. The real window under the cursor wins, so overlapping
    /// previews route the click to the topmost one; the rect check is
    /// only a fallback when the hit-test fails outright.
    /// </summary>
    private PreviewWindow? FindOwnedWindowAt(int x, int y, IntPtr hit)
    {
        if (hit != IntPtr.Zero)
        {
            if (_previewVisible && _previewWindow is not null && _previewWindow.Hwnd == hit)
            {
                return _previewWindow;
            }

            return FindPinnedWindow(hit);
        }

        if (_previewVisible && _previewWindow is not null && _previewWindow.ContainsScreenPoint(x, y))
        {
            return _previewWindow;
        }

        foreach (var pinned in _pinnedWindows)
        {
            if (pinned.ContainsScreenPoint(x, y))
            {
                return pinned;
            }
        }

        return null;
    }

    private PreviewWindow? FindOwnedPopupAt(int x, int y, IntPtr hit)
    {
        if (_previewVisible
            && _previewWindow is not null
            && _previewWindow.ContainsOwnedPopupScreenPoint(x, y, hit))
        {
            return _previewWindow;
        }

        foreach (var pinned in _pinnedWindows)
        {
            if (pinned.ContainsOwnedPopupScreenPoint(x, y, hit))
            {
                return pinned;
            }
        }

        return null;
    }

    private PreviewWindow? FindPinnedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        foreach (var pinned in _pinnedWindows)
        {
            if (pinned.Hwnd == hwnd)
            {
                return pinned;
            }
        }

        return null;
    }

    public void ShowWelcome()
    {
        if (_disposed)
        {
            return;
        }

        _previewWindow ??= CreatePreviewWindow();
        CancelIdleTrim();
        ResetSession();
        _previewOwnerRootHwnd = NativeMethods.GetForegroundRoot();
        DiagnosticLog.Write("welcome shown");
        var presented = _previewWindow.ShowWelcome();
        if (!presented)
        {
            DiagnosticLog.Write(
                $"welcome presentation failed reason={_previewWindow.LastPresentationFailureReason} hwnd=0x{_previewWindow.Hwnd.ToInt64():X}");
            HidePreview();
            return;
        }

        _previewVisible = true;
        SyncHooks();
    }

    private PreviewWindow CreatePreviewWindow()
    {
        var window = new PreviewWindow();
        window.DismissRequested += (sender, _) => OnDismissRequested((PreviewWindow)sender!);
        window.PinToggleRequested += (sender, _) => OnPinToggleRequested((PreviewWindow)sender!);
        window.CloseRequested += (sender, _) => OnPinnedCloseRequested((PreviewWindow)sender!);
        window.BrowseRequested += (sender, direction) => OnBrowseRequested((PreviewWindow)sender!, direction);
        window.SelectionItemRequested += (sender, index) => OnSelectionItemRequested((PreviewWindow)sender!, index);
        window.SlideshowAdvanceRequested += (sender, _) => OnSlideshowAdvanceRequested((PreviewWindow)sender!);
        window.ContentReady += (sender, _) => OnPreviewContentReady((PreviewWindow)sender!);
        window.PresentationFailed += OnPreviewPresentationFailed;
        window.Closing += (sender, args) => OnPreviewWindowClosing((PreviewWindow)sender!, args);
        window.Closed += (sender, _) => OnPreviewWindowClosed((PreviewWindow)sender!);
        window.SetPinLimitReached(_pinnedWindows.Count >= MaxPinnedPreviews);
        return window;
    }

    /// <summary>
    /// Alt+F4, the taskbar's Close or another app's WM_CLOSE is the user
    /// leaving, and leaving goes through the same gates as Esc and the X:
    /// a glance hides, a pinned window closes, and signing work asks
    /// first. Only the controller's own closes get through.
    /// </summary>
    private void OnPreviewWindowClosing(PreviewWindow window, CancelEventArgs args)
    {
        if (_disposed || window == _closingWindow)
        {
            return;
        }

        args.Cancel = true;

        // Out of the close's own call stack: retiring a window closes it.
        // By then a window app shutdown closed, or one already retiring, is
        // no longer held and has nothing to ask.
        _dispatcher.BeginInvoke(() =>
        {
            if (_pinnedWindows.Contains(window))
            {
                DiagnosticLog.Write("window close -> close pinned");
                OnPinnedCloseRequested(window);
            }
            else if (window == _previewWindow)
            {
                DiagnosticLog.Write("window close -> dismiss");
                RequestHide();
            }
        });
    }

    /// <summary>
    /// App shutdown closes windows past any cancel. Whatever closed one, the
    /// controller lets go of it: the next Space must never show a dead window.
    /// </summary>
    private void OnPreviewWindowClosed(PreviewWindow window)
    {
        if (_disposed)
        {
            return;
        }

        if (window == _previewWindow)
        {
            _previewWindow = null;
            _previewVisible = false;
            ResetSession();
        }

        if (_pinnedWindows.Remove(window))
        {
            UpdatePinCapacity();
        }

        SyncHooks();
    }

    /// <summary>The controller's own close: the one close the leave gate lets through.</summary>
    private void CloseWindow(PreviewWindow window)
    {
        _closingWindow = window;
        try
        {
            window.Close();
        }
        finally
        {
            _closingWindow = null;
        }
    }

    private void OnPreviewPresentationFailed(PreviewWindow window, string reason)
    {
        if (_disposed || window != _previewWindow || !_previewVisible)
        {
            return;
        }

        var owner = _previewOwnerRootHwnd;
        var fileDialogMode = _lastSelectionSource == SelectionSource.CommonFileDialog;
        DiagnosticLog.Write(
            $"preview presentation failed reason={reason} hwnd=0x{window.Hwnd.ToInt64():X} owner=0x{owner.ToInt64():X}");
        if (fileDialogMode)
        {
            QueueElevationMismatchCheck(owner);
        }

        HidePreview();
    }

    private void OnSelectionItemRequested(PreviewWindow window, int index)
    {
        if (window == _previewWindow)
        {
            ShowMultiSelectionItem(index, Stopwatch.GetTimestamp());
        }
    }

    /// <summary>The slideshow walks the set on a loop: past the last item it starts over.</summary>
    private void OnSlideshowAdvanceRequested(PreviewWindow window)
    {
        if (window != _previewWindow || _multiSelection is not { Count: > 1 } set)
        {
            return;
        }

        ShowMultiSelectionItem((_multiIndex + 1) % set.Count, inputStamp: 0);
    }

    private void OnDismissRequested(PreviewWindow window)
    {
        // Pinned previews outlive every dismissal gesture (opening the file,
        // clicking a folder child, Space, Esc). Only X or unpinning ends them.
        if (window.IsPinned)
        {
            return;
        }

        // A window that is neither active nor pinned is already retiring; a
        // second retire would stack duplicate close handlers.
        if (window == _previewWindow)
        {
            RequestHide();
        }
    }

    private void OnPinToggleRequested(PreviewWindow window)
    {
        if (!window.IsPinned)
        {
            CancelIdleTrim();
            if (_pinnedWindows.Count >= MaxPinnedPreviews)
            {
                return;
            }

            window.SetPinned(true);
            _pinnedWindows.Add(window);
            if (window == _previewWindow)
            {
                // The pinned window leaves the glance; the next Space press
                // gets a fresh window.
                _previewWindow = null;
                _previewVisible = false;
                ResetSession();
            }

            DiagnosticLog.Write($"pinned preview ({_pinnedWindows.Count} of {MaxPinnedPreviews}) {window.CurrentPath}");
        }
        else
        {
            // Unpinning retires the live glance, so it asks first too.
            if (_previewWindow is { } live && live != window && !CanLeavePreview(live))
            {
                return;
            }

            _pinnedWindows.Remove(window);
            window.SetPinned(false);
            window.DetachBrowseContext();
            ResetSession();

            // The window the user just unpinned becomes THE preview again;
            // a previous live preview retires in its favor.
            var previous = _previewWindow;
            _previewWindow = window;
            _previewVisible = true;
            CancelIdleTrim();
            if (previous is not null && previous != window)
            {
                RetireWindow(previous);
            }

            DiagnosticLog.Write($"unpinned preview ({_pinnedWindows.Count} of {MaxPinnedPreviews}) {window.CurrentPath}");
        }

        UpdatePinCapacity();
        SyncHooks();
    }

    private void OnPinnedCloseRequested(PreviewWindow window)
    {
        // Esc auto-repeat, a double-tap, or Esc racing the X button can queue
        // this twice for one window; the second pass must not stack another
        // retire (duplicate close handlers, double Close).
        if (!_pinnedWindows.Contains(window) || !CanLeavePreview(window))
        {
            return;
        }

        _pinnedWindows.Remove(window);
        RetireWindow(window);
        UpdatePinCapacity();
        SyncHooks();
        DiagnosticLog.Write($"pinned preview closed ({_pinnedWindows.Count} of {MaxPinnedPreviews})");

        // The last pin held off the trim's collection and page-out; with no
        // glance showing, nothing else brings them.
        if (!_previewVisible && _pinnedWindows.Count == 0)
        {
            ScheduleIdleTrim();
        }
    }

    private void OnBrowseRequested(PreviewWindow window, PreviewBrowseDirection direction)
    {
        if (window != _previewWindow || !_previewVisible)
        {
            return;
        }

        BrowseRelativeAsync((int)direction, Stopwatch.GetTimestamp());
    }

    private void UpdatePinCapacity()
    {
        var reached = _pinnedWindows.Count >= MaxPinnedPreviews;
        _previewWindow?.SetPinLimitReached(reached);
    }

    /// <summary>Fades a surplus window out, then closes it for real.</summary>
    private void RetireWindow(PreviewWindow window)
    {
        if (!window.IsVisible)
        {
            CloseWindow(window);
            return;
        }

        void CloseOnceHidden(object? sender, DependencyPropertyChangedEventArgs args)
        {
            if (window.IsVisible)
            {
                return;
            }

            window.IsVisibleChanged -= CloseOnceHidden;
            // The event fires from inside Hide(); closing is deferred out of
            // that call stack.
            window.Dispatcher.BeginInvoke(() => CloseWindow(window));
        }

        window.IsVisibleChanged += CloseOnceHidden;
        window.HidePreview();
    }

    /// <summary>
    /// Shows the selection: at once, or, for a file on a phone or in a zip
    /// folder whose copy is still on its way, when the copy has arrived or
    /// has failed to, and then the card that says why. The lookups that
    /// read Explorer's selection answer with where the copy will be and
    /// hold nothing while it comes, so the arrow keys and a refresh go on
    /// working, and what they find next takes its place.
    /// </summary>
    private void ShowPreview(ExplorerSelection selection, long inputStamp)
    {
        if (_disposed)
        {
            return;
        }

        var arrival = ++_arrival;
        var ready = BorrowedFiles.WhenReady(selection.Path);
        if (ready.IsCompletedSuccessfully)
        {
            Present(selection, inputStamp);
        }
        else
        {
            ShowOnArrival(selection, inputStamp, arrival, ready);
        }
    }

    private async void ShowOnArrival(ExplorerSelection selection, long inputStamp, int arrival, Task<bool> ready)
    {
        var arrived = await ready;

        // Something else was shown, Explorer's selection has moved on, or
        // the glance has ended.
        if (_disposed || arrival != _arrival)
        {
            return;
        }

        if (!arrived)
        {
            // Explorer is on the file all the same, and so is the glance.
            DiagnosticLog.Write($"copy did not arrive -> {selection.Path}");
        }

        // A glance that is not up is opened by its own lookup, which waits
        // for the copy itself. And signing may have started on what is on
        // screen while this one came.
        if (!_previewVisible || !CanLeavePreview(_previewWindow))
        {
            return;
        }

        Present(selection, inputStamp);
    }

    private void Present(ExplorerSelection selection, long inputStamp)
    {
        if (_previewOwnerRootHwnd == IntPtr.Zero)
        {
            _previewOwnerRootHwnd = selection.ExplorerHwnd;
        }

        var fileDialogMode = _lastSelectionSource == SelectionSource.CommonFileDialog;
        if (!_previewVisible)
        {
            PublishLanding(selection, fileDialogMode);
        }

        _previewWindow ??= CreatePreviewWindow();
        CancelIdleTrim();
        _lastExplorerHwnd = selection.ExplorerHwnd;
        DiagnosticLog.Write(
            $"preview requested {selection.Path} hwnd=0x{selection.ExplorerHwnd.ToInt64():X}{(fileDialogMode ? " picker" : string.Empty)}");
        _previewWindow.InputStamp = inputStamp;
        var presented = _previewWindow.ShowPreview(
            selection.Path,
            selection.ExplorerHwnd,
            selection.SelectedCount,
            fileDialogMode);
        if (!presented)
        {
            DiagnosticLog.Write(
                $"preview presentation failed reason={_previewWindow.LastPresentationFailureReason} hwnd=0x{_previewWindow.Hwnd.ToInt64():X} owner=0x{selection.ExplorerHwnd.ToInt64():X}");
            if (fileDialogMode)
            {
                QueueElevationMismatchCheck(selection.ExplorerHwnd);
            }

            HidePreview();
            return;
        }

        if (fileDialogMode)
        {
            ApplyCommonFileDialogContext(selection);
            _officeNeighborSelection = null;
        }
        else
        {
            ApplySelectionBrowseContext(selection);
            _officeNeighborSelection = _multiSelection is null ? selection : null;
        }

        _previewVisible = true;
        SyncHooks();
    }

    /// <summary>
    /// A fresh glance keeps the dispatcher busy from here until its window
    /// is up, and no key gets its turn before then. The hook thread is told
    /// now what will be true at that turn, so a key pressed while the window
    /// is built belongs to the glance, as it did when it waited for the
    /// build. If the window fails to show, the hide that follows says so.
    /// </summary>
    private void PublishLanding(ExplorerSelection selection, bool fileDialogMode)
    {
        Volatile.Write(ref _keyState, CaptureKeyState() is not { } state
            ? null
            : state with
            {
                PreviewVisible = true,
                MultiSelection = !fileDialogMode && selection.SelectedPaths is { Count: > 1 },
                Preview = new WindowKeyState { Hwnd = _previewWindow?.Hwnd ?? IntPtr.Zero, Browsable = true }
            });
    }

    private void OnPreviewContentReady(PreviewWindow window)
    {
        if (window != _previewWindow
            || !_previewVisible
            || _officeNeighborSelection is not { } selection
            || !string.Equals(selection.Path, window.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // The last file's warm-up gives way only now: if it was converting
        // this file, this preview has already joined that job.
        _officeNeighborSelection = null;
        CancelOfficeNeighbors();
        if (!OfficeDocumentPdfConverter.IsAvailable)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _officeNeighbors = cts;
        _ = WarmOfficeNeighborsAsync(
            selection.ExplorerHwnd,
            _lastSelectionSource == SelectionSource.Desktop,
            selection.Path,
            cts.Token);
    }

    /// <summary>
    /// One at a time, nearest first, below normal priority: a preview that
    /// asks for the engine takes it, and the warm-up waits its turn.
    /// </summary>
    private static async Task WarmOfficeNeighborsAsync(IntPtr explorerHwnd, bool isDesktop, string currentPath, CancellationToken token)
    {
        try
        {
            await Task.Delay(OfficeNeighborDelay, token);
            var neighbors = await StaTask.Run(() =>
                ExplorerSelectionService.TryGetNeighborPaths(explorerHwnd, isDesktop, currentPath, OfficeNeighborLimit));
            foreach (var path in neighbors ?? [])
            {
                token.ThrowIfCancellationRequested();
                var kind = FileKinds.GetOfficeKind(path);
                if (kind == OfficeKind.None
                    || await Task.Run(() => OfficeDocumentPdfConverter.TryGetCached(path, kind), token) is not null)
                {
                    continue;
                }

                var job = OfficeDocumentPdfConverter.Convert(path, kind, token, background: true);
                await job.Complete.WaitAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            // The glance ended or moved on.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office neighbor warm-up failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelOfficeNeighbors()
    {
        var cts = Interlocked.Exchange(ref _officeNeighbors, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    private void ApplyCommonFileDialogContext(ExplorerSelection selection)
    {
        _multiSelection = null;
        _multiSelectionSelectedCount = 0;
        _multiIndex = 0;
        _previewWindow?.SetBrowsePosition(-1, 0);
        _previewWindow?.SetMultiSelection(null, 0);
        _previewWindow?.SetSelectionCount(selection.SelectedCount);
    }

    /// <summary>
    /// Multi-selection browsing stays within the selected set. The chevrons
    /// show the position in that set. A single selection browses the folder.
    /// </summary>
    private void ApplySelectionBrowseContext(ExplorerSelection selection)
    {
        _multiSelection = selection.SelectedPaths is { Count: > 1 } paths ? paths : null;
        _multiSelectionSelectedCount = _multiSelection is null ? 0 : selection.SelectedCount;
        _multiIndex = 0;

        if (_multiSelection is { } set)
        {
            for (var i = 0; i < set.Count; i++)
            {
                if (string.Equals(set[i], selection.Path, StringComparison.OrdinalIgnoreCase))
                {
                    _multiIndex = i;
                    break;
                }
            }

            _previewWindow?.SetBrowsePosition(_multiIndex, set.Count);
            _previewWindow?.SetMultiSelection(set, _multiIndex);
            return;
        }

        _previewWindow?.SetBrowsePosition(selection.ViewIndex, selection.ViewCount);
        _previewWindow?.SetMultiSelection(null, 0);
    }

    /// <summary>
    /// Steps within the captured multi-selection. Explorer's own selection
    /// stays untouched: walking through the set must not destroy it.
    /// </summary>
    private void ShowMultiSelectionItem(int index, long inputStamp)
    {
        if (_multiSelection is not { Count: > 1 } set || _previewWindow is null || !_previewVisible)
        {
            return;
        }

        var target = Math.Clamp(index, 0, set.Count - 1);
        var path = set[target];
        var changes = !string.Equals(path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase);
        if (changes && !CanLeavePreview(_previewWindow))
        {
            return;
        }

        _multiIndex = target;
        _officeNeighborSelection = null;
        DiagnosticLog.Write($"browse within selection ({_multiIndex + 1} of {set.Count}) -> {path}");
        if (changes)
        {
            _previewWindow.InputStamp = inputStamp;
            _previewWindow.ShowPreview(path, _lastExplorerHwnd, set.Count);
        }

        _previewWindow.SetBrowsePosition(_multiIndex, set.Count);
        _previewWindow.SetMultiSelectionIndex(_multiIndex);
    }

    private static bool PathListContains(IReadOnlyList<string> paths, string? path)
    {
        if (path is null)
        {
            return false;
        }

        foreach (var candidate in paths)
        {
            if (string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool PathListsEqual(
        IReadOnlyList<string>? left,
        IReadOnlyList<string> right)
    {
        if (left is null || left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<ExplorerSelection?> RunSelectionLookupAsync(
        string origin,
        SelectionSource source,
        IntPtr hwnd,
        bool includeMultiSelectionPaths,
        bool useForegroundWhenHwndMissing = false)
    {
        var diagnostics = new ExplorerSelectionLookupTrace();
        var selection = await StaTask.Run(() =>
        {
            diagnostics.MarkStaStarted();
            try
            {
                // The selected path drives the preview; folder position only
                // trims browse chrome at the ends, so it stays off this path.
                if (source == SelectionSource.Desktop)
                {
                    return ExplorerSelectionService.TryGetDesktopSelection(
                        diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths);
                }

                return hwnd != IntPtr.Zero || !useForegroundWhenHwndMissing
                    ? ExplorerSelectionService.TryGetSelectionFromExplorerWindow(
                        hwnd,
                        diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths)
                    : ExplorerSelectionService.TryGetForegroundSelection(
                        diagnostics: diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths);
            }
            finally
            {
                diagnostics.MarkStaCompleted();
            }
        });

        diagnostics.WriteIfSlow(origin, selection is not null);
        return selection;
    }

    private void QueueOpen(ForegroundSelectionContext context, long inputStamp)
    {
        var generation = ++_openRequestGeneration;
        _openPending = true;
        DiagnosticLog.Write($"space -> queued selection lookup class={context.ClassName} hwnd=0x{context.Hwnd.ToInt64():X}");
        _dispatcher.BeginInvoke(() => ShowPreviewFromContext(context, generation, inputStamp));
    }

    /// <summary>
    /// Space in another window that offers a selection moves the glance:
    /// the old preview leaves first, unless it holds unsaved signing work.
    /// </summary>
    private bool LeaveForNewContext()
    {
        if (!_previewVisible)
        {
            return true;
        }

        if (!CanLeavePreview(_previewWindow))
        {
            return false;
        }

        HidePreview();
        return true;
    }

    private async void ShowPreviewFromContext(ForegroundSelectionContext context, int generation, long inputStamp)
    {
        if (_disposed || generation != _openRequestGeneration)
        {
            return;
        }

        if (!LeaveForNewContext())
        {
            _openPending = false;
            PublishKeyState();
            return;
        }

        // COM lookups are too slow for the UI thread, and the tab-identity
        // QueryService only answers on an STA thread.
        var selection = await _lookUpSelection(
            "open",
            context.Source,
            context.Hwnd,
            includeMultiSelectionPaths: true);

        // A file on a phone or in a zip folder opens from a copy, and the
        // copy may still be on its way. Waiting for it is part of the
        // lookup: the open stays pending, and Esc or another Space ends it.
        var arrived = selection is null || await BorrowedFiles.WhenReady(selection.Path);

        if (_disposed || generation != _openRequestGeneration)
        {
            return;
        }

        _openPending = false;

        // If the user moved on while the lookup ran (Alt-Tab, click), popping
        // a topmost preview over the new foreground app is unexpected.
        if (_foregroundRoot() != context.Hwnd)
        {
            PublishKeyState();
            DiagnosticLog.Write("queued lookup -> foreground changed, dropping");
            return;
        }

        if (selection is null)
        {
            PublishKeyState();
            DiagnosticLog.Write($"queued lookup -> no selection class={context.ClassName} hwnd=0x{context.Hwnd.ToInt64():X}");
            return;
        }

        if (!arrived)
        {
            // Space was pressed on a file, and the glance opens on it.
            DiagnosticLog.Write($"queued lookup -> copy did not arrive {selection.Path}");
        }

        DiagnosticLog.Write($"queued lookup -> preview {selection.Path}");
        _lastSelectionSource = context.Source;
        _previewOwnerRootHwnd = context.Hwnd;
        Present(selection, inputStamp);
    }

    // The hook thread swallowed this Space on the selection it found
    // verified, and the press carries that selection: what opens is the
    // file the key was pressed on. A picker being read again by the key's
    // turn changes nothing here; if it has moved, the glance follows it
    // once the read is back.
    private void QueuePickerOpen(ForegroundSelectionContext context, ExplorerSelection selection, long inputStamp)
    {
        var generation = ++_openRequestGeneration;
        DiagnosticLog.Write($"space -> queued selection lookup class={context.ClassName} hwnd=0x{context.Hwnd.ToInt64():X}");
        ClearPendingFileDialogPreview();
        _dispatcher.BeginInvoke(() => ShowCommonFileDialogPreview(context, selection, generation, inputStamp));
    }

    // Fail open. The hook never waits for UI Automation and never swallows
    // Space on an unverified dialog selection. It remembers this one press
    // briefly, for the answer to one read of the picker: the read already
    // under way, or one asked for here. That answer is the file the press
    // was on. The answer to any later read is a file the user moved to
    // afterwards, and opens nothing.
    private void DeferPickerOpen(ForegroundSelectionContext context, long inputStamp, long refreshAtPress)
    {
        var generation = ++_openRequestGeneration;
        if (_commonFileDialogMonitor.LatestRefresh(context.Hwnd) > refreshAtPress)
        {
            return;
        }

        var refresh = _commonFileDialogMonitor.PendingRefresh(context.Hwnd);
        if (refresh == 0)
        {
            refresh = _commonFileDialogMonitor.RequestRefresh(context.Hwnd, settleMilliseconds: 0);
        }

        ArmPendingFileDialogPreview(context.Hwnd, generation, inputStamp, refresh);
        DiagnosticLog.Write("space -> file dialog selection not ready, deferred and passed through");
    }

    private void ShowCommonFileDialogPreview(
        ForegroundSelectionContext context,
        ExplorerSelection selection,
        int requestGeneration,
        long inputStamp)
    {
        if (_disposed
            || requestGeneration != _openRequestGeneration
            || _foregroundRoot() != context.Hwnd
            || !CommonFileDialogSelectionService.HasFileListFocus(context.Hwnd))
        {
            DiagnosticLog.Write("queued file dialog preview -> focus changed, dropping");
            return;
        }

        if (!LeaveForNewContext())
        {
            return;
        }

        _lastSelectionSource = SelectionSource.CommonFileDialog;
        _previewOwnerRootHwnd = context.Hwnd;
        _showPreview(selection, inputStamp);
    }

    private void ArmPendingFileDialogPreview(IntPtr dialogRoot, int requestGeneration, long inputStamp, long refresh)
    {
        var intent = _pendingFileDialogPreview.Arm(
            dialogRoot,
            requestGeneration,
            Environment.TickCount64,
            inputStamp,
            refresh);

        _dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(DeferredFileDialogPreviewLifetimeMilliseconds);
            if (_pendingFileDialogPreview.Expire(intent))
            {
                DiagnosticLog.Write("deferred file dialog preview expired");
            }
        }, DispatcherPriority.Background);
    }

    private void OnCommonFileDialogRefreshCompleted(
        IntPtr dialogRoot,
        ExplorerSelection? selection,
        long refresh)
    {
        var intent = _pendingFileDialogPreview.Current;
        if (intent is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var ownershipValid = !_disposed
            && _keyboardHookWanted
            && !_fullscreenInputSuspension.IsSuspended
            && _foregroundRoot() == dialogRoot
            && CommonFileDialogSelectionService.HasFileListFocus(dialogRoot);
        var decision = _pendingFileDialogPreview.EvaluateRefresh(
            intent,
            dialogRoot,
            refresh,
            _openRequestGeneration,
            now,
            DeferredFileDialogPreviewLifetimeMilliseconds,
            ownershipValid,
            selectionAvailable: selection is not null);
        if (decision == DeferredFileDialogPreviewDecision.Ignore)
        {
            return;
        }

        if (decision == DeferredFileDialogPreviewDecision.Drop || selection is null)
        {
            DiagnosticLog.Write("deferred file dialog preview -> ownership or request changed, dropping");
            return;
        }

        var elapsed = Math.Max(0, now - intent.ArmedAtTick);
        DiagnosticLog.Write($"deferred file dialog preview -> ready +{elapsed}ms");
        var context = new ForegroundSelectionContext(
            dialogRoot,
            SelectionSource.CommonFileDialog,
            CommonFileDialogSelectionService.DialogWindowClass);
        ShowCommonFileDialogPreview(context, selection, intent.RequestGeneration, intent.InputStamp);
    }

    private void ClearPendingFileDialogPreview() => _pendingFileDialogPreview.Cancel();

    /// <summary>A preview could not be put up over this picker: say so if the app's rights are why.</summary>
    private void QueueElevationMismatchCheck(IntPtr dialogRoot)
    {
        if (_disposed
            || dialogRoot == IntPtr.Zero
            || !PreviewAccessWarningPolicy.CanShowLifetime(
                PreviewAccessWarningPolicy.FromSettings(Settings.Current),
                DateTimeOffset.UtcNow))
        {
            return;
        }

        var processId = NativeMethods.GetWindowProcessId(dialogRoot);
        StartElevationMismatchCheck(dialogRoot, processId);
    }

    /// <summary>
    /// A file picker came to the front. The access notice starts here, and
    /// nowhere a key can reach: Windows calls no low-level hook of ours
    /// while a window with higher rights has the keyboard, so a key pressed
    /// in an elevated app's picker never arrives. The foreground change
    /// does, and a picker's windows can be read from any process.
    /// </summary>
    private void OnCommonFileDialogObserved(IntPtr dialogRoot)
    {
        if (_disposed
            || !PreviewAccessWarningPolicy.CanShowLifetime(
                PreviewAccessWarningPolicy.FromSettings(Settings.Current),
                DateTimeOffset.UtcNow))
        {
            return;
        }

        var processId = NativeMethods.GetWindowProcessId(dialogRoot);
        if (processId == 0)
        {
            return;
        }

        if (_observedAccessDialogs.Count >= MaxObservedAccessDialogs)
        {
            _observedAccessDialogs.Clear();
        }

        if (_observedAccessDialogs.Add((dialogRoot.ToInt64(), processId)))
        {
            StartElevationMismatchCheck(dialogRoot, processId);
        }
    }

    private void StartElevationMismatchCheck(IntPtr dialogRoot, uint processId)
    {
        if (_disposed
            || processId == 0
            || !_elevationChecksInFlight.TryAdd(dialogRoot.ToInt64(), 0))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (_disposed
                    || !NativeMethods.IsWindow(dialogRoot)
                    || NativeMethods.GetWindowProcessId(dialogRoot) != processId
                    || !CommonFileDialogSelectionService.HasDialogWindowClass(dialogRoot))
                {
                    return;
                }

                if (_readAccessWarning(dialogRoot) is { } warning)
                {
                    _dispatcher.BeginInvoke(() => CompletePreviewAccessCheck(
                        dialogRoot,
                        processId,
                        warning));
                }
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write(
                    $"preview access check failed hwnd=0x{dialogRoot.ToInt64():X}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _elevationChecksInFlight.TryRemove(dialogRoot.ToInt64(), out _);
            }
        });
    }

    private void CompletePreviewAccessCheck(
        IntPtr dialogRoot,
        uint processId,
        PreviewAccessWarningInfo warning)
    {
        if (_disposed
            || !NativeMethods.IsWindow(dialogRoot)
            || NativeMethods.GetWindowProcessId(dialogRoot) != processId
            || !CommonFileDialogSelectionService.HasDialogWindowClass(dialogRoot))
        {
            DiagnosticLog.Write(
                $"preview access warning discarded: dialog closed or handle reused hwnd=0x{dialogRoot.ToInt64():X} pid={processId}");
            return;
        }

        DiagnosticLog.Write(
            $"preview access warning detected kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId} hwnd=0x{dialogRoot.ToInt64():X}");
        PreviewAccessWarningDetected?.Invoke(warning);
    }

    private void OnCommonFileDialogSelectionChanged(IntPtr dialogRoot, ExplorerSelection? selection)
    {
        if (_disposed
            || !_previewVisible
            || _previewWindow is null
            || _lastSelectionSource != SelectionSource.CommonFileDialog
            || _previewOwnerRootHwnd != dialogRoot)
        {
            return;
        }

        if (selection is null)
        {
            DiagnosticLog.Write("file dialog selection cleared -> hide");
            HidePreview();
            return;
        }

        _lastExplorerHwnd = dialogRoot;
        if (string.Equals(selection.Path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            ApplyCommonFileDialogContext(selection);
            return;
        }

        // The reveal clock starts at the key or click that moved the picker,
        // not when the settled read lands.
        var stamp = _pickerInputStamp != 0 ? _pickerInputStamp : Stopwatch.GetTimestamp();
        _pickerInputStamp = 0;
        DiagnosticLog.Write($"file dialog selection changed -> {selection.Path}");
        ShowPreview(selection, stamp);
    }

    private void HidePreviewAndRestoreOwner(IntPtr owner)
    {
        HidePreview();
        if (owner != IntPtr.Zero && NativeMethods.IsWindow(owner))
        {
            NativeMethods.SetForegroundWindow(owner);
        }
    }

    // Unsaved signing work is the one thing a glance never drops: every way
    // out asks first.
    private static bool CanLeavePreview(PreviewWindow? window)
    {
        if (window?.TryLeaveFillSign() != false)
        {
            return true;
        }

        DiagnosticLog.Write("exit held for unsaved signing work");
        return false;
    }

    /// <summary>
    /// Quit is a way out too. The first window holding signing work comes
    /// forward with its save choice, and the app stays.
    /// </summary>
    internal bool CanQuit()
    {
        foreach (var window in OpenWindows())
        {
            if (!CanLeavePreview(window))
            {
                window.BringForward();
                return false;
            }
        }

        return true;
    }

    private IEnumerable<PreviewWindow> OpenWindows()
    {
        if (_previewVisible && _previewWindow is { } glance)
        {
            yield return glance;
        }

        foreach (var pinned in _pinnedWindows)
        {
            yield return pinned;
        }
    }

    /// <summary>Every user-driven end of a glance comes through here.</summary>
    private void RequestHide()
    {
        if (!_previewVisible || !CanLeavePreview(_previewWindow))
        {
            return;
        }

        HidePreview();
    }

    private void HidePreview()
    {
        _previewVisible = false;
        ResetSession();

        // A copy still on its way was for this glance.
        BorrowedFiles.StopWaiting();

        // Before the window does its own leaving, which can take a while:
        // from here the hook thread claims no key for this glance.
        PublishKeyState();
        DiagnosticLog.Write("hide preview");
        _previewWindow?.HidePreview();
        SyncHooks();
        ScheduleIdleTrim();
    }

    /// <summary>
    /// The one reset of a glance's state. Hide, pin, unpin, the welcome and
    /// idle trim all end a session the same way, so nothing from one glance
    /// leaks into the next (a queued arrow step once did).
    /// </summary>
    private void ResetSession()
    {
        _arrival++;
        _ownerLossTimer.Stop();
        ClearPendingFileDialogPreview();
        ClearNavigationRefreshTokens();
        _queuedRefreshRequest = null;
        _pendingKeyUpVerification = null;
        _browsePendingDelta = 0;
        _pickerInputStamp = 0;
        _previewOwnerRootHwnd = IntPtr.Zero;
        _lastExplorerHwnd = IntPtr.Zero;
        _lastSelectionSource = SelectionSource.Explorer;
        _multiSelection = null;
        _multiSelectionSelectedCount = 0;
        _multiIndex = 0;
        _officeNeighborSelection = null;
        CancelOfficeNeighbors();
    }

    private void ClearNavigationRefreshTokens() => Array.Clear(_navigationKeyRefreshTokens);

    private static SelectionRefreshRequest NewestRefreshRequest(
        SelectionRefreshRequest? pending,
        SelectionRefreshRequest incoming)
    {
        if (pending is not { } current || incoming.Generation > current.Generation)
        {
            return incoming;
        }

        if (incoming.Generation == current.Generation
            && incoming.IncludeMultiSelectionPaths
            && !current.IncludeMultiSelectionPaths)
        {
            return current with { IncludeMultiSelectionPaths = true };
        }

        return current;
    }

    private static SelectionReleaseVerification NewestReleaseVerification(
        SelectionReleaseVerification? pending,
        SelectionReleaseVerification incoming)
    {
        if (pending is not { } current
            || incoming.Request.Generation > current.Request.Generation)
        {
            return incoming;
        }

        if (incoming.Request.Generation < current.Request.Generation)
        {
            return current;
        }

        return new SelectionReleaseVerification(
            NewestRefreshRequest(current.Request, incoming.Request),
            Math.Max(current.StartedGenerationAtRelease, incoming.StartedGenerationAtRelease));
    }

    private void RequestSelectionRefresh(SelectionRefreshRequest request)
    {
        if (_refreshInFlight)
        {
            _queuedRefreshRequest = NewestRefreshRequest(_queuedRefreshRequest, request);
            return;
        }

        RefreshSelectionAsync(request);
    }

    private void RequestKeyUpSelectionVerification(SelectionReleaseVerification verification)
    {
        var request = verification.Request;
        if (!PreviewInputPolicy.ShouldVerifyNavigationRelease(
                request.Generation,
                _navigationRefreshGeneration,
                verification.StartedGenerationAtRelease,
                _lastChangedRefreshGeneration))
        {
            return;
        }

        if (_refreshInFlight)
        {
            _pendingKeyUpVerification = NewestReleaseVerification(
                _pendingKeyUpVerification,
                verification);
            return;
        }

        RequestSelectionRefresh(request);
    }

    private async void RefreshSelectionAsync(SelectionRefreshRequest request)
    {
        if (!_previewVisible || _previewWindow is null)
        {
            return;
        }

        _refreshInFlight = true;
        var activeRequest = request;
        try
        {
            do
            {
                if (_queuedRefreshRequest is { } queuedRequest)
                {
                    activeRequest = NewestRefreshRequest(activeRequest, queuedRequest);
                }

                _queuedRefreshRequest = null;
                var target = _previewWindow;
                var source = _lastSelectionSource;
                var hwnd = _lastExplorerHwnd;
                var ownerRoot = _previewOwnerRootHwnd;
                _lastStartedRefreshGeneration = activeRequest.Generation;
                var selection = await _lookUpSelection(
                    "refresh",
                    source,
                    hwnd,
                    includeMultiSelectionPaths: activeRequest.IncludeMultiSelectionPaths,
                    useForegroundWhenHwndMissing: true);

                if (_disposed || !_previewVisible || _previewWindow is null)
                {
                    return;
                }

                // The preview changed hands during the lookup (an unpinned
                // window took over as the active preview). Writing Explorer's
                // selection into a window the user just chose to keep would
                // swap its content under them.
                if (_previewWindow != target
                    || _lastSelectionSource != source
                    || _previewOwnerRootHwnd != ownerRoot)
                {
                    return;
                }

                // Explorer has said what is selected now. A copy on its way
                // for anything else is no longer waited for.
                _arrival++;

                // Shift navigation is intentional selection editing. If
                // Explorer still reports the current item as its anchor, keep
                // that preview while installing the newly ordered set around
                // it instead of throwing the enrichment away as "no change".
                if (TryApplyIntentionalMultiSelection(selection, activeRequest))
                {
                    continue;
                }

                // While browsing WITHIN a multi-selection, Explorer keeps
                // reporting the set's anchor; snapping back to it would undo
                // every step. The set holds until the user actually selects
                // something new.
                if (_multiSelection is { Count: > 1 } activeSet)
                {
                    if (selection is { SelectedCount: > 1 }
                        && PathListContains(activeSet, _previewWindow.CurrentPath)
                        && (selection.SelectedPaths is null
                            || PathListContains(selection.SelectedPaths, _previewWindow.CurrentPath)))
                    {
                        continue;
                    }

                    _multiSelection = null;
                    _multiSelectionSelectedCount = 0;
                    _previewWindow.SetMultiSelection(null, 0);
                    _previewWindow.SetSelectionCount(1);
                }

                var decision = PreviewInputPolicy.DecideRefresh(selection, _previewWindow.CurrentPath);
                if (decision.Action == RefreshSelectionAction.KeepCurrent)
                {
                    continue;
                }

                if (decision.Action == RefreshSelectionAction.HidePreview)
                {
                    DiagnosticLog.Write("selection cleared -> hide");
                    RequestHide();
                    return;
                }

                // The glance follows Explorer, but never over unsaved work.
                if (decision.Selection is not { } changed || !CanLeavePreview(_previewWindow))
                {
                    return;
                }

                _lastExplorerHwnd = changed.ExplorerHwnd;
                _lastChangedRefreshGeneration = Math.Max(
                    _lastChangedRefreshGeneration,
                    activeRequest.Generation);
                DiagnosticLog.Write($"selection changed -> {changed.Path}");
                ShowPreview(changed, activeRequest.InputStamp);
            } while (_queuedRefreshRequest is not null);
        }
        finally
        {
            _refreshInFlight = false;

            var pendingVerification = _pendingKeyUpVerification;
            _pendingKeyUpVerification = null;
            if (pendingVerification is { } verification
                && PreviewInputPolicy.ShouldVerifyNavigationRelease(
                    verification.Request.Generation,
                    _navigationRefreshGeneration,
                    verification.StartedGenerationAtRelease,
                    _lastChangedRefreshGeneration)
                && !_disposed
                && _previewVisible)
            {
                RequestSelectionRefresh(verification.Request);
            }
        }
    }

    private bool TryApplyIntentionalMultiSelection(
        ExplorerSelection? selection,
        SelectionRefreshRequest request)
    {
        if (_lastSelectionSource == SelectionSource.CommonFileDialog
            || !request.IncludeMultiSelectionPaths
            || selection?.SelectedPaths is not { Count: > 1 } paths
            || _previewWindow?.CurrentPath is not { } currentPath
            || !PathListContains(paths, currentPath))
        {
            return false;
        }

        var pathsChanged = !PathListsEqual(_multiSelection, paths);
        var countChanged = _multiSelectionSelectedCount != selection.SelectedCount;
        if (pathsChanged)
        {
            _lastExplorerHwnd = selection.ExplorerHwnd;
            ApplySelectionBrowseContext(selection with { Path = currentPath });
        }
        else if (countChanged)
        {
            _multiSelectionSelectedCount = selection.SelectedCount;
        }

        if (pathsChanged || countChanged)
        {
            _previewWindow.SetSelectionCount(selection.SelectedCount);
            _lastChangedRefreshGeneration = Math.Max(
                _lastChangedRefreshGeneration,
                request.Generation);
            DiagnosticLog.Write($"multi-selection changed count={selection.SelectedCount} carried={paths.Count}");
        }

        return true;
    }

    /// <summary>
    /// Steps the Explorer selection by <paramref name="delta"/> view
    /// positions and previews what lands. Coalesced like selection refresh:
    /// holding the key accumulates steps instead of queueing a COM call per
    /// key repeat. Steps left over when a browse ends early are dropped.
    /// </summary>
    private async void BrowseRelativeAsync(int delta, long inputStamp)
    {
        if (_disposed
            || !_previewVisible
            || _previewWindow is null
            || _lastSelectionSource == SelectionSource.CommonFileDialog
            || !CanLeavePreview(_previewWindow))
        {
            return;
        }

        if (_browsePendingDelta == 0)
        {
            _browseInputStamp = inputStamp;
        }

        _browsePendingDelta += delta;
        if (_browseInFlight)
        {
            return;
        }

        _browseInFlight = true;
        try
        {
            while (_browsePendingDelta != 0)
            {
                var step = _browsePendingDelta;
                var stepStamp = _browseInputStamp;
                _browsePendingDelta = 0;

                // Within a multi-selection, stepping is instant
                // and local: no shell round-trip, no selection change.
                if (_multiSelection is { Count: > 1 })
                {
                    ShowMultiSelectionItem(_multiIndex + step, stepStamp);
                    continue;
                }

                var target = _previewWindow;
                var source = _lastSelectionSource;
                var ownerRoot = _previewOwnerRootHwnd;
                var wasDesktop = source == SelectionSource.Desktop;
                var hwnd = _lastExplorerHwnd;
                var selection = await _browseRelative(hwnd, wasDesktop, step);

                if (_disposed
                    || !_previewVisible
                    || _previewWindow is null
                    || _previewWindow != target
                    || _lastSelectionSource != source
                    || _previewOwnerRootHwnd != ownerRoot)
                {
                    return;
                }

                if (selection is null)
                {
                    DiagnosticLog.Write($"browse {step:+0;-0} -> no result");
                    return;
                }

                // The step has landed. A copy on its way for where the
                // last one landed is no longer waited for.
                _arrival++;

                DiagnosticLog.Write($"browse {step:+0;-0} ({selection.ViewIndex + 1} of {selection.ViewCount}) -> {selection.Path}");
                if (!string.Equals(selection.Path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Signing may have started while the shell answered.
                    if (!CanLeavePreview(_previewWindow))
                    {
                        return;
                    }

                    ShowPreview(selection, stepStamp);
                }
                else
                {
                    // Already at the folder's end; the chevrons learn it.
                    _previewWindow.SetBrowsePosition(selection.ViewIndex, selection.ViewCount);
                }
            }
        }
        finally
        {
            _browseInFlight = false;
            _browsePendingDelta = 0;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTrimTimer.Stop();
        _ownerLossTimer.Stop();
        _fullscreenRecheckTimer.Stop();
        CancelOfficeNeighbors();
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        _commonFileDialogMonitor.SelectionChanged -= OnCommonFileDialogSelectionChanged;
        _commonFileDialogMonitor.RefreshCompleted -= OnCommonFileDialogRefreshCompleted;
        _commonFileDialogMonitor.DialogObserved -= OnCommonFileDialogObserved;
        _commonFileDialogMonitor.DialogBuilt -= OnCommonFileDialogBuilt;
        ClearPendingFileDialogPreview();
        _commonFileDialogMonitor.Dispose();
        _fullscreenInputSuspension.SuspensionChanged -= OnFullscreenSuspensionChanged;
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
        _headerFocus.Dispose();

        // Ends the state watch.
        SyncKeyState();
        if (_previewWindow is not null)
        {
            _previewWindow.HidePreview();
            _previewWindow.Close();
            _previewWindow = null;
        }

        foreach (var pinned in _pinnedWindows.ToArray())
        {
            try
            {
                pinned.Close();
            }
            catch
            {
                // Shutdown must not trip over a window mid-teardown.
            }
        }

        _pinnedWindows.Clear();
        _observedAccessDialogs.Clear();
    }

    private void ScheduleIdleTrim()
    {
        if (_disposed)
        {
            return;
        }

        _idleTrimTimer.Stop();
        _idleTrimTimer.Start();
        DiagnosticLog.Write($"idle trim scheduled {IdleTrimDelay.TotalSeconds:0}s");
    }

    private void CancelIdleTrim()
    {
        if (_idleTrimTimer.IsEnabled)
        {
            _idleTrimTimer.Stop();
            DiagnosticLog.Write("idle trim cancelled");
        }
    }

    private void RunIdleTrim()
    {
        _idleTrimTimer.Stop();
        if (_disposed || _previewVisible)
        {
            return;
        }

        var before = GC.GetTotalMemory(forceFullCollection: false);
        if (_previewWindow is { } window)
        {
            if (window.IsVisible || window.IsPinned)
            {
                return;
            }

            try
            {
                CloseWindow(window);
            }
            catch
            {
                // A window already inside teardown should not block trimming.
            }

            _previewWindow = null;
        }

        ResetSession();

        // A pinned window is still in use: no pause across the process and
        // no page-out under it. Closing the last pin trims again.
        if (_pinnedWindows.Count > 0 || Interlocked.Exchange(ref _idleTrimInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                // Blocking and compacting, or the GC may decline: an idle
                // tray allocates too little for it to come by on its own,
                // and a stranded animation clock keeps WPF rendering at
                // 64 Hz until one does. Once, 45 s after the last hide.
                var started = Stopwatch.GetTimestamp();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                var collected = Stopwatch.GetElapsedTime(started);
                var trimmed = NativeMethods.TrimCurrentProcessWorkingSet();
                var after = GC.GetTotalMemory(forceFullCollection: false);
                DiagnosticLog.Write($"idle trim complete managed {before / 1024 / 1024:0}MB -> {after / 1024 / 1024:0}MB gc={collected.TotalMilliseconds:0.0}ms workingSetTrim={trimmed}");
            }
            finally
            {
                Volatile.Write(ref _idleTrimInFlight, 0);
            }
        });
    }
}
