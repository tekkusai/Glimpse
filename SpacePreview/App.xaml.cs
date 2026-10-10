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

        // Dev-only modes (the probe, browse-test) take over the process.
        if (Dev.ProbeRunner.TryRun(this, e.Args))
        {
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
