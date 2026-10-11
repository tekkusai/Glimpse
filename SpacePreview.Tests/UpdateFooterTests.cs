using System.Globalization;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The Settings footer's words for every state of an update: the short
/// status after the version, its one link, and the caption under it only
/// when something needs explaining.
/// </summary>
public sealed class UpdateFooterTests
{
    private static readonly Version Current = new(3, 0, 0);
    // Saturday afternoon on Eastern time, October 10.
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(-4));

    private static readonly AppUpdateRelease Release = new(
        new Version(3, 1, 0),
        "v3.1.0",
        new Uri("https://github.com/tekkusai/Glimpse/releases/download/v3.1.0/Glimpse-Setup-3.1.0.exe"),
        ReleaseFixture.InstallerSize,
        new string('A', 64),
        new Uri("https://github.com/tekkusai/Glimpse/releases/download/v3.1.0/Glimpse-Setup-3.1.0.exe.sha256"))
    {
        PublishedAt = Now.AddDays(-3)
    };

    private const string Installer = @"C:\Updates\Glimpse-Setup-3.1.0.exe";

    private static readonly Settings Checked = new() { LastUpdateCheckUtc = Now.AddHours(-1) };

    [Fact]
    public void BeforeAnyCheckItOnlyOffersOne() =>
        Assert.Equal(new UpdateFooter(null, FooterLink.CheckForUpdates, null), Describe(new UpdateState(), Checked));

    [Fact]
    public void ACheckThatFoundNothingNewerSaysUpToDate() =>
        Assert.Equal(new UpdateFooter("Up to date", FooterLink.CheckForUpdates, null), Describe(new UpdateState { UpToDate = true }, Checked));

    [Fact]
    public void ARunningCheckSaysSo() =>
        Assert.Equal(new UpdateFooter("Checking for updates", FooterLink.None, null), Describe(new UpdateState { Checking = true, UpToDate = true }, Checked));

    [Fact]
    public void ADownloadSaysItRunsInTheBackground() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "Downloading 3.1.0 in the background."),
            Describe(new UpdateState { Pending = Release, Downloading = true }, Checked));

    [Fact]
    public void AnUpdateThatWillInstallOnItsOwnSaysWhen() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "3.1.0 installs the next time you step away."),
            Describe(new UpdateState { Pending = Release, Installer = Installer }, Checked));

    [Fact]
    public void UntilItWouldInstallOnItsOwnItIsOnlyReady()
    {
        const string Ready = "3.1.0 is ready to install.";

        // Not on disk yet: a metered connection, or a download that failed.
        Assert.Equal(new UpdateFooter(null, FooterLink.InstallNow, Ready), Describe(new UpdateState { Pending = Release }, Checked));

        // Its first two days, unless Install now was pressed.
        var fresh = new UpdateState { Pending = Release with { PublishedAt = Now.AddHours(-47) }, Installer = Installer };
        Assert.Equal(Ready, Describe(fresh, Checked).Caption);
        Assert.Equal("3.1.0 installs the next time you step away.", Describe(fresh with { InstallNowPressed = true }, Checked).Caption);

        // A setup of it ran in the last day.
        Assert.Equal(Ready, Describe(new UpdateState { Pending = Release, Installer = Installer }, Checked with
        {
            UpdateAttemptVersion = "3.1.0",
            UpdateAttemptUtc = Now.AddHours(-2)
        }).Caption);
    }

    [Fact]
    public void WithoutAnAnswerFromGitHubItIsOnlyReady() =>
        // Every launch asks GitHub first, so the promise waits for an answer.
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "3.1.0 is ready to install."),
            Describe(new UpdateState { Pending = Release, Installer = Installer, Unanswered = true }, Checked));

    [Fact]
    public void WithAutomaticInstallsOffItWaitsForTheUser() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "3.1.0 is ready to install."),
            Describe(new UpdateState { Pending = Release, Installer = Installer }, Checked with { InstallUpdatesAutomatically = false }));

    [Fact]
    public void InstallNowSaysWhileItDownloadsAndThatGlimpseWillBeBack()
    {
        Assert.Equal(
            new UpdateFooter(null, FooterLink.None, "Downloading 3.1.0."),
            Describe(new UpdateState { Pending = Release, Installing = true, Downloading = true }, Checked));
        Assert.Equal(
            new UpdateFooter(null, FooterLink.None, "Installing 3.1.0. Back in a few seconds."),
            Describe(new UpdateState { Pending = Release, Installing = true, Installer = Installer, SetupStarted = true }, Checked));
    }

    [Fact]
    public void InstallNowSaysInstallingOnlyOnceSetupStarts()
    {
        var installNow = new UpdateState { Pending = Release, Installing = true, Installer = Installer };

        // Asking GitHub once more before the launch.
        Assert.Equal(new UpdateFooter("Checking for updates", FooterLink.None, null), Describe(installNow with { Checking = true }, Checked));

        // Between steps: the link is gone, and nothing is claimed.
        Assert.Equal(new UpdateFooter(null, FooterLink.None, null), Describe(installNow, Checked));
    }

    [Fact]
    public void AnInstallNowThatFailedSaysSoAndOffersItAgain()
    {
        var failed = new UpdateState { Pending = Release, InstallFailed = true };

        Assert.Equal(new UpdateFooter(null, FooterLink.InstallNow, "Couldn't install 3.1.0."), Describe(failed, Checked));
        Assert.Equal("Couldn't install 3.1.0.", Describe(failed with { Installer = Installer }, Checked).Caption);
    }

    [Fact]
    public void ACheckThatGotNoAnswerSaysSoUntilOneDoes()
    {
        var failed = new UpdateState { UpToDate = true, CheckFailed = true };

        Assert.Equal(new UpdateFooter(null, FooterLink.CheckForUpdates, "Couldn't check for updates."), Describe(failed, Checked));

        // Asking again shows only the new check.
        Assert.Equal(new UpdateFooter("Checking for updates", FooterLink.None, null), Describe(failed with { Checking = true }, Checked));

        // After three days the since line takes over.
        Assert.Equal(
            "Couldn't check for updates since Oct 7.",
            Describe(failed, Checked with { LastUpdateCheckUtc = Now.AddDays(-3) }).Caption);
    }

    [Fact]
    public void ThreeDaysWithoutACheckSaysSinceWhenAndNeverUpToDate()
    {
        var stale = Checked with { LastUpdateCheckUtc = new DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero) };

        Assert.Equal(
            new UpdateFooter(null, FooterLink.CheckForUpdates, "Couldn't check for updates since Oct 7."),
            Describe(new UpdateState { UpToDate = true }, stale));
        Assert.Null(Describe(new UpdateState(), Checked with { LastUpdateCheckUtc = Now.AddDays(-3).AddMinutes(1) }).Caption);
    }

    [Fact]
    public void TheDatesAreTheInterfacesEnglishInEveryRegion()
    {
        var stale = Checked with { LastUpdateCheckUtc = new DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero) };
        var lastYear = Checked with { LastUpdateCheckUtc = new DateTimeOffset(2025, 12, 30, 13, 0, 0, TimeSpan.Zero) };
        var saved = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            foreach (var region in new[] { "de-DE", "fr-FR", "en-GB", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(region);

                Assert.Equal("Couldn't check for updates since Oct 7.", Describe(new UpdateState(), stale).Caption);
                Assert.Equal("Couldn't check for updates since Dec 30, 2025.", Describe(new UpdateState(), lastYear).Caption);
                Assert.Equal("Updated yesterday", Describe(new UpdateState(), Checked with { UpdatedAtUtc = Now.AddDays(-1) }).Status);
            }
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = saved;
        }
    }

    [Fact]
    public void AReleaseThatFailedVerificationIsNamedUntilANewerOneCounts()
    {
        Assert.Equal(
            new UpdateFooter(null, FooterLink.CheckForUpdates, "3.1.0 failed verification and was skipped."),
            Describe(new UpdateState(), Checked with { SkippedUpdateVersion = "3.1.0" }));
        Assert.Null(Describe(new UpdateState(), Checked with { SkippedUpdateVersion = "3.0.0" }).Caption);

        // Pulled from GitHub, it is no longer what the check finds.
        Assert.Null(Describe(new UpdateState { UpToDate = true }, Checked with { SkippedUpdateVersion = "3.1.0" }).Caption);
    }

    [Fact]
    public void AnUpdateIsNamedOnItsDayAndTheNextWithItsReleaseNotes()
    {
        UpdateFooter Landed(DateTimeOffset at) => Describe(new UpdateState { UpToDate = true }, Checked with { UpdatedAtUtc = at });

        Assert.Equal(new UpdateFooter("Updated today", FooterLink.WhatsNew, null), Landed(Now.AddHours(-14)));
        Assert.Equal(new UpdateFooter("Updated yesterday", FooterLink.WhatsNew, null), Landed(Now.AddHours(-16)));
        Assert.Equal("Updated yesterday", Landed(new DateTimeOffset(2026, 10, 9, 0, 1, 0, Now.Offset)).Status);

        // The day after that, the usual footer is back.
        Assert.Equal(new UpdateFooter("Up to date", FooterLink.CheckForUpdates, null), Landed(new DateTimeOffset(2026, 10, 8, 23, 59, 0, Now.Offset)));
        Assert.Equal(new UpdateFooter("Up to date", FooterLink.CheckForUpdates, null), Landed(Now.AddDays(-6)));
    }

    [Fact]
    public void APendingUpdateOutranksTheUpdatedLine() =>
        Assert.Equal(
            FooterLink.InstallNow,
            Describe(new UpdateState { Pending = Release }, Checked with { UpdatedAtUtc = Now.AddHours(-1) }).Link);

    [Fact]
    public void NoFooterWordUsesAnEmDash()
    {
        var words = new[]
        {
            Describe(new UpdateState { Pending = Release }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, Installer = Installer }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, Downloading = true }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, Installing = true, SetupStarted = true }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, Installing = true, Downloading = true }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, InstallFailed = true }, Checked).Caption,
            Describe(new UpdateState { CheckFailed = true }, Checked).Caption,
            Describe(new UpdateState(), Checked with { LastUpdateCheckUtc = Now.AddDays(-9) }).Caption,
            Describe(new UpdateState(), Checked with { SkippedUpdateVersion = "3.1.0" }).Caption
        };

        Assert.All(words, word => Assert.DoesNotContain('—', word!));
        Assert.All(words, word => Assert.EndsWith(".", word!, StringComparison.Ordinal));
    }

    private static UpdateFooter Describe(UpdateState state, Settings settings) =>
        UpdateFooter.Describe(state, settings, Current, Now);
}
