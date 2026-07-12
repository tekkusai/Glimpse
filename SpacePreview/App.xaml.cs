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
        foreach (var source in new[] { "Themes/Scrollbars.xaml", "Themes/Menus.xaml" })
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

        // Helper mode for document page thumbnails. Converted Office PDFs can
        // crash pdfium in native code, so thumbnailing them happens in a short
        // child process instead of the resident preview process.
        if (e.Args.Length >= 4 && string.Equals(e.Args[0], "--render-pdf-thumbnails", StringComparison.OrdinalIgnoreCase))
        {
            RenderPdfThumbnailsAndExit(e.Args);
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

        var (width, height) = CaptureWindow(window, outputPng);
        DiagnosticLog.Write($"probe rendered {path} -> {outputPng} ({width}x{height})");
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
            Settings.Current.Theme,
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
