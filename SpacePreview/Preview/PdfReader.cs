using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SpacePreview.Preview;

internal readonly record struct PdfRef(int Number, int Generation)
{
    public override string ToString() => $"{Number} {Generation}";
}

internal readonly record struct PdfName(string Value);

/// <summary>A string's bytes are never needed here, and are encrypted in protected files.</summary>
internal sealed class PdfString
{
    public static readonly PdfString Instance = new();
}

/// <summary>
/// The bytes values were read from. An object is read out of a window far
/// larger than itself, so it keeps a copy of its own bytes and lets the
/// window go. Positions stay the ones the window had.
/// </summary>
internal sealed class PdfSource(byte[] bytes)
{
    private byte[] _bytes = bytes;
    private int _origin;

    public ReadOnlySpan<byte> Span(int start, int end) => _bytes.AsSpan(start - _origin, end - start);

    public void KeepOnly(int start, int end)
    {
        _bytes = _bytes[(start - _origin)..(end - _origin)];
        _origin = start;
    }
}

/// <summary>
/// A dictionary, and where each entry sits in the bytes it was read from:
/// the signer rewrites one entry of a page and copies the rest untouched.
/// A key written twice reads as its last value, as the viewer reads it.
/// </summary>
internal sealed class PdfDictionary(PdfSource source, int start)
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private HashSet<string>? _repeated;

    private readonly record struct Entry(object? Value, int KeyStart, int ValueStart, int ValueEnd);

    public int End { get; private set; }

    public bool Has(string key) => _entries.ContainsKey(key);

    public object? Get(string key) => _entries.TryGetValue(key, out var entry) ? entry.Value : null;

    public bool IsRepeated(string key) => _repeated?.Contains(key) == true;

    /// <summary>The value as it is written, or null when the key is absent.</summary>
    public string? Raw(string key) =>
        _entries.TryGetValue(key, out var entry)
            ? Encoding.Latin1.GetString(source.Span(entry.ValueStart, entry.ValueEnd))
            : null;

    /// <summary>
    /// The dictionary's text with one entry replaced, or added just before
    /// its own '>>'. Null when the key is written twice: replacing one
    /// would leave the other for the viewer to read.
    /// </summary>
    public byte[]? With(string key, string value)
    {
        if (IsRepeated(key))
        {
            return null;
        }

        var text = Encoding.Latin1.GetBytes($"/{key} {value}");
        return _entries.TryGetValue(key, out var entry)
            ? [.. source.Span(start, entry.KeyStart), .. text, .. source.Span(entry.ValueEnd, End)]
            : [.. source.Span(start, End - 2), (byte)' ', .. text, (byte)' ', (byte)'>', (byte)'>'];
    }

    public static PdfDictionary? Parse(byte[] text)
    {
        try
        {
            return new PdfLexer(text, 0).ReadValue() as PdfDictionary;
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }
    }

    internal void Add(string key, object? value, int keyStart, int valueStart, int valueEnd)
    {
        if (_entries.ContainsKey(key))
        {
            (_repeated ??= new HashSet<string>(StringComparer.Ordinal)).Add(key);
        }

        _entries[key] = new Entry(value, keyStart, valueStart, valueEnd);
    }

    internal void Close(int end) => End = end;
}

/// <summary>An array, and each item as it is written.</summary>
internal sealed class PdfArray(PdfSource source)
{
    private readonly List<(int Start, int End)> _spans = [];

    public List<object?> Items { get; } = [];

    public int Count => Items.Count;

    /// <summary>The items as they are written, a reference counting as one.</summary>
    public IEnumerable<string> Raw() =>
        _spans.Select(span => Encoding.Latin1.GetString(source.Span(span.Start, span.End)));

    internal void Add(object? value, int itemStart, int itemEnd)
    {
        Items.Add(value);
        _spans.Add((itemStart, itemEnd));
    }
}

/// <summary>
/// A stream's dictionary and where its data starts. The data is read only
/// when asked for, and so is the member table of an object stream.
/// </summary>
internal sealed class PdfStream(PdfDictionary dictionary, long dataStart)
{
    public PdfDictionary Dictionary { get; } = dictionary;

    public long DataStart { get; } = dataStart;

    internal bool DataRead { get; set; }

    internal byte[]? Data { get; set; }

    internal bool MembersRead { get; set; }

    internal PdfMembers? Members { get; set; }
}

/// <summary>
/// An object stream's header: each member's number and where it starts in
/// the data, and the numbers the header gives to more than one member.
/// </summary>
internal sealed class PdfMembers((int Number, int Offset)[] members, int[] repeated)
{
    public int Count => members.Length;

    public (int Number, int Offset) this[int index] => members[index];

    public bool IsRepeated(int number) => Array.BinarySearch(repeated, number) >= 0;
}

internal readonly record struct PdfBox(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsEmpty => !(Right > Left && Top > Bottom);
}

/// <summary>
/// A page as the viewer frames it: the crop box cut to the media box, and
/// the turn in quarters. MediaBox and CropBox are the page's own or
/// inherited entries, null when the viewer finds none it can use.
/// </summary>
internal readonly record struct PdfPageFrame(PdfBox Box, int Quarters, PdfBox? MediaBox, PdfBox? CropBox);

/// <summary>The value runs past the bytes in hand.</summary>
internal sealed class PdfTruncatedException : Exception;

/// <summary>
/// The file can be read more than one way, and the reader was asked to
/// answer only what every viewer would. The signer refuses such a file.
/// </summary>
internal sealed class PdfUnsureException : Exception;

/// <summary>
/// One reader for a PDF's structure, shared by the page-shape probe and the
/// signer so they cannot disagree about a file. It finds objects the way
/// the viewer does: through the file's own index (startxref, the
/// cross-reference sections newest first, each table followed by its hybrid
/// stream, then the /Prev chain), never by taking the last thing in the
/// file that looks like an object. Reads are bounded and timed.
///
/// The viewer has a second way in: when it cannot follow the index it
/// scans the whole file, and then a later definition wins. Which way it
/// went cannot be seen from here, so the reader has three modes. The probe
/// guesses: a wrong page shape costs a window size. The signer either
/// proves the index is one the viewer follows, or reads only objects the
/// file defines exactly once, which every way in finds the same.
/// </summary>
internal sealed class PdfReader
{
    internal const int MaxNesting = 64;
    internal const int MaxObjectDepth = 16;

    // The viewer reads no object numbered past this.
    internal const int MaxObjectNumber = 4 * 1024 * 1024;

    // The reader stops once it holds more objects than this. It reads only
    // what lies on the way to the pages, so no real file comes near; a page
    // tree of a million nodes is refused instead of read into memory. What
    // the index lists is not counted: free rows and old revisions are not
    // objects.
    internal const int MaxObjects = 200_000;

    private const int TailBytes = 4096;
    private const int ScanPrefixBytes = 256 * 1024;
    private const int ScanSuffixBytes = 64 * 1024;
    private const int MaxTreeDepth = 64;

    // The viewer takes no page count from this up.
    private const int MaxPageCount = 1048575;

    // The viewer looks for startxref in the last 4 KB. Nearer the end than
    // this, it is certain to find it.
    private const int SureTailBytes = 1024;

