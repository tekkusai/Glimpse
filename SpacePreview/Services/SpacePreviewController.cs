using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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
        bool IncludeMultiSelectionPaths);

    private readonly record struct SelectionReleaseVerification(
        SelectionRefreshRequest Request,
        long StartedGenerationAtRelease);

    private static readonly HashSet<int> NavigationKeys =
    [
        NativeMethods.VK_LEFT,
        NativeMethods.VK_UP,
        NativeMethods.VK_RIGHT,
        NativeMethods.VK_DOWN,
        NativeMethods.VK_NEXT,
        NativeMethods.VK_PRIOR,
        NativeMethods.VK_HOME,
        NativeMethods.VK_END,
        NativeMethods.VK_DELETE
    ];

    private const int MaxPinnedPreviews = 6;
    private const int MaxObservedAccessDialogs = 64;
    private const int OfficeNeighborPreconvertLimit = 4;
    private const int DeferredFileDialogPreviewLifetimeMilliseconds = 1200;
    private static readonly TimeSpan OfficeNeighborPreconvertDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan IdleTrimDelay = TimeSpan.FromSeconds(45);

    private readonly Dispatcher _dispatcher;
    private readonly KeyboardHook _keyboardHook;
    private readonly MouseHook _mouseHook;
    private readonly FullscreenInputSuspension _fullscreenInputSuspension;
    private readonly CommonFileDialogSelectionMonitor _commonFileDialogMonitor;
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly DispatcherTimer _dialogOwnerTimer;
    private readonly List<PreviewWindow> _pinnedWindows = [];
    private IntPtr[] _mouseActivationWindows = [];
    private PreviewWindow? _previewWindow;
    private IntPtr _lastExplorerHwnd;
    private IntPtr _previewOwnerRootHwnd;
    private SelectionSource _lastSelectionSource = SelectionSource.Explorer;
    private volatile bool _previewVisible;
    private readonly SpaceKeyLatch _spaceKey = new();
    private bool _refreshInFlight;
    private SelectionRefreshRequest? _queuedRefreshRequest;
    private readonly long[] _navigationKeyRefreshTokens = new long[256];
    private long _navigationRefreshGeneration;
    private long _lastStartedRefreshGeneration;
    private long _lastChangedRefreshGeneration;
    private SelectionReleaseVerification? _pendingKeyUpVerification;
    private bool _browseInFlight;
    private int _browsePendingDelta;
    private IReadOnlyList<string>? _multiSelection;
    private int _multiSelectionSelectedCount;
    private int _multiIndex;
    private int _openRequestGeneration;
    private readonly DeferredFileDialogPreviewIntentSlot _pendingFileDialogPreview = new();
    private readonly ConcurrentDictionary<long, byte> _elevationChecksInFlight = new();
    private readonly HashSet<(long Hwnd, uint ProcessId)> _observedAccessDialogs = [];
    private int _idleTrimInFlight;
    private bool _inputHooksActive;
    private ExplorerSelection? _pendingOfficePreconvertSelection;
    private CancellationTokenSource? _officeNeighborPreconvertCts;
    private bool _disposed;

    // When the latest key or click arrived; the preview it asks for counts
    // its wait and its log from here. Taken once, so a later load the user
    // did not ask for (a slideshow step) starts its own clock.
    private long _inputTimestamp;

    public event Action<PreviewAccessWarningInfo>? PreviewAccessWarningDetected;

    internal bool CanShowUpdateNotification =>
        !_disposed
        && !_previewVisible
        && _pinnedWindows.Count == 0
        && !_fullscreenInputSuspension.IsSuspended;

    public SpacePreviewController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _keyboardHook = new KeyboardHook(OnKeyDown, OnKeyUp);
        _mouseHook = new MouseHook(QueueMouseDown, IsMouseActivationWindow);
        _fullscreenInputSuspension = new FullscreenInputSuspension();
        _fullscreenInputSuspension.SuspensionChanged += OnFullscreenSuspensionChanged;
        _commonFileDialogMonitor = new CommonFileDialogSelectionMonitor(dispatcher);
        _commonFileDialogMonitor.SelectionChanged += OnCommonFileDialogSelectionChanged;
        _commonFileDialogMonitor.RefreshCompleted += OnCommonFileDialogRefreshCompleted;
        _commonFileDialogMonitor.DialogObserved += OnCommonFileDialogObserved;
        _idleTrimTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = IdleTrimDelay
        };
        _idleTrimTimer.Tick += (_, _) => RunIdleTrim();
        _dialogOwnerTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _dialogOwnerTimer.Tick += (_, _) => VerifyCommonFileDialogOwner();
    }

    public void Start()
    {
        _commonFileDialogMonitor.Start();
        _fullscreenInputSuspension.Start();
        SetInputHooksActive(!_fullscreenInputSuspension.IsSuspended);
    }

    private void OnFullscreenSuspensionChanged(bool suspended)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            SetInputHooksActive(!suspended);
            SetFullscreenPreviewSuppressed(suspended);
        });
    }

    private void SetInputHooksActive(bool active)
    {
        if (_inputHooksActive == active)
        {
            return;
        }

        if (active)
        {
            _keyboardHook.Start();
        }
        else
        {
            _keyboardHook.Stop();
            _spaceKey.Reset();
            ClearNavigationRefreshTokens();
            ClearPendingFileDialogPreview();
        }

        _inputHooksActive = active;
        SyncMouseHook();
    }

    private void SyncMouseHook()
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

        var activationWindows = windows.ToArray();
        Volatile.Write(ref _mouseActivationWindows, activationWindows);
        if (MouseHook.ShouldRun(_inputHooksActive, _previewVisible, _pinnedWindows.Count))
        {
            _mouseHook.Start();
        }
        else
        {
            _mouseHook.Stop();
        }
    }

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

    private void SetFullscreenPreviewSuppressed(bool suppressed)
    {
        foreach (var pinned in _pinnedWindows)
        {
            pinned.SetTopmostSuppressed(suppressed);
        }

        if (!suppressed || !_previewVisible || _previewWindow is null)
        {
            return;
        }

        HidePreview();
    }

    // Called synchronously from the global hook callbacks. This lane must
    // stay non-blocking: no disk I/O, sleeps, waits, blocking GC, process
    // lookup, or slow cleanup here. Queue/BeginInvoke/Task.Run instead.
    private bool OnKeyDown(int vk)
    {
        _inputTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        if (vk != NativeMethods.VK_SPACE)
        {
            ClearPendingFileDialogPreview();
        }

        // A handled repeat or a mode change supersedes any earlier native
        // refresh for this physical key. Only key-downs which actually reach
        // Explorer install a fresh token for key-up verification below.
        if ((uint)vk < (uint)_navigationKeyRefreshTokens.Length)
        {
            Interlocked.Exchange(ref _navigationKeyRefreshTokens[vk], 0);
        }

        if (_fullscreenInputSuspension.ShouldPassThroughInput())
        {
            return false;
        }

        // Keys typed into a pinned preview belong to that window: its own
        // handlers decide (and Space may be its content's play/pause).
        // Except Esc - the universal escape hatch closes the pinned window
        // the user is focused on.
        if (FindForegroundPinnedWindow() is { } focusedPinned)
        {
            if (vk == NativeMethods.VK_ESCAPE)
            {
                // A find bar the user opened gets its Esc first; the next
                // one closes the window.
                if (focusedPinned.ConsumeEscapeYield())
                {
                    DiagnosticLog.Write("escape -> pinned content find UI");
                    return false;
                }

                DiagnosticLog.Write("escape -> close focused pinned preview");
                _dispatcher.BeginInvoke(() => OnPinnedCloseRequested(focusedPinned));
                return true;
            }

            if (vk == NativeMethods.VK_F && NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL))
            {
                focusedPinned.NoteFindShortcut();
            }

            // A pinned player keeps Space = play/pause; the host toggles it
            // so the player's own controls keep their keyboard focus.
            if (vk == NativeMethods.VK_SPACE && NoModifierKeysDown() && focusedPinned.IsMediaPreview)
            {
                if (_spaceKey.TryPress())
                {
                    focusedPinned.TryToggleMediaPlayback();
                }

                return true;
            }

            // Arrows over a pinned player: host-side seek/volume too.
            if (NoModifierKeysDown() && focusedPinned.TryNudgeMediaFromArrow(vk))
            {
                return true;
            }

            return false;
        }

        // Sign mode owns the keyboard: the user is placing ink on a
        // document, and Space or an arrow must never dismiss the preview or
        // browse away their unsaved work. Esc walks the mode's own layers
        // (save choice first when work exists); everything else passes
        // through so the date editor stays fully typable.
        if (_previewVisible && _previewWindow?.IsFillSignModeActive == true)
        {
            if (vk == NativeMethods.VK_ESCAPE && IsForegroundActivePreview())
            {
                DiagnosticLog.Write("escape -> fill-sign layer");
                _dispatcher.BeginInvoke(() => _previewWindow?.RequestFillSignEscape());
                return true;
            }

            if (vk is NativeMethods.VK_SPACE or NativeMethods.VK_RETURN
                or NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT
                or NativeMethods.VK_UP or NativeMethods.VK_DOWN)
            {
                return false;
            }
        }

        // Ctrl+F into a focused preview opens the PDF viewer's find bar;
        // remember it so Esc closes that bar before it closes the preview.
        if (vk == NativeMethods.VK_F
            && _previewVisible
            && NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
            && IsForegroundActivePreview())
        {
            _previewWindow?.NoteFindShortcut();
            return false;
        }

        if (_previewVisible
            && _previewWindow?.HasHeaderZoom == true
            && IsForegroundActivePreview()
            && NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
            && !NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
            && (PreviewWindow.IsZoomInKey(vk) || PreviewWindow.IsZoomOutKey(vk) || PreviewWindow.IsZoomResetKey(vk)))
        {
            var window = _previewWindow;
            _dispatcher.BeginInvoke(() => window.TryAdjustZoomFromShortcut(vk));
            return true;
        }

        if (vk == NativeMethods.VK_ESCAPE && _previewVisible)
        {
            if (!CanHandleActivePreviewShortcutFromForeground())
            {
                return false;
            }

            if (IsForegroundActivePreview() && _previewWindow?.ConsumeEscapeYield() == true)
            {
                DiagnosticLog.Write("escape -> content find UI");
                return false;
            }

            // The index sheet and a running slideshow absorb Esc before it
            // can mean dismiss: nearest surface first.
            if (_previewWindow?.TryConsumeEscapeLayer() == true)
            {
                DiagnosticLog.Write("escape -> selection surface");
                return true;
            }

            DiagnosticLog.Write("escape -> hide");
            _dispatcher.BeginInvoke(HidePreview);
            return true;
        }

        if (vk == NativeMethods.VK_RETURN && _previewVisible)
        {
            if (!CanHandleActivePreviewShortcutFromForeground())
            {
                return false;
            }

            // The picker owns commit. When it still has focus, dismiss the
            // glance and let the original Enter reach its Open button. If the
            // user clicked into the preview first, return focus to the picker
            // without launching a second application behind the modal dialog.
            if (_lastSelectionSource == SelectionSource.CommonFileDialog)
            {
                if (GetForegroundRoot() == _previewOwnerRootHwnd)
                {
                    DiagnosticLog.Write("enter -> hide, file dialog confirms");
                    _dispatcher.BeginInvoke(HidePreview);
                    return false;
                }

                if (IsForegroundActivePreview())
                {
                    var owner = _previewOwnerRootHwnd;
                    DiagnosticLog.Write("enter -> return to file dialog");
                    _dispatcher.BeginInvoke(() => HidePreviewAndRestoreOwner(owner));
                    return true;
                }

                return false;
            }

            // Ctrl+Enter toggles the index sheet for a multi-selection
            // (Quick Look's Cmd-Return). Explorer must never see it: it
            // would open every selected file.
            if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL))
            {
                if (_multiSelection is { Count: > 1 })
                {
                    _dispatcher.BeginInvoke(() => _previewWindow?.ToggleGridView());
                    return true;
                }

                return false;
            }

            // With the sheet up, Enter commits its cursor to the preview
            // instead of opening the selection.
            if (_previewWindow?.IsGridViewOpen == true)
            {
                _dispatcher.BeginInvoke(() => _previewWindow?.CommitGridCursor());
                return true;
            }

            // Enter keeps one promise everywhere: open the file in its
            // default app. The host claims it even when the embedded browser
            // holds the keyboard - otherwise a click into a document silently
            // killed the key. The armed find bar is the exception: Enter
            // there means "next match".
            var previewHwnd = _previewWindow?.Hwnd ?? IntPtr.Zero;
            if (previewHwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == previewHwnd)
            {
                if (!NoModifierKeysDown() || _previewWindow?.IsEscapeYieldArmed == true)
                {
                    return false;
                }

                if (_previewWindow?.ConsumeEnterYieldToContent() == true)
                {
                    return false;
                }

                DiagnosticLog.Write("enter -> open current");
                _dispatcher.BeginInvoke(() => _previewWindow?.OpenCurrent());
                return true;
            }

            // Quick Look behavior: opening the file dismisses the preview.
            // Explorer still receives Enter and launches the selection itself.
            DiagnosticLog.Write("enter -> hide, explorer opens");
            _dispatcher.BeginInvoke(HidePreview);
            return false;
        }

        if (NavigationKeys.Contains(vk) && _previewVisible)
        {
            if (!CanHandleActivePreviewShortcutFromForeground())
            {
                return false;
            }

            // Arrow, Home/End, Page, and Delete semantics belong entirely to
            // the modal picker. Its WinEvent selection notification refreshes
            // the preview after the native move; this request is a quiet
            // fallback for providers that coalesce that notification.
            if (_lastSelectionSource == SelectionSource.CommonFileDialog)
            {
                if (!IsForegroundActivePreview()
                    && CommonFileDialogSelectionService.HasFileListFocus(_previewOwnerRootHwnd))
                {
                    _commonFileDialogMonitor.RequestRefresh(_previewOwnerRootHwnd, settleMilliseconds: 55);
                }

                return false;
            }

            if (TryBrowseFromArrow(vk))
            {
                return true;
            }

            // Engaged media handles the arrows through the host: seek and
            // volume land on the player itself instead of walking keyboard
            // focus across its buttons.
            if (IsForegroundActivePreview() && NoModifierKeysDown()
                && _previewWindow?.TryNudgeMediaFromArrow(vk) == true)
            {
                return true;
            }

            // A key the active preview declined still belongs to its content
            // (scrolling, text selection, Delete, and so on). Explorer never
            // receives it, so there is no selection change to verify.
            if (IsForegroundActivePreview())
            {
                return false;
            }

            // Refresh on key DOWN so holding an arrow key scrubs live instead
            // of waiting for release. Explorer still handles the key.
            var generation = Interlocked.Increment(ref _navigationRefreshGeneration);
            var includeMultiSelectionPaths = IntentionalMultiSelectionNavigation(vk);
            Interlocked.Exchange(
                ref _navigationKeyRefreshTokens[vk],
                includeMultiSelectionPaths ? -generation : generation);
            var request = new SelectionRefreshRequest(generation, includeMultiSelectionPaths);
            _dispatcher.BeginInvoke(
                () => RequestSelectionRefresh(request),
                DispatcherPriority.Input);
            return false;
        }

        if (vk == NativeMethods.VK_SPACE && NoModifierKeysDown())
        {
            if (_previewVisible)
            {
                var foregroundRoot = GetForegroundRoot();
                var ownerIsTextEditing = foregroundRoot == _previewOwnerRootHwnd
                    && ExplorerSelectionService.IsWindowTextEditing(_previewOwnerRootHwnd);
                if (PreviewInputPolicy.ShouldYieldSpaceToOwnerTextEditing(
                        foregroundRoot,
                        _previewOwnerRootHwnd,
                        ownerIsTextEditing))
                {
                    DiagnosticLog.Write("space -> owner text editor");
                    return false;
                }
            }

            if (!_spaceKey.TryPress())
            {
                _spaceKey.RepairIfReleased();
                if (!_spaceKey.TryPress())
                {
                    if (!_previewVisible || !CanHandleActivePreviewShortcutFromForeground())
                    {
                        DiagnosticLog.Write("space repeat ignored");
                        return false;
                    }

                    // Holding Space must not machine-gun a media toggle. The only
                    // repeats that still flow to content are find-box spaces: the
                    // first press already hid the preview in every other case.
                    return !IsForegroundActivePreview() || _previewWindow?.IsMediaPreview == true;
                }

                DiagnosticLog.Write("space latch repaired before handling");
            }

            if (_previewVisible)
            {
                if (!CanHandleActivePreviewShortcutFromForeground())
                {
                    return false;
                }

                // A picker filename field or button may gain focus while the
                // preview remains visible. Space must type/click there, not
                // dismiss a glance the user is no longer steering.
                if (_lastSelectionSource == SelectionSource.CommonFileDialog
                    && GetForegroundRoot() == _previewOwnerRootHwnd
                    && !CommonFileDialogSelectionService.HasFileListFocus(_previewOwnerRootHwnd))
                {
                    return false;
                }

                // Space keeps the core promise even after a click into the
                // content: the glance is over. Media stays the exception
                // (play/pause, toggled by the host so the player's controls
                // keep their own focus), and an armed find bar keeps its
                // literal space. Without this, the embedded viewers turned
                // Space into a page-scroll and the preview stopped feeling
                // like Quick Look.
                var previewHwnd = _previewWindow?.Hwnd ?? IntPtr.Zero;
                if (previewHwnd != IntPtr.Zero && NativeMethods.GetForegroundWindow() == previewHwnd)
                {
                    if (_previewWindow?.TryToggleMediaPlayback() == true)
                    {
                        return true;
                    }

                    if (_previewWindow?.IsEscapeYieldArmed == true)
                    {
                        return false;
                    }

                    DiagnosticLog.Write("space -> hide (content engaged)");
                    _dispatcher.BeginInvoke(HidePreview);
                    return true;
                }

                DiagnosticLog.Write("space -> hide");
                _dispatcher.BeginInvoke(HidePreview);
                return true;
            }

            var context = ExplorerSelectionService.TryGetForegroundContext(ignoreTextEditing: true);
            if (context is null)
            {
                QueueForegroundElevationMismatchCheck();
                return false;
            }

            DiagnosticLog.Write($"space -> queued selection lookup class={context.ClassName} hwnd=0x{context.Hwnd.ToInt64():X}");
            var requestGeneration = Interlocked.Increment(ref _openRequestGeneration);
            if (context.IsFileDialog)
            {
                QueueElevationMismatchCheck(context.Hwnd);
                if (!_commonFileDialogMonitor.TryGetCachedSelection(context.Hwnd, out var cachedSelection)
                    || cachedSelection is null)
                {
                    // Fail open. The hook never waits for UI Automation and
                    // never swallows Space on an unverified dialog selection.
                    // Remember this one physical press briefly; a completed,
                    // still-owned refresh may fulfill it off the hook lane.
                    ArmPendingFileDialogPreview(context.Hwnd, requestGeneration);
                    _commonFileDialogMonitor.RequestRefresh(context.Hwnd, settleMilliseconds: 0);
                    DiagnosticLog.Write("space -> file dialog selection not ready, deferred and passed through");
                    return false;
                }

                ClearPendingFileDialogPreview();
                _dispatcher.BeginInvoke(() =>
                    ShowCommonFileDialogPreview(context, cachedSelection, requestGeneration));
                return true;
            }

            _dispatcher.BeginInvoke(() => ShowPreviewFromContext(context, requestGeneration));
            return true;
        }

        return false;
    }

    private void OnKeyUp(int vk)
    {
        _spaceKey.Release(vk);
        var refreshToken = (uint)vk < (uint)_navigationKeyRefreshTokens.Length
            ? Interlocked.Exchange(ref _navigationKeyRefreshTokens[vk], 0)
            : 0;

        if (_fullscreenInputSuspension.ShouldPassThroughInput())
        {
            return;
        }

        if (!_previewVisible
            || refreshToken == 0
            || IsPinnedWindowForeground()
            || !CanHandleActivePreviewShortcutFromForeground())
        {
            return;
        }

        // Most Explorer views commit on key-down. A second COM read on every
        // key-up used to duplicate every successful lookup. Keep the release
        // verification only for the views that really did not move until the
        // key came up.
        var generation = Math.Abs(refreshToken);
        var request = new SelectionRefreshRequest(
            generation,
            IncludeMultiSelectionPaths: refreshToken < 0);
        var verification = new SelectionReleaseVerification(
            request,
            StartedGenerationAtRelease: Interlocked.Read(ref _lastStartedRefreshGeneration));
        _dispatcher.BeginInvoke(
            () => RequestKeyUpSelectionVerification(verification),
            DispatcherPriority.Input);
    }

    private void QueueMouseDown(int message, int screenX, int screenY)
    {
        _dispatcher.BeginInvoke(
            () => OnMouseDown(message, screenX, screenY),
            DispatcherPriority.Input);
    }

    private void OnMouseDown(int message, int screenX, int screenY)
    {
        _inputTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ClearPendingFileDialogPreview();

        if (_fullscreenInputSuspension.ShouldPassThroughInput())
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
            // shortcuts reach the content the user is interacting with.
            _dispatcher.BeginInvoke(() => target.ActivateForInteraction(screenX, screenY));
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
            _dispatcher.BeginInvoke(HidePreview);
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

            foreach (var pinned in _pinnedWindows)
            {
                if (pinned.Hwnd == hit)
                {
                    return pinned;
                }
            }

            return null;
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

    private bool IsPinnedWindowForeground() => FindForegroundPinnedWindow() is not null;

    private PreviewWindow? FindForegroundPinnedWindow()
    {
        if (_pinnedWindows.Count == 0)
        {
            return null;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        foreach (var pinned in _pinnedWindows)
        {
            if (pinned.Hwnd == foreground)
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
        _pendingOfficePreconvertSelection = null;
        CancelOfficeNeighborPreconversion();
        _lastSelectionSource = SelectionSource.Explorer;
        _dialogOwnerTimer.Stop();
        _previewOwnerRootHwnd = GetForegroundRoot();
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
        SyncMouseHook();
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
        window.SetPinLimitReached(_pinnedWindows.Count >= MaxPinnedPreviews);
        return window;
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

    private void OnPreviewContentReady(PreviewWindow window)
    {
        if (window != _previewWindow
            || !_previewVisible
            || _pendingOfficePreconvertSelection is not { } selection
            || !string.Equals(selection.Path, window.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _pendingOfficePreconvertSelection = null;
        StartOfficeNeighborPreconversion(selection);
    }

    private void OnSelectionItemRequested(PreviewWindow window, int index)
    {
        if (window == _previewWindow)
        {
            ShowMultiSelectionItem(index);
        }
    }

    /// <summary>The slideshow walks the set on a loop: past the last item it starts over.</summary>
    private void OnSlideshowAdvanceRequested(PreviewWindow window)
    {
        if (window != _previewWindow || _multiSelection is not { Count: > 1 } set)
        {
            return;
        }

        ShowMultiSelectionItem((_multiIndex + 1) % set.Count);
    }

    private void OnDismissRequested(PreviewWindow window)
    {
        // Pinned previews outlive every dismissal gesture (opening the file,
        // clicking a folder child, Space, Esc). Only X or unpinning ends them.
        if (window.IsPinned)
        {
            return;
        }

        if (window == _previewWindow)
        {
            HidePreview();
            return;
        }

        // A window that is neither active nor pinned is already retiring; a
        // second retire would stack duplicate close handlers.
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
                // The pinned window leaves the dismissal lifecycle; the next
                // Space press gets a fresh window.
                _previewWindow = null;
                _previewVisible = false;
                _previewOwnerRootHwnd = IntPtr.Zero;
                _lastExplorerHwnd = IntPtr.Zero;
                _lastSelectionSource = SelectionSource.Explorer;
                _dialogOwnerTimer.Stop();
                _multiSelection = null;
                _multiSelectionSelectedCount = 0;
                _multiIndex = 0;
                ClearNavigationRefreshTokens();
                _queuedRefreshRequest = null;
                _pendingKeyUpVerification = null;
            }

            DiagnosticLog.Write($"pinned preview ({_pinnedWindows.Count} of {MaxPinnedPreviews}) {window.CurrentPath}");
        }
        else
        {
            _pinnedWindows.Remove(window);
            window.SetPinned(false);
            window.DetachBrowseContext();
            _previewOwnerRootHwnd = IntPtr.Zero;
            _lastExplorerHwnd = IntPtr.Zero;
            _lastSelectionSource = SelectionSource.Explorer;
            _dialogOwnerTimer.Stop();
            _multiSelection = null;
            _multiSelectionSelectedCount = 0;
            _multiIndex = 0;

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
        SyncMouseHook();
    }

    private void OnPinnedCloseRequested(PreviewWindow window)
    {
        // Esc auto-repeat, a double-tap, or Esc racing the X button can queue
        // this twice for one window; the second pass must not stack another
        // retire (duplicate close handlers, double Close). Remove doubles as
        // the idempotence gate.
        if (!_pinnedWindows.Remove(window))
        {
            return;
        }

        RetireWindow(window);
        UpdatePinCapacity();
        SyncMouseHook();
        DiagnosticLog.Write($"pinned preview closed ({_pinnedWindows.Count} of {MaxPinnedPreviews})");
    }

    private void OnBrowseRequested(PreviewWindow window, PreviewBrowseDirection direction)
    {
        if (window != _previewWindow || !_previewVisible)
        {
            return;
        }

        BrowseRelativeAsync((int)direction);
    }

    private void UpdatePinCapacity()
    {
        var reached = _pinnedWindows.Count >= MaxPinnedPreviews;
        _previewWindow?.SetPinLimitReached(reached);
    }

    /// <summary>Fades a surplus window out, then closes it for real.</summary>
    private static void RetireWindow(PreviewWindow window)
    {
        if (!window.IsVisible)
        {
            window.Close();
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
            window.Dispatcher.BeginInvoke(window.Close);
        }

        window.IsVisibleChanged += CloseOnceHidden;
        window.HidePreview();
    }

    private void ShowPreview(ExplorerSelection selection)
    {
        if (_disposed)
        {
            return;
        }

        _previewWindow ??= CreatePreviewWindow();
        CancelIdleTrim();
        CancelOfficeNeighborPreconversion();
        _lastExplorerHwnd = selection.ExplorerHwnd;
        if (_previewOwnerRootHwnd == IntPtr.Zero)
        {
            _previewOwnerRootHwnd = selection.ExplorerHwnd;
        }

        var fileDialogMode = _lastSelectionSource == SelectionSource.CommonFileDialog;
        DiagnosticLog.Write(
            $"preview requested {selection.Path} hwnd=0x{selection.ExplorerHwnd.ToInt64():X}{(fileDialogMode ? " picker" : string.Empty)}");
        var presented = _previewWindow.ShowPreview(
            selection.Path,
            selection.ExplorerHwnd,
            selection.SelectedCount,
            fileDialogMode,
            Interlocked.Exchange(ref _inputTimestamp, 0));
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
            _pendingOfficePreconvertSelection = null;
            _dialogOwnerTimer.Start();
        }
        else
        {
            ApplySelectionBrowseContext(selection);
            _pendingOfficePreconvertSelection = selection;
            _dialogOwnerTimer.Stop();
        }

        _previewVisible = true;
        SyncMouseHook();
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
    private void ShowMultiSelectionItem(int index)
    {
        if (_multiSelection is not { Count: > 1 } set || _previewWindow is null || !_previewVisible)
        {
            return;
        }

        _multiIndex = Math.Clamp(index, 0, set.Count - 1);
        var path = set[_multiIndex];
        DiagnosticLog.Write($"browse within selection -> {path} ({_multiIndex + 1} of {set.Count})");
        if (!string.Equals(path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase))
        {
            _pendingOfficePreconvertSelection = null;
            CancelOfficeNeighborPreconversion();
            _previewWindow.ShowPreview(path, _lastExplorerHwnd, set.Count, inputTimestamp: Interlocked.Exchange(ref _inputTimestamp, 0));
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
                // trims browse chrome at the ends. Explorer can block that
                // optional IFolderView query for hundreds of milliseconds,
                // so leave position unknown until an explicit browse resolves
                // it without delaying selection changes.
                return source switch
                {
                    SelectionSource.Desktop => ExplorerSelectionService.TryGetDesktopSelection(
                        diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths),
                    SelectionSource.CommonFileDialog => null,
                    _ when hwnd != IntPtr.Zero || !useForegroundWhenHwndMissing =>
                        ExplorerSelectionService.TryGetSelectionFromExplorerWindow(
                        hwnd,
                        diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths),
                    _ => ExplorerSelectionService.TryGetForegroundSelection(
                        ignoreTextEditing: false,
                        diagnostics: diagnostics,
                        includeViewPosition: false,
                        includeMultiSelectionPaths: includeMultiSelectionPaths)
                };
            }
            finally
            {
                diagnostics.MarkStaCompleted();
            }
        });

        diagnostics.WriteIfSlow(origin, selection is not null);
        return selection;
    }

    private async void ShowPreviewFromContext(ForegroundSelectionContext context, int requestGeneration)
    {
        if (_disposed)
        {
            return;
        }

        // COM lookups are too slow for the UI thread, and the tab-identity
        // QueryService only answers on an STA thread.
        var selection = await RunSelectionLookupAsync(
            "open",
            context.Source,
            context.Hwnd,
            includeMultiSelectionPaths: true);

        if (_disposed || requestGeneration != _openRequestGeneration)
        {
            return;
        }

        // If the user moved on while the lookup ran (Alt-Tab, click), popping
        // a topmost preview over the new foreground app is unexpected.
        if (NativeMethods.GetForegroundWindow() != context.Hwnd)
        {
            DiagnosticLog.Write("queued lookup -> foreground changed, dropping");
            return;
        }

        var usedRetry = false;
        if (selection is null)
        {
            await Task.Delay(60);
            if (_disposed || requestGeneration != _openRequestGeneration)
            {
                return;
            }

            if (NativeMethods.GetForegroundWindow() != context.Hwnd)
            {
                DiagnosticLog.Write("queued lookup retry -> foreground changed, dropping");
                return;
            }

            selection = await RunSelectionLookupAsync(
                "open-retry",
                context.Source,
                context.Hwnd,
                includeMultiSelectionPaths: true);
            if (selection is null)
            {
                DiagnosticLog.Write($"queued lookup -> no selection class={context.ClassName} hwnd=0x{context.Hwnd.ToInt64():X}");
                return;
            }

            usedRetry = true;
        }

        DiagnosticLog.Write(usedRetry
            ? $"queued lookup retry -> preview {selection.Path}"
            : $"queued lookup -> preview {selection.Path}");
        _lastSelectionSource = context.Source;
        _previewOwnerRootHwnd = context.Hwnd;
        ShowPreview(selection);
    }

    private void ShowCommonFileDialogPreview(
        ForegroundSelectionContext context,
        ExplorerSelection selection,
        int requestGeneration)
    {
        if (_disposed
            || requestGeneration != _openRequestGeneration
            || GetForegroundRoot() != context.Hwnd
            || !CommonFileDialogSelectionService.HasFileListFocus(context.Hwnd))
        {
            DiagnosticLog.Write("queued file dialog preview -> focus changed, dropping");
            return;
        }

        if (!_commonFileDialogMonitor.TryGetCachedSelection(context.Hwnd, out var latestSelection)
            || latestSelection is null)
        {
            DiagnosticLog.Write("queued file dialog preview -> selection changed, dropping");
            return;
        }

        selection = latestSelection;

        _lastSelectionSource = SelectionSource.CommonFileDialog;
        _previewOwnerRootHwnd = context.Hwnd;
        ShowPreview(selection);
    }

    private void ArmPendingFileDialogPreview(IntPtr dialogRoot, int requestGeneration)
    {
        var armedAt = Environment.TickCount64;
        var intent = _pendingFileDialogPreview.Arm(
            dialogRoot,
            requestGeneration,
            armedAt);

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
        ExplorerSelection? selection)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() =>
                OnCommonFileDialogRefreshCompleted(dialogRoot, selection));
            return;
        }

        var intent = _pendingFileDialogPreview.Current;
        if (intent is null)
        {
            return;
        }

        var now = Environment.TickCount64;
        var ownershipValid = !_disposed
            && _inputHooksActive
            && !_fullscreenInputSuspension.ShouldPassThroughInput()
            && GetForegroundRoot() == dialogRoot
            && CommonFileDialogSelectionService.HasFileListFocus(dialogRoot);

        // A newer WinEvent may have started another refresh between this
        // completion and our dispatcher callback. Keep the intent armed; the
        // next completion either fulfills it or the deadline expires it.
        ExplorerSelection? latestSelection = null;
        var cacheReady = selection is not null
            && ownershipValid
            && _commonFileDialogMonitor.TryGetCachedSelection(
                dialogRoot,
                out latestSelection)
            && latestSelection is not null;
        var decision = _pendingFileDialogPreview.EvaluateRefresh(
            intent,
            dialogRoot,
            Volatile.Read(ref _openRequestGeneration),
            now,
            DeferredFileDialogPreviewLifetimeMilliseconds,
            ownershipValid,
            selectionAvailable: selection is not null,
            cacheReady);
        if (decision is DeferredFileDialogPreviewDecision.Ignore
            or DeferredFileDialogPreviewDecision.Wait)
        {
            return;
        }

        if (decision == DeferredFileDialogPreviewDecision.Drop)
        {
            DiagnosticLog.Write("deferred file dialog preview -> ownership or request changed, dropping");
            return;
        }

        if (latestSelection is null)
        {
            return;
        }

        var elapsed = Math.Max(0, now - intent.ArmedAtTick);
        DiagnosticLog.Write($"deferred file dialog preview -> ready +{elapsed}ms");
        var context = new ForegroundSelectionContext(
            dialogRoot,
            SelectionSource.CommonFileDialog,
            CommonFileDialogSelectionService.DialogWindowClass);
        ShowCommonFileDialogPreview(context, latestSelection, intent.RequestGeneration);
    }

    private void ClearPendingFileDialogPreview() => _pendingFileDialogPreview.Cancel();

    private void QueueForegroundElevationMismatchCheck()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var root = foreground == IntPtr.Zero
            ? IntPtr.Zero
            : NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = foreground;
        }

        if (CommonFileDialogSelectionService.HasDialogWindowClass(root))
        {
            QueueElevationMismatchCheck(root);
        }
    }

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

        // Capture identity on the hook lane, then leave immediately. The old
        // dispatcher hop let the preview open first; its later foreground
        // recheck could silently abandon the warning it was meant to show.
        var processId = NativeMethods.GetWindowProcessId(dialogRoot);
        StartElevationMismatchCheck(dialogRoot, processId);
    }

    private void OnCommonFileDialogObserved(IntPtr dialogRoot)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnCommonFileDialogObserved(dialogRoot));
            return;
        }

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

                var detected = ProcessIntegrityService.TryGetPreviewAccessWarning(
                    dialogRoot,
                    out var warning);
                if (detected && warning is not null)
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
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnCommonFileDialogSelectionChanged(dialogRoot, selection));
            return;
        }

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

        DiagnosticLog.Write($"file dialog selection changed -> {selection.Path}");
        ShowPreview(selection);
    }

    private void VerifyCommonFileDialogOwner()
    {
        if (_disposed
            || !_previewVisible
            || _lastSelectionSource != SelectionSource.CommonFileDialog)
        {
            _dialogOwnerTimer.Stop();
            return;
        }

        var owner = _previewOwnerRootHwnd;
        if (owner == IntPtr.Zero || !NativeMethods.IsWindow(owner))
        {
            DiagnosticLog.Write("file dialog closed -> hide");
            HidePreview();
            return;
        }

        var foregroundRoot = GetForegroundRoot();
        if (foregroundRoot == owner
            || _previewWindow?.OwnsForegroundRoot(foregroundRoot) == true)
        {
            return;
        }

        DiagnosticLog.Write("file dialog lost foreground -> hide");
        HidePreview();
    }

    private void HidePreviewAndRestoreOwner(IntPtr owner)
    {
        HidePreview();
        if (owner != IntPtr.Zero && NativeMethods.IsWindow(owner))
        {
            NativeMethods.SetForegroundWindow(owner);
        }
    }

    private void HidePreview()
    {
        ClearPendingFileDialogPreview();
        _previewVisible = false;
        _dialogOwnerTimer.Stop();
        ClearNavigationRefreshTokens();
        _queuedRefreshRequest = null;
        _pendingKeyUpVerification = null;
        _previewOwnerRootHwnd = IntPtr.Zero;
        _lastSelectionSource = SelectionSource.Explorer;
        _multiSelection = null;
        _multiSelectionSelectedCount = 0;
        _pendingOfficePreconvertSelection = null;
        CancelOfficeNeighborPreconversion();
        DiagnosticLog.Write("hide preview");
        _previewWindow?.HidePreview();
        SyncMouseHook();
        ScheduleIdleTrim();
    }

    private void ClearNavigationRefreshTokens()
    {
        for (var i = 0; i < _navigationKeyRefreshTokens.Length; i++)
        {
            Interlocked.Exchange(ref _navigationKeyRefreshTokens[i], 0);
        }
    }

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
                Interlocked.Read(ref _navigationRefreshGeneration),
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
                Interlocked.Exchange(
                    ref _lastStartedRefreshGeneration,
                    activeRequest.Generation);
                var selection = await RunSelectionLookupAsync(
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
                if (decision.Action == RefreshSelectionAction.HidePreview)
                {
                    DiagnosticLog.Write("selection cleared -> hide");
                    HidePreview();
                    return;
                }

                if (decision.Action == RefreshSelectionAction.KeepCurrent)
                {
                    continue;
                }

                selection = decision.Selection;
                if (selection is null)
                {
                    return;
                }

                _lastExplorerHwnd = selection.ExplorerHwnd;
                if (!string.Equals(selection.Path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    _lastChangedRefreshGeneration = Math.Max(
                        _lastChangedRefreshGeneration,
                        activeRequest.Generation);
                    DiagnosticLog.Write($"selection changed -> {selection.Path}");
                    ShowPreview(selection);
                    continue;
                }

                ApplySelectionBrowseContext(selection);
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
                    Interlocked.Read(ref _navigationRefreshGeneration),
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

    private bool CanHandleActivePreviewShortcutFromForeground()
    {
        if (!_previewVisible)
        {
            return false;
        }

        return PreviewInputPolicy.IsPreviewShortcutForeground(
            GetForegroundRoot(),
            _previewWindow?.Hwnd ?? IntPtr.Zero,
            _previewOwnerRootHwnd);
    }

    private bool IsForegroundActivePreview()
    {
        var previewHwnd = _previewWindow?.Hwnd ?? IntPtr.Zero;
        return previewHwnd != IntPtr.Zero && GetForegroundRoot() == previewHwnd;
    }

    /// <summary>
    /// Left and right browse the previous or next item while a preview is
    /// open. Explorer left/right behavior depends on view mode, so the hook
    /// moves selection through Explorer's view object. Up/down stay native
    /// while Explorer has focus; when the preview has focus, they browse unless
    /// the active content needs them for scrolling, media, text, or rename.
    /// </summary>
    private bool TryBrowseFromArrow(int vk)
    {
        if (_lastSelectionSource == SelectionSource.CommonFileDialog)
        {
            return false;
        }

        var vertical = vk is NativeMethods.VK_UP or NativeMethods.VK_DOWN;
        var horizontal = vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT;
        if ((!vertical && !horizontal) || !NoModifierKeysDown())
        {
            return false;
        }

        // The index sheet captures all four arrows while it is up; the
        // preview behind it and Explorer's selection both hold still.
        if (_previewWindow?.IsGridViewOpen == true)
        {
            var deltaColumns = vk switch
            {
                NativeMethods.VK_LEFT => -1,
                NativeMethods.VK_RIGHT => 1,
                _ => 0
            };
            var deltaRows = vk switch
            {
                NativeMethods.VK_UP => -1,
                NativeMethods.VK_DOWN => 1,
                _ => 0
            };
            _dispatcher.BeginInvoke(() => _previewWindow?.MoveGridCursor(deltaColumns, deltaRows));
            return true;
        }

        var previewForeground = IsForegroundActivePreview();
        if (vertical && !previewForeground)
        {
            return false;
        }

        if (previewForeground)
        {
            if (_previewWindow?.ShouldBrowseWithPreviewArrowKeys(vertical) != true)
            {
                return false;
            }
        }
        else if (ExplorerSelectionService.IsWindowTextEditing(_previewOwnerRootHwnd))
        {
            // Renaming in Explorer: the caret handles the arrows.
            return false;
        }

        var delta = vk is NativeMethods.VK_LEFT or NativeMethods.VK_UP ? -1 : 1;
        _dispatcher.BeginInvoke(() => BrowseRelativeAsync(delta));
        return true;
    }

    /// <summary>
    /// Steps the Explorer selection by <paramref name="delta"/> view
    /// positions and previews what lands. Coalesced like selection refresh:
    /// holding the key accumulates steps instead of queueing a COM call per
    /// key repeat.
    /// </summary>
    private async void BrowseRelativeAsync(int delta)
    {
        if (_disposed
            || !_previewVisible
            || _previewWindow is null
            || _lastSelectionSource == SelectionSource.CommonFileDialog)
        {
            return;
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
                _browsePendingDelta = 0;

                // Within a multi-selection, stepping is instant
                // and local: no shell round-trip, no selection change.
                if (_multiSelection is { Count: > 1 })
                {
                    ShowMultiSelectionItem(_multiIndex + step);
                    continue;
                }

                var target = _previewWindow;
                var source = _lastSelectionSource;
                var ownerRoot = _previewOwnerRootHwnd;
                var wasDesktop = source == SelectionSource.Desktop;
                var hwnd = _lastExplorerHwnd;
                var selection = await StaTask.Run(() =>
                    ExplorerSelectionService.TryBrowseRelative(hwnd, wasDesktop, step));

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

                DiagnosticLog.Write($"browse {step:+0;-0} -> {selection.Path} ({selection.ViewIndex + 1} of {selection.ViewCount})");
                if (!string.Equals(selection.Path, _previewWindow.CurrentPath, StringComparison.OrdinalIgnoreCase))
                {
                    ShowPreview(selection);
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
        }
    }

    private static IntPtr GetForegroundRoot()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(foreground, NativeMethods.GA_ROOT);
        return root == IntPtr.Zero ? foreground : root;
    }

    private static bool NoModifierKeysDown()
    {
        return !NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT)
            && !NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL)
            && !NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
            && !NativeMethods.IsKeyDown(NativeMethods.VK_LWIN)
            && !NativeMethods.IsKeyDown(NativeMethods.VK_RWIN);
    }

    private static bool IntentionalMultiSelectionNavigation(int vk)
    {
        if (vk == NativeMethods.VK_DELETE
            || NativeMethods.IsKeyDown(NativeMethods.VK_MENU)
            || NativeMethods.IsKeyDown(NativeMethods.VK_LWIN)
            || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN))
        {
            return false;
        }

        // Ctrl+navigation moves Explorer's focus without changing its selected
        // set. Shift is the modifier which actually grows or shrinks a range;
        // Ctrl+Shift remains covered because Shift is down.
        return NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _idleTrimTimer.Stop();
        _dialogOwnerTimer.Stop();
        CancelOfficeNeighborPreconversion();
        _commonFileDialogMonitor.SelectionChanged -= OnCommonFileDialogSelectionChanged;
        _commonFileDialogMonitor.RefreshCompleted -= OnCommonFileDialogRefreshCompleted;
        _commonFileDialogMonitor.DialogObserved -= OnCommonFileDialogObserved;
        ClearPendingFileDialogPreview();
        _commonFileDialogMonitor.Dispose();
        _fullscreenInputSuspension.SuspensionChanged -= OnFullscreenSuspensionChanged;
        _fullscreenInputSuspension.Dispose();
        _keyboardHook.Dispose();
        _mouseHook.Dispose();
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
        if (_disposed || _pinnedWindows.Count > 0)
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
        if (_disposed || _previewVisible || _pinnedWindows.Count > 0)
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
                window.Close();
            }
            catch
            {
                // A window already inside teardown should not block trimming.
            }

            _previewWindow = null;
        }

        _previewOwnerRootHwnd = IntPtr.Zero;
        _lastExplorerHwnd = IntPtr.Zero;
        _lastSelectionSource = SelectionSource.Explorer;
        _multiSelection = null;
        _multiSelectionSelectedCount = 0;
        _browsePendingDelta = 0;
        _queuedRefreshRequest = null;
        _pendingKeyUpVerification = null;
        _pendingOfficePreconvertSelection = null;
        CancelOfficeNeighborPreconversion();

        if (Interlocked.Exchange(ref _idleTrimInFlight, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
                var trimmed = NativeMethods.TrimCurrentProcessWorkingSet();
                var after = GC.GetTotalMemory(forceFullCollection: false);
                DiagnosticLog.Write($"idle trim complete managed {before / 1024 / 1024:0}MB -> {after / 1024 / 1024:0}MB workingSetTrim={trimmed}");
            }
            finally
            {
                Volatile.Write(ref _idleTrimInFlight, 0);
            }
        });
    }

    private void StartOfficeNeighborPreconversion(ExplorerSelection selection)
    {
        if (_disposed
            || _lastSelectionSource == SelectionSource.CommonFileDialog
            || _multiSelection is not null
            || selection.ViewIndex < 0
            || selection.ViewCount <= 1
            || selection.ExplorerHwnd == IntPtr.Zero)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _officeNeighborPreconvertCts = cts;
        _ = PreconvertOfficeNeighborsAsync(
            selection.ExplorerHwnd,
            _lastSelectionSource == SelectionSource.Desktop,
            selection.ViewIndex,
            cts.Token);
    }

    private async Task PreconvertOfficeNeighborsAsync(IntPtr explorerHwnd, bool wasDesktop, int viewIndex, CancellationToken token)
    {
        try
        {
            await Task.Delay(OfficeNeighborPreconvertDelay, token);
            token.ThrowIfCancellationRequested();
            DiagnosticLog.Write("office neighbor scan");
            var neighbors = await StaTask.Run(() =>
                ExplorerSelectionService.TryGetNeighborPaths(explorerHwnd, wasDesktop, viewIndex, OfficeNeighborPreconvertLimit));
            if (neighbors is null)
            {
                return;
            }

            foreach (var path in neighbors)
            {
                token.ThrowIfCancellationRequested();
                if (!OfficeOpenXmlPreviewLoader.IsSupported(path))
                {
                    continue;
                }

                DiagnosticLog.Write($"office neighbor preconvert {path}");
                var converted = await Task.Run(() => OfficeDocumentPdfConverter.TryConvert(path, token, background: true), token);
                DiagnosticLog.Write(converted is null
                    ? $"office neighbor preconvert skipped {path}"
                    : $"office neighbor preconvert ready {path}");
            }
        }
        catch (OperationCanceledException)
        {
            // A newer preview or dismissal owns the lane now.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office neighbor preconvert failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CancelOfficeNeighborPreconversion()
    {
        var cts = Interlocked.Exchange(ref _officeNeighborPreconvertCts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        finally
        {
            cts.Dispose();
        }
    }
}
