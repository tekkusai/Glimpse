using System.Runtime.InteropServices;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The tray icon comes back when the taskbar does. Restarting Explorer is
/// no unit test, so the shell's messages go to the tray window directly,
/// the window must be one the shell's broadcasts can reach, and the shell
/// itself is a fake that records what the icon sends it.
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

    [Fact]
    public Task ANewTaskbarGetsAFreshIconAndANewScaleChangesItInPlace() => WpfApp.RunAsync(async () =>
    {
        var calls = new List<(int Message, IntPtr Window, IntPtr Icon, int Flags)>();
        var addSucceeds = true;
        var shell = TrayIcon.ShellNotifyIcon;
        TrayIcon.ShellNotifyIcon = (int message, ref NativeMethods.NOTIFYICONDATA data) =>
        {
            calls.Add((message, data.hWnd, data.hIcon, data.uFlags));
            return message != NativeMethods.NIM_ADD || addSucceeds;
        };
        TrayIcon? tray = null;
        try
        {
            const int Everything = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP;
            tray = new TrayIcon();
            var start = Assert.Single(calls);
            Assert.Equal((NativeMethods.NIM_ADD, Everything), (start.Message, start.Flags));
            var window = start.Window;

            // Explorer is back: the icon is drawn again and added.
            calls.Clear();
            NativeMethods.SendMessage(window, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
            var added = Assert.Single(calls);
            Assert.Equal((NativeMethods.NIM_ADD, Everything), (added.Message, added.Flags));
            Assert.NotEqual(start.Icon, added.Icon);

            // The old icon still stands, so the add fails and a change
            // carries the new drawing.
            calls.Clear();
            addSucceeds = false;
            NativeMethods.SendMessage(window, TaskbarCreated, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal([NativeMethods.NIM_ADD, NativeMethods.NIM_MODIFY], calls.Select(call => call.Message));
            Assert.NotEqual(added.Icon, calls[0].Icon);
            Assert.Equal(calls[0].Icon, calls[1].Icon);
            Assert.Equal(Everything, calls[1].Flags);
            var modified = calls[1].Icon;

            // A new scale redraws once the broadcast has returned, and
            // changes only the icon.
            calls.Clear();
            NativeMethods.SendMessage(window, NativeMethods.WM_DPICHANGED, (IntPtr)0x00900090, IntPtr.Zero);
            Assert.Empty(calls);
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var redrawn = Assert.Single(calls);
            Assert.Equal((NativeMethods.NIM_MODIFY, NativeMethods.NIF_ICON), (redrawn.Message, redrawn.Flags));
            Assert.NotEqual(modified, redrawn.Icon);

            calls.Clear();
            tray.Dispose();
            Assert.Equal(NativeMethods.NIM_DELETE, Assert.Single(calls).Message);
        }
        finally
        {
            tray?.Dispose();
            TrayIcon.ShellNotifyIcon = shell;
        }
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
