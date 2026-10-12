using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The page-shape probe and the signer read a PDF through one reader, which
/// finds objects and frames pages the way the viewer does. Each case here
/// asks pdfium, in a helper process as the app does, what it shows, and
/// holds the signer to it: the ink is on the page the viewer shows, where
/// it was placed, or the file is refused.
/// </summary>
public sealed class PdfReaderTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string OnePage = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string WidePage = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>";

    // A page that draws black edge to edge, and is tall: no ink shows on it,
    // and no ink placed for the wide page lands in the same frame.
    private const string BlackPage = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 5 0 R >>";
    private const string BlackInk = "0 0 0 rg 0 0 200 200 re f";

    // ------------------------------------------------------------------
    // Which object: the one the index names, as the viewer reads the index.
    // ------------------------------------------------------------------

    // The 3.0.0 writer took the last thing in the file that looked like
    // object 3. The viewer follows the index to the first.
    [Fact]
    public void AShadowPageAfterTheLiveOneIsNotThePageThatGetsSigned()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "shadow.pdf");
        var output = Path.Combine(temp.Path, "shadow-signed.pdf");
        var file = new Raw().Obj(1, Catalog).Obj(2, OnePage).Obj(3, WidePage).Stream(4, "").Stream(5, BlackInk);
        file.Obj(3, BlackPage, indexed: false);
        file.Save(source, file.Table("/Size 6 /Root 1 0 R"));

        Assert.Equal((200, 100), ViewerPageSize(source, 0));
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);

        AssertInkWherePlaced(output, stamp);
        Assert.Contains("/MediaBox [0 0 200 100]", LastDefinition(output, 3), StringComparison.Ordinal);
    }

    [Fact]
    public void AShadowPageInsideStreamDataIsNotThePageThatGetsSigned()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "embedded.pdf");
        var output = Path.Combine(temp.Path, "embedded-signed.pdf");
        var inner = $"3 0 obj\n{BlackPage}\nendobj\n";
        var file = new Raw().Obj(1, Catalog).Obj(2, OnePage).Obj(3, WidePage).Stream(4, "").Stream(5, BlackInk).Stream(6, inner);
        file.Save(source, file.Table("/Size 7 /Root 1 0 R"));

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);

        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void AShadowPageAfterAStreamIndexedFileIsNotThePageThatGetsSigned()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "stream-shadow.pdf");
        var output = Path.Combine(temp.Path, "stream-shadow-signed.pdf");
        new PdfFillSignWriterTests.PdfBuilder("1.5")
            .Object(1, Catalog)
            .Object(2, OnePage)
            .Object(3, WidePage)
            .Stream(4, "")
            .Stream(5, BlackInk)
            .XrefStream(6, "/Size 7 /Root 1 0 R")
            .Save(source);
        File.AppendAllText(source, $"3 0 obj\n{BlackPage}\nendobj\n", Encoding.Latin1);

        Assert.Equal((200, 100), ViewerPageSize(source, 0));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);

        AssertInkWherePlaced(output, stamp);
    }

    // With an index the viewer may not follow, it may be scanning the file
    // instead, and then a later definition wins. Which it did cannot be
    // seen from here, so two definitions of the page are a refusal. Junk
    // before the header is the case 3.0.0 got wrong: pdfium still follows
    // that index, to the live page, and 3.0.0 signed the shadow.
    [Theory]
    [InlineData("junk before the header")]
    [InlineData("a startxref off its mark")]
    [InlineData("table rows a byte short")]
    [InlineData("padding after the end")]
    public void WhenTheIndexIsInDoubtAPageDefinedTwiceIsRefused(string doubt)
    {
        using var temp = new TempDirectory();
        var twice = Path.Combine(temp.Path, "twice.pdf");
        var once = Path.Combine(temp.Path, "once.pdf");
        WriteDoubtful(twice, doubt, shadow: true);
        WriteDoubtful(once, doubt, shadow: false);

        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(twice).Problem);

        // The same file without the second definition reads one way, and signs.
        var output = Path.Combine(temp.Path, "once-signed.pdf");
        var stamp = BlackStamp(0);
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(once));
        PdfFillSignWriter.WriteSignedCopy(once, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // pdfium keeps an object a hybrid file's stream packed, whatever a later
    // table says of it: ink on such a page was saved and never showed.
    [Fact]
    public void APageTheViewerWouldNotTakeANewRevisionOfIsRefused()
    {
        using var temp = new TempDirectory();
        var packed = Path.Combine(temp.Path, "hybrid-packed.pdf");
        var plain = Path.Combine(temp.Path, "hybrid-plain.pdf");
        WriteHybrid(packed, pagePacked: true);
        WriteHybrid(plain, pagePacked: false);

        Assert.Equal((200, 100), ViewerPageSize(packed, 0));
        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(packed).Problem);

        // Word's own shape, pages plain and other objects packed, signs.
        var output = Path.Combine(temp.Path, "hybrid-plain-signed.pdf");
        var stamp = BlackStamp(0);
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(plain));
        PdfFillSignWriter.WriteSignedCopy(plain, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // A table appended to a file indexed by streams makes pdfium drop the
    // index and scan, and a packed page then keeps its place: the 2.0
    // signer left such files behind, with ink that never showed.
    [Fact]
    public void ATableUpdateOverAPackedPageIsRefused()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "mixed.pdf");
        new PdfFillSignWriterTests.PdfBuilder("1.5")
            .Object(1, Catalog)
            .Object(2, "<< /Type /Pages /Kids [9 0 R] /Count 1 >>")
            .Stream(4, "")
            .Packed(5, (9, WidePage))
            .XrefStream(6, "/Size 10 /Root 1 0 R")
            .Save(source);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(source));
        var previous = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0];
        var update = $"9 0 obj\n{WidePage.Replace("/Contents 4 0 R", "/Contents 4 0 R /Annots []")}\nendobj\n";
        File.AppendAllText(
            source,
            update + $"xref\n9 1\n{text.Length:0000000000} 00000 n \ntrailer\n<< /Size 10 /Root 1 0 R /Prev {previous} >>\nstartxref\n{text.Length + update.Length}\n%%EOF\n",
            Encoding.Latin1);

        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(source).Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASignedCopySignsAgain(bool streams)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "contract.pdf");
        var first = Path.Combine(temp.Path, "contract (signed).pdf");
        var second = Path.Combine(temp.Path, "contract (signed 2).pdf");
        var builder = new PdfFillSignWriterTests.PdfBuilder("1.5").Object(1, Catalog).Object(2, OnePage).Object(3, WidePage).Stream(4, "");
        (streams ? builder.XrefStream(5, "/Size 6 /Root 1 0 R") : builder.Table("/Size 5 /Root 1 0 R")).Save(source);

        var left = new SignPlacement(SignPlacementKind.Stamp, 0, 0.1, 0.2, 0.2, 0.5, null, OpaqueBlackPng());
        var right = new SignPlacement(SignPlacementKind.Stamp, 0, 0.6, 0.2, 0.2, 0.5, null, OpaqueBlackPng());
        PdfFillSignWriter.WriteSignedCopy(source, first, [left]);
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(first));
        PdfFillSignWriter.WriteSignedCopy(first, second, [right]);

        // Both marks show: the bounding box of the ink runs from the first to the second.
        var page = Render(second, 0);
        var ink = InkBounds(page);
        Assert.NotNull(ink);
        Assert.InRange(ink!.Value.Left, 0.1 * page.PixelWidth - 2, 0.1 * page.PixelWidth + 2);
        Assert.InRange(ink.Value.Right, 0.8 * page.PixelWidth - 2, 0.8 * page.PixelWidth + 2);
    }

    // ------------------------------------------------------------------
    // Which page, and how it is framed: what the viewer shows.
    // ------------------------------------------------------------------

    // Each of these the 3.0.0 writer refused, or framed its own way. The
    // viewer shows every one of them as a page, and the ink has to land on
    // it where it was placed.
    [Theory]
    [InlineData("a crop box that overhangs the media box", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /CropBox [-50 -40 150 80] /Resources << >> /Contents 4 0 R >>", OnePage, 150, 80)]
    [InlineData("a crop box inside the media box", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /CropBox [20 10 120 60] /Resources << >> /Contents 4 0 R >>", OnePage, 100, 50)]
    [InlineData("no media box at all", "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 4 0 R >>", OnePage, 612, 792)]
    [InlineData("a media box of five numbers", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100 7] /Resources << >> /Contents 4 0 R >>", OnePage, 612, 792)]
    [InlineData("a crop box of five numbers", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /CropBox [0 0 50 50 7] /Resources << >> /Contents 4 0 R >>", OnePage, 200, 100)]
    [InlineData("a media box written twice", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>", OnePage, 200, 100)]
    [InlineData("a media box kept in another object", "<< /Type /Page /Parent 2 0 R /MediaBox 7 0 R /Resources << >> /Contents 4 0 R >>", OnePage, 250, 125)]
    [InlineData("a leaf with no type", "<< /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>", OnePage, 200, 100)]
    [InlineData("a box inherited from the parent", "<< /Type /Page /Parent 2 0 R /Resources << >> /Contents 4 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 150] >>", 300, 150)]
    [InlineData("a turn inherited from the parent", WidePage, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 >>", 100, 200)]
    [InlineData("a tree whose root is the page", "<< >>", "<< /Type /Pages /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>", 200, 100)]
    public void ThePageIsFramedAsTheViewerFramesIt(string what, string page, string pages, int width, int height)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "page.pdf");
        var output = Path.Combine(temp.Path, "page-signed.pdf");
        var file = new Raw().Obj(1, Catalog).Obj(2, pages).Obj(3, page).Stream(4, "").Obj(7, "[0 0 250 125]");
        file.Save(source, file.Table("/Size 8 /Root 1 0 R"));

        // The viewer, the probe and the signer: one shape.
        Assert.Equal((width, height), ViewerPageSize(source, 0));
        Assert.True(new PdfOutline(width, height, 1) == PdfPageSizeProbe.ReadUncached(source), what);
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // The probe turned a page only at exactly 90 or 270 and took the first
    // four of a longer box: the window opened one shape, the viewer drew another.
    [Theory]
    [InlineData("/MediaBox [0 0 200 100] /Rotate 135", 100, 200)]
    [InlineData("/MediaBox [0 0 200 100] /Rotate 45", 200, 100)]
    [InlineData("/MediaBox [0 0 200 100] /Rotate -90", 100, 200)]
    [InlineData("/MediaBox [0 0 200 100 7]", 612, 792)]
    public void TheProbeReadsThePageTheViewerShows(string entries, int width, int height)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "page.pdf");
        var file = new Raw().Obj(1, Catalog).Obj(2, OnePage).Obj(3, $"<< /Type /Page /Parent 2 0 R {entries} >>");
        file.Save(source, file.Table("/Size 4 /Root 1 0 R"));

        Assert.Equal((width, height), ViewerPageSize(source, 0));
        Assert.Equal(new PdfOutline(width, height, 1), PdfPageSizeProbe.ReadUncached(source));
    }

    // The viewer shows as many pages as the root's /Count says, when it says.
    [Fact]
    public void ACountBelowThePagesHidesTheRestFromTheSignerToo()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "count.pdf");
        var file = new Raw().Obj(1, Catalog).Obj(2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 1 >>").Obj(3, WidePage).Stream(4, "")
            .Obj(6, WidePage);
        file.Save(source, file.Table("/Size 7 /Root 1 0 R"));

        Assert.Null(RenderOrNull(source, 1));
        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));
        Assert.ThrowsAny<Exception>(() => PdfFillSignWriter.WriteSignedCopy(source, Path.Combine(temp.Path, "out.pdf"), [BlackStamp(1)]));
    }

    // A node's /Count moves no page. The viewer finds page n by walking the
    // leaves in order, whatever the nodes above them say they hold, and the
    // signer's pages are those leaves in that order. Measured on pdfium over
    // 33 trees; three of them are held here.
    [Theory]
    [InlineData("/Kids [20 0 R 13 0 R] /Count 3", "20:/Kids [11 0 R 12 0 R] /Count 1", new[] { 110, 120, 130 })]
    [InlineData("/Kids [13 0 R 20 0 R] /Count 3", "20:/Kids [11 0 R 12 0 R] /Count 5", new[] { 130, 110, 120 })]
    [InlineData("/Kids [20 0 R 13 0 R] /Count 3", "20:/Kids [21 0 R] /Count 1;21:/Kids [11 0 R 12 0 R] /Count 0", new[] { 110, 120, 130 })]
    public void APageIsFoundByItsPlaceAmongTheLeavesWhateverANodeSaysItHolds(string root, string nodes, int[] widths)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "tree.pdf");
        var output = Path.Combine(temp.Path, "tree-signed.pdf");
        WriteTree(source, root, nodes);

        for (var index = 0; index < widths.Length; index++)
        {
            Assert.Equal((widths[index], 200), ViewerPageSize(source, index));
        }

        // Ink for the second page the viewer shows goes onto that page's object.
        var stamp = BlackStamp(1);
        Assert.Equal(new PdfSignCheck(3, null), PdfFillSignWriter.Preflight(source));
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
        Assert.Contains("/Annots", LastDefinition(output, widths[1] / 10), StringComparison.Ordinal);
    }

    // With no usable /Count on the root the viewer does not count leaves.
    // It adds up what each node below says of itself, and takes a node at
    // its word. The signer counted leaves: it offered a page the viewer
    // does not have, and disagreed with the rail's count.
    [Theory]
    [InlineData("/Kids [20 0 R 13 0 R]", "20:/Kids [11 0 R 12 0 R] /Count 1", 2)]
    [InlineData("/Kids [20 0 R 13 0 R] /Count 0", "20:/Kids [21 0 R];21:/Kids [11 0 R 12 0 R] /Count 1", 2)]
    [InlineData("/Kids [20 0 R 13 0 R]", "20:/Kids [11 0 R 12 0 R] /Count 0", 3)]
    public void WithNoCountOnTheRootThePagesAreCountedAsTheViewerCountsThem(string root, string nodes, int count)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "tree.pdf");
        WriteTree(source, root, nodes);

        // The viewer draws that many pages and no more.
        Assert.NotNull(RenderOrNull(source, count - 1));
        Assert.Null(RenderOrNull(source, count));
        Assert.Equal(new PdfSignCheck(count, null), PdfFillSignWriter.Preflight(source));
        Assert.ThrowsAny<Exception>(() => PdfFillSignWriter.WriteSignedCopy(source, Path.Combine(temp.Path, "out.pdf"), [BlackStamp(count)]));
    }

    // A node can say more than it holds, too. For this tree pdfium reports
    // six pages (measured) and draws three; the last of them still signs.
    [Fact]
    public void ANodeThatSaysMoreThanItHoldsIsCountedAtItsWord()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "tree.pdf");
        var output = Path.Combine(temp.Path, "tree-signed.pdf");
        WriteTree(source, "/Kids [20 0 R 13 0 R]", "20:/Kids [11 0 R 12 0 R] /Count 5");

        Assert.Equal((130, 200), ViewerPageSize(source, 2));
        Assert.Null(RenderOrNull(source, 3));
        Assert.Equal(new PdfSignCheck(6, null), PdfFillSignWriter.Preflight(source));

        var stamp = BlackStamp(2);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // ------------------------------------------------------------------
    // Cost: the file is streamed, and a hostile one costs little.
    // ------------------------------------------------------------------

    // 3.0.0 held the file as bytes and again as a string twice its size.
    [Fact]
    public void AnOrdinaryFileIsReadWithoutHoldingItWhole()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "large.pdf");
        var file = new Raw().Obj(1, Catalog).Obj(2, OnePage).Obj(3, WidePage).Stream(4, "").Stream(5, new string('x', 24 * 1024 * 1024));
        file.Save(source, file.Table("/Size 6 /Root 1 0 R"));
        PdfFillSignWriter.Preflight(source);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var check = PdfFillSignWriter.Preflight(source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new PdfSignCheck(1, null), check);
        Assert.True(allocated < 4 * 1024 * 1024, $"reading a 24 MB file allocated {allocated / 1024 / 1024} MB");
    }

    [Fact]
    public void AFileOfNothingButObjectHeadersIsRefusedQuickly()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "bomb.pdf");
        var text = new StringBuilder("%PDF-1.7\n");
        for (var number = 1; text.Length < 6_000_000; number++)
        {
            text.Append(number % 300000 + 1).Append(" 0 obj\n<</Type /Page>>\n");
        }

        File.WriteAllText(source, text.ToString());

        var clock = Stopwatch.StartNew();
        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(source).Problem);
        Assert.True(clock.ElapsedMilliseconds < 5000, $"the bomb took {clock.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------
    // The pass over the whole file.
    // ------------------------------------------------------------------

    [Fact]
    public void ThePassFindsNamesAndDefinitionsAcrossItsBuffer()
    {
        // A name spelled with escapes and a definition, each straddling the
        // 256 KB the pass reads at a time.
        var text = new StringBuilder("%PDF-1.4\n");
        text.Append(' ', 256 * 1024 - text.Length - 4).Append("/Encr#79pt ");
        text.Append(' ', 2 * 256 * 1024 - text.Length - 3).Append("12 0 obj\n<< >>\nendobj\n");
        text.Append("7 0 % a comment between\n obj\n<< /Type /ObjStm >>\nendobj\ntrailer\n<< >>\nstartxref\n9\n");
        using var stream = new MemoryStream(Encoding.Latin1.GetBytes(text.ToString()));

        var scan = PdfFileScan.Run(stream, keepDefinitions: true, "Encrypt", "DocMDP");

        Assert.True(scan.Has("Encrypt"));
        Assert.False(scan.Has("DocMDP"));
        Assert.Equal(new[] { 12, 7 }, scan.Definitions.Select(item => item.Number));
        Assert.Equal(2L * 256 * 1024 - 3, scan.Definitions[0].Offset);
        Assert.Equal([scan.Definitions[1].Offset], scan.ObjectStreams);
        Assert.Single(scan.Trailers);
        Assert.Equal(text.ToString().LastIndexOf("startxref", StringComparison.Ordinal), scan.StartXref);
    }

    [Fact]
    public void ADictionaryEntryIsReplacedInPlaceOrAddedBeforeTheEnd()
    {
        var page = PdfDictionary.Parse(Encoding.Latin1.GetBytes("<< /Type /Page /A#6enots [1 0 R] /Inner << /Annots [] >> >>"))!;
        var bare = PdfDictionary.Parse(Encoding.Latin1.GetBytes("<</Type/Page>>"))!;
        var twice = PdfDictionary.Parse(Encoding.Latin1.GetBytes("<< /Annots [] /Annots [] >>"))!;

        Assert.Equal("<< /Type /Page /Annots [9 0 R] /Inner << /Annots [] >> >>", Encoding.Latin1.GetString(page.With("Annots", "[9 0 R]")!));
        Assert.Equal("<</Type/Page /Annots [9 0 R] >>", Encoding.Latin1.GetString(bare.With("Annots", "[9 0 R]")!));
        Assert.Null(twice.With("Annots", "[9 0 R]"));
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>
    /// A one-page file whose index the viewer may not follow, with or
    /// without a second definition of the page after the first.
    /// </summary>
    private static void WriteDoubtful(string path, string doubt, bool shadow)
    {
        var file = new Raw().Obj(1, Catalog).Obj(2, OnePage).Obj(3, WidePage).Stream(4, "").Stream(5, BlackInk);
        if (shadow)
        {
            file.Obj(3, BlackPage, indexed: false);
        }

        var table = file.Table("/Size 6 /Root 1 0 R", rowEnd: doubt == "table rows a byte short" ? "\n" : " \n");
        file.Save(path, doubt == "a startxref off its mark" ? table + 7 : table);
        if (doubt == "junk before the header")
        {
            File.WriteAllBytes(path, [.. "junk\r\n"u8, .. File.ReadAllBytes(path)]);
        }
        else if (doubt == "padding after the end")
        {
            File.AppendAllText(path, new string(' ', 3000));
        }
    }

    /// <summary>
    /// Word's shape: a table that lists packed objects as free, and beside
    /// it a stream that lists every object. The page is packed or plain.
    /// </summary>
    private static void WriteHybrid(string path, bool pagePacked)
    {
        using var stream = File.Create(path);
        var offsets = new Dictionary<int, long>();
        void Write(string value) => stream.Write(Encoding.Latin1.GetBytes(value));
        void Obj(int number, string body)
        {
            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.5\n");
        Obj(1, Catalog);
        Obj(2, OnePage);
        Obj(4, "<< /Length 0 >>\nstream\n\nendstream");
        var packedNumber = pagePacked ? 3 : 7;
        var packedBody = pagePacked ? WidePage : "<< /Producer (test) >>";
        if (!pagePacked)
        {
            Obj(3, WidePage);
        }

        var header = $"{packedNumber} 0 ";
        var data = header + packedBody;
        Obj(5, $"<< /Type /ObjStm /N 1 /First {header.Length} /Length {data.Length} >>\nstream\n{data}\nendstream");
        offsets[8] = stream.Position;
        var rows = new List<byte>();
        for (var number = 0; number <= 8; number++)
        {
            var (type, field) = number == packedNumber ? (2, 5L) : offsets.TryGetValue(number, out var offset) ? (1, offset) : (0, 0L);
            rows.AddRange([(byte)type, (byte)(field >> 24), (byte)(field >> 16), (byte)(field >> 8), (byte)field, 0, 0]);
        }

        Write($"8 0 obj\n<< /Type /XRef /Size 9 /W [1 4 2] /Root 1 0 R /Length {rows.Count} >>\nstream\n");
        stream.Write(rows.ToArray());
        Write("\nendstream\nendobj\n");
        var table = stream.Position;
        Write("xref\n0 9\n");
        for (var number = 0; number <= 8; number++)
        {
            Write(number != packedNumber && offsets.TryGetValue(number, out var offset)
                ? $"{offset:0000000000} 00000 n \n"
                : number == 0 ? "0000000000 65535 f \n" : "0000000000 00000 f \n");
        }

        Write($"trailer\n<< /Size 9 /Root 1 0 R /XRefStm {offsets[8]} >>\nstartxref\n{table}\n%%EOF\n");
    }

    /// <summary>
    /// Three pages of different widths (object 11 is 110 points wide, 12 is
    /// 120, 13 is 130) under a root and the nodes given as "number:entries".
    /// </summary>
    private static void WriteTree(string path, string root, string nodes)
    {
        var file = new Raw().Obj(1, Catalog).Obj(2, $"<< /Type /Pages {root} >>").Stream(4, "");
        foreach (var number in new[] { 11, 12, 13 })
        {
            file.Obj(number, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {number * 10} 200] /Resources << >> /Contents 4 0 R >>");
        }

        var last = 13;
        foreach (var node in nodes.Split(';'))
        {
            var parts = node.Split(':');
            last = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            file.Obj(last, $"<< /Type /Pages /Parent 2 0 R {parts[1]} >>");
        }

        file.Save(path, file.Table($"/Size {last + 1} /Root 1 0 R"));
    }

    /// <summary>A PDF written piece by piece, where an object can be left out of the index.</summary>
    private sealed class Raw
    {
        private readonly MemoryStream _bytes = new();
        private readonly Dictionary<int, long> _offsets = [];

        public Raw() => Add("%PDF-1.4\n");

        public Raw Obj(int number, string body, bool indexed = true)
        {
            var at = Add($"{number} 0 obj\n{body}\nendobj\n");
            if (indexed)
            {
                _offsets[number] = at;
            }

            return this;
        }

        public Raw Stream(int number, string data) =>
            Obj(number, $"<< /Length {data.Length} >>\nstream\n{data}\nendstream");

        /// <summary>A classic table over every indexed object, and its trailer. Returns where it starts.</summary>
        public long Table(string trailer, string rowEnd = " \n")
        {
            var at = _bytes.Position;
            var size = _offsets.Keys.Max() + 1;
            Add($"xref\n0 {size}\n");
            for (var number = 0; number < size; number++)
            {
                Add(_offsets.TryGetValue(number, out var offset) ? $"{offset:0000000000} 00000 n{rowEnd}" : $"0000000000 65535 f{rowEnd}");
            }

            Add($"trailer\n<< {trailer} >>\n");
            return at;
        }

        public void Save(string path, long startXref)
        {
            Add($"startxref\n{startXref}\n%%EOF\n");
            File.WriteAllBytes(path, _bytes.ToArray());
        }

        private long Add(string text)
        {
            var at = _bytes.Position;
            _bytes.Write(Encoding.Latin1.GetBytes(text));
            return at;
        }
    }

    private static SignPlacement BlackStamp(int page) =>
        new(SignPlacementKind.Stamp, page, 0.5, 0.2, 0.3, 0.5, null, OpaqueBlackPng());

    private static byte[] OpaqueBlackPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>The saved page, as the viewer draws it: dark ink filling the placed frame and nowhere else.</summary>
    private static void AssertInkWherePlaced(string output, SignPlacement placement)
    {
        var page = Render(output, placement.PageIndex);
        var frame = new Rect(
            placement.X * page.PixelWidth,
            placement.Y * page.PixelHeight,
            placement.Width * page.PixelWidth,
            placement.Height * page.PixelHeight);
        var ink = InkBounds(page);
        Assert.True(ink is { } found
            && Math.Abs(found.Left - frame.Left) <= 2
            && Math.Abs(found.Top - frame.Top) <= 2
            && Math.Abs(found.Right - frame.Right) <= 2
            && Math.Abs(found.Bottom - frame.Bottom) <= 2,
            $"ink {ink?.ToString() ?? "none"} is not the placed frame {frame}");
    }

    /// <summary>The bounding box of dark pixels, in bitmap pixels.</summary>
    private static Rect? InkBounds(BitmapSource bitmap)
    {
        var source = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;
        for (var y = 0; y < source.PixelHeight; y++)
        {
            for (var x = 0; x < source.PixelWidth; x++)
            {
                var i = y * stride + x * 4;
                if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        return right < 0 ? null : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    /// <summary>The page's size in points as the viewer frames it: the helper renders two pixels a point.</summary>
    private static (int Width, int Height) ViewerPageSize(string path, int pageIndex)
    {
        var page = Render(path, pageIndex);
        return (page.PixelWidth / 2, page.PixelHeight / 2);
    }

    private static BitmapSource Render(string path, int pageIndex) =>
        RenderOrNull(path, pageIndex) ?? throw new Xunit.Sdk.XunitException($"the helper could not render page {pageIndex + 1}");

    /// <summary>
    /// One page, rendered by pdfium in a helper process, as the sign surface
    /// gets it. One try: a helper that fails is the answer.
    /// </summary>
    private static BitmapSource? RenderOrNull(string path, int pageIndex)
    {
        var png = Path.Combine(Path.GetDirectoryName(path)!, $"{Guid.NewGuid():N}.png");
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(AppContext.BaseDirectory, "Glimpse.exe"),
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--render-pdf-page");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add(pageIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(png);
        startInfo.Environment["GLIMPSE_DATA_ROOT"] = Path.Combine(Path.GetDirectoryName(path)!, "data");
        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(TimeSpan.FromSeconds(60)))
        {
            process.Kill(entireProcessTree: true);
            throw new Xunit.Sdk.XunitException("the helper never exited");
        }

        if (process.ExitCode != 0)
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(png);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The text of the newest definition of object <paramref name="number"/>.</summary>
    private static string LastDefinition(string path, int number)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        var start = text.LastIndexOf($"\n{number} 0 obj", StringComparison.Ordinal);
        Assert.True(start >= 0, $"object {number} is not in the file");
        return text[start..text.IndexOf("endobj", start, StringComparison.Ordinal)];
    }
}
