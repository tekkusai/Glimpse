using SpacePreview.Native;

namespace SpacePreview.Services;

/// <summary>
/// One desktop Shell dispatch, owned by the permanent Shell STA.
/// Selection collections are deliberately never cached: they are snapshots
/// and must be read fresh for every physical Space press.
/// </summary>
internal sealed class DesktopShellSessionCache : IDisposable
{
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;
    private DesktopShellSession? _session;

    internal DesktopShellSession? GetOrCreate(out string state)
    {
        EnsureOwnerThread();

        if (_session is not null)
        {
            if (_session.IsIdentityValid(out var invalidReason))
            {
                state = "hit";
                return _session;
            }

            Invalidate($"identity-{invalidReason}");
        }

        if (!DesktopShellSession.TryCreate(out var created, out var failureReason)
            || created is null)
        {
            state = $"acquire-failed-{failureReason}";
            return null;
        }

        _session = created;
        state = "miss";
        DiagnosticLog.Write(
            $"desktop shell session acquired hwnd=0x{created.Hwnd.ToInt64():X} pid={created.ProcessId}");
        return created;
    }

    internal void Invalidate(string reason)
    {
        EnsureOwnerThread();
        if (_session is null)
        {
            return;
        }

        DiagnosticLog.Write(
            $"desktop shell session invalidated reason={reason} hwnd=0x{_session.Hwnd.ToInt64():X} pid={_session.ProcessId}");
        _session.Dispose();
        _session = null;
    }

    public void Dispose()
    {
        EnsureOwnerThread();
        _session?.Dispose();
        _session = null;
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException("Desktop Shell session used outside its owning STA thread.");
        }
    }
}

internal sealed class DesktopShellSession : IDisposable
{
    private bool _disposed;

    private DesktopShellSession(
        object desktop,
        IntPtr hwnd,
        uint processId)
    {
        Desktop = desktop;
        Hwnd = hwnd;
        ProcessId = processId;
    }

    internal object Desktop { get; }
    internal IntPtr Hwnd { get; }
    internal uint ProcessId { get; }

    internal bool IsIdentityValid(out string reason)
    {
        if (_disposed)
        {
            reason = "disposed";
            return false;
        }

        var exists = NativeMethods.IsWindow(Hwnd);
        var currentProcessId = exists ? NativeMethods.GetWindowProcessId(Hwnd) : 0;
        var className = exists ? NativeMethods.GetClassNameString(Hwnd) : string.Empty;
        if (!DesktopShellSessionIdentity.IsValid(
                exists,
                ProcessId,
                currentProcessId,
                className))
        {
            reason = !exists
                ? "window-closed"
                : currentProcessId != ProcessId
                    ? $"pid-changed-{ProcessId}-to-{currentProcessId}"
                    : $"class-changed-{className}";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    internal static bool TryCreate(
        out DesktopShellSession? session,
        out string failureReason)
    {
        session = null;
        failureReason = string.Empty;
        IShellWindows? windows = null;
        object? desktop = null;

        try
        {
            windows = ShellViews.CreateShellWindows();
            desktop = ShellViews.FindDesktop(windows, out var hwnd);
            if (desktop is null || hwnd == IntPtr.Zero)
            {
                failureReason = "desktop-entry-empty";
                return false;
            }

            var exists = NativeMethods.IsWindow(hwnd);
            var processId = exists ? NativeMethods.GetWindowProcessId(hwnd) : 0;
            var className = exists ? NativeMethods.GetClassNameString(hwnd) : string.Empty;
            if (!DesktopShellSessionIdentity.IsValid(
                    exists,
                    processId,
                    processId,
                    className))
            {
                failureReason = "desktop-identity-invalid";
                return false;
            }

            session = new DesktopShellSession(desktop, hwnd, processId);
            desktop = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.GetType().Name;
            return false;
        }
        finally
        {
            ShellViews.Release(desktop);
            ShellViews.Release(windows);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ShellViews.Release(Desktop);
    }
}

internal static class DesktopShellSessionIdentity
{
    internal static bool IsValid(
        bool windowExists,
        uint expectedProcessId,
        uint currentProcessId,
        string? className) =>
        windowExists
        && expectedProcessId != 0
        && currentProcessId == expectedProcessId
        && PreviewInputPolicy.IsDesktopWindowClass(className);
}
