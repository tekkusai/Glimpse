namespace SpacePreview.Services;

internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\SpacePreview.Instance";
    public const string QuitEventName = @"Local\SpacePreview.Quit";
    public const string ShowEventName = @"Local\SpacePreview.Show";

    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex, bool isPrimary)
    {
        _mutex = mutex;
        IsPrimary = isPrimary;
    }

    public bool IsPrimary { get; }

    public static SingleInstance Acquire()
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        return new SingleInstance(mutex, createdNew);
    }

    public static bool SignalQuit()
    {
        try
        {
            using var quitEvent = EventWaitHandle.OpenExisting(QuitEventName);
            quitEvent.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public static EventWaitHandle CreateQuitEvent()
    {
        return new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
    }

    /// <summary>
    /// Launching the app while it already runs means "show yourself": the
    /// second instance signals the primary (which restores a hidden tray
    /// icon) and exits.
    /// </summary>
    public static bool SignalShow()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowEventName);
            showEvent.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    public static EventWaitHandle CreateShowEvent()
    {
        return new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
    }

    public void Dispose()
    {
        try
        {
            if (IsPrimary)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch
        {
            // Process shutdown can race mutex ownership. Ignore.
        }

        _mutex.Dispose();
    }
}
