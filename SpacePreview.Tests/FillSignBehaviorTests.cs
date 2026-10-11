using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Fill and sign as the user meets it: what the saved file shows, what the
/// surface does with the work on it, and what a failure says. These replace
/// source-text pins that passed while dates lost their last digit on A4 and
/// signatures came out sideways on rotated pages.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FillSignBehaviorTests
{
    // ------------------------------------------------------------------
    // The saved file.
    // ------------------------------------------------------------------

    [Fact]
    public void TheDefaultDateOnAnA4PageKeepsEveryDigitInsideItsFrame()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "a4.pdf");
        var output = Path.Combine(temp.Path, "a4-signed.pdf");
        WritePdf(source, 595, 842);

        // The surface's own default: a date frame sized from its text.
        const double height = 0.028;
        var date = new SignPlacement(SignPlacementKind.Date, 0, 0.5, 0.2, 0, height, "10/10/2026")
            .FittedToText(842.0 / 595.0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [date]);

        var page = PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None)!;
        var ink = InkBounds(page.Bitmap);
        Assert.NotNull(ink);

        var frame = new Rect(
            date.X * page.Width,
            date.Y * page.Height,
            date.Width * page.Width,
            date.Height * page.Height);
        Assert.True(ink!.Value.Left >= frame.Left - 1 && ink.Value.Right <= frame.Right + 1, $"ink {ink} leaves frame {frame}");
        Assert.True(ink.Value.Top >= frame.Top - 1 && ink.Value.Bottom <= frame.Bottom + 1, $"ink {ink} leaves frame {frame}");

        // All ten glyphs drew: the ink spans the text's width less the side
        // bearings of the first and last digit. A clipped "10/10/202" falls
        // a whole digit short.
        var fontPixels = SignText.FontScale * frame.Height;
        Assert.True(ink.Value.Width >= 4.6 * fontPixels, $"ink is {ink.Value.Width:0} px wide, text needs {4.6 * fontPixels:0}");
    }

    [Fact]
    public void ASignatureOnARotatedPageComesOutUpright()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "rotated.pdf");
        var output = Path.Combine(temp.Path, "rotated-signed.pdf");
        WritePdf(source, 200, 100, pageExtra: "/Rotate 90");

        // A thin stroke along the top of a wide frame. Upright, the ink is
        // a wide bar; drawn along the unrotated axes it stands on its end.
        var signature = new SignPlacement(SignPlacementKind.Signature, 0, 0.1, 0.1, 0.8, 0.2, null, TopBarPng());
        PdfFillSignWriter.WriteSignedCopy(source, output, [signature]);

        var page = PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None)!;
        Assert.True(page.Height > page.Width, "the rotated page displays portrait");
        var ink = InkBounds(page.Bitmap);
        Assert.NotNull(ink);
        Assert.True(ink!.Value.Width > 2 * ink.Value.Height, $"ink {ink} is not a wide bar");
    }

    [Fact]
    public void TheSameStampOnTwoPagesIsStoredOnce()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "contract.pdf");
        var output = Path.Combine(temp.Path, "contract-stamped.pdf");
        WritePdf(source, 200, 100, pages: 2);
        var png = OpaqueBlackPng();

        PdfFillSignWriter.WriteSignedCopy(source, output,
        [
            new SignPlacement(SignPlacementKind.Stamp, 0, 0.1, 0.1, 0.2, 0.2, null, png),
            new SignPlacement(SignPlacementKind.Stamp, 1, 0.1, 0.1, 0.2, 0.2, null, png)
        ]);

        var appended = File.ReadAllText(output, Encoding.Latin1)[(int)new FileInfo(source).Length..];
        Assert.Equal(1, Count(appended, "/Subtype /Image"));
        Assert.Equal(2, Count(appended, "/Subtype /Stamp"));
    }

    [Fact]
    public void PlacedTextUsesWinAnsiSoTheFileShowsWhatTheScreenShows()
    {
        Assert.True(SignText.TryEncode('\u20AC', out var euro));
        Assert.Equal(0x80, euro);
        Assert.True(SignText.TryEncode('\u2019', out var quote));
        Assert.Equal(0x92, quote);
        Assert.False(SignText.TryEncode('\u65E5', out _));
        Assert.Equal("Paid \u20AC5 \u201Cdone\u201D ", SignText.Writable("Paid \u20AC5 \u201Cdone\u201D \u65E5\u4ED8"));

        // Helvetica's widths: the date the A4 bug clipped is five ems.
        Assert.Equal(5.004, SignText.WidthEm("10/10/2026"), precision: 3);
    }

    [Fact]
    public void ADateFrameGrowsWithItsText()
    {
        var shortDate = new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.4, 0, 0.03, "1/2/26").FittedToText(1.4);
        var longText = (shortDate with { Text = "Signed on 10 October 2026" }).FittedToText(1.4);

        Assert.True(longText.Width > shortDate.Width * 2);
        Assert.Equal(shortDate.Height, longText.Height, precision: 6);
        Assert.Equal(shortDate.X + shortDate.Width / 2, longText.X + longText.Width / 2, precision: 6);
    }

    // ------------------------------------------------------------------
    // Before any work: the preflight.
    // ------------------------------------------------------------------

    [Fact]
    public void PreflightCountsPagesTheWayASaveWould()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "three.pdf");
        WritePdf(source, 200, 100, pages: 3);

        Assert.Equal(new PdfSignCheck(3, null), PdfFillSignWriter.Preflight(source));
    }

    [Fact]
    public void PreflightExplainsAnEncryptedPdfBeforeAnythingIsPlaced()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "locked.pdf");
        WritePdf(source, 200, 100, trailerExtra: "/Encrypt 9 0 R");

        var check = PdfFillSignWriter.Preflight(source);
        Assert.Contains("encrypted", check.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain("Glimpse", check.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void PreflightRefusesAnnotationsItCannotExtend()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "form.pdf");
        WritePdf(source, 200, 100, pageExtra: "/Annots 9 0 R", extraObject: "<< /Unexpected true >>");

        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(source).Problem);
    }

    // ------------------------------------------------------------------
    // Saving and its failures.
    // ------------------------------------------------------------------

    [Fact]
    public void AFailedSaveSaysWhyInTheUsersWords()
    {
        Assert.Equal(
            PdfFillSignWriter.UnsupportedStructure,
            PreviewWindow.SaveFailureReason(new InvalidOperationException(PdfFillSignWriter.UnsupportedStructure)));
        Assert.Contains(
            "open in another app",
            PreviewWindow.SaveFailureReason(new IOException("locked", unchecked((int)0x80070020))),
            StringComparison.Ordinal);
        Assert.Contains(
            "read-only",
            PreviewWindow.SaveFailureReason(new UnauthorizedAccessException()),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedReplaceLeavesNoTemporaryFileBehind()
    {
        using var temp = new TempDirectory();
        var original = temp.CreateFile("deal.pdf", "original");
        var signing = PreviewWindow.SigningTempPath(original);
        File.WriteAllText(signing, "signed");
        File.SetAttributes(signing, FileAttributes.Hidden);

        PreviewWindow.CleanUpSigningTemp(signing, original);

        Assert.False(File.Exists(signing));
        Assert.Equal("original", File.ReadAllText(original));
        Assert.Equal(new[] { original }, Directory.GetFiles(temp.Path));
    }

    [Fact]
    public void AReplaceThatLostTheOriginalKeepsTheSignedDocument()
    {
        using var temp = new TempDirectory();
        var original = Path.Combine(temp.Path, "deal.pdf");
        var signing = PreviewWindow.SigningTempPath(original);
        File.WriteAllText(signing, "signed");
        File.SetAttributes(signing, FileAttributes.Hidden);

        PreviewWindow.CleanUpSigningTemp(signing, original);

        Assert.Equal("signed", File.ReadAllText(original));
        Assert.False(File.GetAttributes(original).HasFlag(FileAttributes.Hidden));
        Assert.False(File.Exists(signing));
    }

    // ------------------------------------------------------------------
    // The surface.
    // ------------------------------------------------------------------

    [Fact]
    public void OneSaveCarriesWorkFromEveryPage()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 2, BlankPage());
            IReadOnlyList<SignPlacement>? saved = null;
            surface.SaveRequested += (placements, _) => saved = placements;

            Assert.True(surface.Place(SignPlacementKind.Date, 0.5, 0.3));
            surface.ShowPage(1, BlankPage());
            Assert.True(surface.Place(SignPlacementKind.Date, 0.5, 0.6));
            surface.ShowPage(0, BlankPage());

            surface.ShowSaveChoice();
            Click(Pill(surface, "Save a signed copy"));

            Assert.NotNull(saved);
            Assert.Equal(new[] { 0, 1 }, saved!.Select(item => item.PageIndex).Order());
        });
    }

    [Fact]
    public void SaveWaitsForSomethingToSave()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 1, BlankPage());
            var exited = false;
            surface.ExitRequested += () => exited = true;

            Assert.False(Pill(surface, "Save").IsEnabled);
            surface.ShowSaveChoice();
            Assert.False(Shown(Pill(surface, "Discard")));
            Assert.False(exited);

            surface.Place(SignPlacementKind.Date, 0.5, 0.5);
            Assert.True(Pill(surface, "Save").IsEnabled);
        });
    }

    [Fact]
    public void LeavingWithWorkAsksFirstAndDiscardIsTheWayOut()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 1, BlankPage());
            var exits = 0;
            surface.ExitRequested += () => exits++;
            surface.Place(SignPlacementKind.Date, 0.5, 0.5);

            surface.HandleEscape();
            Assert.Equal(0, exits);
            Assert.True(Shown(Pill(surface, "Discard")));

            surface.HandleEscape();
            Assert.False(Shown(Pill(surface, "Discard")));
            Click(Pill(surface, "Cancel"));
            Assert.Equal(0, exits);

            Click(Pill(surface, "Discard"));
            Assert.Equal(1, exits);
        });
    }

    [Fact]
    public void EveryExitAsksTheSameQuestion()
    {
        OnSta(() =>
        {
            var empty = new FillSignSurface(0, 1, BlankPage());
            Assert.True(empty.TryLeave());

            var working = new FillSignSurface(0, 1, BlankPage());
            working.Place(SignPlacementKind.Date, 0.5, 0.5);
            Assert.False(working.TryLeave());
            Assert.True(Shown(Pill(working, "Discard")));

            // Work this file could never save is not held hostage.
            var unsignable = new FillSignSurface(0, 1, BlankPage());
            unsignable.Place(SignPlacementKind.Date, 0.5, 0.5);
            unsignable.ApplyCheck(new PdfSignCheck(0, PdfFillSignWriter.UnsupportedStructure));
            Assert.True(unsignable.TryLeave());
        });
    }

    [Fact]
    public void LeavingWithoutWorkJustLeaves()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 1, BlankPage());
            var exits = 0;
            surface.ExitRequested += () => exits++;

            surface.HandleEscape();
            Assert.Equal(1, exits);
        });
    }

    [Fact]
    public void ASaveFailureIsShownWhereTheChoicesAre()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 1, BlankPage());
            surface.Place(SignPlacementKind.Date, 0.5, 0.5);
            surface.SetSaving(true);

            var reason = PreviewWindow.SaveFailureReason(new InvalidOperationException(PdfFillSignWriter.UnsupportedStructure));
            surface.ShowSaveFailure(reason);

            Assert.True(Shown(Text(surface, reason)));
            Assert.True(Pill(surface, "Save a signed copy").IsEnabled);
        });
    }

    [Fact]
    public void AnUnsignableFileSaysSoInsteadOfOfferingTools()
    {
        OnSta(() =>
        {
            var surface = new FillSignSurface(0, 1, BlankPage());
            surface.ApplyCheck(new PdfSignCheck(0, "This PDF is encrypted, so it can't be signed here."));

            var line = Descendants(surface).OfType<TextBlock>().Single(block => block.Text.StartsWith("This PDF is encrypted", StringComparison.Ordinal));
            Assert.True(Shown(line));
            Assert.EndsWith("Open it in another app to sign it.", line.Text, StringComparison.Ordinal);
            Assert.False(Shown(Pill(surface, "Date")));
            Assert.False(Shown(Pill(surface, "Save")));
            Assert.True(Shown(Pill(surface, "Close")));
        });
    }

    [Fact]
    public void ThePageCountComesFromTheDocumentNotTheRail()
    {
        OnSta(() =>
        {
            // The rail had not loaded (or its helper crashed): one page known.
            var surface = new FillSignSurface(0, 1, BlankPage());
            Assert.NotNull(Text(surface, "Page 1 of 1"));

            surface.ApplyCheck(new PdfSignCheck(77, null));
            Assert.NotNull(Text(surface, "Page 1 of 77"));
        });
    }

    [Fact]
    public void PlacedInkKeepsItsPixelsWhenTheSavedSignatureGoes()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            OnSta(() =>
            {
                var stored = SignatureStore.SaveSignatureFromStrokes(
                    [new List<Point> { new(0, 0), new(60, 20), new(120, 0) }],
                    Colors.Black)!;
                File.WriteAllBytes(SignatureStore.StampPath, TopBarPng());
                var surface = new FillSignSurface(0, 1, BlankPage());

                Assert.True(surface.Place(SignPlacementKind.Signature, 0.3, 0.3));
                Assert.True(surface.Place(SignPlacementKind.Stamp, 0.7, 0.7));
                var placed = surface.Placements[0].ImagePng;
                SignatureStore.Remove(stored);
                SignatureStore.Remove(SignatureStore.StampPath);

                Assert.Empty(SignatureStore.ListSignatures());
                Assert.False(File.Exists(stored));
                Assert.Same(placed, surface.Placements[0].ImagePng);
                Assert.NotNull(SignatureStore.TryLoad(placed!));

                // A save after the files went carries both, as placed: each
                // image and its alpha.
                var source = Path.Combine(temp.Path, "contract.pdf");
                var output = Path.Combine(temp.Path, "contract (signed).pdf");
                WritePdf(source, 612, 792);
                PdfFillSignWriter.WriteSignedCopy(source, output, surface.Placements);
                var appended = File.ReadAllText(output, Encoding.Latin1)[(int)new FileInfo(source).Length..];
                Assert.Equal(4, Count(appended, "/Subtype /Image"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    [Fact]
    public async Task InkDeletedInSettingsLeavesThePickerAndPutsDownTheToolHoldingIt()
    {
        // Settings fixes its file at first use: let that be the run's root.
        _ = Settings.Current;
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            await WpfApp.RunAsync(async () =>
            {
                var signatures = temp.CreateDirectory("signatures");
                var kept = Path.Combine(signatures, "sig-20261010-120000-000.png");
                var gone = Path.Combine(signatures, "sig-20261010-120100-000.png");
                File.WriteAllBytes(kept, TopBarPng());
                File.WriteAllBytes(gone, TopBarPng());
                File.SetLastWriteTimeUtc(kept, DateTime.UtcNow.AddMinutes(-1));
                File.WriteAllBytes(SignatureStore.StampPath, TopBarPng());

                // Sign mode open on screen, Settings open beside it.
                var surface = new FillSignSurface(0, 1, BlankPage());
                var host = new Window { Left = -30000, Top = 0, Width = 800, Height = 600, ShowActivated = false, ShowInTaskbar = false, Content = surface };
                host.Show();
                var settings = new SettingsWindow(() => null, () => false, () => { }, _ => { }, () => []);
                try
                {
                    Assert.True(await UntilAsync(() => surface.IsLoaded));

                    // The newest signature in hand, and the picker open again.
                    Click(Pill(surface, "Signature"));
                    Click(SignatureRows(surface)[0]);
                    Click(Pill(surface, "Signature"));
                    Assert.Equal(SignPlacementKind.Signature, Fields.Get<SignPlacementKind?>(surface, "_armedTool"));
                    Assert.Equal(2, SignatureRows(surface).Count);

                    await DeleteInSettingsAsync(settings, gone);

                    Assert.Null(Fields.Get<SignPlacementKind?>(surface, "_armedTool"));
                    Assert.Single(SignatureRows(surface));
                    Assert.True(surface.Place(SignPlacementKind.Signature, 0.5, 0.5));
                    Assert.Equal(kept, surface.Placements.Single().Source!.File);

                    // Ink saved elsewhere joins the open picker.
                    SignatureStore.SaveSignatureFromStrokes([new List<Point> { new(0, 0), new(60, 20) }], Colors.Black);
                    Assert.Equal(2, SignatureRows(surface).Count);

                    // The stamp in hand goes down with its file too.
                    Click(Pill(surface, "Stamp"));
                    Assert.Equal(SignPlacementKind.Stamp, Fields.Get<SignPlacementKind?>(surface, "_armedTool"));
                    await DeleteInSettingsAsync(settings, SignatureStore.StampPath);

                    Assert.Null(Fields.Get<SignPlacementKind?>(surface, "_armedTool"));
                    Assert.False(Shown(Pill(surface, "New stamp")));
                }
                finally
                {
                    settings.Close();
                    host.Content = null;
                    await UntilAsync(() => !surface.IsLoaded);
                    host.Close();
                }
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    [Fact]
    public void TheStoreSaysWhenTheSavedInkChanges()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        var changes = 0;
        void Count() => changes++;
        SignatureStore.Changed += Count;
        try
        {
            OnSta(() =>
            {
                var signature = SignatureStore.SaveSignatureFromStrokes([new List<Point> { new(0, 0), new(60, 20) }], Colors.Black)!;
                Assert.Equal(1, changes);

                var image = Path.Combine(temp.Path, "logo.png");
                File.WriteAllBytes(image, TopBarPng());
                Assert.True(SignatureStore.TryImportStamp(image));
                Assert.Equal(2, changes);
                Assert.False(SignatureStore.TryImportStamp(Path.Combine(temp.Path, "missing.png")));
                Assert.Equal(2, changes);

                SignatureStore.Remove(signature);
                SignatureStore.Remove(SignatureStore.StampPath);
                Assert.Equal(4, changes);
            });
        }
        finally
        {
            SignatureStore.Changed -= Count;
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    [Fact]
    public void DeletingASavedSignatureTakesASecondClick()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            OnSta(() =>
            {
                SignatureStore.SaveSignatureFromStrokes([new List<Point> { new(0, 0), new(60, 20) }], Colors.Black);
                var surface = new FillSignSurface(0, 1, BlankPage());
                Click(Pill(surface, "Signature"));

                var remove = Descendants(surface).OfType<Border>().Single(border => border.ToolTip as string == "Delete this signature");
                Click(remove);
                Assert.Single(SignatureStore.ListSignatures());

                Click(Pill(surface, "Keep"));
                Assert.False(Shown(Pill(surface, "Delete")));
                Assert.Single(SignatureStore.ListSignatures());

                Click(remove);
                Click(Pill(surface, "Delete"));
                Assert.Empty(SignatureStore.ListSignatures());
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    [Fact]
    public void AnImportedStampIsNoLargerThanAPageNeeds()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            var photo = Path.Combine(temp.Path, "logo.png");
            File.WriteAllBytes(photo, SolidPng(3000, 1500));

            Assert.True(SignatureStore.TryImportStamp(photo));
            var stamp = SignatureStore.TryLoad(SignatureStore.StampPath)!;
            Assert.Equal(1200, Math.Max(stamp.PixelWidth, stamp.PixelHeight));

            // A stamp stored at full size before imports were bounded is
            // scaled on its way into a placement.
            File.WriteAllBytes(SignatureStore.StampPath, SolidPng(3000, 1500));
            var placed = SignatureStore.TryLoad(SignatureStore.TryReadForPlacement(SignatureStore.StampPath, out _)!)!;
            Assert.Equal(1200, Math.Max(placed.PixelWidth, placed.PixelHeight));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    private static void OnSta(Action action) => Sta.Run(action);

    private static async Task<bool> UntilAsync(Func<bool> condition)
    {
        for (var waited = 0; waited < 5000 && !condition(); waited += 20)
        {
            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>The open picker's saved signatures, newest first.</summary>
    private static List<Border> SignatureRows(FillSignSurface surface) =>
        ((StackPanel)Fields.Get<Border>(surface, "_signatureFlyout").Child).Children.OfType<Border>().ToList();

    /// <summary>
    /// The trash on that file's row in Settings, then Delete, on rows as
    /// fresh as coming back to Settings makes them. Settings reads the ink
    /// on a worker, and a file still open there cannot be deleted.
    /// </summary>
    private static async Task DeleteInSettingsAsync(SettingsWindow settings, string path)
    {
        Fields.Call(settings, "SyncSignatures");
        Assert.True(await UntilAsync(() => settings.SignaturesList.Children.OfType<Grid>()
            .All(row => ((Image)row.Children.OfType<Border>().Single().Child).Source is not null)));
        var paths = SignatureStore.ListSignatures().ToList();
        if (SignatureStore.HasStamp)
        {
            paths.Add(SignatureStore.StampPath);
        }

        var row = (Grid)settings.SignaturesList.Children[paths.IndexOf(path)];
        Click(row.Children.OfType<Button>().Single());
        Click((ButtonBase)row.Children.OfType<StackPanel>().Single().Children[1]);
        Assert.False(File.Exists(path));
    }

    private static PdfPreviewPage BlankPage()
    {
        var bitmap = BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, Enumerable.Repeat((byte)255, 400 * 600 * 4).ToArray(), 400 * 4);
        bitmap.Freeze();
        return new PdfPreviewPage(bitmap, 400, 600);
    }

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

    private static FrameworkElement Pill(FrameworkElement root, string label) =>
        Descendants(root).OfType<FrameworkElement>().First(element =>
            element is ButtonBase { Content: string content } ? content == label
            : element is Border { Child: TextBlock { Text: var text } } && text == label);

    private static TextBlock? Text(FrameworkElement root, string text) =>
        Descendants(root).OfType<TextBlock>().FirstOrDefault(block => block.Text == text);

    /// <summary>Visible as far as the tree says: the element and every ancestor are Visible.</summary>
    private static bool Shown(FrameworkElement? element)
    {
        for (var current = element; current is not null; current = current.Parent as FrameworkElement)
        {
            if (current.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return element is not null;
    }

    private static void Click(UIElement element)
    {
        if (element is ButtonBase button)
        {
            button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            return;
        }

        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
        element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    }

    /// <summary>The bounding box of dark pixels, in bitmap pixels.</summary>
    private static Rect? InkBounds(BitmapSource bitmap)
    {
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < source.PixelHeight; y++)
        {
            for (var x = 0; x < source.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static int Count(string text, string token)
    {
        var count = 0;
        for (var index = text.IndexOf(token, StringComparison.Ordinal); index >= 0; index = text.IndexOf(token, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static byte[] OpaqueBlackPng() =>
        Encode(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4));

    private static byte[] SolidPng(int width, int height) =>
        Encode(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4));

    /// <summary>16 x 16, black in the top two rows and clear below.</summary>
    private static byte[] TopBarPng()
    {
        var pixels = new byte[16 * 16 * 4];
        for (var i = 0; i < 16 * 2; i++)
        {
            pixels[i * 4 + 3] = 255;
        }

        return Encode(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4));
    }

    internal static void WritePdf(
        string path,
        double width,
        double height,
        int pages = 1,
        string pageExtra = "",
        string trailerExtra = "",
        string? extraObject = null)
    {
        using var stream = File.Create(path);
        var offsets = new List<long>();

        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        void Object(string body)
        {
            offsets.Add(stream.Position);
            Write($"{offsets.Count} 0 obj\n{body}\nendobj\n");
        }

        // 1 catalog, 2 page tree, pages from 3, then the optional extra
        // object, which pages may reference as 9 0 R when it lands there.
        var kids = string.Join(" ", Enumerable.Range(3, pages).Select(number => $"{number} 0 R"));
        Write("%PDF-1.4\n");
        Object("<< /Type /Catalog /Pages 2 0 R >>");
        Object($"<< /Type /Pages /Kids [{kids}] /Count {pages} >>");
        for (var i = 0; i < pages; i++)
        {
            Object($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << >> {pageExtra} >>");
        }

        while (extraObject is not null && offsets.Count < 8)
        {
            Object("<< >>");
        }

        if (extraObject is not null)
        {
            Object(extraObject);
        }

        var xrefOffset = stream.Position;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write($"{offset:0000000000} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R {trailerExtra} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
    }
}
