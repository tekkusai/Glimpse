using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// The one settings surface: a single column on the preview's own frame.
/// The tray menu and a second launch are doors to here, not settings trees.
/// </summary>
public partial class SettingsWindow : Window
{
    internal const int DiagnosticLogLines = 200;

    private readonly Func<AppUpdateRelease?> _availableUpdate;
    private readonly Action _showUpdate;
    private readonly Action<ThemePreference> _themePicked;
    private readonly DispatcherTimer _copiedTimer;
    private bool _syncing;

    internal SettingsWindow(
        Func<AppUpdateRelease?> availableUpdate,
        Action showUpdate,
        Action<ThemePreference> themePicked)
    {
        _availableUpdate = availableUpdate;
        _showUpdate = showUpdate;
        _themePicked = themePicked;
        InitializeComponent();
        Title = $"{Branding.Name} settings";
        WindowFrame.AttachDialog(this, TitleBar);

        CloseButton.Click += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        StartupRow.MouseLeftButtonUp += (_, _) => StartupToggle.IsChecked = StartupToggle.IsChecked != true;
        StartupToggle.Checked += (_, _) => ApplyStartup(true);
        StartupToggle.Unchecked += (_, _) => ApplyStartup(false);

        ThemeSystem.Checked += (_, _) => PickTheme(ThemePreference.FollowSystem);
        ThemeLight.Checked += (_, _) => PickTheme(ThemePreference.Light);
        ThemeDark.Checked += (_, _) => PickTheme(ThemePreference.Dark);

        PinDefaultRow.MouseLeftButtonUp += (_, _) => PinDefaultToggle.IsChecked = PinDefaultToggle.IsChecked != true;
        PinDefaultToggle.Checked += (_, _) => ApplyPinDefault(true);
        PinDefaultToggle.Unchecked += (_, _) => ApplyPinDefault(false);

        UpdateLink.Click += (_, _) => _showUpdate();
        _copiedTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) => EndCopiedFeedback(), Dispatcher);
        _copiedTimer.Stop();
        CopyDiagnosticsButton.Click += async (_, _) => await CopyDiagnosticsAsync();
        OpenLogButton.Click += (_, _) => OpenLog();
        Closed += (_, _) => _copiedTimer.Stop();

        // The registry can change while the window sits open; returning to it
        // re-reads the world instead of trusting a snapshot.
        Activated += (_, _) => SyncAll();
        SyncAll();
    }

    private void SyncAll()
    {
        _syncing = true;
        try
        {
            StartupToggle.IsChecked = StartupRegistration.IsInstalled();

            var theme = Settings.Current.Theme;
            ThemeSystem.IsChecked = theme == ThemePreference.FollowSystem;
            ThemeLight.IsChecked = theme == ThemePreference.Light;
            ThemeDark.IsChecked = theme == ThemePreference.Dark;

            PinDefaultToggle.IsChecked = Settings.Current.PinnedAlwaysOnTopDefault;
            SyncAbout();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncAbout()
    {
        DocumentsText.Text = $"Documents: {(OfficeDocumentPdfConverter.IsAvailable ? OfficeDocumentPdfConverter.EngineName : "basic previews")}";
        VersionText.Text = $"{Branding.Name} {CurrentVersion()}";
        if (_availableUpdate() is { } update)
        {
            UpdateLink.Content = $"Update to {update.VersionText}";
            UpdateLink.Visibility = Visibility.Visible;
        }
        else
        {
            UpdateLink.Visibility = Visibility.Collapsed;
        }
    }

    private static string CurrentVersion() => AppUpdateService.DetectCurrentVersion().ToString(3);

    private void ApplyStartup(bool enabled)
    {
        if (_syncing)
        {
            return;
        }

        if (enabled)
        {
            StartupRegistration.Install();
        }
        else
        {
            StartupRegistration.Uninstall();
        }
    }

    // The palette reaches every open window through the app's resources.
    private void PickTheme(ThemePreference preference)
    {
        if (!_syncing)
        {
            _themePicked(preference);
        }
    }

    private void ApplyPinDefault(bool onTop)
    {
        if (_syncing)
        {
            return;
        }

        Settings.Update(settings => settings with { PinnedAlwaysOnTopDefault = onTop });
    }

    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            var lines = await Task.Run(() => ReadLogTail(DiagnosticLog.Pathname, DiagnosticLogLines));
            var engine = OfficeDocumentPdfConverter.EngineName ?? "Basic previews";
            Clipboard.SetText(BuildDiagnostics(CurrentVersion(), engine, lines));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"copy diagnostics failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        // Holding the width keeps the neighbouring link where it was.
        CopyDiagnosticsButton.MinWidth = CopyDiagnosticsButton.ActualWidth;
        CopyDiagnosticsButton.Content = "Copied";
        _copiedTimer.Stop();
        _copiedTimer.Start();
    }

    private void EndCopiedFeedback()
    {
        _copiedTimer.Stop();
        CopyDiagnosticsButton.Content = "Copy diagnostics";
    }

    /// <summary>
    /// What a bug report needs: version, document engine, Windows and the
    /// recent log. Paths are redacted again here, so a line written by an
    /// older build cannot leak a file name.
    /// </summary>
    internal static string BuildDiagnostics(string version, string engine, IEnumerable<string> logLines)
    {
        var text = new StringBuilder()
            .AppendLine($"{Branding.Name} diagnostics")
            .AppendLine($"Version: {version}")
            .AppendLine($"Documents: {engine}")
            .AppendLine($"Windows: {Environment.OSVersion.Version}")
            .AppendLine()
            .AppendLine("Recent log:");
        foreach (var line in logLines)
        {
            text.AppendLine(DiagnosticLog.Redact(line));
        }

        return text.ToString();
    }

    /// <summary>The last lines of the log, read beside its live writer.</summary>
    internal static IReadOnlyList<string> ReadLogTail(string path, int count)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var tail = new Queue<string>(count);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (tail.Count == count)
            {
                tail.Dequeue();
            }

            tail.Enqueue(line);
        }

        return [.. tail];
    }

    private static void OpenLog()
    {
        try
        {
            var directory = Path.GetDirectoryName(DiagnosticLog.Pathname);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(DiagnosticLog.Pathname))
            {
                File.WriteAllText(DiagnosticLog.Pathname, string.Empty);
            }

            Process.Start(new ProcessStartInfo(DiagnosticLog.Pathname) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open log failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
