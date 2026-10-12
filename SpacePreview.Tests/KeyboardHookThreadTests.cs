using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Every key on the PC waits for the keyboard hook's answer, so the answer
/// must not wait for the UI thread. Windows calls a low-level hook on the
/// thread that installed it; the hook has a thread of its own.
/// </summary>
public sealed class KeyboardHookThreadTests
{
    private const int Space = NativeMethods.VK_SPACE;

    [Fact]
    public void AKeyDeliveredWhileTheDispatcherIsBusyIsAnsweredAtOnce()
    {
        var windows = new FakeHookDelivery();
        Sta.Run(() =>
        {
            var dispatcherThread = Environment.CurrentManagedThreadId;
            var answeredOn = 0;
            var hook = new KeyboardHook(
                (vk, _, _) =>
                {
                    Volatile.Write(ref answeredOn, Environment.CurrentManagedThreadId);
                    return vk == Space;
                },
                _ => { });
            windows.Attach(hook);
            try
            {
                hook.Start();
                Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The hook was never installed.");

                // The UI thread is busy: it runs code and pumps nothing, as
                // it does while a preview window is built.
                var press = Task.Run(() => windows.Deliver(NativeMethods.WM_KEYDOWN, Space, timeoutMilliseconds: 3000));
                var busy = Stopwatch.StartNew();
                while (!press.IsCompleted && busy.ElapsedMilliseconds < 500)
                {
                    Thread.SpinWait(200);
                }

                Assert.True(press.IsCompleted, "The key waited for the UI thread.");
                Assert.True(press.Result, "The key was answered, but not swallowed.");
                Assert.NotEqual(dispatcherThread, Volatile.Read(ref answeredOn));
                Assert.NotEqual(dispatcherThread, windows.InstallThread);
            }
            finally
            {
                hook.Dispose();
            }
        });
    }

    [Fact]
    public void AnInstallWindowsRefusedIsTriedAgainOnTheNextAsk()
    {
        var windows = new FakeHookDelivery { Refusals = 1 };
        var hook = new KeyboardHook((_, _, _) => false, _ => { });
        windows.Attach(hook);
        try
        {
            hook.Start();
            Assert.True(windows.Attempted.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(windows.Installed.IsSet);

            hook.Start();
            Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)), "The second ask did not install the hook.");
        }
        finally
        {
            hook.Dispose();
        }
    }

    [Fact]
    public void TheHookComesOutOnItsOwnThreadWhenAsked()
    {
        var windows = new FakeHookDelivery();
        var hook = new KeyboardHook((_, _, _) => true, _ => { });
        windows.Attach(hook);
        try
        {
            hook.Start();
            Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)));
            var installedOn = windows.InstallThread;

            hook.Stop();
            Assert.True(windows.Removed.Wait(TimeSpan.FromSeconds(5)), "The hook was never removed.");
            Assert.Equal(installedOn, windows.RemoveThread);
            Assert.NotEqual(Environment.CurrentManagedThreadId, installedOn);
        }
        finally
        {
            hook.Dispose();
        }
    }

    /// <summary>
    /// The unhook takes a turn of the hook thread. A key that arrives before
    /// it is not Glimpse's any more and passes unread.
    /// </summary>
    [Fact]
    public async Task AKeyAfterStopIsNotReadEvenBeforeTheUnhook()
    {
        var windows = new FakeHookDelivery();
        var read = 0;
        using var inFirstKey = new ManualResetEventSlim();
        using var stopAsked = new ManualResetEventSlim();
        var hook = new KeyboardHook(
            (_, _, _) =>
            {
                // The first key holds the hook thread until Stop has been
                // asked for and a second key waits behind it. Windows hands
                // a waiting key to a thread before the thread's own posted
                // messages, so that key arrives ahead of the unhook.
                if (Interlocked.Increment(ref read) == 1)
                {
                    inFirstKey.Set();
                    stopAsked.Wait(TimeSpan.FromSeconds(5));
                    FakeHookDelivery.WaitForAKeyBehindThisOne();
                }

                return true;
            },
            _ => { });
        windows.Attach(hook);
        try
        {
            hook.Start();
            Assert.True(windows.Installed.Wait(TimeSpan.FromSeconds(5)));
            var first = Task.Run(() => windows.Deliver(NativeMethods.WM_KEYDOWN, Space, timeoutMilliseconds: 5000));
            Assert.True(inFirstKey.Wait(TimeSpan.FromSeconds(5)), "The first key never reached the hook.");

            hook.Stop();
            var second = Task.Run(() => windows.Deliver(NativeMethods.WM_KEYDOWN, Space, timeoutMilliseconds: 5000));
            stopAsked.Set();

            Assert.True(await first, "The key before Stop was not read.");
            Assert.False(await second, "A key that arrived after Stop was read.");
            Assert.True(windows.Removed.Wait(TimeSpan.FromSeconds(5)), "Stop never took the hook out.");
            Assert.Equal(1, Volatile.Read(ref read));
            Assert.Equal(2, windows.KeysBeforeRemoval);
        }
        finally
        {
            hook.Dispose();
        }
    }
}

