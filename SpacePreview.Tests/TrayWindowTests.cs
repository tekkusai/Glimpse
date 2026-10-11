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

    [Fact]
    public Task TheTaskbarsMessagesReachTheTrayWindow() => WpfApp.RunAsync(() =>
    {
        // Twice: a second window registers its class afresh.
        for (var round = 0; round < 2; round++)
        {
            int clicked = 0, created = 0, changed = 0;
            using var window = new TrayWindow(() => clicked++, () => created++, () => changed++);

            // Broadcasts go to the top-level windows EnumWindows lists; a
            // message-only window is not among them.
            Assert.Contains(window.Handle, TopLevelWindows());

            NativeMethods.SendMessage(window.Handle, NativeMethods.RegisterWindowMessage("TaskbarCreated"), IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(1, created);

            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_SETTINGCHANGE, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(1, changed);

            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_TRAYICON, IntPtr.Zero, (IntPtr)WM_MOUSEMOVE);
            Assert.Equal(0, clicked);
            NativeMethods.SendMessage(window.Handle, NativeMethods.WM_TRAYICON, IntPtr.Zero, NativeMethods.WM_LBUTTONUP);
            Assert.Equal(1, clicked);
            Assert.Equal(1, created);
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
