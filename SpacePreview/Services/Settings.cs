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
    /// Whether a newly pinned preview starts above other windows: wherever
    /// the user last left Always on top on a pinned preview.
    /// </summary>
    public bool PinnedAlwaysOnTopDefault { get; init; } = true;

    /// <summary>
    /// The access notice's lifetime budget (see PreviewAccessWarningPolicy).
    /// Unversioned on purpose: an update can never refill it.
    /// </summary>
    public int PreviewAccessWarningLifetimeShows { get; init; }

    public DateTimeOffset? PreviewAccessWarningLastShownUtc { get; init; }

    public bool PreviewAccessWarningDismissedForever { get; init; }

    /// <summary>
    /// Off: checks still run, and nothing downloads or installs until the
    /// user presses Install now in Settings.
    /// </summary>
    public bool InstallUpdatesAutomatically { get; init; } = true;

    /// <summary>
    /// The last time Glimpse knew it was current: a check that reached
    /// GitHub, or the first launch. Settings speaks up once it is 3 days old.
    /// </summary>
    public DateTimeOffset? LastUpdateCheckUtc { get; init; }

    /// <summary>The last update setup started, and when.</summary>
    public string? UpdateAttemptVersion { get; init; }

    public DateTimeOffset? UpdateAttemptUtc { get; init; }

    /// <summary>The version that last ran. A newer one running means an update landed.</summary>
    public string? LastVersionSeen { get; init; }

    /// <summary>When the running version first ran as an update.</summary>
    public DateTimeOffset? UpdatedAtUtc { get; init; }

    /// <summary>A new major version landed and its one tray notice has not shown yet.</summary>
    public bool MajorUpdateNoticeDue { get; init; }

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
    public static Settings Current => _current ??= Load(Pathname);

    /// <summary>Applies a change to <see cref="Current"/> and persists it.</summary>
    public static void Update(Func<Settings, Settings> change)
    {
        _current = change(Current);
        _current.Save(Pathname);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string Pathname { get; } = AppPaths.InDataRoot("settings.json");

    /// <summary>
    /// Reads the file at <paramref name="path"/>. A file that cannot be read
    /// as settings is moved aside to settings.json.bad and named in the log,
    /// so a torn write costs the user their preferences once, visibly, and
    /// the evidence survives the next save.
    /// </summary>
    internal static Settings Load(string path)
    {
        string json;
        try
        {
            if (!File.Exists(path))
            {
                return new Settings();
            }

            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"settings unreadable, using defaults: {ex.GetType().Name}: {ex.Message}");
            return new Settings();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<Settings>(json, Options) ?? throw new JsonException("null settings");

            // 2.x kept the names of elevated apps the user opened file
            // dialogs in. Nothing reads them any more; the first load
            // rewrites the file without them.
            if (json.Contains("\"PreviewAccessWarnings\"", StringComparison.Ordinal))
            {
                loaded.Save(path);
            }

            return loaded;
        }
        catch (Exception ex)
        {
            var aside = path + ".bad";
            try
            {
                File.Move(path, aside, overwrite: true);
            }
            catch
            {
                // Kept in place, the next save overwrites it; the log line stays.
            }

            DiagnosticLog.Write($"settings corrupt, kept as {Path.GetFileName(aside)} and reset: {ex.GetType().Name}: {ex.Message}");
            return new Settings();
        }
    }

    /// <summary>
    /// Writes beside the file, then moves over it: a crash or a forced quit
    /// mid-write leaves the previous settings, never half of the new ones.
    /// </summary>
    internal void Save(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Options));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // Persisting preferences must not affect app behavior.
            DiagnosticLog.Write($"settings save failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
