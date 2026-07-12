using System.Text.Json;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class UpdateNotificationPolicyTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NewReleaseIgnoresAnOlderReleaseSnooze()
    {
        var settings = new Settings
        {
            UpdateNotificationVersion = "1.1.0",
            UpdateNotificationSnoozedUntilUtc = Now.AddDays(6)
        };

        var delay = UpdateNotificationPolicy.DelayUntilPrompt(
            settings,
            new Version(1, 2, 0),
            Now);

        Assert.Equal(TimeSpan.Zero, delay);
    }

    [Fact]
    public void SameReleaseWaitsUntilItsSnoozeExpires()
    {
        var settings = new Settings
        {
            UpdateNotificationVersion = "1.2.0",
            UpdateNotificationSnoozedUntilUtc = Now.AddHours(3)
        };

        Assert.Equal(
            TimeSpan.FromHours(3),
            UpdateNotificationPolicy.DelayUntilPrompt(
                settings,
                new Version(1, 2, 0),
                Now));
        Assert.Equal(
            TimeSpan.Zero,
            UpdateNotificationPolicy.DelayUntilPrompt(
                settings,
                new Version(1, 2, 0),
                Now.AddHours(3)));
    }

    [Fact]
    public void DismissalSnoozesOnlyThatReleaseForSevenDays()
    {
        var snoozed = UpdateNotificationPolicy.Snooze(
            new Settings(),
            new Version(1, 2),
            Now.ToOffset(TimeSpan.FromHours(-4)));

        Assert.Equal("1.2.0", snoozed.UpdateNotificationVersion);
        Assert.Equal(Now.AddDays(7), snoozed.UpdateNotificationSnoozedUntilUtc);
    }

    [Fact]
    public void SettingsNormalizationCanonicalizesOrDropsNotificationHistory()
    {
        var canonical = Settings.Normalize(new Settings
        {
            UpdateNotificationVersion = " V1.2.0 ",
            UpdateNotificationSnoozedUntilUtc = Now.ToOffset(TimeSpan.FromHours(-4))
        });
        var invalid = Settings.Normalize(new Settings
        {
            UpdateNotificationVersion = "1.2-beta",
            UpdateNotificationSnoozedUntilUtc = Now.AddDays(30)
        });

        Assert.Equal("1.2.0", canonical.UpdateNotificationVersion);
        Assert.Equal(Now, canonical.UpdateNotificationSnoozedUntilUtc);
        Assert.Null(invalid.UpdateNotificationVersion);
        Assert.Null(invalid.UpdateNotificationSnoozedUntilUtc);
    }

    [Fact]
    public void NotificationSnoozeRoundTripsThroughSettingsJson()
    {
        var settings = UpdateNotificationPolicy.Snooze(
            new Settings(),
            new Version(1, 2, 0),
            Now);

        var roundTripped = JsonSerializer.Deserialize<Settings>(
            JsonSerializer.Serialize(settings));

        Assert.Equal("1.2.0", roundTripped!.UpdateNotificationVersion);
        Assert.Equal(Now.AddDays(7), roundTripped.UpdateNotificationSnoozedUntilUtc);
    }

    [Fact]
    public void AutomaticPromptUsesTheExistingWindowWithoutActivationAndWaitsForSafeUi()
    {
        var tray = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var window = TestSource.Read(@"SpacePreview\Services\AppUpdateWindow.xaml.cs");

        Assert.Contains(
            "ShowUpdateWindow(update, activate: false, automatic: true)",
            tray,
            StringComparison.Ordinal);
        Assert.Contains("ShowActivated = activate", tray, StringComparison.Ordinal);
        Assert.Contains("_controller?.CanShowUpdateNotification == true", app, StringComparison.Ordinal);
        Assert.Contains("&& !_previewVisible", controller, StringComparison.Ordinal);
        Assert.Contains("&& _pinnedWindows.Count == 0", controller, StringComparison.Ordinal);
        Assert.Contains("&& !_fullscreenInputSuspension.IsSuspended", controller, StringComparison.Ordinal);
        Assert.Contains("window.InstallerLaunched", tray, StringComparison.Ordinal);
        Assert.Contains("InstallerLaunched = true", window, StringComparison.Ordinal);
        Assert.Contains("|| !window.SnoozeRequested", tray, StringComparison.Ordinal);
        Assert.Contains("SnoozeRequested = true", window, StringComparison.Ordinal);
        Assert.Contains("message == WmSysCommand", window, StringComparison.Ordinal);
        Assert.Contains("== ScClose", window, StringComparison.Ordinal);
    }
}
