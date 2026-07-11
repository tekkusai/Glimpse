namespace SpacePreview.Services;

internal sealed record PreviewAccessWarningHistoryEntry
{
    public int SchemaVersion { get; init; } = PreviewAccessWarningPolicy.SchemaVersion;

    public int InitialShows { get; init; }

    public DateTimeOffset LastShownUtc { get; init; }
}

internal static class PreviewAccessWarningPolicy
{
    internal const int SchemaVersion = 1;
    internal const int InitialShowLimit = 2;
    internal const int MaximumPersistedApps = 64;

    internal static readonly TimeSpan InitialCooldown = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan ReminderInterval = TimeSpan.FromDays(30);

    internal static bool CanShow(
        PreviewAccessWarningHistoryEntry? entry,
        DateTimeOffset nowUtc)
    {
        if (!IsCurrent(entry))
        {
            return true;
        }

        var interval = entry!.InitialShows < InitialShowLimit
            ? InitialCooldown
            : ReminderInterval;
        var elapsed = nowUtc - entry.LastShownUtc;
        return elapsed < TimeSpan.Zero || elapsed >= interval;
    }

    internal static PreviewAccessWarningHistoryEntry RecordShown(
        PreviewAccessWarningHistoryEntry? entry,
        DateTimeOffset nowUtc)
    {
        var initialShows = IsCurrent(entry)
            ? Math.Clamp(entry!.InitialShows, 0, InitialShowLimit)
            : 0;

        return new PreviewAccessWarningHistoryEntry
        {
            InitialShows = Math.Min(initialShows + 1, InitialShowLimit),
            LastShownUtc = nowUtc
        };
    }

    internal static bool TryBuildPersistedKey(
        PreviewAccessWarningInfo warning,
        out string key)
    {
        key = string.Empty;
        if (warning.Key.StartsWith("pid:", StringComparison.OrdinalIgnoreCase)
            || warning.Key.StartsWith("hwnd:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        key = $"v{SchemaVersion}|{warning.Kind}|{warning.Key}".ToUpperInvariant();
        return true;
    }

    private static bool IsCurrent(PreviewAccessWarningHistoryEntry? entry) =>
        entry is
        {
            SchemaVersion: SchemaVersion,
            InitialShows: >= 0,
            LastShownUtc: { } lastShown
        }
        && lastShown != default;
}
