using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// What sign mode says and does when something goes wrong: a save that
/// cannot land where it was sent, a page that will not open, a press that
/// takes a while, and kept work that did not all come back.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FillSignSaveTests
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int WriteProtected = unchecked((int)0x80070013);

    private static readonly IReadOnlyList<IReadOnlyList<Point>> Strokes =
        [new List<Point> { new(0, 0), new(60, 20), new(120, 0) }];

    // ------------------------------------------------------------------
    // Replacing the original.
    // ------------------------------------------------------------------

    [Fact]
    public void AReplaceThatLosesTheOriginalIsStillASave()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("contract.pdf", "original");
        var signed = temp.CreateFile(".contract.pdf.ab12cd34.signing", "signed");
        var lost = false;

        // Windows can fail a replace after it has taken the original away.
        PreviewWindow.SwapIn(signed, original, (_, destination) =>
        {
            File.Delete(destination);
            throw new IOException("unable to move the replacement file");
        }, ref lost);

        Assert.True(lost);
        Assert.Equal("signed", File.ReadAllText(original));
        Assert.Equal(new[] { original }, Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void AReplaceThatFailsWithTheOriginalInPlaceChangesNothing()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("contract.pdf", "original");
        var signed = temp.CreateFile(".contract.pdf.ab12cd34.signing", "signed");
        var lost = false;

        Assert.Throws<IOException>(() => PreviewWindow.SwapIn(
            signed,
            original,
            (_, _) => throw new IOException("in use", SharingViolation),
            ref lost));

        Assert.False(lost);
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal("signed", File.ReadAllText(signed));
    }

    // ------------------------------------------------------------------
    // What a failed save says.
    // ------------------------------------------------------------------

    [Fact]
    public void AFailedSaveSaysOnlyWhatIsTrue()
    {
        // Whoever holds the file, this app's own viewer included.
        var inUse = PreviewWindow.SaveFailureReason(new IOException("locked", SharingViolation), replacing: true);
        Assert.Equal("The file is in use. Close anything else that shows it, then try again.", inUse);
        Assert.DoesNotContain("another app", inUse, StringComparison.Ordinal);

        // A place that takes no write always comes with a way out that works.
        Assert.Equal(
            "The original can't be replaced here. Save a signed copy instead.",
            PreviewWindow.SaveFailureReason(new UnauthorizedAccessException(), replacing: true));
        Assert.Equal(
            "The original can't be replaced here. Save a signed copy instead.",
            PreviewWindow.SaveFailureReason(new IOException("write protected", WriteProtected), replacing: true));
        Assert.Equal(
            "That folder can't be written to. Save a signed copy again and pick another.",
            PreviewWindow.SaveFailureReason(new UnauthorizedAccessException(), replacing: false));
    }

    [Fact]
    public void OnlyASentenceWrittenForTheUserIsShownAsItIs()
    {
        const string generic = "The signed file couldn't be saved. Nothing was changed.";

        // The framework's own words never reach the save bar.
        Assert.Equal(generic, PreviewWindow.SaveFailureReason(new InvalidOperationException("Sequence contains no elements"), replacing: false));
        Assert.Equal(generic, PreviewWindow.SaveFailureReason(new ObjectDisposedException("stream"), replacing: true));
        Assert.Equal(generic, PreviewWindow.SaveFailureReason(new IOException("The parameter is incorrect."), replacing: true));

        Assert.Equal(
            PreviewWindow.ChangedSinceSigningBegan,
            PreviewWindow.SaveFailureReason(new SignRefusalException(PreviewWindow.ChangedSinceSigningBegan), replacing: true));
    }

    // ------------------------------------------------------------------
    // A copy whose folder takes no new file.
    // ------------------------------------------------------------------

    [Fact]
    public Task ACopyThatCannotGoBesideTheOriginalGoesWhereTheUserSays() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var folder = Path.GetDirectoryName(pdf)!;
        var chosen = Path.Combine(root, "elsewhere", "contract (signed).pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(chosen)!);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);

        // The PDF's folder takes no new file, as on a read-only share or a disc.
        using var saved = new ManualResetEventSlim();
        window.WriteSignedPdf = (source, output, placements) =>
        {
            if (string.Equals(Path.GetDirectoryName(output), folder, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException();
            }

            PdfFillSignWriter.WriteSignedCopy(source, output, placements);
            // A finished save shows its file next, in a viewer a test never starts.
            Fields.Set(window, "_loadVersion", Fields.Get<long>(window, "_loadVersion") + 1);
            saved.Set();
        };
        string? askedFor = null;
        window.AskWhereToSaveCopy = path =>
        {
            askedFor = path;
            return chosen;
        };

        surface.ShowSaveChoice();
        Press(surface, "Save a signed copy");

        Assert.True(await EventuallyAsync(() => saved.IsSet), "the copy was never written");
        Assert.Equal(pdf, askedFor);
        Assert.Contains("/Subtype /FreeText", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(chosen)), StringComparison.Ordinal);
        Assert.Equal(new[] { pdf }, Directory.GetFiles(folder));
    });

    [Fact]
    public Task ACopyWithNoPlaceNamedKeepsTheWorkAndSaysHowToGetOne() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        window.WriteSignedPdf = (_, _, _) => throw new UnauthorizedAccessException();
        window.AskWhereToSaveCopy = _ => null;

        surface.ShowSaveChoice();
        Press(surface, "Save a signed copy");

        var status = Fields.Get<TextBlock>(surface, "_saveStatus");
        Assert.True(await EventuallyAsync(() => status.Text.Length > 0 && status.Text != "Saving…"), "the save never answered");
        Assert.Equal("This folder is read-only. Save a signed copy again and pick another.", status.Text);
        Assert.Single(surface.Placements);
        Assert.Same(surface, Fields.Get<FillSignSurface?>(window, "_fillSignSurface"));
        Assert.Equal(new[] { pdf }, Directory.GetFiles(Path.GetDirectoryName(pdf)!));
    });

    [Fact]
    public Task AnOriginalThatCannotBeReplacedPointsAtTheCopy() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        var asked = false;
        window.WriteSignedPdf = (_, _, _) => throw new UnauthorizedAccessException();
        window.AskWhereToSaveCopy = _ =>
        {
            asked = true;
            return null;
        };

        surface.ShowSaveChoice();
        Press(surface, "Replace the original");

        var status = Fields.Get<TextBlock>(surface, "_saveStatus");
        Assert.True(await EventuallyAsync(() => status.Text.Length > 0 && status.Text != "Saving…"), "the save never answered");
        Assert.Equal("The original can't be replaced here. Save a signed copy instead.", status.Text);

        // Replacing never turns into a copy somewhere else on its own.
        Assert.False(asked);
    });

    // ------------------------------------------------------------------
    // The Sign press.
    // ------------------------------------------------------------------

    [Fact]
    public Task ASignPressShowsAtOnceThatItWasTaken() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var page = new TaskCompletionSource<PdfPreviewPage?>();
        Prepare(window, pdf);
        window.RenderSignPage = (_, _, _) => page.Task;

        var entering = (Task)Fields.Call(window, "EnterFillSignModeAsync")!;

        // The page is seconds away, and the press has already answered.
        Assert.False(entering.IsCompleted);
        Assert.True(window.IsEnteringFillSign);
        Assert.False(window.SignButton.IsEnabled);
        Assert.Same(Cursors.AppStarting, window.Cursor);

        page.SetResult(BlankPage());
        await entering;

        Assert.False(window.IsEnteringFillSign);
        Assert.Null(window.Cursor);
        Assert.NotNull(Fields.Get<FillSignSurface?>(window, "_fillSignSurface"));
    });

    [Fact]
    public Task ASignPressWhosePageCannotOpenSaysSo() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var parked = new Border();
        window.PreviewContent.Content = parked;

        var surface = await EnterAsync(window, pdf, render: (_, _) => Task.FromResult<PdfPreviewPage?>(null));

        // The mode opens on a sheet that says why, with nothing to do but close it.
        Assert.NotNull(surface);
        Assert.Equal(
            "This page couldn't be opened for signing. Try again, or open the PDF in another app to sign it.",
            Fields.Get<TextBlock>(surface, "_centerText").Text);
        Assert.Equal(Visibility.Collapsed, Fields.Get<StackPanel>(surface, "_tools").Visibility);
        Assert.Null(Fields.Get<SignDraftKeeper?>(window, "_signDraft"));

        Press(surface, "Close");
        Assert.Null(Fields.Get<FillSignSurface?>(window, "_fillSignSurface"));
        Assert.Same(parked, window.PreviewContent.Content);

        // Trying again is a new press, and a new render.
        var again = await EnterAsync(window, pdf);
        Assert.Equal(Visibility.Visible, Fields.Get<StackPanel>(again, "_tools").Visibility);
        Assert.True(Button(again, "Date").IsEnabled);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task APageThatFailedSaysSoAndOpensWhenAskedAgain(bool faults) => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root, pages: 3);

        // Page two fails twice: rendered ahead of the reader, then asked for.
        var failures = 2;
        var surface = await EnterAsync(window, pdf, render: (index, _) =>
        {
            if (index != 1 || failures-- <= 0)
            {
                return Task.FromResult<PdfPreviewPage?>(BlankPage());
            }

            return faults
                ? Task.FromException<PdfPreviewPage?>(new InvalidOperationException("the helper broke"))
                : Task.FromResult<PdfPreviewPage?>(null);
        });
        var next = Fields.Get<Button>(surface, "_nextPageChip");
        var center = Fields.Get<TextBlock>(surface, "_centerText");

        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await SettleAsync(window);
        Assert.Equal(0, surface.PageIndex);
        Assert.Equal("Page 2 didn't open. Try again.", center.Text);

        next.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        await SettleAsync(window);
        Assert.Equal(1, surface.PageIndex);
        Assert.DoesNotContain("didn't open", center.Text, StringComparison.Ordinal);
    });

    [Fact]
    public Task APageOnItsWaySaysSo() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root, pages: 3);
        var slow = new TaskCompletionSource<PdfPreviewPage?>();
        var surface = await EnterAsync(window, pdf, render: (index, _) =>
            index == 1 ? slow.Task : Task.FromResult<PdfPreviewPage?>(BlankPage()));
        var center = Fields.Get<TextBlock>(surface, "_centerText");

        Fields.Get<Button>(surface, "_nextPageChip").RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal("Opening page 2…", center.Text);

        slow.SetResult(BlankPage());
        await SettleAsync(window);
        Assert.Equal(1, surface.PageIndex);
        Assert.DoesNotContain("Opening", center.Text, StringComparison.Ordinal);
    });

    // ------------------------------------------------------------------
    // Resuming.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(1, "Your earlier work is back, except 1 item.")]
    [InlineData(2, "Your earlier work is back, except 2 items.")]
    public Task ResumingSaysHowManyItemsCouldNotComeBack(int lost, string notice) => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 0));
        var signaturePng = SignatureStore.TryReadForPlacement(signature, out var signatureSource)!;
        var stampPng = SignatureStore.TryReadForPlacement(SignatureStore.StampPath, out var stampSource)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026"),
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.05, null, signaturePng, signatureSource),
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.5, 0.5, 0.18, 0.09, null, stampPng, stampSource)
        ]);

        // The signature is removed and, for two items, the stamp redrawn.
        SignatureStore.Remove(signature);
        if (lost == 2)
        {
            File.WriteAllBytes(SignatureStore.StampPath, SolidPng(40, 20, 255));
        }

        // The Sign pill reads the draft when the file loads, long before a
        // press, and that look already lets the lost placements go.
        Assert.Equal(3 - lost, SignDrafts.Load(pdf).Draft!.Placements.Count);

        var surface = await EnterAsync(window, pdf);

        Assert.Equal(3 - lost, surface.Placements.Count);
        Assert.Equal(notice, Fields.Get<TextBlock>(surface, "_centerText").Text);
    });

    [Fact]
    public Task TheCountIsSaidOnceAndNotAfterTheWorkMovesOn() => InWindow(async (root, window) =>
    {
        var pdf = SignablePdf(root);
        var signature = SignatureStore.SaveSignatureFromStrokes(Strokes, Colors.Black)!;
        var png = SignatureStore.TryReadForPlacement(signature, out var source)!;
        SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0,
        [
            new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026"),
            new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.24, 0.05, null, png, source)
        ]);
        SignatureStore.Remove(signature);
        Assert.Equal(1, SignDrafts.Load(pdf).Draft!.Lost);

        // The session that said it writes the draft again, and the count is spent.
        var surface = await EnterAsync(window, pdf);
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        Assert.True(SignDraftKeeper.FlushAll());

        var kept = SignDrafts.Load(pdf).Draft!;
        Assert.Equal(2, kept.Placements.Count);
        Assert.Equal(0, kept.Lost);
    });

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>A window that is never shown, with the app's data in a folder of its own.</summary>
    private static async Task InWindow(Func<string, PreviewWindow, Task> body)
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
                    await body(temp.Path, window);
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

    private static void Prepare(PreviewWindow window, string pdf)
    {
        Fields.Set(window, "CurrentPath", pdf);
        Fields.Set(window, "_fillSignPath", pdf);
        window.PreviewContent.Content ??= new Border();
    }

    /// <summary>
    /// Sign mode as the Sign pill enters it. The page render is stood in
    /// for, since pdfium never runs in this process.
    /// </summary>
    private static async Task<FillSignSurface> EnterAsync(
        PreviewWindow window,
        string pdf,
        Func<int, CancellationToken, Task<PdfPreviewPage?>>? render = null)
    {
        Prepare(window, pdf);
        window.RenderSignPage = (_, index, token) =>
            render?.Invoke(index, token) ?? Task.FromResult<PdfPreviewPage?>(BlankPage());

        await (Task)Fields.Call(window, "EnterFillSignModeAsync")!;
        return Fields.Get<FillSignSurface>(window, "_fillSignSurface");
    }

    private static string SignablePdf(string root, int pages = 1)
    {
        var path = Path.Combine(root, "docs", "contract.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FillSignBehaviorTests.WritePdf(path, 612, 792, pages);
        return path;
    }

    private static void Press(DependencyObject root, string label) =>
        Button(root, label).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static ButtonBase Button(DependencyObject root, string label) =>
        Descendants(root).OfType<ButtonBase>().First(button => button.Content as string == label);

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

    /// <summary>Lets every continuation already queued on the window's thread run.</summary>
    private static Task SettleAsync(PreviewWindow window) =>
        window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle).Task;

    private static async Task<bool> EventuallyAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 5000 && !condition(); waited += 20)
        {
            await Task.Delay(20);
        }

        return condition();
    }

    private static PdfPreviewPage BlankPage()
    {
        var bitmap = BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 400 * 600 * 4).ToArray(), 400 * 4);
        bitmap.Freeze();
        return new PdfPreviewPage(bitmap, 400, 600);
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
