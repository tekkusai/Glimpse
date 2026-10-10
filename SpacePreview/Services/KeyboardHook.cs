using System.Diagnostics;
using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// The low-level keyboard hook. It runs on the thread that installs it and
/// every key on the PC waits for its callback, so the controller installs it
/// only while a surface Glimpse serves has the foreground.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    // The callback itself is timed precisely. The input stamp is a tick
    // count with ~16 ms steps, so only a wait past one step means anything.
    private const double SlowCallbackMilliseconds = 5;
    private const uint SlowInputDelayMilliseconds = 21;

    private readonly Func<int, uint, long, bool> _onKeyDown;
    private readonly Action<int> _onKeyUp;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private readonly HashSet<Type> _reportedFailures = [];
    private IntPtr _hook;
    private bool _installFailureReported;
    private bool _disposed;

    /// <param name="onKeyDown">vk, the event's tick stamp, the Stopwatch stamp at callback entry; true swallows the key.</param>
    public KeyboardHook(Func<int, uint, long, bool> onKeyDown, Action<int> onKeyUp)
    {
        _onKeyDown = onKeyDown;
        _onKeyUp = onKeyUp;
        _proc = HookCallback;
    }

    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>False when Windows refused the hook; the next foreground change tries again.</summary>
    public bool Start()
    {
        if (_disposed)
        {
            return false;
        }

        if (_hook != IntPtr.Zero)
        {
            return true;
        }

        _hook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _proc,
            NativeMethods.GetModuleHandle(null),
            0);
        if (_hook == IntPtr.Zero)
        {
            if (!_installFailureReported)
            {
                _installFailureReported = true;
                DiagnosticLog.Write($"keyboard hook install failed error={Marshal.GetLastWin32Error()}");
            }

            return false;
        }

        _installFailureReported = false;
        return true;
    }

    /// <summary>Removes the hook without disposing, so <see cref="Start"/> can reinstall it.</summary>
    public void Stop()
    {
        if (_hook == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Every key on the PC waits here. Keep this lane tiny and fail-open:
        // no disk I/O, waits, sleeps, blocking GC or process lookup; queue
        // anything non-trivial. Key-ups are never swallowed.
        if (nCode < 0)
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
        Stop();
    }
}
