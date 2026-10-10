using System.Windows;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview;

public partial class App : Application
{
    private SingleInstance? _instance;
    private SpacePreviewController? _controller;
    private TrayIcon? _trayIcon;
    private EventWaitHandle? _quitEvent;
    private RegisteredWaitHandle? _quitRegistration;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // A crash must name itself: a log that simply stops points at
        // native code, a FATAL line points at ours.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticLog.Write($"FATAL unhandled: {args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) =>
            DiagnosticLog.Write($"FATAL dispatcher: {args.Exception}");

        // Merged manually: without a StartupUri the generated Main() never
        // calls InitializeComponent, so App.xaml resources stay unloaded.
        foreach (var source in new[] { "Themes/Scrollbars.xaml", "Themes/Menus.xaml", "Themes/Chrome.xaml" })
        {
            Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{source}")
            });
        }

        // Dev-only: render the real preview window for a file to a PNG, off
        // screen, without hooks or Explorer. Diagnoses layout issues that
        // resist desk-checking without driving the user's desktop. Applies
        // the saved theme so probes render what the user would see.
        if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--probe", StringComparison.OrdinalIgnoreCase))
        {
            Theme.Preference = Settings.Current.Theme;
            _ = RunProbeSafelyAsync(e.Args[1], e.Args[2]);
            return;
        }

        // Dev-only: proves the folder-view browse mechanism against a real
        // Explorer window (opens the folder, steps the selection through the
        // production browse path, logs every landing, closes the window).
        if (e.Args.Length >= 2 && string.Equals(e.Args[0], "--browse-test", StringComparison.OrdinalIgnoreCase))
        {
            _ = BrowseTestAsync(e.Args[1], e.Args.Length >= 3 ? e.Args[2] : null);
            return;
        }

        // Dev-only: selects one existing desktop file, compares the current
        // ShellFolderView automation reader with native IFolderView, restores
        // the prior selection, and proves one forced cache rebuild.
        if (e.Args.Length >= 3
            && string.Equals(e.Args[0], "--desktop-selection-ab", StringComparison.OrdinalIgnoreCase))
        {
            var iterations = e.Args.Length >= 4
                ? int.Parse(e.Args[3], System.Globalization.CultureInfo.InvariantCulture)
                : 12;
            _ = DesktopSelectionAbTestAsync(e.Args[1], e.Args[2], iterations);
            return;
        }

        // Dev-only reverse-order control for an already-selected desktop item.
        if (e.Args.Length >= 3
            && string.Equals(e.Args[0], "--desktop-current-ab", StringComparison.OrdinalIgnoreCase))
        {
            var iterations = int.Parse(e.Args[2], System.Globalization.CultureInfo.InvariantCulture);
            var nativeFirst = e.Args.Length >= 4
                && string.Equals(e.Args[3], "native", StringComparison.OrdinalIgnoreCase);
            _ = DesktopCurrentSelectionAbTestAsync(e.Args[1], iterations, nativeFirst);
            return;
        }

        // Helper mode for document page thumbnails. Converted Office PDFs can
        // crash pdfium in native code, so thumbnailing them happens in a short
        // child process instead of the resident preview process.
        if (e.Args.Length >= 4 && string.Equals(e.Args[0], "--render-pdf-thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            RenderPdfThumbnailsAndExit(e.Args);
            return;
        }

        // Same reasoning, sharper case: the document being proved is one this
        // app wrote seconds earlier, so it is the likeliest input in the whole
        // program to make pdfium unhappy. A crash here has to cost a fallback,
        // not the resident preview.
        if (e.Args.Length >= 3 && string.Equals(e.Args[0], "--prove-pdf-scale", StringComparison.OrdinalIgnoreCase))
        {
            ProvePdfScaleAndExit(e.Args);
            return;
        }

        if (HandleCommand(e.Args))
        {
            Shutdown();
            return;
        }

        _instance = SingleInstance.Acquire();
        if (!_instance.IsPrimary)
        {
            // Relaunching restores a hidden tray icon on the running instance.
            SingleInstance.SignalShow();
            Shutdown();
            return;
        }

        DiagnosticLog.Write("app starting");

        // Persisted preferences apply before anything renders so the first preview
        // (and the tray menu) already reflect the user's theme choice. The palette
        // also lands in application resources as a safety net for popup trees
        // (tooltips) whose DynamicResource lookup might miss a window.
        Theme.Preference = Settings.Current.Theme;
        Theme.Refresh();
        Theme.ApplyResources(Resources);
        _ = WebViewEnvironment.WarmAsync();
        _ = Task.Run(GeneratedHtmlPreview.WarmSyntax);

        _quitEvent = SingleInstance.CreateQuitEvent();
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _quitEvent,
            (_, _) => Dispatcher.BeginInvoke(() => Shutdown()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _showEvent = SingleInstance.CreateShowEvent();
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _trayIcon?.RestoreIcon()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        try
        {
            _controller = new SpacePreviewController(Dispatcher);
            _trayIcon = CreateTrayIcon();
            _controller.PreviewAccessWarningDetected += warning =>
                _trayIcon?.ShowPreviewAccessWarning(warning);
            _controller.Start();
            _ = Task.Run(() =>
            {
                CacheHousekeeping.Run();
            });

            // Once, right after install: the app introduces itself with its
            // own preview window and is dismissed with the gesture it teaches.
            if (!Settings.Current.WelcomeShown)
            {
                Settings.Update(settings => settings with { WelcomeShown = true });
                Dispatcher.BeginInvoke(() => _controller?.ShowWelcome(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"startup failed: {ex}");
            MessageBox.Show(
                $"{Branding.Name} could not start. Try launching it again.\n\nDetails were saved to the log:\n{DiagnosticLog.Pathname}",
                Branding.Name,
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void RenderPdfThumbnailsAndExit(string[] args)
    {
        var exitCode = 1;
        try
        {
            var pageLimit = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            var startPage = args.Length > 4
                ? int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture)
                : 0;
            exitCode = PdfPreviewRenderer.TryRenderThumbnailsToDirectory(
                args[1],
                args[2],
                pageLimit,
                CancellationToken.None,
                startPage)
                ? 0
                : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf thumbnail helper failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private void ProvePdfScaleAndExit(string[] args)
    {
        var exitCode = 1;
        try
        {
            exitCode = PdfPreviewRenderer.RendersLike(args[1], args[2], CancellationToken.None) ? 0 : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf proof helper failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private async Task BrowseTestAsync(string folder, string? outputPath = null)
    {
        try
        {
            var files = System.IO.Directory.GetFiles(folder)
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
                : $"browse-test initial read -> {System.IO.Path.GetFileName(read.Path)} index={read.ViewIndex} count={read.ViewCount}");
            if (read is not null)
            {
                var neighbors = await StaTask.Run(() => ExplorerSelectionService.TryGetNeighborPaths(hwnd, false, read.ViewIndex, 4));
                var neighborLine = neighbors is null
                    ? "browse-test neighbors -> NULL"
                    : $"browse-test neighbors -> {string.Join(" | ", neighbors)}";
                DiagnosticLog.Write(neighborLine);
                if (!string.IsNullOrWhiteSpace(outputPath))
                {
                    System.IO.File.AppendAllText(outputPath, neighborLine + Environment.NewLine);
                }
            }

            foreach (var delta in new[] { 1, 1, -1, -9, 2 })
            {
                var result = await StaTask.Run(() => ExplorerSelectionService.TryBrowseRelative(hwnd, false, delta));
                DiagnosticLog.Write(result is null
                    ? $"browse-test delta {delta:+0;-0} -> NULL"
                    : $"browse-test delta {delta:+0;-0} -> {System.IO.Path.GetFileName(result.Path)} index={result.ViewIndex} count={result.ViewCount}");
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
            Shutdown();
        }
    }

    private async Task DesktopSelectionAbTestAsync(
        string outputPath,
        string targetPath,
        int iterations)
    {
        var exitCode = 1;
        try
        {
            var result = await StaTask.Run(() =>
                ExplorerSelectionService.RunDesktopSelectionAbTest(targetPath, iterations));
            var directory = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(
                result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await System.IO.File.WriteAllTextAsync(outputPath, json);
            DiagnosticLog.Write(
                $"desktop selection A/B success={result.Success} cacheMiss={result.CacheMissMilliseconds:0.00}ms " +
                $"cacheHit={result.CacheHitMedianMilliseconds:0.00}ms firstAutomation={result.FirstAutomationMilliseconds:0.00}ms " +
                $"firstNative={result.FirstNativeMilliseconds:0.00}ms automation={result.AutomationMedianMilliseconds:0.00}ms " +
                $"native={result.NativeMedianMilliseconds:0.00}ms restored={result.SelectionRestored} rebuild={result.ForcedRebuildSucceeded}");
            exitCode = result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"desktop selection A/B failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Shutdown(exitCode);
        }
    }

    private async Task DesktopCurrentSelectionAbTestAsync(
        string outputPath,
        int iterations,
        bool nativeFirst)
    {
        var exitCode = 1;
        try
        {
            var result = await StaTask.Run(() =>
                ExplorerSelectionService.RunCurrentDesktopSelectionAbTest(iterations, nativeFirst));
            var directory = System.IO.Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            await System.IO.File.WriteAllTextAsync(
                outputPath,
                System.Text.Json.JsonSerializer.Serialize(
                    result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            DiagnosticLog.Write(
                $"desktop current A/B success={result.Success} first={result.FirstReader} cacheMiss={result.CacheMissMilliseconds:0.00}ms " +
                $"firstAutomation={result.FirstAutomationMilliseconds:0.00}ms firstNative={result.FirstNativeMilliseconds:0.00}ms");
            exitCode = result.Success ? 0 : 2;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"desktop current A/B failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Shutdown(exitCode);
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

    private async Task RunProbeSafelyAsync(string path, string outputPng)
    {
        try
        {
            await ProbePreviewAsync(path, outputPng);
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            DiagnosticLog.Write($"probe FATAL pid={Environment.ProcessId}: {ex}");
            Shutdown();
        }
    }

    private async Task ProbePreviewAsync(string path, string outputPng)
    {
        DiagnosticLog.Write($"probe starting pid={Environment.ProcessId} path={path}");
        var window = new Preview.PreviewWindow();
        window.ShowPreview(path, IntPtr.Zero);
        // Park it far off screen and freeze its geometry so later transitions
        // cannot recenter it onto the desktop.
        window.Left = -30000;
        window.MarkGeometryUserPinnedForProbe();
        await Task.Delay(2500);

        // Optional steps cover transitions, sizing, paint, and layout audits.
        var extras = Environment.GetCommandLineArgs().Skip(4).ToArray();
        foreach (var extra in extras)
        {
            if (extra.StartsWith("delay:", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(int.Parse(extra["delay:".Length..]));
            }
            else if (extra.StartsWith("evaljs:", StringComparison.OrdinalIgnoreCase))
            {
                await window.ProbeEvalJsAsync(extra["evaljs:".Length..]);
            }
            else if (string.Equals(extra, "zoomin", StringComparison.OrdinalIgnoreCase))
            {
                window.ZoomInButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(250);
            }
            else if (extra.StartsWith("webshot:", StringComparison.OrdinalIgnoreCase))
            {
                await window.ProbeWebShotAsync(extra["webshot:".Length..]);
            }
            else if (extra.StartsWith("size:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = extra["size:".Length..].Split(',');
                window.Width = double.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
                window.Height = double.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                window.MarkGeometryUserPinnedForProbe();
                window.UpdateLayout();
                await Task.Delay(120);
            }
            else if (extra.StartsWith("multiselect:", StringComparison.OrdinalIgnoreCase))
            {
                var count = Math.Max(2, int.Parse(extra["multiselect:".Length..], System.Globalization.CultureInfo.InvariantCulture));
                window.SetMultiSelection(Enumerable.Repeat(window.CurrentPath ?? path, count).ToArray(), 0);
                window.SetSelectionCount(count);
                window.UpdateLayout();
            }
            else if (extra.StartsWith("audit:", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write($"probe audit {window.ProbeAuditState(extra["audit:".Length..])}");
            }
            else if (string.Equals(extra, "buttonaudit", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog.Write($"probe buttons {window.ProbeButtonAudit()}");
            }
            else if (string.Equals(extra, "pinnedoff", StringComparison.OrdinalIgnoreCase))
            {
                // Leaves the window pinned with Always on top off through the
                // final capture, so a live pass can verify the reveal visuals
                // and taskbar/Alt+Tab reachability from outside the process.
                window.SetPinned(true);
                window.AlwaysOnTopButton.IsChecked = false;
                window.UpdateLayout();
                await Task.Delay(120);
            }
            else if (extra.StartsWith("settingsshot:", StringComparison.OrdinalIgnoreCase))
            {
                // Renders the settings window off-screen for design review.
                // Stub callbacks: the probe never touches live settings.
                var settings = new SettingsWindow(() => null, () => { }, _ => { })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -30000,
                    Top = 0,
                    ShowActivated = false
                };
                settings.Show();
                await Task.Delay(150);
                CaptureWindow(settings, extra["settingsshot:".Length..]);
                settings.Close();
            }
            else if (extra.StartsWith("warningshot:", StringComparison.OrdinalIgnoreCase)
                || extra.StartsWith("warningfinalshot:", StringComparison.OrdinalIgnoreCase))
            {
                // Renders either notification state off-screen for design review.
                const string firstPrefix = "warningshot:";
                const string finalPrefix = "warningfinalshot:";
                var finalReminder = extra.StartsWith(finalPrefix, StringComparison.OrdinalIgnoreCase);
                var outputPath = extra[(finalReminder ? finalPrefix.Length : firstPrefix.Length)..];
                var info = new PreviewAccessWarningInfo(
                    "probe",
                    "Discord",
                    0,
                    PreviewAccessWarningKind.ConfirmedElevationMismatch);
                var notification = new PreviewAccessWarningWindow(info, finalReminder)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -30000,
                    Top = 0
                };
                notification.Loaded += (_, _) =>
                {
                    // Its own Loaded placement runs first; this override keeps
                    // the probe copy off the desktop.
                    notification.Left = -30000;
                    notification.Top = 0;
                };
                notification.Show();
                await Task.Delay(150);
                CaptureWindow(notification, outputPath);
                notification.Dismiss(PreviewAccessWarningDismissalReason.Probe);
            }
            else if (string.Equals(extra, "overlayrace", StringComparison.OrdinalIgnoreCase))
            {
                if (!await window.ProbeLoadingOverlayRaceAsync())
                {
                    throw new InvalidOperationException("The previous load hid the current loading overlay.");
                }
            }
            else if (string.Equals(extra, "welcome", StringComparison.OrdinalIgnoreCase))
            {
                window.ShowWelcome();
                window.Left = -30000;
                await Task.Delay(120);
            }
            else if (extra.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
            {
                window.ShowPreview(extra["show:".Length..], IntPtr.Zero);
                window.Left = -30000;
            }
            else if (string.Equals(extra, "hide", StringComparison.OrdinalIgnoreCase))
            {
                // Closes the preview the way Space does, so a later show:
                // measures a fresh open rather than a browse.
                window.HidePreview();
                await Task.Delay(150);
            }
            else if (extra.StartsWith("fullshot:", StringComparison.OrdinalIgnoreCase))
            {
                await window.ProbeCompositeShotAsync(extra["fullshot:".Length..]);
            }
            else if (extra.StartsWith("shot:", StringComparison.OrdinalIgnoreCase))
            {
                // Mid-sequence capture: catches transient states (the ghost
                // loading overlay) the final screenshot never sees.
                CaptureWindow(window, extra["shot:".Length..]);
            }
            else
            {
                window.ShowPreview(extra, IntPtr.Zero);
                window.Left = -30000;
                await Task.Delay(120);
            }
        }

        await Task.Delay(2500);

        await window.ProbeCompositeShotAsync(outputPng);
        DiagnosticLog.Write($"probe rendered {path} -> {outputPng}");
        Shutdown();
    }

    private static (int Width, int Height) CaptureWindow(Window window, string outputPng)
    {
        var width = (int)Math.Ceiling(window.ActualWidth);
        var height = (int)Math.Ceiling(window.ActualHeight);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
            width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = System.IO.File.Create(outputPng))
        {
            encoder.Save(stream);
        }

        return (width, height);
    }

    private TrayIcon CreateTrayIcon()
    {
        var tray = new TrayIcon(
            () => _controller?.CanShowUpdateNotification == true);

        tray.ThemeChanged += preference =>
        {
            Theme.Preference = preference;
            // The next preview open re-resolves the palette; forcing a refresh here
            // keeps Theme.Current current for anything that reads it in between.
            Theme.Refresh();
            Settings.Update(settings => settings with { Theme = preference });
        };

        tray.QuitRequested += () => Shutdown();
        return tray;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DiagnosticLog.Write("app exiting");
        _trayIcon?.Dispose();
        _controller?.Dispose();
        try
        {
            if (!ExplorerSelectionService.ReleaseDesktopSessionAsync().Wait(TimeSpan.FromSeconds(2)))
            {
                DiagnosticLog.Write("desktop shell session cleanup timed out");
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"desktop shell session cleanup failed: {ex.GetType().Name}: {ex.Message}");
        }

        _quitRegistration?.Unregister(null);
        _quitEvent?.Dispose();
        _showRegistration?.Unregister(null);
        _showEvent?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static bool HandleCommand(string[] args)
    {
        if (args.Length == 0)
        {
            return false;
        }

        foreach (var arg in args)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "--quit":
                case "/quit":
                    SingleInstance.SignalQuit();
                    return true;

                case "--install-startup":
                case "/install-startup":
                    StartupRegistration.Install();
                    return true;

                case "--uninstall-startup":
                case "/uninstall-startup":
                    StartupRegistration.Uninstall();
                    return true;
            }
        }

        return false;
    }

}
