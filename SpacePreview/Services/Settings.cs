using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// Persisted user preferences. "Enabled" is deliberately not here: a paused
/// utility should come back alive on the next launch.
/// </summary>
internal sealed record Settings
{
    public ThemePreference Theme { get; init; } = ThemePreference.FollowSystem;

    /// <summary>The first-run "Press Space" introduction has been shown.</summary>
    public bool WelcomeShown { get; init; }

    /// <summary>
    /// Volume for video and audio previews, 0..1. Starts at a considerate
    /// 75% and thereafter remembers wherever the user last set it.
    /// </summary>
    public double MediaVolume { get; init; } = 0.75;

    /// <summary>Whether the user last left media previews muted.</summary>
    public bool MediaMuted { get; init; }

    /// <summary>
    /// Whether a newly pinned preview starts above other windows. Each pinned
    /// window can still override this for its own lifetime.
    /// </summary>
    public bool PinnedAlwaysOnTopDefault { get; init; } = true;

    /// <summary>
    /// Global, update-stable budget for inaccessible file-picker notices.
    /// Manual dismissal permanently completes the budget; a timed-out first
    /// notice permits one final reminder after seven days.
    /// </summary>
    public int PreviewAccessWarningLifetimeShows { get; init; }

    public DateTimeOffset? PreviewAccessWarningLastShownUtc { get; init; }

    public bool PreviewAccessWarningDismissedForever { get; init; }

    /// <summary>
    /// Legacy per-app history retained for conservative one-time migration.
    /// Existing users never receive a replenished budget after an update.
    /// </summary>
    public Dictionary<string, PreviewAccessWarningHistoryEntry> PreviewAccessWarnings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The release whose automatic update prompt was most recently dismissed.
    /// A different release always gets its own first prompt.
    /// </summary>
    public string? UpdateNotificationVersion { get; init; }

    /// <summary>When that release may be offered automatically again.</summary>
    public DateTimeOffset? UpdateNotificationSnoozedUntilUtc { get; init; }

    // Stage size is deliberately not persisted: content shapes the stage,
    // and a border-drag holds only until the preview closes.

    private static Settings? _current;

    /// <summary>
    /// The loaded settings. Lazy on purpose: an eager initializer would run
    /// in static-initialization textual order, before <see cref="Options"/>
    /// and <see cref="Pathname"/> exist, and silently reset every saved
    /// preference. All reads and updates happen on the UI thread (tray menu,
    /// preview window), so a plain property is enough.
    /// </summary>
    public static Settings Current => _current ??= Load();

    /// <summary>Applies a change to <see cref="Current"/> and persists it.</summary>
    public static void Update(Func<Settings, Settings> change)
    {
        _current = change(Current);
        _current.Save();
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Pathname { get; } = AppPaths.InDataRoot("settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(Pathname))
            {
                var json = File.ReadAllText(Pathname);
                var loaded = JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings();
                var normalized = Normalize(loaded);

                // Persist the legacy per-app migration immediately. Keeping
                // the normalized lifetime fields on disk makes the global
                // budget explicit and stable even if a later release stops
                // understanding the old dictionary shape.
                if (loaded.PreviewAccessWarningLifetimeShows
                        != normalized.PreviewAccessWarningLifetimeShows
                    || loaded.PreviewAccessWarningLastShownUtc?.ToUniversalTime()
                        != normalized.PreviewAccessWarningLastShownUtc
                    || loaded.PreviewAccessWarningDismissedForever
                        != normalized.PreviewAccessWarningDismissedForever)
                {
                    normalized.Save();
                }

                return normalized;
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults silently.
        }

        return new Settings();
    }

    internal static Settings Normalize(Settings settings)
    {
        var history = new Dictionary<string, PreviewAccessWarningHistoryEntry>(
            StringComparer.OrdinalIgnoreCase);
        if (settings.PreviewAccessWarnings is { } loadedHistory)
        {
            foreach (var pair in loadedHistory)
            {
                if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is { } entry)
                {
                    // Assignment deliberately makes the last case-insensitive
                    // duplicate win instead of throwing on a malformed file.
                    history[pair.Key] = entry;
                }
            }
        }

        var lifetime = PreviewAccessWarningPolicy.NormalizeLifetimeState(
            settings.PreviewAccessWarningLifetimeShows,
            settings.PreviewAccessWarningLastShownUtc,
            settings.PreviewAccessWarningDismissedForever,
            history.Values);

        var notificationVersion = settings.UpdateNotificationVersion?.Trim();
        var snoozedUntil = settings.UpdateNotificationSnoozedUntilUtc?.ToUniversalTime();
        if (!AppUpdateService.TryParseVersion(notificationVersion, out var parsedNotificationVersion))
        {
            notificationVersion = null;
            snoozedUntil = null;
        }
        else
        {
            notificationVersion = parsedNotificationVersion.ToString(3);
        }

        return settings with
        {
            PreviewAccessWarningLifetimeShows = lifetime.Shows,
            PreviewAccessWarningLastShownUtc = lifetime.LastShownUtc,
            PreviewAccessWarningDismissedForever = lifetime.DismissedForever,
            PreviewAccessWarnings = history,
            UpdateNotificationVersion = notificationVersion,
            UpdateNotificationSnoozedUntilUtc = snoozedUntil
        };
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(Pathname);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(Pathname, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Persisting preferences must not affect app behavior.
        }
    }
}
