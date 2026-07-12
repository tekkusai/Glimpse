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
    /// Per-app delivery history for inaccessible file-picker warnings. Entries
    /// are versioned so a future warning redesign can intentionally start a new
    /// reminder cycle without disturbing unrelated preferences.
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
                return Normalize(
                    JsonSerializer.Deserialize<Settings>(json, Options) ?? new Settings());
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
