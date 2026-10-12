using System.IO.Compression;
using System.Net;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A book opens whenever its package can be read. The XHTML entity names
/// real books are full of resolve, and a part that cannot be read costs the
/// preview that part and one line saying so.
/// </summary>
public sealed class EpubResilienceTests
{
    private const string PartNotice = "Part of this book can't be shown here. Open the book to read all of it.";
    private const string NoTextNotice = "This book's text can't be shown here. Open the book to read it.";

    // What Sigil writes by default, and what EPUB 2 allows.
    [Fact]
    public void XhtmlEntityNamesBecomeTheirCharacters()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(
            temp,
            title: "Caf&eacute; Stories",
            navTitle: "One&nbsp;&mdash;&nbsp;Start",
            chapters:
            [
                Xhtml("<p>Fish&nbsp;&amp;&nbsp;chips &mdash; caf&eacute; &hellip; &copy; &lsquo;quoted&rsquo; &lt;tag&gt; &#8212; &bogus;</p>"
                    + "<p><img src=\"missing.png\" alt=\"a&nbsp;b\"/></p>")
            ]);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        var text = WebUtility.HtmlDecode(body);
        Assert.Contains("Café Stories", text, StringComparison.Ordinal);
        Assert.Contains("Fish & chips — café … © ‘quoted’ <tag> —", text, StringComparison.Ordinal);
        Assert.Contains("a b", text, StringComparison.Ordinal);
        // A name XHTML does not define shows as written, as in a browser.
        Assert.Contains("&amp;bogus;", body, StringComparison.Ordinal);
        // A title collapses its spaces, the hard ones too.
        Assert.Contains("data-title=\"One — Start\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("epub-unreadable", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AChapterThatIsNotXmlCostsOnlyItselfAndIsSaidOnce()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(
            temp,
            chapters:
            [
                Xhtml("<p>First chapter.</p>"),
                Xhtml("<p>Second chapter, never closed."),
                Xhtml("<p>Third chapter.</p>"),
                "not a document at all",
                Xhtml("<p>Fifth chapter.</p>")
            ]);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("First chapter.", body, StringComparison.Ordinal);
        Assert.Contains("Third chapter.", body, StringComparison.Ordinal);
        Assert.Contains("Fifth chapter.", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Second chapter", body, StringComparison.Ordinal);
        Assert.Equal(1, body.Split(PartNotice).Length - 1);
        // Said above the text. The sections keep their places in the spine,
        // so the rail's anchors still land on what the page holds.
        Assert.True(body.IndexOf("epub-unreadable", StringComparison.Ordinal) < body.IndexOf("First chapter.", StringComparison.Ordinal));
        Assert.Equal(
            ["ch-0", "ch-2", "ch-4"],
            System.Text.RegularExpressions.Regex.Matches(body, "<section id=\"(ch-\\d+)\"").Select(match => match.Groups[1].Value));
    }

