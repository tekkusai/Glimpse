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

        Assert.Contains("PreviewAccessWarningPolicy.CanShowLifetime", source, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningPolicy.RecordLifetimeShow", source, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningPolicy.DismissForever", source, StringComparison.Ordinal);
        Assert.Contains("Settings.Update", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryBuildPersistedKey", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TransientPreviewAccessWarningKey", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIF_INFO", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIIF_NOSOUND", source, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.NIIF_RESPECT_QUIET_TIME", source, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningWindow.Heading", source, StringComparison.Ordinal);
        Assert.Contains("ShowAppOwnedWarning(warning, finalReminder)", source, StringComparison.Ordinal);
        Assert.Contains("ShowTrayWarning(warning, finalReminder)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WarningPolicyAllowsOnlyOneReminderAfterSevenDays()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var empty = new PreviewAccessWarningLifetimeState(0, null, false);
        Assert.True(PreviewAccessWarningPolicy.CanShowLifetime(empty, now));

        var first = PreviewAccessWarningPolicy.RecordLifetimeShow(empty, now);
        Assert.Equal(1, first.Shows);
        Assert.False(PreviewAccessWarningPolicy.CanShowLifetime(first, now.AddDays(7).AddTicks(-1)));
        Assert.True(PreviewAccessWarningPolicy.CanShowLifetime(first, now.AddDays(7)));

        var second = PreviewAccessWarningPolicy.RecordLifetimeShow(first, now.AddDays(7));
        Assert.Equal(PreviewAccessWarningPolicy.LifetimeShowLimit, second.Shows);
        Assert.False(PreviewAccessWarningPolicy.CanShowLifetime(second, now.AddYears(10)));
    }

    [Fact]
    public void ManualDismissalPermanentlyCompletesTheBudget()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var first = PreviewAccessWarningPolicy.RecordLifetimeShow(
            new PreviewAccessWarningLifetimeState(0, null, false),
            now);

        var dismissed = PreviewAccessWarningPolicy.DismissForever(first, now.AddMinutes(1));

        Assert.True(dismissed.DismissedForever);
        Assert.Equal(PreviewAccessWarningPolicy.LifetimeShowLimit, dismissed.Shows);
        Assert.False(PreviewAccessWarningPolicy.CanShowLifetime(dismissed, now.AddYears(10)));
    }

    [Fact]
    public void WarningPolicyFailsQuietOnPartialHistoryAndClockRollback()
    {
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        Assert.False(PreviewAccessWarningPolicy.CanShowLifetime(
            new PreviewAccessWarningLifetimeState(1, null, false),
            now));
        Assert.False(PreviewAccessWarningPolicy.CanShowLifetime(
            new PreviewAccessWarningLifetimeState(1, now, false),
            now.AddHours(-1)));
    }

    [Fact]
    public void GlobalWarningLifetimeRoundTripsThroughSettingsJson()
    {
        var lastShown = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var settings = new Settings
        {
            PreviewAccessWarningLifetimeShows = 1,
            PreviewAccessWarningLastShownUtc = lastShown,
            PreviewAccessWarningDismissedForever = true
        };

        var roundTripped = JsonSerializer.Deserialize<Settings>(
            JsonSerializer.Serialize(settings));

        Assert.Equal(1, roundTripped!.PreviewAccessWarningLifetimeShows);
        Assert.Equal(lastShown, roundTripped.PreviewAccessWarningLastShownUtc);
        Assert.True(roundTripped.PreviewAccessWarningDismissedForever);
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

        Assert.Contains("Glimpse can't access this file picker.", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.Ordinal);
        Assert.Contains("If Discord is running as administrator", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.Ordinal);
        Assert.Contains("restart it without administrator access", PreviewAccessWarningWindow.BuildMessage(warning), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "Close this notice to stop future reminders.",
            PreviewAccessWarningWindow.BuildReminderMessage(finalReminder: false));
        Assert.Equal(
            "Final heads-up. Glimpse won't show this notice again.",
            PreviewAccessWarningWindow.BuildReminderMessage(finalReminder: true));
        Assert.DoesNotContain((char)0x2014, PreviewAccessWarningWindow.BuildReminderMessage(finalReminder: true));
        Assert.True(PreviewAccessWarningWindow.BuildTrayMessage(warning, finalReminder: false).Length <= 255);
        Assert.Contains("WS_EX_NOACTIVATE", source, StringComparison.Ordinal);
        Assert.Contains("ApplyNoActivateStyle(hwnd)", source, StringComparison.Ordinal);
        Assert.Contains("at={Left:0},{Top:0}", source, StringComparison.Ordinal);
        Assert.Contains("DispatcherTimer", source, StringComparison.Ordinal);
        Assert.Contains("ShowActivated=\"False\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Topmost=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Dismiss notification\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FooterText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmedElevationCopyUsesFriendlyPermissionLanguage()
    {
        var warning = new PreviewAccessWarningInfo(
            "adminapp",
            "AdminApp",
            42,
            PreviewAccessWarningKind.ConfirmedElevationMismatch);

        var message = PreviewAccessWarningWindow.BuildMessage(warning);
        Assert.Equal(
            "Glimpse and AdminApp are running at different permission levels. Restart AdminApp without administrator access to use previews.",
            message);
        Assert.DoesNotContain("elevation", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("privilege", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WarningCopyBoundsLongAppNamesAndPreservesTheTrayReassurance()
    {
        var warning = new PreviewAccessWarningInfo(
            "longapp",
            new string('A', 300),
            42,
            PreviewAccessWarningKind.ConfirmedElevationMismatch);

        var message = PreviewAccessWarningWindow.BuildMessage(warning);
        var trayMessage = PreviewAccessWarningWindow.BuildTrayMessage(warning, finalReminder: true);

        Assert.Contains("... are running at different permission levels.", message, StringComparison.Ordinal);
        Assert.True(trayMessage.Length <= 255);
        Assert.EndsWith(
            PreviewAccessWarningWindow.BuildReminderMessage(finalReminder: true),
            trayMessage,
            StringComparison.Ordinal);
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
    public void ForegroundObservationProbesOnlyVerifiedFileDialogsWithinTheGlobalBudget()
    {
        var monitor = TestSource.Read(@"SpacePreview\Services\CommonFileDialogSelectionMonitor.cs");
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");

        Assert.Contains("DialogObserved?.Invoke(root)", monitor, StringComparison.Ordinal);
        Assert.Contains("HasDialogWindowClass(root)", monitor, StringComparison.Ordinal);
        Assert.Contains("OnForegroundPoll", monitor, StringComparison.Ordinal);
        Assert.Contains("_foregroundPoll.Start()", monitor, StringComparison.Ordinal);
        Assert.Contains("_foregroundPoll.Stop()", monitor, StringComparison.Ordinal);
        Assert.Contains("_verifiedDialog == root", monitor, StringComparison.Ordinal);
        Assert.Contains("IsCommonFileDialogRoot(root)", monitor, StringComparison.Ordinal);
        Assert.Contains("RequestRefresh(root, settleMilliseconds: 0)", monitor, StringComparison.Ordinal);
        Assert.Contains("DialogObserved += OnCommonFileDialogObserved", controller, StringComparison.Ordinal);
        Assert.Contains("DialogObserved -= OnCommonFileDialogObserved", controller, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningPolicy.CanShowLifetime", controller, StringComparison.Ordinal);
        Assert.Contains("_observedAccessDialogs.Add", controller, StringComparison.Ordinal);
        Assert.Contains("CompletePreviewAccessCheck", controller, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningDetected?.Invoke(warning)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("PollPreviewAccessAttempt", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("_previewAccessAttemptTimer", controller, StringComparison.Ordinal);
        Assert.Contains("QueueForegroundElevationMismatchCheck();", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void ManualWindowDismissalPermanentlyRetiresFutureWarnings()
    {
        var window = TestSource.Read(@"SpacePreview\Services\PreviewAccessWarningWindow.xaml.cs");
        var tray = TestSource.Read(@"SpacePreview\Services\TrayIcon.cs");

        Assert.Contains("PreviewAccessWarningDismissalReason.Manual", window, StringComparison.Ordinal);
        Assert.Contains("Dismissed?.Invoke(reason)", window, StringComparison.Ordinal);
        Assert.Contains("reason == PreviewAccessWarningDismissalReason.Manual", tray, StringComparison.Ordinal);
        Assert.Contains("PermanentlyDismissPreviewAccessWarnings", tray, StringComparison.Ordinal);
        Assert.Contains("PreviewAccessWarningPolicy.DismissForever", tray, StringComparison.Ordinal);
    }

    [Fact]
    public void ElevationCheckSurvivesForegroundChangesAndValidatesCapturedDialogIdentity()
    {
        var controller = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var start = controller.IndexOf("private void StartElevationMismatchCheck", StringComparison.Ordinal);
        var end = controller.IndexOf("private void OnCommonFileDialogSelectionChanged", start, StringComparison.Ordinal);
        var check = controller[start..end];

        Assert.Contains("HasDialogWindowClass(dialogRoot)", check, StringComparison.Ordinal);
        Assert.DoesNotContain("IsCommonFileDialogRoot(dialogRoot)", check, StringComparison.Ordinal);
        Assert.DoesNotContain("foregroundRoot", check, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.IsWindow(dialogRoot)", check, StringComparison.Ordinal);
        Assert.Contains("NativeMethods.GetWindowProcessId(dialogRoot) != processId", check, StringComparison.Ordinal);
        Assert.Contains("dialog closed or handle reused", check, StringComparison.Ordinal);
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
