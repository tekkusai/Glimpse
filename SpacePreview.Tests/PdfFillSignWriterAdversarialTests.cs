using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfFillSignWriterAdversarialTests
{
    [Fact]
    public void NoEndobjBombFailsFastAndLeavesNoArtifacts()
    {
        var path = Path.Combine(Path.GetTempPath(), "glimpse-sign-bomb.pdf");
        var sb = new StringBuilder("%PDF-1.7\n");
        while (sb.Length < 3_000_000)
        {
            sb.Append("1 0 obj\n<</Type /Page>>\n");
        }

        File.WriteAllText(path, sb.ToString());
        var output = path + ".signing";
        try
        {
            var sw = Stopwatch.StartNew();
            Assert.ThrowsAny<Exception>(() => PdfFillSignWriter.WriteSignedCopy(
                path,
                output,
                [new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.2, 0.05, "1/1/2026")]));
            sw.Stop();
            Assert.True(sw.ElapsedMilliseconds < 5000, $"bomb took {sw.ElapsedMilliseconds}ms");
            Assert.False(File.Exists(output));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SigningNeverTouchesTheSourceBytes()
    {
        var source = Path.Combine(Path.GetTempPath(), "glimpse-sign-src.pdf");
        WriteMinimalPdf(source);
        var hashBefore = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

        var output = source + ".signing";
        PdfFillSignWriter.WriteSignedCopy(
            source,
            output,
            [new SignPlacement(SignPlacementKind.Date, 0, 0.1, 0.1, 0.3, 0.05, "7/7/2026 (test) done")]);
        try
        {
            Assert.Equal(hashBefore, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
            Assert.True(new FileInfo(output).Length > new FileInfo(source).Length);
            // The signed output must still render: the writer's promise is
            // an openable document, not just appended bytes.
            var page = PdfPreviewRenderer.TryRenderPage(output, 0, CancellationToken.None);
            Assert.NotNull(page);
        }
        finally
        {
            File.Delete(source);
            // The test host skips pdfium's teardown (TestPdfium), so the
            // rendered output stays open until the host exits.
            try
            {
                File.Delete(output);
            }
            catch (IOException)
            {
            }
        }
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
