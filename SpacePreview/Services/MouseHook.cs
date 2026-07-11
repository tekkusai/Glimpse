using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed class MouseHook : IDisposable
{
    private readonly Func<int, int, int, bool> _onMouseDown;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private IntPtr _hook;
    private bool _disposed;

    public MouseHook(Func<int, int, int, bool> onMouseDown)
    {
        _onMouseDown = onMouseDown;
        _proc = HookCallback;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero)
        {
            return;
        }

        var module = NativeMethods.GetModuleHandle(null);
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
        if (_hook == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Unable to install mouse hook. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        DiagnosticLog.Write($"mouse hook installed hhook=0x{_hook.ToInt64():X}");
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
            if (message is NativeMethods.WM_LBUTTONDOWN
                or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN
                or NativeMethods.WM_XBUTTONDOWN)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (_onMouseDown(message, data.pt.X, data.pt.Y))
                {
                    return new IntPtr(1);
                }
            }
        }
        catch
        {
            // Global hooks must fail open.
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
        DiagnosticLog.Write("mouse hook removed");
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
