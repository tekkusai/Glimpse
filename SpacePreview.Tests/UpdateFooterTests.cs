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
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    // Saturday afternoon on Eastern time, October 10.
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 15, 0, 0, TimeSpan.FromHours(-4));

    private static readonly AppUpdateRelease Release = new(
        new Version(3, 1, 0),
        "v3.1.0",
        new Uri("https://github.com/tekkusai/Glimpse/releases/download/v3.1.0/Glimpse-Setup-3.1.0.exe"),
        ReleaseFixture.InstallerSize,
        new string('A', 64),
        new Uri("https://github.com/tekkusai/Glimpse/releases/download/v3.1.0/Glimpse-Setup-3.1.0.exe.sha256"));

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
    public void APendingUpdateSaysWhenItInstalls() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "3.1.0 installs the next time you step away."),
            Describe(new UpdateState { Pending = Release }, Checked));

    [Fact]
    public void WithAutomaticInstallsOffItWaitsForTheUser() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.InstallNow, "3.1.0 is ready to install."),
            Describe(new UpdateState { Pending = Release }, Checked with { InstallUpdatesAutomatically = false }));

    [Fact]
    public void InstallNowSaysGlimpseWillBeBack() =>
        Assert.Equal(
            new UpdateFooter(null, FooterLink.None, "Installing 3.1.0. Back in a few seconds."),
            Describe(new UpdateState { Pending = Release, Installing = true, Downloading = true }, Checked));

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
    public void TheDateFollowsTheUsersCulture()
    {
        var stale = Checked with { LastUpdateCheckUtc = new DateTimeOffset(2026, 10, 7, 13, 0, 0, TimeSpan.Zero) };

        Assert.Equal(
            "Couldn't check for updates since 7 Oct.",
            UpdateFooter.Describe(new UpdateState(), stale, Current, Now, CultureInfo.GetCultureInfo("en-GB")).Caption);
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
    public void AnUpdateIsNamedForSevenDaysWithItsReleaseNotes()
    {
        UpdateFooter Landed(DateTimeOffset at) => Describe(new UpdateState { UpToDate = true }, Checked with { UpdatedAtUtc = at });

        Assert.Equal(new UpdateFooter("Updated today", FooterLink.WhatsNew, null), Landed(Now.AddHours(-14)));
        Assert.Equal("Updated yesterday", Landed(Now.AddHours(-16)).Status);
        Assert.Equal("Updated Oct 4", Landed(Now.AddDays(-6)).Status);
        Assert.Equal(new UpdateFooter("Up to date", FooterLink.CheckForUpdates, null), Landed(Now.AddDays(-7)));
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
            Describe(new UpdateState { Pending = Release, Downloading = true }, Checked).Caption,
            Describe(new UpdateState { Pending = Release, Installing = true }, Checked).Caption,
            Describe(new UpdateState(), Checked with { LastUpdateCheckUtc = Now.AddDays(-9) }).Caption,
            Describe(new UpdateState(), Checked with { SkippedUpdateVersion = "3.1.0" }).Caption
        };

        Assert.All(words, word => Assert.DoesNotContain('—', word!));
        Assert.All(words, word => Assert.EndsWith(".", word!, StringComparison.Ordinal));
    }

    private static UpdateFooter Describe(UpdateState state, Settings settings) =>
        UpdateFooter.Describe(state, settings, Current, Now, English);
}
