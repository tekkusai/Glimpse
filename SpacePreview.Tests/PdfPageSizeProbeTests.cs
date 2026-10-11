using System.Buffers.Binary;
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

    // Only strings and streams are encrypted; the page tree's numbers are
    // plain, so a protected file still opens page-shaped.
    [Fact]
    public void ReadsEncryptedDocumentsWhosePageTreeIsPlain()
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

        Assert.True(ok);
        Assert.Equal(612, width);
        Assert.Equal(792, height);
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

    // Past the limit a nested value is an ordinary parse failure, not a
    // stack overflow that takes the app down with it.
    [Theory]
    [InlineData("[", "]")]
    [InlineData("<< /A ", " >>")]
    public void ValuesNestedPastTheLimitFailOrdinarily(string open, string close)
    {
        using var temp = new TempDirectory();
        var deep = Path.Combine(temp.Path, "deep.pdf");
        var ordinary = Path.Combine(temp.Path, "ordinary.pdf");
        WritePdf(deep, PageWithCatalogValue(Nest(open, close, PdfPageSizeProbe.MaxNesting + 50)));
        WritePdf(ordinary, PageWithCatalogValue(Nest(open, close, 10)));

        Assert.Null(PdfPageSizeProbe.ReadUncached(deep));
        Assert.Equal(new PdfOutline(612, 792, 1), PdfPageSizeProbe.ReadUncached(ordinary));
    }

    // Each link is an object stream whose length is kept in the next one,
    // so reading the catalog recurses once per link.
    [Fact]
    public void AChainOfObjectsPastTheLimitReadsAsMissing()
    {
        using var temp = new TempDirectory();
        var deep = Path.Combine(temp.Path, "deep.pdf");
        var ordinary = Path.Combine(temp.Path, "ordinary.pdf");
        WriteChainedObjectStreams(deep, links: PdfPageSizeProbe.MaxObjectDepth + 50);
        WriteChainedObjectStreams(ordinary, links: 1);

        Assert.Null(PdfPageSizeProbe.ReadUncached(deep));
        Assert.Equal(new PdfOutline(612, 792, 1), PdfPageSizeProbe.ReadUncached(ordinary));
    }

    private static string Nest(string open, string close, int depth) =>
        string.Concat(Enumerable.Repeat(open, depth)) + "0" + string.Concat(Enumerable.Repeat(close, depth));

    private static Dictionary<int, string> PageWithCatalogValue(string value) => new()
    {
        [1] = $"<< /Type /Catalog /Pages 2 0 R /Extra {value} >>",
        [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>"
    };

    /// <summary>
    /// The catalog sits in an object stream whose /Length is an object in a
    /// second stream, whose /Length is in a third, <paramref name="links"/>
    /// times over. A cross-reference stream indexes them all.
    /// </summary>
    private static void WriteChainedObjectStreams(string path, int links)
    {
        using var stream = File.Create(path);
        var entries = new SortedDictionary<int, (byte Kind, long Field)>();

        void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

        void Object(int number, string body)
        {
            entries[number] = (1, stream.Position);
            Write($"{number} 0 obj\n{body}\nendobj\n");
        }

        Write("%PDF-1.5\n");
        Object(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Object(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        var (held, value) = (1, "<< /Type /Catalog /Pages 2 0 R >>");
        for (var link = 0; link <= links; link++)
        {
            var number = 10 + 2 * link;
            var header = $"{held} 0 ";
            var data = header + value;
            entries[held] = (2, number);
            var length = link == links ? $"{data.Length}" : $"{number + 1} 0 R";
            Object(number, $"<< /Type /ObjStm /N 1 /First {header.Length} /Length {length} >>\nstream\n{data}\nendstream");
            (held, value) = (number + 1, $"{data.Length}");
        }

        var index = 10 + 2 * (links + 1);
        entries[index] = (1, stream.Position);
        var rows = new byte[(index + 1) * 7];
        foreach (var (number, (kind, field)) in entries)
        {
            rows[number * 7] = kind;
            BinaryPrimitives.WriteUInt32BigEndian(rows.AsSpan(number * 7 + 1), (uint)field);
        }

        Write($"{index} 0 obj\n<< /Type /XRef /Size {index + 1} /W [1 4 2] /Root 1 0 R /Length {rows.Length} >>\nstream\n");
        stream.Write(rows);
        Write($"\nendstream\nendobj\nstartxref\n{entries[index].Field}\n%%EOF\n");
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
