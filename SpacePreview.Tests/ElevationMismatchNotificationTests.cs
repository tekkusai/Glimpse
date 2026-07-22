using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class ElevationMismatchNotificationTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void WarningRequiresLowerCurrentAndElevatedTarget(
        bool currentElevated,
        bool targetElevated,
        bool expected)
    {
        Assert.Equal(
            expected,
            ProcessIntegrityService.IsMismatch(currentElevated, targetElevated));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, null)]
    [InlineData(null, null)]
    public void UnknownIntegrityProducesACautiousAccessWarning(bool? current, bool? target)
    {
        Assert.Equal(
            PreviewAccessWarningKind.AccessUnavailable,
            ProcessIntegrityService.ClassifyWarning(current, target));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void KnownCompatibleIntegrityProducesNoWarning(bool current, bool target)
    {
        Assert.Null(ProcessIntegrityService.ClassifyWarning(current, target));
    }

    [Fact]
    public void ConfirmedMismatchIsDistinguishedFromUnavailableAccess()
    {
        Assert.Equal(
            PreviewAccessWarningKind.ConfirmedElevationMismatch,
            ProcessIntegrityService.ClassifyWarning(false, true));
    }

    [Fact]
    public void CurrentProcessElevationCanBeReadWithoutChangingIt()
    {
        Assert.True(ProcessIntegrityService.TryIsProcessElevated(
            checked((uint)Environment.ProcessId),
            out _));
    }

    [Fact]
    public void NotifyIconInteropIncludesModernBalloonFields()
    {
        var expected = IntPtr.Size == 8 ? 976 : 956;
        Assert.Equal(expected, Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>());
    }

    [Fact]
    public void WarningUsesAppOwnedPrimaryAndQuietTraySecondaryChannels()
    {
        var source = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");

        Assert.Contains("PreviewAccessWarningPolicy.TryBuildPersistedKey", source, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningPolicy.RecordShown", source, StringComparison.Ordinal);
        Assert.Contains("Settings.Update", source, StringComparison.Ordinal);
        Assert.Contains("TransientPreviewAccessWarningKey(warning)", source, StringComparison.Ordinal);
        Assert.Contains("_hiddenForSession", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIF_INFO", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIIF_NOSOUND", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIIF_RESPECT_QUIET_TIME", source, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningWindow.Heading", source, StringComparison.Ordinal);
        Assert.Contains("ShowAppOwnedWarning(warning)", source, StringComparison.Ordinal);
        Assert.Contains("ShowTrayWarning(warning)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_000, 1_999, 1_000, false)]
    [InlineData(1_000, 2_000, 1_000, true)]
    [InlineData(10_000, 100, 1_000, true)]
    [InlineData(1_000, 1_001, 0, true)]
    public void WarningCooldownAllowsLaterDialogsAndTickWrap(
        long previous,
        long now,
        long interval,
        bool expected)
    {
        Assert.Equal(
            expected,
            TrayIcon.HasWarningCooldownElapsed(previous, now, interval));
    }

    [Fact]
    public void WarningPolicyAllowsTwoInitialShowsTenMinutesApart()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.True(PreviewAccessWarningPolicy.CanShow(null, now));

        var first = PreviewAccessWarningPolicy.RecordShown(null, now);
        Assert.Equal(1, first.InitialShows);
        Assert.False(PreviewAccessWarningPolicy.CanShow(first, now.AddMinutes(9)));
        Assert.True(PreviewAccessWarningPolicy.CanShow(first, now.AddMinutes(10)));

        var second = PreviewAccessWarningPolicy.RecordShown(first, now.AddMinutes(10));
        Assert.Equal(2, second.InitialShows);
        Assert.False(PreviewAccessWarningPolicy.CanShow(second, now.AddDays(29)));
        Assert.True(PreviewAccessWarningPolicy.CanShow(second, now.AddMinutes(10).AddDays(30)));
    }

    [Fact]
    public void MonthlyReminderDoesNotRestoreInitialWarningBudget()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var entry = new PreviewAccessWarningHistoryEntry
        {
            InitialShows = PreviewAccessWarningPolicy.InitialShowLimit,
            LastShownUtc = now.AddDays(-30)
        };

        var reminder = PreviewAccessWarningPolicy.RecordShown(entry, now);
        Assert.Equal(PreviewAccessWarningPolicy.InitialShowLimit, reminder.InitialShows);
        Assert.False(PreviewAccessWarningPolicy.CanShow(reminder, now.AddDays(29)));
        Assert.True(PreviewAccessWarningPolicy.CanShow(reminder, now.AddDays(30)));
    }

    [Fact]
    public void WarningPolicyResetsUnknownSchemaAndSurvivesClockRollback()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var oldSchema = new PreviewAccessWarningHistoryEntry
        {
            SchemaVersion = PreviewAccessWarningPolicy.SchemaVersion + 1,
            InitialShows = PreviewAccessWarningPolicy.InitialShowLimit,
            LastShownUtc = now
        };
        Assert.True(PreviewAccessWarningPolicy.CanShow(oldSchema, now));
        Assert.Equal(1, PreviewAccessWarningPolicy.RecordShown(oldSchema, now).InitialShows);

        var current = PreviewAccessWarningPolicy.RecordShown(null, now);
        Assert.True(PreviewAccessWarningPolicy.CanShow(current, now.AddHours(-1)));
    }

    [Fact]
    public void PersistedWarningKeysArePerAppAndWarningKind()
    {
        var unavailable = new PreviewAccessWarningInfo(
            "Discord",
            "Discord",
            42,
            PreviewAccessWarningKind.AccessUnavailable);
        var elevated = unavailable with
        {
            Kind = PreviewAccessWarningKind.ConfirmedElevationMismatch
        };
        var unstable = unavailable with { Key = "pid:42" };

        Assert.True(PreviewAccessWarningPolicy.TryBuildPersistedKey(unavailable, out var unavailableKey));
        Assert.True(PreviewAccessWarningPolicy.TryBuildPersistedKey(elevated, out var elevatedKey));
        Assert.NotEqual(unavailableKey, elevatedKey);
        Assert.False(PreviewAccessWarningPolicy.TryBuildPersistedKey(unstable, out _));
    }

    [Fact]
    public void WarningHistoryRoundTripsThroughSettingsJson()
    {
        var lastShown = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var settings = new Settings
        {
            PreviewAccessWarnings = new Dictionary<string, PreviewAccessWarningHistoryEntry>
            {
                ["V1|ACCESSUNAVAILABLE|DISCORD"] = new()
                {
                    InitialShows = 2,
                    LastShownUtc = lastShown
                }
            }
        };

        var roundTripped = JsonSerializer.Deserialize<Settings>(
            JsonSerializer.Serialize(settings));

        var entry = Assert.Single(roundTripped!.PreviewAccessWarnings).Value;
        Assert.Equal(PreviewAccessWarningPolicy.SchemaVersion, entry.SchemaVersion);
        Assert.Equal(2, entry.InitialShows);
        Assert.Equal(lastShown, entry.LastShownUtc);
    }

    [Fact]
    public void SettingsNormalizationRepairsCaseDuplicatesAndNullHistory()
    {
        var earlier = new PreviewAccessWarningHistoryEntry
        {
            InitialShows = 1,
            LastShownUtc = DateTimeOffset.UtcNow.AddDays(-1)
        };
        var later = earlier with
        {
            InitialShows = 2,
            LastShownUtc = DateTimeOffset.UtcNow
        };
        var settings = new Settings
        {
            PreviewAccessWarnings = new Dictionary<string, PreviewAccessWarningHistoryEntry>
            {
                ["v1|accessunavailable|discord"] = earlier,
                ["V1|ACCESSUNAVAILABLE|DISCORD"] = later,
                ["V1|ACCESSUNAVAILABLE|BROKEN"] = null!
            }
        };

        var normalized = Settings.Normalize(settings);

        var entry = Assert.Single(normalized.PreviewAccessWarnings).Value;
        Assert.Equal(2, entry.InitialShows);
        Assert.Same(later, entry);
    }

    [Fact]
    public void AppOwnedWarningIsCautiousNonActivatingAndSelfDismissing()
    {
        var source = TestSource.Read(@"SpacePreview\Services\PreviewAccessWarningWindow.xaml.cs");
        var xaml = TestSource.Read(@"SpacePreview\Services\PreviewAccessWarningWindow.xaml");
        var warning = new PreviewAccessWarningInfo(
            "discord",
            "Discord",
            42,
            PreviewAccessWarningKind.AccessUnavailable);

        Assert.Contains("Glimpse couldn't access this file picker.", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.Ordinal);
        Assert.Contains("If the app is running as administrator", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.Ordinal);
        Assert.DoesNotContain("Discord is running as administrator", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.Ordinal);
        Assert.Contains("WS_EX_NOACTIVATE", source, StringComparison.Ordinal);
        Assert.Contains("ApplyNoActivateStyle(hwnd)", source, StringComparison.Ordinal);
        Assert.Contains("at={Left:0},{Top:0}", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Dismiss notification\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedElevationCopyRemainsDefinitive()
    {
        var warning = new PreviewAccessWarningInfo(
            "adminapp",
            "AdminApp",
            42,
            PreviewAccessWarningKind.ConfirmedElevationMismatch);

        var message = PreviewAccessWarningWindow.BuildMessage(warning);
        Assert.Contains("AdminApp is running as administrator", message, StringComparison.Ordinal);
        Assert.Contains("Run both at the same permission level", message, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningPlacementUsesBottomRightAndHandlesTinyWorkAreas()
    {
        Assert.Equal(
            new Point(604, 664),
            PreviewAccessWarningWindow.CalculatePlacement(
                new Rect(0, 0, 1000, 800),
                new Size(380, 120),
                16));

        var tiny = PreviewAccessWarningWindow.CalculatePlacement(
            new Rect(100, 200, 240, 100),
            new Size(380, 120),
            16);
        Assert.Equal(new Point(100, 200), tiny);
    }

    [Fact]
    public void IntegrityLookupStaysOffTheKeyboardHookLane()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var hookStart = controller.IndexOf("private bool OnKeyDown", StringComparison.Ordinal);
        var hookEnd = controller.IndexOf("private void OnKeyUp", hookStart, StringComparison.Ordinal);
        var hook = controller[hookStart..hookEnd];

        Assert.Contains("QueueElevationMismatchCheck", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("ProcessIntegrityService", hook, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.GetProcess", hook, StringComparison.Ordinal);
        Assert.Contains("Task.Run", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ForegroundObservationOnlyRefreshesVerifiedFileDialogs()
    {
        var monitor = TestSource.Read(@"SpacePreview\Services\CommonFileDialogSelectionMonitor.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");

        Assert.DoesNotContain("DialogObserved", monitor, StringComparison.Ordinal);
        Assert.Contains("HasDialogWindowClass(root)", monitor, StringComparison.Ordinal);
        Assert.Contains("OnForegroundPoll", monitor, StringComparison.Ordinal);
        Assert.Contains("_foregroundPoll.Start()", monitor, StringComparison.Ordinal);
        Assert.Contains("_foregroundPoll.Stop()", monitor, StringComparison.Ordinal);
        Assert.Contains("_verifiedDialog == root", monitor, StringComparison.Ordinal);
        Assert.Contains("IsCommonFileDialogRoot(root)", monitor, StringComparison.Ordinal);
        Assert.Contains("RequestRefresh(root, settleMilliseconds: 0)", monitor, StringComparison.Ordinal);
        Assert.Contains("CompletePreviewAccessCheck", controller, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningDetected?.Invoke(warning)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("PollPreviewAccessAttempt", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewAccessAttemptTimer", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("DialogObserved", controller, StringComparison.Ordinal);
        Assert.Contains("QueueForegroundElevationMismatchCheck();", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevationCheckDoesNotRequireCrossIntegrityChildEnumeration()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var start = controller.IndexOf("private void StartElevationMismatchCheck", StringComparison.Ordinal);
        var end = controller.IndexOf("private void OnCommonFileDialogSelectionChanged", start, StringComparison.Ordinal);
        var check = controller[start..end];

        Assert.Contains("HasDialogWindowClass(dialogRoot)", check, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCommonFileDialogRoot(dialogRoot)", check, StringComparison.Ordinal);
        Assert.Contains("foregroundRoot != dialogRoot", check, StringComparison.Ordinal);
    }

    [Fact]
    public void AppWiresAccessWarningToTrayNotificationBeforeControllerStart()
    {
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        var eventIndex = app.IndexOf("PreviewAccessWarningDetected +=", StringComparison.Ordinal);
        var showIndex = app.IndexOf("ShowPreviewAccessWarning(warning)", StringComparison.Ordinal);
        var startIndex = app.IndexOf("_controller.Start();", eventIndex, StringComparison.Ordinal);

        Assert.True(eventIndex >= 0);
        Assert.True(showIndex > eventIndex);
        Assert.True(startIndex > showIndex);
    }

}
