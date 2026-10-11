using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// Unsaved signing work survives Glimpse ending without the user choosing:
/// it comes back on its pages while the file is the one it was placed on,
/// never on a changed file, and only with the user's own ink as it was.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class SignDraftTests
{
    private static readonly IReadOnlyList<IReadOnlyList<Point>> Strokes =
        [new List<Point> { new(0, 0), new(60, 20), new(120, 0) }];

    // ------------------------------------------------------------------
    // The draft file.
    // ------------------------------------------------------------------

    [Fact]
    public void WorkComesBackOnItsPagesWhileTheFileIsUnchanged() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var png = SignatureStore.TryReadForPlacement(signature, out var source)!;
        SignPlacement[] placements =
        [
            new(SignPlacementKind.Date, 2, 0.4, 0.2, 0.14, 0.028, "10/10/2026"),
            new(SignPlacementKind.Signature, 0, 0.3, 0.7, 0.24, 0.05, null, png, source)
        ];

        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 2, placements);
        var (file, draft) = SignDrafts.Load(pdf);

        Assert.Equal(FileStamp.Of(pdf), file);
        Assert.NotNull(draft);
        Assert.Equal(2, draft!.PageIndex);
        Assert.Equal(placements.Select(Shape), draft.Placements.Select(Shape));
        Assert.Equal(png, draft.Placements[1].ImagePng);
    });

    [Fact]
    public void ADraftIsNeverAppliedToAChangedFile() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        WriteDateDraft(pdf);
        File.AppendAllText(pdf, " ");

        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));

        // The same length, saved again: a new version all the same.
        WriteDateDraft(pdf);
        File.SetLastWriteTimeUtc(pdf, File.GetLastWriteTimeUtc(pdf).AddSeconds(2));

        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void PlacementsWhoseInkIsGoneAreLetGo() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 0));
        var signaturePng = SignatureStore.TryReadForPlacement(signature, out var signatureSource)!;
        var stampPng = SignatureStore.TryReadForPlacement(SignatureStore.StampPath, out var stampSource)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.05, null, signaturePng, signatureSource),
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.5, 0.5, 0.18, 0.09, null, stampPng, stampSource)
        ]);

        // The user removed the signature: its placement goes, the stamp stays.
        SignatureStore.Remove(signature);
        Assert.Equal([SignPlacementKind.Stamp], SignDrafts.Load(pdf).Draft!.Placements.Select(p => p.Kind));

        // A new stamp under the same name is not the one that was placed.
        File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 255));
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void InkRedrawnOnThePadComesBackAsDrawn() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        SignatureStore.TryReadForPlacement(signature, out var source);
        var redrawn = SolidPng(30, 10, 0);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.08, null, redrawn, source! with { Redrawn = true })
        ]);

        var placed = SignDrafts.Load(pdf).Draft!.Placements.Single();
        Assert.Equal(redrawn, placed.ImagePng);
        Assert.True(placed.Source!.Redrawn);

        // It was drawn over that signature; removing the signature takes it too.
        SignatureStore.Remove(signature);
        Assert.Null(SignDrafts.Load(pdf).Draft);
    });

    [Fact]
    public void ADraftUntouchedFor30DaysIsAbandoned() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        WriteDateDraft(pdf);
        File.SetLastWriteTimeUtc(SignDrafts.PathFor(pdf), DateTime.UtcNow.AddDays(-29));
        Assert.NotNull(SignDrafts.Load(pdf).Draft);

        File.SetLastWriteTimeUtc(SignDrafts.PathFor(pdf), DateTime.UtcNow.AddDays(-31));
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void ADraftOnlyEverPointsIntoTheDataRoot() => InDataRoot(root =>
    {
        using var elsewhere = new TempDirectory();
        var pdf = WritePdf(root);
        var draftPath = SignDrafts.PathFor(pdf);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var outside = Path.Combine(elsewhere.Path, "someone-else.png");
        File.Copy(signature, outside);

        // Ink from outside the data root is never referenced.
        var png = SignatureStore.TryReadForPlacement(outside, out var foreign)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.1, 0.1, 0.2, 0.1, null, png, foreign),
            Date(0)
        ]);

        Assert.DoesNotContain("someone-else", File.ReadAllText(draftPath), StringComparison.Ordinal);
        Assert.Equal([SignPlacementKind.Date], SignDrafts.Load(pdf).Draft!.Placements.Select(p => p.Kind));

        // A draft edited by hand to point there is not followed, though
        // the file there has the very bytes the draft expects.
        SignatureStore.TryReadForPlacement(signature, out var own);
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.2, 0.1, null, png, own)
        ]);
        var json = File.ReadAllText(draftPath);
        var inside = JsonPath(Path.GetRelativePath(root, signature));
        Assert.Contains(inside, json, StringComparison.Ordinal);
        File.WriteAllText(draftPath, json.Replace(inside, JsonPath(Path.GetRelativePath(root, outside)), StringComparison.Ordinal));

        Assert.Null(SignDrafts.Load(pdf).Draft);
    });

    [Fact]
    public void ADamagedDraftIsLetGoWithoutTakingSigningDown() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var draftPath = SignDrafts.PathFor(pdf);
        WriteDateDraft(pdf);
        File.WriteAllText(draftPath, File.ReadAllText(draftPath).Replace("\"Placements\":[", "\"Placements\":[null,", StringComparison.Ordinal));
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

        File.WriteAllText(draftPath, "{\"Path\":");
        Assert.Null(SignDrafts.Load(pdf).Draft);
        Assert.False(File.Exists(draftPath));
    });

    // ------------------------------------------------------------------
    // Keeping the draft as the user works.
    // ------------------------------------------------------------------

    [Fact]
    public void ChangesReachTheDraftAfterAPauseAndAtOnceWhenTheAppEnds() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, restored: false);

        keeper.Update(0, [Date(0)]);
        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
        Assert.True(SpinWait.SpinUntil(() => File.Exists(SignDrafts.PathFor(pdf)), TimeSpan.FromSeconds(5)));
        Assert.Single(SignDrafts.Load(pdf).Draft!.Placements);

        keeper.Update(4, [Date(0), Date(4)]);
        SignDraftKeeper.FlushAll();
        var draft = SignDrafts.Load(pdf).Draft!;
        Assert.Equal(4, draft.PageIndex);
        Assert.Equal(2, draft.Placements.Count);
    });

    [Fact]
    public void DiscardLeavesNoDraftEvenWithAWriteWaiting() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, restored: false);
        keeper.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();
        Assert.True(File.Exists(SignDrafts.PathFor(pdf)));

        keeper.Update(0, [Date(0), Date(0)]);
        keeper.Discard();
        Thread.Sleep(SignDraftKeeper.Pause * 3);
        SignDraftKeeper.FlushAll();

        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void RemovingTheLastPlacementRemovesTheDraft() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var keeper = new SignDraftKeeper(pdf, FileStamp.Of(pdf)!.Value, restored: false);
        keeper.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        keeper.Update(1, []);
        SignDraftKeeper.FlushAll();

        Assert.False(File.Exists(SignDrafts.PathFor(pdf)));
    });

    [Fact]
    public void AWindowThatPlacedNothingNeverErasesAnothersDraft() => InDataRoot(root =>
    {
        var pdf = WritePdf(root);
        var stamp = FileStamp.Of(pdf)!.Value;
        var working = new SignDraftKeeper(pdf, stamp, restored: false);
        working.Update(0, [Date(0)]);
        SignDraftKeeper.FlushAll();

        // A second window on the same PDF turns a page and leaves.
        var looking = new SignDraftKeeper(pdf, stamp, restored: false);
        looking.Update(3, []);
        SignDraftKeeper.FlushAll();
        looking.Discard();

        Assert.NotNull(SignDrafts.Load(pdf).Draft);
    });

    // ------------------------------------------------------------------
    // The surface and the header.
    // ------------------------------------------------------------------

    [Fact]
    public void EveryChangeOnTheSurfaceReachesTheDraft() => InDataRoot(_ => Sta.Run(() =>
    {
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var surface = new FillSignSurface(0, 2, BlankPage());
        var changes = 0;
        surface.WorkChanged += () => changes++;

        Assert.True(surface.Place(SignPlacementKind.Signature, 0.5, 0.5));
        Assert.Equal(1, changes);
        Assert.Equal(signature, surface.Placements.Single().Source!.File);

        var frame = Frames(surface).Single();
        frame.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.MouseWheelEvent });
        Assert.Equal(2, changes);

        Fields.Call(surface, "UpdatePlacementImage", frame, SignatureStore.TryLoad(SolidPng(30, 10, 0))!);
        Assert.Equal(3, changes);
        Assert.True(surface.Placements.Single().Source!.Redrawn);

        surface.ShowPage(1, BlankPage());
        Assert.Equal(4, changes);

        surface.ShowPage(0, BlankPage());
        Click(Descendants(surface).OfType<Border>().Single(border => border.ToolTip as string == "Remove"), Frames(surface).Single());
        Assert.Equal(6, changes);
        Assert.False(surface.HasWork);
    }));

    [Fact]
    public void RestoredWorkComesBackOnItsPagesAndSaysSoOnce() => InDataRoot(_ => Sta.Run(() =>
    {
        var surface = new FillSignSurface(0, 2, BlankPage());
        var changes = 0;
        surface.WorkChanged += () => changes++;

        surface.Restore([Date(0), Date(1)]);

        Assert.Equal(0, changes);
        Assert.Equal(2, surface.Placements.Count);
        Assert.Single(Frames(surface));
        Assert.True(Descendants(surface).OfType<ButtonBase>().Single(button => button.Content as string == "Save").IsEnabled);
        Assert.Contains(Descendants(surface).OfType<TextBlock>(), block => block.Text == "Your earlier work is back.");

        surface.ShowPage(1, BlankPage());
        Assert.Single(Frames(surface));
        Assert.DoesNotContain(Descendants(surface).OfType<TextBlock>(), block => block.Text == "Your earlier work is back.");
    }));

    [Fact]
    public async Task TheSignPillOffersToResumeAtTheSameWidth()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            await WpfApp.RunAsync(async () =>
            {
                var window = new PreviewWindow();
                try
                {
                    var pdf = WritePdf(temp.Path);
                    var version = Fields.Get<long>(window, "_loadVersion");
                    Fields.Call(window, "ShowTool", HeaderTool.Sign);
                    Fields.Set(window, "_fillSignPath", pdf);
                    await (Task)Fields.Call(window, "FindSignDraftAsync", pdf, version)!;
                    Assert.Equal("Fill and sign", AutomationProperties.GetName(window.SignButton));
                    Assert.Equal(Visibility.Visible, window.SignLabel.Visibility);
                    var signWidth = Width(window.SignButton);

                    WriteDateDraft(pdf);
                    await (Task)Fields.Call(window, "FindSignDraftAsync", pdf, version)!;

                    // The pen says what is resumed; the name says it in full.
                    Assert.Equal("Resume signing", AutomationProperties.GetName(window.SignButton));
                    Assert.Equal("Resume signing", window.SignButton.ToolTip);
                    Assert.Equal(Visibility.Visible, window.ResumeLabel.Visibility);
                    Assert.Equal("Resume", window.ResumeLabel.Children.OfType<TextBlock>().Last().Text);
                    Assert.Equal(Visibility.Hidden, window.SignLabel.Visibility);
                    Assert.Equal(signWidth, Width(window.SignButton));
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static void InDataRoot(Action<string> body)
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            body(temp.Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    private static string WritePdf(string root)
    {
        var path = Path.Combine(root, "docs", "contract.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "%PDF-1.4 a contract");
        return path;
    }

    private static void WriteDateDraft(string pdf) =>
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0, [Date(0)]);

    private static SignPlacement Date(int page) =>
        new(SignPlacementKind.Date, page, 0.4, 0.2, 0.14, 0.028, "10/10/2026");

    private static (SignPlacementKind, int, double, double, double, double, string?) Shape(SignPlacement p) =>
        (p.Kind, p.PageIndex, p.X, p.Y, p.Width, p.Height, p.Text);

    /// <summary>A path as it reads inside a JSON string.</summary>
    private static string JsonPath(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

    private static double Width(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    private static PdfPreviewPage BlankPage()
    {
        var bitmap = BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 400 * 600 * 4).ToArray(), 400 * 4);
        bitmap.Freeze();
        return new PdfPreviewPage(bitmap, 400, 600);
    }

    private static Grid[] Frames(FillSignSurface surface) =>
        Fields.Get<Canvas>(surface, "_placementLayer").Children.OfType<Grid>().ToArray();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }

    /// <summary>Selects the frame, then presses the chip, the way a click on each would.</summary>
    private static void Click(UIElement chip, UIElement frame)
    {
        frame.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        frame.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
        chip.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    private static byte[] SolidPng(int width, int height, byte shade)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = shade;
            pixels[i + 3] = 255;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
