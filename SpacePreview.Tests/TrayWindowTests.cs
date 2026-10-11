using System.Runtime.InteropServices;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The tray icon comes back when the taskbar does. Restarting Explorer is
/// no unit test, so the shell's messages go to the tray window directly,
/// and the window must be one the shell's broadcasts can reach.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class TrayWindowTests
{
    private const uint WM_MOUSEMOVE = 0x0200;
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    [Fact]
    public Task TheTaskbarsMessagesReachTheTrayWindow() => WpfApp.RunAsync(() =>
    {
        // Twice: a second window registers its class afresh.
        for (var round = 0; round < 2; round++)
        {
            int clicked = 0, created = 0, redrawn = 0;
            using var window = new TrayWindow(() => clicked++, () => created++, () => redrawn++);

            // Broadcasts go to the top-level windows EnumWindows lists; a
            // message-only window is not among them. Nothing owns it.
            Assert.Contains(window.Handle, TopLevelWindows());
            Assert.Equal(IntPtr.Zero, NativeMethods.GetWindow(window.Handle, NativeMethods.GW_OWNER));

            NativeMethods.SendMessage(window.Handle, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(1, created);

            // A new theme, scale or resolution redraws the glyph.
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_DPICHANGED, (IntPtr)0x00900090, IntPtr.Zero);
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_DISPLAYCHANGE, (IntPtr)32, IntPtr.Zero);
            Assert.Equal(3, redrawn);

            // A polite close from outside leaves it standing, still listening.
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Assert.True(NativeMethods.IsWindow(window.Handle));
            NativeMethods.SendMessage(window.Handle, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(2, created);

            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_TRAYICON, IntPtr.Zero, (IntPtr)WM_MOUSEMOVE);
            Assert.Equal(0, clicked);
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_TRAYICON, IntPtr.Zero, NativeMethods.WM_LBUTTONUP);
            Assert.Equal(1, clicked);
            Assert.Equal(2, created);
        }

        return Task.CompletedTask;
    });

    private static List<IntPtr> TopLevelWindows()
    {
        var windows = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
}
