using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// Watches button presses while a preview is on screen. The hook lives on one
/// observer thread for the app's lifetime. Start and Stop only post to it, so
/// showing a preview never waits for a hook to install.
/// </summary>
internal sealed class MouseHook : IDisposable
{
    private const uint WmSync = NativeMethods.WM_APP + 0x40;

    private readonly Action<int, int, int> _onMouseDown;
    private readonly Func<IntPtr, bool> _shouldActivateWindow;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private uint _threadId;
    private volatile bool _wanted;
    private IntPtr _hook;
    private bool _disposed;

    public MouseHook(Action<int, int, int> onMouseDown, Func<IntPtr, bool> shouldActivateWindow)
    {
        _onMouseDown = onMouseDown;
        _shouldActivateWindow = shouldActivateWindow;
        _proc = HookCallback;

        // Once, at startup: the observer's queue must exist before the first
        // post can reach it.
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() => Run(ready))
        {
            IsBackground = true,
            Name = "Glimpse mouse observer"
        };
        thread.Start();
        ready.Wait();
    }

    internal static bool ShouldRun(bool suspended, bool previewVisible, int pinnedWindowCount) =>
        !suspended && (previewVisible || pinnedWindowCount > 0);

    public void Start() => Request(true);

    public void Stop() => Request(false);

    private void Request(bool wanted)
    {
        if (_disposed || _wanted == wanted)
        {
            return;
        }

        _wanted = wanted;
        if (!NativeMethods.PostThreadMessage(_threadId, WmSync, UIntPtr.Zero, IntPtr.Zero))
        {
            DiagnosticLog.Write($"mouse observer signal failed error={Marshal.GetLastWin32Error()}");
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
            // Low-level hooks are delivered while this thread pumps. It owns
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

    // Observer thread only.
    private void Sync()
    {
        if (_wanted && _hook == IntPtr.Zero)
        {
            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL,
                _proc,
                NativeMethods.GetModuleHandle(null),
                0);
            if (_hook == IntPtr.Zero)
            {
                DiagnosticLog.Write($"mouse hook install failed error={Marshal.GetLastWin32Error()}");
            }
        }
        else if (!_wanted && _hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
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
            if (message is NativeMethods.WM_LBUTTONDOWN
                or NativeMethods.WM_RBUTTONDOWN
                or NativeMethods.WM_MBUTTONDOWN
                or NativeMethods.WM_XBUTTONDOWN)
            {
                // Windows grants foreground rights only while the click is
                // being processed, and a click on web content lands in the
                // browser process. Take the foreground here or never.
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var hit = NativeMethods.GetRootWindowAt(data.pt.X, data.pt.Y);
                if (hit != IntPtr.Zero
                    && _shouldActivateWindow(hit)
                    && NativeMethods.GetForegroundWindow() != hit)
                {
                    NativeMethods.SetForegroundWindow(hit);
                }

                _onMouseDown(message, data.pt.X, data.pt.Y);
            }
        }
        catch
        {
            // Global hooks must fail open.
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
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
