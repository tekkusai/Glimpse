using System.IO.Compression;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// What a file costs the reader. Its limit is on the objects it reads, not
/// on the rows an index lists: free rows and old revisions are not objects,
/// and 3.0.0 signed files full of them. An index costs its bytes and
/// nothing for each row. An object keeps its own bytes, not the window it
/// was read in. An object stream's header is read once.
/// </summary>
public sealed class PdfReaderCostTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string OnePage = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string Page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> >>";

    // ------------------------------------------------------------------
    // The limit is on objects.
    // ------------------------------------------------------------------

    // The limit of 200,000 counted table rows: this file has 200,001 rows
    // and three objects, and was refused.
    [Fact]
    public void ATableOfFreeRowsIsNotATableOfObjects()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "free-rows.pdf");
        using (var file = new Writer(source))
        {
            file.Obj(1, Catalog).Obj(2, OnePage).Obj(3, Page);
            file.End(file.Table(200_001, "/Root 1 0 R"));
        }

        AssertSigns(source, pages: 1);
    }

    // It counted an object again in every revision that listed it: 30,000
    // objects in seven full tables were 210,007.
    [Fact]
    public void AnObjectListedInEveryRevisionIsOneObject()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "revisions.pdf");
        using (var file = new Writer(source))
        {
            file.Obj(1, Catalog).Obj(2, OnePage).Obj(3, Page);
            for (var number = 4; number <= 30_000; number++)
            {
                file.Obj(number, "<< >>");
            }

            var newest = file.Table(30_001, "/Root 1 0 R");
            for (var revision = 1; revision < 7; revision++)
            {
                file.Text($"startxref\n{newest}\n%%EOF\n");
                newest = file.Table(30_001, $"/Root 1 0 R /Prev {newest}");
            }

            file.End(newest);
        }

        AssertSigns(source, pages: 1);
    }

    // What the limit is for: the reader never holds more than 200,000
    // objects. A page tree of more nodes than that is refused when the
    // reader gets there, and the same tree with fewer signs.
    [Fact]
    public void APageTreeOfMoreNodesThanTheReaderHoldsIsRefused()
    {
        using var temp = new TempDirectory();
        var wide = Path.Combine(temp.Path, "wide.pdf");
        var narrow = Path.Combine(temp.Path, "narrow.pdf");
        WriteTreeOfEmptyNodes(wide, 200_010);
        WriteTreeOfEmptyNodes(narrow, 1000);

        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(wide).Problem);
        AssertSigns(narrow, pages: 1);
    }

    // ------------------------------------------------------------------
    // An index costs its bytes.
    // ------------------------------------------------------------------

    // Every row of an index stream became a dictionary entry before the
    // limit was looked at: four million rows that deflate to 27 KB
    // allocated 577 MB, and the file was then refused for its rows. This
    // one has a million.
    [Fact]
    public void AnIndexStreamCostsNothingForEachRow()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "rows.pdf");
        using (var file = new Writer(source))
        {
            file.Obj(1, Catalog).Obj(2, OnePage).Obj(3, Page);
            file.End(file.IndexStream(4, 1_000_000, "/Root 1 0 R"));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var check = PdfFillSignWriter.Preflight(source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new PdfSignCheck(1, null), check);
        Assert.True(allocated < 64 * 1024 * 1024, $"reading a million index rows allocated {allocated / 1024 / 1024} MB");
        AssertSigns(source, pages: 1);
    }

    // A chain of such streams filled memory one section at a time: forty of
    // four million rows, in a file of 1 MB, held 15 GB. Together they are
    // now held to what one stream may be, and the rest of the chain is not read.
    [Fact]
    public void AChainOfIndexStreamsIsNotHeldWhole()
    {
        using var temp = new TempDirectory();
        var within = Path.Combine(temp.Path, "three.pdf");
        var beyond = Path.Combine(temp.Path, "ten.pdf");

        // For the page shape one stream may be 16 MiB. Each of these is 4.2 MB.
        WriteChainOfIndexStreams(within, sections: 3, rows: 600_000);
        WriteChainOfIndexStreams(beyond, sections: 10, rows: 600_000);

        Assert.True(ReadsItsWholeIndex(within));
        Assert.False(ReadsItsWholeIndex(beyond));

        // The newest sections are the ones read, and they hold the page.
        Assert.Equal(new PdfOutline(200, 100, 1), PdfPageSizeProbe.ReadUncached(beyond));
    }

    // ------------------------------------------------------------------
    // An object keeps its own bytes.
    // ------------------------------------------------------------------

    // A dictionary over 64 KiB is read in a window of 1 MiB, and kept all
    // of it: 2,000 such pages in a file of 126 MiB held 2 GiB.
    [Fact]
    public void AnObjectKeepsItsOwnBytesAndNotTheWindowItWasReadIn()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "large-pages.pdf");
        const int PageCount = 24;
        using (var file = new Writer(source))
        {
            file.Obj(1, Catalog);
            file.Obj(2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(3, PageCount).Select(number => $"{number} 0 R"))}] /Count {PageCount} >>");
            var pad = new string('A', 66_000);
            for (var i = 0; i < PageCount; i++)
            {
                file.Obj(3 + i, $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Resources << >> /Pad <{pad}> >>");
            }

            file.End(file.Table(PageCount + 3, "/Root 1 0 R"));
        }

        using var stream = new WatchedStream(File.OpenRead(source));
        var reader = PdfReader.ForSigning(stream);
        Assert.True(reader.TryLoadIndex() && reader.TryFollowAsViewer());
        var tree = reader.PageTree()!.Value;
        var pages = new List<PdfDictionary>();
        reader.WalkPages(tree.Number, tree.Node, (_, page) =>
        {
            pages.Add(page);
            return true;
        });
        Assert.Equal(PageCount, pages.Count);

        // With every page read and still held, as the signer holds them: the
        // buffers the reader read the file into are gone.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var held = stream.BytesStillHeld();
        Assert.True(held <= stream.Length, $"the reader holds {held / 1024} KB of read buffers for a file of {stream.Length / 1024} KB");

        // And a page still gives up its own text, entry by entry.
        var rewritten = Encoding.Latin1.GetString(pages[^1].With("Annots", "[9 0 R]")!);
        Assert.StartsWith("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100]", rewritten, StringComparison.Ordinal);
        Assert.EndsWith("/Annots [9 0 R] >>", rewritten, StringComparison.Ordinal);
        Assert.Equal(66_000, rewritten.Count(ch => ch == 'A') - 1);
        Assert.Equal("[0 0 200 100]", pages[0].Raw("MediaBox"));
        GC.KeepAlive(reader);
    }

    // ------------------------------------------------------------------
    // An object stream's header is read once.
    // ------------------------------------------------------------------

    // Each member read parsed the whole header again: 600 pages in a
    // stream of 5,000 members allocated 769 MB, and 3.0.0 read it once.
    [Fact]
    public void AnObjectStreamsHeaderIsReadOnceHoweverManyMembersAreRead()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "packed.pdf");
        const int PageCount = 600;
        using (var file = new Writer(source))
        {
            file.Obj(1, Catalog);
            var members = new List<(int Number, string Body)>
            {
                (2, $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(10, PageCount).Select(number => $"{number} 0 R"))}] /Count {PageCount} >>")
            };
            members.AddRange(Enumerable.Range(10, PageCount).Select(number => (number, Page)));
            members.AddRange(Enumerable.Range(10 + PageCount, 5000 - members.Count).Select(number => (number, "<< /Is /Filler >>")));
            file.Packed(3, members);
            file.End(file.IndexStream(4, 5100, "/Root 1 0 R"));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        var check = PdfFillSignWriter.Preflight(source);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new PdfSignCheck(PageCount, null), check);
        Assert.True(allocated < 32 * 1024 * 1024, $"reading {PageCount} pages out of one object stream allocated {allocated / 1024 / 1024} MB");
        AssertSigns(source, PageCount);
    }

    // One build of the viewer finds a packed object by its place in the
    // stream, another by its number: a number two members carry is two pages.
    [Fact]
    public void APageWhoseNumberTwoMembersCarryIsRefused()
    {
        using var temp = new TempDirectory();
        var twice = Path.Combine(temp.Path, "twice.pdf");
        var once = Path.Combine(temp.Path, "once.pdf");
        const string OtherPage = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 200] /Resources << >> >>";
        using (var file = new Writer(twice))
        {
            file.Obj(1, Catalog).Obj(2, OnePage).Packed(5, [(3, Page), (3, OtherPage)]);
            file.End(file.IndexStream(6, 7, "/Root 1 0 R"));
        }

        using (var file = new Writer(once))
        {
            file.Obj(1, Catalog).Obj(2, OnePage).Packed(5, [(3, Page), (4, OtherPage)]);
            file.End(file.IndexStream(6, 7, "/Root 1 0 R"));
        }

        Assert.Equal(PdfFillSignWriter.UnsupportedStructure, PdfFillSignWriter.Preflight(twice).Problem);
        AssertSigns(once, pages: 1);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    /// <summary>The file reads, a stamp saves onto page one, and the copy reads again.</summary>
    private static void AssertSigns(string source, int pages)
    {
        var output = Path.ChangeExtension(source, ".signed.pdf");
        Assert.Equal(new PdfSignCheck(pages, null), PdfFillSignWriter.Preflight(source));
        PdfFillSignWriter.WriteSignedCopy(source, output, [new SignPlacement(SignPlacementKind.Stamp, 0, 0.5, 0.2, 0.3, 0.5, null, OnePixelPng())]);
        Assert.Equal(new PdfSignCheck(pages, null), PdfFillSignWriter.Preflight(output));
    }

    private static byte[] OnePixelPng()
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>A page tree whose root holds <paramref name="nodes"/> nodes with no pages in them, and then one page.</summary>
    private static void WriteTreeOfEmptyNodes(string path, int nodes)
    {
        using var file = new Writer(path);
        file.Obj(1, Catalog);
        var kids = new StringBuilder();
        for (var i = 0; i < nodes; i++)
        {
            kids.Append(20 + i).Append(" 0 R ");
        }

        file.Obj(2, $"<< /Type /Pages /Kids [{kids}3 0 R] /Count 1 >>").Obj(3, Page);

        // Packed 50,000 to a stream, so the file stays small and reads fast.
        for (var first = 0; first < nodes; first += 50_000)
        {
            file.Packed(4 + first / 50_000, Enumerable.Range(20 + first, Math.Min(50_000, nodes - first)).Select(number => (number, "<< /Kids [] >>")));
        }

        file.End(file.IndexStream(19, 20 + nodes, "/Root 1 0 R"));
    }

    /// <summary>One page, and an index stream of <paramref name="rows"/> rows written again in every revision.</summary>
    private static void WriteChainOfIndexStreams(string path, int sections, int rows)
    {
        using var file = new Writer(path);
        file.Obj(1, Catalog).Obj(2, OnePage).Obj(3, Page);
        var newest = file.IndexStream(4, rows, "/Root 1 0 R");
        for (var section = 1; section < sections; section++)
        {
            file.Text($"startxref\n{newest}\n%%EOF\n");
            newest = file.IndexStream(4 + section, rows, $"/Root 1 0 R /Prev {newest}");
        }

        file.End(newest);
    }

    /// <summary>True when the reader for the page shape loads every section of the file's index.</summary>
    private static bool ReadsItsWholeIndex(string path)
    {
        using var stream = File.OpenRead(path);
        var reader = PdfReader.ForShape(stream);
        return reader.TryLoadIndex() && reader.TryFollowAsViewer();
    }

    /// <summary>A PDF written straight to a file, with an index of any length.</summary>
    private sealed class Writer : IDisposable
    {
        private readonly FileStream _file;
        private readonly SortedDictionary<int, long> _offsets = [];
        private readonly Dictionary<int, (int Stream, int Index)> _packed = [];

        public Writer(string path)
        {
            _file = File.Create(path);
            Text("%PDF-1.5\n");
        }

        public Writer Text(string text)
        {
            _file.Write(Encoding.Latin1.GetBytes(text));
            return this;
        }

        public Writer Obj(int number, string body)
        {
            _offsets[number] = _file.Position;
            return Text($"{number} 0 obj\n{body}\nendobj\n");
        }

        /// <summary>A classic table of <paramref name="rows"/> rows, free where no object was written. Returns where it starts.</summary>
        public long Table(int rows, string trailer)
        {
            var at = _file.Position;
            var text = new StringBuilder($"xref\n0 {rows}\n");
            for (var number = 0; number < rows; number++)
            {
                text.Append(_offsets.TryGetValue(number, out var offset)
                    ? $"{offset:0000000000} 00000 n \n"
                    : number == 0 ? "0000000000 65535 f \n" : "0000000000 00000 f \n");
            }

            Text(text.ToString());
            Text($"trailer\n<< /Size {rows} {trailer} >>\n");
            return at;
        }

        /// <summary>A deflated object stream of the given members.</summary>
        public Writer Packed(int number, IEnumerable<(int Number, string Body)> members)
        {
            var header = new StringBuilder();
            var bodies = new StringBuilder();
            var count = 0;
            foreach (var (member, body) in members)
            {
                header.Append(member).Append(' ').Append(bodies.Length).Append(' ');
                bodies.Append(body).Append('\n');
                _packed[member] = (number, count++);
            }

            var data = Deflate(Encoding.Latin1.GetBytes(header.ToString() + bodies));
            _offsets[number] = _file.Position;
            Text($"{number} 0 obj\n<< /Type /ObjStm /N {count} /First {header.Length} /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
            _file.Write(data);
            return Text("\nendstream\nendobj\n");
        }

        /// <summary>
        /// A deflated index stream of <paramref name="rows"/> rows over everything
        /// written so far, free where nothing was. Returns where it starts.
        /// </summary>
        public long IndexStream(int number, int rows, string trailer)
        {
            var at = _file.Position;
            _offsets[number] = at;
            var table = new byte[rows * 7];
            foreach (var (listed, offset) in _offsets)
            {
                Row(table, listed, 1, offset, 0);
            }

            foreach (var (listed, place) in _packed)
            {
                Row(table, listed, 2, place.Stream, place.Index);
            }

            var data = Deflate(table);
            Text($"{number} 0 obj\n<< /Type /XRef /Size {rows} /W [1 4 2] {trailer} /Filter /FlateDecode /Length {data.Length} >>\nstream\n");
            _file.Write(data);
            Text("\nendstream\nendobj\n");
            return at;
        }

        public void End(long startXref) => Text($"startxref\n{startXref}\n%%EOF\n");

        public void Dispose() => _file.Dispose();

        private static void Row(byte[] table, int number, byte kind, long field, int index)
        {
            var at = number * 7;
            table[at] = kind;
            table[at + 1] = (byte)(field >> 24);
            table[at + 2] = (byte)(field >> 16);
            table[at + 3] = (byte)(field >> 8);
            table[at + 4] = (byte)field;
            table[at + 5] = (byte)(index >> 8);
            table[at + 6] = (byte)index;
        }

        private static byte[] Deflate(byte[] bytes)
        {
            using var output = new MemoryStream();
            using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(bytes);
            }

            return output.ToArray();
        }
    }

    /// <summary>A stream that remembers every buffer it was asked to read into, without keeping one alive.</summary>
    private sealed class WatchedStream(Stream inner) : Stream
    {
        private readonly List<WeakReference<byte[]>> _buffers = [];

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        /// <summary>The total size of the buffers something still holds.</summary>
        public long BytesStillHeld() =>
            _buffers.Sum(buffer => buffer.TryGetTarget(out var bytes) ? (long)bytes.Length : 0);

        public override int Read(byte[] buffer, int offset, int count)
        {
            // One buffer can take several reads to fill.
            if (_buffers.Count == 0 || !_buffers[^1].TryGetTarget(out var last) || !ReferenceEquals(last, buffer))
            {
                _buffers.Add(new WeakReference<byte[]>(buffer));
            }

            return inner.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