    private readonly Stream _stream;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TimeSpan _budget;
    private readonly int _maxObjectBytes;
    private readonly int _maxStreamBytes;
    private readonly int _maxSections;
    private readonly List<XrefSection> _sections = [];
    private readonly Dictionary<int, Loaded> _objects = [];
    private readonly List<PdfDictionary> _looseTrailers = [];
    private Mode _mode = Mode.Guess;
    private bool _streamsOnly;
    private bool _chainComplete;
    private bool _formatDoubt;
    private int _startXrefFromEnd = int.MaxValue;
    private long _headerOffset;
    private long _indexBytes;
    private int _depth;
    private ILookup<int, long>? _definitions;
    private Dictionary<int, List<XrefEntry>>? _packed;

    private PdfReader(Stream stream, TimeSpan budget, int maxObjectBytes, int maxStreamBytes, int maxSections)
    {
        _stream = stream;
        _budget = budget;
        _maxObjectBytes = maxObjectBytes;
        _maxStreamBytes = maxStreamBytes;
        _maxSections = maxSections;
    }

    /// <summary>For the page shape: a quarter of a second and small reads.</summary>
    public static PdfReader ForShape(Stream stream) =>
        new(stream, TimeSpan.FromMilliseconds(250), 1024 * 1024, 16 * 1024 * 1024, 64);

    /// <summary>For the signer, which has to read every page of a file up to 256 MB.</summary>
    public static PdfReader ForSigning(Stream stream) =>
        new(stream, TimeSpan.FromSeconds(20), 16 * 1024 * 1024, 64 * 1024 * 1024, 4096);

    private enum Mode
    {
        /// <summary>The index newest first, a free entry passed over.</summary>
        Guess,

        /// <summary>The index, proved to be one the viewer follows.</summary>
        Viewer,

        /// <summary>Only objects the file defines exactly once.</summary>
        DefinedOnce
    }

    private enum SectionKind
    {
        Table,
        Stream,

        /// <summary>The stream a table's /XRefStm names. It follows the newest table that names it.</summary>
        TableStream
    }

    /// <summary>Kind 0 is free, 1 lives at an offset, 2 is entry Index of object stream StreamNumber.</summary>
    private readonly record struct XrefEntry(int Kind, long Offset, int StreamNumber, int Index);

    /// <summary>
    /// A run of numbers a section lists, and where its rows start: in the
    /// file for a table, in the decoded data for a stream. A row is read
    /// only when an object in it is asked for, so a section costs nothing
    /// for each row it claims.
    /// </summary>
    private readonly record struct XrefRows(int First, int Count, long Offset, int Stride);

    private sealed class XrefSection(SectionKind kind, PdfDictionary trailer)
    {
        public SectionKind Kind { get; } = kind;
        public PdfDictionary Trailer { get; } = trailer;
        public List<XrefRows> Rows { get; } = [];

        /// <summary>For a stream: its decoded rows and their three field widths. A table's rows stay in the file.</summary>
        public byte[]? Data { get; set; }
        public int[] Widths { get; set; } = [];

        /// <summary>What the prefix scan found. It has no rows.</summary>
        public Dictionary<int, XrefEntry> Entries { get; } = [];

        /// <summary>For a table: the stream its /XRefStm names.</summary>
        public XrefSection? Hybrid { get; set; }
    }

    private readonly record struct Loaded(object? Value, int Generation, bool Packed);

    private readonly record struct Indirect(int Number, int Generation, object? Value);

    public PdfRef? Root { get; private set; }

    /// <summary>A trailer names /Encrypt. Strings and streams are then noise.</summary>
    public bool Encrypted { get; private set; }

    /// <summary>The offset startxref names, or -1.</summary>
    public long StartXref { get; private set; } = -1;

    /// <summary>Where "%PDF-" sits. The viewer counts every offset from there.</summary>
    public long HeaderOffset => _headerOffset;

    /// <summary>The newest cross-reference section is a stream, not a table.</summary>
    public bool NewestIsStream => _sections.Count > 0 && _sections[0].Kind == SectionKind.Stream;

    // ------------------------------------------------------------------
    // The index.
    // ------------------------------------------------------------------

    public bool TryLoadIndex()
    {
        _headerOffset = Math.Max(0, ReadAt(0, 1024).AsSpan().IndexOf("%PDF-"u8));
        var tail = ReadAt(Math.Max(0, _stream.Length - TailBytes), TailBytes);
        var marker = tail.AsSpan().LastIndexOf("startxref"u8);
        if (marker < 0 || ReadNumberAfter(tail, marker + "startxref".Length) is not { } start)
        {
            return false;
        }

        StartXref = start;
        _startXrefFromEnd = tail.Length - marker;
        _chainComplete = true;
        var seen = new HashSet<long>();
        var hybrids = new Dictionary<long, XrefSection>();
        long? next = start;
        while (next is { } offset)
        {
            CheckBudget();
            if (_sections.Count >= _maxSections || !seen.Add(offset) || LoadSection(offset, hybrid: false) is not { } trailer)
            {
                _chainComplete = false;
                break;
            }

            Root ??= trailer.Get("Root") as PdfRef?;
            Encrypted |= trailer.Has("Encrypt");
            if (trailer.Has("XRefStm"))
            {
                // Only a table has one, and the viewer gives up on a table
                // whose stream does not read. Word names one stream from
                // both of its tables: it is read once.
                var table = _sections[^1];
                var at = table.Kind == SectionKind.Table && trailer.Get("XRefStm") is double hybrid && hybrid > 0 ? (long)hybrid : 0;
                if (hybrids.TryGetValue(at, out var shared))
                {
                    table.Hybrid = shared;
                }
                else if (at > 0 && seen.Add(at) && LoadSection(at, hybrid: true) is not null)
                {
                    table.Hybrid = hybrids[at] = _sections[^1];
                }
                else
                {
                    _chainComplete = false;
                }
            }

            next = null;
            if (trailer.Has("Prev"))
            {
                if (trailer.Get("Prev") is double previous)
                {
                    next = previous > 0 ? (long)previous : null;
                }
                else
                {
                    _chainComplete = false;
                }
            }
        }

        return _sections.Count > 0 && Root is not null;
    }

    /// <summary>
    /// For a file whose index cannot be followed: every object that opens
    /// in a small prefix or suffix, later definitions winning, and the
    /// catalog named by a trailer or found by type. A guess, for the page
    /// shape only.
    /// </summary>
    public bool TryScanPrefix()
    {
        _sections.Clear();
        _objects.Clear();
        _headerOffset = 0;
        Root = null;
        var section = new XrefSection(SectionKind.Table, new PdfDictionary(new PdfSource([]), 0));
        var prefix = ReadAt(0, ScanPrefixBytes);
        IndexObjects(section, prefix, 0);
        if (_stream.Length > ScanPrefixBytes)
        {
            var suffixStart = Math.Max(ScanPrefixBytes, _stream.Length - ScanSuffixBytes);
            var suffix = ReadAt(suffixStart, ScanSuffixBytes);
            IndexObjects(section, suffix, suffixStart);
            Root ??= FindTrailerRoot(suffix);
        }

        Root ??= FindTrailerRoot(prefix);
        _sections.Add(section);
        if (Root is null)
        {
            foreach (var (number, _) in section.Entries.OrderBy(pair => pair.Value.Offset))
            {
                CheckBudget();
                if (Read(number) is PdfDictionary candidate && TypeOf(candidate) == "Catalog")
                {
                    Root = new PdfRef(number, 0);
                    break;
                }
            }
        }

        return section.Entries.Count > 0;
    }

