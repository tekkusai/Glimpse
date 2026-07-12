namespace SpacePreview.Services;

internal static class UpdateNotificationPolicy
{
    internal static readonly TimeSpan SnoozeDuration = TimeSpan.FromDays(7);

    internal static TimeSpan DelayUntilPrompt(
        Settings settings,
        Version availableVersion,
        DateTimeOffset utcNow)
    {
        var versionText = VersionText(availableVersion);
        if (!string.Equals(
                settings.UpdateNotificationVersion,
                versionText,
                StringComparison.Ordinal))
        {
            return TimeSpan.Zero;
        }

        if (settings.UpdateNotificationSnoozedUntilUtc is not { } snoozedUntil)
        {
            return TimeSpan.Zero;
        }

        var delay = snoozedUntil - utcNow.ToUniversalTime();
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    internal static Settings Snooze(
        Settings settings,
        Version availableVersion,
        DateTimeOffset utcNow)
    {
        return settings with
        {
            UpdateNotificationVersion = VersionText(availableVersion),
            UpdateNotificationSnoozedUntilUtc = utcNow.ToUniversalTime() + SnoozeDuration
        };
    }

    private static string VersionText(Version version) => new Version(
        Math.Max(0, version.Major),
        Math.Max(0, version.Minor),
        Math.Max(0, version.Build)).ToString(3);
}
