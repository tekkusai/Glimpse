using System.Windows.Threading;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Keeps a verified dialog selection ready before Space is pressed. The
/// keyboard hook never performs UI Automation or filesystem work; if this
/// cache is missing or being refreshed, the dialog receives Space normally.
/// </summary>
internal sealed class CommonFileDialogSelectionMonitor : IDisposable
{
    private const int SelectionSettleMilliseconds = 35;
    private static readonly TimeSpan ForegroundPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.WinEventProc _foregroundChanged;
    private readonly NativeMethods.WinEventProc _objectChanged;
    private readonly DispatcherTimer _foregroundPoll;
    private readonly object _cacheGate = new();
    private IntPtr _foregroundHook;
    private IntPtr _objectHook;
    private IntPtr _cachedDialog;
    private IntPtr _pendingDialog;
    private IntPtr _disabledDialog;
    private IntPtr _verifiedDialog;
    private ExplorerSelection? _cachedSelection;
    private long _refreshGeneration;
    private bool _disposed;

    public CommonFileDialogSelectionMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _foregroundChanged = OnForegroundChanged;
        _objectChanged = OnObjectChanged;
        _foregroundPoll = new DispatcherTimer(
            ForegroundPollInterval,
            DispatcherPriority.Background,
            OnForegroundPoll,
            dispatcher);
        _foregroundPoll.Stop();
    }

    public event Action<IntPtr, ExplorerSelection?>? SelectionChanged;
    public event Action<IntPtr, ExplorerSelection?>? RefreshCompleted;
    public event Action<IntPtr>? DialogObserved;

    public void Start()
    {
        if (_disposed || _foregroundHook != IntPtr.Zero || _objectHook != IntPtr.Zero)
        {
            return;
        }

        var flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _foregroundChanged,
            0,
            0,
            flags);
        _objectHook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_OBJECT_FOCUS,
            NativeMethods.EVENT_OBJECT_SELECTIONWITHIN,
            IntPtr.Zero,
            _objectChanged,
            0,
            0,
            flags);

        if (_foregroundHook == IntPtr.Zero || _objectHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("file dialog selection monitor partially unavailable");
        }

        ObserveForeground(NativeMethods.GetForegroundWindow());
        _foregroundPoll.Start();
    }

    /// <summary>Safe for the low-level keyboard-hook lane.</summary>
    public bool TryGetCachedSelection(IntPtr dialogRoot, out ExplorerSelection? selection)
    {
        selection = null;
        if (_disposed || dialogRoot == IntPtr.Zero || !NativeMethods.IsWindow(dialogRoot))
        {
            return false;
        }

        lock (_cacheGate)
        {
            if (_pendingDialog == dialogRoot
                || _cachedDialog != dialogRoot
                || _cachedSelection is null)
            {
                return false;
            }

            selection = _cachedSelection;
            return true;
        }
    }

    public void RequestRefresh(IntPtr dialogRoot, int settleMilliseconds = SelectionSettleMilliseconds)
    {
        if (_disposed
            || dialogRoot == IntPtr.Zero
            || !CommonFileDialogSelectionService.IsCommonFileDialogRoot(dialogRoot))
        {
            return;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        lock (_cacheGate)
        {
            if (_disabledDialog == dialogRoot)
            {
                return;
            }

            if (_disabledDialog != IntPtr.Zero && _disabledDialog != dialogRoot)
            {
                _disabledDialog = IntPtr.Zero;
            }

            _pendingDialog = dialogRoot;
        }

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

            lock (_cacheGate)
            {
                _pendingDialog = IntPtr.Zero;
                _disabledDialog = dialogRoot;
                _cachedDialog = dialogRoot;
                _cachedSelection = null;
            }

            DiagnosticLog.Write($"file dialog selection timed out hwnd=0x{dialogRoot.ToInt64():X}; disabled for dialog");
            return;
        }
        if (_disposed || generation != Interlocked.Read(ref _refreshGeneration))
        {
            return;
        }

        ExplorerSelection? previous;
        lock (_cacheGate)
        {
            previous = _cachedDialog == dialogRoot ? _cachedSelection : null;
            _cachedDialog = dialogRoot;
            _cachedSelection = selection;
            _pendingDialog = IntPtr.Zero;
        }

        if (!SelectionsEqual(previous, selection))
        {
            SelectionChanged?.Invoke(dialogRoot, selection);
        }

        RefreshCompleted?.Invoke(dialogRoot, selection);
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (_disposed || eventType != NativeMethods.EVENT_SYSTEM_FOREGROUND)
        {
            return;
        }

        ObserveForeground(hwnd == IntPtr.Zero ? NativeMethods.GetForegroundWindow() : hwnd);
    }

    private void OnForegroundPoll(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ObserveForeground(NativeMethods.GetForegroundWindow());
        }
    }

    private void ObserveForeground(IntPtr hwnd)
    {
        var root = GetRoot(hwnd);
        if (!CommonFileDialogSelectionService.HasDialogWindowClass(root))
        {
            _verifiedDialog = IntPtr.Zero;
            return;
        }

        if (_verifiedDialog == root)
        {
            return;
        }

        // #32770 is also used by installers and uninstallers. It is enough
        // to consider after an explicit Space press, but passive monitoring
        // only caches a dialog after the shell view proves it is a picker.
        _verifiedDialog = IntPtr.Zero;
        if (CommonFileDialogSelectionService.IsCommonFileDialogRoot(root))
        {
            _verifiedDialog = root;
            DialogObserved?.Invoke(root);
            RequestRefresh(root, settleMilliseconds: 0);
        }
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

        var root = GetRoot(hwnd);
        if (CommonFileDialogSelectionService.IsCommonFileDialogRoot(root))
        {
            RequestRefresh(root);
        }
    }

    private static IntPtr GetRoot(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        return root == IntPtr.Zero ? hwnd : root;
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
        _foregroundPoll.Stop();
        if (_foregroundHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_objectHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_objectHook);
            _objectHook = IntPtr.Zero;
        }

        lock (_cacheGate)
        {
            _cachedDialog = IntPtr.Zero;
            _pendingDialog = IntPtr.Zero;
            _disabledDialog = IntPtr.Zero;
            _verifiedDialog = IntPtr.Zero;
            _cachedSelection = null;
        }
    }
}
