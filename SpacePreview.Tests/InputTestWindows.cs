using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SpacePreview.Tests;

/// <summary>
/// A native window of any class, for the tests of what Glimpse reads from
/// the window in front: its class, its children, its focus and its caret.
/// It is never on screen. A root that must count as shown is one pixel far
/// off every monitor, takes no activation and has no taskbar button.
/// </summary>
internal sealed class TestWindow : IDisposable
{
    private const uint WsChild = 0x40000000;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const int SwShowNoActivate = 4;

    private static readonly ConcurrentDictionary<string, ushort> Registered = new(StringComparer.Ordinal);
    private static readonly IntPtr Module = GetModuleHandle(null);
    private static readonly IntPtr DefaultProcedure = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW");

    private TestWindow(IntPtr handle) => Handle = handle;

    public IntPtr Handle { get; }

    public static TestWindow Root(string className) =>
        Create(className, WsPopup, WsExToolWindow | WsExNoActivate, IntPtr.Zero);

    public TestWindow Child(string className) =>
        Create(className, WsChild, 0, Handle);

    /// <summary>Counts as shown from here on, as a dialog does once it is built.</summary>
    public void Show() => ShowWindow(Handle, SwShowNoActivate);

    /// <summary>
    /// Gives this window the calling thread's keyboard focus. The thread is
    /// not the one in front, so nothing on screen changes hands.
    /// </summary>
    public void Focus() => SetFocus(Handle);

    /// <summary>A caret owned by this window, as a text box draws one.</summary>
    public void ShowCaret() => Assert.True(CreateCaret(Handle, IntPtr.Zero, 1, 12));

    public void Dispose() => DestroyWindow(Handle);

    /// <summary>
    /// Focus and carets belong to a thread. Each test gets its own, so one
    /// test's focus is never another's.
    /// </summary>
    public static void OnOwnThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static TestWindow Create(string className, uint style, uint exStyle, IntPtr parent)
    {
        // System classes (Edit, #32770) exist already; any other name is
        // registered once with the default window procedure.
        var info = new WNDCLASSEX { cbSize = Marshal.SizeOf<WNDCLASSEX>() };
        if (!GetClassInfoEx(IntPtr.Zero, className, ref info))
        {
            Registered.GetOrAdd(className, name =>
            {
                // The class outlives the test run, and so does its name.
                var created = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = DefaultProcedure,
                    hInstance = Module,
                    lpszClassName = Marshal.StringToHGlobalUni(name)
                };
                var atom = RegisterClassEx(ref created);
                return atom != 0 ? atom : throw new Win32Exception();
            });
        }

        var handle = CreateWindowEx(exStyle, className, null, style, -32000, -32000, 1, 1, parent, IntPtr.Zero, Module, IntPtr.Zero);
        return handle != IntPtr.Zero ? new TestWindow(handle) : throw new Win32Exception();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", EntryPoint = "GetClassInfoExW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClassInfoEx(IntPtr instance, string className, ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle,
        string className,
        string? windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateCaret(IntPtr hwnd, IntPtr bitmap, int width, int height);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
}
