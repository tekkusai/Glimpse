using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The text-only page never cuts a file without saying so. Documents and
/// decks already said it; a workbook lost sheets, rows and columns in
/// silence. And its colors are the page's own tokens, so a deck open when
/// the theme changes turns with it.
/// </summary>
public sealed class OfficeFallbackCutTests
{
    [Theory]
    [InlineData(1, 30, 20, "Showing the first 4,000,000 of 6,000,000 characters. Open the file for the rest.")]
    [InlineData(3, 15, 10, "Showing the first 1,000,000 of 1,500,000 characters. Open the file for the rest.")]
    public void SharedStringsHaveOneTextBudgetForTheWorkbook(int sheets, int rows, int columns, string notice)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "repeated.xlsx");
        var cells = new StringBuilder();
        for (var row = 1; row <= rows; row++)
        {
            cells.Append($"<row r=\"{row}\">");
            for (var column = 0; column < columns; column++)
            {
                cells.Append($"<c r=\"{SheetHtml.ColumnLetters(column)}{row}\" t=\"s\"><v>0</v></c>");
            }
            cells.Append("</row>");
        }

        WriteWorkbook(path, sheets, cells.ToString());
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            Entry(archive, "xl/sharedStrings.xml", $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>{new string('x', 10_000)}</t></si></sst>");
            for (var sheet = 2; sheet <= sheets; sheet++)
            {
                archive.GetEntry($"xl/worksheets/sheet{sheet}.xml")!.Delete();
                Entry(archive, $"xl/worksheets/sheet{sheet}.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{cells}</sheetData></worksheet>");
            }
        }

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Spreadsheet, CancellationToken.None);
        Assert.NotNull(page);
        var pages = (page.Sheets?.Select(sheet => sheet.Path) ?? [page.DocumentPath])
            .Select(File.ReadAllText).ToArray();
        var text = pages.Sum(html => Regex.Matches(html, "<td(?: [^>]*)?>(x*)</td>")
            .Sum(cell => cell.Groups[1].Length));

        Assert.Equal(4_000_000, text);
        Assert.Contains(">" + notice + "</p>", pages[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkbookSaysWhichSheetsRowsAndColumnsItLeftOut()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "big.xlsx");
        // Sheet 1 has a value in row 600 and one in column BH, the 60th.
        WriteWorkbook(path, sheets: 14, firstSheet: """
            <row r="1"><c r="A1" t="inlineStr"><is><t>Top</t></is></c><c r="BH1"><v>7</v></c></row>
            <row r="600"><c r="A600"><v>42</v></c></row>
            """);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Spreadsheet, CancellationToken.None);

        Assert.NotNull(page);
        Assert.NotNull(page.Sheets);
        Assert.Equal(12, page.Sheets.Count);
        var first = File.ReadAllText(page.Sheets[0].Path);
        Assert.Contains(">Showing the first 500 of 600 rows. Open the file for the rest.</p>", first, StringComparison.Ordinal);
        Assert.Contains(">Showing the first 52 of 60 columns. Open the file for the rest.</p>", first, StringComparison.Ordinal);
        Assert.Contains(">Showing the first 12 of 14 sheets. Open the file for the rest.</p>", first, StringComparison.Ordinal);
        Assert.Equal(first, File.ReadAllText(page.DocumentPath));

        // A sheet that fits says only what the tabs cannot.
        var second = File.ReadAllText(page.Sheets[1].Path);
        Assert.Contains(">Showing the first 12 of 14 sheets. Open the file for the rest.</p>", second, StringComparison.Ordinal);
        Assert.DoesNotContain(" rows. Open", second, StringComparison.Ordinal);
        Assert.DoesNotContain(" columns. Open", second, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkbookThatFitsSaysNothing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "small.xlsx");
        // Formatting reaches far past the values, as it does in real sheets.
        WriteWorkbook(path, sheets: 2, firstSheet: """
            <row r="1"><c r="A1"><v>1</v></c><c r="CA1" s="3"/></row>
            <row r="500"><c r="AZ500"><v>2</v></c></row>
            <row r="90000" ht="20"/>
            """);

        var page = OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Spreadsheet, CancellationToken.None);

        Assert.NotNull(page);
        Assert.DoesNotContain("Showing the first", File.ReadAllText(page.DocumentPath), StringComparison.Ordinal);
    }

    [Fact]
    public void DeckCardsAndTableLinesTakeTheirColorsFromThePage()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            Entry(archive, "ppt/presentation.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                                xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:sldIdLst><p:sldId id="256" r:id="rId2"/></p:sldIdLst>
                </p:presentation>
                """);
            Entry(archive, "ppt/_rels/presentation.xml.rels", """
                <?xml version="1.0" encoding="UTF-8"?>
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide1.xml"/>
                </Relationships>
                """);
            Entry(archive, "ppt/slides/slide1.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Only slide</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld>
                </p:sld>
                """);
        }

        var html = File.ReadAllText(OfficeOpenXmlPreview.TryBuild(path, OfficeKind.Presentation, CancellationToken.None)!.DocumentPath);

        // A color written as a number is this moment's theme, and stays
        // behind when the theme changes under an open preview.
        Assert.Contains(".deck .slide{background:var(--raised);border:1px solid var(--raised-border);", html, StringComparison.Ordinal);
        Assert.Contains(".deck .slide-number{margin:0 0 8px;font-size:11.5px;color:var(--text-3)}", html, StringComparison.Ordinal);
        Assert.Contains(".office td{border:1px solid var(--raised-border)}", html, StringComparison.Ordinal);
    }

    /// <summary>A workbook of <paramref name="sheets"/> sheets; the first holds <paramref name="firstSheet"/>, the others one cell.</summary>
    private static void WriteWorkbook(string path, int sheets, string firstSheet)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var list = new StringBuilder();
        var relationships = new StringBuilder();
        for (var number = 1; number <= sheets; number++)
        {
            list.Append($"<sheet name=\"Sheet {number}\" sheetId=\"{number}\" r:id=\"rId{number}\"/>");
            relationships.Append($"<Relationship Id=\"rId{number}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{number}.xml\"/>");
            Entry(archive, $"xl/worksheets/sheet{number}.xml", $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>{(number == 1 ? firstSheet : "<row r=\"1\"><c r=\"A1\"><v>1</v></c></row>")}</sheetData>
                </worksheet>
                """);
        }

        Entry(archive, "xl/workbook.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>{list}</sheets>
            </workbook>
            """);
        Entry(archive, "xl/_rels/workbook.xml.rels", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{relationships}</Relationships>
            """);
    }

    private static void Entry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
