using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// The whole tray presence: a shell notification icon backed by a message-only
/// window, and the WPF flyout that doubles as the app's settings. There is no
/// settings window; the menu is the settings. Manages its GDI handles and cleans
/// them up on <see cref="Dispose"/>.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private static readonly TimeSpan UnstablePreviewAccessWarningRepeatInterval = TimeSpan.FromMinutes(10);

    private readonly NativeMethods.WndProc _wndProc;
    private readonly uint _taskbarCreatedMessage;
    private readonly ContextMenu _menu;
    private IntPtr _hwnd;
    private IntPtr _icon;
    private ushort _classAtom;
    private DateTime _menuClosedAt;
    private bool _iconVisible;
    private bool _hiddenForSession;
    private bool _disposed;
    private readonly Dictionary<string, long> _previewAccessWarningTicks = new(StringComparer.OrdinalIgnoreCase);
    private PreviewAccessWarningWindow? _previewAccessWarningWindow;

    // Menu items whose checked state must reflect live state.
    private readonly MenuItem _followSystemItem;
    private readonly MenuItem _lightItem;
    private readonly MenuItem _darkItem;
    private readonly MenuItem _startupItem;
    private readonly MenuItem _versionItem;
    private readonly MenuItem _documentPreviewItem;

    /// <summary>Raised when the user picks a theme. Handler should persist and re-resolve.</summary>
    public event Action<ThemePreference>? ThemeChanged;

    /// <summary>Raised when the user picks Quit.</summary>
    public event Action? QuitRequested;

    public TrayIcon(ThemePreference theme)
    {
        _wndProc = WindowProc;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");

        _followSystemItem = MakeItem("Follow system", () => PickTheme(ThemePreference.FollowSystem));
        _lightItem = MakeItem("Light", () => PickTheme(ThemePreference.Light));
        _darkItem = MakeItem("Dark", () => PickTheme(ThemePreference.Dark));
        _startupItem = MakeItem("Start with Windows", ToggleStartup);
        _versionItem = new MenuItem { IsEnabled = false };
        _documentPreviewItem = new MenuItem { IsEnabled = false };

        _menu = BuildMenu();
        SyncThemeChecks(theme);

        CreateMessageWindow();
        RebuildIcon();
        AddIcon();
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

        // IsCheckable reserves the check gutter in the shared template, so
        // stateful items align while action items sit flush left.
        _startupItem.Style = itemStyle;
        _startupItem.IsCheckable = true;

        var theme = new MenuItem { Header = "Theme", Style = itemStyle };
        foreach (var child in new[] { _followSystemItem, _lightItem, _darkItem })
        {
            child.Style = itemStyle;
            child.IsCheckable = true;
            theme.Items.Add(child);
        }

        var about = new MenuItem { Header = "About", Style = itemStyle };
        _versionItem.Style = itemStyle;
        _documentPreviewItem.Style = itemStyle;

        var copyDiagnostics = MakeItem("Copy diagnostics", CopyDiagnostics);
        copyDiagnostics.Style = itemStyle;

        var openLog = MakeItem("Open log", OpenLog);
        openLog.Style = itemStyle;

        var hideTray = MakeItem("Hide tray icon", HideForSession);
        hideTray.Style = itemStyle;

        about.Items.Add(_versionItem);
        about.Items.Add(_documentPreviewItem);
        about.Items.Add(new Separator { Style = separatorStyle });
        about.Items.Add(copyDiagnostics);
        about.Items.Add(openLog);

        var quit = MakeItem("Quit", () => QuitRequested?.Invoke());
        quit.Style = itemStyle;

        // See ShowMenu: clicking the icon while the menu is open must close
        // it, not dismiss-and-reopen in the same click.
        menu.Closed += (_, _) => _menuClosedAt = DateTime.UtcNow;

        menu.Items.Add(_startupItem);
        menu.Items.Add(theme);
        menu.Items.Add(about);
        menu.Items.Add(hideTray);
        menu.Items.Add(new Separator { Style = separatorStyle });
        menu.Items.Add(quit);
        return menu;
    }

    private void PickTheme(ThemePreference preference)
    {
        SyncThemeChecks(preference);
        ThemeChanged?.Invoke(preference);
    }

    private void SyncThemeChecks(ThemePreference preference)
    {
        _followSystemItem.IsChecked = preference == ThemePreference.FollowSystem;
        _lightItem.IsChecked = preference == ThemePreference.Light;
        _darkItem.IsChecked = preference == ThemePreference.Dark;
    }

    private void ToggleStartup()
    {
        if (StartupRegistration.IsInstalled())
        {
            StartupRegistration.Uninstall();
        }
        else
        {
            StartupRegistration.Install();
        }
    }

    private void ShowMenu()
    {
        // The outside-click dismissal fires on mouse down; the icon's mouse up
        // would then instantly reopen the menu it just closed.
        if (DateTime.UtcNow - _menuClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        // Re-apply the current palette so the flyout matches the live theme, then do
        // the foreground dance: without it the menu will not close on outside click.
        Theme.ApplyResources(_menu);
        foreach (var item in _menu.Items)
        {
            if (item is MenuItem { HasItems: true } parent)
            {
                Theme.ApplyResources(parent);
            }
        }

        // The startup box must reflect the registry, not an assumption: the user may
        // have changed it elsewhere since the menu was last opened.
        _startupItem.IsChecked = StartupRegistration.IsInstalled();
        RefreshAboutItems();

        NativeMethods.SetForegroundWindow(_hwnd);
        _menu.IsOpen = true;
    }

    private void RefreshAboutItems()
    {
        _versionItem.Header = $"Version {GetVersion()}";
        _documentPreviewItem.Header = OfficeDocumentPdfConverter.IsAvailable
            ? $"Documents: {OfficeDocumentPdfConverter.EngineName}"
            : "Documents: basic previews";
    }

    private static string GetVersion()
    {
        try
        {
            var path = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            var version = FileVersionInfo.GetVersionInfo(path).ProductVersion;
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }
        catch
        {
            // Fall through to assembly identity.
        }

        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";
    }

    private static void CopyDiagnostics()
    {
        try
        {
            var engine = OfficeDocumentPdfConverter.EnginePath is { } enginePath
                ? $"{OfficeDocumentPdfConverter.EngineName} ({enginePath})"
                : "Basic previews";

            Clipboard.SetText(
                $"{Branding.Name} diagnostics" + Environment.NewLine
                + $"Version: {GetVersion()}" + Environment.NewLine
                + $"Documents: {engine}" + Environment.NewLine
                + $"Log: {DiagnosticLog.Pathname}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"copy diagnostics failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void OpenLog()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(DiagnosticLog.Pathname);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(DiagnosticLog.Pathname))
            {
                File.WriteAllText(DiagnosticLog.Pathname, string.Empty);
            }

            Process.Start(new ProcessStartInfo(DiagnosticLog.Pathname) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open log failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void HideForSession()
    {
        _hiddenForSession = true;
        _menu.IsOpen = false;
        RemoveIcon();
        DiagnosticLog.Write("tray icon hidden until restart");
    }

    /// <summary>
    /// Launching the app again while it runs brings a hidden icon back:
    /// Relaunching for "where did it go".
    /// </summary>
    public void RestoreIcon()
    {
        if (!_hiddenForSession)
        {
            return;
        }

        _hiddenForSession = false;
        AddIcon();
        DiagnosticLog.Write("tray icon restored by relaunch");
    }

    public void ShowPreviewAccessWarning(PreviewAccessWarningInfo warning)
    {
        var nowTick = Environment.TickCount64;
        var nowUtc = DateTimeOffset.UtcNow;
        var hasPersistedKey = PreviewAccessWarningPolicy.TryBuildPersistedKey(
            warning,
            out var persistedKey);
        if (_disposed)
        {
            return;
        }

        if (!CanShowPreviewAccessWarning(
                warning,
                hasPersistedKey ? persistedKey : null,
                nowUtc,
                nowTick))
        {
            DiagnosticLog.Write(
                $"preview access warning suppressed by reminder policy kind={warning.Kind} process={warning.DisplayName} pid={warning.ProcessId}");
            return;
        }

        var appWarningShown = ShowAppOwnedWarning(warning);
        var trayWarningShown = false;
        if (!_hiddenForSession && _iconVisible)
        {
            trayWarningShown = ShowTrayWarning(warning);
        }

        if (appWarningShown || trayWarningShown)
        {
            RecordPreviewAccessWarningShown(
                warning,
                hasPersistedKey ? persistedKey : null,
                nowUtc,
                nowTick);
        }
        else
        {
            _previewAccessWarningTicks.Remove(TransientPreviewAccessWarningKey(warning));
        }
    }

    private bool CanShowPreviewAccessWarning(
        PreviewAccessWarningInfo warning,
        string? persistedKey,
        DateTimeOffset nowUtc,
        long nowTick)
    {
        if (persistedKey is not null)
        {
            PreviewAccessWarningHistoryEntry? entry = null;
            if (Settings.Current.PreviewAccessWarnings is { } history)
            {
                history.TryGetValue(persistedKey, out entry);
            }

            return PreviewAccessWarningPolicy.CanShow(entry, nowUtc);
        }

        if (!_previewAccessWarningTicks.TryGetValue(
                TransientPreviewAccessWarningKey(warning),
                out var previous))
        {
            return true;
        }

        return HasWarningCooldownElapsed(
            previous,
            nowTick,
            (long)UnstablePreviewAccessWarningRepeatInterval.TotalMilliseconds);
    }

    private void RecordPreviewAccessWarningShown(
        PreviewAccessWarningInfo warning,
        string? persistedKey,
        DateTimeOffset nowUtc,
        long nowTick)
    {
        if (persistedKey is null)
        {
            _previewAccessWarningTicks[TransientPreviewAccessWarningKey(warning)] = nowTick;
            return;
        }

        Settings.Update(current =>
        {
            var history = new Dictionary<string, PreviewAccessWarningHistoryEntry>(
                StringComparer.OrdinalIgnoreCase);
            if (current.PreviewAccessWarnings is { } existing)
            {
                foreach (var pair in existing)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { } entry)
                    {
                        history[pair.Key] = entry;
                    }
                }
            }

            history.TryGetValue(persistedKey, out var previous);
            history[persistedKey] = PreviewAccessWarningPolicy.RecordShown(previous, nowUtc);
            PrunePreviewAccessWarningHistory(history, persistedKey);
            return current with { PreviewAccessWarnings = history };
        });
    }

    private static string TransientPreviewAccessWarningKey(PreviewAccessWarningInfo warning) =>
        $"{warning.Kind}|{warning.Key}";

    private static void PrunePreviewAccessWarningHistory(
        Dictionary<string, PreviewAccessWarningHistoryEntry> history,
        string currentKey)
    {
        while (history.Count > PreviewAccessWarningPolicy.MaximumPersistedApps)
        {
            var oldest = history
                .Where(pair => !string.Equals(pair.Key, currentKey, StringComparison.OrdinalIgnoreCase))
                .MinBy(pair => pair.Value.LastShownUtc);
            if (oldest.Key is null)
            {
                break;
            }

            history.Remove(oldest.Key);
        }
    }

    internal static bool HasWarningCooldownElapsed(long previous, long now, long intervalMilliseconds) =>
        intervalMilliseconds <= 0
        || now < previous
        || now - previous >= intervalMilliseconds;

    private bool ShowAppOwnedWarning(PreviewAccessWarningInfo warning)
    {
        try
        {
            _previewAccessWarningWindow?.Dismiss("replaced");
            var window = new PreviewAccessWarningWindow(warning);
            _previewAccessWarningWindow = window;
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

    private bool ShowTrayWarning(PreviewAccessWarningInfo warning)
    {
        var data = NewIconData();
        data.uFlags = NativeMethods.NIF_INFO;
        data.szInfoTitle = "Preview unavailable for this app";
        data.szInfo = PreviewAccessWarningWindow.BuildMessage(warning);
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
            }

            return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // Explorer restarted and dropped every tray icon; re-add ours.
            if (!_hiddenForSession)
            {
                DiagnosticLog.Write("taskbar recreated -> re-adding tray icon");
                AddIcon();
            }

            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ---- Shell notification icon --------------------------------------------

    private void AddIcon()
    {
        if (_hiddenForSession)
        {
            return;
        }

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
        _previewAccessWarningWindow?.Dismiss("dispose");
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
/// Draws the tray glyph at the taskbar's real pixel size and hands back an HICON.
/// A rounded-rect spacebar key, light for Win11's dark taskbar.
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
        var fill = new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF6));
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
