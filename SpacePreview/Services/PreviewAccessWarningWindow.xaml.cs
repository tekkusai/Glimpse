using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

internal enum PreviewAccessWarningDismissalReason
{
    Manual,
    TimedOut,
    Replaced,
    Disposed,
    Probe
}

/// <summary>
/// A quiet app-owned fallback for warnings that Windows may suppress while
/// Focus Assist is active. It never activates or takes keyboard focus.
/// </summary>
public partial class PreviewAccessWarningWindow : Window
{
    private static readonly TimeSpan InitialDismissDelay = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan HoverLeaveDismissDelay = TimeSpan.FromSeconds(4);
    private const int MaxDisplayNameLength = 48;
    private const int MaxTrayMessageLength = 255;
    private const double WorkAreaMargin = 16;

    private readonly PreviewAccessWarningInfo _warning;
    private readonly DispatcherTimer _dismissTimer;
    private bool _dismissed;

    internal PreviewAccessWarningWindow(
        PreviewAccessWarningInfo warning,
        bool finalReminder)
    {
        _warning = warning;
        InitializeComponent();
        Title = $"{Branding.Name} notification";
        WindowFrame.AttachDialog(this, titleArea: null);
        HeadingText.Text = Heading;
        BodyText.Text = BuildMessage(warning);
        FooterText.Text = BuildReminderMessage(finalReminder);

        _dismissTimer = new DispatcherTimer(
            InitialDismissDelay,
            DispatcherPriority.Background,
            (_, _) => Dismiss(PreviewAccessWarningDismissalReason.TimedOut),
            Dispatcher);
        _dismissTimer.Stop();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        CloseButton.Click += (_, _) => Dismiss(PreviewAccessWarningDismissalReason.Manual);
        MouseEnter += (_, _) => _dismissTimer.Stop();
        MouseLeave += (_, _) =>
        {
            _dismissTimer.Interval = HoverLeaveDismissDelay;
            _dismissTimer.Start();
        };
    }

    /// <summary>One heading for this warning, wherever it appears: this window and the tray toast.</summary>
    internal const string Heading = "Preview unavailable here";

    internal event Action<PreviewAccessWarningDismissalReason>? Dismissed;

    internal static string BuildMessage(PreviewAccessWarningInfo warning)
    {
        var displayName = FriendlyDisplayName(warning.DisplayName);
        return warning.Kind == PreviewAccessWarningKind.ConfirmedElevationMismatch
            ? $"Glimpse and {displayName} are running at different permission levels. Restart {displayName} without administrator access to use previews."
            : $"{Branding.Name} can't access this file picker. If {displayName} is running as administrator, restart it without administrator access to use previews.";
    }

    internal static string BuildReminderMessage(bool finalReminder) =>
        finalReminder
            ? "Final heads-up. Glimpse won't show this notice again."
            : "Close this notice to stop future reminders.";

    internal static string BuildTrayMessage(
        PreviewAccessWarningInfo warning,
        bool finalReminder)
    {
        var reminder = BuildReminderMessage(finalReminder);
        var body = BuildMessage(warning);
        var maxBodyLength = MaxTrayMessageLength - reminder.Length - 1;
        if (body.Length > maxBodyLength)
        {
            body = body[..Math.Max(0, maxBodyLength - 3)].TrimEnd() + "...";
        }

        return $"{body} {reminder}";
    }

    private static string FriendlyDisplayName(string? displayName)
    {
        var normalized = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "this app";
        }

        return normalized.Length <= MaxDisplayNameLength
            ? normalized
            : normalized[..(MaxDisplayNameLength - 3)].TrimEnd() + "...";
    }

    internal static Point CalculatePlacement(Rect workArea, Size windowSize, double margin)
    {
        var availableWidth = Math.Max(0, workArea.Width - (margin * 2));
        var availableHeight = Math.Max(0, workArea.Height - (margin * 2));
        var x = windowSize.Width <= availableWidth
            ? workArea.Right - windowSize.Width - margin
            : workArea.Left + Math.Max(0, (workArea.Width - windowSize.Width) / 2);
        var y = windowSize.Height <= availableHeight
            ? workArea.Bottom - windowSize.Height - margin
            : workArea.Top + Math.Max(0, (workArea.Height - windowSize.Height) / 2);
        return new Point(x, y);
    }

    internal void Dismiss(PreviewAccessWarningDismissalReason reason)
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        _dismissTimer.Stop();
        DiagnosticLog.Write(
            $"app warning dismissed reason={reason.ToString().ToLowerInvariant()} kind={_warning.Kind} process={_warning.DisplayName} pid={_warning.ProcessId}");
        Dismissed?.Invoke(reason);
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        ApplyNoActivateStyle(hwnd);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionNearForegroundWorkArea();

        var hwnd = new WindowInteropHelper(this).Handle;
        var noActivate = false;
        var ownsForeground = false;
        if (hwnd != IntPtr.Zero)
        {
            // WPF can rewrite extended styles between SourceInitialized and
            // Loaded. Reassert the style against the final HWND before
            // placing it topmost.
            ApplyNoActivateStyle(hwnd);
            NativeMethods.SetWindowPos(
                hwnd,
                new IntPtr(-1),
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE);
            var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            noActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
            ownsForeground = NativeMethods.GetForegroundWindow() == hwnd;
        }

        DiagnosticLog.Write(
            $"app warning visible kind={_warning.Kind} process={_warning.DisplayName} pid={_warning.ProcessId} noActivate={noActivate} ownsForeground={ownsForeground} at={Left:0},{Top:0} size={ActualWidth:0}x{ActualHeight:0}");
        _dismissTimer.Start();
    }

    private static void ApplyNoActivateStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    private void PositionNearForegroundWorkArea()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var monitor = NativeMethods.MonitorFromWindow(
            foreground,
            NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };

        Rect workArea;
        if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            var dpi = foreground == IntPtr.Zero ? 96 : NativeMethods.GetDpiForWindow(foreground);
            if (dpi == 0)
            {
                dpi = 96;
            }

            var scale = 96.0 / dpi;
            workArea = new Rect(
                info.rcWork.Left * scale,
                info.rcWork.Top * scale,
                info.rcWork.Width * scale,
                info.rcWork.Height * scale);
        }
        else
        {
            workArea = SystemParameters.WorkArea;
        }

        var placement = CalculatePlacement(
            workArea,
            new Size(ActualWidth, ActualHeight),
            WorkAreaMargin);
        Left = placement.X;
        Top = placement.Y;
    }
}
