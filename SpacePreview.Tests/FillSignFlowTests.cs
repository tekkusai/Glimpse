using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The tester round's three promises: placed ink survives the stored
/// signature changing, replacing or removing the stored signature is
/// reachable from the sign flow, and pages turn without leaving the mode.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class FillSignFlowTests
{
    [Fact]
    public void FillSignPathIsCommittedOnlyAfterTheLoadStillOwnsThePreview()
    {
        var load = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "LoadPreviewAsync");
        var ownershipGuard = load.IndexOf("token.IsCancellationRequested || version != _loadVersion", StringComparison.Ordinal);
        var commit = load.IndexOf("_fillSignPath = fillSignPath", StringComparison.Ordinal);

        Assert.True(ownershipGuard >= 0);
        Assert.True(commit > ownershipGuard);
        Assert.Contains("fillSignPath = contentPath", load, StringComparison.Ordinal);
        Assert.DoesNotContain("_fillSignPath = contentPath", load, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLoadErrorClearsTheFillSignActionBeforeShowingTheError()
    {
        var load = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "LoadPreviewAsync");
        var failureLog = load.IndexOf("preview load failed", StringComparison.Ordinal);
        var clear = load.IndexOf("_fillSignPath = null", failureLog, StringComparison.Ordinal);
        var pill = load.IndexOf("SyncHeaderTools()", clear, StringComparison.Ordinal);
        var error = load.IndexOf("SetPreviewContent(BuildErrorPreview", pill, StringComparison.Ordinal);

        Assert.True(failureLog >= 0);
        Assert.True(clear > failureLog);
        Assert.True(pill > clear);
        Assert.True(error > pill);
    }

    [Fact]
    public void FillSignEntryPagingAndSaveRequireTheCurrentDocument()
    {
        var entry = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "EnterFillSignModeAsync");
        var paging = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ShowFillSignPageAsync");
        var save = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "OnFillSignSaveRequested");

        Assert.Contains("!IsCurrentContentPath(path)", entry, StringComparison.Ordinal);
        Assert.Contains("!IsCurrentContentPath(path)", paging, StringComparison.Ordinal);
        Assert.Contains("!IsCurrentContentPath(path)", save, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacementsSnapshotTheirPixelsAtPlacementTime()
    {
        // The record carries the pixels; Clamped() must not shed them.
        var placement = new SignPlacement(
            SignPlacementKind.Signature, 0, 0.5, 0.5, 0.2, 0.05, null, [1, 2, 3]);
        Assert.NotNull(placement.Clamped().ImagePng);
        Assert.Equal(placement.ImagePng, placement.Clamped().ImagePng);

        var surfaceSource = TestSource.Read(@"SpacePreview\Preview\FillSignSurface.cs");
        Assert.Contains("imagePng = assetPath is null ? null : TryReadStoreBytes(assetPath)", surfaceSource, StringComparison.Ordinal);

        // Nothing at save time reaches back into the store.
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        Assert.Contains("PngBytes: item.ImagePng)", windowSource, StringComparison.Ordinal);
        Assert.DoesNotContain("File.ReadAllBytes(SignatureStore", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void SignatureLibraryListsSavesAndDeletes()
    {
        using var temp = new TempDirectory();
        var previous = Environment.GetEnvironmentVariable("GLIMPSE_DATA_ROOT");
        Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", temp.Path);
        try
        {
            Assert.Empty(SignatureStore.ListSignatures());

            var first = SignatureStore.SaveSignatureFromStrokes(
                [new List<System.Windows.Point> { new(0, 0), new(40, 20), new(80, 0) }],
                Colors.Black);
            Assert.NotNull(first);

            // A pre-library signature.png still counts as an entry.
            var legacy = Path.Combine(temp.Path, "signature.png");
            File.Copy(first!, legacy);

            var listed = SignatureStore.ListSignatures();
            Assert.Equal(2, listed.Count);
            Assert.Contains(first, listed);
            Assert.Contains(legacy, listed);

            SignatureStore.Remove(first!);
            SignatureStore.Remove(legacy);
            Assert.Empty(SignatureStore.ListSignatures());
        }
        finally
        {
            Environment.SetEnvironmentVariable("GLIMPSE_DATA_ROOT", previous);
        }
    }

    [Fact]
    public void SignaturePickerOwnsChoiceCreationAndDeletion()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\FillSignSurface.cs");

        // The Signature pill opens the picker; an empty library goes
        // straight to the pad.
        Assert.Contains("ToggleSignaturePicker();", source, StringComparison.Ordinal);
        Assert.Contains("if (SignatureStore.ListSignatures().Count == 0)", source, StringComparison.Ordinal);

        // Rows arm their signature; deletion lives on the row, and the
        // drawing card carries no removal control.
        Assert.Contains("ArmSignature(path);", source, StringComparison.Ordinal);
        Assert.Contains("DeleteSignature(path);", source, StringComparison.Ordinal);
        Assert.Contains("\"New signature\", ShowInkCapture, PillRole.Ghost", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Remove saved", source, StringComparison.Ordinal);

        // Deleting the armed signature puts the tool down; placed ink is
        // untouched by construction (snapshots).
        Assert.Contains("_selectedSignaturePath = null;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PlacedSignatureEditsInTheCardWithItsInkAsBase()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\FillSignSurface.cs");

        // Double-click opens the pad with the placed ink visible.
        Assert.Contains("Kind: SignPlacementKind.Signature, ImagePng: { } png", source, StringComparison.Ordinal);
        Assert.Contains("ShowInkCapture(placedInk, frame);", source, StringComparison.Ordinal);
        Assert.Contains("\"Edit this signature\"", source, StringComparison.Ordinal);

        // Commit re-renders base plus strokes into the placement alone.
        Assert.Contains("SignatureStore.RenderComposite(_inkBaseImage, _inkBaseRect, strokes, Colors.Black)", source, StringComparison.Ordinal);
        Assert.Contains("UpdatePlacementImage(frame, edited);", source, StringComparison.Ordinal);

        // Ink cannot leave the pad while drawing.
        Assert.Contains("ClampToPad(e.GetPosition(_inkCanvas))", source, StringComparison.Ordinal);
        Assert.Contains("ClipToBounds = true", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositeRenderCarriesBaseInkAndNewStrokes()
    {
        var pixels = new byte[] { 0, 0, 0, 255 };
        var baseInk = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);

        // Base alone re-renders; base plus strokes grows to their union;
        // nothing at all renders nothing.
        var baseOnly = SignatureStore.RenderComposite(baseInk, new Rect(10, 10, 40, 20), [], Colors.Black);
        Assert.NotNull(baseOnly);

        var composite = SignatureStore.RenderComposite(
            baseInk, new Rect(10, 10, 40, 20),
            [new List<System.Windows.Point> { new(10, 40), new(120, 60) }],
            Colors.Black);
        Assert.NotNull(composite);
        Assert.True(composite!.PixelWidth > baseOnly!.PixelWidth);

        Assert.Null(SignatureStore.RenderComposite(null, Rect.Empty, [], Colors.Black));
    }

    [Fact]
    public void PagesTurnInsideSignModeWithoutLosingWork()
    {
        var surfaceSource = TestSource.Read(@"SpacePreview\Preview\FillSignSurface.cs");

        // Arrows flank the readout, bounded at the ends, hidden for
        // single-page documents.
        Assert.Contains("event Action<int>? PageChangeRequested", surfaceSource, StringComparison.Ordinal);
        Assert.Contains("pageIndex < 0 || pageIndex >= _totalPages || pageIndex == _session.PageIndex", surfaceSource, StringComparison.Ordinal);
        Assert.Contains("SetChipEnabled(_previousPageChip, _session.PageIndex > 0)", surfaceSource, StringComparison.Ordinal);
        Assert.Contains("SetChipEnabled(_nextPageChip, _session.PageIndex < _totalPages - 1)", surfaceSource, StringComparison.Ordinal);
        Assert.Contains("navigable ? Visibility.Visible : Visibility.Collapsed", surfaceSource, StringComparison.Ordinal);

        // ShowPage keeps the whole session and rebuilds only the visible
        // page's frames, selecting nothing.
        Assert.Contains("placement.PageIndex == pageIndex", surfaceSource, StringComparison.Ordinal);
        Assert.Contains("AddPlacementVisual(placement, select: false)", surfaceSource, StringComparison.Ordinal);

        // The host renders the page and hands it back to the same surface.
        var windowSource = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        Assert.Contains("surface.PageChangeRequested += target => _ = ShowFillSignPageAsync(surface, target)", windowSource, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(_fillSignSurface, surface)", windowSource, StringComparison.Ordinal);
        Assert.Contains("surface.ShowPage(pageIndex, page)", windowSource, StringComparison.Ordinal);
    }

    [Fact]
    public void OneSaveCarriesInkAcrossPages()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "two-pages.pdf");
        var output = Path.Combine(temp.Path, "two-pages-signed.pdf");
        WriteTwoPagePdf(source);

        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [
                new PdfFillSignPlacement(
                    1, PdfFillSignPlacementKind.Date,
                    new PdfNormalizedRect(0.1, 0.2, 0.3, 0.2), Text: "7/8/2026"),
                new PdfFillSignPlacement(
                    2, PdfFillSignPlacementKind.Stamp,
                    new PdfNormalizedRect(0.25, 0.25, 0.25, 0.25), PngBytes: OpaqueBlackPng())
            ]);

        // Both pages render with visible ink: one save, two signed pages.
        var before = PdfPreviewRenderer.TryRender(source, CancellationToken.None);
        var after = PdfPreviewRenderer.TryRender(output, CancellationToken.None);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(2, after!.Pages.Count);
        Assert.True(PixelsDiffer(before!.Pages[0].Bitmap, after.Pages[0].Bitmap), "Page 1 shows no ink.");
        Assert.True(PixelsDiffer(before.Pages[1].Bitmap, after.Pages[1].Bitmap), "Page 2 shows no ink.");
    }

    private static bool PixelsDiffer(BitmapSource left, BitmapSource right)
    {
        if (left.PixelWidth != right.PixelWidth || left.PixelHeight != right.PixelHeight)
        {
            return true;
        }

        var stride = left.PixelWidth * 4;
        var a = new byte[stride * left.PixelHeight];
        var b = new byte[stride * right.PixelHeight];
        left.CopyPixels(a, stride, 0);
        right.CopyPixels(b, stride, 0);
        return !a.AsSpan().SequenceEqual(b);
    }

    private static byte[] OpaqueBlackPng()
    {
        var pixels = new byte[] { 0, 0, 0, 255 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WriteTwoPagePdf(string path)
    {
        using var stream = File.Create(path);
        var offsets = new Dictionary<int, long>();

        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        void Object(int number, string body)
        {
            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 5 0 R >>");
        Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 5 0 R >>");
        Object(5, "<< /Length 0 >>\nstream\n\nendstream");

        var xrefOffset = stream.Position;
        Write("xref\n0 6\n0000000000 65535 f \n");
        for (var i = 1; i <= 5; i++)
        {
            Write($"{offsets[i]:0000000000} 00000 n \n");
        }

        Write("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
    }

}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment mutation";
}
