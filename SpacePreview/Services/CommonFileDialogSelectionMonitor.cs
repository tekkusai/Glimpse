using System.Windows.Threading;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Keeps a verified dialog selection ready before Space is pressed. The
/// keyboard hook never performs UI Automation or filesystem work; if this
/// cache is missing or being refreshed, the dialog receives Space normally.
/// The controller reports foreground changes. A dialog in front is a file
/// picker or it is nothing to Glimpse, and while it is nothing, the one
/// thing that could make it a picker is listened for: its thread creating
/// the frame or the shell view. Focus and selection events are watched only
/// in a picker's own process, only while it is in front, and nothing here
/// runs on a clock.
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
        ExplorerSelection? Selection)
    {
        public static readonly DialogCache Empty = new(IntPtr.Zero, IntPtr.Zero, null);
    }

    private const int SelectionSettleMilliseconds = 35;
    private const uint EventObjectCreate = 0x8000;
    private const int ObjectIdWindow = 0;

    private readonly Dispatcher _dispatcher;

    // Tests stand in for the picker's own answer and shorten the wait for it.
    private readonly Func<IntPtr, ExplorerSelection?> _lookUp = CommonFileDialogSelectionService.TryGetSelection;
    private readonly TimeSpan _lookupTimeout = TimeSpan.FromSeconds(2);
    private readonly NativeMethods.WinEventProc _objectChanged;
    private readonly NativeMethods.WinEventProc _windowCreated;

    // A dialog is asked one thing at a time, and one that did not answer in
    // time is not asked again: a provider that hangs holds one worker.
    private readonly Dictionary<IntPtr, Task> _lookups = [];
    private readonly HashSet<IntPtr> _unanswering = [];
    private IntPtr _objectHook;
    private uint _objectHookProcessId;
    private IntPtr _verifiedDialog;
    private IntPtr _buildHook;
    private IntPtr _unbuiltDialog;
    private DialogCache _cache = DialogCache.Empty;
    private long _refreshGeneration;
    private bool _disposed;

    public CommonFileDialogSelectionMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _objectChanged = OnObjectChanged;
        _windowCreated = OnWindowCreated;
    }

    public event Action<IntPtr, ExplorerSelection?>? SelectionChanged;

    /// <summary>A read of the picker is back: the dialog, what is selected, and the read's number.</summary>
    public event Action<IntPtr, ExplorerSelection?, long>? RefreshCompleted;

    /// <summary>A file picker is in front and confirmed.</summary>
    public event Action<IntPtr>? DialogObserved;

    /// <summary>A dialog in front that was no picker has built a frame or a view: look at it now.</summary>
    public event Action<IntPtr>? DialogBuilt;

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

    /// <summary>
    /// Asks for the picker to be read again. Returns the number of this
    /// read, which <see cref="RefreshCompleted"/> names when its answer is
    /// back; 0 when the dialog is not read at all.
    /// </summary>
    public long RequestRefresh(IntPtr dialogRoot, int settleMilliseconds = SelectionSettleMilliseconds)
    {
        if (_disposed
            || dialogRoot == IntPtr.Zero
            || _unanswering.Contains(dialogRoot)
            || !CommonFileDialogSelectionService.IsFilePicker(dialogRoot))
        {
            return 0;
        }

        var generation = Interlocked.Increment(ref _refreshGeneration);
        Volatile.Write(ref _cache, _cache with { Pending = dialogRoot });

        _dispatcher.BeginInvoke(
            () => _ = RefreshAsync(dialogRoot, generation, Math.Max(0, settleMilliseconds)),
            DispatcherPriority.Background);
        return generation;
    }

    /// <summary>
    /// The number of the read under way for this dialog; 0 when none is.
    /// It was asked for at the picker's last move and nothing has been heard
    /// from the picker since, so its answer is what is selected now.
    /// </summary>
    public long PendingRefresh(IntPtr dialogRoot) =>
        dialogRoot != IntPtr.Zero && _cache.Pending == dialogRoot
            ? Interlocked.Read(ref _refreshGeneration)
            : 0;

    private async Task RefreshAsync(IntPtr dialogRoot, long generation, int settleMilliseconds)
    {
        if (settleMilliseconds > 0)
        {
            await Task.Delay(settleMilliseconds);
        }

        // One question per dialog at a time. The answer that is out was
        // asked before this request, so it is waited for and not used: the
        // newest request asks again.
        if (generation == Interlocked.Read(ref _refreshGeneration)
            && _lookups.TryGetValue(dialogRoot, out var answered))
        {
            await answered;
        }

        if (_disposed
            || generation != Interlocked.Read(ref _refreshGeneration)
            || _unanswering.Contains(dialogRoot))
        {
            return;
        }

        var back = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _lookups[dialogRoot] = back.Task;
        ExplorerSelection? selection;
        try
        {
            // UI Automation providers live in another process and can become
            // unresponsive during teardown. Keep them away from the shell STA
            // used by Explorer and stop querying this dialog after one bounded
            // timeout; Space then remains native for the rest of its lifetime.
            selection = await Task.Run(() => _lookUp(dialogRoot)).WaitAsync(_lookupTimeout);
        }
        catch (TimeoutException)
        {
            GiveUpOn(dialogRoot);
            return;
        }
        finally
        {
            _lookups.Remove(dialogRoot);
            back.SetResult();
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

        RefreshCompleted?.Invoke(dialogRoot, selection, generation);
    }

    // The wait counts against the dialog, whatever has been asked of it
    // since. A glance over it would go on showing a file the user has left,
    // so the picker is reported as having nothing selected.
    private void GiveUpOn(IntPtr dialogRoot)
    {
        _unanswering.RemoveWhere(dialog => !NativeMethods.IsWindow(dialog));
        _unanswering.Add(dialogRoot);
        DiagnosticLog.Write($"file dialog selection timed out hwnd=0x{dialogRoot.ToInt64():X}; disabled for dialog");

        var cache = _cache;
        if (_disposed || (cache.Cached != dialogRoot && cache.Pending != dialogRoot))
        {
            return;
        }

        var hadSelection = cache.Cached == dialogRoot && cache.Selection is not null;
        Volatile.Write(ref _cache, new DialogCache(dialogRoot, IntPtr.Zero, null));
        if (hadSelection)
        {
            SelectionChanged?.Invoke(dialogRoot, null);
        }
    }

    /// <summary>Called for every foreground root outside this process.</summary>
    public void ObserveForeground(IntPtr root)
    {
        if (_disposed)
        {
            return;
        }

        // Whatever was listened to, the foreground has moved on or this is it.
        StopWatchingBuild();
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

        // #32770 is also a message box, an installer page, a whole app, and
        // an Open dialog that has not built its frame yet. A picker takes
        // the foreground before it builds one, and in what order it then
        // shows itself is the app's own code: a dialog can be on screen
        // before it has a frame. So a dialog that is no picker is listened
        // to for as long as it stays in front, for the frame or the view
        // alone, and nothing looks at it again without one.
        _verifiedDialog = IntPtr.Zero;
        if (!CommonFileDialogSelectionService.IsFilePicker(root))
        {
            WatchBuild(root);

            // Built between the look and the hook: no event is coming.
            if (!CommonFileDialogSelectionService.IsFilePicker(root))
            {
                UnwatchDialogProcess();
                return;
            }

            StopWatchingBuild();
        }

        _verifiedDialog = root;
        WatchDialogProcess(root);
        DialogObserved?.Invoke(root);
        RequestRefresh(root, settleMilliseconds: 0);
    }

    private void WatchBuild(IntPtr root)
    {
        // No thread means no window; with neither named the hook would
        // hear every window on the PC.
        var threadId = NativeMethods.GetWindowThreadProcessId(root, out var processId);
        if (threadId == 0)
        {
            return;
        }

        _buildHook = NativeMethods.SetWinEventHook(
            EventObjectCreate,
            EventObjectCreate,
            IntPtr.Zero,
            _windowCreated,
            processId,
            threadId,
            NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (_buildHook == IntPtr.Zero)
        {
            DiagnosticLog.Write("dialog create event unavailable; a file dialog opening now is not served");
            return;
        }

        _unbuiltDialog = root;
    }

    /// <summary>The dialog that was no picker has left the front: nothing listens to it.</summary>
    public void StopWatchingBuild()
    {
        if (_buildHook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWinEvent(_buildHook);
        _buildHook = IntPtr.Zero;
        _unbuiltDialog = IntPtr.Zero;
    }

    // Every window the dialog's thread creates while it is in front and no
    // picker comes through here. Only the two that make it one go further.
    private void OnWindowCreated(
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
            || idObject != ObjectIdWindow)
        {
            return;
        }

        var className = NativeMethods.GetClassNameString(hwnd);
        if (!className.Equals(CommonFileDialogSelectionService.ShellViewWindowClass, StringComparison.OrdinalIgnoreCase)
            && !className.Equals(CommonFileDialogSelectionService.ExplorerFrameWindowClass, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var dialog = _unbuiltDialog;
        if (dialog != IntPtr.Zero && NativeMethods.GetRoot(hwnd) == dialog)
        {
            DialogBuilt?.Invoke(dialog);
        }
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
        StopWatchingBuild();
        UnwatchDialogProcess();
        _verifiedDialog = IntPtr.Zero;
        Volatile.Write(ref _cache, DialogCache.Empty);
    }
}
