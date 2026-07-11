using SpacePreview.Preview;
using System.IO.Compression;

namespace SpacePreview.Tests;

public sealed class OfficeDocumentPdfConverterTests
{
    [Fact]
    public void DefaultEngineCandidatesIncludeLibreOfficeAndOpenOffice()
    {
        var candidates = OfficeDocumentPdfConverter.GetDefaultSofficeCandidates(null, @"C:\Users\PreviewUser\AppData\Local");

        Assert.Contains(candidates, path => path.EndsWith(@"LibreOffice\program\soffice.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, path => path.EndsWith(@"OpenOffice 4\program\soffice.exe", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProgressivePlanUsesSlideCountForLargeDecks()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        WriteOfficeAppProperties(path, "Slides", 51);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path);

        Assert.NotNull(plan);
        Assert.Equal(6, plan.FirstPageCount);
        Assert.Equal(51, plan.ExpectedPageCount);
        Assert.Contains("impress_pdf_Export", plan.Filter, StringComparison.Ordinal);
        Assert.Contains("1-6", plan.Filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressivePlanAvoidsShortImageHeavyDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "contract.docx");
        WriteOfficeAppProperties(path, "Pages", 5, minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path);

        Assert.Null(plan);
    }

    [Fact]
    public void ProgressivePlanRequiresBothSizeAndPageCountForDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "manual.docx");
        WriteOfficeAppProperties(path, "Pages", 60, minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path);

        Assert.NotNull(plan);
        Assert.Equal(60, plan.ExpectedPageCount);
        Assert.Contains("writer_pdf_Export", plan.Filter, StringComparison.Ordinal);
    }

    private static void WriteOfficeAppProperties(string path, string propertyName, int value, int minimumBytes = 0)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("docProps/app.xml");
            using (var writer = new StreamWriter(entry.Open()))
            {
                writer.Write($"""
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <Properties xmlns="http://schemas.openxmlformats.org/officeDocument/2006/extended-properties">
                      <{propertyName}>{value}</{propertyName}>
                    </Properties>
                    """);
            }

            if (minimumBytes > 0)
            {
                var filler = archive.CreateEntry("docProps/filler.bin", CompressionLevel.NoCompression);
                using var stream = filler.Open();
                var buffer = new byte[8192];
                var remaining = minimumBytes;
                while (remaining > 0)
                {
                    var count = Math.Min(buffer.Length, remaining);
                    stream.Write(buffer, 0, count);
                    remaining -= count;
                }
            }
        }
    }
}
