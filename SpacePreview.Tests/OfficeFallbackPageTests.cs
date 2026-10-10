using System.IO.Compression;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>The engine-less preview: a page, not a text dump, and nothing written beside it.</summary>
public sealed class OfficeFallbackPageTests
{
    private static readonly byte[] Pixel = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");

    [Fact]
    public void ADocumentReadsAsHeadingsParagraphsAndItsOwnPictures()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "report.docx");
        WriteDocument(path);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Document, CancellationToken.None);

        Assert.NotNull(page);
        var html = File.ReadAllText(page.DocumentPath);
        Assert.Contains("<h1>Quarterly report</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<h2>Results</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<p><strong>Revenue</strong> grew.</p>", html, StringComparison.Ordinal);
        Assert.Contains("<td>Q1</td><td>12</td>", html, StringComparison.Ordinal);
        Assert.Contains("src=\"data:image/png;base64,", html, StringComparison.Ordinal);
        Assert.Contains("class=\"md-root office\"", html, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(TestDataRoot.Path, "OfficeMedia")));
    }

    [Fact]
    public void ARenamedDocumentPreviewsByTheKindItsContentNames()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "renamed-file.bin");
        WriteDocument(path);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Document, CancellationToken.None);

        Assert.NotNull(page);
        Assert.Contains("Quarterly report", File.ReadAllText(page.DocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkbookBecomesSheetsWithValuesInTheirColumns()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "book.xlsx");
        WriteWorkbook(path);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Spreadsheet, CancellationToken.None);

        Assert.NotNull(page);
        Assert.NotNull(page.Sheets);
        Assert.Equal(["Budget", "Notes"], page.Sheets.Select(sheet => sheet.Name));
        var budget = File.ReadAllText(page.Sheets[0].Path);
        // A1 is a string, C1 a number: B1 stays an empty cell between them.
        Assert.Contains("<td>Item</td><td></td><td align=\"right\">42</td>", budget, StringComparison.Ordinal);
        Assert.Contains("class=\"sp-h sp-cl\">C</td>", budget, StringComparison.Ordinal);
        Assert.Contains("class=\"sp-h sp-rn\">2</td>", budget, StringComparison.Ordinal);
        Assert.Contains("Second sheet", File.ReadAllText(page.Sheets[1].Path), StringComparison.Ordinal);
    }

    [Fact]
    public void ADeckBecomesOneCardPerSlideInOrder()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        WriteDeck(path);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Presentation, CancellationToken.None);

        Assert.NotNull(page);
        var html = File.ReadAllText(page.DocumentPath);
        Assert.Equal(2, html.Split("<section class=\"slide\"").Length - 1);
        Assert.Contains("aspect-ratio:12192000 / 6858000", html, StringComparison.Ordinal);
        Assert.True(html.IndexOf("<h2>Opening</h2>", StringComparison.Ordinal) < html.IndexOf("<h2>Closing</h2>", StringComparison.Ordinal));
        Assert.Contains("<p>First point</p>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonZipFileHasNoPage()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("old.doc", "not a package");

        Assert.Null(OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Document, CancellationToken.None));
    }

    private static void WriteDocument(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                        xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
              <w:body>
                <w:p><w:pPr><w:pStyle w:val="Title"/></w:pPr><w:r><w:t>Quarterly report</w:t></w:r></w:p>
                <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Results</w:t></w:r></w:p>
                <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Revenue</w:t></w:r><w:r><w:t xml:space="preserve"> grew.</w:t></w:r></w:p>
                <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Q1</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>12</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
                <w:p><w:r><w:drawing><a:graphic><a:graphicData><a:blip r:embed="rId5"/></a:graphicData></a:graphic></w:drawing></w:r></w:p>
              </w:body>
            </w:document>
            """);
        Entry(archive, "word/_rels/document.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId5" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/image" Target="media/image1.png"/>
            </Relationships>
            """);
        var media = archive.CreateEntry("word/media/image1.png");
        using var stream = media.Open();
        stream.Write(Pixel);
    }

    private static void WriteWorkbook(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(archive, "xl/workbook.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets><sheet name="Budget" sheetId="1" r:id="rId1"/><sheet name="Notes" sheetId="2" r:id="rId2"/></sheets>
            </workbook>
            """);
        Entry(archive, "xl/_rels/workbook.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
            </Relationships>
            """);
        Entry(archive, "xl/sharedStrings.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><si><t>Item</t></si><si><t>Second sheet</t></si></sst>
            """);
        Entry(archive, "xl/worksheets/sheet1.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData>
                <row r="1"><c r="A1" t="s"><v>0</v></c><c r="C1"><v>42</v></c></row>
                <row r="2"><c r="A2" t="inlineStr"><is><t>Total</t></is></c></row>
              </sheetData>
            </worksheet>
            """);
        Entry(archive, "xl/worksheets/sheet2.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData><row r="1"><c r="A1" t="s"><v>1</v></c></row></sheetData>
            </worksheet>
            """);
    }

    private static void WriteDeck(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Entry(archive, "ppt/presentation.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst><p:sldId id="256" r:id="rId2"/><p:sldId id="257" r:id="rId3"/></p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000"/>
            </p:presentation>
            """);
        Entry(archive, "ppt/_rels/presentation.xml.rels", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
              <Relationship Id="rId3" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide2.xml"/>
            </Relationships>
            """);
        Entry(archive, "ppt/slides/slide1.xml", Slide("Opening", "First point"));
        Entry(archive, "ppt/slides/slide2.xml", Slide("Closing", "Last point"));
    }

    private static string Slide(string title, string body) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
               xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
          <p:cSld><p:spTree>
            <p:sp><p:nvSpPr><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{title}</a:t></a:r></a:p></p:txBody></p:sp>
            <p:sp><p:nvSpPr><p:nvPr><p:ph idx="1"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>{body}</a:t></a:r></a:p></p:txBody></p:sp>
          </p:spTree></p:cSld>
        </p:sld>
        """;

    private static void Entry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
