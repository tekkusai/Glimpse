using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The sign flow's wiring into the preview: the mode belongs to the load
/// that started it, the signature library, and one save across pages.
/// Behavior of the surface itself lives in FillSignBehaviorTests.
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
        var save = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "SaveFillSignAsync");

        Assert.Contains("!IsCurrentContentPath(path)", entry, StringComparison.Ordinal);
        Assert.Contains("!IsCurrentContentPath(path)", paging, StringComparison.Ordinal);
        Assert.Contains("!IsCurrentContentPath(path)", save, StringComparison.Ordinal);
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
                new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.2, 0.3, 0.2, "7/8/2026"),
                new SignPlacement(SignPlacementKind.Stamp, 1, 0.25, 0.25, 0.25, 0.25, null, OpaqueBlackPng())
            ]);

        // Both pages render with visible ink: one save, two signed pages.
        for (var page = 0; page < 2; page++)
        {
            var before = PdfPreviewRenderer.TryRenderPage(source, page, CancellationToken.None);
            var after = PdfPreviewRenderer.TryRenderPage(output, page, CancellationToken.None);
            Assert.NotNull(before);
            Assert.NotNull(after);
            Assert.True(PixelsDiffer(before!.Bitmap, after!.Bitmap), $"Page {page + 1} shows no ink.");
        }
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
