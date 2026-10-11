using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Win32;
using SpacePreview.Native;
using Windows.Networking.Connectivity;

namespace SpacePreview.Services;

/// <summary>
/// The PC's half of what an automatic install waits on, read fresh from
/// the app each minute. The release's half is <see cref="AppUpdater.InstallsWhenAway"/>.
/// </summary>
internal readonly record struct InstallState
{
    public bool PreviewOpen { get; init; }

    public bool FullscreenSuspended { get; init; }

    public NativeMethods.UserNotificationState Notifications { get; init; }

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

    /// <summary>The pending release's installer, verified on disk.</summary>
    public string? Installer { get; init; }

    /// <summary>The user pressed Install now for the pending release, which waives its two days.</summary>
    public bool InstallNowPressed { get; init; }

    /// <summary>The last Install now ended without starting setup.</summary>
    public bool InstallFailed { get; init; }

    /// <summary>The last Check for updates got no answer, and no check has since.</summary>
    public bool CheckFailed { get; init; }

    /// <summary>The most recent check, scheduled or asked, got no answer. Nothing installs on its own until one does.</summary>
    public bool Unanswered { get; init; }

    /// <summary>Install now is under way, or setup is.</summary>
    public bool Installing { get; init; }

    /// <summary>Setup is on its way, and Glimpse closes for a few seconds.</summary>
    public bool SetupStarted { get; init; }
}

