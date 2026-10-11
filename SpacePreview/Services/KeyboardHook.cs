using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed class KeyboardHook : IDisposable
{
    private readonly Func<int, bool> _onKeyDown;
    private readonly Action<int> _onKeyUp;
    private readonly NativeMethods.LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    public KeyboardHook(Func<int, bool> onKeyDown, Action<int> onKeyUp)
    {
        _onKeyDown = onKeyDown;
        _onKeyUp = onKeyUp;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        var module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Unable to install keyboard hook. Win32 error: {Marshal.GetLastWin32Error()}");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // Global low-level hook path: keep this lane tiny and fail-open. Do
        // not add disk I/O, waits, sleeps, blocking GC, process lookup, or
        // other slow work here; enqueue or dispatch anything non-trivial.
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        try
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vk = unchecked((int)data.vkCode);

            if (message is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                if (_onKeyDown(vk))
                {
                    return new IntPtr(1);
                }
            }
            else if (message is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                _onKeyUp(vk);
            }
        }
        catch
        {
            // Fail open rather than eat user input.
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
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
