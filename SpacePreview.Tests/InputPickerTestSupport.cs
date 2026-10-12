using System.Diagnostics;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A file picker's windows as Glimpse reads them, in the classes and at the
/// depth a real Open dialog has them on build 26300, with the keyboard in
/// the file list. Never on screen. They live on a thread of their own, as a
/// real picker's do: focus belongs to a thread, and the tests share one.
/// </summary>
internal sealed class TestPicker : IDisposable
{
    private readonly Thread _thread;
    private uint _threadId;

    private TestPicker()
    {
        using var built = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            using var root = TestWindow.Root("#32770");
            using var frame = root.Child("DUIViewWndClassName");
            using var host = frame.Child("DirectUIHWND");
            using var sink = host.Child("CtrlNotifySink");
            using var view = sink.Child("SHELLDLL_DefView");
            using var list = view.Child("DirectUIHWND");
            list.Focus();
            Handle = root.Handle;
            List = list.Handle;
            _threadId = NativeMethods.GetCurrentThreadId();
            built.Set();

            // Answers whatever is sent to the windows until the test is done.
            while (NativeMethods.GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
            {
            }
        })
        {
            IsBackground = true,
            Name = "Glimpse test picker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        built.Wait();
    }

    public IntPtr Handle { get; private set; }

    /// <summary>The file list, which holds the keyboard.</summary>
    public IntPtr List { get; private set; }

    public ForegroundSelectionContext Context =>
        new(Handle, SelectionSource.CommonFileDialog, CommonFileDialogSelectionService.DialogWindowClass);

    public static TestPicker Open() => new();

    public ExplorerSelection Selected(string name) => new(Path.Combine(TestDataRoot.Path, name), Handle);

    public void Dispose()
    {
        NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>
/// Stands in for asking a picker what is selected. The real question goes
/// to another process through UI Automation and can take as long as that
/// process likes; here it takes as long as the test says.
/// </summary>
internal sealed class PickerLookups
{
    private readonly ManualResetEventSlim _open = new(initialState: true);
    private int _started;
    private int _out;
    private int _mostAtOnce;

    /// <summary>What the picker has selected at the moment a lookup comes back.</summary>
    public ExplorerSelection? Selected { get; set; }

    public int Started => Volatile.Read(ref _started);

    /// <summary>The most lookups that were out at one moment.</summary>
    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    /// <summary>From here no lookup comes back until <see cref="Release"/>.</summary>
    public void Hold() => _open.Reset();

    public void Release() => _open.Set();

    public void Attach(CommonFileDialogSelectionMonitor monitor, TimeSpan? timeout = null)
    {
        Fields.Set(monitor, "_lookUp", (Func<IntPtr, ExplorerSelection?>)LookUp);
        if (timeout is { } bound)
        {
            Fields.Set(monitor, "_lookupTimeout", bound);
        }
    }

    private ExplorerSelection? LookUp(IntPtr dialog)
    {
        Interlocked.Increment(ref _started);
        var now = Interlocked.Increment(ref _out);
        int most;
        while (now > (most = Volatile.Read(ref _mostAtOnce)))
        {
            Interlocked.CompareExchange(ref _mostAtOnce, now, most);
        }

        try
        {
            // A lookup no test let go ends with the test run, not before.
            _open.Wait(TimeSpan.FromSeconds(30));
            return Selected;
        }
        finally
        {
            Interlocked.Decrement(ref _out);
        }
    }
}

/// <summary>Keys as the keyboard hook thread delivers them, and the waits the picker tests share.</summary>
internal static class HookThread
{
    /// <summary>
    /// A key-down from a thread that is not the dispatcher's, while the
    /// dispatcher runs the test and nothing else. True when it was swallowed.
    /// </summary>
    public static bool KeyDown(SpacePreviewController controller, KeyPress press, uint time = 1000)
    {
        Fields.Set(controller, "_readKey", (Func<int, bool, KeyPress>)((_, repeat) => press with { SpaceRepeat = repeat }));
        return OffDispatcher(() => (bool)Fields.Call(controller, "OnKeyDown", press.Vk, time, 0L)!);
    }

    public static void KeyUp(SpacePreviewController controller, int vk) =>
        OffDispatcher(() => Fields.Call(controller, "OnKeyUp", vk));

    /// <summary>Runs the call on another thread and spins for it: the dispatcher pumps nothing meanwhile.</summary>
    public static T OffDispatcher<T>(Func<T> call)
    {
        var task = Task.Run(call);
        var waited = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Assert.True(waited.ElapsedMilliseconds < 5000, "The hook thread's work waited for the dispatcher.");
            Thread.SpinWait(100);
        }

        return task.GetAwaiter().GetResult();
    }

    /// <summary>Lets the dispatcher run everything the keys posted.</summary>
    public static async Task Idle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    /// <summary>Lets the dispatcher pump until the condition holds.</summary>
    public static async Task Until(Func<bool> condition, string because = "The awaited state never came.")
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.ElapsedMilliseconds < 5000, because);
            await Task.Delay(10);
        }
    }
}