/// <summary>
/// Keeps Glimpse current without asking. It checks GitHub, verifies the
/// release key's signature, downloads in the background on an unmetered
/// connection (on a metered one it uses only an installer already on
/// disk) and installs while the user is away. It never shows a window:
/// Settings reads <see cref="State"/>.
/// </summary>
internal sealed class AppUpdater : IDisposable
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);
    internal static readonly TimeSpan ReleaseAgeBeforeInstall = TimeSpan.FromHours(48);
    internal static readonly TimeSpan IdleBeforeInstall = TimeSpan.FromMinutes(3);
    internal static readonly TimeSpan RetryInstallAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan CheckIsFreshFor = TimeSpan.FromMinutes(1);
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
    private Task<bool>? _check;
    private DateTimeOffset _downloadRetryAt;
    private bool _disposed;

    /// <param name="observe">The PC's half, <see cref="InstallState"/>: what is open, full screen, the shell's notification state and idle.</param>
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
        _checkTimer = NewTimer(CheckInterval, () => _ = CheckThenTickAsync(asked: false));
        _checkTimer.Start();
        _retryTimer = NewTimer(RetryAfterFailure, () => _ = CheckThenTickAsync(asked: false));
        _gateTimer = NewTimer(GateInterval, () => _ = TickAsync());
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _ = CheckSoonAsync();
    }

    /// <summary>The Check for updates link: a check that gets no answer says so.</summary>
    public Task CheckNowAsync() => CheckThenTickAsync(asked: true);

    private async Task CheckThenTickAsync(bool asked)
    {
        // Install now asks GitHub itself, and setup ends this run.
        if (!State.Installing && await CheckAsync(asked) && State.Pending is not null)
        {
            await TickAsync();
        }
    }

    /// <summary>
    /// Asks GitHub for the latest release and verifies its signature, every
    /// time: a release pulled, replaced or unsigned since the last check
    /// stops being pending. True when GitHub answered. One at a time: a
    /// caller while one runs gets its answer.
    /// </summary>
    private Task<bool> CheckAsync(bool asked) =>
        _check is { IsCompleted: false } running ? running : _check = RunCheckAsync(asked);

    private async Task<bool> RunCheckAsync(bool asked)
    {
        if (_disposed)
        {
            return false;
        }

        _retryTimer?.Stop();
        Set(State with { Checking = true });
        try
        {
            var latest = await _service.CheckForUpdateAsync(_lifetime.Token);
            var pending = latest is null ? null : await VerifyAsync(latest);
            Settings.Update(settings => settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow });
            Set(State with { Checking = false, CheckFailed = false, Unanswered = false, UpToDate = latest is null, Pending = pending });
            DiagnosticLog.Write(pending is null
                ? $"update check current={_service.CurrentVersionText}"
                : $"update available current={_service.CurrentVersionText} latest={pending.VersionText}");
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // App shutdown owns the cancellation.
            return false;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"update check unavailable: {ex.GetType().Name}: {ex.Message}");
            Set(State with { Checking = false, CheckFailed = State.CheckFailed || asked, Unanswered = true });
            _retryTimer?.Start();
            return false;
        }
    }

    /// <summary>
    /// Settings' Install now: downloads if needed, makes the same test as
    /// an automatic install and installs at once, in either mode. Unsaved
    /// signing work comes forward and nothing installs.
    /// </summary>
    public async Task InstallNowAsync()
    {
        if (_disposed || State.Pending is not { } release || State.Installing)
        {
            return;
        }

        Set(State with { InstallNowPressed = true, InstallFailed = false });
        if (!_canQuit())
        {
            return;
        }

        Set(State with { Installing = true });
        var installer = State.Installer ?? await DownloadAsync(release, download: true);

        // A release pulled or unsigned since the last check does not
        // install, and the footer says what is true now. Signing may have
        // started meanwhile. Only a failed download or no answer from
        // GitHub is a failed install.
        if (installer is null || !await ConfirmAsync(release) || !_canQuit())
        {
            Set(State with { Installing = false, InstallFailed = State.Pending == release && (installer is null || State.Unanswered) });
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

        if (State.Installer is null)
        {
            // Metered, only an installer already on disk will do: checking
            // it costs the sidecar and .sig, not the download.
            var download = !_isMetered();
            if (_download is not { IsCompleted: false }
                && DateTimeOffset.UtcNow >= _downloadRetryAt
                && (download || _service.IsOnDisk(release)))
            {
                await DownloadAsync(release, download);
            }

            return;
        }

        bool InstallsNow() =>
            !State.Installing
            && InstallsWhenAway(State, Settings.Current, DateTimeOffset.UtcNow)
            && IsAway(_observe());

        // A release pulled late in its two days must not install.
        if (InstallsNow() && await ConfirmAsync(release) && State.Installer is { } installer && InstallsNow())
        {
            await LaunchAsync(release, installer, asked: false);
        }
    }

    /// <summary>
    /// The test before any launch, automatic or Install now: GitHub still
    /// offers this release, signed, or it is no longer pending. A check
    /// that found it within the last minute is that answer, so a scheduled
    /// check that opens the gate does not ask twice. A clock set back is
    /// no answer.
    /// </summary>
    private async Task<bool> ConfirmAsync(AppUpdateRelease release) =>
        State.Pending == release
        && (!State.Unanswered
            && DateTimeOffset.UtcNow - Settings.Current.LastUpdateCheckUtc is { } age
            && age >= TimeSpan.Zero
            && age < CheckIsFreshFor
            || await CheckAsync(asked: false) && State.Pending == release);

    /// <summary>
    /// The release's half of an automatic install, and what Settings says
    /// about it: the switch is on, the verified installer is on disk, the
    /// most recent check reached GitHub (the launch asks it again), the
    /// release has been public for 48 hours or the user pressed Install
    /// now, and no setup of this version ran in the last day. When this
    /// holds, the update installs the next time the user is away. A check
    /// with no answer closes it until one answers; the next is 30 minutes on.
    /// </summary>
    internal static bool InstallsWhenAway(UpdateState state, Settings settings, DateTimeOffset now) =>
        state.Pending is { } release
        && state.Installer is not null
        && !state.Unanswered
        && settings.InstallUpdatesAutomatically
        && (state.InstallNowPressed || release.PublishedAt is { } published && now - published >= ReleaseAgeBeforeInstall)
        && !TriedRecently(settings, release, now);

    /// <summary>
    /// The PC's half: nothing of Glimpse is on screen, nobody has touched
    /// the keyboard or mouse for 3 minutes, and either the shell says nobody
    /// is at the screen (locked, a screen saver, another user's session) or
    /// Windows would show a notification and nothing is full screen. The
    /// lock screen and a screen saver cover the monitor themselves, so the
    /// shell's word outranks Glimpse's own full-screen hold.
    /// </summary>
    internal static bool IsAway(InstallState pc) =>
        !pc.PreviewOpen
        && (pc.Notifications == NativeMethods.UserNotificationState.NotPresent
            || pc.Notifications == NativeMethods.UserNotificationState.AcceptsNotifications && !pc.FullscreenSuspended)
        && pc.Idle >= IdleBeforeInstall;

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

    /// <summary>
    /// One download at a time. Install now waits out one that ends with
    /// nothing (the switch cancelled it, or it only looked on disk) and
    /// starts its own.
    /// </summary>
    /// <param name="download">False: use only an installer already on disk.</param>
    private async Task<string?> DownloadAsync(AppUpdateRelease release, bool download)
    {
        while (_download is { IsCompleted: false } running)
        {
            var installer = await running;
            if (installer is not null || !download)
            {
                return installer;
            }
        }

        return State.Pending == release ? await (_download = RunDownloadAsync(release, download)) : null;
    }

    private async Task<string?> RunDownloadAsync(AppUpdateRelease release, bool download)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _downloadCancel = cancel;
        try
        {
            // Downloading only once the installer itself is on its way: the
            // sidecar, the .sig and a look at one already on disk are not.
            var installer = await _service.DownloadAndVerifyAsync(
                release,
                download,
                () => Set(State with { Downloading = true }),
                cancel.Token);
            if (installer is null || State.Pending != release)
            {
                return null;
            }

            DiagnosticLog.Write($"update {(download ? "downloaded" : "found on disk")} version={release.VersionText}");
            Set(State with { Installer = installer });
            return installer;
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
            // A newer download may already own the state. One the switch
            // stopped while Install now waits on it hands over still
            // Downloading: the footer never says Installing in between.
            if (_downloadCancel == cancel)
            {
                _downloadCancel = null;
                if (!cancel.IsCancellationRequested || !State.Installing)
                {
                    Set(State with { Downloading = false });
                }
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
        Set(State with { Installing = true, SetupStarted = true });
        try
        {
            await _launch(release, installer, _lifetime.Token);
            _ = NoticeSetupThatNeverQuitAsync(release, asked);
        }
        catch (UpdateRejectedException ex)
        {
            Set(State with { Installing = false, SetupStarted = false });
            Skip(release, ex);
        }
        catch (Exception ex)
        {
            // The file vanished or changed on disk: fetch it again.
            DiagnosticLog.Write($"update install failed version={release.VersionText}: {ex.GetType().Name}: {ex.Message}");
            Set(State with { Installer = null, Installing = false, SetupStarted = false, InstallFailed = asked });
        }
    }

    // Setup quits Glimpse within seconds; still here means it failed early.
    private async Task NoticeSetupThatNeverQuitAsync(AppUpdateRelease release, bool asked)
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
        Set(State with { Installer = null, Installing = false, SetupStarted = false, InstallFailed = asked });
    }

    private void Skip(AppUpdateRelease release, Exception reason)
    {
        DiagnosticLog.Write($"update skipped version={release.VersionText}: {reason.Message}");
        Settings.Update(settings => settings with { SkippedUpdateVersion = release.VersionText });
        if (State.Pending?.Version == release.Version)
        {
            Set(State with { Pending = null, Installing = false });
        }
    }

    private void Set(UpdateState state)
    {
        // Another release, or none, starts clean: the download, the
        // installer, the press and its failure belonged to the old one.
        if (state.Pending != State.Pending)
        {
            _downloadCancel?.Cancel();
            _download = null;
            _downloadRetryAt = default;
            state = state with { Installer = null, InstallNowPressed = false, InstallFailed = false };
        }

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
            await CheckThenTickAsync(asked: false);
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
