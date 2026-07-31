using System.Windows;
using System.Windows.Input;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Services;

/// <summary>
/// The one settings surface: a single column over the shared dialog chrome.
/// The tray menu is a door to here, not a settings tree.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly Func<AppUpdateRelease?> _availableUpdate;
    private readonly Action _showUpdate;
    private readonly Action<ThemePreference> _themePicked;
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
        Theme.ApplyResources(this);

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

        UpdateLink.MouseLeftButtonUp += (_, _) => _showUpdate();
        CopyDiagnosticsButton.Click += (_, _) => TrayIcon.CopyDiagnostics();
        OpenLogButton.Click += (_, _) => TrayIcon.OpenLog();

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
        DocumentsText.Text = OfficeDocumentPdfConverter.IsAvailable
            ? $"Documents: {OfficeDocumentPdfConverter.EngineName}"
            : "Documents: basic previews";

        if (_availableUpdate() is { } update)
        {
            VersionText.Visibility = Visibility.Collapsed;
            UpdateLink.Text = $"Update to {update.VersionText}";
            UpdateLink.Visibility = Visibility.Visible;
        }
        else
        {
            VersionText.Text = $"Glimpse {AppUpdateService.DetectCurrentVersion().ToString(3)}";
            VersionText.Visibility = Visibility.Visible;
            UpdateLink.Visibility = Visibility.Collapsed;
        }
    }

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

    private void PickTheme(ThemePreference preference)
    {
        if (_syncing)
        {
            return;
        }

        _themePicked(preference);

        // The palette just changed under this window; repaint it live.
        Theme.ApplyResources(this);
    }

    private void ApplyPinDefault(bool onTop)
    {
        if (_syncing)
        {
            return;
        }

        Settings.Update(settings => settings with { PinnedAlwaysOnTopDefault = onTop });
    }
}
