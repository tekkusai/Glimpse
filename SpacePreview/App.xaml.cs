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

        // Helpers, the probe and installer commands share the resident's
        // log. Until a process proves it is the resident, its lines carry
        // its first argument.
        DiagnosticLog.TagProcess(e.Args.Length > 0 ? e.Args[0].TrimStart('-', '/') : null);

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

        DiagnosticLog.TagProcess(null);
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
            CacheHousekeeping.Start();

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
        // Helpers, the probe and installer commands exit through here too,
        // but only the resident has hooks, a tray and a shell session to
        // tear down. A helper's exit was 'app exiting' in the user's log and
        // a shell thread started to dispose nothing.
        if (_controller is not null)
        {
            DiagnosticLog.Write("app exiting");
            _trayIcon?.Dispose();
            _controller.Dispose();
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
