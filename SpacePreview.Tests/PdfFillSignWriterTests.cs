using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfFillSignWriterTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string OnePage = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string WidePage = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Contents 4 0 R >>";

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
    // Protection: a file that forbids the change is refused, not changed.
    // ------------------------------------------------------------------

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

    private static SignPlacement BlackStamp(int page) =>
        new(SignPlacementKind.Stamp, page, 0.5, 0.2, 0.3, 0.5, null, OpaqueBlackPng());

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
