using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

public partial class AppUpdateWindow : Window
{
    private const int WmSysCommand = 0x0112;
    private const long ScClose = 0xF060;

    private readonly AppUpdateService _updateService;
    private readonly AppUpdateRelease _release;
    private readonly Func<bool> _canQuit;
    private CancellationTokenSource? _downloadCancellation;
    private bool _busy;
    private bool _closing;

    internal bool InstallerLaunched { get; private set; }
    internal bool SnoozeRequested { get; private set; }

    internal AppUpdateWindow(AppUpdateService updateService, AppUpdateRelease release, Func<bool>? canQuit = null)
    {
        _updateService = updateService;
        _release = release;
        _canQuit = canQuit ?? (() => true);

        InitializeComponent();
        Title = $"Update {Branding.Name}";
        TitleText.Text = Title;
        BodyText.Text = $"{Branding.Name} will download and verify the update, then close briefly while setup finishes.";
        WindowFrame.AttachDialog(this, TitleBar);
        VersionText.Text = $"Version {release.VersionText} is ready";
        StatusText.Text = $"Download size: {FormatSize(release.InstallerSize)}";

        CloseButton.Click += (_, _) => Dismiss();
        SecondaryButton.Click += (_, _) => OnSecondaryAction();
        PrimaryButton.Click += async (_, _) => await DownloadAndInstallAsync();
        KeyDown += (_, args) =>
        {
            if (args.Key == Key.Escape)
            {
                Dismiss();
            }
        };
        Closing += OnClosing;
    }

    private void Dismiss()
    {
        SnoozeRequested = true;
        Close();
    }

    private void OnSecondaryAction()
    {
        if (_busy)
        {
            CancelDownload();
            return;
        }

        Dismiss();
    }

    private void CancelDownload()
    {
        SnoozeRequested = false;
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessage);
    }

    private IntPtr WindowMessage(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message == WmSysCommand
            && (wParam.ToInt64() & 0xFFF0) == ScClose)
        {
            SnoozeRequested = true;
        }

        return IntPtr.Zero;
    }

    private async Task DownloadAndInstallAsync()
    {
        // Setup closes the app, so unsaved signing work holds the update.
        if (_busy || !_canQuit())
        {
            return;
        }

        _busy = true;
        _downloadCancellation = new CancellationTokenSource();
        SetDownloadingState();

        try
        {
            var progress = new Progress<double>(value =>
            {
                var percent = Math.Clamp(value * 100, 0, 100);
                DownloadProgress.Value = percent;
                StatusText.Text = percent >= 100
                    ? "Verifying update…"
                    : $"Downloading… {Math.Floor(percent):0}%";
            });

            var installer = await _updateService.DownloadAndVerifyAsync(
                _release,
                progress,
                _downloadCancellation.Token);

            StatusText.Text = "Starting setup…";
            await _updateService.LaunchInstallerAsync(
                _release,
                installer,
                _downloadCancellation.Token);

            InstallerLaunched = true;
            DiagnosticLog.Write($"update accepted version={_release.VersionText}");
            Closing -= OnClosing;
            Close();
        }
        catch (OperationCanceledException) when (
            _closing || _downloadCancellation?.IsCancellationRequested == true)
        {
            DiagnosticLog.Write($"update cancelled version={_release.VersionText}");
        }
        catch (OperationCanceledException ex)
        {
            DiagnosticLog.Write(
                $"update failed version={_release.VersionText}: {ex.GetType().Name}: timed out");
            SetFailureState();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write(
                $"update failed version={_release.VersionText}: {ex.GetType().Name}: {ex.Message}");
            if (!_closing)
            {
                SetFailureState();
            }
        }
        finally
        {
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;
            _busy = false;
        }
    }

    private void SetDownloadingState()
    {
        DownloadProgress.Value = 0;
        DownloadProgress.Visibility = Visibility.Visible;
        StatusText.Text = "Preparing download…";
        PrimaryButton.IsEnabled = false;
        PrimaryButton.Content = "Updating…";
        SecondaryButton.Content = "Cancel";
        AutomationProperties.SetName(SecondaryButton, "Cancel update download");
        CloseButton.IsEnabled = true;
    }

    private void SetFailureState()
    {
        DownloadProgress.Visibility = Visibility.Collapsed;
        StatusText.Text = "The update could not be verified or installed. Check your connection and try again.";
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Content = "Try again";
        AutomationProperties.SetName(PrimaryButton, "Try update again");
        SecondaryButton.Content = "Close";
        AutomationProperties.SetName(SecondaryButton, "Close update");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _downloadCancellation?.Cancel();
    }

    internal static string FormatSize(long bytes)
    {
        var megabytes = Math.Max(0, bytes) / (1024d * 1024d);
        return megabytes >= 10
            ? $"{megabytes:0} MB"
            : $"{megabytes:0.0} MB";
    }
}
