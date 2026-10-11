using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfPreviewRendererTests
{
    [Fact]
    public void SinglePageRailProbeCountsWithoutRasterizing()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "one-page.pdf");
        WriteBlankPdf(pdfPath, 1);

        var preview = PdfPreviewRenderer.TryRenderThumbnails(
            pdfPath,
            CancellationToken.None,
            pageLimit: 10);

        Assert.NotNull(preview);
        Assert.Equal(1, preview!.TotalPages);
        Assert.Empty(preview.Pages);
    }

    [Fact]
    public void ThumbnailExportBeyondLastPageWritesEmptyManifest()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "six-pages.pdf");
        var outputPath = Path.Combine(temp.Path, "thumbs");
        WriteBlankPdf(pdfPath, 6);

        var ok = PdfPreviewRenderer.TryRenderThumbnailsToDirectory(
            pdfPath,
            outputPath,
            pageLimit: 10,
            CancellationToken.None,
            startPage: 10);

        Assert.True(ok);
        var manifest = JsonSerializer.Deserialize<PdfThumbnailManifest>(
            File.ReadAllText(Path.Combine(outputPath, "manifest.json")));
        Assert.NotNull(manifest);
        Assert.Equal(6, manifest.TotalPages);
        Assert.Empty(manifest.Pages);
        Assert.Empty(Directory.EnumerateFiles(outputPath, "*.png"));
    }

    [Fact]
    public void ThumbnailHelperProcessExportsMultiPageManifest()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "three-pages.pdf");
        var outputPath = Path.Combine(temp.Path, "helper-thumbs");
        var helperPath = Path.Combine(AppContext.BaseDirectory, "Glimpse.exe");
        WriteBlankPdf(pdfPath, 3);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "--render-pdf-thumbnails",
                pdfPath,
                outputPath,
                "10",
                "0"
            }
        });

        Assert.NotNull(process);
        Assert.True(process!.WaitForExit(TimeSpan.FromSeconds(30)));
        Assert.Equal(0, process.ExitCode);
        var manifest = JsonSerializer.Deserialize<PdfThumbnailManifest>(
            File.ReadAllText(Path.Combine(outputPath, "manifest.json")));
        Assert.NotNull(manifest);
        Assert.Equal(3, manifest.TotalPages);
        Assert.Equal(3, manifest.Pages.Count);
        Assert.Equal(3, Directory.EnumerateFiles(outputPath, "*.png").Count());
    }

    [Fact]
    public void FillSignPageRenderAdaptsUnusuallyLargePdfPagesToTheRasterBudget()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "large-two-page.pdf");
        WriteBlankPdf(pdfPath, 2, pageWidth: 2250, pageHeight: 3228);

        var page = PdfPreviewRenderer.TryRenderPage(pdfPath, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(PdfPreviewRenderer.IsRasterSizeSafe(page!.Width, page.Height, thumbnail: false));
        Assert.True((long)page.Width * page.Height <= 12_000_000);
        Assert.True(page.Width >= 2000);
    }

    // 12848 x 5503 points is a real packaging dieline, and a 283 megapixel
    // raster at reading resolution. It used to come back as a damaged file.
    [Fact]
    public void PreviewRenderAdaptsLargeFormatPagesToTheRasterBudget()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "dieline.pdf");
        WriteBlankPdf(pdfPath, 1, pageWidth: 12848, pageHeight: 5503);

        var page = PdfPreviewRenderer.TryRenderPage(pdfPath, 0, CancellationToken.None);

        Assert.NotNull(page);
        Assert.True(PdfPreviewRenderer.IsRasterSizeSafe(page!.Width, page.Height, thumbnail: false));
        Assert.True(page.Width >= 2000);
    }

    // A multi-artboard dieline used to lose its whole page rail: one page over
    // the thumbnail budget returned null for the set.
    [Fact]
    public void RailFitsLargeFormatPagesInsteadOfDroppingEveryThumbnail()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "dielines.pdf");
        WriteBlankPdf(pdfPath, 3, pageWidth: 12848, pageHeight: 5503);

        var preview = PdfPreviewRenderer.TryRenderThumbnails(pdfPath, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal(3, preview!.TotalPages);
        Assert.Equal(3, preview.Pages.Count);
        Assert.All(preview.Pages, page =>
            Assert.True(PdfPreviewRenderer.IsRasterSizeSafe(page.Width, page.Height, thumbnail: true)));
    }

    // A page the viewer cannot frame is rewritten to one it can. The zoom
    // ladder stops at 25%, so a page opening at 2% could never be zoomed back
    // out to how it arrived.
    [Fact]
    public void OversizedPagesAreRewrittenToASizeTheViewerCanFrame()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "dieline.pdf");
        var scaled = Path.Combine(temp.Path, "dieline-scaled.pdf");
        WriteBlankPdf(source, 1, pageWidth: 12848, pageHeight: 5503);

        Assert.True(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1800));

        var page = PdfPreviewRenderer.TryRenderPage(scaled, 0, CancellationToken.None);
        Assert.NotNull(page);

        // The renderer works at 2 pixels per point, and never fits a page that
        // already sits inside the budget, so the raster reports the page size.
        Assert.True(Math.Max(page!.Width, page.Height) / 2.0 <= 1800);

        // The shape has to survive: a stretched dieline is a wrong drawing.
        var before = 12848 / 5503.0;
        var after = page.Width / (double)page.Height;
        Assert.True(Math.Abs(before - after) < 0.01, $"aspect {before:0.000} became {after:0.000}");
    }

    // A cover that fits followed by a dieline that does not: measuring only
    // the first page took the whole rail down instead of fitting the set.
    [Fact]
    public void ARailSurvivesAnOversizedPageAfterAnOrdinaryOne()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "mixed.pdf");
        WriteMixedSizePdf(pdfPath);

        var preview = PdfPreviewRenderer.TryRenderThumbnails(pdfPath, CancellationToken.None);

        Assert.NotNull(preview);
        Assert.Equal(2, preview!.Pages.Count);
        Assert.All(preview.Pages, page =>
            Assert.True(PdfPreviewRenderer.IsRasterSizeSafe(page.Width, page.Height, thumbnail: true)));
    }

    // The rewrite is trusted only when it still draws the original, so the
    // proof has to actually reject a document that does not.
    [Fact]
    public void TheRenderProofRejectsADocumentThatStoppedDrawing()
    {
        using var temp = new TempDirectory();
        var drawn = Path.Combine(temp.Path, "drawn.pdf");
        var blank = Path.Combine(temp.Path, "blank.pdf");
        WriteBlankPdf(drawn, 1, pageWidth: 400, pageHeight: 400, inked: true);
        WriteBlankPdf(blank, 1, pageWidth: 400, pageHeight: 400);

        Assert.Equal(ProofOutcome.Proved, Prove(drawn, drawn));
        Assert.Equal(ProofOutcome.Rejected, Prove(drawn, blank));
    }

    [Fact]
    public void TheRenderProofChecksEveryPage()
    {
        using var temp = new TempDirectory();
        var original = Path.Combine(temp.Path, "original.pdf");
        var changed = Path.Combine(temp.Path, "changed.pdf");
        WriteBlankPdf(
            original,
            2,
            pageWidth: 400,
            pageHeight: 400,
            contentForPage: page => page == 1 ? "0 0 0 rg 180 180 40 40 re f\n" : string.Empty);
        WriteBlankPdf(changed, 2, pageWidth: 400, pageHeight: 400);

        Assert.Equal(ProofOutcome.Rejected, Prove(original, changed));
    }

    [Fact]
    public void TheRenderProofRejectsAColorChange()
    {
        using var temp = new TempDirectory();
        var red = Path.Combine(temp.Path, "red.pdf");
        var teal = Path.Combine(temp.Path, "teal.pdf");
        WriteBlankPdf(
            red,
            1,
            pageWidth: 400,
            pageHeight: 400,
            contentForPage: _ => "1 0 0 rg 40 40 320 320 re f\n");
        WriteBlankPdf(
            teal,
            1,
            pageWidth: 400,
            pageHeight: 400,
            contentForPage: _ => "0 0.5 0.5 rg 40 40 320 320 re f\n");

        Assert.Equal(ProofOutcome.Rejected, Prove(red, teal));
    }

    [Fact]
    public void TheRenderProofRejectsASmallLostMark()
    {
        using var temp = new TempDirectory();
        var marked = Path.Combine(temp.Path, "marked.pdf");
        var blank = Path.Combine(temp.Path, "blank.pdf");
        WriteBlankPdf(
            marked,
            1,
            pageWidth: 400,
            pageHeight: 400,
            contentForPage: _ => "0 0 0 rg 197 197 6 6 re f\n");
        WriteBlankPdf(blank, 1, pageWidth: 400, pageHeight: 400);

        Assert.Equal(ProofOutcome.Rejected, Prove(marked, blank));
    }

    [Fact]
    public void AnUnbalancedGraphicsStateRewriteIsRejectedByTheRenderProof()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "unbalanced.pdf");
        var scaled = Path.Combine(temp.Path, "unbalanced-scaled.pdf");
        WriteBlankPdf(
            source,
            1,
            pageWidth: 12848,
            pageHeight: 5503,
            contentForPage: _ => "Q 0 0 0 rg 11000 2000 400 400 re f\n");

        Assert.True(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1400));
        Assert.Equal(ProofOutcome.Rejected, Prove(source, scaled));
    }

    // A page-level scale cannot move pattern space or annotation rectangles,
    // so those documents must be left alone rather than quietly redrawn wrong.
    [Theory]
    [InlineData("/PatternType 1")]
    [InlineData("/Pattern#54ype 1")]
    [InlineData("/ShadingType 2")]
    [InlineData("/Shading#54ype 2")]
    [InlineData("/Annots [9 0 R]")]
    [InlineData("/Ann#6Fts [9 0 R]")]
    [InlineData("/Unsupported /ObjStm")]
    [InlineData("/UserUnit 2")]
    [InlineData("/ByteRange [0 1 2 3]")]
    public void DocumentsAPageScaleCannotCarryAreLeftAlone(string marker)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "artwork.pdf");
        var scaled = Path.Combine(temp.Path, "artwork-scaled.pdf");
        WriteBlankPdf(source, 1, pageWidth: 12848, pageHeight: 5503, extraPageEntries: marker);

        Assert.False(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1400));
        Assert.False(File.Exists(scaled));
    }

    [Fact]
    public void InheritedAndPrintBoxesAreScaledIndependently()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "boxes.pdf");
        var scaled = Path.Combine(temp.Path, "boxes-scaled.pdf");
        WriteBlankPdf(
            source,
            1,
            pageWidth: 2400,
            pageHeight: 1200,
            extraPageEntries: "/BleedBox [90 90 1910 910] /TrimBox [100 100 1900 900] /ArtBox [120 120 1880 880] /Rotate 90",
            extraPagesEntries: "/MediaBox [0 0 2000 1000] /CropBox [100 100 1900 900]");

        Assert.True(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1400));
        Assert.Equal(ProofOutcome.Proved, Prove(source, scaled));

        var written = File.ReadAllText(scaled, Encoding.Latin1);
        var updatedPage = written[written.LastIndexOf("3 0 obj", StringComparison.Ordinal)..];
        Assert.Contains("/MediaBox [0 0 1866.667 933.334]", updatedPage, StringComparison.Ordinal);
        Assert.Contains("/CropBox [77.778 77.778 1477.778 700]", updatedPage, StringComparison.Ordinal);
        Assert.Contains("/BleedBox [70 70 1485.556 707.778]", updatedPage, StringComparison.Ordinal);
        Assert.Contains("/TrimBox [77.778 77.778 1477.778 700]", updatedPage, StringComparison.Ordinal);
        Assert.Contains("/ArtBox [93.333 93.333 1462.223 684.445]", updatedPage, StringComparison.Ordinal);
        Assert.Contains("/Rotate 90", updatedPage, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIndirectContentsArrayIsSplicedInsteadOfNested()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "indirect-contents.pdf");
        var scaled = Path.Combine(temp.Path, "indirect-contents-scaled.pdf");
        WriteIndirectContentsPdf(source);

        Assert.True(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1400));
        Assert.Equal(ProofOutcome.Proved, Prove(source, scaled));

        var written = File.ReadAllText(scaled, Encoding.Latin1);
        var updatedPage = written[written.LastIndexOf("3 0 obj", StringComparison.Ordinal)..];
        Assert.Contains("/Contents [7 0 R 5 0 R 6 0 R 8 0 R]", updatedPage, StringComparison.Ordinal);
        Assert.DoesNotContain("/Contents [7 0 R 4 0 R 8 0 R]", updatedPage, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryPagesAreLeftAlone()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "letter.pdf");
        var scaled = Path.Combine(temp.Path, "letter-scaled.pdf");
        WriteBlankPdf(source, 2, pageWidth: 612, pageHeight: 792);

        Assert.False(PdfFillSignWriter.TryWriteScaledCopy(source, scaled, 1800));
        Assert.False(File.Exists(scaled));
    }

    private enum ProofOutcome
    {
        Proved,
        Rejected
    }

    /// <summary>
    /// Runs the proof exactly as the app does, in a child process. Calling
    /// RendersLike in-process would let a pdfium fault in native teardown kill
    /// the whole test run, which is both unlike production and a way to lose
    /// every other result in the suite. A crash here fails this test and only
    /// this test. Retrying instead would hide the very fault worth seeing.
    /// </summary>
    private static ProofOutcome Prove(string left, string right)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Glimpse.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "--prove-pdf-scale", left, right }
        });

        Assert.NotNull(process);
        if (!process!.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            // Disposing a Process does not end it. Failing the assertion here
            // without this would leave a Glimpse.exe holding the fixture files
            // for the rest of the run, exactly as the app kills a proof that
            // overruns rather than walking away from it.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            throw new Xunit.Sdk.XunitException("the proof helper never exited");
        }

        return process.ExitCode switch
        {
            0 => ProofOutcome.Proved,
            2 => ProofOutcome.Rejected,
            var other => throw new Xunit.Sdk.XunitException(
                $"the proof helper reached no verdict (exit {other}); pdfium probably died in the child")
        };
    }

    /// <summary>An ordinary page followed by one the thumbnail budget refuses.</summary>
    private static void WriteMixedSizePdf(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new List<long> { 0 };

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        void Object(int number, string body)
        {
            while (offsets.Count <= number)
            {
                offsets.Add(0);
            }

            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> /Contents 4 0 R >>");
        Object(4, "<< /Length 0 >>\nstream\n\nendstream");
        Object(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 12848 5503] /Resources << >> /Contents 6 0 R >>");
        Object(6, "<< /Length 0 >>\nstream\n\nendstream");

        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static void WriteBlankPdf(
        string path,
        int pageCount,
        int pageWidth = 200,
        int pageHeight = 200,
        bool inked = false,
        string extraPageEntries = "",
        string extraPagesEntries = "",
        Func<int, string>? contentForPage = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new List<long> { 0 };

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        void Object(int number, string body)
        {
            while (offsets.Count <= number)
            {
                offsets.Add(0);
            }

            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i * 2} 0 R"));
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} {extraPagesEntries} >>");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObject = 3 + i * 2;
            var contentObject = pageObject + 1;
            Object(pageObject, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Resources << >> /Contents {contentObject} 0 R {extraPageEntries} >>");
            var content = contentForPage?.Invoke(i) ?? (inked
                ? $"0 0 0 rg 20 20 {Math.Max(1, pageWidth - 40)} {Math.Max(1, pageHeight - 40)} re f\n"
                : string.Empty);
            Object(contentObject, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }

    private static void WriteIndirectContentsPdf(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new List<long> { 0 };

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes);
        }

        void Object(int number, string body)
        {
            while (offsets.Count <= number)
            {
                offsets.Add(0);
            }

            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2000 1000] /Resources << >> /Contents 4 0 R >>");
        Object(4, "[5 0 R 6 0 R]");
        const string firstContent = "0 0 0 rg 100 100 200 200 re f\n";
        Object(5, $"<< /Length {firstContent.Length} >>\nstream\n{firstContent}endstream");
        Object(6, "<< /Length 0 >>\nstream\n\nendstream");

        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
