using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// What sign mode learns before the user places anything: the writer's own
/// page count, or the sentence that explains why this file cannot be saved.
/// </summary>
internal readonly record struct PdfSignCheck(int PageCount, string? Problem);

internal readonly record struct PdfPageRect(double Left, double Bottom, double Right, double Top)
{
    public double Width => Right - Left;

    public double Height => Top - Bottom;

    public bool IsValid =>
        double.IsFinite(Left)
        && double.IsFinite(Bottom)
        && double.IsFinite(Right)
        && double.IsFinite(Top)
        && Width > 0.1
        && Height > 0.1;
}

internal static class PdfFillSignWriter
{
    private const int MaxPages = 3000;
    private const int MaxObjects = 200000;
    private const int MaxImagePixels = 24_000_000;
    private const long MaxPdfBytes = 256L * 1024 * 1024;

    // Arrays and dictionaries nest this deep at most. Deeper is refused,
    // never walked.
    private const int MaxNesting = 64;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(750);

    private static readonly Regex ObjectRegex = new(
        @"(?ms)(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+obj\b(?<body>.*?)\bendobj\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex ObjectHeaderRegex = new(
        @"\G\d{1,10}\s+\d{1,5}\s+obj\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex NumberRegex = new(
        @"^[-+]?(?:\d+(?:\.\d*)?|\.\d+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    // Every reason a PDF cannot be signed is said in one of these sentences.
    // They reach the user unchanged, so they name no app and no parser.
    internal const string UnsupportedStructure = "This PDF is built in a way that can't be signed here yet.";
    internal const string UnreadableItem = "Something placed on the page couldn't be written. Nothing was saved.";

    /// <summary>
    /// Reads the document exactly as a save would, without writing. Run on
    /// entry, so a file the writer cannot update says so before any work is
    /// placed on it instead of after.
    /// </summary>
    public static PdfSignCheck Preflight(string path)
    {
        try
        {
            var document = PdfDocumentModel.Read(path);
            foreach (var page in document.Pages)
            {
                ReadExistingAnnotationEntries(document, page.Body);
            }

            return new PdfSignCheck(document.Pages.Count, null);
        }
        catch (InvalidOperationException ex)
        {
            return new PdfSignCheck(0, ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return new PdfSignCheck(0, UnsupportedStructure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new PdfSignCheck(0, "This PDF can't be read right now.");
        }
    }

    public static void WriteSignedCopy(string sourcePath, string outputPath, IReadOnlyList<SignPlacement> placements)
    {
        if (placements.Count == 0)
        {
            throw new InvalidOperationException(UnreadableItem);
        }

        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The signed PDF must be written to a different file first.");
        }

        var created = false;
        try
        {
            var document = PdfDocumentModel.Read(sourcePath);
            var objects = BuildUpdateObjects(document, placements);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // A file already at this name is someone else's: never written over.
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            output.Write(document.Bytes);
            WriteIncrementalUpdate(output, document, objects);
        }
        catch
        {
            // Only a file this call made is taken back.
            if (created)
            {
                TryDelete(outputPath);
            }

            throw;
        }
    }

    /// <summary>
    /// A copy whose oversized pages are shrunk to <paramref name="maxEdgePoints"/>,
    /// for documents a viewer cannot frame. The Chromium zoom ladder stops at
    /// 25%, so a page that opens at 2% can never be zoomed back out to how it
    /// arrived. Shrinking the page geometry puts the whole document inside the
    /// ladder, and vector artwork loses nothing: it is drawn at the new scale
    /// rather than resampled. False when every page already fits.
    /// </summary>
    public static bool TryWriteScaledCopy(string sourcePath, string outputPath, double maxEdgePoints)
    {
        var document = PdfDocumentModel.Read(sourcePath);

        // A page-level scale moves page content, not every PDF coordinate
        // system or interactive object. Leave formats this small parser cannot
        // transform completely to the original viewer path. PDF names may use
        // #xx escapes, so a raw substring check is not a safe gate.
        if (FindUnsupportedScaleFeature(document.Bytes) is { } unsupported)
        {
            DiagnosticLog.Write($"pdf scale skipped unsupported /{unsupported}");
            return false;
        }

        var objects = new List<PdfWriteObject>();
        var nextObject = document.MaxObjectNumber + 1;
        PdfRef Allocate() => new(nextObject++, 0);

        foreach (var page in document.Pages)
        {
            var longest = Math.Max(page.Box.Width, page.Box.Height);
            if (longest <= maxEdgePoints)
            {
                continue;
            }

            // The written scale and the box have to agree exactly, so round
            // once and read it back rather than formatting twice.
            var scaleText = (maxEdgePoints / longest).ToString("0.######", CultureInfo.InvariantCulture);
            var scale = double.Parse(scaleText, CultureInfo.InvariantCulture);
            if (!double.IsFinite(scale) || scale <= 0)
            {
                continue;
            }

            // The page's own content is left untouched between a matched
            // q/Q pair, so nothing has to be understood to be scaled.
            var open = Allocate();
            var close = Allocate();
            objects.Add(new PdfWriteObject(open, StreamObject("<<", AsLatin1($"q {scaleText} 0 0 {scaleText} 0 0 cm\n"))));
            objects.Add(new PdfWriteObject(close, StreamObject("<<", AsLatin1("\nQ"))));

            if (WrapPageContents(document, page.Body, open, close) is not { } contents)
            {
                return false;
            }

            var body = ReplaceOrAddDictionaryValue(page.Body, "Contents", contents);
            body = ScalePageBoxes(body, page.Boxes, scale);
            objects.Add(new PdfWriteObject(page.Ref, AsLatin1(body)));
        }

        if (objects.Count == 0)
        {
            return false;
        }

        try
        {
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            using var output = File.Create(outputPath);
            output.Write(document.Bytes);
            WriteIncrementalUpdate(output, document, objects);
        }
        catch
        {
            TryDelete(outputPath);
            throw;
        }

        return true;
    }

    /// <summary>
    /// The page's existing content, wrapped by the two new streams. Contents
    /// is a stream reference or an array of them, and the array itself may be
    /// reached through a reference: splicing that reference in as though it
    /// were a stream nests an array inside an array and the page draws
    /// nothing. Returns null when the shape is not one of those.
    /// </summary>
    private static string? WrapPageContents(
        PdfDocumentModel document,
        string pageBody,
        PdfRef open,
        PdfRef close)
    {
        if (!TryReadNamedValue(pageBody, 0, "Contents", out var value, out _))
        {
            return $"[{open} R {close} R]";
        }

        var existing = value;
        if (TryParseReference(value, out var reference))
        {
            if (!document.Objects.TryGetValue(reference, out var target))
            {
                return null;
            }

            var body = target.Body.Trim();
            if (body.StartsWith('['))
            {
                if (ArrayItems(ObjectValue(body)) is not { } items)
                {
                    return null;
                }

                existing = string.Join(" ", items);
            }
            else if (!body.Contains("stream", StringComparison.Ordinal))
            {
                // Neither a stream nor an array: not something to splice.
                return null;
            }
        }
        else if (ArrayItems(value) is { } items)
        {
            existing = string.Join(" ", items);
        }
        else
        {
            return null;
        }

        return $"[{open} R {existing} {close} R]";
    }

    private static string ScalePageBoxes(string body, PdfPageBoxes boxes, double scale)
    {
        body = ScalePageBox(body, "MediaBox", boxes.MediaBox, scale);
        body = ScalePageBox(body, "CropBox", boxes.CropBox, scale);
        body = ScalePageBox(body, "BleedBox", boxes.BleedBox, scale);
        body = ScalePageBox(body, "TrimBox", boxes.TrimBox, scale);
        return ScalePageBox(body, "ArtBox", boxes.ArtBox, scale);
    }

    private static string ScalePageBox(string body, string name, PdfPageBox? box, double scale)
    {
        if (box is not { IsValid: true } value)
        {
            return body;
        }

        var scaled = $"[{Format(value.Left * scale)} {Format(value.Bottom * scale)} "
            + $"{Format(value.Right * scale)} {Format(value.Top * scale)}]";
        return ReplaceOrAddDictionaryValue(body, name, scaled);
    }

    private static string? FindUnsupportedScaleFeature(ReadOnlySpan<byte> bytes) =>
        FindName(bytes, "PatternType", "ShadingType", "Annots", "ObjStm", "UserUnit", "ByteRange");

    /// <summary>
    /// The first of <paramref name="names"/> spelled anywhere in the file,
    /// read as a viewer reads names: any character may be written as #xx,
    /// so a raw substring is not a gate.
    /// </summary>
    private static string? FindName(ReadOnlySpan<byte> bytes, params string[] names)
    {
        Span<byte> decoded = stackalloc byte[16];
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'/')
            {
                continue;
            }

            var cursor = index + 1;
            var length = 0;
            while (cursor < bytes.Length && !IsPdfDelimiter(bytes[cursor]))
            {
                var value = bytes[cursor++];
                if (value == (byte)'#'
                    && cursor + 1 < bytes.Length
                    && TryHex(bytes[cursor], out var high)
                    && TryHex(bytes[cursor + 1], out var low))
                {
                    value = (byte)((high << 4) | low);
                    cursor += 2;
                }

                if (length < decoded.Length)
                {
                    decoded[length] = value;
                }

                length++;
            }

            foreach (var name in names)
            {
                var same = name.Length == length && length <= decoded.Length;
                for (var at = 0; same && at < length; at++)
                {
                    same = decoded[at] == name[at];
                }

                if (same)
                {
                    return name;
                }
            }

            index = cursor - 1;
        }

        return null;
    }

