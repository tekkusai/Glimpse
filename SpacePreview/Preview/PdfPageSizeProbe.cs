using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SpacePreview.Preview;

/// <summary>The first page's shape in points and the document's page count (0 when unknown).</summary>
internal readonly record struct PdfOutline(double WidthPoints, double HeightPoints, int PageCount);

/// <summary>
/// The first page's shape and the page count, read before anything renders:
/// the shape sizes the stage and the count decides the page rail, both
/// before the viewer lays out. It follows the file's own index (startxref,
/// the cross-reference sections, the catalog, the first leaf of the page
/// tree) and reads only those objects. A damaged index falls back to a scan
/// of a small prefix. Answers are remembered per file version, so the stage
/// fit, the rail and a converted document's second look all share one read.
/// </summary>
internal static class PdfPageSizeProbe
{
    private const int TailBytes = 4096;
    private const int ScanPrefixBytes = 256 * 1024;
    private const int ScanSuffixBytes = 64 * 1024;
    private const int MaxObjectBytes = 1024 * 1024;
    private const int MaxStreamBytes = 16 * 1024 * 1024;
    private const int MaxSections = 64;
    private const int MaxTreeDepth = 32;
    private const int MemoLimit = 256;
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

    private static readonly ConcurrentDictionary<string, Lazy<PdfOutline?>> Memo =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The remembered read, started on the pool when it is new.</summary>
    public static Task<PdfOutline?> ReadAsync(string path)
    {
        if (MemoFor(path) is not { } entry)
        {
            return Task.FromResult<PdfOutline?>(null);
        }

        return entry.IsValueCreated
            ? Task.FromResult(entry.Value)
            : Task.Run(() => entry.Value);
    }

    /// <summary>The remembered read, computed on this thread when it is new.</summary>
    public static PdfOutline? Read(string path) => MemoFor(path)?.Value;

    public static bool TryReadFirstPageSize(string path, out double widthPoints, out double heightPoints)
    {
        var outline = Read(path);
        widthPoints = outline?.WidthPoints ?? 0;
        heightPoints = outline?.HeightPoints ?? 0;
        return outline is not null;
    }

    private static Lazy<PdfOutline?>? MemoFor(string path)
    {
        string key;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            return null;
        }

        if (Memo.TryGetValue(key, out var known))
        {
            return known;
        }

        if (Memo.Count >= MemoLimit)
        {
            Memo.Clear();
        }

