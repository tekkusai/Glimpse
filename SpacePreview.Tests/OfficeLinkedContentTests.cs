using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A preview never reads what a file links to. A picture linked by path
/// can sit on a share, and reading it there starts a Windows sign-in to
/// that host. OpenDocument links are cut from a private copy; the engine's
/// profile stops the links in files that cannot be cut. The engine tests
/// link to a local picture, which takes the same road without the network.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class OfficeLinkedContentTests
{
    private const string TextBody = "office:text";
    private const string SheetBody = "office:spreadsheet";

    [Fact]
    public void TheProfileBlocksTheLinksOfEveryDocument()
    {
        using var temp = new TempDirectory();

        OfficeDocumentPdfConverter.PrepareNetworkIsolatedProfile(temp.Path);
        OfficeDocumentPdfConverter.PrepareNetworkIsolatedProfile(temp.Path);

        XNamespace registry = "http://openoffice.org/2001/registry";
        var switches = XDocument.Load(Path.Combine(temp.Path, "user", "registrymodifications.xcu"))
            .Descendants("prop")
            .Where(property => (string?)property.Parent?.Attribute(registry + "path") == "/org.openoffice.Office.Common/Security/Scripting")
            .Select(property => ((string?)property.Attribute(registry + "name"), property.Element("value")?.Value))
            .ToList();

        Assert.Equal([("BlockUntrustedRefererLinks", "true")], switches);
    }

    [Theory]
    [InlineData("file:///C:/Users/PreviewUser/pixel.png")]
    [InlineData("http://127.0.0.1/pixel.png")]
    [InlineData(@"\\preview-server\share\pixel.png")]
    [InlineData("//preview-server/share/pixel.png")]
    [InlineData("/C:/Users/PreviewUser/pixel.png")]
    [InlineData(@"C:\Users\PreviewUser\pixel.png")]
    [InlineData("../pixel.png")]
    [InlineData("Pictures/../../pixel.png")]
    [InlineData("%2e%2e/pixel.png")]
    [InlineData("  file:///C:/pixel.png")]
    public void AReferenceOutOfThePackageIsALink(string reference) =>
        Assert.True(OfficePreviewSourceSanitizer.LeavesPackage(reference));

    [Theory]
    [InlineData("Pictures/100000010000.png")]
    [InlineData("./Object 1")]
    [InlineData("#Sheet2.A1")]
    [InlineData("..")]
    [InlineData("")]
    public void AReferenceIntoThePackageIsNot(string reference) =>
        Assert.False(OfficePreviewSourceSanitizer.LeavesPackage(reference));

    [Fact]
    public void OpenDocumentLinksAreCutAndHyperlinksKept()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "linked.odt");
        WriteOpenDocument(
            source,
            TextBody,
            """
            <text:p>Read <text:a xlink:type="simple" xlink:href="https://example.com/more">more</text:a> &amp; see.</text:p>
            <text:p><draw:frame><draw:image xlink:href="\\preview-server\share\pixel.png" xlink:type="simple"/></draw:frame></text:p>
            <text:p><draw:frame><draw:image xlink:href="Pictures/own.png" xlink:type="simple"/></draw:frame></text:p>
            <text:section text:name="Linked"><text:section-source xlink:href="file:///C:/other.odt"/><text:p>Kept  words</text:p></text:section>
            """);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(2, prepared.ExternalRelationshipsRemoved);
        Assert.NotEqual(source, prepared.Path);
        using var archive = ZipFile.OpenRead(prepared.Path);
        var content = Read(archive, "content.xml");
        Assert.DoesNotContain("preview-server", content, StringComparison.Ordinal);
        Assert.DoesNotContain("other.odt", content, StringComparison.Ordinal);
        Assert.Contains("xlink:href=\"https://example.com/more\"", content, StringComparison.Ordinal);
        Assert.Contains("xlink:href=\"Pictures/own.png\"", content, StringComparison.Ordinal);
        Assert.Contains("<text:p>Kept  words</text:p>", content, StringComparison.Ordinal);
        Assert.Contains("more</text:a> &amp; see.", content, StringComparison.Ordinal);

        // The engine knows the package by its first entry, stored as it is.
        var first = archive.Entries[0];
        Assert.Equal("mimetype", first.FullName);
        Assert.Equal(first.Length, first.CompressedLength);

        // The user's own file is never the one that is cut.
        using var original = ZipFile.OpenRead(source);
        Assert.Contains("preview-server", Read(original, "content.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOpenDocumentWithoutLinksReachesTheEngineUntouched()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "plain.odt");
        WriteOpenDocument(source, TextBody, """<text:p><text:a xlink:href="https://example.com/">A hyperlink only</text:a></text:p>""");

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(source, prepared.Path);
        Assert.Equal(0, prepared.ExternalRelationshipsRemoved);
    }

    // A part this reader refuses could still be read by the engine, links
    // and all, so the file is not handed over.
    [Fact]
    public void AnOpenDocumentThisReaderRefusesNeverReachesTheEngine()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "doctype.odt");
        WriteOpenDocument(source, TextBody, "<text:p>Words</text:p>", prolog: "<!DOCTYPE office:document-content [<!ENTITY x \"y\">]>");

        Assert.Throws<XmlException>(() =>
            OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None));
    }

    [Fact]
    public void TheProfileKeepsALinkedPictureOutOfADocumentTheEngineReadsUntouched()
    {
        if (FindEngine() is not { } soffice)
        {
            return;
        }

        using var temp = new TempDirectory();
        var picture = WriteNoisePicture(temp.Path);
        var source = Path.Combine(temp.Path, "linked.odt");
        WriteOpenDocument(source, TextBody, $"""<text:p>Linked picture</text:p><text:p>{LinkedPicture(picture, "text:anchor-type=\"as-char\"")}</text:p>""");

        var pdf = ConvertToPdf(soffice, source, temp.Path);

        Assert.False(HoldsAPicture(pdf));
    }

    // The engine's switch does not reach a picture on a sheet, so the cut is
    // what keeps it out.
    [Fact]
    public void ALinkedPictureOnASheetIsCutBeforeTheEngineSeesIt()
    {
        if (FindEngine() is not { } soffice)
        {
            return;
        }

        using var temp = new TempDirectory();
        var picture = WriteNoisePicture(temp.Path);
        var source = Path.Combine(temp.Path, "linked.ods");
        WriteOpenDocument(
            source,
            SheetBody,
            $"""
            <table:table table:name="Sheet1"><table:shapes>{LinkedPicture(picture, "svg:x=\"1cm\" svg:y=\"1cm\"")}</table:shapes>
            <table:table-column/><table:table-row><table:table-cell office:value-type="string"><text:p>Linked picture</text:p></table:table-cell></table:table-row></table:table>
            """);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);
        var pdf = ConvertToPdf(soffice, prepared.Path, temp.Path);

        Assert.Equal(1, prepared.ExternalRelationshipsRemoved);
        Assert.False(HoldsAPicture(pdf));
    }

    [Fact]
    public void RtfFetchableFieldsLinkedObjectsAndTemplatesAreCutAndHyperlinksKept()
    {
        using var temp = new TempDirectory();
        // Renamed as .doc on purpose: RTF sniffing protects disguised .doc files too.
        var source = Path.Combine(temp.Path, "linked.doc");
        var rtf =
            @"{\rtf1\ansi\deff0"
            + @"{\*\template \\preview-server\share\evil.dotm}"
            + @"Hello {\field{\*\fldinst HYPERLINK ""https://example.com/safe""}{\fldrslt safe link}}."
            + @"{\field{\*\fldinst INCLUDEPICTURE ""\\\\preview-server\\share\\pixel.png""}{\fldrslt }}"
            + @"{\field{\*\fldinst \'49NCLUDETEXT ""C:\\secret.txt""}{\fldrslt }}"
            + @"{\field{\*\fldinst {\f0 DDEAUTO c:\\windows\\system32\\cmd.exe ""/k calc""}}{\fldrslt }}"
            + @"{\object\objlink{\*\objclass Package}{\*\objdata 0105}}"
            + @"{\pict\bin4}{{} Keep after bin}"
            + @" Tail text.}";
        File.WriteAllText(source, rtf, Encoding.ASCII);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(5, prepared.ExternalRelationshipsRemoved);
        Assert.NotEqual(source, prepared.Path);
        var sanitized = File.ReadAllText(prepared.Path, Encoding.ASCII);
        Assert.DoesNotContain("evil.dotm", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("INCLUDEPICTURE", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.txt", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("DDEAUTO", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\objlink", sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://example.com/safe", sanitized, StringComparison.Ordinal);
        Assert.Contains("safe link", sanitized, StringComparison.Ordinal);
        Assert.Contains("Keep after bin", sanitized, StringComparison.Ordinal);
        Assert.Contains("Tail text.", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void AnRtfWithoutLinksIsConvertedFromAPrivateSnapshot()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "plain.rtf");
        const string original = @"{\rtf1\ansi Plain {\field{\*\fldinst HYPERLINK ""https://example.com/""}{\fldrslt link}} only.}";
        File.WriteAllText(source, original, Encoding.ASCII);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        // Always hands the engine a private snapshot so the source cannot change after scanning.
        Assert.NotEqual(source, prepared.Path);
        Assert.Equal(0, prepared.ExternalRelationshipsRemoved);
        Assert.Equal(original, File.ReadAllText(prepared.Path, Encoding.ASCII));
    }

    [Fact]
    public void RtfUnicodeEscapesUcFallbacksAndMalformedGroupsAreHandledFailClosed()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "unicode.rtf");
        var rtf =
            @"{\rtf1\ansi"
            + @"{\field{\*\fldinst \u81?UOTE ""kept quote""}{\fldrslt kept quote}}"
            + @"{\field{\*\fldinst \u73?NCLUDEPICTURE ""file:///C:/pixel.png""}{\fldrslt }}"
            + @"{\field{\*\fldinst \uc2\u73??NCLUDETEXT ""\\\\preview-server\\share\\x.txt""}{\fldrslt }}"
            + @"{\field{\*\fldinst CUSTOMFIELD ""\\\\preview-server\\share\\y.png""}{\fldrslt }}"
            + @"}";
        File.WriteAllText(source, rtf, Encoding.ASCII);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);

        Assert.Equal(3, prepared.ExternalRelationshipsRemoved);
        var sanitized = File.ReadAllText(prepared.Path, Encoding.ASCII);
        Assert.Contains("kept quote", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("pixel.png", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("x.txt", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("y.png", sanitized, StringComparison.Ordinal);

        // Malformed unbalanced braces fail closed instead of passing through to LibreOffice.
        var broken = Path.Combine(temp.Path, "broken.rtf");
        File.WriteAllText(broken, @"{\rtf1\ansi {\field{\*\fldinst INCLUDEPICTURE ""x""}", Encoding.ASCII);
        Assert.Throws<InvalidDataException>(() =>
            OfficePreviewSourceSanitizer.Prepare(broken, Path.Combine(temp.Path, "staging"), CancellationToken.None));

        // Excessive nesting depth fails closed in linear time.
        var tooDeep = Path.Combine(temp.Path, "deep.rtf");
        File.WriteAllText(tooDeep, @"{\rtf1" + new string('{', 4200) + new string('}', 4200) + "}", Encoding.ASCII);
        Assert.Throws<InvalidDataException>(() =>
            OfficePreviewSourceSanitizer.Prepare(tooDeep, Path.Combine(temp.Path, "staging"), CancellationToken.None));

        // Truncated or unreadable .rtf fails closed instead of reaching LibreOffice unchecked.
        var truncated = Path.Combine(temp.Path, "truncated.rtf");
        File.WriteAllText(truncated, "{", Encoding.ASCII);
        Assert.Throws<InvalidDataException>(() =>
            OfficePreviewSourceSanitizer.Prepare(truncated, Path.Combine(temp.Path, "staging"), CancellationToken.None));

        // Unsupported \uc fallback counts fail closed instead of silently clamping.
        var badUc = Path.Combine(temp.Path, "baduc.rtf");
        File.WriteAllText(badUc, @"{\rtf1\ansi\uc50 hello}", Encoding.ASCII);
        Assert.Throws<InvalidDataException>(() =>
            OfficePreviewSourceSanitizer.Prepare(badUc, Path.Combine(temp.Path, "staging"), CancellationToken.None));

        // Overflowing \bin length fails closed without wrapping past EOF or backward.
        var badBin = Path.Combine(temp.Path, "badbin.rtf");
        File.WriteAllText(badBin, @"{\rtf1\ansi\bin9223372036854775807 hello}", Encoding.ASCII);
        Assert.Throws<InvalidDataException>(() =>
            OfficePreviewSourceSanitizer.Prepare(badBin, Path.Combine(temp.Path, "staging"), CancellationToken.None));

        // 100 KB body at nesting depth 512 scans in O(N) time without per-byte stack walks.
        var nested = Encoding.ASCII.GetBytes(@"{\rtf1" + new string('{', 512) + new string('a', 100_000) + new string('}', 512) + "}");
        var sw = Stopwatch.StartNew();
        var cleaned = OfficePreviewSourceSanitizer.SanitizeRtfBytes(nested, CancellationToken.None, out var removed);
        sw.Stop();
        Assert.Equal(0, removed);
        Assert.Equal(nested, cleaned);
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(250), $"Nested 100 KB RTF scan took {sw.Elapsed.TotalMilliseconds:0}ms");
    }

    [Fact]
    public void ALinkedPictureInAnRtfIsCutBeforeTheEngineSeesIt()
    {
        if (FindEngine() is not { } soffice)
        {
            return;
        }

        using var temp = new TempDirectory();
        var picture = WriteNoisePicture(temp.Path);
        var escapedPicture = picture.Replace(@"\", @"\\", StringComparison.Ordinal);
        var source = Path.Combine(temp.Path, "linked.rtf");
        File.WriteAllText(
            source,
            $"{{\\rtf1\\ansi Linked picture {{\\field{{\\*\\fldinst INCLUDEPICTURE \"{escapedPicture}\" \\\\* MERGEFORMAT}}{{\\fldrslt }}}}}}",
            Encoding.ASCII);

        using var prepared = OfficePreviewSourceSanitizer.Prepare(source, Path.Combine(temp.Path, "staging"), CancellationToken.None);
        var pdf = ConvertToPdf(soffice, prepared.Path, temp.Path);

        Assert.Equal(1, prepared.ExternalRelationshipsRemoved);
        Assert.False(HoldsAPicture(pdf));
    }

    private static string LinkedPicture(string picture, string placement) =>
        $"""<draw:frame draw:name="linked" {placement} svg:width="8cm" svg:height="8cm"><draw:image xlink:href="{new Uri(picture).AbsoluteUri}" xlink:type="simple" xlink:show="embed" xlink:actuate="onLoad"/></draw:frame>""";

    private static bool HoldsAPicture(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        return text.Contains("/Subtype/Image", StringComparison.Ordinal) || text.Contains("/Subtype /Image", StringComparison.Ordinal);
    }

    private static string? FindEngine() =>
        OfficeDocumentPdfConverter.GetDefaultSofficeCandidates(null, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            .FirstOrDefault(candidate => candidate.Contains("LibreOffice", StringComparison.Ordinal) && File.Exists(candidate));

    /// <summary>One engine run the way the converter starts it: its profile, its environment, a private folder.</summary>
    private static byte[] ConvertToPdf(string soffice, string source, string folder)
    {
        var profile = Path.Combine(folder, "profile");
        var output = Path.Combine(folder, "out");
        Directory.CreateDirectory(Path.Combine(output, "engine-tmp"));
        OfficeDocumentPdfConverter.PrepareNetworkIsolatedProfile(profile);
        using var engine = Process.Start(OfficeDocumentPdfConverter.BuildStartInfo(soffice, source, "pdf", output, profile))!;
        _ = engine.StandardOutput.ReadToEndAsync();
        _ = engine.StandardError.ReadToEndAsync();
        if (!engine.WaitForExit(TimeSpan.FromSeconds(90)))
        {
            engine.Kill(entireProcessTree: true);
            Assert.Fail("The engine never finished.");
        }

        return File.ReadAllBytes(Path.Combine(output, Path.GetFileNameWithoutExtension(source) + ".pdf"));
    }

    /// <summary>Noise does not compress, so the picture is unmistakable in a PDF that holds it.</summary>
    private static string WriteNoisePicture(string folder)
    {
        const int size = 128;
        var pixels = new byte[size * size * 3];
        new Random(11).NextBytes(pixels);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgr24, null, pixels, size * 3);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var path = Path.Combine(folder, "noise.png");
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private static void WriteOpenDocument(string path, string body, string content, string prolog = "")
    {
        var kind = body == SheetBody ? "spreadsheet" : "text";
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        Write(archive, "mimetype", $"application/vnd.oasis.opendocument.{kind}", CompressionLevel.NoCompression);
        Write(archive, "content.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>{prolog}
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0"
                xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0"
                xmlns:table="urn:oasis:names:tc:opendocument:xmlns:table:1.0"
                xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0"
                xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0"
                xmlns:xlink="http://www.w3.org/1999/xlink" office:version="1.2"><office:body><{body}>{content}</{body}></office:body></office:document-content>
            """);
        Write(archive, "META-INF/manifest.xml", $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
              <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.{kind}"/>
              <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
            </manifest:manifest>
            """);
    }

    private static void Write(ZipArchive archive, string name, string text, CompressionLevel compression = CompressionLevel.Optimal)
    {
        using var stream = archive.CreateEntry(name, compression).Open();
        stream.Write(new UTF8Encoding(false).GetBytes(text));
    }

    private static string Read(ZipArchive archive, string name)
    {
        using var reader = new StreamReader(archive.GetEntry(name)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
