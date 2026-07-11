using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// A quiet app-owned fallback for warnings that Windows may suppress while
/// Focus Assist is active. It never activates or takes keyboard focus.
/// </summary>
public partial class PreviewAccessWarningWindow : Window
{
    private static readonly TimeSpan InitialDismissDelay = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan HoverLeaveDismissDelay = TimeSpan.FromSeconds(4);
    private const double WorkAreaMargin = 16;

    private readonly PreviewAccessWarningInfo _warning;
    private readonly DispatcherTimer _dismissTimer;
    private bool _dismissed;

    internal PreviewAccessWarningWindow(PreviewAccessWarningInfo warning)
    {
        _warning = warning;
        InitializeComponent();
        Theme.ApplyResources(this);
        AccentBar.Background = Theme.Brush(Theme.Current.Accent);
        BodyText.Text = BuildMessage(warning);

        _dismissTimer = new DispatcherTimer(
            InitialDismissDelay,
            DispatcherPriority.Background,
            (_, _) => Dismiss("timeout"),
            Dispatcher);
        _dismissTimer.Stop();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        CloseButton.Click += (_, _) => Dismiss("close");
        MouseEnter += (_, _) => _dismissTimer.Stop();
        MouseLeave += (_, _) =>
        {
            _dismissTimer.Interval = HoverLeaveDismissDelay;
            _dismissTimer.Start();
        };
    }

    internal static string BuildMessage(PreviewAccessWarningInfo warning) =>
        warning.Kind == PreviewAccessWarningKind.ConfirmedElevationMismatch
            ? $"{warning.DisplayName} is running as administrator, but Glimpse isn't. Run both at the same level to preview files."
            : "Glimpse couldn't access this file picker. If the app is running as administrator, run both apps at the same permission level.";

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

    public void Dismiss(string reason)
    {
        if (_dismissed)
        {
            return;
        }

        _dismissed = true;
        _dismissTimer.Stop();
        DiagnosticLog.Write(
            $"app warning dismissed reason={reason} kind={_warning.Kind} process={_warning.DisplayName} pid={_warning.ProcessId}");
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
            // Loaded, especially for transparent windows. Reassert the style
            // against the final HWND before placing it topmost.
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
