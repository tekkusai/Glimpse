using System.Windows.Threading;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Keeps a verified dialog selection ready before Space is pressed. The
/// keyboard hook never performs UI Automation or filesystem work; if this
/// cache is missing or being refreshed, the dialog receives Space normally.
/// The controller reports foreground changes. A dialog is looked at once,
/// when it is on screen: it is a file picker or it is nothing to Glimpse.
/// Focus and selection events are watched only in a picker's own process,
/// only while it is in front, and nothing here runs on a clock.
/// Everything here runs on the dispatcher except
/// <see cref="TryGetCachedSelection"/>, which the keyboard hook thread calls.
/// </summary>
internal sealed class CommonFileDialogSelectionMonitor : IDisposable
{
    /// <summary>
    /// What the hook thread reads. The dispatcher swaps a new one in whole,
    /// so the hook thread takes no lock and never sees half an update.
    /// </summary>
    private sealed record DialogCache(
        IntPtr Cached,
        IntPtr Pending,
        IntPtr Disabled,
        ExplorerSelection? Selection)
    {
        public static readonly DialogCache Empty = new(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null);
    }

    private const int SelectionSettleMilliseconds = 35;
    private const uint EventObjectShow = 0x8002;
    private const int ObjectIdWindow = 0;

    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.WinEventProc _objectChanged;
    private readonly NativeMethods.WinEventProc _windowShown;
    private IntPtr _objectHook;
    private uint _objectHookProcessId;
    private IntPtr _verifiedDialog;
    private IntPtr _shownHook;
    private IntPtr _awaitedDialog;
    private DialogCache _cache = DialogCache.Empty;
    private long _refreshGeneration;
    private bool _disposed;

    public CommonFileDialogSelectionMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _objectChanged = OnObjectChanged;
        _windowShown = OnWindowShown;
    }

    public event Action<IntPtr, ExplorerSelection?>? SelectionChanged;
    public event Action<IntPtr, ExplorerSelection?>? RefreshCompleted;

    /// <summary>A file picker is in front and confirmed.</summary>
    public event Action<IntPtr>? DialogObserved;

    /// <summary>A dialog that came to the front unbuilt has shown itself: look at it now.</summary>
    public event Action<IntPtr>? DialogShown;

    /// <summary>The dialog is the confirmed picker this monitor watches.</summary>
    public bool IsWatching(IntPtr root) => root != IntPtr.Zero && _verifiedDialog == root;

    /// <summary>Safe for the keyboard hook thread: one read of the cache, no lock.</summary>
    public bool TryGetCachedSelection(IntPtr dialogRoot, out ExplorerSelection? selection)
    {
        selection = null;
        if (dialogRoot == IntPtr.Zero || !NativeMethods.IsWindow(dialogRoot))
        {
            return false;
        }

        var cache = Volatile.Read(ref _cache);
        if (cache.Pending == dialogRoot
            || cache.Cached != dialogRoot
            || cache.Selection is null)
        {
            return false;
        }

        selection = cache.Selection;
        return true;
    }

    public void RequestRefresh(IntPtr dialogRoot, int settleMilliseconds = SelectionSettleMilliseconds)
    {
        if (_disposed
            || dialogRoot == IntPtr.Zero
            || !CommonFileDialogSelectionService.IsFilePicker(dialogRoot))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        var cache = _cache;
        if (cache.Disabled == dialogRoot)
        {
            return;
        }

        // Another dialog's timeout no longer applies.
        Volatile.Write(ref _cache, cache with { Disabled = IntPtr.Zero, Pending = dialogRoot });

        _dispatcher.BeginInvoke(
            () => _ = RefreshAsync(dialogRoot, generation, Math.Max(0, settleMilliseconds)),
            DispatcherPriority.Background);
    }

    private async Task RefreshAsync(IntPtr dialogRoot, long generation, int settleMilliseconds)
    {
        if (settleMilliseconds > 0)
        {
            await Task.Delay(settleMilliseconds);
        }

        if (_disposed || generation != Interlocked.Read(ref _refreshGeneration))
        {
            return;
        }

        ExplorerSelection? selection;
        try
        {
            // UI Automation providers live in another process and can become
            // unresponsive during teardown. Keep them away from the shell STA
            // used by Explorer and stop querying this dialog after one bounded
            // timeout; Space then remains native for the rest of its lifetime.
            selection = await Task.Run(() =>
                    CommonFileDialogSelectionService.TryGetSelection(dialogRoot))
                .WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            if (_disposed || generation != Interlocked.Read(ref _refreshGeneration))
            {
                return;
            }

            Volatile.Write(ref _cache, new DialogCache(dialogRoot, IntPtr.Zero, dialogRoot, null));

            DiagnosticLog.Write($"file dialog selection timed out hwnd=0x{dialogRoot.ToInt64():X}; disabled for dialog");
            return;
        }
        if (_disposed || generation != Interlocked.Read(ref _refreshGeneration))
        {
            return;
        }

        var cache = _cache;
        var previous = cache.Cached == dialogRoot ? cache.Selection : null;
        Volatile.Write(ref _cache, cache with { Cached = dialogRoot, Selection = selection, Pending = IntPtr.Zero });

        if (!SelectionsEqual(previous, selection))
        {
            SelectionChanged?.Invoke(dialogRoot, selection);
        }

        RefreshCompleted?.Invoke(dialogRoot, selection);
    }

    /// <summary>Called for every foreground root outside this process.</summary>
    public void ObserveForeground(IntPtr root)
    {
        if (_disposed)
        {
            return;
        }

        // Whatever was waited for, the foreground has moved on or this is it.
        StopAwaitingShow();
        if (!CommonFileDialogSelectionService.HasDialogWindowClass(root))
        {
            _verifiedDialog = IntPtr.Zero;
            UnwatchDialogProcess();
            return;
        }

        if (_verifiedDialog == root)
        {
            return;
        }

        // #32770 is also a message box, an installer page, a whole app. A
        // dialog takes the foreground while it is still hidden, builds its
        // controls, and shows itself when its first work is done: a picker
        // has its frame by then, a dialog without one never gets it. So a
        // dialog is judged once, on screen. One still hidden is waited for
        // by its own show event, and nothing looks at it a second time.
        _verifiedDialog = IntPtr.Zero;
        var picker = CommonFileDialogSelectionService.IsFilePicker(root);
        if (!picker && !NativeMethods.IsWindowVisible(root))
        {
            AwaitShow(root);

            // Shown between the look and the hook: no event is coming.
            if (NativeMethods.IsWindowVisible(root))
            {
                StopAwaitingShow();
                picker = CommonFileDialogSelectionService.IsFilePicker(root);
            }
        }

        if (!picker)
        {
            UnwatchDialogProcess();
            return;
        }

        _verifiedDialog = root;
        WatchDialogProcess(root);
        DialogObserved?.Invoke(root);
        RequestRefresh(root, settleMilliseconds: 0);
    }

    private void AwaitShow(IntPtr root)
    {
        var threadId = NativeMethods.GetWindowThreadProcessId(root, out var processId);
        _shownHook = NativeMethods.SetWinEventHook(
            EventObjectShow,
            EventObjectShow,
            IntPtr.Zero,
            _windowShown,
            processId,
            threadId,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_shownHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("dialog show event unavailable; a file dialog opening now is not served");
            return;
        }

        _awaitedDialog = root;
    }

    private void StopAwaitingShow()
    {
        if (_shownHook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_shownHook);
        _shownHook = IntPtr.Zero;
        _awaitedDialog = IntPtr.Zero;
    }

    private void OnWindowShown(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed
            || hwnd == IntPtr.Zero
            || hwnd != _awaitedDialog
            || idObject != ObjectIdWindow)
        {
            return;
        }

        StopAwaitingShow();
        DialogShown?.Invoke(hwnd);
    }

    private void WatchDialogProcess(IntPtr root)
    {
        var processId = NativeMethods.GetWindowProcessId(root);
        if (processId == 0 || (_objectHook != IntPtr.Zero && _objectHookProcessId == processId))
        {
            return;
        }

        UnwatchDialogProcess();
        _objectHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS,
            NativeMethods.EVENT_OBJECT_SELECTIONWITHIN,
            IntPtr.Zero,
            _objectChanged,
            processId,
            0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        if (_objectHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("file dialog selection events unavailable");
            return;
        }

        _objectHookProcessId = processId;
    }

    private void UnwatchDialogProcess()
    {
        if (_objectHook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_objectHook);
        _objectHook = IntPtr.Zero;
        _objectHookProcessId = 0;
    }

    private void OnObjectChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed
            || hwnd == IntPtr.Zero
            || eventType < NativeMethods.EVENT_OBJECT_FOCUS
            || eventType > NativeMethods.EVENT_OBJECT_SELECTIONWITHIN)
        {
            return;
        }

        // The hook hears the picker's whole process. Only the picker counts.
        var root = NativeMethods.GetRoot(hwnd);
        if (root == _verifiedDialog)
        {
            RequestRefresh(root);
        }
    }

    private static bool SelectionsEqual(ExplorerSelection? left, ExplorerSelection? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null
            || left.ExplorerHwnd != right.ExplorerHwnd
            || left.SelectedCount != right.SelectedCount
            || !string.Equals(left.Path, right.Path, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var leftPaths = left.SelectedPaths;
        var rightPaths = right.SelectedPaths;
        if (leftPaths is null || rightPaths is null)
        {
            return leftPaths is null && rightPaths is null;
        }

        return leftPaths.Count == rightPaths.Count
            && leftPaths.Zip(rightPaths).All(pair =>
                string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Interlocked.Increment(ref _refreshGeneration);
        StopAwaitingShow();
        UnwatchDialogProcess();
        _verifiedDialog = IntPtr.Zero;
        Volatile.Write(ref _cache, DialogCache.Empty);
    }
}
