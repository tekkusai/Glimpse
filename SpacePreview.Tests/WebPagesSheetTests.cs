using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>The sheet page: its tabs take the workbook's names, whatever language the engine speaks.</summary>
public sealed class WebPagesSheetTests
{
    // LibreOffice writes the heading in its own interface language.
    [Theory]
    [InlineData("Sheet")]
    [InlineData("Tabelle")]
    [InlineData("Feuille")]
    [InlineData("シート")]
    public void TabsTakeTheSheetNamesInAnyEngineLanguage(string word)
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("book.html", $"""
            <!DOCTYPE html>
            <html>
            <head></head>
            <body>
            <h1>Overview</h1>
            <a href="#table0">{word} 1: <em>Haushalt &amp; Plan</em></a><br>
            <a href="#table1">{word} 2: <em>Notizen</em></a><br>
            <hr>
            <a name="table0"><h1>{word} 1: <em>Haushalt &amp; Plan</em></h1></a>
            <table cellspacing="0" border="0">
            	<colgroup width="124"></colgroup>
            	<tr><td>1</td></tr>
            </table>
            <a name="table1"><h1>{word} 2: <em>Notizen</em></h1></a>
            <table cellspacing="0" border="0">
            	<colgroup width="80"></colgroup>
            	<tr><td>2</td></tr>
            </table>
            </body>
            </html>
            """);

        SheetHtml.Normalize(path);

        var sheets = SheetHtml.TryReadSheetList(path);
        Assert.NotNull(sheets);
        Assert.Equal(["Haushalt & Plan", "Notizen"], sheets.Select(sheet => sheet.Name));
    }
}
