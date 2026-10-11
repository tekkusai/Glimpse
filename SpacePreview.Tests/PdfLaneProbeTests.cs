using System.IO.Compression;
using System.Text;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The page-index probe against the index shapes real writers produce:
/// classic tables, compressed cross-reference streams, object streams, the
/// hybrid files Word saves, incremental updates and protected files.
/// </summary>
public sealed class PdfLaneProbeTests
{
    private static readonly Dictionary<int, string> ThreePages = new()
    {
        [1] = "<< /Type /Catalog /Pages 2 0 R >>",
        [2] = "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 /MediaBox [0 0 612 792] >>",
        [3] = "<< /Type /Page /Parent 2 0 R /CropBox [0 0 500 700] >>",
        [4] = "<< /Type /Page /Parent 2 0 R >>",
        [5] = "<< /Type /Page /Parent 2 0 R >>"
    };

    [Theory]
    [InlineData(IndexStyle.Table)]
    [InlineData(IndexStyle.Stream)]
    [InlineData(IndexStyle.Hybrid)]
    public void ReadsShapeAndCountThroughEveryIndexStyle(IndexStyle style)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, $"{style}.pdf");
        IReadOnlyCollection<int> compressed = style == IndexStyle.Table ? [] : [2, 3, 4, 5];
        PdfIndexWriter.Write(path, ThreePages, style, compressed);

        var outline = PdfPageSizeProbe.ReadUncached(path);

        Assert.Equal(new PdfOutline(500, 700, 3), outline);
    }

    [Fact]
    public void FollowsAnIncrementalUpdateToThePageItReplaced()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "updated.pdf");
        PdfIndexWriter.Write(path, ThreePages, IndexStyle.Table, []);
        PdfIndexWriter.AppendUpdate(path, 3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 792 612] >>");

        var outline = PdfPageSizeProbe.ReadUncached(path);

        Assert.Equal(new PdfOutline(792, 612, 3), outline);
    }

    [Fact]
    public void AProtectedFileWithCompressedPagesIsNotGuessed()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "protected.pdf");
        var objects = new Dictionary<int, string>(ThreePages) { [6] = "<< /Filter /Standard >>" };
        PdfIndexWriter.Write(path, objects, IndexStyle.Stream, [2, 3, 4, 5], "/Encrypt 6 0 R");

        Assert.Null(PdfPageSizeProbe.ReadUncached(path));
    }

    [Fact]
    public void ABrokenIndexFallsBackToScanningThePrefix()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "broken.pdf");
        PdfIndexWriter.Write(path, ThreePages, IndexStyle.Table, [], startXrefOverride: 7);

        var outline = PdfPageSizeProbe.ReadUncached(path);

        Assert.Equal(new PdfOutline(500, 700, 3), outline);
    }

    [Fact]
    public void ARewrittenFileIsReadAgain()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "rewritten.pdf");
        PdfIndexWriter.Write(path, ThreePages, IndexStyle.Table, []);
        Assert.Equal(3, PdfPageSizeProbe.Read(path)?.PageCount);

        var onePage = new Dictionary<int, string>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>",
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] >>"
        };
        PdfIndexWriter.Write(path, onePage, IndexStyle.Table, []);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        Assert.Equal(new PdfOutline(400, 300, 1), PdfPageSizeProbe.Read(path));
    }

    [Fact]
    public async Task TheRememberedReadAnswersWithoutTouchingTheFileAgain()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "remembered.pdf");
        PdfIndexWriter.Write(path, ThreePages, IndexStyle.Stream, [2, 3, 4, 5]);
        var first = await PdfPageSizeProbe.ReadAsync(path);

        // Held open exclusively: a second read of the bytes would fail.
        using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var second = PdfPageSizeProbe.ReadAsync(path);

        Assert.True(second.IsCompleted);
        Assert.Equal(first, await second);
    }

    public enum IndexStyle
    {
        Table,
        Stream,
        Hybrid
    }

    /// <summary>Writes small PDFs with the index shapes the probe has to follow.</summary>
    private static class PdfIndexWriter
    {
        public static void Write(
            string path,
            IReadOnlyDictionary<int, string> objects,
            IndexStyle style,
            IReadOnlyCollection<int> compressed,
            string trailerExtra = "",
            long? startXrefOverride = null)
        {
            using var stream = File.Create(path);
            var offsets = new SortedDictionary<int, long>();
            var inStream = new SortedDictionary<int, (int Stream, int Index)>();
            var next = objects.Keys.Max() + 1;

            void Write(string text) => stream.Write(Encoding.ASCII.GetBytes(text));

            void WriteObject(int number, string body, byte[]? data = null)
            {
                offsets[number] = stream.Position;
                Write($"{number} 0 obj\n{body}");
                if (data is not null)
                {
                    Write("\nstream\n");
                    stream.Write(data);
                    Write("\nendstream");
                }

                Write("\nendobj\n");
            }

            Write("%PDF-1.5\n");
            foreach (var (number, body) in objects.Where(pair => !compressed.Contains(pair.Key)))
            {
                WriteObject(number, body);
            }

            if (compressed.Count > 0)
            {
                var objectStream = next++;
                var header = new StringBuilder();
                var bodies = new StringBuilder();
                var index = 0;
                foreach (var number in compressed.Order())
                {
                    header.Append($"{number} {bodies.Length} ");
                    bodies.Append(objects[number]).Append('\n');
                    inStream[number] = (objectStream, index++);
                }

                var plain = Encoding.ASCII.GetBytes(header.ToString() + bodies);
                var packed = Deflate(plain);
                WriteObject(
                    objectStream,
                    $"<< /Type /ObjStm /N {compressed.Count} /First {header.Length} /Filter /FlateDecode /Length {packed.Length} >>",
                    packed);
            }

            long xrefOffset;
            if (style == IndexStyle.Table)
            {
                xrefOffset = stream.Position;
                WriteTable(Write, offsets, [], next);
                Write($"trailer\n<< /Size {next} /Root 1 0 R {trailerExtra} >>\n");
            }
            else
            {
                var xrefNumber = next++;
                var hybridStream = stream.Position;
                offsets[xrefNumber] = hybridStream;
                var rows = new List<byte[]>();
                for (var number = 0; number < next; number++)
                {
                    rows.Add(inStream.TryGetValue(number, out var slot)
                        ? Row(2, slot.Stream, slot.Index)
                        : offsets.TryGetValue(number, out var offset) && style == IndexStyle.Stream
                            ? Row(1, offset, 0)
                            : Row(0, 0, 0));
                }

                var packed = Deflate(PngUp(rows));
                Write($"{xrefNumber} 0 obj\n<< /Type /XRef /Size {next} /W [1 4 2] /Root 1 0 R {trailerExtra} "
                    + $"/Filter /FlateDecode /DecodeParms << /Columns 7 /Predictor 12 >> /Length {packed.Length} >>\nstream\n");
                stream.Write(packed);
                Write("\nendstream\nendobj\n");

                xrefOffset = hybridStream;
                if (style == IndexStyle.Hybrid)
                {
                    // Word's shape: a table older readers can follow, listing the
                    // compressed objects as free, and the stream beside it.
                    xrefOffset = stream.Position;
                    WriteTable(Write, offsets, inStream.Keys.ToHashSet(), next);
                    Write($"trailer\n<< /Size {next} /Root 1 0 R /XRefStm {hybridStream} {trailerExtra} >>\n");
                }
            }

            Write($"startxref\n{startXrefOverride ?? xrefOffset}\n%%EOF\n");
        }

        public static void AppendUpdate(string path, int number, string body)
        {
            var bytes = File.ReadAllBytes(path);
            var text = Encoding.ASCII.GetString(bytes);
            var previous = text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 9)..].Trim().Split('\n')[0].Trim();
            var size = int.Parse(text[(text.LastIndexOf("/Size ", StringComparison.Ordinal) + 6)..].Split(' ')[0]);
            using var stream = new FileStream(path, FileMode.Append);
            void Write(string value) => stream.Write(Encoding.ASCII.GetBytes(value));
            var offset = bytes.Length;
            Write($"{number} 0 obj\n{body}\nendobj\n");
            var xref = offset + Encoding.ASCII.GetByteCount($"{number} 0 obj\n{body}\nendobj\n");
            Write($"xref\n{number} 1\n{offset:D10} 00000 n\r\n");
            Write($"trailer\n<< /Size {size} /Root 1 0 R /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n");
        }

        private static void WriteTable(Action<string> write, SortedDictionary<int, long> offsets, IReadOnlyCollection<int> free, int size)
        {
            write($"xref\n0 {size}\n0000000000 65535 f\r\n");
            for (var number = 1; number < size; number++)
            {
                write(offsets.TryGetValue(number, out var offset) && !free.Contains(number)
                    ? $"{offset:D10} 00000 n\r\n"
                    : "0000000000 00000 f\r\n");
            }
        }

        private static byte[] Row(int kind, long second, int third) =>
        [
            (byte)kind,
            (byte)(second >> 24), (byte)(second >> 16), (byte)(second >> 8), (byte)second,
            (byte)(third >> 8), (byte)third
        ];

        /// <summary>The PNG Up predictor, which is what writers use for cross-reference streams.</summary>
        private static byte[] PngUp(List<byte[]> rows)
        {
            var output = new List<byte>();
            var previous = new byte[rows[0].Length];
            foreach (var row in rows)
            {
                output.Add(2);
                for (var i = 0; i < row.Length; i++)
                {
                    output.Add((byte)(row[i] - previous[i]));
                }

                previous = row;
            }

            return [.. output];
        }

        private static byte[] Deflate(byte[] data)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
            {
                zlib.Write(data);
            }

            return output.ToArray();
        }
    }
}
