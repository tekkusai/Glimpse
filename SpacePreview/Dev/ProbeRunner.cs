using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Dev;

/// <summary>
/// The dev modes, kept out of the product types. --probe renders the real
/// preview window for a file to a PNG, off screen, without hooks or
/// Explorer. --browse-test drives a real Explorer window through the
/// production browse path. Both ship in the exe because the regression
/// scripts run against the Release build, and both own the process they
/// start in: it exits when they finish.
/// </summary>
internal static class ProbeRunner
{
    /// <summary>True when the arguments name a dev mode, which now runs the process.</summary>
    public static bool TryRun(Application app, string[] args)
    {
        if (args.Length >= 3 && IsSwitch(args[0], "--probe"))
        {
            // Probes render what the user would see, in the saved theme.
            Theme.Use(Settings.Current.Theme);
            _ = RunProbeSafelyAsync(app, args[1], args[2], args[3..]);
            return true;
        }

        if (args.Length >= 2 && IsSwitch(args[0], "--browse-test"))
        {
            _ = BrowseTestAsync(app, args[1]);
            return true;
        }

        return false;
    }

    private static bool IsSwitch(string arg, string name) => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase);

    private static async Task RunProbeSafelyAsync(Application app, string path, string outputPng, string[] steps)
    {
        try
        {
            await ProbePreviewAsync(path, outputPng, steps);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            DiagnosticLog.Write($"probe FATAL pid={Environment.ProcessId}: {ex}");
        }
        finally
        {
            app.Shutdown(Environment.ExitCode);
        }
    }

    /// <summary>
    /// Opens the file, runs the optional steps in order, then captures the
    /// whole window (chrome plus web content) about 2.5 s after the last one.
    /// </summary>
    private static async Task ProbePreviewAsync(string path, string outputPng, string[] steps)
    {
        DiagnosticLog.Write($"probe starting pid={Environment.ProcessId} path={path}");
        var window = new PreviewWindow();
        window.ShowPreview(path, IntPtr.Zero);
        // Park it far off screen and freeze its geometry so later transitions
        // cannot recenter it onto the desktop.
        window.Left = -30000;
        window.MarkGeometryUserPinnedForProbe();
        await Task.Delay(2500);

        foreach (var step in steps)
        {
            await RunStepAsync(window, path, step);
        }

        await Task.Delay(2500);
        await window.ProbeCompositeShotAsync(outputPng);
        DiagnosticLog.Write($"probe rendered {path} -> {outputPng}");
    }

    private static async Task RunStepAsync(PreviewWindow window, string path, string step)
    {
        var (verb, value) = step.IndexOf(':') is var colon and > 0
            ? (step[..colon].ToLowerInvariant(), step[(colon + 1)..])
            : (step.ToLowerInvariant(), string.Empty);

        switch (verb)
        {
            case "delay":
                await Task.Delay(int.Parse(value, CultureInfo.InvariantCulture));
                break;

            case "evaljs":
                await window.ProbeEvalJsAsync(value);
                break;

            case "zoomin":
                window.ZoomInButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                await Task.Delay(250);
                break;

            case "webshot":
                await window.ProbeWebShotAsync(value);
                break;

            case "size":
                var parts = value.Split(',');
                window.Width = double.Parse(parts[0], CultureInfo.InvariantCulture);
                window.Height = double.Parse(parts[1], CultureInfo.InvariantCulture);
                window.MarkGeometryUserPinnedForProbe();
                window.UpdateLayout();
                await Task.Delay(120);
                break;

            case "multiselect":
                var count = Math.Max(2, int.Parse(value, CultureInfo.InvariantCulture));
                window.SetMultiSelection(Enumerable.Repeat(window.CurrentPath ?? path, count).ToArray(), 0);
                window.SetSelectionCount(count);
                window.UpdateLayout();
                break;

            case "audit":
                DiagnosticLog.Write($"probe audit {window.ProbeAuditState(value)}");
                break;

            case "buttonaudit":
                DiagnosticLog.Write($"probe buttons {window.ProbeButtonAudit()}");
                break;

            case "zoomkey":
                // The window's Ctrl+Plus, Ctrl+Minus and Ctrl+0 path.
                window.TryAdjustZoomFromShortcut(value switch
                {
                    "+" => Native.NativeMethods.VK_OEM_PLUS,
                    "-" => Native.NativeMethods.VK_OEM_MINUS,
                    _ => Native.NativeMethods.VK_0
                });
                await Task.Delay(250);
                break;

            case "imagetoggle":
                window.ProbeImageToggle();
                await Task.Delay(250);
                break;

            case "meshspin":
                DiagnosticLog.Write($"probe meshspin {await window.ProbeMeshSpinAsync()}");
                break;

            case "pinnedoff":
                // Leaves the window pinned with Always on top off through the
                // final capture, so a live pass can verify the reveal visuals
                // and taskbar/Alt+Tab reachability from outside the process.
                // Like a click, it becomes the next pin's default.
                window.SetPinned(true);
                window.AlwaysOnTopButton.IsChecked = false;
                window.UpdateLayout();
                await Task.Delay(120);
                break;

            case "settingsshot":
                await CaptureSettingsAsync(value);
                break;

            case "framesshot":
                await CaptureFramesOnScreenAsync(window, value);
                break;

            case "warningshot":
            case "warningfinalshot":
                await CaptureWarningAsync(value, finalReminder: verb == "warningfinalshot");
                break;

            case "overlayrace":
                if (!await window.ProbeLoadingOverlayRaceAsync())
                {
                    throw new InvalidOperationException("The previous load hid the current loading overlay.");
                }

                break;

            case "welcome":
                window.ShowWelcome();
                window.Left = -30000;
                await Task.Delay(120);
                break;

            case "sign":
            case "signdate":
            case "signpress":
                DiagnosticLog.Write($"probe {verb} -> {await window.ProbeFillSignAsync(verb, value)}");
                break;

            case "show":
                window.ShowPreview(value, IntPtr.Zero);
                window.Left = -30000;
                break;

            case "hide":
                // Closes the preview the way Space does, so a later show:
                // measures a fresh open rather than a browse.
                window.HidePreview();
                await Task.Delay(150);
                break;

            case "fullshot":
                await window.ProbeCompositeShotAsync(value);
                break;

            case "shot":
                // Mid-sequence capture: catches transient states (the ghost
                // loading overlay) the final screenshot never sees.
                CaptureWindow(window, value);
                break;

            default:
                // A bare path previews that file next, like an arrow press.
                window.ShowPreview(step, IntPtr.Zero);
                window.Left = -30000;
                await Task.Delay(120);
                break;
        }
    }

    /// <summary>Renders the settings window off screen. Stub callbacks: the probe never touches live settings.</summary>
    private static async Task CaptureSettingsAsync(string outputPng)
    {
        var settings = new SettingsWindow(() => null, () => { }, _ => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = 0,
            ShowActivated = false
        };
        settings.Show();
        await Task.Delay(150);
        CaptureWindow(settings, outputPng);
        settings.Close();
    }

    /// <summary>
    /// The window frame (rounded corners, edge, shadow) is drawn by DWM
    /// outside the client area, so no off-screen render can show it. This
    /// puts the preview and Settings on screen for a moment, side by side
    /// over a plain backdrop, without taking focus, and copies the screen:
    /// once over a dark backdrop (-dark.png) and once over a light one
    /// (-light.png).
    /// </summary>
    private static async Task CaptureFramesOnScreenAsync(PreviewWindow window, string outputPng)
    {
        const double Gap = 48;
        var area = SystemParameters.WorkArea;
        var settings = new SettingsWindow(() => null, () => { }, _ => { })
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false
        };
        var backdrop = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Left = area.Left + Gap,
            Top = area.Top + Gap
        };

        var previewLeft = window.Left;
        try
        {
            backdrop.Width = Gap + window.Width + Gap + settings.Width + Gap;
            backdrop.Height = Gap + Math.Max(window.Height, 320) + Gap;
            backdrop.Show();
            window.Left = backdrop.Left + Gap;
            window.Top = backdrop.Top + Gap;
            // Re-entering the topmost band raises it over the backdrop.
            window.Topmost = false;
            window.Topmost = true;
            settings.Left = window.Left + window.Width + Gap;
            settings.Top = window.Top;
            settings.Topmost = true;
            settings.Show();

            var stem = Path.Combine(Path.GetDirectoryName(outputPng) ?? ".", Path.GetFileNameWithoutExtension(outputPng));
            foreach (var (name, color) in new[] { ("dark", Color.FromRgb(0x1E, 0x1E, 0x1E)), ("light", Color.FromRgb(0xE6, 0xE6, 0xE6)) })
            {
                backdrop.Background = new SolidColorBrush(color);
                await Task.Delay(600);
                CopyScreen(backdrop, $"{stem}-{name}.png");
            }
        }
        finally
        {
            settings.Close();
            backdrop.Close();
            window.Topmost = false;
            window.Left = previewLeft;
        }
    }

    private static void CopyScreen(Window area, string outputPng)
    {
        var toDevice = PresentationSource.FromVisual(area)?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        var topLeft = toDevice.Transform(new Point(area.Left, area.Top));
        var size = toDevice.Transform(new Point(area.ActualWidth, area.ActualHeight));
        int x = (int)topLeft.X, y = (int)topLeft.Y, width = (int)size.X, height = (int)size.Y;

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var bitmap = CreateCompatibleBitmap(screen, width, height);
        var previous = SelectObject(memory, bitmap);
        try
        {
            // SRCCOPY | CAPTUREBLT: layered windows (the preview while it fades) included.
            BitBlt(memory, 0, 0, width, height, screen, x, y, 0x40CC0020);
            SelectObject(memory, previous);
            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = File.Create(outputPng);
            encoder.Save(stream);
        }
        finally
        {
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int rop);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    /// <summary>Renders either access-notice state off screen.</summary>
    private static async Task CaptureWarningAsync(string outputPng, bool finalReminder)
    {
        var info = new PreviewAccessWarningInfo("probe", "Discord", 0, PreviewAccessWarningKind.ConfirmedElevationMismatch);
        var notification = new PreviewAccessWarningWindow(info, finalReminder)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = 0
        };
        notification.Loaded += (_, _) =>
        {
            // Its own Loaded placement runs first; this override keeps the
            // probe copy off the desktop.
            notification.Left = -30000;
            notification.Top = 0;
        };
        notification.Show();
        await Task.Delay(150);
        CaptureWindow(notification, outputPng);
        notification.Dismiss(PreviewAccessWarningDismissalReason.Probe);
    }

    private static void CaptureWindow(Window window, string outputPng)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(outputPng);
        encoder.Save(stream);
    }

    private static async Task BrowseTestAsync(Application app, string folder)
    {
        try
        {
            var files = Directory.GetFiles(folder)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (files.Length < 4)
            {
                DiagnosticLog.Write("browse-test: needs at least 4 files");
                return;
            }

            // Open the folder with its second item selected - the state a
            // real browse always starts from (a selection exists).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "explorer.exe", $"/select,\"{files[1]}\"")
            { UseShellExecute = true });

            var hwnd = IntPtr.Zero;
            for (var attempt = 0; attempt < 40 && hwnd == IntPtr.Zero; attempt++)
            {
                await Task.Delay(250);
                hwnd = await StaTask.Run(() => FindExplorerWindowForFolder(folder));
            }

            DiagnosticLog.Write($"browse-test window 0x{hwnd.ToInt64():X}");
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            await Task.Delay(700);
            var read = await StaTask.Run(() => ExplorerSelectionService.TryGetSelectionFromExplorerWindow(hwnd));
            DiagnosticLog.Write(read is null
                ? "browse-test initial read -> NULL"
                : $"browse-test initial read -> {Path.GetFileName(read.Path)} index={read.ViewIndex} count={read.ViewCount}");
            foreach (var delta in new[] { 1, 1, -1, -9, 2 })
            {
                var result = await StaTask.Run(() => ExplorerSelectionService.TryBrowseRelative(hwnd, false, delta));
                DiagnosticLog.Write(result is null
                    ? $"browse-test delta {delta:+0;-0} -> NULL"
                    : $"browse-test delta {delta:+0;-0} -> {Path.GetFileName(result.Path)} index={result.ViewIndex} count={result.ViewCount}");
                await Task.Delay(200);
            }

            // Grow the selection (current item + view indexes 0 and 4) and
            // read it back: within-selection browsing stays on the carried
            // set arriving in view order.
            var multi = await StaTask.Run(() => ExplorerSelectionService.DescribeMultiSelectionForTest(hwnd, 0, 4));
            DiagnosticLog.Write($"browse-test multi -> {multi ?? "NULL"}");

            Native.NativeMethods.SendMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            DiagnosticLog.Write("browse-test done");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"browse-test failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            app.Shutdown();
        }
    }

    private static IntPtr FindExplorerWindowForFolder(string folder)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType is null)
            {
                return IntPtr.Zero;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            var windows = shell.Windows();
            foreach (dynamic window in (System.Collections.IEnumerable)windows)
            {
                try
                {
                    if (window.LocationURL is not string location || string.IsNullOrEmpty(location))
                    {
                        continue;
                    }

                    var path = new Uri(location).LocalPath;
                    if (string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    {
                        return new IntPtr(Convert.ToInt64(window.HWND));
                    }
                }
                catch
                {
                    // Entries mid-navigation answer with errors; skip them.
                }
            }
        }
        catch
        {
            // No shell, no test.
        }

        return IntPtr.Zero;
    }
}
