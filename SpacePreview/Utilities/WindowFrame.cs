using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SpacePreview.Native;

namespace SpacePreview.Utilities;

/// <summary>
/// The one window frame the app wears: Windows 11 rounds the corners and
/// draws the border and shadow, and the frame's dark mode follows the
/// palette. Dialogs wear the preview's frame too; they move by their title
/// area and keep the size their content gave them.
/// </summary>
internal static class WindowFrame
{
    private const long WS_MAXIMIZEBOX = 0x00010000;
    private const long WS_MINIMIZEBOX = 0x00020000;
    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    public static void Apply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var corners = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref corners, sizeof(int));
        ApplyThemeMode(hwnd);
    }

    public static void ApplyThemeMode(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dark = Theme.Current.IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    /// <summary>
    /// A dialog on the preview's frame, moved by its title area when it has
    /// one. The frame needs the resizable style for its shadow, so the
    /// maximize and minimize boxes go and a drag never changes the size:
    /// Snap can move a dialog, not stretch it.
    /// </summary>
    public static void AttachDialog(Window window, UIElement? titleArea)
    {
        var moving = false;
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE, new IntPtr(style & ~(WS_MAXIMIZEBOX | WS_MINIMIZEBOX)));
            Apply(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr _, int message, IntPtr _, IntPtr lParam, ref bool _) =>
            {
                if (message == WM_ENTERSIZEMOVE || message == WM_EXITSIZEMOVE)
                {
                    moving = message == WM_ENTERSIZEMOVE;
                }
                else if (message == WM_WINDOWPOSCHANGING && moving)
                {
                    var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                    position.flags |= NativeMethods.SWP_NOSIZE;
                    Marshal.StructureToPtr(position, lParam, fDeleteOld: false);
                }

                return IntPtr.Zero;
            });
        };

        Action repaint = () => ApplyThemeMode(new WindowInteropHelper(window).Handle);
        Theme.Changed += repaint;
        window.Closed += (_, _) => Theme.Changed -= repaint;

        if (titleArea is null)
        {
            return;
        }

        titleArea.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                window.DragMove();
            }
            catch (InvalidOperationException)
            {
            }
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }
}