        return Memo.GetOrAdd(key, _ => new Lazy<PdfOutline?>(() => ReadUncached(path)));
    }

    internal static PdfOutline? ReadUncached(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 8)
            {
                return null;
            }

            var document = new PdfIndex(stream, Stopwatch.StartNew());
            return document.TryLoadIndex() && document.TryReadOutline() is { } outline
                ? outline
                : document.TryScanPrefix() ? document.TryReadOutline() : null;
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct PdfRef(int Number, int Generation);

    private readonly record struct PdfName(string Value);

    /// <summary>A string's bytes are never needed here, and are encrypted in protected files.</summary>
    private sealed class PdfString
    {
        public static readonly PdfString Instance = new();
    }

    private sealed class PdfStream(Dictionary<string, object?> dictionary, byte[] data)
    {
        public Dictionary<string, object?> Dictionary { get; } = dictionary;
        public byte[] Data { get; } = data;
    }

    /// <summary>Kind 1 lives at an offset; kind 2 is entry Index of object stream StreamNumber.</summary>
    private readonly record struct XrefEntry(int Kind, long Offset, int StreamNumber, int Index);

    /// <summary>A classic table's rows are read only when an object in them is asked for.</summary>
    private readonly record struct XrefRows(int First, int Count, long Offset, int Stride);

    private sealed class XrefSection
    {
        public List<XrefRows> Rows { get; } = [];
        public Dictionary<int, XrefEntry> Entries { get; } = [];
    }

    private sealed class TruncatedException : Exception;

    /// <summary>
    /// One file's index: the cross-reference sections newest first, each table
    /// followed by its hybrid stream, then the /Prev chain. A free entry
    /// keeps the search going, because hybrid files list their compressed
    /// objects as free in the table that older readers see.
    /// </summary>
    private sealed class PdfIndex(Stream stream, Stopwatch clock)
    {
        private readonly List<XrefSection> _sections = [];
        private readonly Dictionary<int, object?> _objects = [];
        private PdfRef? _root;
        private bool _encrypted;

        public bool TryLoadIndex()
        {
            var tail = ReadAt(Math.Max(0, stream.Length - TailBytes), TailBytes);
            var marker = LastIndexOf(tail, "startxref"u8);
            if (marker < 0)
            {
                return false;
            }

            var lexer = new Lexer(tail, marker + "startxref".Length);
            if (lexer.ReadValue() is not double start)
            {
                return false;
            }

            var seen = new HashSet<long>();
            long? next = (long)start;
            while (next is { } offset && _sections.Count < MaxSections && seen.Add(offset))
            {
                CheckBudget();
                if (LoadSection(offset) is not { } trailer)
                {
                    break;
                }

                _root ??= trailer.GetValueOrDefault("Root") as PdfRef?;
                _encrypted |= trailer.ContainsKey("Encrypt");
                if (trailer.GetValueOrDefault("XRefStm") is double hybrid && seen.Add((long)hybrid))
                {
                    LoadSection((long)hybrid);
                }

                next = trailer.GetValueOrDefault("Prev") is double previous ? (long)previous : null;
            }

            return _sections.Count > 0 && _root is not null;
        }

        /// <summary>
        /// For a file whose index cannot be followed: every object that opens
        /// in a small prefix or suffix, later definitions winning, and the
        /// catalog named by a trailer or found by type.
        /// </summary>
        public bool TryScanPrefix()
        {
            _sections.Clear();
            _objects.Clear();
            var section = new XrefSection();
            var prefix = ReadAt(0, ScanPrefixBytes);
            IndexObjects(section, prefix, 0);
            if (stream.Length > ScanPrefixBytes)
            {
                var suffixStart = Math.Max(ScanPrefixBytes, stream.Length - ScanSuffixBytes);
                var suffix = ReadAt(suffixStart, ScanSuffixBytes);
                IndexObjects(section, suffix, suffixStart);
                _root ??= FindTrailerRoot(suffix);
            }

            _root ??= FindTrailerRoot(prefix);
            _sections.Add(section);
            if (_root is null)
            {
                foreach (var (number, _) in section.Entries.OrderBy(pair => pair.Value.Offset))
                {
                    CheckBudget();
                    if (ReadObject(number) is Dictionary<string, object?> candidate && TypeOf(candidate) == "Catalog")
                    {
                        _root = new PdfRef(number, 0);
                        break;
                    }
                }
            }

            return section.Entries.Count > 0;
        }

        public PdfOutline? TryReadOutline()
        {
            if (_root is { } root
                && ReadObject(root.Number) is Dictionary<string, object?> catalog
                && catalog.GetValueOrDefault("Pages") is PdfRef pagesRef
                && ReadObject(pagesRef.Number) is Dictionary<string, object?> pages)
            {
                var count = Resolve(pages.GetValueOrDefault("Count")) is double total && total >= 1 && total <= int.MaxValue
                    ? (int)total
                    : 0;
                return FirstLeaf(pages, null, null, null, 0, []) is { } shape
                    ? shape with { PageCount = count }
                    : null;
            }

            return null;
        }

        private PdfOutline? FirstLeaf(
            Dictionary<string, object?> node,
            double[]? mediaBox,
            double[]? cropBox,
            int? rotate,
            int depth,
            HashSet<int> seen)
        {
            CheckBudget();
            if (depth > MaxTreeDepth)
            {
                return null;
            }

            mediaBox = Box(node, "MediaBox") ?? mediaBox;
            cropBox = Box(node, "CropBox") ?? cropBox;
            rotate = Resolve(node.GetValueOrDefault("Rotate")) is double turn ? (int)turn : rotate;
            if (Resolve(node.GetValueOrDefault("Kids")) is not List<object?> kids)
            {
                return TypeOf(node) == "Pages" ? null : Shape(cropBox ?? mediaBox, rotate, 0);
            }

            foreach (var kid in kids)
            {
                if (kid is PdfRef kidRef
                    && seen.Add(kidRef.Number)
                    && ReadObject(kidRef.Number) is Dictionary<string, object?> child
                    && FirstLeaf(child, mediaBox, cropBox, rotate, depth + 1, seen) is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        private double[]? Box(Dictionary<string, object?> node, string key)
        {
            if (Resolve(node.GetValueOrDefault(key)) is not List<object?> { Count: >= 4 } values)
            {
                return null;
            }

            var box = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (Resolve(values[i]) is not double value || !double.IsFinite(value))
                {
                    return null;
                }

                box[i] = value;
            }

            return box;
        }

        private static PdfOutline? Shape(double[]? box, int? rotate, int count)
        {
            if (box is null)
            {
                return null;
            }

            var width = Math.Abs(box[2] - box[0]);
            var height = Math.Abs(box[3] - box[1]);
            if (width < 1 || height < 1 || width > 20000 || height > 20000)
            {
                return null;
            }

            var turn = (((rotate ?? 0) % 360) + 360) % 360;
            return turn is 90 or 270
                ? new PdfOutline(height, width, count)
                : new PdfOutline(width, height, count);
        }

        private static string? TypeOf(Dictionary<string, object?> node) =>
            (node.GetValueOrDefault("Type") as PdfName?)?.Value;

        private object? Resolve(object? value) =>
            value is PdfRef reference ? ReadObject(reference.Number) : value;

        private object? ReadObject(int number)
        {
            if (_objects.TryGetValue(number, out var known))
            {
                return known;
            }

            CheckBudget();
            _objects[number] = null; // a reference cycle reads as missing
            object? value = null;
            if (Find(number) is { } entry)
            {
                value = entry.Kind == 1
                    ? ReadIndirect(entry.Offset)?.Value
                    : ReadFromObjectStream(entry.StreamNumber, entry.Index);
            }

            _objects[number] = value;
            return value;
        }

        private XrefEntry? Find(int number)
        {
            foreach (var section in _sections)
            {
                if (section.Entries.TryGetValue(number, out var entry) && entry.Kind != 0)
                {
                    return entry;
                }

                foreach (var rows in section.Rows)
                {
                    if (number >= rows.First && number < rows.First + rows.Count
                        && ReadTableEntry(rows.Offset + (long)(number - rows.First) * rows.Stride) is { Kind: not 0 } row)
                    {
                        return row;
                    }
                }
            }

            return null;
        }

        private XrefEntry? ReadTableEntry(long offset)
        {
            var bytes = ReadAt(offset, 20);
            var lexer = new Lexer(bytes, 0);
            try
            {
                return lexer.ReadValue() is double position
                    && lexer.ReadValue() is double
                    && lexer.ReadKeyword() == "n"
                        ? new XrefEntry(1, (long)position, 0, 0)
                        : null;
            }
            catch (Exception ex) when (ex is FormatException or TruncatedException)
            {
                return null;
            }
        }

        /// <summary>"N G obj value", read with a window that grows until the value fits.</summary>
        private (object? Value, long DataStart)? ReadIndirect(long offset)
        {
            for (var window = 4096; window <= MaxObjectBytes; window *= 16)
            {
                var bytes = ReadAt(offset, window);
                var lexer = new Lexer(bytes, 0);
                try
                {
                    if (lexer.ReadValue() is not double || lexer.ReadValue() is not double || lexer.ReadKeyword() != "obj")
                    {
                        return null;
                    }

                    var value = lexer.ReadValue();
                    if (value is Dictionary<string, object?> dictionary && lexer.TryReadStreamKeyword(out var dataStart))
                    {
                        return (ReadStream(dictionary, offset + dataStart), offset + dataStart);
                    }

                    return (value, 0);
                }
                catch (TruncatedException) when (bytes.Length == window)
                {
                    // The object runs past this window; read a larger one.
                }
                catch (Exception ex) when (ex is FormatException or TruncatedException)
                {
                    return null;
                }
            }

            return null;
        }

        private PdfStream? ReadStream(Dictionary<string, object?> dictionary, long dataStart)
        {
            if (Resolve(dictionary.GetValueOrDefault("Length")) is not double length || length < 0 || length > MaxStreamBytes)
            {
                return null;
            }

            var raw = ReadAt(dataStart, (int)length);
            return raw.Length == (int)length && Decode(dictionary, raw) is { } data
                ? new PdfStream(dictionary, data)
                : null;
        }

        private object? ReadFromObjectStream(int streamNumber, int index)
        {
            // Protected files encrypt object streams, so their bytes would be
            // noise. Top-level dictionaries are never encrypted.
            if (_encrypted
                || ReadObject(streamNumber) is not PdfStream objectStream
                || TypeOf(objectStream.Dictionary) != "ObjStm"
                || objectStream.Dictionary.GetValueOrDefault("N") is not double count
                || objectStream.Dictionary.GetValueOrDefault("First") is not double first
                || index < 0
                || index >= count)
            {
                return null;
            }

            var data = objectStream.Data;
            try
            {
                var header = new Lexer(data, 0);
                long offset = -1;
                for (var i = 0; i <= index; i++)
                {
                    header.ReadValue();
                    offset = header.ReadValue() is double position ? (long)position : -1;
                }

                return offset < 0 || first + offset >= data.Length
                    ? null
                    : new Lexer(data, (int)(first + offset)).ReadValue();
            }
            catch (Exception ex) when (ex is FormatException or TruncatedException)
            {
                return null;
            }
        }

        /// <summary>A table section's trailer or a cross-reference stream's dictionary.</summary>
        private Dictionary<string, object?>? LoadSection(long offset)
        {
            if (offset <= 0 || offset >= stream.Length)
            {
                return null;
            }

            var head = ReadAt(offset, 64);
            var lexer = new Lexer(head, 0);
            try
            {
                var at = lexer.SkipWhitespace();
                if (lexer.PeekKeyword() == "xref")
                {
                    return LoadTable(offset + at + "xref".Length);
                }
            }
            catch (Exception ex) when (ex is FormatException or TruncatedException)
            {
                return null;
            }

            if (ReadIndirect(offset)?.Value is not PdfStream xref || TypeOf(xref.Dictionary) != "XRef")
            {
                return null;
            }

            var section = new XrefSection();
            if (!ReadXrefStream(xref, section))
            {
                return null;
            }

            _sections.Add(section);
            return xref.Dictionary;
        }

        private Dictionary<string, object?>? LoadTable(long position)
        {
            var section = new XrefSection();
            while (section.Rows.Count < 4096)
            {
                CheckBudget();
                var bytes = ReadAt(position, 256);
                var lexer = new Lexer(bytes, 0);
                try
                {
                    var at = lexer.SkipWhitespace();
                    if (lexer.PeekKeyword() == "trailer")
                    {
                        var dictionaryStart = position + at + "trailer".Length;
                        for (var window = 4096; window <= MaxObjectBytes; window *= 16)
                        {
                            var trailerBytes = ReadAt(dictionaryStart, window);
                            try
                            {
                                if (new Lexer(trailerBytes, 0).ReadValue() is Dictionary<string, object?> trailer)
                                {
                                    _sections.Add(section);
                                    return trailer;
                                }

                                return null;
                            }
                            catch (TruncatedException) when (trailerBytes.Length == window)
                            {
                                // A long trailer; read a larger window.
                            }
                        }

                        return null;
                    }

                    if (lexer.ReadValue() is not double first || lexer.ReadValue() is not double count || first < 0 || count < 0)
                    {
                        return null;
                    }

                    var entryStart = lexer.SkipWhitespace();
                    var stride = EntryStride(bytes, entryStart);
                    if (count > 0 && stride == 0)
                    {
                        return null;
                    }

                    section.Rows.Add(new XrefRows((int)first, (int)count, position + entryStart, stride));
                    position += entryStart + (long)count * stride;
                }
                catch (Exception ex) when (ex is FormatException or TruncatedException)
                {
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// The bytes per table entry: 20 by the spec, 19 from writers that end
        /// lines with a bare newline. Measured on the first entry.
        /// </summary>
        private static int EntryStride(byte[] bytes, int start)
        {
            var end = start + 18;
            if (end > bytes.Length)
            {
                return 0;
            }

            while (end < bytes.Length && end < start + 21 && bytes[end] is (byte)' ' or (byte)'\r' or (byte)'\n')
            {
                end++;
            }

            return end - start is 19 or 20 ? end - start : 0;
        }

        private bool ReadXrefStream(PdfStream xref, XrefSection section)
        {
            if (xref.Dictionary.GetValueOrDefault("W") is not List<object?> { Count: 3 } widths
                || widths.Any(width => width is not double value || value < 0 || value > 8))
            {
                return false;
            }

            var w = widths.Select(width => (int)(double)width!).ToArray();
            var rowBytes = w.Sum();
            if (rowBytes == 0)
            {
                return false;
            }

            var index = xref.Dictionary.GetValueOrDefault("Index") as List<object?>
                ?? [0d, xref.Dictionary.GetValueOrDefault("Size")];
            var data = xref.Data;
            var row = 0;
            for (var pair = 0; pair + 1 < index.Count; pair += 2)
            {
                if (index[pair] is not double first || index[pair + 1] is not double count)
                {
                    return false;
                }

                for (var i = 0; i < count; i++, row++)
                {
                    var at = row * rowBytes;
                    if (at + rowBytes > data.Length)
                    {
                        return section.Entries.Count > 0;
                    }

                    var kind = w[0] == 0 ? 1 : Field(data, at, w[0]);
                    var second = Field(data, at + w[0], w[1]);
                    var third = Field(data, at + w[0] + w[1], w[2]);
                    var number = (int)first + i;
                    section.Entries.TryAdd(number, kind switch
                    {
                        1 => new XrefEntry(1, second, 0, 0),
                        2 => new XrefEntry(2, 0, (int)second, (int)third),
                        _ => new XrefEntry(0, 0, 0, 0)
                    });
                }
            }

            return true;
        }

        private static long Field(byte[] data, int at, int width)
        {
            long value = 0;
            for (var i = 0; i < width; i++)
            {
                value = (value << 8) | data[at + i];
            }

            return value;
        }

        /// <summary>FlateDecode with an optional PNG predictor, which is what indexes use.</summary>
        private static byte[]? Decode(Dictionary<string, object?> dictionary, byte[] raw)
        {
            var filter = dictionary.GetValueOrDefault("Filter") switch
            {
                null => null,
                PdfName name => name.Value,
                List<object?> { Count: 1 } list when list[0] is PdfName only => only.Value,
                _ => "unsupported"
            };
            if (filter is null)
            {
                return raw;
            }

            if (filter != "FlateDecode")
            {
                return null;
            }

            byte[] inflated;
            try
            {
                using var input = new MemoryStream(raw);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[16 * 1024];
                int read;
                while ((read = zlib.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    if (output.Length > MaxStreamBytes)
                    {
                        return null;
                    }
                }

                inflated = output.ToArray();
            }
            catch (InvalidDataException)
            {
                return null;
            }

            var parameters = dictionary.GetValueOrDefault("DecodeParms") switch
            {
                Dictionary<string, object?> single => single,
                List<object?> { Count: 1 } list => list[0] as Dictionary<string, object?>,
                _ => null
            };
            var predictor = parameters?.GetValueOrDefault("Predictor") is double p ? (int)p : 1;
            if (predictor < 10)
            {
                return predictor == 1 ? inflated : null;
            }

            var columns = parameters?.GetValueOrDefault("Columns") is double c ? (int)c : 1;
            return columns <= 0 ? null : UndoPngPredictor(inflated, columns);
        }

        private static byte[]? UndoPngPredictor(byte[] data, int columns)
        {
            var stride = columns + 1;
            var rows = data.Length / stride;
            var output = new byte[rows * columns];
            for (var row = 0; row < rows; row++)
            {
                var type = data[row * stride];
                for (var column = 0; column < columns; column++)
                {
                    var value = data[row * stride + 1 + column];
                    var left = column > 0 ? output[row * columns + column - 1] : 0;
                    var up = row > 0 ? output[(row - 1) * columns + column] : 0;
                    var upLeft = row > 0 && column > 0 ? output[(row - 1) * columns + column - 1] : 0;
                    output[row * columns + column] = type switch
                    {
                        0 => value,
                        1 => (byte)(value + left),
                        2 => (byte)(value + up),
                        3 => (byte)(value + (left + up) / 2),
                        4 => (byte)(value + Paeth(left, up, upLeft)),
                        _ => throw new FormatException()
                    };
                }
            }

            return output;
        }

        private static int Paeth(int left, int up, int upLeft)
        {
            var estimate = left + up - upLeft;
            var toLeft = Math.Abs(estimate - left);
            var toUp = Math.Abs(estimate - up);
            var toUpLeft = Math.Abs(estimate - upLeft);
            return toLeft <= toUp && toLeft <= toUpLeft ? left : toUp <= toUpLeft ? up : upLeft;
        }

        private void IndexObjects(XrefSection section, byte[] bytes, long baseOffset)
        {
            for (var i = 0; i + 3 < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'o' || bytes[i + 1] != (byte)'b' || bytes[i + 2] != (byte)'j'
                    || (i + 3 < bytes.Length && !Lexer.IsDelimiterOrSpace(bytes[i + 3])))
                {
                    continue;
                }

                // Walk back over "N G " to the object's start.
                var start = i - 1;
                var numbers = 0;
                while (numbers < 2 && start >= 0)
                {
                    while (start >= 0 && Lexer.IsSpace(bytes[start]))
                    {
                        start--;
                    }

                    var end = start;
                    while (start >= 0 && bytes[start] is >= (byte)'0' and <= (byte)'9')
                    {
                        start--;
                    }

                    if (start == end)
                    {
                        break;
                    }

                    numbers++;
                }

                if (numbers == 2 && int.TryParse(ObjectNumber(bytes, start + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    section.Entries[number] = new XrefEntry(1, baseOffset + start + 1, 0, 0);
                }
            }
        }

        private static string ObjectNumber(byte[] bytes, int start)
        {
            var end = start;
            while (end < bytes.Length && bytes[end] is >= (byte)'0' and <= (byte)'9')
            {
                end++;
            }

            return Encoding.ASCII.GetString(bytes, start, end - start);
        }

        private static PdfRef? FindTrailerRoot(byte[] bytes)
        {
            var at = LastIndexOf(bytes, "trailer"u8);
            if (at < 0)
            {
                return null;
            }

            try
            {
                return new Lexer(bytes, at + "trailer".Length).ReadValue() is Dictionary<string, object?> trailer
                    ? trailer.GetValueOrDefault("Root") as PdfRef?
                    : null;
            }
            catch (Exception ex) when (ex is FormatException or TruncatedException)
            {
                return null;
            }
        }

        private byte[] ReadAt(long offset, int length)
        {
            if (offset < 0 || offset >= stream.Length)
            {
                return [];
            }

            var count = (int)Math.Min(length, stream.Length - offset);
            var buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            var read = 0;
            while (read < count)
            {
                var got = stream.Read(buffer, read, count - read);
                if (got == 0)
                {
                    break;
                }

                read += got;
            }

            return read == count ? buffer : buffer[..read];
        }

        private void CheckBudget()
        {
            if (clock.Elapsed > Budget)
            {
                throw new TimeoutException("The PDF probe ran out of time.");
            }
        }

        private static int LastIndexOf(byte[] bytes, ReadOnlySpan<byte> needle) =>
            bytes.AsSpan().LastIndexOf(needle);
    }

    /// <summary>The PDF object syntax, over bytes. Running off the end throws TruncatedException.</summary>
    private sealed class Lexer(byte[] bytes, int position)
    {
        public int Position => position;

        public static bool IsSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

        public static bool IsDelimiterOrSpace(byte value) =>
            IsSpace(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        public int SkipWhitespace()
        {
            while (position < bytes.Length)
            {
                if (IsSpace(bytes[position]))
                {
                    position++;
                }
                else if (bytes[position] == (byte)'%')
                {
                    while (position < bytes.Length && bytes[position] is not (10 or 13))
                    {
                        position++;
                    }
                }
                else
                {
                    break;
                }
            }

            return position;
        }

        public string? PeekKeyword()
        {
            var start = position;
            var keyword = ReadKeyword();
            position = start;
            return keyword;
        }

        public string? ReadKeyword()
        {
            SkipWhitespace();
            var start = position;
            while (position < bytes.Length && !IsDelimiterOrSpace(bytes[position]))
            {
                position++;
            }

            return position == start ? null : Encoding.ASCII.GetString(bytes, start, position - start);
        }

        /// <summary>After a stream dictionary: "stream" and its end of line, then the data's offset.</summary>
        public bool TryReadStreamKeyword(out int dataStart)
        {
            dataStart = 0;
            if (SkipWhitespace() + "stream".Length > bytes.Length)
            {
                throw new TruncatedException();
            }

            if (ReadKeyword() != "stream")
            {
                return false;
            }

            if (position < bytes.Length && bytes[position] == 13)
            {
                position++;
            }

            if (position < bytes.Length && bytes[position] == 10)
            {
                position++;
            }

            dataStart = position;
            return true;
        }

        public object? ReadValue()
        {
            SkipWhitespace();
            if (position >= bytes.Length)
            {
                throw new TruncatedException();
            }

            switch (bytes[position])
            {
                case (byte)'<' when position + 1 < bytes.Length && bytes[position + 1] == (byte)'<':
                    return ReadDictionary();
                case (byte)'<':
                    SkipPast((byte)'>');
                    return PdfString.Instance;
                case (byte)'[':
                    return ReadArray();
                case (byte)'(':
                    SkipLiteralString();
                    return PdfString.Instance;
                case (byte)'/':
                    return ReadName();
            }

            var token = ReadKeyword() ?? throw new FormatException();
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                return token switch
                {
                    "true" => true,
                    "false" => false,
                    "null" => null,
                    _ => throw new FormatException()
                };
            }

            // "N G R" is a reference; anything else leaves the number alone.
            var resume = position;
            if (number >= 0 && number == Math.Floor(number) && position < bytes.Length)
            {
                try
                {
                    if (ReadKeyword() is { } generation
                        && int.TryParse(generation, NumberStyles.None, CultureInfo.InvariantCulture, out var generationNumber)
                        && ReadKeyword() == "R")
                    {
                        return new PdfRef((int)number, generationNumber);
                    }
                }
                catch (TruncatedException)
                {
                    // A number at the very end of the window stays a number.
                }
            }

            position = resume;
            return number;
        }

        private Dictionary<string, object?> ReadDictionary()
        {
            position += 2;
            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (position + 1 >= bytes.Length)
                {
                    throw new TruncatedException();
                }

                if (bytes[position] == (byte)'>' && bytes[position + 1] == (byte)'>')
                {
                    position += 2;
                    return dictionary;
                }

                if (ReadValue() is not PdfName key)
                {
                    throw new FormatException();
                }

                dictionary[key.Value] = ReadValue();
            }
        }

        private List<object?> ReadArray()
        {
            position++;
            var values = new List<object?>();
            while (true)
            {
                SkipWhitespace();
                if (position >= bytes.Length)
                {
                    throw new TruncatedException();
                }

                if (bytes[position] == (byte)']')
                {
                    position++;
                    return values;
                }

                values.Add(ReadValue());
            }
        }

        private PdfName ReadName()
        {
            position++;
            var builder = new StringBuilder();
            while (position < bytes.Length && !IsDelimiterOrSpace(bytes[position]))
            {
                if (bytes[position] == (byte)'#' && position + 2 < bytes.Length
                    && int.TryParse(Encoding.ASCII.GetString(bytes, position + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var escaped))
                {
                    builder.Append((char)escaped);
                    position += 3;
                    continue;
                }

                builder.Append((char)bytes[position]);
                position++;
            }

            return new PdfName(builder.ToString());
        }

        private void SkipLiteralString()
        {
            position++;
            var depth = 1;
            while (position < bytes.Length)
            {
                switch (bytes[position])
                {
                    case (byte)'\\':
                        position += 2;
                        continue;
                    case (byte)'(':
                        depth++;
                        break;
                    case (byte)')' when --depth == 0:
                        position++;
                        return;
                }

                position++;
            }

            throw new TruncatedException();
        }

        private void SkipPast(byte terminator)
        {
            var end = Array.IndexOf(bytes, terminator, position);
            if (end < 0)
            {
                throw new TruncatedException();
            }

            position = end + 1;
        }
    }
}
