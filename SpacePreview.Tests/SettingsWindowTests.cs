using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Settings as the user meets it: the version line claims "Up to date" only
/// after a check reached GitHub, the update line says what will happen and
/// fits, the missing pieces say only what is certain, saved
/// ink goes on the second click, and the rows that were cut stay cut. The
/// collection runs alone because the ink test moves the data root.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsWindowTests
{
    private static readonly Guid H264Video = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid PngContainer = new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF");

    [Fact]
    public Task UpToDateIsClaimedOnlyAfterACheckReachedGitHub() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var nothingNewer = new ReleaseFixture { NoRelease = true };
        using var root = new TempDirectory();
        using var offline = ReleaseFixture.QuietUpdater();
        using var current = new AppUpdater(nothingNewer.Service(root.Path));
        var neverChecked = Open(offline);
        var checkedOk = Open(current);
        try
        {
            Assert.Equal($"{Branding.Name} 3.0.0", neverChecked.VersionText.Text);
            Assert.Equal(Visibility.Collapsed, neverChecked.UpdateStatusText.Visibility);
            Assert.Equal(Visibility.Visible, neverChecked.CheckLink.Visibility);
            Assert.Equal(Visibility.Collapsed, neverChecked.UpdateLink.Visibility);
            Assert.Equal(Visibility.Collapsed, neverChecked.UpdateCaption.Visibility);

            await offline.CheckNowAsync();
            Assert.Equal(Visibility.Collapsed, neverChecked.UpdateStatusText.Visibility);
            Assert.Equal("Couldn't check for updates.", neverChecked.UpdateCaption.Text);
            Assert.Equal(Visibility.Visible, neverChecked.CheckLink.Visibility);

            await current.CheckNowAsync();
            Assert.Equal(Visibility.Visible, checkedOk.UpdateStatusText.Visibility);
            Assert.Equal("Up to date", checkedOk.UpdateStatusText.Text);
            Assert.Equal(Visibility.Visible, checkedOk.CheckLink.Visibility);
        }
        finally
        {
            neverChecked.Close();
            checkedOk.Close();
        }
    });

    [Fact]
    public Task CheckForUpdatesAsksGitHubAndSaysSoWhileItRuns() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture { NoRelease = true, HoldCheck = new() };
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path));
        var settings = Open(updates);
        try
        {
            Click(settings.CheckLink);
            Assert.Equal("Checking for updates", settings.UpdateStatusText.Text);
            Assert.Equal(Visibility.Collapsed, settings.CheckLink.Visibility);

            release.HoldCheck.SetResult();
            await WaitUntilAsync(() => !updates.State.Checking);
            Assert.Equal("Up to date", settings.UpdateStatusText.Text);
            Assert.Equal(1, release.RequestsFor("/releases/latest"));
        }
        finally
        {
            settings.Close();
        }
    });

    [Fact]
    public Task APendingUpdateOffersInstallNowAndSaysWhenItWillInstall() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope();
        using var release = new ReleaseFixture();
        using var root = new TempDirectory();
        var metered = true;
        var launched = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ => launched++),
            isMetered: () => metered);
        await updates.CheckNowAsync();
        var settings = Open(updates);
        try
        {
            Assert.Equal($"{Branding.Name} 3.0.0", settings.VersionText.Text);
            Assert.Equal(Visibility.Collapsed, settings.UpdateStatusText.Visibility);
            Assert.Equal("Install now", settings.UpdateLink.Content);
            Assert.Equal("Install update now", AutomationProperties.GetName(settings.UpdateLink));

            // Metered, nothing is on disk yet: it installs only when asked.
            Assert.Equal("3.1.0 is ready to install.", settings.UpdateCaption.Text);

            metered = false;
            await updates.TickAsync();
            Assert.Equal("3.1.0 installs the next time you step away.", settings.UpdateCaption.Text);

            // Off, it waits for the user; the open window follows at once.
            settings.AutoUpdateToggle.IsChecked = false;
            Assert.False(Settings.Current.InstallUpdatesAutomatically);
            Assert.Equal("3.1.0 is ready to install.", settings.UpdateCaption.Text);

            Click(settings.UpdateLink);
            await WaitUntilAsync(() => launched == 1);
            Assert.Equal(1, launched);
            Assert.Equal("Installing 3.1.0. Back in a few seconds.", settings.UpdateCaption.Text);
            Assert.Equal(Visibility.Collapsed, settings.UpdateLink.Visibility);
        }
        finally
        {
            settings.Close();
        }
    });

    [Fact]
    public Task InstallNowSaysWhatItIsDoingAndWhenItFailed() => WpfApp.RunAsync(async () =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { InstallUpdatesAutomatically = false });
        using var release = new ReleaseFixture { HoldInstaller = new() };
        using var root = new TempDirectory();
        var launched = 0;
        using var updates = new AppUpdater(
            release.Service(root.Path, start: _ =>
            {
                // Windows refuses setup the first time.
                if (++launched == 1)
                {
                    throw new InvalidOperationException("Windows did not start the installer.");
                }
            }),
            isMetered: () => false);
        await updates.CheckNowAsync();
        var settings = Open(updates);
        try
        {
            Click(settings.UpdateLink);
            Assert.Equal("Downloading 3.1.0.", settings.UpdateCaption.Text);
            Assert.Equal(Visibility.Collapsed, settings.UpdateLink.Visibility);
            release.HoldInstaller.SetResult();
            await WaitUntilAsync(() => launched == 1 && !updates.State.Installing);
            Assert.Equal("Couldn't install 3.1.0.", settings.UpdateCaption.Text);
            Assert.Equal("Install now", settings.UpdateLink.Content);
            Assert.Equal(Visibility.Visible, settings.UpdateLink.Visibility);

            Click(settings.UpdateLink);
            await WaitUntilAsync(() => launched == 2);
            Assert.Equal("Installing 3.1.0. Back in a few seconds.", settings.UpdateCaption.Text);
        }
        finally
        {
            settings.Close();
        }
    });

    [Fact]
    public Task AFreshUpdateNamesItselfAndLinksItsReleaseNotes() => WpfApp.RunAsync(() =>
    {
        using var scope = new UpdateSettingsScope(settings => settings with { UpdatedAtUtc = DateTimeOffset.Now });
        var settings = Open();
        try
        {
            Assert.Equal("Updated today", settings.UpdateStatusText.Text);
            Assert.Equal("What's new", settings.UpdateLink.Content);
            Assert.Equal("See what's new in 3.0.0", AutomationProperties.GetName(settings.UpdateLink));
            Assert.Equal(Visibility.Collapsed, settings.CheckLink.Visibility);
        }
        finally
        {
            settings.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task CopyDiagnosticsNamesTheVersionTheFooterShows() => WpfApp.RunAsync(async () =>
    {
        using var release = new ReleaseFixture { Offline = true };
        using var root = new TempDirectory();
        using var updates = new AppUpdater(release.Service(root.Path, currentVersion: "2.9.4"));
        var settings = Open(updates);
        try
        {
            Assert.Equal($"{Branding.Name} 2.9.4", settings.VersionText.Text);
            Assert.Contains($"Version: 2.9.4{Environment.NewLine}", await settings.DiagnosticsAsync(), StringComparison.Ordinal);
        }
        finally
        {
            settings.Close();
        }
    });

    [Fact]
    public Task TheAutomaticInstallToggleStartsOnAndSavesTheChoice() => WpfApp.RunAsync(() =>
    {
        using var scope = new UpdateSettingsScope();
        var settings = Open();
        try
        {
            Assert.True(settings.AutoUpdateToggle.IsChecked);
            Assert.Equal("Install updates automatically", AutomationProperties.GetName(settings.AutoUpdateToggle));

            settings.AutoUpdateRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            Assert.False(Settings.Current.InstallUpdatesAutomatically);
        }
        finally
        {
            settings.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task EveryUpdateStatusFitsBesideCopyDiagnostics() => WpfApp.RunAsync(async () =>
    {
        // Each footer at its natural width, against the window's content width.
        using var scope = new UpdateSettingsScope(settings => settings with { SkippedUpdateVersion = "10.10.10" });
        using var nothingNewer = new ReleaseFixture { NoRelease = true, HoldCheck = new() };
        using var newer = new ReleaseFixture();
        using var root = new TempDirectory();
        using var checking = new AppUpdater(nothingNewer.Service(root.Path));
        using var pending = new AppUpdater(newer.Service(root.Path), isMetered: () => true);
        var footers = new List<(string Words, double Width)>();
        void Measure(SettingsWindow settings)
        {
            settings.Footer.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var words = new object?[]
            {
                settings.UpdateStatusText.Text,
                settings.UpdateLink.Visibility == Visibility.Visible ? settings.UpdateLink.Content : null,
                settings.CheckLink.Visibility == Visibility.Visible ? settings.CheckLink.Content : null,
                settings.UpdateCaption.Text
            };
            footers.Add((string.Join(" | ", words.Where(word => word is not (null or ""))), settings.Footer.DesiredSize.Width));
        }

        var windows = new List<SettingsWindow>();
        SettingsWindow Open2(AppUpdater? updates = null)
        {
            var window = Open(updates);
            windows.Add(window);
            return window;
        }

        try
        {
            var first = Open2(checking);
            Measure(first);
            _ = checking.CheckNowAsync();
            Measure(first);
            nothingNewer.HoldCheck.SetResult();
            await WaitUntilAsync(() => checking.State.UpToDate);
            Measure(first);

            await pending.CheckNowAsync();
            Measure(Open2(pending));

            Settings.Update(settings => settings with { UpdatedAtUtc = DateTimeOffset.Now.AddDays(-1) });
            Measure(Open2());

            Settings.Update(settings => settings with { UpdatedAtUtc = null, LastUpdateCheckUtc = DateTimeOffset.Now.AddDays(-30) });
            Measure(Open2());

            var content = first.Width - 40;
            Assert.All(footers, footer => Assert.True(footer.Width <= content, $"\"{footer.Words}\" needs {footer.Width:0} of {content:0} px"));
            Assert.Equal(6, footers.Count);
            Assert.Contains(footers, footer => footer.Words.StartsWith("Up to date | Check for updates", StringComparison.Ordinal));
            Assert.Contains(footers, footer => footer.Words.StartsWith("Updated yesterday | What's new", StringComparison.Ordinal));
            Assert.Contains(footers, footer => footer.Words.StartsWith("Couldn't check for updates since", StringComparison.Ordinal) || footer.Words.Contains("| Couldn't check", StringComparison.Ordinal));
        }
        finally
        {
            windows.ForEach(window => window.Close());
        }
    });

    [Fact]
    public Task MissingPiecesArriveFromAWorkerAndAreAbsentOnACompletePc() => WpfApp.RunAsync(async () =>
    {
        var uiThread = Environment.CurrentManagedThreadId;
        var askedOn = uiThread;
        var lacking = Open(findMissingPieces: () =>
        {
            askedOn = Environment.CurrentManagedThreadId;
            return MissingPieces.Describe(officeEngine: false, hevcDecoder: true, heifDecoder: true);
        });
        var completeAsked = false;
        var complete = Open(findMissingPieces: () =>
        {
            completeAsked = true;
            return [];
        });
        try
        {
            await WaitUntilAsync(() => lacking.MissingPiecesSection.Visibility == Visibility.Visible && completeAsked);
            await Task.Delay(100);

            Assert.NotEqual(uiThread, askedOn);
            var row = Assert.Single(lacking.MissingPiecesList.Children.OfType<Grid>());
            Assert.Equal("Office files open as basic previews.", row.Children.OfType<TextBlock>().Single().Text);
            Assert.Equal("Get LibreOffice", row.Children.OfType<ButtonBase>().Single().Content);
            Assert.Equal(2, VisibleDividers(lacking));

            Assert.Equal(Visibility.Collapsed, complete.MissingPiecesSection.Visibility);
            Assert.Equal(1, VisibleDividers(complete));
        }
        finally
        {
            lacking.Close();
            complete.Close();
        }
    });

    [Fact]
    public void MissingPiecesClaimOnlyWhatIsCertain()
    {
        Assert.Empty(MissingPieces.Describe(officeEngine: true, hevcDecoder: true, heifDecoder: true));

        // A check that could not answer claims nothing.
        Assert.Empty(MissingPieces.Describe(officeEngine: true, hevcDecoder: null, heifDecoder: null));

        var office = Assert.Single(MissingPieces.Describe(officeEngine: false, hevcDecoder: true, heifDecoder: true));
        Assert.Same(PreviewWindow.LibreOfficeFix, office.Fix);
        Assert.Equal("https://www.libreoffice.org/download/", office.Fix.Uri);

        var all = MissingPieces.Describe(officeEngine: false, hevcDecoder: false, heifDecoder: false);
        Assert.Equal([PreviewWindow.LibreOfficeFix, PreviewWindow.HevcFix, PreviewWindow.HeifFix], all.Select(piece => piece.Fix));
        Assert.All(all, piece => Assert.DoesNotContain('—', piece.Text));

        // HEIC needs both decoders; with both missing it is named once.
        Assert.Single(all, piece => piece.Text.Contains("HEIC", StringComparison.Ordinal));
        var heif = Assert.Single(MissingPieces.Describe(officeEngine: true, hevcDecoder: true, heifDecoder: false));
        Assert.Equal("HEIC and HEIF photos won't open.", heif.Text);
    }

    [Fact]
    public Task EveryActionInTheDialogIsReachableFromTheKeyboard() => WpfApp.RunAsync(async () =>
    {
        _ = Settings.Current;
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        SettingsWindow? settings = null;
        try
        {
            File.WriteAllBytes(Path.Combine(temp.CreateDirectory("signatures"), "sig-20261010-120000-000.png"), InkPng());
            settings = Open(findMissingPieces: () => MissingPieces.Describe(officeEngine: false, hevcDecoder: true, heifDecoder: true));
            await WaitUntilAsync(() => settings.MissingPiecesSection.Visibility == Visibility.Visible);

            var fix = settings.MissingPiecesList.Children.OfType<Grid>().Single().Children.OfType<ButtonBase>().Single();
            var (trash, keep, delete) = Controls(settings, 0);
            Assert.All(new ButtonBase[] { fix, trash, keep, delete }, button => Assert.True(button.Focusable, $"{button.Content} is out of the Tab order"));
        }
        finally
        {
            settings?.Close();
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    });

    [Fact]
    public Task ADeleteThatFailsPutsTheTrashBackAndKeepsTheTile() => WpfApp.RunAsync(async () =>
    {
        _ = Settings.Current;
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        SettingsWindow? settings = null;
        try
        {
            var signature = Path.Combine(temp.CreateDirectory("signatures"), "sig-20261010-120000-000.png");
            File.WriteAllBytes(signature, InkPng());
            settings = Open();
            await InkLoadedAsync(settings);
            var (trash, _, delete) = Controls(settings, 0);

            using (new FileStream(signature, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Click(trash);
                Click(delete);
            }

            Assert.True(File.Exists(signature));
            Assert.Single(settings.SignaturesList.Children);
            Assert.Equal(Visibility.Visible, trash.Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)VisualParent(delete)).Visibility);
        }
        finally
        {
            settings?.Close();
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    });

    [Fact]
    public Task TheHeadingNamesWhatIsSaved() => WpfApp.RunAsync(async () =>
    {
        _ = Settings.Current;
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        var opened = new List<SettingsWindow>();
        async Task<string> Heading()
        {
            var settings = Open();
            opened.Add(settings);
            await InkLoadedAsync(settings);
            return settings.SignaturesHeading.Text;
        }

        try
        {
            File.WriteAllBytes(SignatureStore.StampPath, InkPng());
            Assert.Equal("Saved stamp", await Heading());

            File.WriteAllBytes(Path.Combine(temp.CreateDirectory("signatures"), "sig-20261010-120000-000.png"), InkPng());
            Assert.Equal("Saved signatures and stamp", await Heading());

            File.Delete(SignatureStore.StampPath);
            Assert.Equal("Saved signatures", await Heading());
        }
        finally
        {
            opened.ForEach(settings => settings.Close());
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    });

    [Fact]
    public void TheCodecChecksTellAnInstalledDecoderFromAMissingOne()
    {
        Assert.True(MediaCodecs.HasImageDecoder(PngContainer));
        Assert.False(MediaCodecs.HasImageDecoder(Guid.NewGuid()));
        Assert.NotNull(MediaCodecs.HasImageDecoder(MediaCodecs.HeifContainer));

        // Windows N without its media pack has no Media Foundation: every
        // video question is rightly unanswerable there.
        if (MediaCodecs.HasVideoDecoder(H264Video) is null)
        {
            return;
        }

        Assert.True(MediaCodecs.HasVideoDecoder(H264Video));
        Assert.False(MediaCodecs.HasVideoDecoder(Guid.NewGuid()));
        Assert.NotNull(MediaCodecs.HasVideoDecoder(MediaCodecs.HevcVideo));
    }

    [Fact]
    public Task SavedInkGoesOnlyOnTheSecondClickAndTheRowGoesWithTheLast() => WpfApp.RunAsync(async () =>
    {
        // Settings fixes its file at first use: let that be the run's root,
        // never this temp one.
        _ = Settings.Current;
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        SettingsWindow? settings = null;
        try
        {
            var signature = Path.Combine(temp.CreateDirectory("signatures"), "sig-20261010-120000-000.png");
            File.WriteAllBytes(signature, InkPng());
            File.WriteAllBytes(SignatureStore.StampPath, InkPng());
            settings = Open();

            Assert.Equal(Visibility.Visible, settings.SignaturesSection.Visibility);
            Assert.Equal(2, settings.SignaturesList.Children.Count);
            Assert.Equal(2, VisibleDividers(settings));

            // Dark ink on transparent reads on white paper in either theme.
            var papers = settings.SignaturesList.Children.OfType<Grid>().Select(row => row.Children.OfType<Border>().Single()).ToList();
            Assert.All(papers, paper => Assert.Equal(Colors.White, ((SolidColorBrush)paper.Background).Color));
            await WaitUntilAsync(() => papers.All(paper => ((Image)paper.Child).Source is not null));
            Assert.All(papers, paper => Assert.NotNull(((Image)paper.Child).Source));

            var (trash, keep, delete) = Controls(settings, 0);
            Assert.Equal("Delete this signature", trash.ToolTip);
            Click(trash);
            Assert.True(File.Exists(signature));
            Click(keep);
            Assert.True(File.Exists(signature));
            Assert.Equal(Visibility.Visible, trash.Visibility);

            Click(trash);
            Click(delete);
            Assert.False(File.Exists(signature));
            Assert.Single(settings.SignaturesList.Children);

            // The rows were rebuilt, and the stamp is being read again.
            await InkLoadedAsync(settings);
            (trash, _, delete) = Controls(settings, 0);
            Assert.Equal("Delete this stamp", trash.ToolTip);
            Click(trash);
            Click(delete);
            Assert.False(SignatureStore.HasStamp);
            Assert.Equal(Visibility.Collapsed, settings.SignaturesSection.Visibility);
            Assert.Equal(1, VisibleDividers(settings));
        }
        finally
        {
            settings?.Close();
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    });

    [Fact]
    public Task TheCutRowsStayCut() => WpfApp.RunAsync(() =>
    {
        var settings = Open();
        try
        {
            var words = Words(settings).ToList();
            Assert.Contains("Copy diagnostics", words);
            Assert.DoesNotContain("Pinned previews stay on top", words);
            Assert.DoesNotContain("Open log", words);
            Assert.DoesNotContain(words, word => word.StartsWith("Documents", StringComparison.Ordinal));
        }
        finally
        {
            settings.Close();
        }

        return Task.CompletedTask;
    });

    private static SettingsWindow Open(
        AppUpdater? updates = null,
        Func<IReadOnlyList<MissingPiece>>? findMissingPieces = null) =>
        new(updates ?? ReleaseFixture.QuietUpdater(), _ => { }, findMissingPieces ?? (() => []));

    private static (Button Trash, ButtonBase Keep, ButtonBase Delete) Controls(SettingsWindow settings, int row)
    {
        var grid = (Grid)settings.SignaturesList.Children[row];
        var confirm = grid.Children.OfType<StackPanel>().Single();
        return (grid.Children.OfType<Button>().Single(), (ButtonBase)confirm.Children[0], (ButtonBase)confirm.Children[1]);
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static DependencyObject VisualParent(DependencyObject child) => LogicalTreeHelper.GetParent(child);

    // The ink decodes on a worker; a test that locks or deletes a file waits for it.
    private static Task InkLoadedAsync(SettingsWindow settings) =>
        WaitUntilAsync(() => settings.SignaturesList.Children.OfType<Grid>()
            .All(row => ((Image)row.Children.OfType<Border>().Single().Child).Source is not null));

    /// <summary>
    /// Dividers the user would see: visible themselves and inside visible
    /// sections. The window is never shown, so its own visibility is not asked.
    /// </summary>
    private static int VisibleDividers(SettingsWindow settings) =>
        Descendants(settings)
            .OfType<Border>()
            .Count(border => border.Height == 1 && Shown(border));

    private static bool Shown(DependencyObject element)
    {
        for (var current = element; current is not null and not Window; current = LogicalTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<string> Words(SettingsWindow settings)
    {
        foreach (var element in Descendants(settings))
        {
            if (element is TextBlock { Text: { Length: > 0 } text })
            {
                yield return text;
            }
            else if (element is ContentControl { Content: string content })
            {
                yield return content;
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static byte[] InkPng()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < 16 * 2; i++)
        {
            pixels[i * 4 + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
