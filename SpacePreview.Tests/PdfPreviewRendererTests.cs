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

    private static void WriteBlankPdf(
        string path,
        int pageCount,
        int pageWidth = 200,
        int pageHeight = 200)
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
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObject = 3 + i * 2;
            var contentObject = pageObject + 1;
            Object(pageObject, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageWidth} {pageHeight}] /Resources << >> /Contents {contentObject} 0 R >>");
            Object(contentObject, "<< /Length 0 >>\nstream\n\nendstream");
        }

        var xref = stream.Position;
        Write($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        for (var i = 1; i < offsets.Count; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    }
}
