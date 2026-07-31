namespace SpacePreview.Services;

internal sealed record PreviewAccessWarningHistoryEntry
{
    // Retained only to migrate the former per-app reminder history into the
    // global lifetime budget. New deliveries no longer append these entries.
    public int SchemaVersion { get; init; } = PreviewAccessWarningPolicy.SchemaVersion;

    public int InitialShows { get; init; }

    public DateTimeOffset LastShownUtc { get; init; }
}

internal readonly record struct PreviewAccessWarningLifetimeState(
    int Shows,
    DateTimeOffset? LastShownUtc,
    bool DismissedForever);

internal static class PreviewAccessWarningPolicy
{
    // SchemaVersion belongs to the legacy per-app entries above. The active
    // lifetime state is deliberately unversioned so an app update can never
    // replenish a user's warning budget.
    internal const int SchemaVersion = 2;
    internal const int LifetimeShowLimit = 2;

    internal static readonly TimeSpan LifetimeReminderInterval = TimeSpan.FromDays(7);

    internal static bool CanShowLifetime(
        PreviewAccessWarningLifetimeState state,
        DateTimeOffset nowUtc)
    {
        if (state.DismissedForever || state.Shows >= LifetimeShowLimit)
        {
            return false;
        }

        if (state.Shows <= 0)
        {
            return true;
        }

        if (state.LastShownUtc is not { } lastShown || lastShown == default)
        {
            // A malformed partial history must fail quiet rather than create
            // an unexpected reminder.
            return false;
        }

        var elapsed = nowUtc.ToUniversalTime() - lastShown.ToUniversalTime();
        return elapsed >= LifetimeReminderInterval;
    }

    internal static PreviewAccessWarningLifetimeState RecordLifetimeShow(
        PreviewAccessWarningLifetimeState state,
        DateTimeOffset nowUtc) =>
        state with
        {
            Shows = Math.Min(Math.Max(0, state.Shows) + 1, LifetimeShowLimit),
            LastShownUtc = nowUtc.ToUniversalTime()
        };

    internal static PreviewAccessWarningLifetimeState DismissForever(
        PreviewAccessWarningLifetimeState state,
        DateTimeOffset nowUtc) =>
        new(
            LifetimeShowLimit,
            state.LastShownUtc ?? nowUtc.ToUniversalTime(),
            DismissedForever: true);

    internal static PreviewAccessWarningLifetimeState NormalizeLifetimeState(
        int shows,
        DateTimeOffset? lastShownUtc,
        bool dismissedForever,
        IEnumerable<PreviewAccessWarningHistoryEntry> legacyEntries)
    {
        var normalizedShows = Math.Clamp(shows, 0, LifetimeShowLimit);
        DateTimeOffset? normalizedLastShown = lastShownUtc is { } explicitLast && explicitLast != default
            ? explicitLast.ToUniversalTime()
            : null;
        var legacyShows = 0;
        DateTimeOffset? latestLegacyShow = null;

        foreach (var entry in legacyEntries)
        {
            if (entry is not
                {
                    InitialShows: > 0,
                    LastShownUtc: { } legacyLast
                }
                || legacyLast == default)
            {
                continue;
            }

            legacyShows = Math.Min(
                LifetimeShowLimit,
                legacyShows + Math.Clamp(entry.InitialShows, 0, LifetimeShowLimit));
            var normalizedLegacyLast = legacyLast.ToUniversalTime();
            if (latestLegacyShow is null || normalizedLegacyLast > latestLegacyShow)
            {
                latestLegacyShow = normalizedLegacyLast;
            }
        }

        normalizedShows = Math.Max(normalizedShows, legacyShows);
        if (latestLegacyShow is { } latest
            && (normalizedLastShown is null || latest > normalizedLastShown))
        {
            normalizedLastShown = latest;
        }

        if (dismissedForever)
        {
            normalizedShows = LifetimeShowLimit;
        }

        if (normalizedShows == 0)
        {
            normalizedLastShown = null;
        }

        return new PreviewAccessWarningLifetimeState(
            normalizedShows,
            normalizedLastShown,
            dismissedForever);
    }

    internal static PreviewAccessWarningLifetimeState FromSettings(Settings settings) =>
        new(
            Math.Clamp(
                settings.PreviewAccessWarningLifetimeShows,
                0,
                LifetimeShowLimit),
            settings.PreviewAccessWarningLastShownUtc,
            settings.PreviewAccessWarningDismissedForever);

    internal static Settings ApplyLifetimeState(
        Settings settings,
        PreviewAccessWarningLifetimeState state) =>
        settings with
        {
            PreviewAccessWarningLifetimeShows = Math.Clamp(
                state.Shows,
                0,
                LifetimeShowLimit),
            PreviewAccessWarningLastShownUtc = state.LastShownUtc?.ToUniversalTime(),
            PreviewAccessWarningDismissedForever = state.DismissedForever
        };
}
