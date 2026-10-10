using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The page rail's helper processes, its range plan and its cache, run the
/// way the app runs them.
/// </summary>
public sealed class PdfLaneRailTests
{
    // Large pages make pdfium's form-fill teardown fault early: the helper
    // used to die closing the document after 20 of these, and every reopen
    // of a long PDF died the same way, leaving the rail at "First 10 of N".
    [Fact]
    public void TheThumbnailHelperFinishesALongRangeOfLargePages()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "large-pages.pdf");
        var output = Path.Combine(temp.Path, "thumbs");
        WriteTextPdf(pdfPath, 30, 1200, 1600);

        var exitCode = RunHelper(temp.Path, "--render-pdf-thumbnails", pdfPath, output, "30", "0");

        Assert.Equal(0, exitCode);
        var manifest = ReadManifest(output);
        Assert.Equal(30, manifest.TotalPages);
        Assert.Equal(Enumerable.Range(1, 30), manifest.Pages.Select(page => page.Number));
        Assert.Equal(30, Directory.EnumerateFiles(output, "page-*.png").Count());
    }

    // A helper is a rasterizer, not the app: it must not run the resident
    // app's shutdown, which wrote "app exiting" into the shared log hundreds
    // of times and started the shell thread only to stop it.
    [Fact]
    public void HelpersLeaveNoAppLifecycleInTheLog()
    {
        using var temp = new TempDirectory();
        var pdfPath = Path.Combine(temp.Path, "three-pages.pdf");
        WriteTextPdf(pdfPath, 3, 612, 792);

        Assert.Equal(0, RunHelper(temp.Path, "--render-pdf-thumbnails", pdfPath, Path.Combine(temp.Path, "thumbs"), "10", "0"));
        Assert.Equal(0, RunHelper(temp.Path, "--prove-pdf-scale", pdfPath, pdfPath));

        var log = Path.Combine(temp.Path, "data", "Glimpse.log");
        var text = File.Exists(log) ? File.ReadAllText(log) : string.Empty;
        Assert.DoesNotContain("app exiting", text, StringComparison.Ordinal);
        Assert.DoesNotContain("app starting", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// pdfium can die in native code on an ordinary document, so it runs in
    /// helper processes. The one in-process exception is Fill &amp; Sign,
    /// which renders one page per document it opens.
    /// </summary>
    [Fact]
    public void PdfiumRunsOnlyInHelpersAndTheNamedException()
    {
        var root = TestSource.FilePath("SpacePreview");
        var separator = Path.DirectorySeparatorChar;
        var sources = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}obj{separator}") && !path.Contains($"{separator}bin{separator}"))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)))
            .ToList();

        Assert.Equal(new[] { "PdfPreviewRenderer.cs" }, Callers(sources, "DocLib."));
        Assert.Equal(new[] { "Program.cs" }, Callers(sources, "PdfPreviewRenderer.TryRenderThumbnails"));
        Assert.Equal(new[] { "Program.cs" }, Callers(sources, "PdfPreviewRenderer.RendersLike"));
        Assert.Equal(new[] { "PreviewWindow.FillSign.cs" }, Callers(sources, "PdfPreviewRenderer.TryRenderPage"));
        Assert.Equal(new[] { "PreviewWindow.Pdf.cs", "Program.cs" }, Callers(sources, "\"--render-pdf-thumbnails\""));
        Assert.Equal(new[] { "PdfViewerLink.cs", "Program.cs" }, Callers(sources, "\"--prove-pdf-scale\""));
    }

    private static string[] Callers(List<(string Name, string Text)> sources, string call) =>
        sources.Where(source => source.Text.Contains(call, StringComparison.Ordinal))
            .Select(source => source.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static PdfThumbnailManifest ReadManifest(string directory) =>
        JsonSerializer.Deserialize<PdfThumbnailManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")))!;

    /// <summary>Runs Glimpse.exe as the app runs its helpers, with its own data root.</summary>
    private static int RunHelper(string scratch, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Glimpse.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["GLIMPSE_DATA_ROOT"] = Path.Combine(scratch, "data");
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        if (!process!.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Already gone.
            }

            throw new Xunit.Sdk.XunitException("the helper never exited");
        }

        return process.ExitCode;
    }

    /// <summary>Pages with a filled frame and a line of text, like a datasheet.</summary>
    private static void WriteTextPdf(string path, int pageCount, int width, int height)
    {
        using var stream = File.Create(path);
        var offsets = new List<long> { 0 };

        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

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
        Object(3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{4 + i * 2} 0 R"));
        Object(2, $"<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>");
        for (var i = 0; i < pageCount; i++)
        {
            var page = 4 + i * 2;
            var content = $"0 0 0 rg 20 20 {width - 40} {height - 40} re f\n"
                + $"BT /F1 24 Tf 1 1 1 rg 40 {height - 80} Td (Page {i + 1}) Tj ET\n";
            Object(page, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << /Font << /F1 3 0 R >> >> /Contents {page + 1} 0 R >>");
            Object(page + 1, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
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
