using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// The whole tray presence: a shell notification icon backed by a message-only
/// window, a compact WPF flyout, and ownership of the settings and notification
/// windows opened from that surface. Manages its GDI handles and cleans them up
/// on <see cref="Dispose"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(5);

    private readonly NativeMethods.WndProc _wndProc;
    private readonly uint _taskbarCreatedMessage;
    private readonly ContextMenu _menu;
    private readonly AppUpdater _updates;

    // Quitting ends the app. Unsaved signing work comes forward first and
    // holds it.
    private readonly Func<bool> _canQuit;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private ushort _classAtom;
    private DateTime _menuClosedAt;
    private bool _iconVisible;
    private bool _disposed;
    private DispatcherTimer? _presenceTimer;

    // A click on the balloon opens the release page while the balloon is ours.
    private bool _balloonIsUpdateNotice;

    private PreviewAccessWarningWindow? _previewAccessWarningWindow;
    private SettingsWindow? _settingsWindow;

    /// <summary>Raised when the user picks a theme. Handler should persist and re-resolve.</summary>
    public event Action<ThemePreference>? ThemeChanged;

    /// <summary>Raised when the user picks Quit.</summary>
    public event Action? QuitRequested;

    public TrayIcon(AppUpdater updates, Func<bool>? canQuit = null)
    {
        _wndProc = WindowProc;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        _updates = updates;
        _canQuit = canQuit ?? (() => true);

        _menu = BuildMenu();

        CreateMessageWindow();
        RebuildIcon();
        AddIcon();
        ShowMajorUpdateNoticeOnReturn();

        // The taskbar's own theme (a separate setting from app theme) picks
        // the glyph color; broadcasts never reach a message-only window, so
        // SystemEvents carries the change here instead.
        _userPreferenceChanged = (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
            {
                Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshIconForTheme));
            }
        };
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += _userPreferenceChanged;
    }

    private readonly Microsoft.Win32.UserPreferenceChangedEventHandler _userPreferenceChanged;

    private void RefreshIconForTheme()
    {
        if (_disposed)
        {
            return;
        }

        RebuildIcon();
        if (_iconVisible)
        {
            var data = NewIconData();
            data.uFlags = NativeMethods.NIF_ICON;
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }
    }

    private static MenuItem MakeItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private ContextMenu BuildMenu()
    {
        var app = Application.Current;
        var menu = new ContextMenu
        {
            Style = (Style)app.FindResource("PreviewContextMenu"),
            StaysOpen = false
        };

        var itemStyle = (Style)app.FindResource("PreviewMenuItem");
        var separatorStyle = (Style)app.FindResource("PreviewMenuSeparator");

        // The menu is a door, not a settings tree: everything stateful,
        // updates included, lives in the Settings window.
        var settings = MakeItem("Settings…", OpenSettings);
        settings.Style = itemStyle;

        var quit = MakeItem("Quit", () =>
        {
            if (_canQuit())
            {
                QuitRequested?.Invoke();
            }
        });
        quit.Style = itemStyle;

        // See ShowMenu: clicking the icon while the menu is open must close
        // it, not dismiss-and-reopen in the same click.
        menu.Closed += (_, _) => _menuClosedAt = DateTime.UtcNow;

        menu.Items.Add(settings);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(quit);
        return menu;
    }

    private void PickTheme(ThemePreference preference)
    {
        ThemeChanged?.Invoke(preference);
    }

    /// <summary>The tray's Settings item, and launching the app while it runs.</summary>
    public void OpenSettings()
    {
        _menu.IsOpen = false;
        if (_settingsWindow is null)
        {
            var window = new SettingsWindow(_updates, PickTheme);
            _settingsWindow = window;
            window.Closed += (_, _) => _settingsWindow = null;
            _ = ShowSettingsWhenWholeAsync(window);
        }
        else if (_settingsWindow.IsVisible)
        {
            BringForward(_settingsWindow);
        }
    }

    // Opening waits up to the house beat for what this PC lacks, so the
    // dialog appears whole instead of growing under the pointer.
    private async Task ShowSettingsWhenWholeAsync(SettingsWindow window)
    {
        await Task.WhenAny(window.MissingPiecesChecked, Task.Delay(Motion.OpenWait));
        if (_disposed || _settingsWindow != window)
        {
            return;
        }

        window.Show();
        BringForward(window);
    }

    // A launch from Start gives the foreground to the second instance,
    // which exits; take it the way a click on a preview does.
    private static void BringForward(Window window)
    {
        NativeMethods.ForceForeground(new System.Windows.Interop.WindowInteropHelper(window).Handle);
        window.Activate();
    }

    private void ShowMenu()
    {
        // The outside-click dismissal fires on mouse down; the icon's mouse up
        // would then instantly reopen the menu it just closed.
        if (DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        // The foreground dance: without it the menu will not close on an
        // outside click.
        NativeMethods.SetForegroundWindow(_hwnd);
        _menu.IsOpen = true;
    }

    /// <summary>The one notice a new major version gets. A click opens its release page.</summary>
    internal static (string Title, string Text) MajorUpdateNotice(Version version) => (
        $"{Branding.Name} is now {version.Major}.{version.Minor}",
        "Select to see what's new.");

    /// <summary>
    /// An update usually lands while the user is away, so the notice waits
    /// for the first keyboard or mouse input after Glimpse starts.
    /// </summary>
    private void ShowMajorUpdateNoticeOnReturn()
    {
        if (!Settings.Current.MajorUpdateNoticeDue)
        {
            return;
        }

        var startedAt = Environment.TickCount64;
        _presenceTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = PresencePollInterval };
        _presenceTimer.Tick += (_, _) =>
        {
            var running = TimeSpan.FromMilliseconds(Environment.TickCount64 - startedAt);
            if (NativeMethods.TimeSinceLastInput() >= running)
            {
                return;
            }

            _presenceTimer.Stop();
            ShowMajorUpdateNotice();
        };
        _presenceTimer.Start();
    }

    private void ShowMajorUpdateNotice()
    {
        var (title, text) = MajorUpdateNotice(_updates.CurrentVersion);
        var data = NewIconData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NativeMethods.NIIF_INFO
            | NativeMethods.NIIF_NOSOUND
            | NativeMethods.NIIF_RESPECT_QUIET_TIME;
        if (_disposed || !_iconVisible || !NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            DiagnosticLog.Write("update notice failed");
            return;
        }

        _balloonIsUpdateNotice = true;
        Settings.Update(settings => settings with { MajorUpdateNoticeDue = false });
        DiagnosticLog.Write($"update notice shown version={_updates.CurrentVersion.ToString(3)}");
    }

    private void OnBalloonClicked()
    {
        if (_balloonIsUpdateNotice)
        {
            _balloonIsUpdateNotice = false;
            AppUpdater.OpenReleasePage(_updates.CurrentVersion);
        }
    }

    public void ShowPreviewAccessWarning(PreviewAccessWarningInfo warning)
    {
        if (_disposed)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        var state = PreviewAccessWarningPolicy.FromSettings(Settings.Current);
        if (!PreviewAccessWarningPolicy.CanShowLifetime(state, nowUtc))
        {
            DiagnosticLog.Write(
                $"preview access warning suppressed by lifetime policy kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
            return;
        }

        var finalReminder = state.Shows == PreviewAccessWarningPolicy.LifetimeShowLimit - 1;
        var appWarningShown = ShowAppOwnedWarning(warning, finalReminder);
        var trayWarningShown = false;
        if (_iconVisible)
        {
            trayWarningShown = ShowTrayWarning(warning, finalReminder);
        }

        if (appWarningShown || trayWarningShown)
        {
            Settings.Update(current =>
                PreviewAccessWarningPolicy.ApplyLifetimeState(
                    current,
                    PreviewAccessWarningPolicy.RecordLifetimeShow(
                        PreviewAccessWarningPolicy.FromSettings(current),
                        nowUtc)));
        }
    }

    private void PermanentlyDismissPreviewAccessWarnings()
    {
        if (_disposed)
        {
            return;
        }

        var nowUtc = DateTimeOffset.UtcNow;
        Settings.Update(current =>
            PreviewAccessWarningPolicy.ApplyLifetimeState(
                current,
                PreviewAccessWarningPolicy.DismissForever(
                    PreviewAccessWarningPolicy.FromSettings(current),
                    nowUtc)));
        DiagnosticLog.Write("preview access warnings permanently dismissed");
    }

    private bool ShowAppOwnedWarning(
        PreviewAccessWarningInfo warning,
        bool finalReminder)
    {
        try
        {
            _previewAccessWarningWindow?.Dismiss(PreviewAccessWarningDismissalReason.Replaced);
            var window = new PreviewAccessWarningWindow(warning, finalReminder);
            _previewAccessWarningWindow = window;
            window.Dismissed += reason =>
            {
                if (reason == PreviewAccessWarningDismissalReason.Manual)
                {
                    PermanentlyDismissPreviewAccessWarnings();
                }
            };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_previewAccessWarningWindow, window))
                {
                    _previewAccessWarningWindow = null;
                }
            };
            window.Show();
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"app warning failed kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool ShowTrayWarning(
        PreviewAccessWarningInfo warning,
        bool finalReminder)
    {
        _balloonIsUpdateNotice = false;
        var data = NewIconData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = PreviewAccessWarningWindow.Heading;
        data.szInfo = PreviewAccessWarningWindow.BuildTrayMessage(warning, finalReminder);
        data.dwInfoFlags = NativeMethods.NIIF_INFO
            | NativeMethods.NIIF_NOSOUND
            | NativeMethods.NIIF_RESPECT_QUIET_TIME;
        if (NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            DiagnosticLog.Write($"preview access warning shown kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
            return true;
        }

        DiagnosticLog.Write($"preview access warning failed kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
        return false;
    }

    // ---- Message-only window -------------------------------------------------

    private void CreateMessageWindow()
    {
        var module = NativeMethods.GetModuleHandle(null);
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProc,
            hInstance = module,
            lpszClassName = "Glimpse.TrayWindow"
        };

        _classAtom = NativeMethods.RegisterClassEx(ref wndClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException($"Unable to register tray window class. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            "Glimpse.TrayWindow",
            Branding.Name,
            0,
            0, 0, 0, 0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero,
            module,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException($"Unable to create tray message window. Win32 error: {Marshal.GetLastWin32Error()}");
        }

        DiagnosticLog.Write($"tray window created hwnd=0x{_hwnd.ToInt64():X}");
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == NativeMethods.WM_TRAYICON)
        {
            var mouse = lParam.ToInt32() & 0xFFFF;
            switch (mouse)
            {
                case NativeMethods.WM_LBUTTONUP:
                case NativeMethods.WM_RBUTTONUP:
                case NativeMethods.WM_CONTEXTMENU:
                    ShowMenu();
                    break;

                case NativeMethods.NIN_BALLOONUSERCLICK:
                    OnBalloonClicked();
                    break;
            }

            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted and dropped every tray icon; re-add ours.
            DiagnosticLog.Write("taskbar recreated -> re-adding tray icon");
            AddIcon();

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---- Shell notification icon --------------------------------------------

    private void AddIcon()
    {
        var data = NewIconData();
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
        _iconVisible = true;
    }

    private void RemoveIcon()
    {
        if (!_iconVisible || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _iconVisible = false;
    }

    private NativeMethods.NOTIFYICONDATA NewIconData()
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = NativeMethods.WM_TRAYICON,
            hIcon = _icon,
            szTip = Branding.Name,
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    // ---- Icon rendering ------------------------------------------------------

    private void RebuildIcon()
    {
        var previous = _icon;
        _icon = TrayIconRenderer.Create(_hwnd);
        if (previous != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(previous);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _presenceTimer?.Stop();
        _settingsWindow?.Close();
        _settingsWindow = null;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= _userPreferenceChanged;
        _previewAccessWarningWindow?.Dismiss(PreviewAccessWarningDismissalReason.Disposed);
        _previewAccessWarningWindow = null;

        if (_hwnd != IntPtr.Zero)
        {
            RemoveIcon();
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }

        DiagnosticLog.Write("tray icon disposed");
    }
}

/// <summary>
/// Draws the tray glyph at the taskbar's real pixel size and hands back an
/// HICON: light on a dark taskbar, dark on a light one. The taskbar follows
/// SystemUsesLightTheme, not the app-theme value the palettes track.
/// </summary>
internal static class TrayIconRenderer
{
    public static IntPtr Create(IntPtr hwnd)
    {
        // Render at the icon's true pixel size so it stays crisp at 100% and 150%.
        var dpi = hwnd != IntPtr.Zero ? NativeMethods.GetDpiForWindow(hwnd) : 96u;
        if (dpi == 0)
        {
            dpi = 96;
        }

        var size = (int)Math.Round(16 * dpi / 96.0);
        size = Math.Max(16, size);

        var visual = BuildVisual(size);
        var bitmap = new RenderTargetBitmap(size, size, dpi, dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        return ToHIcon(bitmap, size);
    }

    private static DrawingVisual BuildVisual(int size)
    {
        var visual = new DrawingVisual();
        using var dc = visual.RenderOpen();

        double s = size;
        var fill = new SolidColorBrush(TaskbarIsLight()
            ? Color.FromRgb(0x2A, 0x2C, 0x33)
            : Color.FromRgb(0xF2, 0xF3, 0xF6));
        fill.Freeze();

        // The brand glint, scaled from its -50..50 box into the icon square.
        // Just the main spark: at tray sizes the companion would be a smudge.
        var spark = Geometry.Parse(Preview.BrandMark.SparkPath).Clone();
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(s * 0.92 / 100.0, s * 0.92 / 100.0));
        transform.Children.Add(new TranslateTransform(s / 2.0, s / 2.0));
        spark.Transform = transform;
        dc.DrawGeometry(fill, null, spark);

        return visual;
    }

    private static bool TaskbarIsLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr ToHIcon(RenderTargetBitmap bitmap, int size)
    {
        // Pull BGRA pixels out of WPF and hand them to GDI as the icon's colour
        // bitmap. A zeroed mask keeps the per-pixel alpha (32-bit icons ignore it).
        var stride = size * 4;
        var pixels = new byte[stride * size];
        bitmap.CopyPixels(pixels, stride, 0);

        var colorBitmap = NativeMethods.CreateBitmap(size, size, 1, 32, IntPtr.Zero);
        var maskBitmap = NativeMethods.CreateBitmap(size, size, 1, 1, IntPtr.Zero);

        try
        {
            var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                NativeMethods.SetBitmapBits(colorBitmap, (uint)pixels.Length, pinned.AddrOfPinnedObject());
            }
            finally
            {
                pinned.Free();
            }

            var info = new NativeMethods.ICONINFO
            {
                fIcon = true,
                xHotspot = 0,
                yHotspot = 0,
                hbmMask = maskBitmap,
                hbmColor = colorBitmap
            };

            return NativeMethods.CreateIconIndirect(ref info);
        }
        finally
        {
            NativeMethods.DeleteObject(colorBitmap);
            NativeMethods.DeleteObject(maskBitmap);
        }
    }
}
