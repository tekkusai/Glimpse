using System.IO.Compression;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class OfficePageSizeProbeTests
{
    [Fact]
    public void ReadsWidePowerPointSlideAspect()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        WriteZipEntry(
            path,
            "ppt/presentation.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:sldSz cx="12192000" cy="6858000"/>
            </p:presentation>
            """);

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.True(ok);
        Assert.Equal(16.0 / 9.0, aspect, precision: 3);
    }

    [Fact]
    public void ReadsStandardPowerPointSlideAspect()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        WriteZipEntry(
            path,
            "ppt/presentation.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main">
              <p:sldSz cx="9144000" cy="6858000"/>
            </p:presentation>
            """);

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.True(ok);
        Assert.Equal(4.0 / 3.0, aspect, precision: 3);
    }

    [Fact]
    public void ReadsWordPageAspect()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "doc.docx");
        WriteZipEntry(
            path,
            "word/document.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:sectPr><w:pgSz w:w="12240" w:h="15840"/></w:sectPr>
              </w:body>
            </w:document>
            """);

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.True(ok);
        Assert.Equal(12240.0 / 15840.0, aspect, precision: 3);
    }

    [Fact]
    public void AppliesWordLandscapeOrientation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "landscape.docx");
        WriteZipEntry(
            path,
            "word/document.xml",
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">
              <w:body>
                <w:sectPr><w:pgSz w:w="12240" w:h="15840" w:orient="landscape"/></w:sectPr>
              </w:body>
            </w:document>
            """);

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.True(ok);
        Assert.Equal(15840.0 / 12240.0, aspect, precision: 3);
    }

    [Fact]
    public void IgnoresSpreadsheets()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "sheet.xlsx");
        WriteZipEntry(path, "xl/workbook.xml", "<workbook/>");

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.False(ok);
        Assert.Equal(0, aspect);
    }

    [Fact]
    public void ReturnsFalseForCorruptZip()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken.pptx");
        File.WriteAllText(path, "not a zip");

        var ok = OfficePageSizeProbe.TryReadFirstPageAspect(path, out var aspect);

        Assert.False(ok);
        Assert.Equal(0, aspect);
    }

    private static void WriteZipEntry(string path, string entryName, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
