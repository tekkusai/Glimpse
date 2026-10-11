using System.Diagnostics;
using SpacePreview.Native;

namespace SpacePreview.Services;

internal sealed class FullscreenInputSuspension
{
    private const int MonitorTolerance = 2;

    private static readonly HashSet<string> ExemptClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "CabinetWClass",
        "ExploreWClass",
        "XamlExplorerHostIslandWindow",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd"
    };

    private readonly int _currentProcessId = Environment.ProcessId;
    private volatile bool _suspended;
    private IntPtr _suspendedHwnd;
    private string _suspendedDescription = string.Empty;

    /// <summary>Raised on the thread that calls <see cref="Observe"/>.</summary>
    public event Action<bool>? SuspensionChanged;

    public bool IsSuspended => _suspended;

    /// <summary>Re-decides suspension for a new foreground root.</summary>
    public void Observe(IntPtr root)
    {
        if (root == IntPtr.Zero)
        {
            SetState(false, IntPtr.Zero, string.Empty);
            return;
        }

        var snapshot = CreateSnapshot(root, _currentProcessId);
        var suspend = ShouldSuspendForWindow(snapshot);
        if (SetState(suspend, suspend ? root : IntPtr.Zero, suspend ? DescribeWindow(root, snapshot) : string.Empty)
            && suspend)
        {
            WriteWindowDetailsAsync(root, snapshot);
        }
    }

    private bool SetState(bool suspended, IntPtr hwnd, string description)
    {
        if (_suspended == suspended && _suspendedHwnd == hwnd)
        {
            return false;
        }

        var previousDescription = _suspendedDescription;
        _suspended = suspended;
        _suspendedHwnd = hwnd;
        _suspendedDescription = suspended ? description : string.Empty;

        DiagnosticLog.Write(suspended
            ? $"input suspended for fullscreen {description}"
            : string.IsNullOrWhiteSpace(previousDescription)
                ? "input resumed"
                : $"input resumed from {previousDescription}");
        SuspensionChanged?.Invoke(suspended);
        return true;
    }

    private static WindowSnapshot CreateSnapshot(IntPtr hwnd, int currentProcessId)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        var className = NativeMethods.GetClassNameString(hwnd);
        var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        var windowRect = NativeMethods.GetWindowRect(hwnd, out var rect) ? rect : default;

        var monitorRect = default(NativeMethods.RECT);
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };
            if (NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                monitorRect = info.rcMonitor;
            }
        }

        return new WindowSnapshot(
            className,
            (int)processId,
            currentProcessId,
            style,
            windowRect,
            monitorRect);
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            return Process.GetProcessById(processId).ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DescribeWindow(IntPtr hwnd, WindowSnapshot snapshot)
    {
        var className = SanitizeLogValue(snapshot.ClassName);

        return $"hwnd=0x{hwnd.ToInt64():X} pid={snapshot.HwndProcessId} class={ValueOrUnknown(className)}";
    }

    private static void WriteWindowDetailsAsync(IntPtr hwnd, WindowSnapshot snapshot)
    {
        _ = Task.Run(() =>
        {
            var processName = SanitizeLogValue(GetProcessName(snapshot.HwndProcessId));
            var title = SanitizeLogValue(NativeMethods.GetWindowTextString(hwnd));
            var titlePart = string.IsNullOrWhiteSpace(title) ? string.Empty : $" title=\"{title}\"";
            DiagnosticLog.Write($"input suspended details hwnd=0x{hwnd.ToInt64():X} pid={snapshot.HwndProcessId} process={ValueOrUnknown(processName)} class={ValueOrUnknown(SanitizeLogValue(snapshot.ClassName))}{titlePart}");
        });
    }

    private static string ValueOrUnknown(string value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static string SanitizeLogValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sanitized = value
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Replace('"', '\'')
            .Trim();

        return sanitized.Length <= 160 ? sanitized : sanitized[..160] + "...";
    }

    internal static bool ShouldSuspendForWindow(WindowSnapshot window)
    {
        if (window.HwndProcessId == 0
            || window.HwndProcessId == window.CurrentProcessId
            || ExemptClasses.Contains(window.ClassName)
            || HasCaption(window.Style))
        {
            return false;
        }

        return CoversMonitor(window.WindowRect, window.MonitorRect);
    }

    private static bool HasCaption(long style) =>
        (style & NativeMethods.WS_CAPTION) == NativeMethods.WS_CAPTION;

    private static bool CoversMonitor(NativeMethods.RECT window, NativeMethods.RECT monitor)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0 || window.Width <= 0 || window.Height <= 0)
        {
            return false;
        }

        return Math.Abs(window.Left - monitor.Left) <= MonitorTolerance
            && Math.Abs(window.Top - monitor.Top) <= MonitorTolerance
            && Math.Abs(window.Right - monitor.Right) <= MonitorTolerance
            && Math.Abs(window.Bottom - monitor.Bottom) <= MonitorTolerance;
    }

    internal readonly record struct WindowSnapshot(
        string ClassName,
        int HwndProcessId,
        int CurrentProcessId,
        long Style,
        NativeMethods.RECT WindowRect,
        NativeMethods.RECT MonitorRect);
}
