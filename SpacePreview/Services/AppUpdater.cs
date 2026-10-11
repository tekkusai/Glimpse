using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace SpacePreview.Services;

/// <summary>
/// Everything an automatic install waits on, read fresh each minute. The
/// PC's half comes from the app; the release's half from the updater.
/// </summary>
internal readonly record struct InstallState
{
    public TimeSpan ReleaseAge { get; init; }

    public bool InstallNowPressed { get; init; }

    /// <summary>This version's setup already ran in the last day and Glimpse is still the old one.</summary>
    public bool TriedRecently { get; init; }

    public bool PreviewOpen { get; init; }

    public bool FullscreenSuspended { get; init; }

    /// <summary>Not presenting, not in a full-screen game, not in quiet time.</summary>
    public bool AcceptsNotifications { get; init; }

    public TimeSpan Idle { get; init; }
}

/// <summary>Where an update stands, as Settings shows it.</summary>
internal sealed record UpdateState
{
    public bool Checking { get; init; }

    /// <summary>A check this run reached GitHub and found nothing newer.</summary>
    public bool UpToDate { get; init; }

    /// <summary>A newer release whose signature and checksum verified.</summary>
    public AppUpdateRelease? Pending { get; init; }

    public bool Downloading { get; init; }

    /// <summary>Setup is on its way; Glimpse closes for a few seconds.</summary>
    public bool Installing { get; init; }
}

