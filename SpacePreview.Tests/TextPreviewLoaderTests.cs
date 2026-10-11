using System.Globalization;
using System.Text;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class TextPreviewLoaderTests
{
    [Fact]
    public void AnsiCsvReadsInTheAnsiCodePage()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "export.csv");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var bytes = Encoding.GetEncoding(1252).GetBytes("Name,City,Price\r\nRenée,Zürich,£12.50 €3\r\n");
        File.WriteAllBytes(path, bytes);
        var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);

        var text = TextPreviewLoader.TryLoad(path)!.Text;

        Assert.DoesNotContain('�', text);
        Assert.Equal(ansi.GetString(bytes), text);
    }

    [Fact]
    public void Utf8CutMidCharacterByTheReadLimitStaysUtf8()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "long.txt");
        // One ASCII byte, then two-byte letters: the read limit (an even
        // byte count) ends on the first half of a letter.
        var text = "a" + new string('é', TextPreviewLoader.MaxPreviewBytes / 2 + 10);
        File.WriteAllText(path, text, new UTF8Encoding(false));

        var preview = TextPreviewLoader.TryLoad(path)!;

        Assert.True(preview.Truncated);
        Assert.StartsWith("aéé", preview.Text, StringComparison.Ordinal);
        Assert.DoesNotContain('�', preview.Text);
        Assert.DoesNotContain("Ã", preview.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ByteOrderMarkIsNotShownAsText()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "marked.txt");
        File.WriteAllText(path, "hello", new UTF8Encoding(true));

        Assert.Equal("hello", TextPreviewLoader.TryLoad(path)!.Text);
    }

    [Fact]
    public void JsonIsIndentedWithLettersAndMarkupKeptLiteral()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "min.json");
        File.WriteAllText(path, """{"name":"Renée <b> & co","note":"it's","price":1.10,"tags":["α"]}""", new UTF8Encoding(false));

        var text = TextPreviewLoader.TryLoad(path)!.Text;

        Assert.Contains("\n  \"name\": \"Renée <b> & co\",", text.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("\"note\": \"it's\"", text, StringComparison.Ordinal);
        Assert.Contains("\"price\": 1.10", text, StringComparison.Ordinal);
        Assert.Contains("\"α\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonThatDoesNotParseStaysAsWritten()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        const string source = "{ // a comment\n  \"a\": 1 }";
        File.WriteAllText(path, source);

        Assert.Equal(source, TextPreviewLoader.TryLoad(path)!.Text);
    }
}
