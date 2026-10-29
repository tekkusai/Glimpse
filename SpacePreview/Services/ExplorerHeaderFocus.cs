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
/// read twice with no input between. Anything else is typing. A Space kept
/// from a preview costs a second press; a Space kept from a search box
/// costs the user their text.
/// </summary>
internal sealed class ExplorerHeaderFocus : IDisposable
{
    internal const int ReadsToTrust = 2;

    // Mouse buttons invalidate an answer before Windows moves the focus.
    // These reads learn where it landed, away from the input hooks.
    internal const int IntervalMilliseconds = 50;

    // One read takes 0.9 ms here and about 1 ms of Explorer's own CPU:
    // twenty a second cost Explorer 2 % of a core, measured on build 26300.
    // The focus moves when the user does something. Once nothing has been
    // pressed, clicked or moved for this long, and a read made after that
    // still stands, Explorer is not asked again until something is.
    internal const uint QuietMilliseconds = 500;

    // A watcher that stalls says nothing: its last answer runs out.
    internal const long FreshMilliseconds = 250;

    // A Tab pressed outside the header may be carrying the focus into it.
    internal const long WalkMilliseconds = 500;

    private static readonly CacheRequest Request = BuildRequest();

    private readonly Func<IntPtr> _foregroundRoot;
    private readonly Func<IntPtr, bool> _inHeader;
    private readonly Func<IntPtr, bool> _readsPlain;
    private readonly Func<uint> _lastInput;
    private readonly Timer? _timer;

    // What was read, under which input count and which watch. It holds only
    // while both still stand, so no input and no change of window can come
    // between a read and its use.
    private sealed record Answer(nint Root, int Inputs, int Watch);

    private Answer? _answer;
    private long _answerAt;
    private nint _root;
    private int _watch;

    // Both hook threads count changes; only the keyboard records Tab.
    private int _inputs;
    private long _walkedAt;

    // The watcher's own.
    private int _reads;
    private int _readsInputs;
    private int _readsWatch;
    private uint _readInput;
    private bool _readQuiet;
    private bool _readPlain;
    private long _outsideAt;
    private int _looking;
    private int _disposed;

    public ExplorerHeaderFocus()
        : this(NativeMethods.GetForegroundRoot, InHeader, ReadsPlain, NativeMethods.LastInputTick)
    {
        _timer = new Timer(_ => OnTimer(), null, Timeout.Infinite, Timeout.Infinite);
    }

    internal ExplorerHeaderFocus(
        Func<IntPtr> foregroundRoot,
        Func<IntPtr, bool> inHeader,
        Func<IntPtr, bool> readsPlain,
        Func<uint> lastInput)
    {
        _foregroundRoot = foregroundRoot;
        _inHeader = inHeader;
        _readsPlain = readsPlain;
        _lastInput = lastInput;
    }

    /// <summary>The Explorer window in front, or none. Nothing is asked of any other window.</summary>
    public void Watch(IntPtr root)
    {
        if (Volatile.Read(ref _root) == root)
        {
            return;
        }

        // A Tab pressed in another window walked nothing here, and nothing
        // looks while no Explorer window is in front, so it would stand.
        Volatile.Write(ref _walkedAt, 0);
        Volatile.Write(ref _watch, _watch + 1);
        Volatile.Write(ref _root, root);
        Arm(root == IntPtr.Zero ? Timeout.Infinite : IntervalMilliseconds);
    }

    /// <summary>
    /// Hook thread. This window's header was read, twice and lately, with
    /// the keyboard on something that takes no text, and no key or mouse
    /// button has reached the app since.
    /// </summary>
    public bool IsPlain(IntPtr root) =>
        root != IntPtr.Zero
        && Volatile.Read(ref _answer) is { } answer
        && answer.Root == root
        && answer.Inputs == Volatile.Read(ref _inputs)
        && answer.Watch == Volatile.Read(ref _watch)
        && Environment.TickCount64 - Volatile.Read(ref _answerAt) <= FreshMilliseconds;

    /// <summary>
    /// Hook threads. A key or mouse button may move the focus, so what was
    /// read is no longer known. A bare Tab or F6
    /// <paramref name="walks"/> the focus through the header: someone using
    /// the header by keyboard means its buttons, and Space is theirs until
    /// the focus leaves.
    /// </summary>
    public void InputWentBy(bool walks)
    {
        if (walks)
        {
            Volatile.Write(ref _walkedAt, Environment.TickCount64);
        }

        Interlocked.Increment(ref _inputs);
    }

    /// <summary>One look at the header.</summary>
    internal void Tick(long now)
    {
        // A change of window arms the timer while a look may still be out
        // with Explorer. Two never run together.
        if (Interlocked.Exchange(ref _looking, 1) != 0)
        {
            return;
        }

        try
        {
            Look(now);
        }
        finally
        {
            Volatile.Write(ref _looking, 0);
        }
    }

    private void Look(long now)
    {
        var root = Volatile.Read(ref _root);
        var inputs = Volatile.Read(ref _inputs);
        var watch = Volatile.Read(ref _watch);
        var input = _lastInput();
        if (!Facing(root))
        {
            _outsideAt = now;
            _readQuiet = false;
            Forget();
            return;
        }

        // A walk lasts until the focus is seen out of the header, once the
        // Tab that may have been carrying it in has had its time.
        var walkedAt = Volatile.Read(ref _walkedAt);
        if (walkedAt != 0 && _outsideAt <= walkedAt + WalkMilliseconds)
        {
            Forget();
            return;
        }

        // Hands off since a read that was itself made in the quiet: what
        // it saw still holds, and Explorer is left alone.
        if (_readQuiet
            && input == _readInput
            && inputs == _readsInputs
            && watch == _readsWatch
            && (!_readPlain || _reads >= ReadsToTrust))
        {
            if (_readPlain)
            {
                Volatile.Write(ref _answerAt, now);
            }

            return;
        }

        // The read asks for whatever has the focus, anywhere. It counts
        // only if this header still had it afterwards. Input or a change
        // of window during it needs no check here: the answer carries the
        // counts it was started under.
        _readPlain = _readsPlain(root) && Facing(root);
        _readInput = input;
        _readQuiet = unchecked((uint)now - input) > QuietMilliseconds;
        if (inputs != _readsInputs || watch != _readsWatch)
        {
            _reads = 0;
            _readsInputs = inputs;
            _readsWatch = watch;
        }

        if (!_readPlain)
        {
            Forget();
            return;
        }

        if (++_reads >= ReadsToTrust)
        {
            Volatile.Write(ref _answerAt, now);
            Volatile.Write(ref _answer, new Answer(root, inputs, watch));
        }
    }

    // Only a look writes the answer, so one that did not confirm it ends it.
    private void Forget()
    {
        _reads = 0;
        Volatile.Write(ref _answer, null);
    }

    private bool Facing(IntPtr root) =>
        root != IntPtr.Zero && _foregroundRoot() == root && _inHeader(root);

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

    private void OnTimer()
    {
        Tick(Environment.TickCount64);
        if (Volatile.Read(ref _root) != IntPtr.Zero)
        {
            Arm(IntervalMilliseconds);
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

    // Any refusal is typing.
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

        Volatile.Write(ref _watch, _watch + 1);
        Volatile.Write(ref _root, 0);
        _timer?.Dispose();
    }
}
