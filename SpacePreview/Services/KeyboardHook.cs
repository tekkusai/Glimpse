using System.Diagnostics;
using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// The low-level keyboard hook, on a thread of its own. Windows calls a
/// low-level hook on the thread that installed it, and every key on the PC
/// waits for the answer. This thread does nothing but pump and answer, so a
/// key never waits for the UI thread. It lives for the app's lifetime; the
/// controller asks for the hook only while a surface Glimpse serves has the
/// foreground. Start and Stop only post to it.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private const uint WmSync = NativeMethods.WM_APP + 0x41;

    // The callback itself is timed precisely. The input stamp is a tick
    // count with ~16 ms steps, so only a wait past one step means anything.
    private const double SlowCallbackMilliseconds = 5;
    private const uint SlowInputDelayMilliseconds = 21;

    private readonly Func<int, uint, long, bool> _onKeyDown;
    private readonly Action<int> _onKeyUp;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;

    // Tests stand in for Windows here, so no test hooks the real keyboard.
    private readonly Func<NativeMethods.LowLevelKeyboardProc, IntPtr> _install = proc =>
        NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, proc, NativeMethods.GetModuleHandle(null), 0);
    private readonly Func<IntPtr, bool> _uninstall = NativeMethods.UnhookWindowsHookEx;

    // Hook thread only.
    private readonly HashSet<Type> _reportedFailures = [];
    private IntPtr _hook;
    private bool _installFailureReported;

    private uint _threadId;
    private volatile bool _wanted;
    private bool _disposed;

    /// <param name="onKeyDown">
    /// vk, the event's tick stamp, the Stopwatch stamp at callback entry;
    /// true swallows the key. Both callbacks run on the hook thread.
    /// </param>
    public KeyboardHook(Func<int, uint, long, bool> onKeyDown, Action<int> onKeyUp)
    {
        _onKeyDown = onKeyDown;
        _onKeyUp = onKeyUp;
        _proc = HookCallback;

        // Once, at startup: the thread's queue must exist before the first
        // post can reach it.
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "Glimpse keyboard hook"
        };
        thread.Start();
        ready.Wait();
    }

    /// <summary>Asks for the hook. Asking again gives an install Windows refused another try.</summary>
    public void Start() => Request(true);

    /// <summary>Keys stop being read at once; the unhook follows on the hook thread.</summary>
    public void Stop()
    {
        if (_wanted)
        {
            Request(false);
        }
    }

    private void Request(bool wanted)
    {
        if (_disposed)
        {
            return;
        }

        _wanted = wanted;
        if (!NativeMethods.PostThreadMessage(_threadId, WmSync, UIntPtr.Zero, IntPtr.Zero))
        {
            DiagnosticLog.Write($"keyboard hook signal failed error={Marshal.GetLastWin32Error()}");
        }
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        NativeMethods.PeekMessage(
            out _,
            IntPtr.Zero,
            NativeMethods.WM_QUIT,
            NativeMethods.WM_QUIT,
            NativeMethods.PM_NOREMOVE);
        ready.Set();

        try
        {
            // Windows calls the hook from inside GetMessage. The thread owns
            // no windows; its only messages are sync requests and quit.
            while (NativeMethods.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                if (message.message == WmSync)
                {
                    Sync();
                }
            }
        }
        finally
        {
            _wanted = false;
            Sync();
        }
    }

    // Hook thread only.
    private void Sync()
    {
        if (_wanted && _hook == IntPtr.Zero)
        {
            _hook = _install(_proc);
            if (_hook != IntPtr.Zero)
            {
                _installFailureReported = false;
            }
            else if (!_installFailureReported)
            {
                _installFailureReported = true;
                DiagnosticLog.Write($"keyboard hook install failed error={Marshal.GetLastWin32Error()}");
            }
        }
        else if (!_wanted && _hook != IntPtr.Zero)
        {
            _uninstall(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Every key on the PC waits here. Keep this lane tiny and fail-open:
        // no disk I/O, waits, locks, sleeps, blocking GC or process lookup,
        // and nothing that waits for the UI thread; post anything
        // non-trivial. Key-ups are never swallowed.
        if (nCode < 0 || !_wanted)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        var entered = Stopwatch.GetTimestamp();
        var swallow = false;
        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
        var vk = unchecked((int)data.vkCode);
        try
        {
            var message = wParam.ToInt32();
            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                swallow = _onKeyDown(vk, data.time, entered);
            }
            else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                _onKeyUp(vk);
            }
        }
        catch (Exception ex)
        {
            // Fail open rather than eat user input, and say so once per kind.
            swallow = false;
            if (_reportedFailures.Add(ex.GetType()))
            {
                DiagnosticLog.Write($"keyboard hook callback failed vk=0x{vk:X}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var callbackMilliseconds = Stopwatch.GetElapsedTime(entered).TotalMilliseconds;
        var inputDelay = unchecked((uint)Environment.TickCount - data.time);
        if (callbackMilliseconds > SlowCallbackMilliseconds
            || (inputDelay > SlowInputDelayMilliseconds && inputDelay < int.MaxValue))
        {
            DiagnosticLog.Write(
                $"keyboard hook slow vk=0x{vk:X} delay={inputDelay}ms callback={callbackMilliseconds:0.0}ms");
        }

        return swallow ? new IntPtr(1) : NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wanted = false;
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
    }
}
