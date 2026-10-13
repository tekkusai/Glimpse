using System.Windows;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview;

public partial class App : Application
{
    private SingleInstance? _instance;
    private SpacePreviewController? _controller;
    private TrayIcon? _trayIcon;
    private AppUpdater? _updater;
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
        // native code, a FATAL line points at ours. Placed work still
        // waiting for its draft is written on the way down.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            DiagnosticLog.Write($"FATAL unhandled: {args.ExceptionObject}");
            SignDraftKeeper.FlushAll();
        };
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
            // Launching again opens Settings in the running instance.
            SingleInstance.SignalShow();
            Shutdown();
            return;
        }

        DiagnosticLog.TagProcess(null);
        DiagnosticLog.Write("app starting");

        // The palette reaches application resources before anything renders,
        // and every window and popup reads it from there.
        Theme.Use(Settings.Current.Theme);
        _ = WebViewEnvironment.WarmAsync();
        _ = Task.Run(GeneratedHtmlPreview.WarmSyntax);

        _quitEvent = SingleInstance.CreateQuitEvent();
        _quitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _quitEvent,
            (_, _) => Dispatcher.BeginInvoke(QuitForSetup),
            null,
            Timeout.Infinite,
            executeOnlyOnce: true);

        _showEvent = SingleInstance.CreateShowEvent();
        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            _showEvent,
            (_, _) => Dispatcher.BeginInvoke(() => _trayIcon?.OpenSettings()),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);

        // Before the welcome: a first launch is an install, not an update.
        var launch = AppUpdater.RecordLaunch(Settings.Current, AppUpdateService.DetectCurrentVersion(), DateTimeOffset.UtcNow);
        if (launch != Settings.Current)
        {
            Settings.Update(_ => launch);
        }

        try
        {
            _controller = new SpacePreviewController(Dispatcher);
            _trayIcon = CreateTrayIcon();
            _controller.PreviewAccessWarningDetected += warning =>
                _trayIcon?.ShowPreviewAccessWarning(warning);
            PreviewWindow.RotationNotSavedUnseen += (title, text) => _trayIcon?.ShowNotice(title, text);
            _controller.Start();
            CacheHousekeeping.Start();

            // Copies of files from a phone or a zip folder never outlive
            // the session that made them. Space is already answered by
            // now, so only what is older than this start goes: a copy
            // asked for since is dated from the asking.
            var started = System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();
            _ = Task.Run(() => BorrowedFiles.Clear(started));

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

    /// <summary>
    /// Setup's --quit cannot wait for an answer: setup force-kills a resident
    /// still running five seconds later. Unsaved signing work goes to its
    /// draft and comes back as Resume.
    /// </summary>
    private void QuitForSetup()
    {
        KeepSigningWork("installer quit");
        Shutdown();
    }

    /// <summary>
    /// Windows closes every window past any question when the session ends.
    /// Placed work goes to its draft in milliseconds, and the session is
    /// never held up.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        KeepSigningWork("session ending");
        base.OnSessionEnding(e);
    }

    private static void KeepSigningWork(string reason)
    {
        if (SignDraftKeeper.FlushAll())
        {
            DiagnosticLog.Write($"{reason} kept unsaved signing work as a draft");
        }
    }

    private TrayIcon CreateTrayIcon()
    {
        Func<bool> canQuit = () => _controller?.CanQuit() != false;
        _updater = new AppUpdater(new AppUpdateService(), ObserveForUpdate, canQuit);
        var tray = new TrayIcon(_updater, canQuit);

        tray.ThemeChanged += preference =>
        {
            Theme.Use(preference);
            Settings.Update(settings => settings with { Theme = preference });
        };

        tray.QuitRequested += () => Shutdown();
        _updater.Start();
        return tray;
    }

    private InstallState ObserveForUpdate() => new()
    {
        PreviewOpen = _controller?.NoPreviewOpen != true,
        FullscreenSuspended = _controller?.InputSuspendedForFullscreen != false,
        Notifications = NativeMethods.QueryUserNotificationState(),
        Idle = NativeMethods.TimeSinceLastInput()
    };

    protected override void OnExit(ExitEventArgs e)
    {
        // First, before anything that can wait: whatever ends the app,
        // placed work is on disk.
        KeepSigningWork("app exit");

        // Helpers, the probe and installer commands exit through here too,
        // but only the resident has hooks, a tray and a shell session to
        // tear down. A helper's exit was 'app exiting' in the user's log and
        // a shell thread started to dispose nothing.
        if (_controller is not null)
        {
            DiagnosticLog.Write("app exiting");
            _trayIcon?.Dispose();
            _updater?.Dispose();
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

        // The windows handed their last turns to the disk as they closed.
        // A write cut off here would leave its working file beside the
        // user's picture, so it gets the same two seconds to land.
        if (!ImageRotationSession.WaitForSaves(TimeSpan.FromSeconds(2)))
        {
            DiagnosticLog.Write("rotation save still running at exit");
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
