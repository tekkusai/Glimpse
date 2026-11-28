using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal sealed class PreparedOfficeSource : IDisposable
{
    private readonly string? _stagingRoot;

    public string Path { get; }
    public int ExternalRelationshipsRemoved { get; }

    internal PreparedOfficeSource(
        string path,
        string? stagingRoot,
        int externalRelationshipsRemoved)
    {
        Path = path;
        _stagingRoot = stagingRoot;
        ExternalRelationshipsRemoved = externalRelationshipsRemoved;
    }

    public void Dispose()
    {
        if (_stagingRoot is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_stagingRoot, recursive: true);
        }
        catch
        {
            // Cache housekeeping can remove an abandoned staging folder later.
        }
    }
}

/// <summary>
/// A preview never fetches. OOXML names its remote parts (linked images,
/// templates, OLE links, external workbooks) as external relationships,
/// OpenDocument names them where they are used as an xlink:href that leaves
/// the package, and RTF names them in fetchable fields (INCLUDEPICTURE,
/// INCLUDETEXT, LINK, DDE, DDEAUTO, IMPORT, DATABASE, RD, DATA), linked OLE
/// objects (\objlink, \objautlink), and external templates (\*\template).
/// All three are cut from a private copy before the engine sees the file
/// (RTF always converts from its scanned private snapshot so the source
/// cannot change between inspection and conversion). Hyperlinks stay:
/// nothing loads them, and the preview keeps them clickable. Legacy binary
/// formats (.doc, .xls, .ppt) reach the engine as they are, and its profile
/// is what stops their links.
/// </summary>
internal static class OfficePreviewSourceSanitizer
{
    private const long MaxRelationshipsBytes = 4L * 1024 * 1024;
    private const long MaxRtfBytes = 64L * 1024 * 1024;
    private const int MaxRtfGroupDepth = 4096;
    private const int CopyBufferBytes = 128 * 1024;
    private const string OpenDocumentManifest = "META-INF/manifest.xml";
    private const string XLink = "http://www.w3.org/1999/xlink";
    private const string OpenDocumentText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string OpenDocumentDrawing = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly HashSet<string> HyperlinkTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink",
        "http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink"
    };

    private static readonly HashSet<string> FetchableRtfFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "INCLUDEPICTURE",
        "INCLUDETEXT",
        "LINK",
        "IMPORT",
        "DDE",
        "DDEAUTO",
        "DATABASE",
        "RD",
        "DATA",
        "EMBED",
        "GLOSSARY",
        "AUTOTEXTLIST",
        "SUBSCRIBER",
        "CONTROL",
        "OCX",
        "HTMLCONTROL"
    };

    internal static PreparedOfficeSource Prepare(
        string sourcePath,
        string stagingParent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Any zip is checked, whatever its name: a renamed .docx is still OOXML.
        int fetchable;
        bool openDocument;
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            openDocument = source.GetEntry(OpenDocumentManifest) is not null;
            fetchable = source.Entries
                .Where(IsRelationshipsEntry)
                .Sum(entry => FetchableRelationships(LoadRelationships(entry, cancellationToken)).Count());
            if (openDocument)
            {
                foreach (var entry in source.Entries.Where(IsOpenDocumentPart))
                {
                    using var part = entry.Open();
                    fetchable += CutOpenDocumentLinks(part, null, cancellationToken);
                }
            }
        }
        catch (InvalidDataException) when (!IsZip(sourcePath))
        {
            // RTF can also arrive renamed as .doc; sniff the {\rtf header.
            // A file named .rtf whose header is truncated or unreadable fails closed
            // instead of reaching LibreOffice unchecked.
            var isRtfExtension = System.IO.Path.GetExtension(sourcePath).Equals(".rtf", StringComparison.OrdinalIgnoreCase);
            if (IsRtf(sourcePath))
            {
                return PrepareRtf(sourcePath, stagingParent, cancellationToken);
            }

            if (isRtfExtension)
            {
                throw new InvalidDataException("Malformed or unreadable RTF document.");
            }

            // Legacy binary formats (.doc, .xls, .ppt) have no package to cut links from.
            return new PreparedOfficeSource(sourcePath, null, 0);
        }

        if (fetchable == 0)
        {
            return new PreparedOfficeSource(sourcePath, null, 0);
        }

        var stagingRoot = System.IO.Path.Combine(stagingParent, $".glimpse-source-{Guid.NewGuid():N}");
        var sanitizedPath = System.IO.Path.Combine(stagingRoot, System.IO.Path.GetFileName(sourcePath));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            using var destination = ZipFile.Open(sanitizedPath, ZipArchiveMode.Create);
            foreach (var sourceEntry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // An OpenDocument package is known by its first entry, stored as it is.
                var compression = sourceEntry.FullName == "mimetype" ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
                var destinationEntry = destination.CreateEntry(sourceEntry.FullName, compression);
                destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
                destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;

                using var output = destinationEntry.Open();
                if (IsRelationshipsEntry(sourceEntry))
                {
                    var document = LoadRelationships(sourceEntry, cancellationToken);
                    foreach (var relationship in FetchableRelationships(document).ToList())
                    {
                        relationship.Remove();
                    }

                    document.Save(output, SaveOptions.DisableFormatting);
                    continue;
                }

                using var input = sourceEntry.Open();
                if (openDocument && IsOpenDocumentPart(sourceEntry))
                {
                    CutOpenDocumentLinks(input, output, cancellationToken);
                    continue;
                }

                CopyTo(input, output, cancellationToken);
            }

            return new PreparedOfficeSource(sanitizedPath, stagingRoot, fetchable);
        }
        catch
        {
            try
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Preserve the original exception.
            }

            throw;
        }
    }

    private static bool IsZip(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[2];
            return stream.Read(header) == 2 && header[0] == (byte)'P' && header[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRtf(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[64];
            var read = stream.Read(header);
            var i = 0;
            while (i < read && header[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                i++;
            }

            if (i + 5 > read)
            {
                return false;
            }

            return header[i] == (byte)'{'
                && header[i + 1] == (byte)'\\'
                && (header[i + 2] | 0x20) == (byte)'r'
                && (header[i + 3] | 0x20) == (byte)'t'
                && (header[i + 4] | 0x20) == (byte)'f';
        }
        catch
        {
            return false;
        }
    }

    private static PreparedOfficeSource PrepareRtf(
        string sourcePath,
        string stagingParent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new FileInfo(sourcePath);
        if (info.Length > MaxRtfBytes)
        {
            throw new InvalidDataException("RTF document exceeded the preview safety limit.");
        }

        byte[] bytes;
        using (var stream = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[info.Length];
            var total = 0;
            while (total < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var n = stream.Read(bytes, total, bytes.Length - total);
                if (n <= 0)
                {
                    Array.Resize(ref bytes, total);
                    break;
                }

                total += n;
            }
        }

        var sanitized = SanitizeRtfBytes(bytes, cancellationToken, out var removed);

        // Always hand LibreOffice a private snapshot of the exact bytes we
        // scanned so the source file cannot change between scan and conversion.
        var stagingRoot = System.IO.Path.Combine(stagingParent, $".glimpse-source-{Guid.NewGuid():N}");
        var sanitizedPath = System.IO.Path.Combine(stagingRoot, System.IO.Path.GetFileName(sourcePath));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            File.WriteAllBytes(sanitizedPath, sanitized);
            return new PreparedOfficeSource(sanitizedPath, stagingRoot, removed);
        }
        catch
        {
            try
            {
                Directory.Delete(stagingRoot, recursive: true);
            }
            catch
            {
                // Preserve the original exception.
            }

            throw;
        }
    }

    private sealed class RtfGroupFrame
    {
        public int StartOffset;
        public int Uc = 1;
        public int LeadingStage; // 0 = start/ws, 1 = saw \*, 2 = destination resolved
        public bool Ignorable;
        public string? Destination;
        public bool Strip;
        public bool IsFldInst;
        public bool FldInstAmbiguous;
        public RtfGroupFrame? ActiveField;
        public RtfGroupFrame? ParentField;
        public RtfGroupFrame? ActiveFldInst;
        public StringBuilder? FldInstText;
    }

    /// <summary>
    /// Single-pass linear O(N) RTF sanitizer. Strips fetchable fields
    /// (INCLUDEPICTURE, INCLUDETEXT, LINK, IMPORT, DDE, DDEAUTO, DATABASE,
    /// RD, DATA, EMBED, etc., plus any non-HYPERLINK field containing an
    /// external URI/UNC path or ambiguous instruction), linked OLE objects
    /// (\objlink, \objautlink), and external templates (\*\template), while
    /// preserving normal text, embedded pictures, and clickable HYPERLINK
    /// fields. Decodes \'HH and \uN (with per-group \ucN fallback skipping),
    /// respects \binN binary runs, and fails closed on malformed/unbalanced
    /// groups, unsupported \ucN counts, or excessive nesting depth.
    /// </summary>
    internal static byte[] SanitizeRtfBytes(byte[] input, CancellationToken cancellationToken, out int removed)
    {
        removed = 0;
        var len = input.Length;
        var stack = new List<RtfGroupFrame>(64);
        var stripRanges = new List<(int Start, int End)>();
        var i = 0;
        var steps = 0;
        var sawRootGroup = false;
        var pendingUnicodeSkips = 0;

        static void AddStripRange(List<(int Start, int End)> ranges, int start, int end)
        {
            while (ranges.Count > 0 && ranges[^1].Start >= start)
            {
                ranges.RemoveAt(ranges.Count - 1);
            }

            ranges.Add((start, end));
        }

        while (i < len)
        {
            if ((++steps & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var b = input[i];
            if (b == (byte)'{')
            {
                if (stack.Count >= MaxRtfGroupDepth)
                {
                    throw new InvalidDataException("RTF group nesting exceeded the preview safety limit.");
                }

                var parent = stack.Count > 0 ? stack[^1] : null;
                var frame = new RtfGroupFrame
                {
                    StartOffset = i,
                    Uc = parent?.Uc ?? 1,
                    ActiveField = parent?.ActiveField,
                    ActiveFldInst = parent?.ActiveFldInst
                };
                if (stack.Count == 0)
                {
                    sawRootGroup = true;
                }

                stack.Add(frame);
                pendingUnicodeSkips = 0;
                i++;
                continue;
            }

            if (b == (byte)'}')
            {
                if (stack.Count == 0)
                {
                    throw new InvalidDataException("Malformed RTF: unexpected closing brace.");
                }

                var frame = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                pendingUnicodeSkips = 0;
                var end = i + 1;

                if (frame.IsFldInst)
                {
                    var text = frame.FldInstText?.ToString() ?? string.Empty;
                    if (frame.FldInstAmbiguous || IsFetchableRtfFieldInstruction(text))
                    {
                        if (frame.ParentField is not null)
                        {
                            frame.ParentField.Strip = true;
                        }
                        else
                        {
                            frame.Strip = true;
                        }
                    }
                }

                if (frame.Strip)
                {
                    // AddStripRange pops any child ranges subsumed by an enclosing range,
                    // keeping nested group stripping strictly O(N) without walking ancestors.
                    AddStripRange(stripRanges, frame.StartOffset, end);
                }

                i = end;
                continue;
            }

            var current = stack.Count > 0 ? stack[^1] : null;
            var fldInst = current?.ActiveFldInst;

            if (b == (byte)'\\')
            {
                if (i + 1 >= len)
                {
                    throw new InvalidDataException("Malformed RTF: trailing backslash.");
                }

                var next = input[i + 1];

                // Leading ignorable destination marker \*
                if (next == (byte)'*')
                {
                    if (current is { LeadingStage: 0 })
                    {
                        current.Ignorable = true;
                        current.LeadingStage = 1;
                    }

                    pendingUnicodeSkips = 0;
                    i += 2;
                    continue;
                }

                // Hex escape \'HH
                if (next == (byte)'\'')
                {
                    if (i + 3 >= len || !TryHexByte(input[i + 2], input[i + 3], out var decoded))
                    {
                        if (fldInst is not null)
                        {
                            fldInst.FldInstAmbiguous = true;
                        }

                        i = Math.Min(len, i + 2);
                        continue;
                    }

                    if (current is { LeadingStage: < 2 })
                    {
                        current.LeadingStage = 2;
                    }

                    if (pendingUnicodeSkips > 0)
                    {
                        pendingUnicodeSkips--;
                    }
                    else
                    {
                        fldInst?.FldInstText?.Append((char)decoded);
                    }

                    i += 4;
                    continue;
                }

                // Escaped literal or control symbol
                if (!IsAsciiLetter(next))
                {
                    if (current is { LeadingStage: < 2 })
                    {
                        current.LeadingStage = 2;
                    }

                    if (pendingUnicodeSkips > 0)
                    {
                        pendingUnicodeSkips--;
                    }
                    else if (fldInst?.FldInstText is { } sb)
                    {
                        if (next is (byte)'\\' or (byte)'{' or (byte)'}')
                        {
                            sb.Append((char)next);
                        }
                        else if (next is (byte)'~' or (byte)'\r' or (byte)'\n')
                        {
                            sb.Append(' ');
                        }
                    }

                    i += 2;
                    continue;
                }

                // Control word: \[A-Za-z]+(-?[0-9]+)?[ ]?
                var wordStart = i + 1;
                var j = wordStart;
                while (j < len && IsAsciiLetter(input[j]))
                {
                    j++;
                }

                var word = Encoding.ASCII.GetString(input, wordStart, j - wordStart);
                var paramStart = j;
                if (j < len && input[j] == (byte)'-')
                {
                    j++;
                }

                var digitsStart = j;
                while (j < len && input[j] is >= (byte)'0' and <= (byte)'9')
                {
                    j++;
                }

                var hasParam = j > digitsStart;
                long paramValue = 0;
                var validParam = hasParam
                    && long.TryParse(Encoding.ASCII.GetString(input, paramStart, j - paramStart), out paramValue);

                if (j < len && input[j] == (byte)' ')
                {
                    j++;
                }

                // \binN skips N raw binary bytes verbatim. Compare against remaining
                // bytes (len - j) to prevent signed 64-bit addition overflow.
                if (word.Equals("bin", StringComparison.OrdinalIgnoreCase))
                {
                    if (!validParam || paramValue < 0 || paramValue > len - j)
                    {
                        throw new InvalidDataException("Malformed RTF: invalid \\bin length.");
                    }

                    if (fldInst is not null)
                    {
                        fldInst.FldInstAmbiguous = true;
                    }

                    pendingUnicodeSkips = 0;
                    i = j + (int)paramValue;
                    continue;
                }

                // \ucN sets the Unicode fallback byte count for the current group.
                // Reject unsupported values fail-closed instead of silently clamping.
                if (word.Equals("uc", StringComparison.OrdinalIgnoreCase))
                {
                    if (!validParam || paramValue < 0 || paramValue > 32)
                    {
                        throw new InvalidDataException("Malformed RTF: unsupported \\uc fallback count.");
                    }

                    if (current is not null)
                    {
                        current.Uc = (int)paramValue;
                    }

                    i = j;
                    continue;
                }

                // \uN emits a UTF-16 code unit and skips the next Uc fallback chars.
                if (word.Equals("u", StringComparison.OrdinalIgnoreCase) && validParam)
                {
                    if (current is { LeadingStage: < 2 })
                    {
                        current.LeadingStage = 2;
                    }

                    var codeUnit = paramValue < 0 ? paramValue + 65536 : paramValue;
                    if (codeUnit is >= 0 and <= 0xFFFF)
                    {
                        fldInst?.FldInstText?.Append((char)codeUnit);
                    }
                    else if (fldInst is not null)
                    {
                        fldInst.FldInstAmbiguous = true;
                    }

                    pendingUnicodeSkips = current?.Uc ?? 1;
                    i = j;
                    continue;
                }

                // Resolve group destination on the first control word after { or {\*
                if (current is { LeadingStage: < 2 })
                {
                    current.LeadingStage = 2;
                    current.Destination = word;
                    if (current.Ignorable && word.Equals("template", StringComparison.OrdinalIgnoreCase))
                    {
                        current.Strip = true;
                    }
                    else if (!current.Ignorable && word.Equals("field", StringComparison.OrdinalIgnoreCase))
                    {
                        current.ActiveField = current;
                    }
                    else if (word.Equals("fldinst", StringComparison.OrdinalIgnoreCase))
                    {
                        current.IsFldInst = true;
                        current.ActiveFldInst = current;
                        current.FldInstText = new StringBuilder(64);
                        current.ParentField = current.ActiveField;
                    }
                }
                else if (current is { Ignorable: false }
                    && string.Equals(current.Destination, "object", StringComparison.OrdinalIgnoreCase)
                    && (word.Equals("objlink", StringComparison.OrdinalIgnoreCase)
                        || word.Equals("objautlink", StringComparison.OrdinalIgnoreCase)))
                {
                    current.Strip = true;
                }
                else if (fldInst?.FldInstText is { } sb)
                {
                    if (word.Equals("tab", StringComparison.OrdinalIgnoreCase)
                        || word.Equals("line", StringComparison.OrdinalIgnoreCase)
                        || word.Equals("par", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append(' ');
                    }
                }

                i = j;
                continue;
            }

            // Plain byte
            if (current is { LeadingStage: < 2 })
            {
                if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
                {
                    current.LeadingStage = 2;
                }
            }

            if (pendingUnicodeSkips > 0)
            {
                // CR/LF are ignored in RTF and do not consume a \uc fallback char.
                if (b is not ((byte)'\r' or (byte)'\n'))
                {
                    pendingUnicodeSkips--;
                }
            }
            else if (fldInst?.FldInstText is { } sb)
            {
                sb.Append(b is (byte)'\r' or (byte)'\n' ? ' ' : (char)b);
            }

            i++;
        }

        if (!sawRootGroup || stack.Count != 0)
        {
            throw new InvalidDataException("Malformed RTF: unbalanced group braces.");
        }

        if (stripRanges.Count == 0)
        {
            return input;
        }

        removed = stripRanges.Count;
        using var output = new MemoryStream(len);
        var cursor = 0;
        foreach (var (start, end) in stripRanges)
        {
            if (start > cursor)
            {
                output.Write(input, cursor, start - cursor);
            }

            cursor = end;
        }

        if (cursor < len)
        {
            output.Write(input, cursor, len - cursor);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decides whether a decoded RTF \fldinst instruction can fetch external
    /// content. Known fetchable field types are always stripped. Any other
    /// field except HYPERLINK is also stripped if it contains an external
    /// URI/UNC reference or cannot be identified cleanly.
    /// </summary>
    internal static bool IsFetchableRtfFieldInstruction(string instruction)
    {
        var keyword = ExtractLeadingFieldKeyword(instruction);
        if (keyword.Length == 0)
        {
            // Non-empty instruction with no identifiable ASCII keyword (or formula starting with '=').
            var trimmed = instruction.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '=')
            {
                return false;
            }

            return ContainsExternalReferenceHint(trimmed);
        }

        if (FetchableRtfFields.Contains(keyword))
        {
            return true;
        }

        if (keyword.Equals("HYPERLINK", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ContainsExternalReferenceHint(instruction);
    }

    internal static string ExtractLeadingFieldKeyword(string instruction)
    {
        var i = 0;
        var len = instruction.Length;
        while (i < len && instruction[i] is ' ' or '\t' or '\r' or '\n' or '"' or '\'')
        {
            i++;
        }

        var start = i;
        while (i < len && instruction[i] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
        {
            i++;
        }

        return i > start ? instruction[start..i] : string.Empty;
    }

    private static bool ContainsExternalReferenceHint(string text) =>
        text.Contains(@"\\", StringComparison.Ordinal)
        || text.Contains("://", StringComparison.Ordinal)
        || text.Contains("file:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("http:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("https:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("smb:", StringComparison.OrdinalIgnoreCase)
        || text.Contains("ftp:", StringComparison.OrdinalIgnoreCase);

    private static bool IsAsciiLetter(byte b) =>
        (b | 0x20) is >= (byte)'a' and <= (byte)'z';

    private static bool TryHexByte(byte hi, byte lo, out byte value)
    {
        value = 0;
        var h = HexVal(hi);
        var l = HexVal(lo);
        if (h < 0 || l < 0)
        {
            return false;
        }

        value = (byte)((h << 4) | l);
        return true;
    }

    private static int HexVal(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - (byte)'0',
        >= (byte)'A' and <= (byte)'F' => b - (byte)'A' + 10,
        >= (byte)'a' and <= (byte)'f' => b - (byte)'a' + 10,
        _ => -1
    };

    private static bool IsRelationshipsEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    /// <summary>The parts that carry links: the document's own, and those of each object embedded in it.</summary>
    private static bool IsOpenDocumentPart(ZipArchiveEntry entry) =>
        entry.Name is "content.xml" or "styles.xml" or "meta.xml";

    /// <summary>
    /// Reads one OpenDocument part and counts the links that leave the
    /// package: a linked picture, section, sheet, object, font or template.
    /// With an <paramref name="output"/> it also writes the part without
    /// them. A part that is not well-formed XML throws, and the file never
    /// reaches the engine, which might read it more forgivingly.
    /// </summary>
    private static int CutOpenDocumentLinks(Stream input, Stream? output, CancellationToken cancellationToken)
    {
        var cut = 0;
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        using var writer = output is null
            ? null
            : XmlWriter.Create(output, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    cancellationToken.ThrowIfCancellationRequested();
                    var hyperlink = reader.LocalName == "a" && reader.NamespaceURI is OpenDocumentText or OpenDocumentDrawing;
                    var empty = reader.IsEmptyElement;
                    writer?.WriteStartElement(reader.Prefix, reader.LocalName, reader.NamespaceURI);
                    while (reader.MoveToNextAttribute())
                    {
                        if (!hyperlink
                            && reader.LocalName == "href"
                            && reader.NamespaceURI == XLink
                            && LeavesPackage(reader.Value))
                        {
                            cut++;
                            continue;
                        }

                        writer?.WriteAttributeString(reader.Prefix, reader.LocalName, reader.NamespaceURI, reader.Value);
                    }

                    if (empty)
                    {
                        writer?.WriteEndElement();
                    }

                    break;
                case XmlNodeType.EndElement:
                    writer?.WriteFullEndElement();
                    break;
                case XmlNodeType.Text:
                    writer?.WriteString(reader.Value);
                    break;
                case XmlNodeType.CDATA:
                    writer?.WriteCData(reader.Value);
                    break;
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    writer?.WriteWhitespace(reader.Value);
                    break;
                case XmlNodeType.ProcessingInstruction:
                    writer?.WriteProcessingInstruction(reader.Name, reader.Value);
                    break;
                case XmlNodeType.Comment:
                    writer?.WriteComment(reader.Value);
                    break;
            }
        }

        return cut;
    }

    /// <summary>
    /// A reference stays in the package when it is a plain path downward:
    /// "Pictures/1.png", "./Object 1". Anything with a scheme, a drive, a
    /// leading slash, a share or a step up is outside, the way the engine
    /// reads it. The lone ".." is an embedded chart naming the document
    /// that holds it, and loads nothing.
    /// </summary>
    internal static bool LeavesPackage(string reference)
    {
        reference = Uri.UnescapeDataString(reference.Trim());
        if (reference.Length == 0 || reference[0] == '#' || reference == "..")
        {
            return false;
        }

        if (reference[0] is '/' or '\\')
        {
            return true;
        }

        var separator = reference.IndexOfAny(['/', '\\', '?', '#']);
        var colon = reference.IndexOf(':');
        return (colon >= 0 && (separator < 0 || colon < separator))
            || reference.Split('/', '\\').Contains("..");
    }

    private static IEnumerable<XElement> FetchableRelationships(XDocument document) =>
        document.Descendants(PackageRelationships + "Relationship")
            .Where(relationship =>
                string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase)
                && !HyperlinkTypes.Contains((string?)relationship.Attribute("Type") ?? string.Empty));

    private static XDocument LoadRelationships(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Length > MaxRelationshipsBytes)
        {
            throw new InvalidDataException("Office relationships exceeded the preview safety limit.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRelationshipsBytes
        };
        var document = XmlNesting.Load(entry.Open, settings, LoadOptions.PreserveWhitespace);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    private static void CopyTo(
        Stream input,
        Stream output,
        CancellationToken cancellationToken)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = input.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    return;
                }

                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
