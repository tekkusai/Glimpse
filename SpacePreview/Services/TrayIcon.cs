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
/// The whole tray presence: a shell notification icon backed by a hidden
/// <see cref="TrayWindow"/>, a compact WPF flyout, and ownership of the
/// settings and notification windows opened from that surface. Manages its GDI
/// handles and cleans them up on <see cref="Dispose"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private static readonly TimeSpan PresencePollInterval = TimeSpan.FromSeconds(5);

    private readonly TrayWindow _window;
    private readonly ContextMenu _menu;
    private readonly AppUpdater _updates;

    // Quitting ends the app. Unsaved signing work comes forward first and
    // holds it.
    private readonly Func<bool> _canQuit;
    private IntPtr _icon;
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
        _updates = updates;
        _canQuit = canQuit ?? (() => true);

        _menu = BuildMenu();

        // A new taskbar may sit at a new scale, so draw for it, then add.
        _window = new TrayWindow(
            ShowMenu,
            taskbarCreated: () =>
            {
                RebuildIcon();
                AddIcon();
            },
            redraw: () => Application.Current?.Dispatcher.BeginInvoke(new Action(RefreshIcon)),
            balloonClicked: OnBalloonClicked);
        RebuildIcon();
        AddIcon();
        ShowMajorUpdateNoticeOnReturn();
    }

    // The taskbar's own theme (a separate setting from app theme) picks the
    // glyph color, and its scale the glyph's size. A broadcast holds up the
    // app that sent it, so the redraw waits for this thread's next turn.
    private void RefreshIcon()
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
            ShellNotifyIcon(NativeMethods.NIM_MODIFY, ref data);
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
        NativeMethods.SetForegroundWindow(_window.Handle);
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
        if (!ShowBalloon(title, text))
        {
            DiagnosticLog.Write("update notice failed");
            return;
        }

        _balloonIsUpdateNotice = true;
        Settings.Update(settings => settings with { MajorUpdateNoticeDue = false });
        DiagnosticLog.Write($"update notice shown version={_updates.CurrentVersion.ToString(3)}");
    }

    /// <summary>
    /// A word about something the user can no longer see in a preview, such
    /// as a rotation that did not save after its picture had gone.
    /// </summary>
    public void ShowNotice(string title, string text)
    {
        if (!ShowBalloon(title, text))
        {
            DiagnosticLog.Write("tray notice failed");
            return;
        }

        _balloonIsUpdateNotice = false;
    }

    // Quiet by rule: no sound, and never during Windows' quiet time.
    private bool ShowBalloon(string title, string text)
    {
        var data = NewIconData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = title;
        data.szInfo = text;
        data.dwInfoFlags = NativeMethods.NIIF_INFO
            | NativeMethods.NIIF_NOSOUND
            | NativeMethods.NIIF_RESPECT_QUIET_TIME;
        return !_disposed && _iconVisible && ShellNotifyIcon(NativeMethods.NIM_MODIFY, ref data);
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
        if (ShellNotifyIcon(NativeMethods.NIM_MODIFY, ref data))
        {
            DiagnosticLog.Write($"preview access warning shown kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
            return true;
        }

        DiagnosticLog.Write($"preview access warning failed kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
        return false;
    }

    // ---- Shell notification icon --------------------------------------------

    internal delegate bool ShellNotify(int message, ref NativeMethods.NOTIFYICONDATA data);

    /// <summary>Every call to the shell about the icon. Tests swap in a fake; production asks the shell.</summary>
    internal static ShellNotify ShellNotifyIcon { get; set; } = NativeMethods.Shell_NotifyIcon;

    private void AddIcon()
    {
        var data = NewIconData();

        // An icon still in place refuses the add; the change carries the new
        // drawing instead.
        if (!ShellNotifyIcon(NativeMethods.NIM_ADD, ref data))
        {
            ShellNotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }

        _iconVisible = true;
    }

    private void RemoveIcon()
    {
        if (!_iconVisible)
        {
            return;
        }

        var data = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _window.Handle,
            uID = IconId
        };
        ShellNotifyIcon(NativeMethods.NIM_DELETE, ref data);
        _iconVisible = false;
    }

    private NativeMethods.NOTIFYICONDATA NewIconData()
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _window.Handle,
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
        _icon = TrayIconRenderer.Create();
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
        _previewAccessWarningWindow?.Dismiss(PreviewAccessWarningDismissalReason.Disposed);
        _previewAccessWarningWindow = null;

        RemoveIcon();
        _window.Dispose();

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
    public static IntPtr Create() => ToHIcon(Render(TaskbarDpi(), TaskbarIsLight()));

    /// <summary>The glyph at the tray's pixel size for <paramref name="dpi"/>.</summary>
    internal static BitmapSource Render(uint dpi, bool lightTaskbar) => BrandMark.RenderGlyph(
        NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi),
        lightTaskbar ? Color.FromRgb(0x2A, 0x2C, 0x33) : Color.FromRgb(0xF2, 0xF3, 0xF6));

    // The tray lives on the taskbar's monitor. The hidden tray window sits
    // wherever it was made, so its own DPI is no answer.
    private static uint TaskbarDpi()
    {
        var monitor = NativeMethods.MonitorFromWindow(
            NativeMethods.FindWindow("Shell_TrayWnd", null),
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        return NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpi, out _) == 0 ? dpi : 96;
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

    private static IntPtr ToHIcon(BitmapSource bitmap)
    {
        // Pull BGRA pixels out of WPF and hand them to GDI as the icon's colour
        // bitmap. A zeroed mask keeps the per-pixel alpha (32-bit icons ignore it).
        var size = bitmap.PixelWidth;
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
