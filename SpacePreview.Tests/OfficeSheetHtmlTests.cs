using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>Calc's export, made into a sheet that follows the app's palette in both themes.</summary>
public sealed class OfficeSheetHtmlTests
{
    private const string Export = """
        <!DOCTYPE html>
        <html>
        <head>
        	<style type="text/css">
        		body,div,table,thead,tbody,tfoot,tr,th,td,p { font-family:"Calibri"; font-size:x-small }
        	</style>
        </head>
        <body>
        <h1>Sheet 1: <em>Budget &amp; plan</em></h1>
        <table cellspacing="0" border="0">
        	<colgroup width="124"></colgroup>
        	<colgroup span="2" width="68"></colgroup>
        	<tr><td>GTIN</td><td>SST</td><td><img src="https://tracker.example/pixel.png"></td></tr>
        	<tr><td>1</td><td>2</td><td>3</td></tr>
        </table>
        <h1>Sheet 2: <em>Notes</em></h1>
        <table cellspacing="0" border="0">
        	<colgroup width="80"></colgroup>
        	<tr><td>Second</td></tr>
        </table>
        </body>
        </html>
        """;

    [Fact]
    public void TheSheetTakesBothPalettesFromTheDesignTokens()
    {
        Assert.Contains($"--canvas:{Hex(Theme.Light.CanvasBg)}", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme:dark){:root{color-scheme:dark;", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains($"--canvas:{Hex(Theme.Dark.CanvasBg)}", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains($"--chrome:{Hex(Theme.Dark.ChromeBg)}", SheetHtml.Style, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSheetDissolvesInLikeEveryOtherPageAndStillsForReducedMotion()
    {
        Assert.Contains("html{animation:glimpse-in 160ms cubic-bezier(.2,0,0,1) backwards}", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains("@keyframes glimpse-in{from{opacity:0}}", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion:reduce){html{animation:none}}", SheetHtml.Style, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizeGivesEachSheetItsOwnPageWithStickyChromeAndNoRemoteImages()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("book.html", Export);

        SheetHtml.Normalize(path);

        var sheets = SheetHtml.TryReadSheetList(path);
        Assert.NotNull(sheets);
        Assert.Equal(["Budget & plan", "Notes"], sheets.Select(sheet => sheet.Name));
        var first = File.ReadAllText(sheets[0].Path);
        Assert.Contains("<td class=\"sp-h sp-cl\">A</td><td class=\"sp-h sp-cl\">B</td><td class=\"sp-h sp-cl\">C</td>", first, StringComparison.Ordinal);
        Assert.Contains("<td class=\"sp-h sp-rn\">2</td>", first, StringComparison.Ordinal);
        Assert.Contains("table-layout:fixed;width:300px", first, StringComparison.Ordinal);
        Assert.DoesNotContain("tracker.example", first, StringComparison.Ordinal);
        Assert.Contains("font-size:13px", first, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", first, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "A")]
    [InlineData(25, "Z")]
    [InlineData(26, "AA")]
    [InlineData(701, "ZZ")]
    [InlineData(702, "AAA")]
    public void ColumnLettersCountLikeASpreadsheet(int index, string letters) =>
        Assert.Equal(letters, SheetHtml.ColumnLetters(index));

    private static string Hex(System.Windows.Media.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
