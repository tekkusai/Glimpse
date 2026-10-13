using System.Windows.Automation;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Explorer's header is one window: the tabs, the address bar, the search
/// box and the buttons share it, and Windows cannot say which of them has
/// the keyboard. UI Automation can, in about a millisecond, which is a
/// thousand times what a key may wait. So the header is asked here, off
/// every key's path, while an Explorer window is in front, and the hook
/// thread reads the last answer.
///
/// The one answer the hook thread acts on is "nothing here takes text",
/// read twice with no key between. Anything else is typing. A Space kept
/// from a preview costs a second press; a Space kept from a search box
/// costs the user their text.
/// </summary>
internal sealed class ExplorerHeaderFocus : IDisposable
{
    internal const int ReadsToTrust = 2;

    // One read takes 0.9 ms, measured on build 26300. A header left alone
    // is asked less often.
    internal const int IntervalMilliseconds = 50;
    internal const int RestingIntervalMilliseconds = 400;
    private const int RestAfterReads = 20;

    // A watcher that stalls says nothing: its last answer runs out.
    internal const long FreshMilliseconds = 1000;

    // A Tab pressed outside the header may be carrying the focus into it.
    internal const long WalkMilliseconds = 500;

    private static readonly CacheRequest Request = BuildRequest();

    private readonly Func<IntPtr> _foregroundRoot;
    private readonly Func<IntPtr, bool> _inHeader;
    private readonly Func<IntPtr, bool> _readsPlain;
    private readonly Timer? _timer;

    private nint _root;
    private nint _plainRoot;
    private long _plainAt;

    // The hook thread's: keys that reached the app, and the last Tab.
    private int _keys;
    private long _walkedAt;
    private int _walking;

    // The watcher's own.
    private int _reads;
    private int _readsKeys;
    private nint _readsRoot;
    private int _disposed;

    public ExplorerHeaderFocus()
        : this(NativeMethods.GetForegroundRoot, InHeader, ReadsPlain)
    {
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal ExplorerHeaderFocus(
        Func<IntPtr> foregroundRoot,
        Func<IntPtr, bool> inHeader,
        Func<IntPtr, bool> readsPlain)
    {
        _foregroundRoot = foregroundRoot;
        _inHeader = inHeader;
        _readsPlain = readsPlain;
    }

    /// <summary>The Explorer window in front, or none. Nothing is asked of any other window.</summary>
    public void Watch(IntPtr root)
    {
        if (Volatile.Read(ref _root) == root)
        {
            return;
        }

        Volatile.Write(ref _root, root);
        Volatile.Write(ref _plainRoot, 0);
        Arm(root == IntPtr.Zero ? Timeout.Infinite : IntervalMilliseconds);
    }

    /// <summary>
    /// Hook thread. This window's header was read, twice and lately, with
    /// the keyboard on something that takes no text.
    /// </summary>
    public bool IsPlain(IntPtr root) =>
        root != IntPtr.Zero
        && Volatile.Read(ref _plainRoot) == root
        && Environment.TickCount64 - Volatile.Read(ref _plainAt) <= FreshMilliseconds;

    /// <summary>
    /// Hook thread. A key reached the app in front and may have moved the
    /// focus there, so what was read is no longer known. A bare Tab or F6
    /// <paramref name="walks"/> the focus through the header: someone using
    /// the header by keyboard means its buttons, and Space is theirs until
    /// the focus leaves.
    /// </summary>
    public void KeyWentBy(bool walks)
    {
        if (walks)
        {
            Volatile.Write(ref _walkedAt, Environment.TickCount64);
            Volatile.Write(ref _walking, 1);
        }

        Volatile.Write(ref _keys, _keys + 1);
        Volatile.Write(ref _plainRoot, 0);
    }

    /// <summary>One look at the header. Returns the wait before the next.</summary>
    internal int Tick(long now)
    {
        var root = Volatile.Read(ref _root);
        var keys = Volatile.Read(ref _keys);
        if (root == IntPtr.Zero || _foregroundRoot() != root || !_inHeader(root))
        {
            // Out of the header a walk is over, once the Tab that may be
            // carrying the focus in has had its time.
            if (now - Volatile.Read(ref _walkedAt) > WalkMilliseconds)
            {
                Volatile.Write(ref _walking, 0);
            }

            Forget();
            return IntervalMilliseconds;
        }

        if (Volatile.Read(ref _walking) != 0
            || !_readsPlain(root)
            || keys != Volatile.Read(ref _keys))
        {
            Forget();
            return IntervalMilliseconds;
        }

        // Two reads count together only of one window, with no key between.
        if (keys != _readsKeys || root != _readsRoot)
        {
            _reads = 0;
            _readsKeys = keys;
            _readsRoot = root;
        }

        if (++_reads < ReadsToTrust)
        {
            return IntervalMilliseconds;
        }

        Volatile.Write(ref _plainAt, now);
        Volatile.Write(ref _plainRoot, root);

        // A key that went by as this was published wrote its zero first.
        if (keys != Volatile.Read(ref _keys))
        {
            Forget();
            return IntervalMilliseconds;
        }

        return _reads >= RestAfterReads ? RestingIntervalMilliseconds : IntervalMilliseconds;
    }

    /// <summary>
    /// What takes no text in the header: its buttons, a breadcrumb, a tab,
    /// and the header with nothing in it focused (where Esc leaves a search
    /// box). A menu is not here: its keys are its own. Anything not named
    /// is typing.
    /// </summary>
    internal static bool TakesNoText(ControlType type, string? className, bool takesText) =>
        !takesText
        && (type == ControlType.Button
            || type == ControlType.SplitButton
            || type == ControlType.TabItem
            || (type == ControlType.Pane
                && string.Equals(className, "InputSiteWindowClass", StringComparison.Ordinal)));

    private void Forget()
    {
        _reads = 0;
        Volatile.Write(ref _plainRoot, 0);
    }

    private void OnTimer()
    {
        var wait = Tick(Environment.TickCount64);
        if (Volatile.Read(ref _root) != IntPtr.Zero)
        {
            Arm(wait);
        }
    }

    private void Arm(int milliseconds)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        try
        {
            _timer?.Change(milliseconds, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the call.
        }
    }

    private static bool InHeader(IntPtr root)
    {
        ExplorerSelectionService.IsTyping(root, out var header);
        return header;
    }

    private static CacheRequest BuildRequest()
    {
        var request = new CacheRequest();
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.ClassNameProperty);
        request.Add(AutomationElement.ProcessIdProperty);
        request.Add(AutomationElement.IsTextPatternAvailableProperty);
        return request;
    }

    // The focus could move to another app between the check and the read.
    // The process is checked, and any refusal is typing.
    private static bool ReadsPlain(IntPtr root)
    {
        try
        {
            AutomationElement focused;
            using (Request.Activate())
            {
                focused = AutomationElement.FocusedElement;
            }

            var cached = focused.Cached;
            return (uint)cached.ProcessId == NativeMethods.GetWindowProcessId(root)
                && TakesNoText(
                    cached.ControlType,
                    cached.ClassName,
                    (bool)focused.GetCachedPropertyValue(AutomationElement.IsTextPatternAvailableProperty));
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _root, 0);
        Volatile.Write(ref _plainRoot, 0);
        _timer?.Dispose();
    }
}