    // text/html is allowed in a spine. One that is really XHTML reads; one
    // that is HTML proper (an unclosed br) is skipped, and the book stays.
    [Fact]
    public void AnHtmlTypedChapterDoesNotTakeTheBookDown()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(
            temp,
            chapters:
            [
                Xhtml("<p>An XHTML chapter.</p>"),
                "<html><body><p>Loose HTML<br>with an open break</body></html>",
                "<html><body><p>Tidy HTML<br/>that is also XML.</p></body></html>"
            ],
            mediaType: index => index == 0 ? "application/xhtml+xml" : "text/html");

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("An XHTML chapter.", body, StringComparison.Ordinal);
        Assert.Contains("Tidy HTML", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Loose HTML", body, StringComparison.Ordinal);
        Assert.Contains(PartNotice, body, StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenNavigationDocumentCostsOnlyTheTitles()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(
            temp,
            chapters: [Xhtml("<p>First chapter.</p>"), Xhtml("<p>Second chapter.</p>")],
            nav: "<html><body><nav><ol><li><a href=\"c0.xhtml\">Broken",
            ncx: "<ncx><navMap><navPoint>");

        var page = GeneratedHtmlPreview.TryBuildEpub(path);

        Assert.NotNull(page);
        var html = File.ReadAllText(page.HtmlPath);
        Assert.Contains("First chapter.", html, StringComparison.Ordinal);
        Assert.Contains("Second chapter.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("epub-unreadable", html, StringComparison.Ordinal);
        // With no navigation to name them, every section is listed by its file.
        Assert.Equal(["c0", "c1"], EpubPreviewLoader.ReadContents(page.HtmlPath).Sections.Select(section => section.Title));
    }

    [Fact]
    public void ABrokenContainerFileFallsBackToThePackageInTheBook()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(temp, chapters: [Xhtml("<p>First chapter.</p>")], container: "<container><rootfiles>");

        Assert.Contains("First chapter.", EpubPreviewLoader.TryBuildBody(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ABookWithNoReadableChapterStillOpensAndSaysSo()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(temp, title: "Loose Leaves", chapters: ["<html><body><p>One<br>", "<html><body><p>Two<br>"]);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("Loose Leaves", body, StringComparison.Ordinal);
        Assert.Contains(NoTextNotice, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PartNotice, body, StringComparison.Ordinal);
    }

    // The bounds stay: a chapter over the size cap is one that cannot be read.
    [Fact]
    public void AChapterPastTheSizeCapIsSkippedLikeAnyOtherItCannotRead()
    {
        using var temp = new TempDirectory();
        var path = WriteBook(
            temp,
            chapters: [Xhtml("<p>First chapter.</p>"), Xhtml($"<p>{new string('x', 5 * 1024 * 1024)}</p>")]);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("First chapter.", body, StringComparison.Ordinal);
        Assert.Contains("epub-unreadable", body, StringComparison.Ordinal);
        Assert.True(body.Length < 100_000);
    }

    [Theory]
    [InlineData("a&nbsp;b", "a&#160;b")]
    [InlineData("&nbsp;", "&#160;")]
    [InlineData("&amp;&lt;&gt;&quot;&apos;", "&amp;&lt;&gt;&quot;&apos;")]
    [InlineData("&#160;&#xA0;", "&#160;&#xA0;")]
    [InlineData("&Eacute;&eacute;&thetasym;", "&#201;&#233;&#977;")]
    [InlineData("&bogus;", "&amp;bogus;")]
    [InlineData("AT&T; R&D &; & ;", "AT&amp;T; R&D &; & ;")]
    [InlineData("<!-- &nbsp; --><p>&nbsp;</p>", "<!-- &nbsp; --><p>&#160;</p>")]
    [InlineData("<![CDATA[&nbsp;]]>&nbsp;", "<![CDATA[&nbsp;]]>&#160;")]
    [InlineData("<!-- never closed &nbsp;", "<!-- never closed &#160;")]
    [InlineData("<a title=\"x&nbsp;y\">", "<a title=\"x&#160;y\">")]
    [InlineData("&nbsp", "&nbsp")]
    [InlineData("", "")]
    public void OnlyNamesBecomeReferences(string xml, string expected) =>
        Assert.Equal(expected, Encoding.UTF8.GetString(EpubPreviewLoader.ResolveNamedEntities(Encoding.UTF8.GetBytes(xml))));

    [Fact]
    public void TextThatIsNotAsciiCompatibleIsLeftForTheParser()
    {
        foreach (var encoding in new Encoding[] { Encoding.Unicode, Encoding.BigEndianUnicode, new UnicodeEncoding(false, false), Encoding.UTF32 })
        {
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes("<p>a&nbsp;b</p>")).ToArray();
            Assert.Equal(bytes, EpubPreviewLoader.ResolveNamedEntities(bytes));
        }

        // UTF-8 with its mark is read like UTF-8 without.
        var marked = Encoding.UTF8.GetPreamble().Concat("<p>a&nbsp;b é</p>"u8.ToArray()).ToArray();
        Assert.EndsWith("<p>a&#160;b é</p>", Encoding.UTF8.GetString(EpubPreviewLoader.ResolveNamedEntities(marked)), StringComparison.Ordinal);
    }

    private static string Xhtml(string body) =>
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<!DOCTYPE html PUBLIC \"-//W3C//DTD XHTML 1.1//EN\" \"http://www.w3.org/TR/xhtml11/DTD/xhtml11.dtd\">\n"
        + $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>t</title></head><body>{body}</body></html>";

    /// <summary>A book of chapters c0.xhtml, c1.xhtml and so on, each written as given.</summary>
    private static string WriteBook(
        TempDirectory temp,
        string[] chapters,
        string title = "Book",
        string navTitle = "One",
        string? nav = null,
        string? ncx = null,
        string? container = null,
        Func<int, string>? mediaType = null)
    {
        var path = Path.Combine(temp.Path, $"{Guid.NewGuid():N}.epub");
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        Write(archive, "mimetype", "application/epub+zip");
        Write(archive, "META-INF/container.xml", container ?? """
            <?xml version="1.0"?>
            <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
              <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles>
            </container>
            """);

        var manifest = new StringBuilder();
        var spine = new StringBuilder();
        for (var index = 0; index < chapters.Length; index++)
        {
            manifest.Append($"<item id=\"c{index}\" href=\"c{index}.xhtml\" media-type=\"{mediaType?.Invoke(index) ?? "application/xhtml+xml"}\"/>");
            spine.Append($"<itemref idref=\"c{index}\"/>");
            Write(archive, $"c{index}.xhtml", chapters[index]);
        }

        manifest.Append("<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>");
        Write(archive, "nav.xhtml", nav ?? Xhtml($"<nav><ol><li><a href=\"c0.xhtml\">{navTitle}</a></li></ol></nav>"));
        if (ncx is not null)
        {
            manifest.Append("<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>");
            Write(archive, "toc.ncx", ncx);
        }

        Write(archive, "content.opf",
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
            + "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"2.0\">"
            + $"<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>{title}</dc:title></metadata>"
            + $"<manifest>{manifest}</manifest><spine{(ncx is null ? string.Empty : " toc=\"ncx\"")}>{spine}</spine></package>");
        return path;
    }

    private static void Write(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }
}
