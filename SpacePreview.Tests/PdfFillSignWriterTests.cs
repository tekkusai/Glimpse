using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfFillSignWriterTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string OnePage = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string WidePage = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>";
    private const string BlackPage = "<< /Type /Page /MediaBox [0 0 200 100] /Resources << >> /Contents 5 0 R >>";
    private const string BlackInk = "0 0 0 rg 0 0 200 100 re f";

    [Fact]
    public void TextPlacementCreatesIncrementalUpdateWithoutTouchingSource()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "form.pdf");
        var output = Path.Combine(temp.Path, "form-signed.pdf");
        WritePdf(source, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>");
        var original = File.ReadAllBytes(source);

        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [
                new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.2, 0.3, 0.4, "7/7/2026")
            ]);

        Assert.Equal(original, File.ReadAllBytes(source));
        var written = File.ReadAllText(output, Encoding.Latin1);
        Assert.Contains("/Subtype /FreeText", written, StringComparison.Ordinal);
        Assert.Contains("/Annots", written, StringComparison.Ordinal);
        Assert.Contains("/Rect [20 40 80 80]", written, StringComparison.Ordinal);
        Assert.Contains("/Prev ", written, StringComparison.Ordinal);
        Assert.NotNull(PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None));
    }

    [Fact]
    public void StampPlacementWritesReusableImageAppearance()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "form.pdf");
        var output = Path.Combine(temp.Path, "form-stamped.pdf");
        WritePdf(source, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>");

        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [
                new SignPlacement(SignPlacementKind.Stamp, 0, 0.25, 0.25, 0.25, 0.25, null, TransparentRedPng())
            ]);

        var written = File.ReadAllText(output, Encoding.Latin1);
        Assert.Contains("/Subtype /Stamp", written, StringComparison.Ordinal);
        Assert.Contains("/Subtype /Image", written, StringComparison.Ordinal);
        Assert.Contains("/SMask", written, StringComparison.Ordinal);
        Assert.NotNull(PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 20, 40, 80, 80)]
    [InlineData(90, 40, 10, 120, 40)]
    [InlineData(180, 120, 20, 180, 60)]
    [InlineData(270, 80, 60, 160, 90)]
    public void DisplayCoordinatesMapThroughPageRotation(int rotate, double left, double bottom, double right, double top)
    {
        var rect = PdfFillSignWriter.MapDisplayRectToPageRectForTest(
            0.1,
            0.2,
            0.3,
            0.4,
            0,
            0,
            200,
            100,
            rotate);

        Assert.Equal(left, rect.Left, precision: 3);
        Assert.Equal(bottom, rect.Bottom, precision: 3);
        Assert.Equal(right, rect.Right, precision: 3);
        Assert.Equal(top, rect.Top, precision: 3);
    }

    [Fact]
    public void DisplayCoordinatesHonorCropBoxOffset()
    {
        var rect = PdfFillSignWriter.MapDisplayRectToPageRectForTest(
            0.1,
            0.25,
            0.5,
            0.5,
            10,
            20,
            110,
            220,
            0);

        Assert.Equal(20, rect.Left, precision: 3);
        Assert.Equal(70, rect.Bottom, precision: 3);
        Assert.Equal(70, rect.Right, precision: 3);
        Assert.Equal(170, rect.Top, precision: 3);
    }

    [Fact]
    public void FailedWriteDeletesOutputAndLeavesSourceUntouched()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "encrypted.pdf");
        var output = Path.Combine(temp.Path, "encrypted-signed.pdf");
        WritePdf(
            source,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>",
            trailerExtra: "/Encrypt 9 0 R");
        var original = File.ReadAllBytes(source);

        Assert.Throws<InvalidOperationException>(() =>
            PdfFillSignWriter.WriteSignedCopy(
                source,
                output,
                [
                    new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.2, 0.1, "Name")
                ]));

        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void TrailerSizeIsNotShrunkByIncrementalUpdate()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "sparse.pdf");
        var output = Path.Combine(temp.Path, "sparse-signed.pdf");
        WritePdf(
            source,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>",
            sizeOverride: 20);

        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [
                new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.2, 0.1, "Name")
            ]);

        var written = File.ReadAllText(output, Encoding.Latin1);
        Assert.Contains("/Size 23", written, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    // Which page: a save puts ink on the page the user saw, or refuses.
    // ------------------------------------------------------------------

    // Acrobat and Word lay files out this way: ten of Mark's PDFs that 2.0
    // signed were refused by 3.0.0, and 2.0 took the pages in file order.
    [Fact]
    public void ATwoPageFileWhoseTreeIsPackedAwaySignsThePageTheViewerShowsFirst()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "packed.pdf");
        var output = Path.Combine(temp.Path, "packed-signed.pdf");

        // The catalog and page tree sit in an object stream. The two pages
        // are plain objects, stored second page first.
        new PdfBuilder("1.5")
            .Object(4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 5 0 R >>")
            .Object(3, WidePage.Replace("4 0 R", "5 0 R"))
            .Stream(5, "")
            .Packed(6, (1, Catalog), (2, "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>"))
            .XrefStream(7, "/Size 8 /Root 1 0 R")
            .Save(source);

        // The viewer reads the tree: page 1 is the wide one.
        var first = PdfPreviewRenderer.TryRenderPage(source, 0, CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first!.Width > first.Height, "the fixture's first page is the wide one");

        Assert.Equal(new PdfSignCheck(2, null), PdfFillSignWriter.Preflight(source));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // A page packed in an object stream is rewritten as a plain object. Only
    // a cross-reference stream makes pdfium take the new one; under a table
    // the ink vanished.
    [Fact]
    public void APackedPageSignsAndItsInkShows()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "mixed.pdf");
        var output = Path.Combine(temp.Path, "mixed-signed.pdf");

        // Page 1 is packed in an object stream, page 2 is a plain object.
        new PdfBuilder("1.5")
            .Object(1, Catalog)
            .Object(2, "<< /Type /Pages /Kids [9 0 R 3 0 R] /Count 2 >>")
            .Object(3, WidePage)
            .Stream(4, "")
            .Packed(5, (9, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> /Contents 4 0 R >>"))
            .XrefStream(6, "/Size 10 /Root 1 0 R")
            .Save(source);

        var first = PdfPreviewRenderer.TryRenderPage(source, 0, CancellationToken.None);
        Assert.NotNull(first);
        Assert.True(first!.Height > first.Width, "the fixture's first page is the packed, tall one");

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
        Assert.Contains("/Type /XRef", Encoding.Latin1.GetString(File.ReadAllBytes(output))[(int)new FileInfo(source).Length..], StringComparison.Ordinal);
    }

    [Fact]
    public void ASinglePageWhoseTreeIsPackedAwayStillSigns()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "packed-one.pdf");
        var output = Path.Combine(temp.Path, "packed-one-signed.pdf");
        new PdfBuilder("1.5")
            .Object(3, WidePage)
            .Stream(4, "")
            .Packed(5, (1, Catalog), (2, OnePage))
            .XrefStream(6, "/Size 7 /Root 1 0 R")
            .Save(source);

        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    // ------------------------------------------------------------------
    // Protection: a file that forbids the change is refused, not changed.
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("/P 1")]
    [InlineData("/P 2")]
    [InlineData("")]
    public void ACertificationThatForbidsAnnotationsIsRefused(string permission)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "certified.pdf");
        WriteSignedPdf(source, certification: permission);

        AssertRefusedBeforeWriting(temp, source, "This PDF is certified against changes, so it can't be signed here.");
    }

    [Fact]
    public void ACertificationThatAllowsAnnotationsStillSigns()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "certified-p3.pdf");
        var output = Path.Combine(temp.Path, "certified-p3-signed.pdf");
        WriteSignedPdf(source, certification: "/P 3");

        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void AnApprovalSignedPdfSignsAsAnAppendedRevision()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "approved.pdf");
        var output = Path.Combine(temp.Path, "approved-signed.pdf");
        WriteSignedPdf(source, certification: null);
        var original = File.ReadAllBytes(source);

        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(source));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);

        // Every byte the signature covers is still there, unchanged, ahead
        // of the new revision; the signature field stays on the page.
        var written = File.ReadAllBytes(output);
        Assert.True(written.AsSpan(0, original.Length).SequenceEqual(original), "the signed revision was rewritten");
        var appended = Encoding.Latin1.GetString(written, original.Length, written.Length - original.Length);
        Assert.Matches(@"/Annots \[5 0 R \d+ 0 R\]", appended);
        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void AnEscapedEncryptKeyIsStillRefused()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "escaped.pdf");

        // /Encr#79pt is /Encrypt: names may spell any character as #xx.
        new PdfBuilder()
            .Object(1, Catalog)
            .Object(2, OnePage)
            .Object(3, WidePage)
            .Stream(4, "")
            .Object(9, "<< /Filter /Standard /V 1 /R 2 /O <0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF> "
                + "/U <0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF> /P -4 >>")
            .Table("/Size 10 /Root 1 0 R /Encr#79pt 9 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]")
            .Save(source);

        AssertRefusedBeforeWriting(temp, source, "This PDF is encrypted, so it can't be signed here.");
    }

    // ------------------------------------------------------------------
    // Where on the page: keys are read from the dictionary they belong to.
    // ------------------------------------------------------------------

    [Fact]
    public void NewInkJoinsThePagesOwnAnnotsNotANestedOne()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "private.pdf");
        var output = Path.Combine(temp.Path, "private-signed.pdf");
        const string nested = "/PieceInfo << /Vendor << /LastModified (D:20261011000000Z) /Private << /Annots [] >> >> >>";
        WritePdf(source, $"<< /Type /Page /Parent 2 0 R {nested} /Annots [] /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>");

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);

        AssertInkWherePlaced(output, stamp);
        var page = LastDefinition(output, 3);
        Assert.Contains(nested, page, StringComparison.Ordinal);
        Assert.Matches(@"/Annots \[\d+ 0 R\]", page);
    }

    [Fact]
    public void APageThatNamesAnnotsTwiceIsRefused()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "twice.pdf");
        WritePdf(source, "<< /Type /Page /Parent 2 0 R /Annots [] /MediaBox [0 0 200 100] /Annots [] /Resources << >> /Contents 4 0 R >>");

        AssertRefusedBeforeWriting(temp, source, PdfFillSignWriter.UnsupportedStructure);
    }

    [Fact]
    public void NestingPastTheLimitIsRefusedAndOrdinaryNestingStillReads()
    {
        using var temp = new TempDirectory();
        var deep = Path.Combine(temp.Path, "deep.pdf");
        var ordinary = Path.Combine(temp.Path, "ordinary.pdf");
        static string Page(int depth) =>
            "<< /Type /Page /Parent 2 0 R /PieceInfo "
            + string.Concat(Enumerable.Repeat("<< /A ", depth)) + "1" + string.Concat(Enumerable.Repeat(" >>", depth))
            + " /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>";

        // The writer walks at most 64 containers deep.
        WritePdf(deep, Page(64 + 50));
        WritePdf(ordinary, Page(32));

        Assert.Equal(new PdfSignCheck(1, null), PdfFillSignWriter.Preflight(ordinary));
        AssertRefusedBeforeWriting(temp, deep, PdfFillSignWriter.UnsupportedStructure);
    }

    [Theory]
    [InlineData("/PieceInfo << /App << /Private << /MediaBox [0 0 400 400] >> >> >>")]
    [InlineData("/PieceInfo << /App << /Private << /CropBox [100 50 200 100] >> >> >>")]
    [InlineData("/Annots [<< /Type /Annot /Subtype /Square /Rect [0 0 1 1] /F 2 /Rotate 90 >>]")]
    [InlineData("/Note (/MediaBox [0 0 400 400] /Rotate 90)")]
    public void PageGeometryComesFromThePagesOwnKeys(string decoy)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "decoy.pdf");
        var output = Path.Combine(temp.Path, "decoy-signed.pdf");
        WritePdf(source, $"<< /Type /Page /Parent 2 0 R {decoy} /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>");

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Theory]
    [InlineData("<< /Type /Catalog /Names << /Pages 7 0 R >> /Pages 2 0 R >>", OnePage)]
    [InlineData(Catalog, "<< /Type /Pages /Resources << /Properties << /P0 << /Kids [9 0 R] >> >> >> /Kids [3 0 R] /Count 1 >>")]
    [InlineData(Catalog, "<< /PieceInfo << /App << /Type /Page >> >> /Type /Pages /Kids [3 0 R] /Count 1 >>")]
    public void ThePageTreeComesFromItsOwnKeys(string catalog, string pages)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "tree.pdf");
        var output = Path.Combine(temp.Path, "tree-signed.pdf");

        // Objects 7 to 9 are a page tree nobody points at: a black page,
        // stored ahead of the real one.
        new PdfBuilder()
            .Object(1, catalog)
            .Object(2, pages)
            .Object(7, "<< /Type /Pages /Kids [9 0 R] /Count 1 >>")
            .Object(9, BlackPage)
            .Stream(5, BlackInk)
            .Object(3, WidePage)
            .Stream(4, "")
            .Table("/Size 10 /Root 1 0 R")
            .Save(source);

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void TheRootComesFromTheTrailerNotAStringInIt()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "root.pdf");
        var output = Path.Combine(temp.Path, "root-signed.pdf");

        // Signing must not swap the document: a /Root inside a string after
        // the real one names a second, black document.
        new PdfBuilder()
            .Object(1, Catalog)
            .Object(2, OnePage)
            .Object(3, WidePage)
            .Stream(4, "")
            .Stream(5, BlackInk)
            .Object(7, "<< /Type /Catalog /Pages 8 0 R >>")
            .Object(8, "<< /Type /Pages /Kids [9 0 R] /Count 1 >>")
            .Object(9, BlackPage.Replace("/Type /Page ", "/Type /Page /Parent 8 0 R "))
            .Table("/Size 10 /Root 1 0 R /Note (/Root 7 0 R)")
            .Save(source);

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Theory]
    [InlineData("junk before the header")]
    [InlineData("a startxref off its mark")]
    public void ADamagedXrefOffsetStillSignsFromTheLastTrailer(string damage)
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "damaged.pdf");
        var output = Path.Combine(temp.Path, "damaged-signed.pdf");

        // Viewers repair both from the last trailer, and so did the writer
        // before it learned to read the newest section.
        new PdfBuilder()
            .Object(1, Catalog)
            .Object(2, OnePage)
            .Object(3, WidePage)
            .Stream(4, "")
            .Table("/Size 5 /Root 1 0 R")
            .Save(source);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(source));
        text = damage == "junk before the header"
            ? "junk\r\n" + text
            : System.Text.RegularExpressions.Regex.Replace(text, @"startxref\s+(\d+)", match => $"startxref\n{int.Parse(match.Groups[1].Value) + 7}");
        File.WriteAllBytes(source, Encoding.Latin1.GetBytes(text));

        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void NewObjectsAreNumberedPastTheLatestSectionsSize()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "linearized.pdf");
        var output = Path.Combine(temp.Path, "linearized-signed.pdf");

        // Laid out the way a linearized file is: the newest xref section
        // comes first and covers the packed page tree, objects 12 and 13.
        // The older section at the end of the file says /Size 6.
        PdfBuilder.SaveTwice(source, file => file
            .XrefStream(10, $"/Size 14 /Root 1 0 R /Prev {file.OffsetOf(5):0000000000}", 10, 11, 12, 13)
            .Packed(11, (12, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"), (13, "<< /Producer (test) >>"))
            .Object(1, "<< /Type /Catalog /Pages 12 0 R >>")
            .Object(3, WidePage.Replace("2 0 R", "12 0 R"))
            .Stream(4, "")
            .XrefStream(5, "/Size 6 /Root 1 0 R", 0, 1, 2, 3, 4, 5)
            .StartAt(10));

        Assert.NotNull(PdfPreviewRenderer.TryRenderPage(source, 0, CancellationToken.None));
        var stamp = BlackStamp(0);
        PdfFillSignWriter.WriteSignedCopy(source, output, [stamp]);
        AssertInkWherePlaced(output, stamp);
    }

    [Fact]
    public void ARotationBetweenQuarterTurnsIsRefused()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "askew.pdf");

        // The viewer shows /Rotate 135 as a quarter turn; the writer would
        // have placed ink as though the page were upright.
        WritePdf(source, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Rotate 135 /Resources << >> /Contents 4 0 R >>");

        AssertRefusedBeforeWriting(temp, source, PdfFillSignWriter.UnsupportedStructure);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>
    /// The check refuses with <paramref name="problem"/> before any work is
    /// placed, and a save refuses too: no output, the source untouched.
    /// </summary>
    private static void AssertRefusedBeforeWriting(TempDirectory temp, string source, string problem)
    {
        var original = File.ReadAllBytes(source);
        var output = Path.Combine(temp.Path, "refused-signed.pdf");

        Assert.Equal(problem, PdfFillSignWriter.Preflight(source).Problem);
        var error = Assert.Throws<InvalidOperationException>(() =>
            PdfFillSignWriter.WriteSignedCopy(source, output, [BlackStamp(0)]));
        Assert.Equal(problem, error.Message);
        Assert.False(File.Exists(output));
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    /// <summary>The saved page, as the viewer draws it: dark ink filling the placed frame and nowhere else.</summary>
    private static void AssertInkWherePlaced(string output, SignPlacement placement)
    {
        var page = PdfPreviewRenderer.TryRenderPage(output, placement.PageIndex, CancellationToken.None);
        Assert.NotNull(page);
        var frame = new Rect(
            placement.X * page!.Width,
            placement.Y * page.Height,
            placement.Width * page.Width,
            placement.Height * page.Height);
        var ink = InkBounds(page.Bitmap);
        Assert.True(ink is { } found
            && Math.Abs(found.Left - frame.Left) <= 2
            && Math.Abs(found.Top - frame.Top) <= 2
            && Math.Abs(found.Right - frame.Right) <= 2
            && Math.Abs(found.Bottom - frame.Bottom) <= 2,
            $"ink {ink?.ToString() ?? "none"} is not the placed frame {frame}");
    }

    private static SignPlacement BlackStamp(int page) =>
        new(SignPlacementKind.Stamp, page, 0.5, 0.2, 0.3, 0.5, null, OpaqueBlackPng());

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

    /// <summary>The text of the newest definition of object <paramref name="number"/>.</summary>
    private static string LastDefinition(string path, int number)
    {
        var text = File.ReadAllText(path, Encoding.Latin1);
        var start = text.LastIndexOf($"\n{number} 0 obj", StringComparison.Ordinal);
        Assert.True(start >= 0, $"object {number} is not in the file");
        return text[start..text.IndexOf("endobj", start, StringComparison.Ordinal)];
    }

    /// <summary>
    /// A one-page PDF with a signature field. Certification is the DocMDP
    /// entry of its transform parameters, or null for an approval signature.
    /// The widget's own /P, its page, is there to be mistaken for one.
    /// </summary>
    private static void WriteSignedPdf(string path, string? certification)
    {
        var perms = certification is null ? string.Empty : " /Perms << /DocMDP 6 0 R >>";
        var reference = certification is null
            ? string.Empty
            : $" /Reference [<< /Type /SigRef /TransformMethod /DocMDP /TransformParams << /Type /TransformParams {certification} /V /1.2 >> >>]";
        new PdfBuilder()
            .Object(1, $"<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >>{perms} >>")
            .Object(2, OnePage)
            .Object(3, WidePage.Replace("/Contents 4 0 R", "/Contents 4 0 R /Annots [5 0 R]"))
            .Stream(4, "")
            .Object(5, "<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1) /Rect [0 0 0 0] /F 132 /P 3 0 R /V 6 0 R >>")
            .Object(6, "<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /ByteRange [0 10 20 30] "
                + $"/Contents <0000000000000000>{reference} /M (D:20261011000000Z) >>")
            .Table("/Size 7 /Root 1 0 R")
            .Save(path);
    }

    private static byte[] TransparentRedPng()
    {
        var pixels = new byte[] { 0, 0, 255, 128 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        return Encode(bitmap);
    }

    private static byte[] OpaqueBlackPng() =>
        Encode(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4));

    private static byte[] Encode(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WritePdf(string path, string pageBody, string trailerExtra = "", int? sizeOverride = null) =>
        new PdfBuilder()
            .Object(1, Catalog)
            .Object(2, OnePage)
            .Object(3, pageBody)
            .Stream(4, "")
            .Table($"/Size {sizeOverride ?? 5} /Root 1 0 R {trailerExtra}")
            .Save(path);

    /// <summary>
    /// A PDF written object by object in the order given, so a test can store
    /// pages out of order, pack objects into an object stream, or point an
    /// xref section forward the way a linearized file does.
    /// </summary>
    internal sealed class PdfBuilder
    {
        private readonly MemoryStream _bytes = new();
        private readonly Dictionary<int, long> _offsets = [];
        private readonly Dictionary<int, (int Stream, int Index)> _packed = [];
        private readonly PdfBuilder? _earlier;
        private long _startXref;

        public PdfBuilder(string version = "1.4")
            : this(version, null)
        {
        }

        private PdfBuilder(string version, PdfBuilder? earlier)
        {
            _earlier = earlier;
            Write($"%PDF-{version}\n");
        }

        /// <summary>
        /// Lays the file out twice, so a section may point at objects written
        /// after it: the second pass knows where the first put everything.
        /// Every forward number must be written at a fixed width.
        /// </summary>
        public static void SaveTwice(string path, Func<PdfBuilder, PdfBuilder> layout, string version = "1.5")
        {
            var first = layout(new PdfBuilder(version, null));
            layout(new PdfBuilder(version, first)).Save(path);
        }

        /// <summary>Where object <paramref name="number"/> starts, from this pass or the one before.</summary>
        public long OffsetOf(int number) =>
            _offsets.TryGetValue(number, out var offset) ? offset : _earlier?.OffsetOf(number) ?? 0;

        public PdfBuilder Object(int number, string body)
        {
            _offsets[number] = _bytes.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
            return this;
        }

        public PdfBuilder Stream(int number, string data, string dictionary = "") =>
            Object(number, $"<< {dictionary} /Length {data.Length} >>\nstream\n{data}\nendstream");

        /// <summary>An object stream holding <paramref name="members"/>, out of reach of a reader that only scans for objects.</summary>
        public PdfBuilder Packed(int number, params (int Number, string Body)[] members)
        {
            var header = new StringBuilder();
            var bodies = new StringBuilder();
            for (var i = 0; i < members.Length; i++)
            {
                header.Append($"{members[i].Number} {bodies.Length} ");
                bodies.Append(members[i].Body).Append('\n');
                _packed[members[i].Number] = (number, i);
            }

            return Stream(number, header.ToString() + bodies, $"/Type /ObjStm /N {members.Length} /First {header.Length}");
        }

        /// <summary>A classic xref table over every object written so far, and its trailer.</summary>
        public PdfBuilder Table(string trailer)
        {
            _startXref = _bytes.Position;
            var size = _offsets.Keys.Max() + 1;
            Write($"xref\n0 {size}\n");
            for (var number = 0; number < size; number++)
            {
                Write(_offsets.TryGetValue(number, out var offset) ? $"{offset:0000000000} 00000 n \n" : "0000000000 65535 f \n");
            }

            Write($"trailer\n<< {trailer} >>\n");
            return this;
        }

        /// <summary>
        /// An xref stream over <paramref name="numbers"/>, or over every object
        /// so far, packed ones included, when none are named.
        /// </summary>
        public PdfBuilder XrefStream(int number, string dictionary, params int[] numbers)
        {
            _startXref = _bytes.Position;
            _offsets[number] = _bytes.Position;
            var last = _offsets.Keys.Concat(_packed.Keys).Max();
            List<int> covered = numbers.Length > 0 ? [.. numbers.Append(number).Distinct().Order()] : [.. Enumerable.Range(0, last + 1)];
            var rows = new List<byte>();
            foreach (var item in covered)
            {
                var packed = Packing(item);
                var offset = item == 0 ? 0 : OffsetOf(item);
                var (type, field, index) = packed is { } slot ? (2, (long)slot.Stream, slot.Index)
                    : offset > 0 ? (1, offset, 0)
                    : (0, 0L, 0xFFFF);
                rows.Add((byte)type);
                rows.AddRange([(byte)(field >> 24), (byte)(field >> 16), (byte)(field >> 8), (byte)field]);
                rows.AddRange([(byte)(index >> 8), (byte)index]);
            }

            var runs = new List<string>();
            for (var i = 0; i < covered.Count;)
            {
                var start = i;
                while (i + 1 < covered.Count && covered[i + 1] == covered[i] + 1)
                {
                    i++;
                }

                runs.Add($"{covered[start]} {i - start + 1}");
                i++;
            }

            Write($"{number} 0 obj\n<< /Type /XRef /W [1 4 2] /Index [{string.Join(" ", runs)}] {dictionary} /Length {rows.Count} >>\nstream\n");
            _bytes.Write(rows.ToArray());
            Write("\nendstream\nendobj\n");
            return this;
        }

        /// <summary>Points startxref at the section object <paramref name="number"/> begins, rather than the last one written.</summary>
        public PdfBuilder StartAt(int number)
        {
            _startXref = OffsetOf(number);
            return this;
        }

        public void Save(string path)
        {
            Write($"startxref\n{_startXref}\n%%EOF\n");
            File.WriteAllBytes(path, _bytes.ToArray());
        }

        private (int Stream, int Index)? Packing(int number) =>
            _packed.TryGetValue(number, out var slot) ? slot : _earlier?.Packing(number);

        private void Write(string text) => _bytes.Write(Encoding.Latin1.GetBytes(text));
    }
}
