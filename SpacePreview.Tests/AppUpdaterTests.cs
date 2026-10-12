using System.Text.Json.Nodes;
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
    public void TheLockScreenOrAScreenSaverIsNotAFullScreenApp()
    {
        // Both cover the monitor without a caption, so Glimpse holds its
        // input for them as for any full-screen window. Only the shell's
        // word that nobody is there lets that hold pass.
        var locked = Away with { Notifications = NativeMethods.UserNotificationState.NotPresent, FullscreenSuspended = true };

        Assert.True(AppUpdater.IsAway(locked));
        Assert.False(AppUpdater.IsAway(locked with { Notifications = NativeMethods.UserNotificationState.AcceptsNotifications }));
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
        var launches = new List<string>();
        using var updates = new AppUpdater(
            release.Service(root.Path, start: info => launches.Add(info.FileName)),
            () => pc,
            isMetered: () => false);

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

        // Away an hour later: GitHub still offers it, signed, and then it installs.
        AgeTheLastCheck();
        pc = Away;
        await updates.TickAsync();
        Assert.Equal(installer, Assert.Single(launches));
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
        Assert.Equal(3, release.RequestsFor(".sha256.sig"));
        Assert.Equal("3.1.0", Settings.Current.UpdateAttemptVersion);
        Assert.True(updates.State.Installing);

        await updates.TickAsync();
        Assert.Single(launches);
    });

    [Fact]
    public Task ACheckThatOpensTheGateIsTheAnswerItsLaunchNeeds() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { PreviewOpen = true };
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => pc,
            isMetered: () => false);

        // Downloaded while the user was here.
        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        // Hours later, away: the check finds the gate open and launches without asking again.
        AgeTheLastCheck();
        pc = Away;
        await updates.CheckNowAsync();
        Assert.Equal(1, launches);
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
    });

    [Fact]
    public Task AClockSetBackIsNoAnswerFromGitHub() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { PreviewOpen = true };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        // The last check now reads as an hour from now, and the release is gone.
        Settings.Update(settings => settings with { LastUpdateCheckUtc = DateTimeOffset.UtcNow.AddHours(1) });
        release.NoRelease = true;
        pc = Away;
        await updates.TickAsync();

        Assert.Equal(2, release.RequestsFor("/releases/latest"));
        Assert.Null(updates.State.Pending);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
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
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            canQuit: () => mayQuit,
            isMetered: () => false);

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
    public Task AReleaseWithoutItsSignatureIsOfferedOnceItHasOne() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture(signed: false);
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true);

        await updates.CheckNowAsync();
        await updates.CheckNowAsync();

        Assert.Null(updates.State.Pending);
        Assert.False(updates.State.UpToDate);
        Assert.Equal("3.1.0", updates.State.Rejected);
        Assert.Equal(0, release.RequestsFor(".sha256"));
        Assert.Equal(0, release.RequestsFor(".exe"));

        // Published before its .sig was attached, then put right: nothing
        // was kept against the version, so the next check offers it.
        release.Json = ReleaseFixture.BuildJson("3.1.0", ReleaseFixture.InstallerSize, release.Sha256, signed: true, release.PublishedAt);
        await updates.CheckNowAsync();

        Assert.Equal("3.1.0", updates.State.Pending?.VersionText);
        Assert.Null(updates.State.Rejected);
    });

    [Fact]
    public Task ASignatureStillUploadingIsAskedForAgain() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var uploaded = release.Json;
        var uploading = JsonNode.Parse(uploaded)!;
        uploading["assets"]!.AsArray().Single(asset => ((string)asset!["name"]!).EndsWith(".sig", StringComparison.Ordinal))!["state"] = "open";
        release.Json = uploading.ToJsonString();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true);

        await updates.CheckNowAsync();
        Assert.Null(updates.State.Pending);
        Assert.False(updates.State.UpToDate);
        Assert.Null(updates.State.Rejected);
        Assert.Equal(0, release.RequestsFor(".sha256"));

        // Uploaded by the next check, it is offered.
        release.Json = uploaded;
        await updates.CheckNowAsync();
        Assert.Equal("3.1.0", updates.State.Pending?.VersionText);
    });

    [Fact]
    public Task AReleaseSignedWithAnotherKeyIsNeverDownloaded() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var service = new AppUpdateService(release.Client(), new Version(3, 0, 0), root.Path, ownsHttpClient: true, start: ReleaseFixture.NeverStart);
        using var updates = new AppUpdater(service, () => Away, isMetered: () => false);

        await updates.CheckNowAsync();
        await updates.CheckNowAsync();

        // Each check reads the sidecar and the .sig again, and nothing more.
        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", updates.State.Rejected);
        Assert.Equal(2, release.RequestsFor(".sha256.sig"));
        Assert.Equal(0, release.RequestsFor(".exe"));
    });

    [Fact]
    public Task AnInstallerThatFailsItsChecksumIsNotInstalled() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        release.Installer = (byte[])release.Installer.Clone();
        release.Installer[1] = 0xFF;
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            isMetered: () => false);

        await updates.CheckNowAsync();
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", updates.State.Rejected);
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
        using var updates = new AppUpdater(release.Service(root.Path), () => Away with { PreviewOpen = true }, isMetered: () => metered);

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
            release.Service(root.Path, start: info => launches.Add(info.FileName)),
            () => Away,
            isMetered: () => true);

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
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true);

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
    public Task AnInstallerAlreadyOnDiskIsNeverCalledADownload() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        await File.WriteAllBytesAsync(Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe"), release.Installer);

        // Metered, and then unmetered: either way the look fetches the
        // sidecar and .sig and hashes the file, and no installer bytes.
        foreach (var metered in new[] { true, false })
        {
            using var updates = new AppUpdater(
                release.Service(root.Path),
                () => Away with { PreviewOpen = true },
                isMetered: () => metered);
            var downloading = false;
            updates.Changed += () => downloading |= updates.State.Downloading;

            await updates.CheckNowAsync();
            Assert.NotNull(updates.State.Installer);
            Assert.False(downloading, metered ? "metered" : "unmetered");
        }

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
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            isMetered: () => true);

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
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => false);

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
            release.Service(root.Path, start: _ => launches++),
            () => Away with { PreviewOpen = true },
            isMetered: () => false);

        // Off while the background download is held, then Install now at once.
        var check = updates.CheckNowAsync();
        await WaitUntilAsync(() => updates.State.Downloading);
        var footers = new List<UpdateFooter>();
        updates.Changed += () => footers.Add(UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now));
        updates.SetInstallAutomatically(false);
        var install = updates.InstallNowAsync();
        await check;
        await WaitUntilAsync(() => release.RequestsFor(".exe") == 2);
        Assert.True(updates.State is { Installing: true, Downloading: true });

        release.HoldInstaller.SetResult();
        await install;
        Assert.Equal(1, launches);
        Assert.False(updates.State.InstallFailed);

        // The stopped download hands over to Install now's own without a
        // word of Installing before setup starts.
        Assert.Equal(
            [
                new(null, FooterLink.InstallNow, "3.1.0 is ready to install."),
                new(null, FooterLink.None, "Downloading 3.1.0."),
                new(null, FooterLink.None, "3.1.0 is ready to install."),
                new UpdateFooter(null, FooterLink.None, "Installing 3.1.0. Back in a few seconds.")
            ],
            footers.Where((footer, i) => i == 0 || footer != footers[i - 1]));
    });

    [Fact]
    public Task AnInstallNowThatFailsSaysSoAndCanBePressedAgain() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { InstallUpdatesAutomatically = false });
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            isMetered: () => false);

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
    public Task CouldNotInstallEndsWhenTheInstallerVerifiesAgain() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => throw new InvalidOperationException("Windows did not start the installer.")),
            () => Away with { PreviewOpen = true },
            isMetered: () => false);
        string? Caption() => UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now).Caption;

        await updates.CheckNowAsync();
        await updates.InstallNowAsync();
        Assert.Equal("Couldn't install 3.1.0.", Caption());

        // The next minute verifies the installer again. Setup ran within the
        // day, so it waits for the user, and says only that.
        await updates.TickAsync();
        Assert.NotNull(updates.State.Installer);
        Assert.Equal("3.1.0 is ready to install.", Caption());
    });

    [Fact]
    public Task CouldNotInstallEndsWhenTheUpdateWillInstallOnItsOwn() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture(publishedAt: DateTimeOffset.UtcNow.AddHours(-1));
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away with { PreviewOpen = true }, isMetered: () => false);
        string? Caption() => UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now).Caption;

        // Install now on a fresh release, and GitHub gives no answer before the launch.
        await updates.CheckNowAsync();
        AgeTheLastCheck();
        release.Offline = true;
        await updates.InstallNowAsync();
        Assert.Equal("Couldn't install 3.1.0.", Caption());

        // GitHub answers again; the press waived the two days.
        release.Offline = false;
        await updates.CheckNowAsync();
        Assert.Equal("3.1.0 installs the next time you step away.", Caption());
    });

    [Fact]
    public Task InstallNowAsksGitHubOnceMoreBeforeItLaunches() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away with { PreviewOpen = true },
            isMetered: () => false);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        // Hours later: one call to GitHub for the press, then setup.
        AgeTheLastCheck();
        await updates.InstallNowAsync();
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
        Assert.Equal(1, launches);
    });

    [Fact]
    public Task InstallNowDuringACheckTakesThatChecksAnswer() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away with { PreviewOpen = true },
            isMetered: () => false);

        await updates.CheckNowAsync();
        AgeTheLastCheck();

        // A scheduled check is waiting on GitHub when the user presses Install now.
        release.HoldCheck = new();
        var check = updates.CheckNowAsync();
        Assert.True(updates.State.Checking);
        var install = updates.InstallNowAsync();
        release.HoldCheck.SetResult();
        await Task.WhenAll(check, install);

        Assert.Equal(1, launches);
        Assert.Equal(2, release.RequestsFor("/releases/latest"));
    });

    [Fact]
    public Task InstallNowInstallsNothingPulledOrUnsignedSinceTheLastCheck() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away with { PreviewOpen = true }, isMetered: () => false);
        UpdateFooter Footer() => UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now);

        // Pulled: nothing installs, and the footer says Glimpse is current.
        await updates.CheckNowAsync();
        AgeTheLastCheck();
        release.NoRelease = true;
        await updates.InstallNowAsync();
        Assert.Equal(new UpdateFooter("Up to date", FooterLink.CheckForUpdates, null), Footer());

        // Back, then unsigned: it is not offered, and says so.
        release.NoRelease = false;
        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Pending);
        AgeTheLastCheck();
        release.Json = ReleaseFixture.BuildJson("3.1.0", ReleaseFixture.InstallerSize, release.Sha256, signed: false, release.PublishedAt);
        await updates.InstallNowAsync();
        Assert.Equal(new UpdateFooter(null, FooterLink.CheckForUpdates, "3.1.0 failed verification."), Footer());

        Assert.Equal(4, release.RequestsFor("/releases/latest"));
        Assert.Null(Settings.Current.UpdateAttemptVersion);
        Assert.False(updates.State.Installing);
    });

    [Fact]
    public Task InstallNowWithoutAnAnswerFromGitHubSaysItCouldNotInstall() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away with { PreviewOpen = true }, isMetered: () => false);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);
        AgeTheLastCheck();
        release.Offline = true;
        await updates.InstallNowAsync();

        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "Couldn't install 3.1.0."),
            UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now));
        Assert.Null(Settings.Current.UpdateAttemptVersion);
    });

    [Fact]
    public Task APulledReleaseIsDropped() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path), () => Away, isMetered: () => true);

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
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        // Pulled late in its two days, while the user was at the keyboard.
        AgeTheLastCheck();
        release.NoRelease = true;
        pc = Away;
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.True(updates.State.UpToDate);
        Assert.False(updates.State.Installing);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
    });

    [Fact]
    public Task AReleaseThatLostItsSignatureBeforeLaunchIsNotInstalled() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.Zero };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false);

        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        AgeTheLastCheck();
        release.Json = ReleaseFixture.BuildJson("3.1.0", ReleaseFixture.InstallerSize, release.Sha256, signed: false, release.PublishedAt);
        pc = Away;
        await updates.TickAsync();

        Assert.Null(updates.State.Pending);
        Assert.Equal("3.1.0", updates.State.Rejected);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
    });

    [Fact]
    public Task WithoutAnAnswerFromGitHubNothingInstallsAndItAsksAgainInHalfAnHour() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { Idle = TimeSpan.Zero };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false);

        await updates.CheckNowAsync();
        AgeTheLastCheck();
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
    public Task WhileGitHubGivesNoAnswerTheUpdateIsOnlyReady() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var pc = Away with { PreviewOpen = true };
        using var updates = new AppUpdater(release.Service(root.Path), () => pc, isMetered: () => false);
        string? Caption() => UpdateFooter.Describe(updates.State, Settings.Current, updates.CurrentVersion, DateTimeOffset.Now).Caption;

        await updates.CheckNowAsync();
        Assert.Equal("3.1.0 installs the next time you step away.", Caption());

        // Away, the check before the launch gets no answer: every launch needs one.
        AgeTheLastCheck();
        release.Offline = true;
        pc = Away;
        await updates.TickAsync();
        Assert.Equal("3.1.0 is ready to install.", Caption());

        release.Offline = false;
        pc = Away with { PreviewOpen = true };
        await updates.CheckNowAsync();
        Assert.Equal("3.1.0 installs the next time you step away.", Caption());

        // Check for updates with no answer says the same.
        release.Offline = true;
        await updates.CheckNowAsync();
        Assert.Equal("3.1.0 is ready to install.", Caption());
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
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            isMetered: () => false);

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
        using var updates = new AppUpdater(release.Service(root.Path));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task TheInstallerThatStartsIsTheOneThatWasVerified(bool installNow) => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { InstallUpdatesAutomatically = !installNow });
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();

        // Names itself the same, and was never signed.
        var impostor = (byte[])release.Installer.Clone();
        impostor[^1] = 0x01;
        var replacement = Path.Combine(root.Path, "impostor.exe");
        await File.WriteAllBytesAsync(replacement, impostor);

        var swaps = new List<string>();
        byte[]? started = null;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: info =>
            {
                // The last moment a swap could matter: every way to put
                // other bytes where setup starts from.
                if (Swapped(() => File.WriteAllBytes(info.FileName, impostor)))
                {
                    swaps.Add("written over");
                }

                if (Swapped(() => File.Move(replacement, info.FileName, overwrite: true)))
                {
                    swaps.Add("replaced");
                }

                if (Swapped(() => File.Delete(info.FileName)))
                {
                    swaps.Add("deleted");
                }

                started = File.ReadAllBytes(info.FileName);
            }),
            () => Away,
            isMetered: () => false);

        await updates.CheckNowAsync();
        await (installNow ? updates.InstallNowAsync() : updates.TickAsync());

        Assert.Empty(swaps);
        Assert.Equal(release.Installer, started);
    });

    [Fact]
    public Task SetupWaitsWhenTheUserComesBackWhileTheInstallerIsCheckedOneLastTime() => WpfApp.RunAsync(async () =>
    {
        foreach (var back in new[]
        {
            Away with { Idle = TimeSpan.Zero },
            Away with { PreviewOpen = true },
            Away with { FullscreenSuspended = true }
        })
        {
            using var scope = new UpdateSettingsScope();
            using var release = new ReleaseFixture();
            using var root = new TempDirectory();
            var installer = Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe");
            var returns = true;
            var launches = new List<(string? Attempt, bool SetupStarted)>();
            AppUpdater? updates = null;
            using var updater = new AppUpdater(
                release.Service(root.Path, start: _ => launches.Add((Settings.Current.UpdateAttemptVersion, updates!.State.SetupStarted))),
                // Away for every look before the launch; back while Glimpse
                // holds the installer for its last hash.
                () => returns && HeldOpen(installer) ? back : Away,
                isMetered: () => false);
            updates = updater;

            await updates.CheckNowAsync();
            await updates.TickAsync();

            Assert.Empty(launches);
            Assert.Null(Settings.Current.UpdateAttemptVersion);
            Assert.Null(updates.State.Rejected);
            Assert.True(updates.State is { Installing: false, SetupStarted: false, InstallFailed: false, Installer: not null, Pending: not null });

            // Not a failed attempt: the next time the user is away, it installs,
            // and the attempt is on disk before setup starts.
            returns = false;
            await updates.TickAsync();
            Assert.Equal(("3.1.0", true), Assert.Single(launches));
        }
    });

    [Fact]
    public Task SetupWaitsWhenTheSwitchGoesOffWhileTheInstallerIsCheckedOneLastTime() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away,
            isMetered: () => false);
        await updates.CheckNowAsync();

        // Off the moment the install is under way.
        updates.Changed += () =>
        {
            if (updates.State.Installing && Settings.Current.InstallUpdatesAutomatically)
            {
                updates.SetInstallAutomatically(false);
            }
        };
        await updates.TickAsync();

        Assert.Equal(0, launches);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
        Assert.Null(updates.State.Rejected);
        Assert.True(updates.State is { Installing: false, SetupStarted: false, Installer: not null, Pending: not null });
    });

    [Fact]
    public Task InstallNowWaitsWhenSigningStartsWhileTheInstallerIsCheckedOneLastTime() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var installer = Path.Combine(root.Path, "Glimpse-Setup-3.1.0.exe");
        var signs = true;
        var launches = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launches++),
            () => Away with { PreviewOpen = true },
            // Signing starts while Glimpse holds the installer for its last hash.
            canQuit: () => !(signs && HeldOpen(installer)),
            isMetered: () => false);
        await updates.CheckNowAsync();
        Assert.NotNull(updates.State.Installer);

        await updates.InstallNowAsync();

        Assert.Equal(0, launches);
        Assert.Null(Settings.Current.UpdateAttemptVersion);
        Assert.True(updates.State is { Installing: false, SetupStarted: false, InstallFailed: false, Installer: not null, Pending: not null });

        // The work saved, the next press installs.
        signs = false;
        await updates.InstallNowAsync();
        Assert.Equal(1, launches);
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
        // Every tray notice goes through one quiet balloon.
        Assert.Contains("ShowBalloon(title, text)", notice, StringComparison.Ordinal);
        var balloon = TestSource.Method(@"SpacePreview\Services\TrayIcon.cs", "ShowBalloon");
        Assert.Contains("NativeMethods.NIIF_RESPECT_QUIET_TIME", balloon, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIIF_NOSOUND", balloon, StringComparison.Ordinal);
        Assert.Contains("MajorUpdateNoticeDue = false", notice, StringComparison.Ordinal);
        // TrayWindowTests drives the click itself; this is the tray's wiring to it.
        Assert.Contains("balloonClicked: OnBalloonClicked", tray, StringComparison.Ordinal);
        Assert.Contains(
            "NativeMethods.TimeSinceLastInput() >= running",
            TestSource.Method(@"SpacePreview\Services\TrayIcon.cs", "ShowMajorUpdateNoticeOnReturn"),
            StringComparison.Ordinal);
    }

    // A minute on, a launch asks GitHub again.
    private static void AgeTheLastCheck() =>
        Settings.Update(settings => settings with { LastUpdateCheckUtc = settings.LastUpdateCheckUtc - TimeSpan.FromMinutes(1) });

    private static AppUpdateRelease Release(string version) => new(
        new Version(version),
        $"v{version}",
        new Uri($"https://github.com/tekkusai/Glimpse/releases/download/v{version}/Glimpse-Setup-{version}.exe"),
        ReleaseFixture.InstallerSize,
        new string('A', 64),
        new Uri($"https://github.com/tekkusai/Glimpse/releases/download/v{version}/Glimpse-Setup-{version}.exe.sha256"));

    private static bool Swapped(Action swap)
    {
        try
        {
            swap();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // Glimpse's hold shares only reads: a writer is turned away while it lasts.
    private static bool HeldOpen(string path)
    {
        try
        {
            using var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
