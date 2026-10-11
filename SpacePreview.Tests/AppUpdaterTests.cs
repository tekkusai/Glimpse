using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Updates as they run on a user's PC, without GitHub and without an
/// install: a recorded release with its installer, sidecar and .sig served
/// from memory, the PC's state chosen by the test, and the launch stood in
/// for. Every test runs on the WPF test thread, where the run's other
/// settings writes happen too.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class AppUpdaterTests
{
    // These tests stand in for every launch; none may start an installer.
    private static readonly Func<AppUpdateRelease, string, CancellationToken, Task> NeverLaunch =
        (_, _, _) => throw new InvalidOperationException("This test never installs.");

    // The PC's half of an automatic install, all met.
    private static readonly InstallState Away = new()
    {
        Notifications = NativeMethods.UserNotificationState.AcceptsNotifications,
        Idle = TimeSpan.FromMinutes(3)
    };

    private static readonly DateTimeOffset Now = new(2026, 10, 10, 19, 0, 0, TimeSpan.Zero);

    private static readonly Settings Automatic = new() { InstallUpdatesAutomatically = true };

    // The release's half, all met: two days old, its installer on disk.
    private static readonly UpdateState Ready = new()
    {
        Pending = Release("3.1.0") with { PublishedAt = Now.AddHours(-48) },
        Installer = @"C:\Updates\Glimpse-Setup-3.1.0.exe"
    };

    [Fact]
    public void AnAutomaticInstallStartsWhenEveryRuleHolds()
    {
        Assert.True(AppUpdater.InstallsWhenAway(Ready, Automatic, Now));
        Assert.True(AppUpdater.IsAway(Away));
    }

    [Fact]
    public void AReleaseWaitsTwoDaysAfterItIsPublished()
    {
        Assert.False(AppUpdater.InstallsWhenAway(Ready, Automatic, Now.AddSeconds(-1)));
        Assert.False(AppUpdater.InstallsWhenAway(Ready with { Pending = Ready.Pending! with { PublishedAt = null } }, Automatic, Now));
    }

    [Fact]
    public void InstallNowWaivesOnlyTheTwoDayWait()
    {
        var pressed = Ready with { Pending = Ready.Pending! with { PublishedAt = Now }, InstallNowPressed = true };

        Assert.True(AppUpdater.InstallsWhenAway(pressed, Automatic, Now));
        Assert.False(AppUpdater.InstallsWhenAway(pressed with { Installer = null }, Automatic, Now));
        Assert.False(AppUpdater.InstallsWhenAway(pressed, Automatic with { UpdateAttemptVersion = "3.1.0", UpdateAttemptUtc = Now }, Now));
    }

    [Fact]
    public void NothingInstallsOnItsOwnWithoutTheInstallerOnDiskOrWithTheSwitchOff()
    {
        Assert.False(AppUpdater.InstallsWhenAway(Ready with { Installer = null }, Automatic, Now));
        Assert.False(AppUpdater.InstallsWhenAway(Ready, Automatic with { InstallUpdatesAutomatically = false }, Now));
        Assert.False(AppUpdater.InstallsWhenAway(new UpdateState(), Automatic, Now));
    }

    [Fact]
    public void AnOpenGlanceOrPinnedWindowHoldsIt() =>
        Assert.False(AppUpdater.IsAway(Away with { PreviewOpen = true }));

    [Fact]
    public void AFullScreenAppHoldsIt() =>
        Assert.False(AppUpdater.IsAway(Away with { FullscreenSuspended = true }));

    [Fact]
    public void FullScreenAppsPresentationModeAndQuietTimeHoldIt()
    {
        foreach (var state in new[]
        {
            NativeMethods.UserNotificationState.Unknown,
            NativeMethods.UserNotificationState.Busy,
            NativeMethods.UserNotificationState.RunningD3DFullScreen,
            NativeMethods.UserNotificationState.PresentationMode,
            NativeMethods.UserNotificationState.QuietTime,
            NativeMethods.UserNotificationState.App
        })
        {
            Assert.False(AppUpdater.IsAway(Away with { Notifications = state }), state.ToString());
        }
    }

    [Fact]
    public void ALockedPcOrAScreenSaverCountsAsAway()
    {
        var locked = Away with { Notifications = NativeMethods.UserNotificationState.NotPresent };

        Assert.True(AppUpdater.IsAway(locked));
        Assert.False(AppUpdater.IsAway(locked with { Idle = TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1) }));
    }

    [Fact]
    public void TheUserMustBeAwayForThreeMinutes() =>
        Assert.False(AppUpdater.IsAway(Away with { Idle = TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(1) }));

    [Fact]
    public void ASetupThatRanInTheLastDayIsNotStartedAgainOnItsOwn()
    {
        var now = DateTimeOffset.UtcNow;
        var release = Release("3.1.0");
        var tried = new Settings { UpdateAttemptVersion = "3.1.0", UpdateAttemptUtc = now.AddHours(-23) };

        Assert.True(AppUpdater.TriedRecently(tried, release, now));
        Assert.False(AppUpdater.TriedRecently(tried with { UpdateAttemptUtc = now.AddHours(-24) }, release, now));
        Assert.False(AppUpdater.TriedRecently(tried with { UpdateAttemptVersion = "3.0.1" }, release, now));
        Assert.False(AppUpdater.InstallsWhenAway(Ready, Automatic with { UpdateAttemptVersion = "3.1.0", UpdateAttemptUtc = Now.AddHours(-23) }, Now));
    }

    [Fact]
    public void TheRulesReadTheLivePc()
    {
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        Assert.Contains("PreviewOpen = _controller?.NoPreviewOpen != true", app, StringComparison.Ordinal);
        Assert.Contains("FullscreenSuspended = _controller?.InputSuspendedForFullscreen != false", app, StringComparison.Ordinal);
        Assert.Contains("Notifications = NativeMethods.QueryUserNotificationState()", app, StringComparison.Ordinal);
        Assert.Contains("Idle = NativeMethods.TimeSinceLastInput()", app, StringComparison.Ordinal);
        Assert.Contains(
            "SystemEvents.PowerModeChanged += OnPowerModeChanged",
            TestSource.Method(@"SpacePreview\Services\AppUpdater.cs", "Start"),
            StringComparison.Ordinal);

        // The calls themselves answer on this PC without throwing.
        Assert.InRange(NativeMethods.TimeSinceLastInput(), TimeSpan.Zero, TimeSpan.FromDays(50));
        Assert.InRange(NativeMethods.QueryUserNotificationState(), NativeMethods.UserNotificationState.NotPresent, NativeMethods.UserNotificationState.App);
        _ = AppUpdater.ConnectionIsMetered();
    }

    [Fact]
    public void WakingChecksOnlyWhenTheLastCheckIsOverSixHoursOld()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(AppUpdater.CheckIsDueOnResume(null, now));
        Assert.True(AppUpdater.CheckIsDueOnResume(now.AddHours(-6).AddMinutes(-1), now));
        Assert.False(AppUpdater.CheckIsDueOnResume(now.AddHours(-5), now));
    }

    [Fact]
    public void AFirstLaunchIsAnInstallNotAnUpdate()
    {
        var now = DateTimeOffset.UtcNow;

        var launched = AppUpdater.RecordLaunch(new Settings(), new Version(3, 0, 0), now);

        Assert.Equal("3.0.0", launched.LastVersionSeen);
        Assert.Null(launched.UpdatedAtUtc);
        Assert.False(launched.MajorUpdateNoticeDue);
        Assert.Equal(now, launched.LastUpdateCheckUtc);
    }

    [Fact]
    public void A2xUserArrivesAsAMajorUpdate()
    {
        // 2.x kept no version, and its users have seen the welcome.
        var now = DateTimeOffset.UtcNow;

        var launched = AppUpdater.RecordLaunch(new Settings { WelcomeShown = true }, new Version(3, 0, 0), now);

        Assert.Equal(now, launched.UpdatedAtUtc);
        Assert.True(launched.MajorUpdateNoticeDue);
        Assert.Equal("3.0.0", launched.LastVersionSeen);
    }

    [Fact]
    public void AnyNewerVersionIsAnUpdateHoweverItWasInstalled()
    {
        var now = DateTimeOffset.UtcNow;
        var before = new Settings { WelcomeShown = true, LastVersionSeen = "3.0.0", LastUpdateCheckUtc = now.AddDays(-1) };

        var minor = AppUpdater.RecordLaunch(before, new Version(3, 1, 0), now);
        var major = AppUpdater.RecordLaunch(before, new Version(4, 0, 0), now);

        Assert.Equal(now, minor.UpdatedAtUtc);
        Assert.False(minor.MajorUpdateNoticeDue);
        Assert.True(major.MajorUpdateNoticeDue);
        Assert.Equal(now.AddDays(-1), minor.LastUpdateCheckUtc);
    }

    [Fact]
    public void LaterLaunchesOfTheSameVersionKeepItsUpdateAndADowngradeIsNone()
    {
        var landed = DateTimeOffset.UtcNow.AddDays(-2);
        var updated = new Settings
        {
            WelcomeShown = true,
            LastVersionSeen = "3.1.0",
            UpdatedAtUtc = landed,
            MajorUpdateNoticeDue = true,
            LastUpdateCheckUtc = landed
        };

        Assert.Equal(updated, AppUpdater.RecordLaunch(updated, new Version(3, 1, 0), DateTimeOffset.UtcNow));

        var downgraded = AppUpdater.RecordLaunch(updated, new Version(3, 0, 0), DateTimeOffset.UtcNow);
        Assert.Null(downgraded.UpdatedAtUtc);
        Assert.False(downgraded.MajorUpdateNoticeDue);
        Assert.Equal("3.0.0", downgraded.LastVersionSeen);
    }

    [Fact]
    public void AutomaticInstallsAreOnWhenSettingsPredateTheSwitch()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """{ "Theme": "Light", "WelcomeShown": true }""");

        var loaded = Settings.Load(path);

        Assert.True(loaded.InstallUpdatesAutomatically);
        Assert.Equal(ThemePreference.Light, loaded.Theme);
    }

    [Fact]
    public void SettingsFromTheSnoozeDaysStillLoad()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("settings.json", """
            {
              "Theme": "Light",
              "WelcomeShown": true,
              "UpdateNotificationVersion": "2.1.0",
              "UpdateNotificationSnoozedUntilUtc": "2026-10-01T00:00:00+00:00"
            }
            """);

        var loaded = Settings.Load(path);

        Assert.Equal(ThemePreference.Light, loaded.Theme);
        Assert.True(loaded.WelcomeShown);
        Assert.False(File.Exists(path + ".bad"));
    }

    [Fact]
    public Task TheWholePathRunsFromCheckToLaunchWithoutGitHub() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.FromSeconds(5) };
        var launches = new List<(string Version, string Installer)>();
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => pc,
            isMetered: () => false,
            launch: (update, installer, _) =>
            {
                launches.Add((update.VersionText, installer));
                return Task.CompletedTask;
            });

        // The check verifies the signature, then downloads in the background.
        await updates.CheckNowAsync();
        var installer = Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe");
        Assert.Equal("3.1.0", updates.State.Pending?.VersionText);
        Assert.Equal(release.Installer, await File.ReadAllBytesAsync(installer));
        Assert.Equal(2, release.RequestsFor(".sha256.sig"));
        Assert.False(updates.State.Downloading);
        Assert.NotNull(Settings.Current.LastUpdateCheckUtc);

        // The user is at the keyboard: it waits.
        await updates.TickAsync();
        Assert.Empty(launches);

        // Away: GitHub still offers it, signed, and then it installs.
        pc = Away;
        await updates.TickAsync();
        Assert.Equal(("3.1.0", installer), Assert.Single(launches));
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
        Assert.Equal(3, release.RequestsFor(".sha256.sig"));
        Assert.Equal("3.1.0", Settings.Current.UpdateAttemptVersion);
        Assert.True(updates.State.Installing);

        await updates.TickAsync();
        Assert.Single(launches);
    });

    [Fact]
    public Task ANewReleaseWaitsTwoDaysUnlessTheUserAsks() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture(publishedAt: DateTimeOffset.UtcNow.AddHours(-12));
        using var root = new TempDirectory();
        var mayQuit = false;
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            canQuit: () => mayQuit,
            isMetered: () => false,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        await updates.CheckNowAsync();
        await updates.TickAsync();
        Assert.Equal(0, launches);

        // Unsaved signing work holds Install now; the press still counts.
        await updates.InstallNowAsync();
        Assert.Equal(0, launches);
        Assert.False(updates.State.Installing);

        await updates.TickAsync();
        Assert.Equal(1, launches);
    });

    [Fact]
    public Task AReleaseWithoutItsSignatureIsSkippedForGood() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture(signed: false);
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => false, launch: NeverLaunch);

        await updates.CheckNowAsync();
        await updates.CheckNowAsync();

        Assert.Null(updates.State.Pending);
        Assert.False(updates.State.UpToDate);
        Assert.Equal("3.1.0", Settings.Current.SkippedUpdateVersion);
        Assert.Equal(0, release.RequestsFor(".sha256"));
        Assert.Equal(0, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task AReleaseSignedWithAnotherKeyIsSkippedBeforeItDownloads() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = new AppUpdateService(release.Client(), new Version(3, 0, 0), root.Path, ownsHttpClient: true);
        using var updates = new AppUpdater(service, () => Away, isMetered: () => false, launch: NeverLaunch);

        await updates.CheckNowAsync();
        await updates.CheckNowAsync();

        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", Settings.Current.SkippedUpdateVersion);
        Assert.Equal(1, release.RequestsFor(".sha256.sig"));
        Assert.Equal(0, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task AnInstallerThatFailsItsChecksumIsSkipped() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        release.Installer = (byte[])release.Installer.Clone();
        release.Installer[1] = 0xFF;
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            isMetered: () => false,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        await updates.CheckNowAsync();
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", Settings.Current.SkippedUpdateVersion);
        Assert.Equal(0, launches);
        Assert.Empty(Directory.EnumerateFiles(root.Path));
    });

    [Fact]
    public Task NothingDownloadsOnAMeteredConnection() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var metered = true;
        using var updates = new AppUpdater(release.Service(root.Path), () => Away with { PreviewOpen = true }, isMetered: () => metered, launch: NeverLaunch);

        await updates.CheckNowAsync();
        await updates.TickAsync();
        Assert.Equal(0, release.RequestsFor(".exe"));
        Assert.NotNull(updates.State.Pending);

        metered = false;
        await updates.TickAsync();
        Assert.Equal(1, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task OnAMeteredConnectionAnInstallerAlreadyOnDiskIsUsed() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var installer = Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe");
        await File.WriteAllBytesAsync(installer, release.Installer);
        var launches = new List<string>();
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            isMetered: () => true,
            launch: (_, path, _) =>
            {
                launches.Add(path);
                return Task.CompletedTask;
            });

        // An earlier run downloaded it: checking it costs the sidecar and .sig.
        await updates.CheckNowAsync();
        Assert.Equal(installer, updates.State.Installer);
        Assert.Equal(0, release.RequestsFor(".exe"));
        Assert.Equal(2, release.RequestsFor(".sha256.sig"));

        await updates.TickAsync();
        Assert.Equal(installer, Assert.Single(launches));
        Assert.Equal(0, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task OnAMeteredConnectionAnInstallerThatFailsItsChecksumIsDeletedNotReplaced() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var installer = Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe");
        await File.WriteAllBytesAsync(installer, new byte[ReleaseFixture.InstallerSize]);
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true, launch: NeverLaunch);

        await updates.CheckNowAsync();
        Assert.Null(updates.State.Installer);
        Assert.False(File.Exists(installer));
        Assert.NotNull(updates.State.Pending);

        // With nothing on disk, a minute's look asks GitHub for nothing.
        var signatures = release.RequestsFor(".sha256.sig");
        await updates.TickAsync();
        Assert.Equal(signatures, release.RequestsFor(".sha256.sig"));
        Assert.Equal(0, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task WithTheSwitchOffNothingDownloadsOrInstallsUntilInstallNow() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { InstallUpdatesAutomatically = false });
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            isMetered: () => true,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        await updates.CheckNowAsync();
        await updates.TickAsync();
        Assert.NotNull(updates.State.Pending);
        Assert.Equal(0, release.RequestsFor(".exe"));
        Assert.Equal(0, launches);

        // Asked for, it downloads even on a metered connection, and installs at once.
        await updates.InstallNowAsync();
        Assert.Equal(1, release.RequestsFor(".exe"));
        Assert.Equal(1, launches);
    });

    [Fact]
    public Task TurningTheSwitchOffStopsABackgroundDownload() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture { HoldInstaller = new() };
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => false, launch: NeverLaunch);

        var check = updates.CheckNowAsync();
        await WaitUntilAsync(() => updates.State.Downloading);
        updates.SetInstallAutomatically(false);
        await check;

        Assert.False(updates.State.Downloading);
        Assert.False(Settings.Current.InstallUpdatesAutomatically);
        Assert.Empty(Directory.EnumerateFiles(root.Path));
    });

    [Fact]
    public Task InstallNowAfterTheSwitchStoppedADownloadStartsItsOwn() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture { HoldInstaller = new() };
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away with { PreviewOpen = true },
            isMetered: () => false,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        // Off while the background download is held, then Install now at once.
        var check = updates.CheckNowAsync();
        await WaitUntilAsync(() => updates.State.Downloading);
        updates.SetInstallAutomatically(false);
        var install = updates.InstallNowAsync();
        await check;
        await WaitUntilAsync(() => release.RequestsFor(".exe") == 2);
        Assert.True(updates.State is { Installing: true, Downloading: true });

        release.HoldInstaller.SetResult();
        await install;
        Assert.Equal(1, launches);
        Assert.False(updates.State.InstallFailed);
    });

    [Fact]
    public Task AnInstallNowThatFailsSaysSoAndCanBePressedAgain() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { InstallUpdatesAutomatically = false });
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            isMetered: () => false,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        await updates.CheckNowAsync();
        release.Offline = true;
        await updates.InstallNowAsync();
        Assert.True(updates.State is { InstallFailed: true, Installing: false, Downloading: false });
        Assert.NotNull(updates.State.Pending);

        release.Offline = false;
        await updates.InstallNowAsync();
        Assert.Equal(1, launches);
        Assert.False(updates.State.InstallFailed);
    });

    [Fact]
    public Task APulledReleaseIsDropped() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true, launch: NeverLaunch);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Pending);

        release.NoRelease = true;
        await updates.CheckNowAsync();
        Assert.Null(updates.State.Pending);
        Assert.True(updates.State.UpToDate);
    });

    [Fact]
    public Task AReleasePulledBetweenDownloadAndLaunchNeverInstalls() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.Zero };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false, launch: NeverLaunch);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        // Pulled late in its two days, while the user was at the keyboard.
        release.NoRelease = true;
        pc = Away;
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.True(updates.State.UpToDate);
        Assert.False(updates.State.Installing);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
    });

    [Fact]
    public Task AReleaseThatLostItsSignatureBeforeLaunchIsSkipped() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.Zero };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false, launch: NeverLaunch);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        release.Json = ReleaseFixture.BuildJson("3.1.0", ReleaseFixture.InstallerSize, release.Sha256, signed: false, release.PublishedAt);
        pc = Away;
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", Settings.Current.SkippedUpdateVersion);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
    });

    [Fact]
    public Task WithoutAnAnswerFromGitHubNothingInstallsAndItAsksAgainInHalfAnHour() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.Zero };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false, launch: NeverLaunch);

        await updates.CheckNowAsync();
        release.Offline = true;
        pc = Away;
        await updates.TickAsync();
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
        Assert.NotNull(updates.State.Pending);
        Assert.False(updates.State.Installing);

        // Only the Check for updates link reports a failed check.
        Assert.False(updates.State.CheckFailed);

        // The next minute does not ask again.
        await updates.TickAsync();
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
    });

    [Fact]
    public Task ASetupThatFailedAndRestartedGlimpseWaitsADay() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with
        {
            UpdateAttemptVersion = "3.1.0",
            UpdateAttemptUtc = DateTimeOffset.UtcNow.AddHours(-1)
        });
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path),
            () => Away,
            isMetered: () => false,
            launch: (_, _, _) =>
            {
                launches++;
                return Task.CompletedTask;
            });

        await updates.CheckNowAsync();
        await updates.TickAsync();
        Assert.Equal(0, launches);

        Settings.Update(settings => settings with { UpdateAttemptUtc = DateTimeOffset.UtcNow.AddHours(-25) });
        await updates.TickAsync();
        Assert.Equal(1, launches);
    });

    [Fact]
    public Task AFailedCheckTriesAgainInHalfAnHour() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture { Offline = true };
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), launch: NeverLaunch);
        updates.Start();

        await updates.CheckNowAsync();
        var retry = Fields.Get<DispatcherTimer>(updates, "_retryTimer");
        Assert.True(retry.IsEnabled);
        Assert.Equal(TimeSpan.FromMinutes(30), retry.Interval);
        Assert.Null(Settings.Current.LastUpdateCheckUtc);
        Assert.True(updates.State.CheckFailed);

        release.Offline = false;
        release.NoRelease = true;
        await updates.CheckNowAsync();
        Assert.False(retry.IsEnabled);
        Assert.True(updates.State.UpToDate);
        Assert.False(updates.State.CheckFailed);
        Assert.Equal(TimeSpan.FromHours(6), Fields.Get<DispatcherTimer>(updates, "_checkTimer").Interval);
    });

    [Fact]
    public void AMajorUpdateGetsOneQuietTrayNoticeThatOpensItsReleasePage()
    {
        Assert.Equal(("Glimpse is now 3.0", "Select to see what's new."), TrayIcon.MajorUpdateNotice(new Version(3, 0, 0)));
        Assert.Equal("Glimpse is now 4.1", TrayIcon.MajorUpdateNotice(new Version(4, 1, 2)).Title);
        Assert.Equal(
            "https://github.com/tekkusai/Glimpse/releases/tag/v3.0.0",
            AppUpdateService.ReleasePageUri(new Version(3, 0, 0)).AbsoluteUri);

        var tray = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");
        var notice = TestSource.Method(@"SpacePreview\Services\TrayIcon.cs", "ShowMajorUpdateNotice");
        Assert.Contains("NativeMethods.NIIF_RESPECT_QUIET_TIME", notice, StringComparison.Ordinal);
        Assert.Contains("MajorUpdateNoticeDue = false", notice, StringComparison.Ordinal);
        // TrayWindowTests drives the click itself; this is the tray's wiring to it.
        Assert.Contains("balloonClicked: OnBalloonClicked", tray, StringComparison.Ordinal);
        Assert.Contains(
            "NativeMethods.TimeSinceLastInput() >= running",
            TestSource.Method(@"SpacePreview\Services\TrayIcon.cs", "ShowMajorUpdateNoticeOnReturn"),
            StringComparison.Ordinal);
    }

    private static AppUpdateRelease Release(string version) => new(
        new Version(version),
        $"v{version}",
        new Uri($"https://github.com/tekkusai/Glimpse/releases/download/v{version}/Glimpse-Setup-{version}.exe"),
        ReleaseFixture.InstallerSize,
        new string('A', 64),
        new Uri($"https://github.com/tekkusai/Glimpse/releases/download/v{version}/Glimpse-Setup-{version}.exe.sha256"));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
