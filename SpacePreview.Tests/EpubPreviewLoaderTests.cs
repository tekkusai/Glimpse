using System.IO.Compression;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class EpubPreviewLoaderTests
{
    [Fact]
    public void EpubFilesUseDedicatedPreviewKind()
    {
        Assert.True(FileKinds.IsEpub("book.epub"));
    }

    [Fact]
    public void TryBuildBodyReadsMetadataTocChapterAndLocalImages()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "book.epub");
        CreateMinimalEpub(path);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("Sample Book", body!, StringComparison.Ordinal);
        Assert.Contains("Writer", body, StringComparison.Ordinal);
        Assert.Contains("Chapter One", body, StringComparison.Ordinal);
        Assert.Contains("Hello from the book", body, StringComparison.Ordinal);
        Assert.Contains("Chapter Two", body, StringComparison.Ordinal);
        Assert.Contains("The second chapter should render too.", body, StringComparison.Ordinal);
        Assert.Contains("class=\"cover\"", body, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>Chapter One</h1>", body, StringComparison.Ordinal);
        Assert.Contains("<h2>Chapter One</h2>", body, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert(", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryBuildBodyTreatsEncryptedEpubAsProtectedBook()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "protected.epub");
        CreateMinimalEpub(path, encrypted: true);

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("Sample Book", body!, StringComparison.Ordinal);
        Assert.Contains("Protected book.", body, StringComparison.Ordinal);
        Assert.Contains("class=\"cover\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Hello from the book", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildBodyShowsHonestTruncationForLongBooks()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "long.epub");
        CreateMinimalEpub(path, chapter2Text: new string('x', 510_000) + "TAIL");

        var body = EpubPreviewLoader.TryBuildBody(path);

        Assert.NotNull(body);
        Assert.Contains("Showing the beginning of this book. Open the book for the rest.", body!, StringComparison.Ordinal);
        Assert.DoesNotContain("TAIL", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TryGetChaptersReturnsSpineOrderTitlesAndLazyBodies()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "book.epub");
        CreateMinimalEpub(path);

        var chapters = EpubPreviewLoader.TryGetChapters(path);

        Assert.NotNull(chapters);
        Assert.Collection(chapters!,
            first =>
            {
                Assert.Equal(0, first.SpineIndex);
                Assert.Equal("Chapter One", first.Title);
                Assert.EndsWith("chapter1.xhtml", first.EntryPath, StringComparison.Ordinal);
            },
            second =>
            {
                Assert.Equal(1, second.SpineIndex);
                Assert.Equal("Chapter Two", second.Title);
                Assert.EndsWith("chapter2.xhtml", second.EntryPath, StringComparison.Ordinal);
                var body = second.TryRenderBody();
                Assert.NotNull(body);
                Assert.Contains("The second chapter should render too.", body!, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void TryGetChaptersFallsBackToFileNameWhenNavigationIsMissing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "book.epub");
        CreateMinimalEpub(path, includeNav: false);

        var chapters = EpubPreviewLoader.TryGetChapters(path);

        Assert.NotNull(chapters);
        Assert.Equal("chapter1", chapters![0].Title);
        Assert.Equal("chapter2", chapters[1].Title);
    }

    [Fact]
    public void TryGetChaptersReturnsNoChaptersForProtectedBooks()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "protected.epub");
        CreateMinimalEpub(path, encrypted: true);

        var chapters = EpubPreviewLoader.TryGetChapters(path);

        Assert.NotNull(chapters);
        Assert.Empty(chapters!);
    }

    [Fact]
    public void GeneratedEpubCacheChangesWhenContentChangesButModifiedTimeDoesNot()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "changing.epub");
        CreateMinimalEpub(path, chapter2Text: "AAAA");
        var modified = File.GetLastWriteTimeUtc(path);
        var first = GeneratedHtmlPreview.TryBuildEpub(path);

        File.Delete(path);
        CreateMinimalEpub(path, chapter2Text: "BBBB");
        File.SetLastWriteTimeUtc(path, modified);
        var second = GeneratedHtmlPreview.TryBuildEpub(path);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.HtmlPath, second!.HtmlPath);
        Assert.Contains("BBBB", File.ReadAllText(second.HtmlPath), StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedEpubCacheTokenHonorsCancellation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cancelled.epub");
        CreateMinimalEpub(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GeneratedHtmlPreview.TryBuildEpub(path, cancellation.Token));
    }

    [Fact]
    public void TryBuildBodyRethrowsCancellation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cancelled-body.epub");
        CreateMinimalEpub(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            EpubPreviewLoader.TryBuildBody(path, cancellation.Token));
    }

    [Fact]
    public void GeneratedEpubPassesCancellationIntoBodyExtraction()
    {
        var method = TestSource.CodeMethod(
            @"SpacePreview\Preview\GeneratedHtmlPreview.cs",
            "TryBuildEpub");

        Assert.Contains("TryBuildBody(path, cancellationToken)", method, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedEpubCacheTokenUsesZipDirectoryChecksumsInsteadOfPayloadReads()
    {
        var method = TestSource.CodeMethod(
            @"SpacePreview\Preview\GeneratedHtmlPreview.cs",
            "StableEpubContentToken");

        Assert.Contains("entry.Crc32", method, StringComparison.Ordinal);
        Assert.Contains("entry.CompressedLength", method, StringComparison.Ordinal);
        Assert.DoesNotContain("stream.Read", method, StringComparison.Ordinal);
    }

    private static void CreateMinimalEpub(
        string path,
        bool encrypted = false,
        string chapter2Text = "The second chapter should render too.",
        bool includeNav = true)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        WriteEntry(archive, "mimetype", "application/epub+zip");
        if (encrypted)
        {
            WriteEntry(archive, "META-INF/encryption.xml", """
<?xml version="1.0"?>
<encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container"/>
""");
        }

        WriteEntry(archive, "META-INF/container.xml", """
<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
""");
        WriteEntry(archive, "OEBPS/content.opf", """
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="3.0">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:title>Sample Book</dc:title>
    <dc:creator>Writer</dc:creator>
  </metadata>
  <manifest>
    <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>
    <item id="c1" href="chapter1.xhtml" media-type="application/xhtml+xml"/>
    <item id="c2" href="chapter2.xhtml" media-type="application/xhtml+xml"/>
    <item id="cover" href="cover.png" media-type="image/png" properties="cover-image"/>
  </manifest>
  <spine>
    <itemref idref="c1"/>
    <itemref idref="c2"/>
  </spine>
</package>
""");
        if (includeNav)
        {
            WriteEntry(archive, "OEBPS/nav.xhtml", """
<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <body>
    <nav epub:type="toc" xmlns:epub="http://www.idpf.org/2007/ops">
      <ol>
        <li><a href="chapter1.xhtml">Chapter One</a></li>
        <li><a href="chapter2.xhtml">Chapter Two</a></li>
      </ol>
    </nav>
  </body>
</html>
""");
        }

        WriteEntry(archive, "OEBPS/chapter1.xhtml", """
<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <body>
    <h1>Chapter One</h1>
    <p>Hello from the book.</p>
    <script>alert('no');</script>
    <p><img src="cover.png" alt="Cover image"/></p>
  </body>
</html>
""");
        WriteEntry(archive, "OEBPS/chapter2.xhtml", """
<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <body>
    <p>
""" + chapter2Text + """
</p>
  </body>
</html>
""");
        var image = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII=");
        var entry = archive.CreateEntry("OEBPS/cover.png");
        using var imageStream = entry.Open();
        imageStream.Write(image);
    }

    private static void WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(text);
    }
}
