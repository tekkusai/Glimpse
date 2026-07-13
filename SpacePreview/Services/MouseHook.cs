using System.Runtime.InteropServices;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed class MouseHook : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(2);

    private readonly Action<int, int, int> _onMouseDown;
    private readonly Func<IntPtr, bool> _shouldActivateWindow;
    private readonly NativeMethods.LowLevelMouseProc _proc;
    private readonly object _lifecycleGate = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private Exception? _startupFailure;
    private bool _disposed;

    public MouseHook(Action<int, int, int> onMouseDown, Func<IntPtr, bool> shouldActivateWindow)
    {
        _onMouseDown = onMouseDown;
        _shouldActivateWindow = shouldActivateWindow;
        _proc = HookCallback;
    }

    public void Start()
    {
        Thread thread;
        ManualResetEventSlim ready;
        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_thread is { IsAlive: true })
            {
                return;
            }

            ready = new ManualResetEventSlim(initialState: false);
            _startupFailure = null;
            thread = new Thread(() => RunHookThread(ready))
            {
                IsBackground = true,
                Name = "Glimpse mouse observer"
            };
            _thread = thread;
            thread.Start();
        }

        if (!ready.Wait(StartupTimeout))
        {
            Stop();
            throw new TimeoutException("Timed out while starting the mouse observer.");
        }

        Exception? failure;
        lock (_lifecycleGate)
        {
            failure = _startupFailure;
        }

        if (failure is not null)
        {
            throw new InvalidOperationException("Unable to install mouse hook.", failure);
        }
    }

    internal static bool ShouldRun(bool inputHooksActive, bool previewVisible, int pinnedWindowCount) =>
        inputHooksActive && (previewVisible || pinnedWindowCount > 0);

    private void RunHookThread(ManualResetEventSlim ready)
    {
        IntPtr hook = IntPtr.Zero;
        try
        {
            var threadId = NativeMethods.GetCurrentThreadId();
            NativeMethods.PeekMessage(
                out _,
                IntPtr.Zero,
                NativeMethods.WM_QUIT,
                NativeMethods.WM_QUIT,
                NativeMethods.PM_NOREMOVE);

            var module = NativeMethods.GetModuleHandle(null);
            hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _proc, module, 0);
            if (hook == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    $"Win32 error {Marshal.GetLastWin32Error()} while installing the mouse observer.");
            }

            lock (_lifecycleGate)
            {
                if (!ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    NativeMethods.UnhookWindowsHookEx(hook);
                    hook = IntPtr.Zero;
                    return;
                }

                _threadId = threadId;
                _hook = hook;
            }

            DiagnosticLog.Write(
                $"mouse hook installed hhook=0x{hook.ToInt64():X} thread={threadId}");
            ready.Set();

            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
                // Low-level hooks are delivered while this thread pumps its
                // queue. Glimpse owns no windows on this observer thread.
            }
        }
        catch (Exception ex)
        {
            lock (_lifecycleGate)
            {
                _startupFailure = ex;
            }
        }
        finally
        {
            ready.Set();
            if (hook != IntPtr.Zero)
            {
                if (NativeMethods.UnhookWindowsHookEx(hook))
                {
                    DiagnosticLog.Write("mouse hook removed");
                }
            }

            lock (_lifecycleGate)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _hook = IntPtr.Zero;
                    _threadId = 0;
                    _thread = null;
                }
            }
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

    /// <summary>Removes the hook without disposing, so <see cref="Start"/> can reinstall it.</summary>
    public void Stop()
    {
        Thread? thread;
        uint threadId;
        IntPtr hook;
        lock (_lifecycleGate)
        {
            thread = _thread;
            threadId = _threadId;
            hook = _hook;
            _thread = null;
            _threadId = 0;
            _hook = IntPtr.Zero;
        }

        if (thread is null)
        {
            return;
        }

        if (hook != IntPtr.Zero && NativeMethods.UnhookWindowsHookEx(hook))
        {
            DiagnosticLog.Write("mouse hook removed");
        }

        if (threadId != 0
            && !NativeMethods.PostThreadMessage(
                threadId,
                NativeMethods.WM_QUIT,
                UIntPtr.Zero,
                IntPtr.Zero))
        {
            DiagnosticLog.Write(
                $"mouse observer stop signal failed error={Marshal.GetLastWin32Error()}");
        }

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