    private static bool IsPdfDelimiter(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
            or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
            or (byte)'/' or (byte)'%';

    private static bool TryHex(byte value, out int decoded)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            decoded = value - (byte)'0';
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            decoded = value - (byte)'A' + 10;
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            decoded = value - (byte)'a' + 10;
            return true;
        }

        decoded = 0;
        return false;
    }

    internal static PdfPageRect MapDisplayRectToPageRectForTest(
        double left,
        double top,
        double width,
        double height,
        double x1,
        double y1,
        double x2,
        double y2,
        int rotate)
    {
        return MapDisplayRectToPageRect(left, top, width, height, new PdfPageBox(x1, y1, x2, y2), rotate);
    }

    private static List<PdfWriteObject> BuildUpdateObjects(PdfDocumentModel document, IReadOnlyList<SignPlacement> placements)
    {
        var nextObject = document.MaxObjectNumber + 1;
        var newObjects = new List<PdfWriteObject>();
        var annotationsByPage = new Dictionary<PdfRef, List<PdfRef>>();
        var imagesByHash = new Dictionary<string, PdfRef>(StringComparer.Ordinal);
        PdfRef? fontRef = null;

        PdfRef Allocate() => new(nextObject++, 0);

        PdfRef EnsureFont()
        {
            if (fontRef is { } existing)
            {
                return existing;
            }

            var created = Allocate();
            fontRef = created;
            newObjects.Add(new PdfWriteObject(
                created,
                AsLatin1("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")));
            return created;
        }

        // A logo stamped on every page is one image in the file, drawn by
        // as many appearances as there are placements.
        PdfRef EnsureImage(byte[] png)
        {
            var hash = Convert.ToHexString(SHA256.HashData(png));
            if (imagesByHash.TryGetValue(hash, out var existing))
            {
                return existing;
            }

            var image = DecodePng(png);
            var alphaRef = image.HasAlpha ? Allocate() : (PdfRef?)null;
            var imageRef = Allocate();
            if (alphaRef is { } maskRef)
            {
                newObjects.Add(new PdfWriteObject(
                    maskRef,
                    ImageObject(image.Width, image.Height, "/DeviceGray", image.AlphaBytes, null)));
            }

            newObjects.Add(new PdfWriteObject(
                imageRef,
                ImageObject(image.Width, image.Height, "/DeviceRGB", image.RgbBytes, alphaRef)));
            imagesByHash[hash] = imageRef;
            return imageRef;
        }

        foreach (var placement in placements)
        {
            ValidatePlacement(document, placement);
            var page = document.Pages[placement.PageIndex];
            var rect = MapDisplayRectToPageRect(placement.X, placement.Y, placement.Width, placement.Height, page.Box, page.Rotate);
            if (!rect.IsValid)
            {
                throw new InvalidOperationException(UnreadableItem);
            }

            var annotationRef = placement.Kind == SignPlacementKind.Date
                ? AddTextAnnotation(newObjects, Allocate, EnsureFont(), placement.Text!, rect, page.Rotate)
                : AddImageAnnotation(newObjects, Allocate, EnsureImage(placement.ImagePng!), placement.Kind, rect, page.Rotate);

            if (!annotationsByPage.TryGetValue(page.Ref, out var list))
            {
                list = [];
                annotationsByPage[page.Ref] = list;
            }

            list.Add(annotationRef);
        }

        foreach (var (pageRef, annotationRefs) in annotationsByPage)
        {
            var page = document.Pages.First(item => item.Ref.Equals(pageRef));
            var updatedPage = UpdatePageAnnotations(document, page, annotationRefs);
            newObjects.Add(new PdfWriteObject(page.Ref, AsLatin1(updatedPage)));
        }

        return newObjects;
    }

    private static void ValidatePlacement(PdfDocumentModel document, SignPlacement placement)
    {
        var inside = double.IsFinite(placement.X)
            && double.IsFinite(placement.Y)
            && double.IsFinite(placement.Width)
            && double.IsFinite(placement.Height)
            && placement.X >= 0
            && placement.Y >= 0
            && placement.Width > 0
            && placement.Height > 0
            && placement.X + placement.Width <= 1.0001
            && placement.Y + placement.Height <= 1.0001;
        var content = placement.Kind == SignPlacementKind.Date
            ? !string.IsNullOrWhiteSpace(placement.Text)
            : placement.ImagePng is { Length: > 0 };
        if (placement.PageIndex < 0 || placement.PageIndex >= document.Pages.Count || !inside || !content)
        {
            throw new InvalidOperationException(UnreadableItem);
        }
    }

    /// <summary>
    /// An appearance is drawn upright in the displayed page's orientation;
    /// its matrix turns it against the page's /Rotate so the viewer's turn
    /// brings it back upright. Width and height are in display terms.
    /// </summary>
    private static (double Width, double Height, string Matrix) DisplayFrame(PdfPageRect rect, int rotate) =>
        NormalizeRotation(rotate) switch
        {
            90 => (rect.Height, rect.Width, $" /Matrix [0 1 -1 0 {Format(rect.Width)} 0]"),
            180 => (rect.Width, rect.Height, $" /Matrix [-1 0 0 -1 {Format(rect.Width)} {Format(rect.Height)}]"),
            270 => (rect.Height, rect.Width, $" /Matrix [0 -1 1 0 0 {Format(rect.Height)}]"),
            _ => (rect.Width, rect.Height, string.Empty)
        };

    private static PdfRef AddTextAnnotation(
        List<PdfWriteObject> objects,
        Func<PdfRef> allocate,
        PdfRef fontRef,
        string text,
        PdfPageRect rect,
        int rotate)
    {
        var appearanceRef = allocate();
        var annotationRef = allocate();
        var (width, height, matrix) = DisplayFrame(rect, rotate);

        // The same rule the sign surface lays its label out with: the font
        // is a fixed share of the frame height and the text is centered.
        var fontSize = SignText.FontScale * height;
        var left = (width - SignText.WidthEm(text) * fontSize) / 2;
        var baseline = SignText.BaselineScale * height;
        var stream = AsLatin1(
            $"q\nBT\n/Helv {Format(fontSize)} Tf\n0 0 0 rg\n{Format(left)} {Format(baseline)} Td\n{WinAnsiLiteral(text)} Tj\nET\nQ\n");
        objects.Add(new PdfWriteObject(
            appearanceRef,
            StreamObject(
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 {Format(width)} {Format(height)}]{matrix} /Resources << /Font << /Helv {fontRef} R >> >>",
                stream)));
        objects.Add(new PdfWriteObject(
            annotationRef,
            AsLatin1(
                "<< /Type /Annot /Subtype /FreeText "
                + $"/Rect {PdfRect(rect)} /Contents {PdfTextString(text)} /F 4 /Border [0 0 0] "
                + $"/DA (/Helv {Format(fontSize)} Tf 0 0 0 rg) /AP << /N {appearanceRef} R >> >>")));
        return annotationRef;
    }

    private static PdfRef AddImageAnnotation(
        List<PdfWriteObject> objects,
        Func<PdfRef> allocate,
        PdfRef imageRef,
        SignPlacementKind kind,
        PdfPageRect rect,
        int rotate)
    {
        var appearanceRef = allocate();
        var annotationRef = allocate();
        var (width, height, matrix) = DisplayFrame(rect, rotate);
        var formStream = AsLatin1($"q\n{Format(width)} 0 0 {Format(height)} 0 0 cm\n/Im1 Do\nQ\n");
        objects.Add(new PdfWriteObject(
            appearanceRef,
            StreamObject(
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 {Format(width)} {Format(height)}]{matrix} /Resources << /XObject << /Im1 {imageRef} R >> >>",
                formStream)));

        var label = kind == SignPlacementKind.Signature ? "Signature" : "Stamp";
        objects.Add(new PdfWriteObject(
            annotationRef,
            AsLatin1(
                "<< /Type /Annot /Subtype /Stamp "
                + $"/Rect {PdfRect(rect)} /Contents ({label}) /F 4 /Border [0 0 0] "
                + $"/AP << /N {appearanceRef} R >> >>")));
        return annotationRef;
    }

    private static byte[] ImageObject(int width, int height, string colorSpace, byte[] pixels, PdfRef? alphaRef)
    {
        var compressed = Deflate(pixels);
        var mask = alphaRef is { } smask ? $" /SMask {smask} R" : string.Empty;
        return StreamObject(
            $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {colorSpace} /BitsPerComponent 8 /Filter /FlateDecode{mask}",
            compressed);
    }

    private static string UpdatePageAnnotations(PdfDocumentModel document, PdfPageInfo page, IReadOnlyList<PdfRef> newAnnotations)
    {
        var oldEntries = ReadExistingAnnotationEntries(document, page.Body);
        var added = string.Join(" ", newAnnotations.Select(item => $"{item} R"));
        var combined = string.IsNullOrWhiteSpace(oldEntries)
            ? added
            : $"{oldEntries} {added}";
        return ReplaceOrAddDictionaryValue(page.Body, "Annots", $"[{combined}]");
    }

    private static string ReadExistingAnnotationEntries(PdfDocumentModel document, string pageBody)
    {
        if (!TryReadNamedValue(pageBody, 0, "Annots", out var value, out _))
        {
            return string.Empty;
        }

        return ArrayItems(Resolve(document.Objects, value)) is { } items
            ? string.Join(" ", items)
            : throw new InvalidOperationException(UnsupportedStructure);
    }

    private static string ReplaceOrAddDictionaryValue(string body, string name, string replacementValue)
    {
        if (TryReadNamedValue(body, 0, name, out _, out var range))
        {
            return body[..range.Start] + $"/{name} {replacementValue}" + body[range.End..];
        }

        // A new key goes just before the outer dictionary's own '>>'.
        var start = SkipSpace(body, 0);
        var end = At(body, start, "<<") ? SkipObject(body, start) : -1;
        if (end < 0)
        {
            throw new InvalidOperationException(UnsupportedStructure);
        }

        return body[..(end - 2)] + $" /{name} {replacementValue} " + body[(end - 2)..];
    }

    /// <summary>
    /// Reads /<paramref name="name"/> from the dictionary's own keys; a key
    /// inside a nested value never answers for it. False when the text is no
    /// dictionary or lacks the key. A dictionary that can't be walked, or
    /// names the key twice, is refused: the viewer reads the last of two.
    /// </summary>
    private static bool TryReadNamedValue(string text, int start, string name, out string value, out PdfTextRange range)
    {
        value = string.Empty;
        range = default;
        var found = false;
        var index = SkipSpace(text, start);
        if (!At(text, index, "<<"))
        {
            return false;
        }

        index += 2;
        while (true)
        {
            index = SkipSpace(text, index);
            if (At(text, index, ">>"))
            {
                return found;
            }

            var keyEnd = At(text, index, "/") ? SkipObject(text, index) : -1;
            var valueEnd = keyEnd < 0 ? -1 : SkipObject(text, keyEnd);
            if (valueEnd < 0)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            if (DecodeName(text, index + 1, keyEnd) == name)
            {
                if (found)
                {
                    throw new InvalidOperationException(UnsupportedStructure);
                }

                found = true;
                value = text[SkipSpace(text, keyEnd)..valueEnd];
                range = new PdfTextRange(index, valueEnd);
            }

            index = valueEnd;
        }
    }

    private static string? ValueOf(string? dictionary, string name) =>
        dictionary is not null && TryReadNamedValue(dictionary, 0, name, out var value, out _) ? value : null;

    /// <summary>The value itself, or the object it refers to. Null when that object is out of reach.</summary>
    private static string? Resolve(Dictionary<PdfRef, PdfObject> objects, string? value) =>
        value is null || !TryParseReference(value, out var reference) ? value
            : objects.TryGetValue(reference, out var target) ? ObjectValue(target.Body) : null;

    /// <summary>An object's own value, without the stream data that may follow it.</summary>
    private static string? ObjectValue(string body)
    {
        var start = SkipSpace(body, 0);
        var end = SkipObject(body, start);
        return end < 0 ? null : body[start..end];
    }

    /// <summary>The items of an array value, a reference counting as one. Null when it is no array.</summary>
    private static List<string>? ArrayItems(string? value)
    {
        if (value is null || !At(value, 0, "[") || SkipObject(value, 0) != value.Length)
        {
            return null;
        }

        var items = new List<string>();
        var index = 1;
        while (true)
        {
            index = SkipSpace(value, index);
            if (At(value, index, "]"))
            {
                return items;
            }

            var end = SkipObject(value, index);
            if (end < 0)
            {
                return null;
            }

            items.Add(value[index..end]);
            index = end;
        }
    }

    /// <summary>The name a value holds, decoded, or null when it holds something else.</summary>
    private static string? NameValue(string? value) =>
        value is not null && At(value, 0, "/") && SkipObject(value, 0) == value.Length
            ? DecodeName(value, 1, value.Length)
            : null;

    /// <summary>A name as a viewer reads it: each #xx is the character it spells.</summary>
    private static string DecodeName(string text, int start, int end)
    {
        var name = new StringBuilder(end - start);
        for (var index = start; index < end; index++)
        {
            if (text[index] == '#'
                && index + 2 < end
                && TryHex((byte)text[index + 1], out var high)
                && TryHex((byte)text[index + 2], out var low))
            {
                name.Append((char)((high << 4) | low));
                index += 2;
                continue;
            }

            name.Append(text[index]);
        }

        return name.ToString();
    }

    /// <summary>
    /// The index just past the object at <paramref name="start"/>, or -1
    /// when it is not well formed. Strings and nested containers are
    /// stepped over whole, and a reference counts as one object.
    /// </summary>
    private static int SkipObject(string text, int start)
    {
        var closers = new Stack<char>();
        var first = SkipSpace(text, start);
        var index = first;
        do
        {
            index = SkipSpace(text, index);
            if (index >= text.Length)
            {
                return -1;
            }

            var ch = text[index];
            if (ch == '[' || At(text, index, "<<"))
            {
                if (closers.Count == MaxNesting)
                {
                    return -1;
                }

                closers.Push(ch == '[' ? ']' : '>');
                index += ch == '[' ? 1 : 2;
            }
            else if (ch == ']' || At(text, index, ">>"))
            {
                if (closers.Count == 0 || closers.Pop() != ch)
                {
                    return -1;
                }

                index += ch == ']' ? 1 : 2;
            }
            else
            {
                index = SkipToken(text, index);
                if (index < 0)
                {
                    return -1;
                }
            }
        }
        while (closers.Count > 0);

        return ReferenceEnd(text, first, index);
    }

    /// <summary>The index just past one string, name, number or keyword, or -1 for a stray delimiter.</summary>
    private static int SkipToken(string text, int start)
    {
        switch (text[start])
        {
            case '(':
                return FindLiteralEnd(text, start);
            case '<':
                var close = text.IndexOf('>', start + 1);
                return close < 0 ? -1 : close + 1;
            case ')' or '>' or '{' or '}':
                return -1;
        }

        var index = start + 1;
        while (index < text.Length && !IsPdfDelimiter((byte)text[index]))
        {
            index++;
        }

        return index;
    }

    /// <summary>
    /// Extends an object number at [<paramref name="start"/>, <paramref name="end"/>)
    /// over the generation and R that make it a reference, when they follow.
    /// </summary>
    private static int ReferenceEnd(string text, int start, int end)
    {
        if (!IsDigits(text, start, end))
        {
            return end;
        }

        var generation = SkipSpace(text, end);
        var generationEnd = generation;
        while (generationEnd < text.Length && char.IsAsciiDigit(text[generationEnd]))
        {
            generationEnd++;
        }

        var r = SkipSpace(text, generationEnd);
        var isReference = generation > end
            && generationEnd > generation
            && r > generationEnd
            && At(text, r, "R")
            && (r + 1 == text.Length || IsPdfDelimiter((byte)text[r + 1]));
        return isReference ? r + 1 : end;
    }

    private static bool IsDigits(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (!char.IsAsciiDigit(text[index]))
            {
                return false;
            }
        }

        return end > start;
    }

    /// <summary>Steps over PDF white space and comments.</summary>
    private static int SkipSpace(string text, int index)
    {
        while (index < text.Length)
        {
            if (text[index] is '\0' or '\t' or '\n' or '\f' or '\r' or ' ')
            {
                index++;
            }
            else if (text[index] == '%')
            {
                while (index < text.Length && text[index] is not ('\r' or '\n'))
                {
                    index++;
                }
            }
            else
            {
                break;
            }
        }

        return index;
    }

    private static bool At(string text, int index, string token) =>
        index >= 0 && index <= text.Length && text.AsSpan(index).StartsWith(token, StringComparison.Ordinal);

    private static bool TryParseReference(string value, out PdfRef reference)
    {
        reference = default;
        var match = Regex.Match(
            value,
            @"^\s*(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+R\s*$",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || !int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
        {
            return false;
        }

        reference = new PdfRef(number, generation);
        return true;
    }

    private static PdfPageRect MapDisplayRectToPageRect(
        double left,
        double topEdge,
        double width,
        double height,
        PdfPageBox box,
        int rotate)
    {
        var normalizedRotate = NormalizeRotation(rotate);
        var displayWidth = normalizedRotate is 90 or 270 ? box.Height : box.Width;
        var displayHeight = normalizedRotate is 90 or 270 ? box.Width : box.Height;
        var sx = left * displayWidth;
        var top = topEdge * displayHeight;
        var sw = width * displayWidth;
        var sh = height * displayHeight;
        var sy = displayHeight - top - sh;

        return normalizedRotate switch
        {
            90 => new PdfPageRect(
                box.Left + box.Width - sy - sh,
                box.Bottom + sx,
                box.Left + box.Width - sy,
                box.Bottom + sx + sw),
            180 => new PdfPageRect(
                box.Left + box.Width - sx - sw,
                box.Bottom + box.Height - sy - sh,
                box.Left + box.Width - sx,
                box.Bottom + box.Height - sy),
            270 => new PdfPageRect(
                box.Left + sy,
                box.Bottom + box.Height - sx - sw,
                box.Left + sy + sh,
                box.Bottom + box.Height - sx),
            _ => new PdfPageRect(
                box.Left + sx,
                box.Bottom + sy,
                box.Left + sx + sw,
                box.Bottom + sy + sh)
        };
    }

    private static int NormalizeRotation(int rotate) => ((rotate % 360) + 360) % 360;

    private static void WriteIncrementalUpdate(Stream output, PdfDocumentModel document, IReadOnlyList<PdfWriteObject> objects)
    {
        if (output.Length > 0 && output.Position == output.Length)
        {
            WriteAscii(output, "\n");
        }

        var offsets = new SortedDictionary<PdfRef, long>(PdfRefComparer.Instance);
        foreach (var obj in objects)
        {
            offsets[obj.Ref] = output.Position;
            WriteAscii(output, $"{obj.Ref.Number} {obj.Ref.Generation} obj\n");
            output.Write(obj.Body);
            WriteAscii(output, "\nendobj\n");
        }

        var xrefOffset = output.Position;
        WriteAscii(output, "xref\n");
        foreach (var group in ConsecutiveGroups(offsets.Keys))
        {
            WriteAscii(output, $"{group[0].Number} {group.Count}\n");
            foreach (var reference in group)
            {
                WriteAscii(output, $"{offsets[reference]:0000000000} {reference.Generation:00000} n \n");
            }
        }

        var size = Math.Max(document.MaxObjectNumber, offsets.Keys.Max(item => item.Number)) + 1;
        WriteAscii(output, $"trailer\n<< /Size {size} /Root {document.Root} R");
        if (document.Info is { } info)
        {
            WriteAscii(output, $" /Info {info} R");
        }

        if (!string.IsNullOrWhiteSpace(document.IdText))
        {
            WriteAscii(output, $" /ID {document.IdText}");
        }

        WriteAscii(output, $" /Prev {document.PreviousXrefOffset} >>\nstartxref\n{xrefOffset}\n%%EOF\n");
    }

    private static IEnumerable<List<PdfRef>> ConsecutiveGroups(IEnumerable<PdfRef> refs)
    {
        var group = new List<PdfRef>();
        foreach (var reference in refs.OrderBy(item => item.Number).ThenBy(item => item.Generation))
        {
            if (group.Count == 0 || reference.Number == group[^1].Number + 1)
            {
                group.Add(reference);
                continue;
            }

            yield return group;
            group = [reference];
        }

        if (group.Count > 0)
        {
            yield return group;
        }
    }

    private static byte[] StreamObject(string dictionaryStart, byte[] data)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, $"{dictionaryStart} /Length {data.Length} >>\nstream\n");
        stream.Write(data);
        WriteAscii(stream, "\nendstream");
        return stream.ToArray();
    }

    private static byte[] Deflate(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(bytes);
        }

        return output.ToArray();
    }

    private static DecodedImage DecodePng(byte[] pngBytes)
    {
        using var input = new MemoryStream(pngBytes, writable: false);
        var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0)
        {
            throw new InvalidOperationException(UnreadableItem);
        }

        BitmapSource source = decoder.Frames[0];
        if (source.Format != PixelFormats.Bgra32)
        {
            source = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            source.Freeze();
        }

        var width = source.PixelWidth;
        var height = source.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > MaxImagePixels)
        {
            throw new InvalidOperationException(UnreadableItem);
        }

        var stride = width * 4;
        var bgra = new byte[stride * height];
        source.CopyPixels(bgra, stride, 0);
        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var hasAlpha = false;
        for (var src = 0; src < bgra.Length; src += 4)
        {
            var pixel = src / 4;
            rgb[pixel * 3] = bgra[src + 2];
            rgb[pixel * 3 + 1] = bgra[src + 1];
            rgb[pixel * 3 + 2] = bgra[src];
            alpha[pixel] = bgra[src + 3];
            hasAlpha |= bgra[src + 3] != 255;
        }

        return new DecodedImage(width, height, rgb, alpha, hasAlpha);
    }

    private static string PdfRect(PdfPageRect rect) =>
        $"[{Format(rect.Left)} {Format(rect.Bottom)} {Format(rect.Right)} {Format(rect.Top)}]";

    /// <summary>
    /// A string shown with the WinAnsi Helvetica font: each character is its
    /// WinAnsi byte, so a euro sign or a curly quote prints as itself.
    /// The surface only accepts characters that have one.
    /// </summary>
    private static string WinAnsiLiteral(string value)
    {
        var builder = new StringBuilder("(");
        foreach (var ch in value)
        {
            if (!SignText.TryEncode(ch, out var code))
            {
                continue;
            }

            if (code is (byte)'\\' or (byte)'(' or (byte)')')
            {
                builder.Append('\\');
            }

            builder.Append((char)code);
        }

        return builder.Append(')').ToString();
    }

    /// <summary>A text string for metadata such as /Contents: UTF-16 when it is not plain ASCII.</summary>
    private static string PdfTextString(string value)
    {
        if (value.All(ch => ch is >= ' ' and <= '~'))
        {
            return "(" + value.Replace(@"\", @"\\").Replace("(", @"\(").Replace(")", @"\)") + ")";
        }

        return "<FEFF" + Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(value)) + ">";
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static byte[] AsLatin1(string text) => Encoding.Latin1.GetBytes(text);

    private static void WriteAscii(Stream stream, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        stream.Write(bytes);
    }

    private static int FindLiteralEnd(string text, int start)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\\')
            {
                i++;
                continue;
            }

            if (ch == '(')
            {
                depth++;
            }
            else if (ch == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i + 1;
                }
            }
        }

        return -1;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A failed cleanup should not hide the original write error.
        }
    }

    private sealed record DecodedImage(int Width, int Height, byte[] RgbBytes, byte[] AlphaBytes, bool HasAlpha);

    private readonly record struct PdfTextRange(int Start, int End);

    private readonly record struct PdfRef(int Number, int Generation)
    {
        public override string ToString() => $"{Number} {Generation}";
    }

    private sealed class PdfRefComparer : IComparer<PdfRef>
    {
        public static readonly PdfRefComparer Instance = new();

        public int Compare(PdfRef left, PdfRef right)
        {
            var number = left.Number.CompareTo(right.Number);
            return number != 0 ? number : left.Generation.CompareTo(right.Generation);
        }
    }

    private sealed record PdfWriteObject(PdfRef Ref, byte[] Body);

    private sealed record PdfObject(PdfRef Ref, string Body, int Position);

    private sealed record PdfPageInfo(PdfRef Ref, string Body, PdfPageBoxes Boxes, int Rotate)
    {
        public PdfPageBox Box => Boxes.EffectiveBox!.Value;
    }

    private readonly record struct PdfPageBoxes(
        PdfPageBox? MediaBox,
        PdfPageBox? CropBox,
        PdfPageBox? BleedBox,
        PdfPageBox? TrimBox,
        PdfPageBox? ArtBox)
    {
        public PdfPageBox? EffectiveBox => CropBox ?? MediaBox;
    }

    private readonly record struct PdfPageBox(double X1, double Y1, double X2, double Y2)
    {
        public double Left => Math.Min(X1, X2);

        public double Bottom => Math.Min(Y1, Y2);

        public double Right => Math.Max(X1, X2);

        public double Top => Math.Max(Y1, Y2);

        public double Width => Right - Left;

        public double Height => Top - Bottom;

        public bool IsValid =>
            double.IsFinite(Width)
            && double.IsFinite(Height)
            && Width >= 1
            && Height >= 1
            && Width <= 20000
            && Height <= 20000;
    }

    private sealed class PdfDocumentModel
    {
        private PdfDocumentModel(
            byte[] bytes,
            Dictionary<PdfRef, PdfObject> objects,
            PdfRef root,
            PdfRef? info,
            string? idText,
            long previousXrefOffset,
            int maxObjectNumber,
            List<PdfPageInfo> pages)
        {
            Bytes = bytes;
            Objects = objects;
            Root = root;
            Info = info;
            IdText = idText;
            PreviousXrefOffset = previousXrefOffset;
            MaxObjectNumber = maxObjectNumber;
            Pages = pages;
        }

        public byte[] Bytes { get; }

        public Dictionary<PdfRef, PdfObject> Objects { get; }

        public PdfRef Root { get; }

        public PdfRef? Info { get; }

        public string? IdText { get; }

        public long PreviousXrefOffset { get; }

        public int MaxObjectNumber { get; }

        public List<PdfPageInfo> Pages { get; }

        public static PdfDocumentModel Read(string path)
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaxPdfBytes)
            {
                throw new InvalidOperationException("This PDF is too large to sign here.");
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || !Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("%PDF-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This file isn't a PDF that can be signed here.");
            }

            if (FindName(bytes, "Encrypt") is not null)
            {
                throw new InvalidOperationException("This PDF is encrypted, so it can't be signed here.");
            }

            var text = Encoding.Latin1.GetString(bytes);
            var previousXref = ReadPreviousXrefOffset(text);
            var trailer = ReadTrailerStart(text, previousXref);
            if (!TryReadNamedValue(text, trailer, "Root", out var rootText, out _)
                || !TryParseReference(rootText, out var root))
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            PdfRef? info = TryReadNamedValue(text, trailer, "Info", out var infoText, out _)
                && TryParseReference(infoText, out var infoRef)
                    ? infoRef
                    : null;
            var idText = TryReadNamedValue(text, trailer, "ID", out var id, out _) ? id : null;
            var trailerSize = TryReadNamedValue(text, trailer, "Size", out var sizeText, out _)
                && int.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var size)
                    ? size
                    : 0;
            var objects = ReadObjects(text);

            // Ink is a change a no-changes certification forbids: the saved
            // copy would carry a broken certificate.
            if (FindName(bytes, "DocMDP") is not null && ReadCertificationPermission(objects, root) is not 3)
            {
                throw new InvalidOperationException("This PDF is certified against changes, so it can't be signed here.");
            }

            var objectMax = objects.Keys.Select(item => item.Number).DefaultIfEmpty(0).Max();
            var maxObjectNumber = Math.Max(objectMax, trailerSize - 1);
            var pages = ReadPages(objects, root);
            if (pages.Count == 0)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            return new PdfDocumentModel(
                bytes,
                objects,
                root,
                info,
                idText,
                previousXref,
                maxObjectNumber,
                pages);
        }

        /// <summary>
        /// Where the newest xref section's dictionary starts: the trailer
        /// after an xref table, or the xref stream's own. Root and Size are
        /// read there alone, as the viewer reads them.
        /// </summary>
        private static int ReadTrailerStart(string text, long xrefOffset)
        {
            var at = xrefOffset > 0 && xrefOffset < text.Length ? SkipSpace(text, (int)xrefOffset) : -1;
            if (At(text, at, "xref"))
            {
                var trailer = text.IndexOf("trailer", at, StringComparison.Ordinal);
                if (trailer >= 0)
                {
                    return trailer + "trailer".Length;
                }
            }
            else if (at >= 0 && ObjectHeaderRegex.Match(text, at) is { Success: true } header)
            {
                var dictionary = header.Index + header.Length;
                if (TryReadNamedValue(text, dictionary, "Type", out var type, out _) && NameValue(type) == "XRef")
                {
                    return dictionary;
                }
            }

            // A startxref off its mark (a damaged file, or bytes before the
            // header) is one every viewer repairs from the last trailer, so
            // the writer reads that one, as it always has.
            var last = text.LastIndexOf("trailer", StringComparison.Ordinal);
            if (last >= 0 && At(text, SkipSpace(text, last + "trailer".Length), "<<"))
            {
                return last + "trailer".Length;
            }

            throw new InvalidOperationException(UnsupportedStructure);
        }

        /// <summary>
        /// The certification's /P, found as a viewer finds it: catalog
        /// /Perms /DocMDP, then the signature's DocMDP reference. Unset is 2.
        /// Null when a step is out of reach; only a /P read as 3 allows ink.
        /// </summary>
        private static int? ReadCertificationPermission(Dictionary<PdfRef, PdfObject> objects, PdfRef root)
        {
            var catalog = objects.TryGetValue(root, out var rootObject) ? rootObject.Body : null;
            var signature = Resolve(objects, ValueOf(Resolve(objects, ValueOf(catalog, "Perms")), "DocMDP"));
            foreach (var item in ArrayItems(Resolve(objects, ValueOf(signature, "Reference"))) ?? [])
            {
                var reference = Resolve(objects, item);
                if (NameValue(ValueOf(reference, "TransformMethod")) != "DocMDP")
                {
                    continue;
                }

                var permission = Resolve(objects, ValueOf(Resolve(objects, ValueOf(reference, "TransformParams")), "P"));
                return permission is null ? 2
                    : int.TryParse(permission, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value
                    : null;
            }

            return null;
        }

        private static Dictionary<PdfRef, PdfObject> ReadObjects(string text)
        {
            var objects = new Dictionary<PdfRef, PdfObject>();
            foreach (Match match in ObjectRegex.Matches(text))
            {
                if (objects.Count > MaxObjects)
                {
                    throw new InvalidOperationException(UnsupportedStructure);
                }

                if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    || !int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
                {
                    continue;
                }

                var obj = new PdfObject(new PdfRef(number, generation), match.Groups["body"].Value, match.Index);
                if (!objects.TryGetValue(obj.Ref, out var prior) || obj.Position > prior.Position)
                {
                    objects[obj.Ref] = obj;
                }
            }

            return objects;
        }

        private static long ReadPreviousXrefOffset(string text)
        {
            var index = text.LastIndexOf("startxref", StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            var match = Regex.Match(
                text[index..],
                @"startxref\s+(?<offset>\d+)",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (!match.Success
                || !long.TryParse(match.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            return offset;
        }

        private static List<PdfPageInfo> ReadPages(Dictionary<PdfRef, PdfObject> objects, PdfRef root)
        {
            if (objects.TryGetValue(root, out var rootObject)
                && TryReadReference(rootObject.Body, "Pages", out var pagesRoot)
                && objects.ContainsKey(pagesRoot))
            {
                var pages = new List<PdfPageInfo>();
                TraversePageTree(objects, pagesRoot, default, null, 0, [], pages);
                return pages;
            }

            return ReadOnlyPage(objects);
        }

        /// <summary>
        /// The page of a file whose page tree is packed out of reach. Without
        /// the tree there is no page order, and only a lone page can't land
        /// on the wrong one.
        /// </summary>
        private static List<PdfPageInfo> ReadOnlyPage(Dictionary<PdfRef, PdfObject> objects)
        {
            var found = objects.Values
                .Where(item => TryReadType(item.Body, out var type) && type == "Page")
                .Take(2)
                .ToList();
            if (found.Count != 1)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            var page = found[0];
            var boxes = ReadPageBoxes(objects, page.Body, default, inheritPageTreeBoxes: false);
            if (boxes.EffectiveBox is not { IsValid: true })
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            var rotate = TryReadRotate(objects, page.Body, out var ownRotate) ? ownRotate : 0;
            return [new PdfPageInfo(page.Ref, page.Body, boxes, rotate)];
        }

        /// <summary>
        /// Every node is read or the file is refused: a node skipped is a
        /// page the viewer counts, and every mark after it lands a page early.
        /// </summary>
        private static void TraversePageTree(
            Dictionary<PdfRef, PdfObject> objects,
            PdfRef reference,
            PdfPageBoxes inheritedBoxes,
            int? inheritedRotate,
            int depth,
            HashSet<PdfRef> seen,
            List<PdfPageInfo> pages)
        {
            if (depth > 64
                || pages.Count >= MaxPages
                || !seen.Add(reference)
                || !objects.TryGetValue(reference, out var obj)
                || !TryReadType(obj.Body, out var type))
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            var boxes = ReadPageBoxes(objects, obj.Body, inheritedBoxes, inheritPageTreeBoxes: type == "Pages");
            var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : inheritedRotate;

            if (type == "Page" && boxes.EffectiveBox is { IsValid: true })
            {
                pages.Add(new PdfPageInfo(reference, obj.Body, boxes, rotate ?? 0));
                return;
            }

            if (type != "Pages" || !TryReadKids(objects, obj.Body, out var kids))
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            foreach (var kid in kids)
            {
                TraversePageTree(objects, kid, boxes, rotate, depth + 1, seen, pages);
            }
        }

        private static PdfPageBoxes ReadPageBoxes(
            Dictionary<PdfRef, PdfObject> objects,
            string body,
            PdfPageBoxes inherited,
            bool inheritPageTreeBoxes)
        {
            PdfPageBox? Read(string name) =>
                TryReadBox(objects, body, name, out var box) ? box : null;

            // MediaBox and CropBox are inheritable page-tree attributes. The
            // print boxes are page entries whose absent value follows CropBox;
            // inheriting a non-standard parent value would change the source.
            return new PdfPageBoxes(
                Read("MediaBox") ?? inherited.MediaBox,
                Read("CropBox") ?? inherited.CropBox,
                inheritPageTreeBoxes ? null : Read("BleedBox"),
                inheritPageTreeBoxes ? null : Read("TrimBox"),
                inheritPageTreeBoxes ? null : Read("ArtBox"));
        }

        private static bool TryReadType(string body, out string type)
        {
            type = NameValue(ValueOf(body, "Type")) ?? string.Empty;
            return type.Length > 0;
        }

        /// <summary>
        /// A box of four numbers, or false when the dictionary names none
        /// usable. Anything else is refused, not guessed at: a misread box
        /// moves every mark.
        /// </summary>
        private static bool TryReadBox(Dictionary<PdfRef, PdfObject> objects, string body, string name, out PdfPageBox box)
        {
            box = default;
            if (ValueOf(body, name) is not { } value)
            {
                return false;
            }

            var items = ArrayItems(Resolve(objects, value));
            if (items is not { Count: 4 })
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            var numbers = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!NumberRegex.IsMatch(items[i])
                    || !double.TryParse(items[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i])
                    || !double.IsFinite(numbers[i]))
                {
                    throw new InvalidOperationException(UnsupportedStructure);
                }
            }

            box = new PdfPageBox(numbers[0], numbers[1], numbers[2], numbers[3]);
            return box.IsValid;
        }

        /// <summary>
        /// The page's turn. The viewer turns by whole quarters, so a value
        /// between them is a turn the writer could only guess.
        /// </summary>
        private static bool TryReadRotate(Dictionary<PdfRef, PdfObject> objects, string body, out int rotate)
        {
            rotate = 0;
            if (ValueOf(body, "Rotate") is not { } value)
            {
                return false;
            }

            if (!int.TryParse(Resolve(objects, value), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out rotate)
                || rotate % 90 != 0)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            return true;
        }

        private static bool TryReadKids(Dictionary<PdfRef, PdfObject> objects, string body, out List<PdfRef> kids)
        {
            kids = [];
            if (ArrayItems(Resolve(objects, ValueOf(body, "Kids"))) is not { } items)
            {
                return false;
            }

            foreach (var item in items)
            {
                if (!TryParseReference(item, out var kid))
                {
                    return false;
                }

                kids.Add(kid);
            }

            return true;
        }

        private static bool TryReadReference(string body, string name, out PdfRef reference)
        {
            reference = default;
            return ValueOf(body, name) is { } value && TryParseReference(value, out reference);
        }
    }
}