    /// <summary>
    /// True when the viewer is certain to follow this index, and from then
    /// on the reader answers only what the viewer would. The index starts
    /// at byte zero and ends near the end of the file, every section reads
    /// cleanly, the chain is all tables or all streams (the viewer drops a
    /// mixed chain and scans), every table row has the fixed shape the
    /// viewer reads by position, the oldest table's first object sits
    /// where its row says, and the root is there.
    /// </summary>
    public bool TryFollowAsViewer()
    {
        if (_headerOffset != 0
            || !ReadAt(0, 5).AsSpan().SequenceEqual("%PDF-"u8)
            || _startXrefFromEnd > SureTailBytes
            || !_chainComplete
            || _formatDoubt
            || _sections.Count == 0
            || _sections[0].Trailer.Get("Root") is not PdfRef root)
        {
            return false;
        }

        _streamsOnly = _sections.All(section => section.Kind == SectionKind.Stream);
        if (!_streamsOnly && (_sections.Any(section => section.Kind == SectionKind.Stream) || !TablesReadAsTheViewerReadsThem()))
        {
            return false;
        }

        _mode = Mode.Viewer;
        _objects.Clear();
        try
        {
            if (Read(root.Number) is PdfDictionary)
            {
                return true;
            }
        }
        catch (PdfUnsureException)
        {
            // The root itself reads two ways.
        }

        _mode = Mode.Guess;
        _objects.Clear();
        return false;
    }

    /// <summary>
    /// For a file whose index the viewer may or may not follow: from here
    /// on, an object is read only when the whole file holds one definition
    /// of it, counting every "N G obj" anywhere and every object stream
    /// that packs it. An index can only point at one of those, so following
    /// the index and scanning the file find the same object. Anything else
    /// throws <see cref="PdfUnsureException"/>. False when no root is named.
    /// </summary>
    public bool ReadOnlyWhatIsDefinedOnce(PdfFileScan scan)
    {
        _mode = Mode.DefinedOnce;
        _objects.Clear();
        _definitions = scan.Definitions.ToLookup(item => item.Number, item => item.Offset - _headerOffset);
        _packed = [];
        if (StartXref < 0 && scan.StartXref >= 0)
        {
            StartXref = ReadNumberAfter(ReadAt(scan.StartXref, 64), "startxref".Length) ?? -1;
        }

        foreach (var owner in scan.ObjectStreams)
        {
            CheckBudget();
            if (ReadIndirect(owner) is not { Value: PdfStream stream } container || TypeOf(stream.Dictionary) != "ObjStm")
            {
                continue;
            }

            var members = Members(stream) ?? throw new PdfUnsureException();
            for (var index = 0; index < members.Count; index++)
            {
                if (!_packed.TryGetValue(members[index].Number, out var ways))
                {
                    _packed[members[index].Number] = ways = [];
                }

                ways.Add(new XrefEntry(2, 0, container.Number, index));
            }
        }

        // Every trailer a scan would meet has to name the same root, or the
        // viewer may be showing another document.
        var roots = new HashSet<int>();
        foreach (var trailer in _sections.Select(section => section.Trailer).Where(trailer => trailer.Get("Root") is PdfRef))
        {
            roots.Add(((PdfRef)trailer.Get("Root")!).Number);
        }

        foreach (var offset in scan.Trailers)
        {
            CheckBudget();
            if (ReadDictionaryAt(offset + "trailer".Length) is { } trailer && trailer.Get("Root") is PdfRef named)
            {
                roots.Add(named.Number);
                _looseTrailers.Add(trailer);
            }
        }

        foreach (var owner in scan.IndexStreams)
        {
            CheckBudget();
            if (ReadIndirect(owner) is { Value: PdfStream stream } && TypeOf(stream.Dictionary) == "XRef" && stream.Dictionary.Get("Root") is PdfRef named)
            {
                roots.Add(named.Number);
            }
        }

        if (roots.Count > 1)
        {
            throw new PdfUnsureException();
        }

        Root = roots.Count == 1 ? new PdfRef(roots.First(), 0) : null;
        return Root is not null;
    }

    /// <summary>
    /// A trailer entry, from the newest trailer that has it: the viewer
    /// folds an older trailer's keys under a newer one's.
    /// </summary>
    public PdfDictionary? TrailerWith(string key) =>
        _sections.Select(section => section.Trailer).FirstOrDefault(trailer => trailer.Has(key))
            ?? _looseTrailers.LastOrDefault(trailer => trailer.Has(key));

    /// <summary>The highest object number any section or trailer speaks of.</summary>
    public int HighestListedNumber()
    {
        long highest = 0;
        foreach (var section in _sections)
        {
            foreach (var rows in section.Rows)
            {
                highest = Math.Max(highest, (long)rows.First + rows.Count - 1);
            }

            foreach (var number in section.Entries.Keys)
            {
                highest = Math.Max(highest, number);
            }
        }

        foreach (var trailer in _sections.Select(section => section.Trailer).Concat(_looseTrailers))
        {
            if (trailer.Get("Size") is double size && size >= 1 && size <= MaxObjectNumber)
            {
                highest = Math.Max(highest, (long)size - 1);
            }
        }

        return (int)Math.Min(highest, MaxObjectNumber);
    }

    /// <summary>A table section's trailer or a cross-reference stream's dictionary.</summary>
    private PdfDictionary? LoadSection(long offset, bool hybrid)
    {
        offset += _headerOffset;
        if (offset <= 0 || offset >= _stream.Length)
        {
            return null;
        }

        var head = ReadAt(offset, 64);
        var lexer = new PdfLexer(head, 0);
        try
        {
            var at = lexer.SkipWhitespace();
            if (lexer.PeekKeyword() == "xref")
            {
                return hybrid ? null : LoadTable(offset + at + "xref".Length);
            }
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }

        // The viewer takes any stream with field widths as an index, typed or not.
        if (ReadIndirect(offset)?.Value is not PdfStream xref || xref.Dictionary.Get("W") is not PdfArray)
        {
            return null;
        }

        var section = new XrefSection(hybrid ? SectionKind.TableStream : SectionKind.Stream, xref.Dictionary);
        if (!ReadXrefStream(xref, section))
        {
            return null;
        }

        _sections.Add(section);
        return xref.Dictionary;
    }