/// <summary>
/// Keeps Glimpse current without asking. It checks GitHub, verifies the
/// release key's signature, downloads in the background on an unmetered
/// connection and installs while the user is away. It never shows a
/// window: Settings reads <see cref="State"/>.
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ReleaseAgeBeforeInstall = TimeSpan.FromHours(48);
    internal static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan RetryInstallAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan GateInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SetupQuitsWithin = TimeSpan.FromMinutes(2);

    private readonly AppUpdateService _service;
    private readonly Func<InstallState> _observe;
    private readonly Func<bool> _canQuit;
    private readonly Func<bool> _isMetered;
    private readonly Func<AppUpdateRelease, string, CancellationToken, Task> _launch;
    private readonly CancellationTokenSource _lifetime = new();
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _checkTimer;
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _gateTimer;
    private CancellationTokenSource? _downloadCancel;
    private Task<string?>? _download;
    private string? _installer;
    private DateTimeOffset _downloadRetryAt;
    private bool _installNowPressed;
    private bool _disposed;

    /// <param name="observe">The PC's side of <see cref="InstallState"/>: what is open, full screen, quiet time and idle.</param>
    /// <param name="canQuit">Unsaved signing work comes forward and says no.</param>
    /// <param name="launch">Starts the verified installer; tests stand in for it.</param>
    public AppUpdater(
        AppUpdateService service,
        Func<InstallState>? observe = null,
        Func<bool>? canQuit = null,
        Func<bool>? isMetered = null,
        Func<AppUpdateRelease, string, CancellationToken, Task>? launch = null)
    {
        _service = service;
        _observe = observe ?? (() => new InstallState { PreviewOpen = true });
        _canQuit = canQuit ?? (() => true);
        _isMetered = isMetered ?? ConnectionIsMetered;
        _launch = launch ?? service.LaunchInstallerAsync;
    }

    public UpdateState State { get; private set; } = new();

    public Version CurrentVersion => _service.CurrentVersion;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or the automatic setting changes.</summary>
    public event Action? Changed;

    /// <summary>Checks 4 s after launch, every 6 hours, 30 minutes after a failure, and on waking when the last check is old.</summary>
    public void Start()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service.CleanupStaleDownloads();
        _checkTimer = NewTimer(CheckInterval, () => _ = CheckNowAsync());
        _checkTimer.Start();
        _retryTimer = NewTimer(RetryAfterFailure, () => _ = CheckNowAsync());
        _gateTimer = NewTimer(GateInterval, () => _ = TickAsync());
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = CheckSoonAsync();
    }

    /// <summary>The Check for updates link, and every scheduled check.</summary>
    public async Task CheckNowAsync()
    {
        if (_disposed || State.Checking || State.Installing)
        {
            return;
        }

        _retryTimer?.Stop();
        Set(State with { Checking = true });
        try
        {
            var latest = await _service.CheckForUpdateAsync(_lifetime.Token);
            var pending = latest is null ? null
                : latest.Version == State.Pending?.Version ? State.Pending
                : await VerifyAsync(latest);
            Settings.Update(settings => settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow });
            if (pending?.Version != State.Pending?.Version)
            {
                DropDownload();
            }

            Set(State with { Checking = false, UpToDate = latest is null, Pending = pending });
            DiagnosticLog.Write(pending is null
                ? $"update check current={_service.CurrentVersionText}"
                : $"update available current={_service.CurrentVersionText} latest={pending.VersionText}");
            if (pending is not null)
            {
                await TickAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App shutdown owns the cancellation.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"update check unavailable: {ex.GetType().Name}: {ex.Message}");
            Set(State with { Checking = false });
            _retryTimer?.Start();
        }
    }

    /// <summary>
    /// Settings' Install now: downloads if needed and installs at once,
    /// in either mode. Unsaved signing work comes forward and nothing installs.
    /// </summary>
    public async Task InstallNowAsync()
    {
        if (_disposed || State.Pending is not { } release || State.Installing)
        {
            return;
        }

        _installNowPressed = true;
        if (!_canQuit())
        {
            return;
        }

        Set(State with { Installing = true });
        var installer = _installer ?? await DownloadAsync(release);

        // Signing may have started while it downloaded.
        if (installer is null || !_canQuit())
        {
            Set(State with { Installing = false });
            return;
        }

        await LaunchAsync(release, installer, asked: true);
    }

    /// <summary>Off: checks still run, and nothing downloads or installs until Install now.</summary>
    public void SetInstallAutomatically(bool automatic)
    {
        Settings.Update(settings => settings with { InstallUpdatesAutomatically = automatic });
        if (!automatic && !State.Installing)
        {
            _downloadCancel?.Cancel();
        }

        Changed?.Invoke();
        if (automatic)
        {
            _ = TickAsync();
        }
    }

    /// <summary>
    /// The minute's look while an update is pending: download when the
    /// connection allows, install when the user is away.
    /// </summary>
    internal async Task TickAsync()
    {
        if (_disposed
            || State.Pending is not { } release
            || State.Installing
            || !Settings.Current.InstallUpdatesAutomatically)
        {
            return;
        }

        if (_installer is null)
        {
            if (_download is not { IsCompleted: false }
                && DateTimeOffset.UtcNow >= _downloadRetryAt
                && !_isMetered())
            {
                await DownloadAsync(release);
            }

            return;
        }

        var state = _observe() with
        {
            ReleaseAge = release.PublishedAt is { } published ? DateTimeOffset.UtcNow - published : TimeSpan.Zero,
            InstallNowPressed = _installNowPressed,
            TriedRecently = TriedRecently(Settings.Current, release, DateTimeOffset.UtcNow)
        };
        if (CanInstallNow(state))
        {
            await LaunchAsync(release, _installer, asked: false);
        }
    }

    /// <summary>
    /// Whether an automatic install may start now. The release has been
    /// public for 48 hours (or the user asked for it), nothing of Glimpse
    /// is on screen, nothing is full screen, Windows would show a
    /// notification, and nobody has touched the keyboard or mouse for 3
    /// minutes.
    /// </summary>
    internal static bool CanInstallNow(InstallState state) =>
        (state.InstallNowPressed || state.ReleaseAge >= ReleaseAgeBeforeInstall)
        && !state.TriedRecently
        && !state.PreviewOpen
        && !state.FullscreenSuspended
        && state.AcceptsNotifications
        && state.Idle >= IdleBeforeInstall;

    /// <summary>
    /// A setup that fails starts the old Glimpse again, which would find
    /// the same update a minute later. Once a day is enough to retry.
    /// </summary>
    internal static bool TriedRecently(Settings settings, AppUpdateRelease release, DateTimeOffset now) =>
        settings.UpdateAttemptVersion == release.VersionText
        && settings.UpdateAttemptUtc is { } attempt
        && now - attempt < RetryInstallAfter;

    /// <summary>
    /// Once per launch, before the welcome. A version newer than the last
    /// one seen means an update landed, however it was installed. 2.x kept
    /// no version, so someone who has seen the welcome came from 2.x.
    /// </summary>
    internal static Settings RecordLaunch(Settings settings, Version current, DateTimeOffset now)
    {
        var seen = AppUpdateService.TryParseVersion(settings.LastVersionSeen, out var last) ? last
            : settings.WelcomeShown ? new Version(2, 0, 0)
            : null;
        var updated = seen is not null && current > seen;
        var same = seen == current;
        return settings with
        {
            LastVersionSeen = current.ToString(3),
            LastUpdateCheckUtc = settings.LastUpdateCheckUtc ?? now,
            UpdatedAtUtc = updated ? now : same ? settings.UpdatedAtUtc : null,
            MajorUpdateNoticeDue = updated ? current.Major > seen!.Major : same && settings.MajorUpdateNoticeDue
        };
    }

    internal static bool CheckIsDueOnResume(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || now - last > CheckInterval;

    /// <summary>Windows calls the connection metered. Unknown counts as unmetered.</summary>
    internal static bool ConnectionIsMetered()
    {
        try
        {
            var cost = NetworkInformation.GetInternetConnectionProfile()?.GetConnectionCost();
            return cost?.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"connection cost unavailable: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    internal static void OpenReleasePage(Version version)
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppUpdateService.ReleasePageUri(version).AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"release page did not open: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _checkTimer?.Stop();
        _retryTimer?.Stop();
        _gateTimer?.Stop();
        if (_dispatcher is not null)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }

        _lifetime.Cancel();
        _service.Dispose();
    }

    private async Task<AppUpdateRelease?> VerifyAsync(AppUpdateRelease release)
    {
        if (Settings.Current.SkippedUpdateVersion == release.VersionText)
        {
            return null;
        }

        try
        {
            await _service.VerifyAsync(release, _lifetime.Token);
            DiagnosticLog.Write($"update verified version={release.VersionText}");
            return release;
        }
        catch (UpdateRejectedException ex)
        {
            Skip(release, ex);
            return null;
        }
    }

    private Task<string?> DownloadAsync(AppUpdateRelease release) =>
        _download is { IsCompleted: false } running ? running : _download = RunDownloadAsync(release);

    private async Task<string?> RunDownloadAsync(AppUpdateRelease release)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancel = cancel;
        Set(State with { Downloading = true });
        try
        {
            var installer = await _service.DownloadAndVerifyAsync(release, cancel.Token);
            if (State.Pending?.Version != release.Version)
            {
                return null;
            }

            DiagnosticLog.Write($"update downloaded version={release.VersionText}");
            return _installer = installer;
        }
        catch (UpdateRejectedException ex)
        {
            Skip(release, ex);
            return null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"update download failed version={release.VersionText}: {ex.GetType().Name}: {ex.Message}");
            _downloadRetryAt = DateTimeOffset.UtcNow + RetryAfterFailure;
            return null;
        }
        finally
        {
            // A newer download may already own the state.
            if (_downloadCancel == cancel)
            {
                _downloadCancel = null;
                Set(State with { Downloading = false });
            }
        }
    }

    private async Task LaunchAsync(AppUpdateRelease release, string installer, bool asked)
    {
        DiagnosticLog.Write($"update install starting version={release.VersionText} by={(asked ? "install now" : "away")}");

        // Written first: setup ends this process within seconds.
        Settings.Update(settings => settings with
        {
            UpdateAttemptVersion = release.VersionText,
            UpdateAttemptUtc = DateTimeOffset.UtcNow
        });
        Set(State with { Installing = true });
        try
        {
            await _launch(release, installer, _lifetime.Token);
            _ = NoticeSetupThatNeverQuitAsync(release);
        }
        catch (UpdateRejectedException ex)
        {
            Set(State with { Installing = false });
            Skip(release, ex);
        }
        catch (Exception ex)
        {
            // The file vanished or changed on disk: fetch it again.
            DiagnosticLog.Write($"update install failed version={release.VersionText}: {ex.GetType().Name}: {ex.Message}");
            _installer = null;
            Set(State with { Installing = false });
        }
    }

    // Setup quits Glimpse within seconds; still here means it failed early.
    private async Task NoticeSetupThatNeverQuitAsync(AppUpdateRelease release)
    {
        try
        {
            await Task.Delay(SetupQuitsWithin, _lifetime.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DiagnosticLog.Write($"update setup did not finish version={release.VersionText}");
        _installer = null;
        Set(State with { Installing = false });
    }

    private void Skip(AppUpdateRelease release, Exception reason)
    {
        DiagnosticLog.Write($"update skipped version={release.VersionText}: {reason.Message}");
        Settings.Update(settings => settings with { SkippedUpdateVersion = release.VersionText });
        if (State.Pending?.Version == release.Version)
        {
            DropDownload();
            Set(State with { Pending = null, Installing = false });
        }
    }

    private void DropDownload()
    {
        _downloadCancel?.Cancel();
        _download = null;
        _installer = null;
        _installNowPressed = false;
        _downloadRetryAt = default;
    }

    private void Set(UpdateState state)
    {
        if (state == State)
        {
            return;
        }

        State = state;
        if (_gateTimer is not null)
        {
            _gateTimer.IsEnabled = state.Pending is not null && !_disposed;
        }

        Changed?.Invoke();
    }

    private async Task CheckSoonAsync()
    {
        try
        {
            await Task.Delay(FirstCheckDelay, _lifetime.Token);
            await CheckNowAsync();
        }
        catch (OperationCanceledException)
        {
            // App shutdown owns the cancellation.
        }
    }

    // The 6-hour timer may have slept through its turn, and Wi-Fi takes a
    // moment to come back: an old check runs a few seconds after waking.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _dispatcher?.BeginInvoke(() =>
        {
            if (!_disposed && CheckIsDueOnResume(Settings.Current.LastUpdateCheckUtc, DateTimeOffset.UtcNow))
            {
                _ = CheckSoonAsync();
            }
        });
    }

    private static DispatcherTimer NewTimer(TimeSpan interval, Action tick)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = interval };
        timer.Tick += (_, _) => tick();
        return timer;
    }
}
