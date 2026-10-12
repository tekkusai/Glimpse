using SpacePreview.Preview;
using SpacePreview.Utilities;
using System.IO.Compression;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace SpacePreview.Tests;

[Collection(ProcessEnvironmentCollection.Name)]
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
    public void ConversionProfileForcesHttpAndHttpsThroughAClosedLocalProxy()
    {
        using var temp = new TempDirectory();

        OfficeDocumentPdfConverter.PrepareNetworkIsolatedProfile(temp.Path);
        OfficeDocumentPdfConverter.PrepareNetworkIsolatedProfile(temp.Path);

        var settingsPath = Path.Combine(temp.Path, "user", "registrymodifications.xcu");
        var document = XDocument.Load(settingsPath);
        XNamespace registry = "http://openoffice.org/2001/registry";
        var values = document.Descendants("prop")
            .Where(property => (string?)property.Parent?.Attribute(registry + "path") == "/org.openoffice.Inet/Settings")
            .ToDictionary(
                property => (string)property.Attribute(registry + "name")!,
                property => property.Element("value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("2", values["ooInetProxyType"]);
        Assert.Equal("127.0.0.1", values["ooInetHTTPProxyName"]);
        Assert.Equal("9", values["ooInetHTTPProxyPort"]);
        Assert.Equal("127.0.0.1", values["ooInetHTTPSProxyName"]);
        Assert.Equal("9", values["ooInetHTTPSProxyPort"]);
        Assert.Equal(string.Empty, values["ooInetNoProxy"]);
    }

    [Fact]
    public void ConversionProcessCarriesAFailClosedProxyAndAPrivateProfile()
    {
        var startInfo = OfficeDocumentPdfConverter.BuildStartInfo(
            @"C:\LibreOffice\program\soffice.exe",
            @"C:\staging\report.docx",
            "pdf",
            @"C:\cache\entry",
            @"C:\data\LibreOfficeProfile");

        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
        {
            Assert.Equal("http://127.0.0.1:9", startInfo.Environment[key]);
        }

        Assert.Equal(string.Empty, startInfo.Environment["NO_PROXY"]);
        Assert.Equal(string.Empty, startInfo.Environment["no_proxy"]);
        // The engine's scratch goes with the run's staging folder, not into %TEMP%.
        Assert.Equal(@"C:\cache\entry\engine-tmp", startInfo.Environment["TMP"]);
        Assert.Equal(@"C:\cache\entry\engine-tmp", startInfo.Environment["TEMP"]);
        Assert.Contains("--headless", startInfo.ArgumentList);
        Assert.Contains("--nolockcheck", startInfo.ArgumentList);
        Assert.Contains("-env:UserInstallation=file:///C:/data/LibreOfficeProfile/", startInfo.ArgumentList);
        Assert.Equal(@"C:\staging\report.docx", startInfo.ArgumentList[^1]);
        Assert.Equal("pdf", startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--convert-to") + 1]);
        Assert.Equal(@"C:\cache\entry", startInfo.ArgumentList[startInfo.ArgumentList.IndexOf("--outdir") + 1]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void HyperlinkOnlyDocumentsReachTheEngineUntouched()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "links.docx");
        WriteLinkedImageDocument(source, "https://example.com/page", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink");

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(source, prepared.Path);
        Assert.Equal(0, prepared.ExternalRelationshipsRemoved);
    }

    [Theory]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/attachedTemplate")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/oleObject")]
    [InlineData("http://schemas.openxmlformats.org/officeDocument/2006/relationships/subDocument")]
    public void FetchableRelationshipsAreCutAndHyperlinksKept(string fetchableType)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "mixed.docx");
        WriteLinkedImageDocument(
            source,
            "https://example.com/template.dotx",
            fetchableType,
            extraRelationship: "<Relationship Id=\"rId9\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"https://example.com/\" TargetMode=\"External\"/>");

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(1, prepared.ExternalRelationshipsRemoved);
        var relationships = ReadRelationshipXml(prepared.Path);
        Assert.DoesNotContain(fetchableType, relationships, StringComparison.Ordinal);
        Assert.Contains("relationships/hyperlink", relationships, StringComparison.Ordinal);
    }

    [Fact]
    public void ARenamedDocumentIsSanitizedByItsContent()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "report-without-extension");
        WriteLinkedImageDocument(source, "http://127.0.0.1/pixel.png");

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(1, prepared.ExternalRelationshipsRemoved);
        Assert.DoesNotContain("TargetMode=\"External\"", ReadRelationshipXml(prepared.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyBinaryDocumentsPassThrough()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "old.doc");
        File.WriteAllBytes(source, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0]);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(source, prepared.Path);
    }

    [Theory]
    [InlineData("http://127.0.0.1/pixel.png")]
    [InlineData("file:///C:/Users/PreviewUser/pixel.png")]
    [InlineData(@"\\preview-server\share\pixel.png")]
    public void ConversionSourceRemovesExternalOpenXmlRelationships(string target)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "linked-image.docx");
        var staging = Path.Combine(temp.Path, "staging");
        WriteLinkedImageDocument(source, target);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(
            source,
            staging,
            CancellationToken.None);

        Assert.Equal(1, prepared.ExternalRelationshipsRemoved);
        Assert.NotEqual(source, prepared.Path);
        Assert.Contains("TargetMode=\"External\"", ReadRelationshipXml(source), StringComparison.Ordinal);
        Assert.DoesNotContain("TargetMode=\"External\"", ReadRelationshipXml(prepared.Path), StringComparison.Ordinal);
        Assert.True(File.Exists(source));
    }

    // The image link is cut before the engine sees it; the hyperlink reaches
    // the engine, which must leave it alone.
    [Theory]
    [InlineData("linked-image.docx", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image")]
    [InlineData("hyperlink.docx", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink")]
    public async Task ConversionNeverContactsALinkedHost(string name, string relationshipType)
    {
        if (!OfficeDocumentPdfConverter.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var path = Path.Combine(temp.Path, name);
        WriteLinkedImageDocument(path, $"http://127.0.0.1:{port}/pixel.png", relationshipType);
        using var observationCancellation = new CancellationTokenSource();
        var observation = ObserveConnectionAsync(listener, observationCancellation.Token);

        ConvertedOfficeDocument? converted;
        try
        {
            converted = await OfficeDocumentPdfConverter.Convert(path, OfficeKind.Document, CancellationToken.None).Complete;
            // A lingering soffice.bin could still reach out after the launcher returns.
            await Task.Delay(300);
        }
        finally
        {
            observationCancellation.Cancel();
        }

        Assert.NotNull(converted);
        Assert.False(await observation);
    }

    [Fact]
    public void ProgressivePlanUsesSlideCountForLargeDecks()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deck.pptx");
        WriteOfficeAppProperties(path, "Slides", 51);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Presentation);

        Assert.NotNull(plan);
        Assert.Equal(6, plan.FirstPageCount);
        Assert.Equal(51, plan.ExpectedPageCount);
        Assert.Contains("impress_pdf_Export", plan.Filter, StringComparison.Ordinal);
        Assert.Contains("1-6", plan.Filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ACountNestedPastTheLimitIsNotRead()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "deep.pptx");

        // Read with no depth bound, a value this deep can overflow the
        // stack; refused, the deck simply has no count.
        var depth = XmlNesting.MaxDepth + 50;
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            WriteEntry(
                archive,
                "docProps/app.xml",
                "<Properties>" + string.Concat(Enumerable.Repeat("<a>", depth)) + "<Slides>51</Slides>"
                    + string.Concat(Enumerable.Repeat("</a>", depth)) + "</Properties>");
        }

        Assert.Null(OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Presentation));

        path = Path.Combine(temp.Path, "shallow.pptx");
        WriteOfficeAppProperties(path, "Slides", 51);
        Assert.Equal(51, OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Presentation)?.ExpectedPageCount);
    }

    [Fact]
    public void ProgressivePlanAvoidsShortImageHeavyDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "contract.docx");
        WriteOfficeAppProperties(path, "Pages", 5, minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Document);

        Assert.Null(plan);
    }

    [Fact]
    public void ProgressivePlanRequiresBothSizeAndPageCountForDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "manual.docx");
        WriteOfficeAppProperties(path, "Pages", 60, minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Document);

        Assert.NotNull(plan);
        Assert.Equal(60, plan.ExpectedPageCount);
        Assert.Contains("writer_pdf_Export", plan.Filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgressivePlanReadsTheOpenDocumentPageCount()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "manual.odt");
        WriteOpenDocumentMeta(path, """<meta:document-statistic meta:page-count="60"/>""", minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Document);

        Assert.NotNull(plan);
        Assert.Equal(60, plan.ExpectedPageCount);
    }

    [Fact]
    public void ABigDeckWithoutACountPromisesNoNumber()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "talk.odp");
        WriteOpenDocumentMeta(path, """<meta:document-statistic meta:object-count="1699"/>""", minimumBytes: 1_600_000);

        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(path, OfficeKind.Presentation);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.ExpectedPageCount);
    }

    private static void WriteOpenDocumentMeta(string path, string statistic, int minimumBytes)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "meta.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-meta xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                                  xmlns:meta="urn:oasis:names:tc:opendocument:xmlns:meta:1.0">
              <office:meta>{statistic}</office:meta>
            </office:document-meta>
            """);
        var filler = archive.CreateEntry("Pictures/filler.bin", CompressionLevel.NoCompression);
        using var stream = filler.Open();
        stream.Write(new byte[minimumBytes]);
    }

    internal static void WriteOfficeAppProperties(string path, string propertyName, int value, int minimumBytes = 0)
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

    private static async Task<bool> ObserveConnectionAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var stream = client.GetStream();
            var request = new byte[2048];
            _ = await stream.ReadAsync(request, cancellationToken);
            var response = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();
            await stream.WriteAsync(response, cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    internal static void WriteLinkedImageDocument(
        string path,
        string target,
        string type = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image",
        string extraRelationship = "")
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        WriteEntry(archive, "[Content_Types].xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
              <Default Extension="xml" ContentType="application/xml"/>
              <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
            </Types>
            """);
        WriteEntry(archive, "_rels/.rels", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
            </Relationships>
            """);
        WriteEntry(archive, "word/document.xml", """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                        xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"
                        xmlns:wp="http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing"
                        xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                        xmlns:pic="http://schemas.openxmlformats.org/drawingml/2006/picture">
              <w:body>
                <w:p><w:r><w:t>Linked image preview</w:t></w:r></w:p>
                <w:p><w:r><w:drawing><wp:inline>
                  <wp:extent cx="9525" cy="9525"/><wp:docPr id="1" name="Linked image"/>
                  <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/picture">
                    <pic:pic><pic:nvPicPr><pic:cNvPr id="0" name="pixel"/><pic:cNvPicPr/></pic:nvPicPr>
                    <pic:blipFill><a:blip r:link="rId1"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>
                    <pic:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="9525" cy="9525"/></a:xfrm><a:prstGeom prst="rect"><a:avLst/></a:prstGeom></pic:spPr></pic:pic>
                  </a:graphicData></a:graphic>
                </wp:inline></w:drawing></w:r></w:p>
                <w:sectPr><w:pgSz w:w="12240" w:h="15840"/><w:pgMar w:top="1440" w:right="1440" w:bottom="1440" w:left="1440"/></w:sectPr>
              </w:body>
            </w:document>
            """);
        WriteEntry(archive, "word/_rels/document.xml.rels", $$"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="{{type}}" Target="{{target}}" TargetMode="External"/>
              {{extraRelationship}}
            </Relationships>
            """);
    }

    private static void WriteEntry(ZipArchive archive, string name, string contents)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(contents);
    }

    private static string ReadRelationshipXml(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/_rels/document.xml.rels")!;
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
