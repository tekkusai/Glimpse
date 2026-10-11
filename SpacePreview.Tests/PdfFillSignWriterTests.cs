using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfFillSignWriterTests
{
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

    private static byte[] TransparentRedPng()
    {
        var pixels = new byte[] { 0, 0, 255, 128 };
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static void WritePdf(string path, string pageBody, string trailerExtra = "", int? sizeOverride = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new Dictionary<int, long>();

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes);
        }

        void Object(int number, string body)
        {
            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.4\n");
        Object(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, pageBody);
        Object(4, "<< /Length 0 >>\nstream\n\nendstream");

        var xrefOffset = stream.Position;
        Write("xref\n0 5\n0000000000 65535 f \n");
        for (var i = 1; i <= 4; i++)
        {
            Write($"{offsets[i]:0000000000} 00000 n \n");
        }

        Write($"trailer\n<< /Size {sizeOverride ?? 5} /Root 1 0 R {trailerExtra} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
    }
}
