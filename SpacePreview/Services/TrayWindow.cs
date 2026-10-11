using System.Runtime.InteropServices;
using SpacePreview.Native;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// The hidden window the shell talks to about the tray icon. It is a
/// top-level window, never a message-only one: the taskbar announces a
/// fresh start (Explorer restarted, or the primary display changed scale)
/// by broadcasting TaskbarCreated to top-level windows, the taskbar's light
/// or dark switch arrives as a broadcast WM_SETTINGCHANGE, a new resolution
/// as a broadcast WM_DISPLAYCHANGE, and a message-only window receives no
/// broadcasts at all. It has no owner, and only <see cref="Dispose"/> ends
/// it.
/// </summary>
internal sealed class TrayWindow : IDisposable
{
    private const string ClassName = "Glimpse.TrayWindow";
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    private readonly NativeMethods.WndProc _wndProc;
    private readonly Action _clicked;
    private readonly Action _taskbarCreated;
    private readonly Action _redraw;
    private readonly Action _balloonClicked;

    /// <param name="redraw">The taskbar's theme, scale or resolution may have changed.</param>
    public TrayWindow(Action clicked, Action taskbarCreated, Action redraw, Action balloonClicked)
    {
        _clicked = clicked;
        _taskbarCreated = taskbarCreated;
        _redraw = redraw;
        _balloonClicked = balloonClicked;
        _wndProc = WindowProc;

        var module = NativeMethods.GetModuleHandle(null);
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = module,
            lpszClassName = ClassName
        };

        if (NativeMethods.RegisterClassEx(ref wndClass) == 0)
        {
            throw new InvalidOperationException($"Unable to register tray window class. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        // Never shown; a tool window all the same, so it can never reach
        // the taskbar or Alt+Tab.
        Handle = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_TOOLWINDOW,
            ClassName,
            Branding.Name,
            0,
            0, 0, 0, 0,
            IntPtr.Zero,
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (Handle == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnregisterClass(ClassName, module);
            throw new InvalidOperationException($"Unable to create tray window. Win32 error: {error}");
        }

        DiagnosticLog.Write($"tray window created hwnd=0x{Handle.ToInt64():X}");
    }

    public IntPtr Handle { get; private set; }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_TRAYICON)
        {
            switch (lParam.ToInt32() & 0xFFFF)
            {
                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_CONTEXTMENU:
                    _clicked();
                    break;

                case NativeMethods.NIN_BALLOONUSERCLICK:
                    _balloonClicked();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            // A new taskbar holds none of the old icons.
            DiagnosticLog.Write("taskbar recreated -> re-adding tray icon");
            _taskbarCreated();
            return IntPtr.Zero;
        }

        // DefWindowProc would destroy the window, and the tray icon with
        // it, while Glimpse stays running. Nothing outside may close it.
        if (msg == NativeMethods.WM_CLOSE)
        {
            return IntPtr.Zero;
        }

        // The window sits on the primary display, where the tray is, so a
        // new scale there may arrive as WM_DPICHANGED even when the taskbar
        // is not recreated; a new resolution and TaskbarCreated redraw too.
        if (msg is NativeMethods.WM_SETTINGCHANGE or NativeMethods.WM_DPICHANGED or NativeMethods.WM_DISPLAYCHANGE)
        {
            _redraw();
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero)
        {
            return;
        }

        NativeMethods.DestroyWindow(Handle);
        Handle = IntPtr.Zero;

        // The class holds this instance's window procedure; the next tray
        // window registers its own.
        NativeMethods.UnregisterClass(ClassName, NativeMethods.GetModuleHandle(null));
    }
}
