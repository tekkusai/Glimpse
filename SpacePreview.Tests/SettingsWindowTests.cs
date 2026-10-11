using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Settings as the user meets it: the version line claims "Up to date" only
/// after a real check, the missing pieces say only what is certain, saved
/// ink goes on the second click, and the rows that were cut stay cut. The
/// collection runs alone because the ink test moves the data root.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SettingsWindowTests
{
    private static readonly Guid H264Video = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid PngContainer = new("1B7CFAF4-713F-473C-BBCD-6137425FAEAF");

    [Fact]
    public Task UpToDateIsClaimedOnlyAfterACheckFoundNothingNewer() => WpfApp.RunAsync(() =>
    {
        var release = new AppUpdateRelease(
            new Version(9, 1, 0),
            "v9.1.0",
            new Uri("https://example.invalid/setup.exe"),
            1,
            string.Empty,
            new Uri("https://example.invalid/setup.exe.sha256"));
        var neverChecked = Open(update: null, updateChecked: false);
        var current = Open(update: null, updateChecked: true);
        var behind = Open(update: release, updateChecked: true);
        try
        {
            Assert.Equal($"{Branding.Name} {AppUpdateService.DetectCurrentVersion().ToString(3)}", neverChecked.VersionText.Text);
            Assert.Equal(Visibility.Collapsed, neverChecked.UpToDateText.Visibility);
            Assert.Equal(Visibility.Collapsed, neverChecked.UpdateLink.Visibility);

            Assert.Equal(Visibility.Visible, current.UpToDateText.Visibility);
            Assert.Equal("Up to date", current.UpToDateText.Text);

            Assert.Equal(Visibility.Collapsed, behind.UpToDateText.Visibility);
            Assert.Equal(Visibility.Visible, behind.UpdateLink.Visibility);
            Assert.Equal("Update to 9.1.0", behind.UpdateLink.Content);
        }
        finally
        {
            neverChecked.Close();
            current.Close();
            behind.Close();
        }

        return Task.CompletedTask;
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
        AppUpdateRelease? update = null,
        bool updateChecked = false,
        Func<IReadOnlyList<MissingPiece>>? findMissingPieces = null) =>
        new(() => update, () => updateChecked, () => { }, _ => { }, findMissingPieces ?? (() => []));

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
