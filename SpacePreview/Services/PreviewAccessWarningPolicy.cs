namespace SpacePreview.Services;

internal readonly record struct PreviewAccessWarningLifetimeState(
    int Shows,
    DateTimeOffset? LastShownUtc,
    bool DismissedForever);

/// <summary>
/// The access notice's lifetime budget: at most two showings ever, the
/// second only a week after the first, and none after the user closes one
/// by hand.
/// </summary>
internal static class PreviewAccessWarningPolicy
{
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

    internal static PreviewAccessWarningLifetimeState FromSettings(Settings settings) =>
        new(
            Math.Clamp(settings.PreviewAccessWarningLifetimeShows, 0, LifetimeShowLimit),
            settings.PreviewAccessWarningLastShownUtc,
            settings.PreviewAccessWarningDismissedForever);

    internal static Settings ApplyLifetimeState(
        Settings settings,
        PreviewAccessWarningLifetimeState state) =>
        settings with
        {
            PreviewAccessWarningLifetimeShows = Math.Clamp(state.Shows, 0, LifetimeShowLimit),
            PreviewAccessWarningLastShownUtc = state.LastShownUtc?.ToUniversalTime(),
            PreviewAccessWarningDismissedForever = state.DismissedForever
        };
}
