using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfFillSignWriterAdversarialTests
{
    [Fact]
    public void NoEndobjBombFailsFastAndLeavesNoArtifacts()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bomb.pdf");
        var sb = new StringBuilder("%PDF-1.7\n");
        while (sb.Length < 3_000_000)
        {
            sb.Append("1 0 obj\n<</Type /Page>>\n");
        }

        // A sound footer, so the reader gets past the trailer and the
        // object scan meets three million bytes with no endobj in them.
        var xref = sb.Length;
        sb.Append("xref\n0 2\n0000000000 65535 f \n0000000009 00000 n \n");
        sb.Append($"trailer\n<< /Size 2 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString());
        var output = path + ".signing";

        var sw = Stopwatch.StartNew();
        Assert.Throws<RegexMatchTimeoutException>(() => PdfFillSignWriter.WriteSignedCopy(
            path,
            output,
            [new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.2, 0.05, "1/1/2026")]));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 5000, $"bomb took {sw.ElapsedMilliseconds}ms");
        Assert.False(File.Exists(output));
        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(path).Problem);
    }

    [Fact]
    public void SigningNeverTouchesTheSourceBytes()
    {
        // The test host skips pdfium's teardown (TestPdfium), so the
        // rendered output stays open until the host exits; the temp
        // directory's cleanup is best effort for that reason.
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "source.pdf");
        WriteMinimalPdf(source);
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

        // Its own name each run: the writer never writes over a file, and a
        // rendered output stays open until the host exits.
        var output = $"{source}.{Guid.NewGuid():N}.signing";
        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.3, 0.05, "7/7/2026 (test) done")]);

        Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        Assert.True(new FileInfo(output).Length > new FileInfo(source).Length);
        // The signed output must still render: the writer's promise is
        // an openable document, not just appended bytes.
        var page = PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None);
        Assert.NotNull(page);
    }

    private static void WriteMinimalPdf(string path)
    {
        var objs = new (int Num, string Body)[]
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>")
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new Dictionary<int, int>();
        foreach (var (num, body) in objs)
        {
            offsets[num] = Encoding.Latin1.GetByteCount(sb.ToString());
            sb.Append($"{num} 0 obj\n{body}\nendobj\n");
        }

        var xrefAt = Encoding.Latin1.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var (num, _) in objs)
        {
            sb.Append($"{offsets[num]:0000000000} 00000 n \n");
        }

        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefAt}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString());
    }
}
