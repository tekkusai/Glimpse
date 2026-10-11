using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
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

    private readonly AppUpdater _updates;
    private readonly Action<ThemePreference> _themePicked;
    private readonly Func<IReadOnlyList<MissingPiece>> _findMissingPieces;
    private readonly DispatcherTimer _copiedTimer;
    private IReadOnlyList<MissingPiece> _missingPieces = [];
    private IReadOnlyList<(string Path, DateTime Written)> _signatures = [];
    private Task _missingPiecesChecked = Task.CompletedTask;
    private bool _findingMissingPieces;
    private FooterLink _footerLink;
    private bool _closed;
    private bool _syncing;

    /// <param name="findMissingPieces">Runs off the UI thread; tests pass their own.</param>
    internal SettingsWindow(
        AppUpdater updates,
        Action<ThemePreference> themePicked,
        Func<IReadOnlyList<MissingPiece>>? findMissingPieces = null)
    {
        _updates = updates;
        _themePicked = themePicked;
        _findMissingPieces = findMissingPieces ?? MissingPieces.Find;
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
        AutoUpdateRow.MouseLeftButtonUp += (_, _) => AutoUpdateToggle.IsChecked = AutoUpdateToggle.IsChecked != true;
        AutoUpdateToggle.Checked += (_, _) => ApplyAutoUpdate(true);
        AutoUpdateToggle.Unchecked += (_, _) => ApplyAutoUpdate(false);

        ThemeSystem.Checked += (_, _) => PickTheme(ThemePreference.FollowSystem);
        ThemeLight.Checked += (_, _) => PickTheme(ThemePreference.Light);
        ThemeDark.Checked += (_, _) => PickTheme(ThemePreference.Dark);

        UpdateLink.Click += (_, _) => FollowUpdateLink();
        CheckLink.Click += (_, _) => _ = _updates.CheckNowAsync();
        _copiedTimer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal, (_, _) => EndCopiedFeedback(), Dispatcher);
        _copiedTimer.Stop();
        CopyDiagnosticsButton.Click += async (_, _) => await CopyDiagnosticsAsync();
        _updates.Changed += RefreshUpdateStatus;
        Closed += (_, _) =>
        {
            _closed = true;
            _copiedTimer.Stop();
            _updates.Changed -= RefreshUpdateStatus;
        };

        // The registry can change while the window sits open; returning to it
        // re-reads the world instead of trusting a snapshot.
        Activated += (_, _) => SyncAll();
        SizeChanged += (_, _) => KeepOnScreen();
        SyncAll();
    }

    /// <summary>The latest look for missing pieces; opening waits briefly on it.</summary>
    internal Task MissingPiecesChecked => _missingPiecesChecked;

    /// <summary>The update moved on, or its setting changed, while the window was open.</summary>
    private void RefreshUpdateStatus()
    {
        _syncing = true;
        try
        {
            AutoUpdateToggle.IsChecked = Settings.Current.InstallUpdatesAutomatically;
            SyncAbout();
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncAll()
    {
        _syncing = true;
        try
        {
            StartupToggle.IsChecked = StartupRegistration.IsInstalled();
            AutoUpdateToggle.IsChecked = Settings.Current.InstallUpdatesAutomatically;

            var theme = Settings.Current.Theme;
            ThemeSystem.IsChecked = theme == ThemePreference.FollowSystem;
            ThemeLight.IsChecked = theme == ThemePreference.Light;
            ThemeDark.IsChecked = theme == ThemePreference.Dark;

            SyncSignatures();
            SyncAbout();
        }
        finally
        {
            _syncing = false;
        }

        if (!_findingMissingPieces)
        {
            _missingPiecesChecked = FindMissingPiecesAsync();
        }
    }

    // A row that lands after the window is up grows it downward; the whole
    // dialog stays on its screen.
    private void KeepOnScreen()
    {
        if (!IsLoaded)
        {
            return;
        }

        var area = PreviewWindow.GetWorkAreaDips(new WindowInteropHelper(this).Handle);
        if (Top + ActualHeight > area.Bottom)
        {
            Top = Math.Max(area.Top, area.Bottom - ActualHeight);
        }
    }

    private void SyncAbout()
    {
        var current = _updates.CurrentVersion;
        var footer = UpdateFooter.Describe(_updates.State, Settings.Current, current, DateTimeOffset.Now, CultureInfo.CurrentCulture);
        VersionText.Text = $"{Branding.Name} {current.ToString(3)}";
        ShowText(UpdateStatusText, footer.Status);
        ShowText(UpdateCaption, footer.Caption);
        CheckLink.Visibility = footer.Link == FooterLink.CheckForUpdates ? Visibility.Visible : Visibility.Collapsed;
        _footerLink = footer.Link;
        var (link, name) = footer.Link switch
        {
            FooterLink.InstallNow => ("Install now", "Install update now"),
            FooterLink.WhatsNew => ("What's new", $"See what's new in {current.ToString(3)}"),
            _ => ((string?)null, (string?)null)
        };
        UpdateLink.Content = link;
        AutomationProperties.SetName(UpdateLink, name ?? string.Empty);
        UpdateLink.Visibility = link is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void ShowText(TextBlock block, string? text)
    {
        block.Text = text ?? string.Empty;
        block.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void FollowUpdateLink()
    {
        if (_footerLink == FooterLink.WhatsNew)
        {
            AppUpdater.OpenReleasePage(_updates.CurrentVersion);
        }
        else if (_footerLink == FooterLink.InstallNow)
        {
            _ = _updates.InstallNowAsync();
        }
    }

    private static string CurrentVersion() => AppUpdateService.DetectCurrentVersion().ToString(3);

    private async Task FindMissingPiecesAsync()
    {
        if (_findingMissingPieces)
        {
            return;
        }

        _findingMissingPieces = true;
        try
        {
            var pieces = await Task.Run(_findMissingPieces);
            if (!_closed)
            {
                ShowMissingPieces(pieces);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"missing pieces check failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _findingMissingPieces = false;
        }
    }

    private void ShowMissingPieces(IReadOnlyList<MissingPiece> pieces)
    {
        if (pieces.SequenceEqual(_missingPieces))
        {
            return;
        }

        _missingPieces = pieces;
        MissingPiecesList.Children.Clear();
        foreach (var piece in pieces)
        {
            MissingPiecesList.Children.Add(BuildMissingPieceRow(piece));
        }

        ShowSection(MissingPiecesSection, pieces.Count > 0);
    }

    private Grid BuildMissingPieceRow(MissingPiece piece)
    {
        var fix = PreviewWindow.BuildActionPill(piece.Fix);
        // House pills stay out of Tab in the preview, where keys browse; in
        // a dialog every action must be reachable.
        fix.Focusable = true;
        fix.Margin = new Thickness(16, 0, 0, 0);
        fix.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(fix, 1);

        var row = new Grid
        {
            Margin = new Thickness(0, 9, 0, 9),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        row.Children.Add(new TextBlock
        {
            Text = piece.Text,
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("RowLabel")
        });
        row.Children.Add(fix);
        return row;
    }

    /// <summary>
    /// Fill &amp; Sign's saved ink, newest first, then the stamp. Rebuilt
    /// only when a file comes, goes or changes, so a pending delete
    /// survives the window being activated again.
    /// </summary>
    private void SyncSignatures()
    {
        var paths = SignatureStore.ListSignatures().ToList();
        if (SignatureStore.HasStamp)
        {
            paths.Add(SignatureStore.StampPath);
        }

        var saved = paths.Select(path => (path, File.GetLastWriteTimeUtc(path))).ToList();
        if (saved.SequenceEqual(_signatures))
        {
            return;
        }

        _signatures = saved;
        var stamp = SignatureStore.HasStamp;
        SignaturesHeading.Text = (saved.Count - (stamp ? 1 : 0), stamp) switch
        {
            (0, true) => "Saved stamp",
            (_, true) => "Saved signatures and stamp",
            _ => "Saved signatures"
        };
        SignaturesList.Children.Clear();
        foreach (var (path, _) in saved)
        {
            SignaturesList.Children.Add(BuildSignatureRow(path));
        }

        ShowSection(SignaturesSection, saved.Count > 0);
    }

    /// <summary>
    /// One saved signature or the stamp, on white paper: ink is dark on
    /// transparent and must read in both themes. Deleting takes a second
    /// click on a named button, as in Fill &amp; Sign, because ink is work
    /// to redo.
    /// </summary>
    private Grid BuildSignatureRow(string path)
    {
        var what = string.Equals(path, SignatureStore.StampPath, StringComparison.OrdinalIgnoreCase) ? "stamp" : "signature";
        var ink = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(8, 4, 8, 4) };
        _ = LoadInkAsync(ink, path);
        var paper = new Border
        {
            Width = 160,
            Height = 48,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = Metrics.SurfaceCorner,
            Child = ink
        };
        paper.SetResourceReference(Border.BorderBrushProperty, "Theme.RaisedBorder");
        AutomationProperties.SetName(paper, $"Saved {what}");

        var trash = new Button
        {
            Style = (Style)FindResource("IconButton"),
            Focusable = true,
            Content = "\uE74D",
            ToolTip = $"Delete this {what}",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, -8, 0)
        };
        AutomationProperties.SetName(trash, $"Delete this {what}");

        var confirm = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        // Focus follows the choice for a keyboard user; a collapsed button
        // would keep it otherwise.
        void CloseConfirm()
        {
            var keyboard = confirm.IsKeyboardFocusWithin;
            confirm.Visibility = Visibility.Collapsed;
            trash.Visibility = Visibility.Visible;
            if (keyboard)
            {
                trash.Focus();
            }
        }

        var keep = HousePill.Build("Keep", CloseConfirm, PillRole.Ghost);
        var delete = HousePill.Build("Delete", () =>
        {
            SignatureStore.Remove(path);
            if (File.Exists(path))
            {
                // Locked or protected: the tile stays, and so must the truth.
                DiagnosticLog.Write($"saved {what} could not be deleted");
                CloseConfirm();
                return;
            }

            SyncSignatures();
        });
        keep.Focusable = true;
        delete.Focusable = true;
        delete.Margin = new Thickness(0);
        confirm.Children.Add(keep);
        confirm.Children.Add(delete);

        trash.Click += (_, _) =>
        {
            var keyboard = trash.IsKeyboardFocused;
            trash.Visibility = Visibility.Collapsed;
            confirm.Visibility = Visibility.Visible;
            if (keyboard)
            {
                keep.Focus();
            }
        };

        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(paper);
        row.Children.Add(trash);
        row.Children.Add(confirm);
        return row;
    }

    // The paper is there at once; the decode stays off the UI thread.
    private static async Task LoadInkAsync(Image ink, string path) =>
        ink.Source = await Task.Run(() => SignatureStore.TryLoad(path));

    /// <summary>
    /// A section that arrives once the window is up fades in; one present at
    /// open simply is there, and one that empties goes at once with its
    /// divider.
    /// </summary>
    private void ShowSection(FrameworkElement section, bool show)
    {
        if (!show)
        {
            section.Visibility = Visibility.Collapsed;
            return;
        }

        if (section.Visibility == Visibility.Visible)
        {
            return;
        }

        section.Visibility = Visibility.Visible;
        if (IsLoaded)
        {
            Motion.Settle(section, 0);
            Motion.Fade(section, 1, Motion.Reveal);
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

    private void ApplyAutoUpdate(bool automatic)
    {
        if (!_syncing)
        {
            _updates.SetInstallAutomatically(automatic);
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
}

internal enum FooterLink
{
    None,
    CheckForUpdates,
    InstallNow,
    WhatsNew
}

/// <summary>
/// What the Settings footer says about updates: one short status after the
/// version, one link, and a caption only when something needs explaining.
/// </summary>
internal sealed record UpdateFooter(string? Status, FooterLink Link, string? Caption)
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromDays(3);
    internal static readonly TimeSpan UpdatedNoteFor = TimeSpan.FromDays(7);

    /// <param name="now">Local time: "today" is the user's day.</param>
    public static UpdateFooter Describe(
        UpdateState state,
        Settings settings,
        Version current,
        DateTimeOffset now,
        CultureInfo culture)
    {
        if (state.Pending is { } pending)
        {
            var version = pending.VersionText;
            return state.Installing
                ? new(null, FooterLink.None, $"Installing {version}. Back in a few seconds.")
                : new(null, FooterLink.InstallNow, state.Downloading && settings.InstallUpdatesAutomatically
                    ? $"Downloading {version} in the background."
                    : AppUpdater.InstallsWhenAway(state, settings, now)
                        ? $"{version} installs the next time you step away."
                        : $"{version} is ready to install.");
        }

        var stale = settings.LastUpdateCheckUtc is { } last && now - last >= StaleAfter
            ? $"Couldn't check for updates since {ShortDate(last, now, culture)}."
            : null;
        // A skipped release that is no longer the latest needs no word.
        var caption = stale
            ?? (!state.UpToDate && AppUpdateService.TryParseVersion(settings.SkippedUpdateVersion, out var skipped) && skipped > current
                ? $"{skipped.ToString(3)} failed verification and was skipped."
                : null);
        if (settings.UpdatedAtUtc is { } updated && now - updated < UpdatedNoteFor)
        {
            return new(UpdatedOn(updated, now, culture), FooterLink.WhatsNew, caption);
        }

        if (state.Checking)
        {
            return new("Checking for updates", FooterLink.None, caption);
        }

        // Up to date only on the word of a check that reached GitHub.
        return new(state.UpToDate && stale is null ? "Up to date" : null, FooterLink.CheckForUpdates, caption);
    }

    private static string UpdatedOn(DateTimeOffset updated, DateTimeOffset now, CultureInfo culture) =>
        (now.Date - updated.ToOffset(now.Offset).Date).Days switch
        {
            0 => "Updated today",
            1 => "Updated yesterday",
            _ => $"Updated {ShortDate(updated, now, culture)}"
        };

    // The culture's month and day with the month abbreviated: Oct 7, 7 Oct.
    private static string ShortDate(DateTimeOffset when, DateTimeOffset now, CultureInfo culture) =>
        when.ToOffset(now.Offset).ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), culture);
}
