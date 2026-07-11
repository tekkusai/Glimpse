using System.Diagnostics;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class PdfPageSizeProbeTests
{
    [Fact]
    public void ReadsPortraitFirstPageMediaBox()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "portrait.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"
            });

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(612, width);
        Assert.Equal(792, height);
    }

    [Fact]
    public void ReadsLandscapeFirstPageMediaBox()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "landscape.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] >>"
            });

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(792, width);
        Assert.Equal(612, height);
    }

    [Fact]
    public void AppliesPagesTreeInheritanceAndRotation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "rotated.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 612 792] >>",
                [3] = "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 /Rotate 90 >>",
                [4] = "<< /Type /Page /Parent 3 0 R >>"
            });

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(792, width);
        Assert.Equal(612, height);
    }

    [Fact]
    public void PrefersCropBoxWhenItDiffersFromMediaBox()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cropbox.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 1000 1000] /CropBox [10 20 410 620] >>"
            });

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(400, width);
        Assert.Equal(600, height);
    }

    [Fact]
    public void InheritsCropBoxFromPagesTree()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "inherited-cropbox.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 1000 1000] /CropBox [0 0 500 700] >>",
                [3] = "<< /Type /Page /Parent 2 0 R >>"
            });

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.True(ok);
        Assert.Equal(500, width);
        Assert.Equal(700, height);
    }

    [Fact]
    public void ReturnsFalseForEncryptedDocuments()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "encrypted.pdf");
        WritePdf(
            path,
            new Dictionary<int, string>
            {
                [1] = "<< /Type /Catalog /Pages 2 0 R >>",
                [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
                [4] = "<< >>"
            },
            "/Encrypt 4 0 R");

        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);

        Assert.False(ok);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
    }

    [Fact]
    public void ReturnsFalseQuicklyForUnclosedObjectHeaders()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "bomb.pdf");
        var fragment = "1 0 obj <</Type /Page>>\n";
        var builder = new StringBuilder("%PDF-1.7\n");
        var bytes = Encoding.ASCII.GetByteCount(builder.ToString());
        var fragmentBytes = Encoding.ASCII.GetByteCount(fragment);
        while (bytes < 4_500_000)
        {
            builder.Append(fragment);
            bytes += fragmentBytes;
        }

        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);

        var started = Stopwatch.StartNew();
        var ok = PdfPageSizeProbe.TryReadFirstPageSize(path, out var width, out var height);
        started.Stop();

        Assert.False(ok);
        Assert.Equal(0, width);
        Assert.Equal(0, height);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"Probe took {started.Elapsed}.");
    }

    private static void WritePdf(string path, IReadOnlyDictionary<int, string> objects, string trailerExtra = "")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        var offsets = new Dictionary<int, long>();

        void Write(string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes);
        }

        Write("%PDF-1.4\n");
        foreach (var (number, body) in objects.OrderBy(pair => pair.Key))
        {
            offsets[number] = stream.Position;
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        var xrefOffset = stream.Position;
        var maxObject = objects.Keys.Max();
        Write($"xref\n0 {maxObject + 1}\n");
        Write("0000000000 65535 f \n");
        for (var i = 1; i <= maxObject; i++)
        {
            if (offsets.TryGetValue(i, out var offset))
            {
                Write($"{offset:0000000000} 00000 n \n");
            }
            else
            {
                Write("0000000000 65535 f \n");
            }
        }

        Write($"trailer\n<< /Size {maxObject + 1} /Root 1 0 R {trailerExtra} >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");
    }
}