/// <summary>
/// Stands in for Windows' low-level hook delivery, with no real hook and no
/// real key. Windows calls a low-level hook by sending a message to the
/// thread that installed it, so a key waits until that thread pumps. Here
/// the install makes a message-only window on the installing thread, and a
/// key is a message sent to that window.
/// </summary>
internal sealed class FakeHookDelivery
{
    private const uint WmKey = NativeMethods.WM_APP + 0x71;
    private const int GwlpWndProc = -4;
    private const uint QsSendMessage = 0x0040;
    private static readonly IntPtr MessageOnlyParent = new(-3);
    private static readonly WindowProc Proc = OnMessage;
    private static readonly IntPtr ProcPointer = Marshal.GetFunctionPointerForDelegate(Proc);
    private static readonly ConcurrentDictionary<IntPtr, (NativeMethods.LowLevelKeyboardProc Proc, FakeHookDelivery Owner)> Hooks = new();
    private IntPtr _window;
    private int _keys;

    private delegate IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    /// <summary>How many installs to refuse first, as Windows can.</summary>
    public int Refusals { get; set; }

    /// <summary>Set once an install was asked for, refused or not.</summary>
    public ManualResetEventSlim Attempted { get; } = new();

    public ManualResetEventSlim Installed { get; } = new();

    public ManualResetEventSlim Removed { get; } = new();

    /// <summary>The managed id of the thread the hook was last installed from.</summary>
    public int InstallThread { get; private set; }

    public int RemoveThread { get; private set; }

    public int InstallCount { get; private set; }

    /// <summary>How many keys had reached the hook when it was last removed.</summary>
    public int KeysBeforeRemoval { get; private set; }

    /// <summary>
    /// For the hook's callback: returns once another key has been delivered
    /// to this thread and waits for the callback to return.
    /// </summary>
    public static void WaitForAKeyBehindThisOne()
    {
        var waited = Stopwatch.StartNew();
        while ((GetQueueStatus(QsSendMessage) >> 16 & QsSendMessage) == 0)
        {
            Assert.True(waited.ElapsedMilliseconds < 5000, "No second key was delivered.");
            Thread.Yield();
        }
    }

    public void Attach(KeyboardHook hook)
    {
        Fields.Set(hook, "_install", (Func<NativeMethods.LowLevelKeyboardProc, IntPtr>)Install);
        Fields.Set(hook, "_uninstall", (Func<IntPtr, bool>)Uninstall);
    }

    /// <summary>The swallow answer, or null when the installing thread did not pump in time.</summary>
    public bool? Deliver(int message, int vk, int timeoutMilliseconds)
    {
        var delivered = SendMessageTimeout(
            Volatile.Read(ref _window),
            WmKey,
            new IntPtr(message),
            new IntPtr(vk),
            0,
            (uint)timeoutMilliseconds,
            out var answer);
        return delivered == IntPtr.Zero ? null : answer != IntPtr.Zero;
    }

    private IntPtr Install(NativeMethods.LowLevelKeyboardProc proc)
    {
        if (Refusals > 0)
        {
            Refusals--;
            Attempted.Set();
            return IntPtr.Zero;
        }

        var window = CreateWindowEx(0, "STATIC", null, 0, 0, 0, 0, 0, MessageOnlyParent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        Assert.NotEqual(IntPtr.Zero, window);
        Hooks[window] = (proc, this);
        SetWindowLongPtr(window, GwlpWndProc, ProcPointer);
        InstallThread = Environment.CurrentManagedThreadId;
        InstallCount++;
        Volatile.Write(ref _window, window);
        Removed.Reset();
        Attempted.Set();
        Installed.Set();
        return window;
    }

    private bool Uninstall(IntPtr hook)
    {
        Hooks.TryRemove(hook, out _);
        RemoveThread = Environment.CurrentManagedThreadId;
        KeysBeforeRemoval = _keys;
        Installed.Reset();
        Removed.Set();
        return DestroyWindow(hook);
    }

    private static IntPtr OnMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message != WmKey || !Hooks.TryGetValue(hwnd, out var hook))
        {
            return DefWindowProc(hwnd, message, wParam, lParam);
        }

        // Counted on the installing thread, which also removes the hook.
        hook.Owner._keys++;
        var proc = hook.Proc;
        var data = new NativeMethods.KBDLLHOOKSTRUCT
        {
            vkCode = (uint)lParam.ToInt32(),
            time = unchecked((uint)Environment.TickCount)
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.KBDLLHOOKSTRUCT>());
        try
        {
            Marshal.StructureToPtr(data, memory, false);
            return proc(0, wParam, memory);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string? windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        uint flags,
        uint timeoutMilliseconds,
        out IntPtr result);
}