    private PdfDictionary? LoadTable(long position)
    {
        var rows = new List<XrefRows>();
        while (rows.Count < 65536)
        {
            CheckBudget();
            var bytes = ReadAt(position, 256);
            var lexer = new PdfLexer(bytes, 0);
            try
            {
                var at = lexer.SkipWhitespace();
                if (lexer.PeekKeyword() == "trailer")
                {
                    if (ReadDictionaryAt(position + at + "trailer".Length) is not { } trailer)
                    {
                        return null;
                    }

                    var section = new XrefSection(SectionKind.Table, trailer);
                    section.Rows.AddRange(rows);
                    _sections.Add(section);
                    return trailer;
                }

                if (lexer.ReadValue() is not double first || lexer.ReadValue() is not double count
                    || first < 0 || count < 0 || first + count > MaxObjectNumber)
                {
                    return null;
                }

                var entryStart = lexer.SkipWhitespace();
                var stride = EntryStride(bytes, entryStart);
                var end = position + entryStart + (long)count * stride;
                if ((count > 0 && stride == 0) || end > _stream.Length)
                {
                    return null;
                }

                // A number listed twice in one table is read one way here and another by the viewer.
                _formatDoubt |= rows.Any(known => first < known.First + known.Count && known.First < first + count);
                if (count > 0)
                {
                    rows.Add(new XrefRows((int)first, (int)count, position + entryStart, stride));
                }

                position = end;
            }
            catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A dictionary at an offset, read with a window that grows until it fits.</summary>
    private PdfDictionary? ReadDictionaryAt(long offset)
    {
        for (var window = 4096; window <= _maxObjectBytes; window *= 16)
        {
            CheckBudget();
            var bytes = ReadAt(offset, window);
            try
            {
                return new PdfLexer(bytes, 0).ReadValueAlone() as PdfDictionary;
            }
            catch (PdfTruncatedException) when (bytes.Length == window)
            {
                // A long trailer; read a larger window.
            }
            catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
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
        if (xref.Dictionary.Get("W") is not PdfArray { Count: 3 } widths
            || widths.Items.Any(width => width is not double value || value < 0 || value > 8)
            || DataOf(xref) is not { } data)
        {
            return false;
        }

        var w = widths.Items.Select(width => (int)(double)width!).ToArray();
        var rowBytes = w.Sum();
        if (rowBytes == 0)
        {
            return false;
        }

        // Every index stream of the file together is held to the size one
        // stream may have: a chain of them cannot fill memory.
        _indexBytes += data.Length;
        if (_indexBytes > _maxStreamBytes)
        {
            return false;
        }

        section.Data = data;
        section.Widths = w;
        var index = (xref.Dictionary.Get("Index") as PdfArray)?.Items ?? [0d, xref.Dictionary.Get("Size")];
        var held = data.Length / rowBytes;
        var row = 0;
        var reach = 0;
        for (var pair = 0; pair + 1 < index.Count; pair += 2)
        {
            CheckBudget();
            if (index[pair] is not double first || index[pair + 1] is not double count
                || first < 0 || count < 0 || first + count > MaxObjectNumber)
            {
                return false;
            }

            var rows = (int)Math.Min(Math.Ceiling(count), held - row);
            if (rows > 0)
            {
                // A number listed twice is a doubt, and only an index out of order can list one twice.
                _formatDoubt |= first < reach && section.Rows.Any(known => first < known.First + known.Count && known.First < first + rows);
                section.Rows.Add(new XrefRows((int)first, rows, (long)row * rowBytes, rowBytes));
                reach = Math.Max(reach, (int)first + rows);
                row += rows;
            }

            if (rows < count)
            {
                // Rows the stream promises and does not hold.
                _formatDoubt = true;
                return section.Rows.Count > 0;
            }
        }

        return true;
    }

    /// <summary>One row of an index stream. A kind no viewer knows lists nothing.</summary>
    private static XrefEntry? ReadStreamEntry(byte[] data, int at, int[] w)
    {
        var kind = w[0] == 0 ? 1 : Field(data, at, w[0]);
        var second = Field(data, at + w[0], w[1]);
        var third = Field(data, at + w[0] + w[1], w[2]);
        return kind switch
        {
            0 => new XrefEntry(0, 0, 0, 0),
            1 => new XrefEntry(1, second, 0, 0),
            2 when second is >= 0 and <= MaxObjectNumber && third is >= 0 and <= int.MaxValue =>
                new XrefEntry(2, 0, (int)second, (int)third),
            _ => null
        };
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

    /// <summary>
    /// The viewer reads a table by position: twenty bytes a row, the offset
    /// in the first ten, the kind in the eighteenth. A row of any other
    /// shape makes it read something else or scan the file instead, so
    /// every row of every table has to have the one shape. Then the check
    /// the viewer itself makes: the oldest table's first object sits where
    /// its row says.
    /// </summary>
    private bool TablesReadAsTheViewerReadsThem()
    {
        (int Number, long Offset)? first = null;
        foreach (var section in _sections.Where(section => section.Kind == SectionKind.Table))
        {
            first = null;
            foreach (var rows in section.Rows.OrderBy(rows => rows.First))
            {
                if (rows.Stride != 20)
                {
                    return false;
                }

                for (var done = 0; done < rows.Count;)
                {
                    CheckBudget();
                    var batch = Math.Min(rows.Count - done, 8192);
                    var bytes = ReadAt(rows.Offset + (long)done * 20, batch * 20);
                    if (bytes.Length != batch * 20)
                    {
                        return false;
                    }

                    for (var i = 0; i < batch; i++)
                    {
                        if (!TryReadRow(bytes.AsSpan(i * 20, 20), out var inUse, out var offset))
                        {
                            return false;
                        }

                        if (inUse && offset > 0 && first is null)
                        {
                            first = (rows.First + done + i, offset);
                        }
                    }

                    done += batch;
                }
            }
        }

        // The loop ends on the oldest table, which the viewer checks with
        // its hybrid stream already read: an object that stream lists ahead
        // of the table's first is checked instead, at no offset at all.
        var oldest = _sections.FindLast(section => section.Kind == SectionKind.Table);
        if (first is not { } row
            || (oldest?.Hybrid is { } hybrid && ListsInUseBelow(hybrid, row.Number)))
        {
            return false;
        }

        return ReadNumberAfter(ReadAt(row.Offset, 32), 0) == row.Number;
    }

    /// <summary>True when the section lists, in use, an object numbered below <paramref name="limit"/>.</summary>
    private bool ListsInUseBelow(XrefSection section, int limit)
    {
        foreach (var rows in section.Rows)
        {
            for (var number = rows.First; number < Math.Min(limit, rows.First + rows.Count); number++)
            {
                if (Row(section, rows, number) is { Kind: not 0 })
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>"nnnnnnnnnn ggggg n" and a two-byte line end.</summary>
    private static bool TryReadRow(ReadOnlySpan<byte> row, out bool inUse, out long offset)
    {
        inUse = false;
        offset = 0;
        for (var i = 0; i < 16; i++)
        {
            var value = row[i];
            if (i == 10 ? value != (byte)' ' : value is < (byte)'0' or > (byte)'9')
            {
                return false;
            }

            if (i < 10)
            {
                offset = offset * 10 + (value - (byte)'0');
            }
        }

        inUse = row[17] == (byte)'n';
        return row[16] == (byte)' '
            && row[17] is (byte)'n' or (byte)'f'
            && row[18] is (byte)' ' or (byte)'\r'
            && row[19] is (byte)'\r' or (byte)'\n';
    }

    // ------------------------------------------------------------------
    // Objects.
    // ------------------------------------------------------------------

    public object? Resolve(object? value) =>
        value is PdfRef reference ? Read(reference.Number) : value;

    /// <summary>The object's value, or null when it is free, missing or does not read.</summary>
    public object? Read(int number) => Load(number).Value;

    /// <summary>The generation the object's own header gives. Zero for a packed object.</summary>
    public int GenerationOf(int number) => Load(number).Generation;

    /// <summary>
    /// True when a plain definition appended to the file takes this
    /// object's place in the viewer. Under a chain of streams the newest
    /// entry always wins. Under tables, and when the viewer scans, an
    /// object packed in an object stream keeps its place whatever follows.
    /// </summary>
    public bool CanBeReplaced(int number) =>
        (_mode == Mode.Viewer && _streamsOnly) || !Load(number).Packed;

    private Loaded Load(int number)
    {
        if (_objects.TryGetValue(number, out var known))
        {
            return known;
        }

        // An object can need another to be read (its object stream, a
        // length kept elsewhere); a chain longer than any real file's
        // reads as missing, like a cycle.
        if (_depth >= MaxObjectDepth)
        {
            return default;
        }

        CheckBudget();
        _objects[number] = default; // a reference cycle reads as missing
        var loaded = default(Loaded);
        _depth++;
        try
        {
            if (Find(number) is { } entry)
            {
                loaded = entry.Kind == 1
                    ? ReadIndirect(entry.Offset + _headerOffset) is { } plain && plain.Number == number
                        ? new Loaded(plain.Value, plain.Generation, false)
                        : default
                    : new Loaded(ReadFromObjectStream(number, entry.StreamNumber, entry.Index), 0, true);
            }
        }
        finally
        {
            _depth--;
        }

        _objects[number] = loaded;
        return loaded;
    }

    private XrefEntry? Find(int number) => _mode switch
    {
        Mode.Viewer => FindAsViewer(number),
        Mode.DefinedOnce => FindDefinedOnce(number),
        _ => _sections.Select(section => Listing(section, number)).FirstOrDefault(entry => entry is { Kind: not 0 })
    };

    /// <summary>What one section says of an object, or null when it does not list it.</summary>
    private XrefEntry? Listing(XrefSection section, int number)
    {
        if (section.Entries.TryGetValue(number, out var entry))
        {
            return entry;
        }

        foreach (var rows in section.Rows)
        {
            if (number >= rows.First && number < rows.First + rows.Count && Row(section, rows, number) is { } row)
            {
                return row;
            }
        }

        return null;
    }

    /// <summary>The row a run holds for a number: out of a stream's decoded data, or out of the file for a table.</summary>
    private XrefEntry? Row(XrefSection section, XrefRows rows, int number)
    {
        var at = rows.Offset + (long)(number - rows.First) * rows.Stride;
        return section.Data is { } data ? ReadStreamEntry(data, (int)at, section.Widths) : ReadTableEntry(at);
    }

    /// <summary>
    /// What pdfium does with an index it follows, as measured on the build
    /// the sign surface renders with. Under streams the newest section to
    /// list an object decides, a free entry included. Under tables a row
    /// in use beats an older one, a free row changes nothing, and an
    /// object a hybrid stream packs keeps its place whatever a newer table
    /// says. Where builds may differ the answer is no answer.
    /// </summary>
    private XrefEntry? FindAsViewer(int number)
    {
        XrefEntry? found = null;
        var winner = -1;
        var newestFree = -1;
        var disagree = false;
        var streamed = false;
        for (var i = 0; i < _sections.Count; i++)
        {
            if (Listing(_sections[i], number) is not { } entry)
            {
                continue;
            }

            if (_streamsOnly)
            {
                return entry.Kind == 0 ? null : entry;
            }

            if (entry.Kind == 0)
            {
                newestFree = newestFree < 0 ? i : newestFree;
                continue;
            }

            streamed |= _sections[i].Kind == SectionKind.TableStream;
            disagree |= found is { } first && first != entry;
            if (found is null)
            {
                found = entry;
                winner = i;
            }
        }

        if (found is null)
        {
            return null;
        }

        // Tables that disagree are an update: the newest row wins. A hybrid
        // stream that disagrees with a table is the case the builds settle
        // differently. Word lists every object in both, saying the same.
        // A hybrid's tables list its packed objects as free: that row is the
        // convention, not a deletion. A free row in a table newer than any
        // that names the stream is one.
        var ownTable = _sections[winner].Kind == SectionKind.TableStream ? winner - 1 : winner;
        if ((streamed && disagree) || (newestFree >= 0 && newestFree < ownTable))
        {
            throw new PdfUnsureException();
        }

        return found;
    }

    private XrefEntry? FindDefinedOnce(int number)
    {
        var ways = new HashSet<XrefEntry>();
        foreach (var offset in _definitions![number])
        {
            ways.Add(new XrefEntry(1, offset, 0, 0));
        }

        if (_packed!.TryGetValue(number, out var packed))
        {
            ways.UnionWith(packed);
        }

        return ways.Count switch
        {
            0 => null,
            1 => ways.First(),
            _ => throw new PdfUnsureException()
        };
    }

    private XrefEntry? ReadTableEntry(long offset)
    {
        var bytes = ReadAt(offset, 20);
        var lexer = new PdfLexer(bytes, 0);
        try
        {
            if (lexer.ReadValue() is not double position || lexer.ReadValue() is not double)
            {
                return null;
            }

            return lexer.ReadKeyword() switch
            {
                "n" => new XrefEntry(1, (long)position, 0, 0),
                "f" => new XrefEntry(0, 0, 0, 0),
                _ => null
            };
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }
    }

    /// <summary>"N G obj value", read with a window that grows until the value fits.</summary>
    private Indirect? ReadIndirect(long offset)
    {
        for (var window = 4096; window <= _maxObjectBytes; window *= 16)
        {
            CheckBudget();
            var bytes = ReadAt(offset, window);
            var lexer = new PdfLexer(bytes, 0);
            try
            {
                if (lexer.ReadValue() is not double number || lexer.ReadValue() is not double generation
                    || lexer.ReadKeyword() != "obj"
                    || number < 0 || number > MaxObjectNumber || generation < 0 || generation > 65535)
                {
                    return null;
                }

                var value = lexer.ReadValueAlone();
                if (value is PdfDictionary dictionary && lexer.TryReadStreamKeyword(out var dataStart))
                {
                    value = new PdfStream(dictionary, offset + dataStart);
                }

                return new Indirect((int)number, (int)generation, value);
            }
            catch (PdfTruncatedException) when (bytes.Length == window)
            {
                // The object runs past this window; read a larger one.
            }
            catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>A stream's data, decoded, or null when it is too long or does not decode.</summary>
    public byte[]? DataOf(PdfStream stream)
    {
        if (!stream.DataRead)
        {
            stream.DataRead = true;
            if (Resolve(stream.Dictionary.Get("Length")) is double length && length >= 0 && length <= _maxStreamBytes)
            {
                var raw = ReadAt(stream.DataStart, (int)length);
                stream.Data = raw.Length == (int)length ? Decode(stream.Dictionary, raw) : null;
            }
        }

        return stream.Data;
    }

    private object? ReadFromObjectStream(int number, int streamNumber, int index)
    {
        // Protected files encrypt object streams, so their bytes would be
        // noise. Top-level dictionaries are never encrypted.
        if (Encrypted
            || Read(streamNumber) is not PdfStream objectStream
            || TypeOf(objectStream.Dictionary) != "ObjStm"
            || Members(objectStream) is not { } members
            || index < 0
            || index >= members.Count)
        {
            return null;
        }

        // One build looks a packed object up by its place, another by its
        // number. The signer needs both to land on the same one.
        if (_mode != Mode.Guess && (members[index].Number != number || members.IsRepeated(number)))
        {
            throw new PdfUnsureException();
        }

        try
        {
            return members[index].Offset < objectStream.Data!.Length
                ? new PdfLexer(objectStream.Data, members[index].Offset).ReadValue()
                : null;
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }
    }

    /// <summary>
    /// An object stream's members. The header is read once for the stream:
    /// read again for every member asked for, a thousand pages in a stream
    /// of twenty thousand members took seconds.
    /// </summary>
    private PdfMembers? Members(PdfStream objectStream)
    {
        if (!objectStream.MembersRead)
        {
            objectStream.MembersRead = true;
            objectStream.Members = ReadMembers(objectStream);
        }

        return objectStream.Members;
    }

    private PdfMembers? ReadMembers(PdfStream objectStream)
    {
        if (objectStream.Dictionary.Get("N") is not double count
            || objectStream.Dictionary.Get("First") is not double first
            || count < 0 || count > MaxObjectNumber || first < 0
            || DataOf(objectStream) is not { } data)
        {
            return null;
        }

        var members = new List<(int Number, int Offset)>((int)Math.Min(count, 4096));
        try
        {
            var header = new PdfLexer(data, 0);
            for (var i = 0; i < count; i++)
            {
                if (header.ReadValue() is not double number || header.ReadValue() is not double offset
                    || number < 0 || number > MaxObjectNumber || offset < 0 || first + offset > int.MaxValue)
                {
                    return null;
                }

                members.Add(((int)number, (int)(first + offset)));
            }
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }

        // A number two members carry sits next to itself once the numbers are in order.
        var numbers = members.Select(member => member.Number).Order().ToArray();
        var repeated = numbers.Where((number, i) => i > 0 && number == numbers[i - 1]).Distinct().ToArray();
        return new PdfMembers([.. members], repeated);
    }

    /// <summary>FlateDecode with an optional PNG predictor, which is what indexes use.</summary>
    private byte[]? Decode(PdfDictionary dictionary, byte[] raw)
    {
        var filter = dictionary.Get("Filter") switch
        {
            null => null,
            PdfName name => name.Value,
            PdfArray { Count: 1 } list when list.Items[0] is PdfName only => only.Value,
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
                if (output.Length > _maxStreamBytes)
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

        var parameters = dictionary.Get("DecodeParms") switch
        {
            PdfDictionary single => single,
            PdfArray { Count: 1 } list => list.Items[0] as PdfDictionary,
            _ => null
        };
        var predictor = parameters?.Get("Predictor") is double p ? (int)p : 1;
        if (predictor < 10)
        {
            return predictor == 1 ? inflated : null;
        }

        var columns = parameters?.Get("Columns") is double c ? (int)c : 1;
        try
        {
            return columns <= 0 ? null : UndoPngPredictor(inflated, columns);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[] UndoPngPredictor(byte[] data, int columns)
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

    // ------------------------------------------------------------------
    // Pages, as the viewer finds and frames them.
    // ------------------------------------------------------------------

    /// <summary>The page tree's root: its object number and dictionary.</summary>
    public (int Number, PdfDictionary Node)? PageTree() =>
        Root is { } root
        && Read(root.Number) is PdfDictionary catalog
        && catalog.Get("Pages") is PdfRef pages
        && Read(pages.Number) is PdfDictionary node
            ? (pages.Number, node)
            : null;

    /// <summary>
    /// What a node says of its own pages, which the viewer takes on trust:
    /// its /Count when that is a usable number, one for a node that is
    /// itself a page. Null when it does not say.
    /// </summary>
    public int? DeclaredPageCount(PdfDictionary node) =>
        !node.Has("Kids") ? 1
        : Resolve(node.Get("Count")) is double count && count >= 1 && count < MaxPageCount ? (int)count
        : null;

    /// <summary>
    /// The page count the viewer reports, counted as pdfium counts on the
    /// build that renders the sign surface: what the node says of itself,
    /// and for a node that does not say, its kids added up. So a root with
    /// no usable /Count is not counted by its leaves: a node below it that
    /// says 1 and holds two pages counts 1.
    /// </summary>
    public int PageCount(PdfDictionary node) => PageCount(node, 0);

    private int PageCount(PdfDictionary node, int depth)
    {
        if (DeclaredPageCount(node) is { } declared)
        {
            return declared;
        }

        if (depth >= MaxTreeDepth || Resolve(node.Get("Kids")) is not PdfArray kids)
        {
            throw new PdfUnsureException();
        }

        var count = 0;
        foreach (var kid in kids.Items)
        {
            CheckBudget();
            count += Resolve(kid) is PdfDictionary child ? PageCount(child, depth + 1) : throw new PdfUnsureException();
            if (count >= MaxPageCount)
            {
                throw new PdfUnsureException();
            }
        }

        return count;
    }

    /// <summary>
    /// Visits the pages in the viewer's order until <paramref name="visit"/>
    /// answers false. To the viewer a node is a page when it has no /Kids,
    /// whatever its /Type says. A tree the viewer walks its own way (a kid
    /// that is no dictionary, a node met twice, /Kids that is no array)
    /// throws <see cref="PdfUnsureException"/>.
    /// </summary>
    public bool WalkPages(int number, PdfDictionary node, Func<int, PdfDictionary, bool> visit) =>
        WalkPages(number, node, 0, [number], visit);

    private bool WalkPages(int number, PdfDictionary node, int depth, HashSet<int> seen, Func<int, PdfDictionary, bool> visit)
    {
        CheckBudget();
        if (!node.Has("Kids"))
        {
            return visit(number, node);
        }

        if (depth >= MaxTreeDepth || Resolve(node.Get("Kids")) is not PdfArray kids)
        {
            throw new PdfUnsureException();
        }

        foreach (var kid in kids.Items)
        {
            if (kid is not PdfRef reference || !seen.Add(reference.Number) || Read(reference.Number) is not PdfDictionary child)
            {
                throw new PdfUnsureException();
            }

            if (!WalkPages(reference.Number, child, depth + 1, seen, visit))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The page as pdfium frames it, measured on the build that renders
    /// the sign surface. A box is four numbers or it is not there. No
    /// MediaBox is US Letter. The CropBox is cut to the MediaBox. /Rotate
    /// is whole quarters, rounded toward zero. Each is the page's own entry
    /// or the nearest up its /Parent chain. Null when nothing is left of
    /// the page.
    /// </summary>
    public PdfPageFrame? FrameOf(PdfDictionary page)
    {
        var mediaBox = BoxOf(page, "MediaBox", inherited: true);
        var cropBox = BoxOf(page, "CropBox", inherited: true);
        var media = mediaBox ?? new PdfBox(0, 0, 612, 792);
        var box = cropBox is { } crop
            ? new PdfBox(Math.Max(crop.Left, media.Left), Math.Max(crop.Bottom, media.Bottom), Math.Min(crop.Right, media.Right), Math.Min(crop.Top, media.Top))
            : media;
        if (box.IsEmpty)
        {
            return null;
        }

        var quarters = Resolve(Attribute(page, "Rotate", inherited: true)) is double turn && Math.Abs(turn) < int.MaxValue
            ? ((int)turn / 90 % 4 + 4) % 4
            : 0;
        return new PdfPageFrame(box, quarters, mediaBox, cropBox);
    }

    /// <summary>A box of four numbers with some area, or null: the viewer treats any other value as absent.</summary>
    public PdfBox? BoxOf(PdfDictionary page, string name, bool inherited)
    {
        if (Resolve(Attribute(page, name, inherited)) is not PdfArray { Count: 4 } values)
        {
            return null;
        }

        var numbers = new double[4];
        for (var i = 0; i < 4; i++)
        {
            // Anything that is no number counts as zero.
            numbers[i] = Resolve(values.Items[i]) is double value ? value : 0;
            if (!double.IsFinite(numbers[i]))
            {
                return null;
            }
        }

        var box = new PdfBox(
            Math.Min(numbers[0], numbers[2]),
            Math.Min(numbers[1], numbers[3]),
            Math.Max(numbers[0], numbers[2]),
            Math.Max(numbers[1], numbers[3]));
        return box.IsEmpty ? null : box;
    }

    /// <summary>The first dictionary on the page's /Parent chain that has the key decides, usable value or not.</summary>
    private object? Attribute(PdfDictionary page, string name, bool inherited)
    {
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var node = page; node is not null && visited.Add(node) && visited.Count <= MaxTreeDepth; node = Resolve(node.Get("Parent")) as PdfDictionary)
        {
            if (node.Has(name))
            {
                return node.Get(name);
            }

            if (!inherited)
            {
                break;
            }
        }

        return null;
    }

    public static string? TypeOf(PdfDictionary node) =>
        (node.Get("Type") as PdfName?)?.Value;

    // ------------------------------------------------------------------
    // The prefix scan and the plumbing.
    // ------------------------------------------------------------------

    private static void IndexObjects(XrefSection section, byte[] bytes, long baseOffset)
    {
        for (var i = 0; i + 3 < bytes.Length; i++)
        {
            if (bytes[i] != (byte)'o' || bytes[i + 1] != (byte)'b' || bytes[i + 2] != (byte)'j'
                || (i + 3 < bytes.Length && !PdfLexer.IsDelimiterOrSpace(bytes[i + 3])))
            {
                continue;
            }

            // Walk back over "N G " to the object's start.
            var start = i - 1;
            var numbers = 0;
            while (numbers < 2 && start >= 0)
            {
                while (start >= 0 && PdfLexer.IsSpace(bytes[start]))
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
        var at = bytes.AsSpan().LastIndexOf("trailer"u8);
        if (at < 0)
        {
            return null;
        }

        try
        {
            return new PdfLexer(bytes, at + "trailer".Length).ReadValue() is PdfDictionary trailer
                ? trailer.Get("Root") as PdfRef?
                : null;
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }
    }

    private static long? ReadNumberAfter(byte[] bytes, int position)
    {
        try
        {
            return new PdfLexer(bytes, position).ReadValue() is double number && number >= 0 && number == Math.Floor(number) && number < long.MaxValue
                ? (long)number
                : null;
        }
        catch (Exception ex) when (ex is FormatException or PdfTruncatedException)
        {
            return null;
        }
    }

    private byte[] ReadAt(long offset, int length)
    {
        if (offset < 0 || offset >= _stream.Length)
        {
            return [];
        }

        var count = (int)Math.Min(length, _stream.Length - offset);
        var buffer = new byte[count];
        _stream.Seek(offset, SeekOrigin.Begin);
        var read = 0;
        while (read < count)
        {
            var got = _stream.Read(buffer, read, count - read);
            if (got == 0)
            {
                break;
            }

            read += got;
        }

        return read == count ? buffer : buffer[..read];
    }

    /// <summary>The reader's two budgets: its time, and the objects it holds.</summary>
    private void CheckBudget()
    {
        if (_clock.Elapsed > _budget || _objects.Count > MaxObjects)
        {
            throw new TimeoutException("The PDF reader ran out of time or room.");
        }
    }
}

/// <summary>The PDF object syntax, over bytes. Running off the end throws <see cref="PdfTruncatedException"/>.</summary>
internal sealed class PdfLexer(byte[] bytes, int position)
{
    private int _depth;
    private PdfSource? _source;

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
            throw new PdfTruncatedException();
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

    /// <summary>
    /// A value read out of a window of the file. What it keeps is a copy of
    /// its own bytes, not the window: a page dictionary of 65 KB held the
    /// megabyte it was read in.
    /// </summary>
    public object? ReadValueAlone()
    {
        var start = SkipWhitespace();
        var value = ReadValue();
        _source?.KeepOnly(start, position);
        _source = null;
        return value;
    }

    public object? ReadValue()
    {
        SkipWhitespace();
        if (position >= bytes.Length)
        {
            throw new PdfTruncatedException();
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
        if (!TryReadNumber(token, out var number))
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
        if (number >= 0 && number <= int.MaxValue && number == Math.Floor(number) && position < bytes.Length)
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
            catch (PdfTruncatedException)
            {
                // A number at the very end of the window stays a number.
            }
        }

        position = resume;
        return number;
    }

    /// <summary>
    /// A number as PDF writes one: a sign, digits, one point. The viewer
    /// reads "1e3" or "NaN" as no number at all, so neither does this.
    /// </summary>
    private static bool TryReadNumber(string token, out double number)
    {
        number = 0;
        var digits = 0;
        var points = 0;
        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (char.IsAsciiDigit(ch))
            {
                digits++;
            }
            else if (ch == '.')
            {
                points++;
            }
            else if (i != 0 || ch is not ('+' or '-'))
            {
                return false;
            }
        }

        return digits > 0
            && points <= 1
            && double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number);
    }

    private PdfDictionary ReadDictionary()
    {
        Descend();
        var dictionary = new PdfDictionary(_source ??= new PdfSource(bytes), position);
        position += 2;
        while (true)
        {
            var keyStart = SkipWhitespace();
            if (position + 1 >= bytes.Length)
            {
                throw new PdfTruncatedException();
            }

            if (bytes[position] == (byte)'>' && bytes[position + 1] == (byte)'>')
            {
                position += 2;
                _depth--;
                dictionary.Close(position);
                return dictionary;
            }

            if (ReadValue() is not PdfName key)
            {
                throw new FormatException();
            }

            var valueStart = SkipWhitespace();
            var value = ReadValue();
            dictionary.Add(key.Value, value, keyStart, valueStart, position);
        }
    }

    private PdfArray ReadArray()
    {
        Descend();
        position++;
        var values = new PdfArray(_source ??= new PdfSource(bytes));
        while (true)
        {
            var itemStart = SkipWhitespace();
            if (position >= bytes.Length)
            {
                throw new PdfTruncatedException();
            }

            if (bytes[position] == (byte)']')
            {
                position++;
                _depth--;
                return values;
            }

            var value = ReadValue();
            values.Add(value, itemStart, position);
        }
    }

    private void Descend()
    {
        if (++_depth > PdfReader.MaxNesting)
        {
            throw new FormatException("The PDF nests values too deeply.");
        }
    }

    private PdfName ReadName()
    {
        position++;
        var builder = new StringBuilder();
        while (position < bytes.Length && !IsDelimiterOrSpace(bytes[position]))
        {
            if (bytes[position] == (byte)'#' && position + 2 < bytes.Length
                && char.IsAsciiHexDigit((char)bytes[position + 1]) && char.IsAsciiHexDigit((char)bytes[position + 2]))
            {
                builder.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(bytes, position + 1, 2), 16));
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

        throw new PdfTruncatedException();
    }

    private void SkipPast(byte terminator)
    {
        var end = Array.IndexOf(bytes, terminator, position);
        if (end < 0)
        {
            throw new PdfTruncatedException();
        }

        position = end + 1;
    }
}

/// <summary>
/// One pass over the whole file, front to back, in a small buffer. It
/// answers what only the whole file can: which of a few names appear
/// anywhere (spelled plainly or with #xx), and every place something that
/// reads as "N G obj", a trailer or a startxref begins, strings and stream
/// data included. A scanning viewer steps over some of those, so this is
/// every definition it could meet and then some.
/// </summary>
internal sealed class PdfFileScan
{
    private const int MaxDefinitions = 1_000_000;

    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly string[] _wanted;
    private readonly bool _keepDefinitions;
    private long _lastDefinition = -1;

    // Two readings of "number number obj". One steps over comments, as a
    // tokenizer in step with the file does. The other never does, for a
    // '%' that was only a byte of data.
    private NumberRun _skipping;
    private NumberRun _plain;

    private PdfFileScan(bool keepDefinitions, string[] names)
    {
        _keepDefinitions = keepDefinitions;
        _wanted = names;
    }

    /// <summary>The last two number words in a row: where each began and the object number it spells.</summary>
    private struct NumberRun
    {
        public int Count;
        public long FirstStart;
        public long FirstNumber;
        public long SecondStart;
        public long SecondNumber;

        public void Push(long start, long number)
        {
            if (Count == 2)
            {
                (FirstStart, FirstNumber) = (SecondStart, SecondNumber);
            }

            if (Count == 0)
            {
                (FirstStart, FirstNumber) = (start, number);
            }
            else
            {
                (SecondStart, SecondNumber) = (start, number);
            }

            Count = Math.Min(2, Count + 1);
        }
    }

    public bool Has(string name) => _names.Contains(name);

    public string? FirstOf(params string[] names) => names.FirstOrDefault(Has);

    /// <summary>Where the last "startxref" begins, or -1.</summary>
    public long StartXref { get; private set; } = -1;

    public List<long> Trailers { get; } = [];

    /// <summary>Every "N G obj", when the pass was asked to keep them.</summary>
    public List<(int Number, long Offset)> Definitions { get; } = [];

    /// <summary>Objects that name /ObjStm: object streams, whose members are definitions too.</summary>
    public HashSet<long> ObjectStreams { get; } = [];

    /// <summary>Objects that name /XRef: a scanning viewer takes their dictionaries for trailers.</summary>
    public HashSet<long> IndexStreams { get; } = [];

    /// <summary>The highest object number any definition carries.</summary>
    public int HighestNumber { get; private set; }

    /// <summary>
    /// Reads the stream from its start. With <paramref name="keepDefinitions"/>
    /// off only the names, the last startxref and the highest number are
    /// kept. Throws <see cref="PdfUnsureException"/> past a million definitions.
    /// </summary>
    public static PdfFileScan Run(Stream stream, bool keepDefinitions, params string[] names)
    {
        var scan = new PdfFileScan(keepDefinitions, names);
        var buffer = new byte[256 * 1024];

        // A word's first bytes: enough for any name asked for with every
        // character escaped, and for any object number.
        var word = new byte[48];
        var length = 0;
        var start = 0L;
        var isName = false;
        var isNumber = true;
        var inComment = false;

        stream.Seek(0, SeekOrigin.Begin);
        var position = 0L;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < read; i++)
            {
                var value = buffer[i];
                if (!PdfLexer.IsDelimiterOrSpace(value))
                {
                    if (length == 0)
                    {
                        start = position + i;
                    }

                    if (length < word.Length)
                    {
                        word[length] = value;
                    }

                    isNumber &= value is (>= (byte)'0' and <= (byte)'9') or (byte)'+' or (byte)'-' or (byte)'.';
                    length++;
                    continue;
                }

                if (length > 0 || isName)
                {
                    scan.EndWord(word.AsSpan(0, Math.Min(length, word.Length)), length, start, isName, isNumber, inComment);
                    length = 0;
                    isName = false;
                    isNumber = true;
                }

                if (value is 10 or 13)
                {
                    inComment = false;
                }
                else if (!PdfLexer.IsSpace(value))
                {
                    // A delimiter is no number: it ends a run. To the reading
                    // that steps over comments, a '%' is white space.
                    scan._plain.Count = 0;
                    if (!inComment && value != (byte)'%')
                    {
                        scan._skipping.Count = 0;
                    }

                    isName = value == (byte)'/';
                    inComment |= value == (byte)'%';
                }
            }

            position += read;
        }

        if (length > 0)
        {
            scan.EndWord(word.AsSpan(0, Math.Min(length, word.Length)), length, start, isName, isNumber, inComment);
        }

        return scan;
    }

    private void EndWord(ReadOnlySpan<byte> word, int length, long start, bool isName, bool isNumber, bool inComment)
    {
        if (isName)
        {
            NoteName(word, length);
        }
        else if (isNumber)
        {
            var number = length <= word.Length ? ObjectNumber(word) : -1;
            _plain.Push(start, number);
            if (!inComment)
            {
                _skipping.Push(start, number);
            }

            return;
        }
        else if (length == 3 && word.SequenceEqual("obj"u8))
        {
            if (_plain.Count == 2)
            {
                NoteDefinition(_plain.FirstNumber, _plain.FirstStart);
            }

            if (!inComment && _skipping.Count == 2 && (_plain.Count != 2 || _skipping.FirstStart != _plain.FirstStart))
            {
                NoteDefinition(_skipping.FirstNumber, _skipping.FirstStart);
            }
        }
        else if (length == 7 && word.SequenceEqual("trailer"u8) && _keepDefinitions)
        {
            Trailers.Add(start);
        }
        else if (length == 9 && word.SequenceEqual("startxref"u8))
        {
            StartXref = start;
        }

        _plain.Count = 0;
        if (!inComment)
        {
            _skipping.Count = 0;
        }
    }

    private void NoteDefinition(long number, long offset)
    {
        if (number is < 1 or > PdfReader.MaxObjectNumber)
        {
            return;
        }

        _lastDefinition = offset;
        HighestNumber = Math.Max(HighestNumber, (int)number);
        if (_keepDefinitions)
        {
            if (Definitions.Count >= MaxDefinitions)
            {
                throw new PdfUnsureException();
            }

            Definitions.Add(((int)number, offset));
        }
    }

    /// <summary>A name as a viewer reads it: each #xx is the character it spells.</summary>
    private void NoteName(ReadOnlySpan<byte> raw, int length)
    {
        if (length > raw.Length)
        {
            return;
        }

        Span<byte> name = stackalloc byte[raw.Length];
        var decoded = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == (byte)'#' && i + 2 < raw.Length && HexValue(raw[i + 1]) >= 0 && HexValue(raw[i + 2]) >= 0)
            {
                name[decoded++] = (byte)((HexValue(raw[i + 1]) << 4) | HexValue(raw[i + 2]));
                i += 2;
            }
            else
            {
                name[decoded++] = raw[i];
            }
        }

        name = name[..decoded];
        foreach (var wanted in _wanted)
        {
            if (Spells(name, wanted))
            {
                _names.Add(wanted);
            }
        }

        if (_keepDefinitions && _lastDefinition >= 0)
        {
            if (Spells(name, "ObjStm"))
            {
                ObjectStreams.Add(_lastDefinition);
            }
            else if (Spells(name, "XRef"))
            {
                IndexStreams.Add(_lastDefinition);
            }
        }
    }

    private static bool Spells(ReadOnlySpan<byte> name, string text)
    {
        if (name.Length != text.Length)
        {
            return false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (name[i] != text[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The object number a viewer's tokenizer takes from a number word, or -1 for none it would keep.</summary>
    private static long ObjectNumber(ReadOnlySpan<byte> word)
    {
        var i = word.Length > 0 && word[0] == (byte)'+' ? 1 : 0;
        long number = 0;
        var digits = 0;
        for (; i < word.Length && word[i] is >= (byte)'0' and <= (byte)'9'; i++, digits++)
        {
            number = number * 10 + (word[i] - (byte)'0');
            if (number > PdfReader.MaxObjectNumber)
            {
                return -1;
            }
        }

        return digits > 0 ? number : -1;
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        _ => -1
    };
}
