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
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(750);

    private static readonly Regex ObjectRegex = new(
        @"(?ms)(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+obj\b(?<body>.*?)\bendobj\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex TypeRegex = new(
        @"/Type\s*/(?<type>Catalog|Pages|Page)(?![A-Za-z0-9])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex NumberRegex = new(
        @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)",
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
                ReadExistingAnnotationEntries(document, Header(page.Body));
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
        if (!TryReadNamedValue(Header(pageBody), "Contents", out var value, out _))
        {
            return $"[{open} R {close} R]";
        }

        var existing = value.Trim();
        if (TryParseReference(existing, out var reference))
        {
            if (!document.Objects.TryGetValue(reference, out var target))
            {
                return null;
            }

            var body = target.Body.Trim();
            if (body.StartsWith('['))
            {
                var end = FindMatching(body, 0, '[', ']');
                if (end < 0)
                {
                    return null;
                }

                existing = body[1..end];
            }
            else if (!body.Contains("stream", StringComparison.Ordinal))
            {
                // Neither a stream nor an array: not something to splice.
                return null;
            }
        }
        else if (existing.StartsWith('['))
        {
            var end = FindMatching(existing, 0, '[', ']');
            if (end < 0)
            {
                return null;
            }

            existing = existing[1..end];
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

    private static string? FindUnsupportedScaleFeature(ReadOnlySpan<byte> bytes)
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

            if (length <= decoded.Length)
            {
                var name = decoded[..length];
                if (name.SequenceEqual("PatternType"u8))
                {
                    return "PatternType";
                }

                if (name.SequenceEqual("ShadingType"u8))
                {
                    return "ShadingType";
                }

                if (name.SequenceEqual("Annots"u8))
                {
                    return "Annots";
                }

                if (name.SequenceEqual("ObjStm"u8))
                {
                    return "ObjStm";
                }

                if (name.SequenceEqual("UserUnit"u8))
                {
                    return "UserUnit";
                }

                if (name.SequenceEqual("ByteRange"u8))
                {
                    return "ByteRange";
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
        var header = Header(page.Body);
        var oldEntries = ReadExistingAnnotationEntries(document, header);
        var added = string.Join(" ", newAnnotations.Select(item => $"{item} R"));
        var combined = string.IsNullOrWhiteSpace(oldEntries)
            ? added
            : $"{oldEntries.Trim()} {added}";
        return ReplaceOrAddDictionaryValue(page.Body, "Annots", $"[{combined}]");
    }

    private static string ReadExistingAnnotationEntries(PdfDocumentModel document, string header)
    {
        if (!TryReadNamedValue(header, "Annots", out var value, out _))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal) && trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return trimmed[1..^1].Trim();
        }

        if (TryParseReference(trimmed, out var reference)
            && document.Objects.TryGetValue(reference, out var obj))
        {
            var body = Header(obj.Body).Trim();
            if (body.StartsWith("[", StringComparison.Ordinal) && body.EndsWith("]", StringComparison.Ordinal))
            {
                return body[1..^1].Trim();
            }
        }

        throw new InvalidOperationException(UnsupportedStructure);
    }

    private static string ReplaceOrAddDictionaryValue(string body, string name, string replacementValue)
    {
        var header = Header(body);
        if (TryReadNamedValue(header, name, out _, out var range))
        {
            return body[..range.Start] + $"/{name} {replacementValue}" + body[range.End..];
        }

        var close = FindOuterDictionaryClose(header);
        if (close < 0)
        {
            throw new InvalidOperationException("This PDF page dictionary could not be updated safely.");
        }

        return body[..close] + $" /{name} {replacementValue} " + body[close..];
    }

    private static bool TryReadNamedValue(string text, string name, out string value, out PdfTextRange range)
    {
        value = string.Empty;
        range = default;
        var token = "/" + name;
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var afterName = index + token.Length;
            if (afterName < text.Length && IsNameChar(text[afterName]))
            {
                index = text.IndexOf(token, afterName, StringComparison.Ordinal);
                continue;
            }

            var valueStart = SkipWhite(text, afterName);
            if (valueStart >= text.Length)
            {
                return false;
            }

            var valueEnd = ReadValueEnd(text, valueStart);
            if (valueEnd <= valueStart)
            {
                return false;
            }

            value = text[valueStart..valueEnd];
            range = new PdfTextRange(index, valueEnd);
            return true;
        }

        return false;
    }

    private static int ReadValueEnd(string text, int start)
    {
        return text[start] switch
        {
            '[' => FindMatching(text, start, '[', ']') + 1,
            '<' when start + 1 < text.Length && text[start + 1] == '<' => FindMatchingDictionary(text, start) + 2,
            '(' => FindLiteralEnd(text, start),
            _ => ReadScalarValueEnd(text, start)
        };
    }

    private static int ReadScalarValueEnd(string text, int start)
    {
        var reference = Regex.Match(
            text[start..Math.Min(text.Length, start + 64)],
            @"^\s*\d{1,10}\s+\d{1,5}\s+R(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant,
            RegexTimeout);
        if (reference.Success)
        {
            return start + reference.Length;
        }

        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '/' && text[index] != '>' && text[index] != ']')
        {
            index++;
        }

        return index;
    }

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

    private static string Header(string body)
    {
        var streamIndex = body.IndexOf("stream", StringComparison.Ordinal);
        return streamIndex >= 0 ? body[..streamIndex] : body;
    }

    private static bool IsNameChar(char ch) =>
        char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.';

    private static int SkipWhite(string text, int start)
    {
        while (start < text.Length && char.IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start;
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

    private static int FindMatching(string text, int start, char open, char close)
    {
        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                i = Math.Max(i, FindLiteralEnd(text, i) - 1);
                continue;
            }

            if (text[i] == open)
            {
                depth++;
            }
            else if (text[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static int FindMatchingDictionary(string text, int start)
    {
        var depth = 0;
        for (var i = start; i + 1 < text.Length; i++)
        {
            if (text[i] == '(')
            {
                i = Math.Max(i, FindLiteralEnd(text, i) - 1);
                continue;
            }

            if (text[i] == '<' && text[i + 1] == '<')
            {
                depth++;
                i++;
            }
            else if (text[i] == '>' && text[i + 1] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }

                i++;
            }
        }

        return -1;
    }

    private static int FindOuterDictionaryClose(string text)
    {
        var open = text.IndexOf("<<", StringComparison.Ordinal);
        return open < 0 ? -1 : FindMatchingDictionary(text, open);
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

            var text = Encoding.Latin1.GetString(bytes);
            if (text.Contains("/Encrypt", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This PDF is encrypted, so it can't be signed here.");
            }

            var previousXref = ReadPreviousXrefOffset(text);
            var objects = ReadObjects(text);
            if (!TryReadLastReference(text, "Root", out var root))
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            TryReadLastReference(text, "Info", out var info);
            var idText = TryReadLastId(text, out var foundId) ? foundId : null;
            var objectMax = objects.Keys.Select(item => item.Number).DefaultIfEmpty(0).Max();
            var trailerMax = TryReadLastSize(text, out var trailerSize) ? Math.Max(0, trailerSize - 1) : 0;
            var maxObjectNumber = Math.Max(objectMax, trailerMax);
            var pages = ReadPages(objects, root);
            if (pages.Count == 0)
            {
                throw new InvalidOperationException(UnsupportedStructure);
            }

            return new PdfDocumentModel(
                bytes,
                objects,
                root,
                info.Equals(default(PdfRef)) ? null : info,
                idText,
                previousXref,
                maxObjectNumber,
                pages);
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

        private static bool TryReadLastReference(string text, string name, out PdfRef reference)
        {
            reference = default;
            var matches = Regex.Matches(
                text,
                $@"/{Regex.Escape(name)}\s+(?<number>\d{{1,10}})\s+(?<generation>\d{{1,5}})\s+R(?![A-Za-z0-9])",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (matches.Count == 0)
            {
                return false;
            }

            var match = matches[^1];
            if (!int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                || !int.TryParse(match.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            {
                return false;
            }

            reference = new PdfRef(number, generation);
            return true;
        }

        private static bool TryReadLastId(string text, out string idText)
        {
            idText = string.Empty;
            var matches = Regex.Matches(
                text,
                @"/ID\s*(?<id>\[(?:\s*<[^>]{1,256}>\s*){1,2}\])",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (matches.Count == 0)
            {
                return false;
            }

            idText = matches[^1].Groups["id"].Value;
            return true;
        }

        private static bool TryReadLastSize(string text, out int size)
        {
            size = 0;
            var matches = Regex.Matches(
                text,
                @"/Size\s+(?<size>\d{1,10})(?![.\d])",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            return matches.Count > 0
                && int.TryParse(matches[^1].Groups["size"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out size)
                && size > 0;
        }

        private static List<PdfPageInfo> ReadPages(Dictionary<PdfRef, PdfObject> objects, PdfRef root)
        {
            if (!objects.TryGetValue(root, out var rootObject)
                || !TryReadReference(rootObject.Body, "Pages", out var pagesRoot))
            {
                return ReadPagesByObjectOrder(objects);
            }

            var pages = new List<PdfPageInfo>();
            TraversePageTree(objects, pagesRoot, default, null, 0, [], pages);
            return pages.Count > 0 ? pages : ReadPagesByObjectOrder(objects);
        }

        private static List<PdfPageInfo> ReadPagesByObjectOrder(Dictionary<PdfRef, PdfObject> objects)
        {
            var pages = new List<PdfPageInfo>();
            foreach (var obj in objects.Values.OrderBy(item => item.Position))
            {
                if (pages.Count >= MaxPages)
                {
                    break;
                }

                if (!TryReadType(obj.Body, out var type) || type != "Page")
                {
                    continue;
                }

                var boxes = ReadPageBoxes(objects, obj.Body, default, inheritPageTreeBoxes: false);
                if (boxes.EffectiveBox is not { IsValid: true })
                {
                    continue;
                }

                var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : 0;
                pages.Add(new PdfPageInfo(obj.Ref, obj.Body, boxes, rotate));
            }

            return pages;
        }

        private static void TraversePageTree(
            Dictionary<PdfRef, PdfObject> objects,
            PdfRef reference,
            PdfPageBoxes inheritedBoxes,
            int? inheritedRotate,
            int depth,
            HashSet<PdfRef> seen,
            List<PdfPageInfo> pages)
        {
            if (depth > 64 || pages.Count >= MaxPages || !seen.Add(reference) || !objects.TryGetValue(reference, out var obj))
            {
                return;
            }

            if (!TryReadType(obj.Body, out var type))
            {
                return;
            }

            var boxes = ReadPageBoxes(objects, obj.Body, inheritedBoxes, inheritPageTreeBoxes: type == "Pages");
            var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : inheritedRotate;

            if (type == "Page")
            {
                if (boxes.EffectiveBox is { IsValid: true })
                {
                    pages.Add(new PdfPageInfo(reference, obj.Body, boxes, rotate ?? 0));
                }

                return;
            }

            if (type != "Pages" || !TryReadKids(obj.Body, out var kids))
            {
                return;
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
            type = string.Empty;
            var match = TypeRegex.Match(Header(body));
            if (!match.Success)
            {
                return false;
            }

            type = match.Groups["type"].Value;
            return true;
        }

        private static bool TryReadBox(Dictionary<PdfRef, PdfObject> objects, string body, string name, out PdfPageBox box)
        {
            box = default;
            var header = Header(body);
            var direct = Regex.Match(
                header,
                $@"/{Regex.Escape(name)}\s*\[(?<values>[^\]]{{1,512}})\]",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (direct.Success)
            {
                return TryParseBox(direct.Groups["values"].Value, out box);
            }

            if (TryReadReference(header, name, out var boxRef)
                && objects.TryGetValue(boxRef, out var boxObject))
            {
                var bracket = Regex.Match(
                    Header(boxObject.Body),
                    @"\[(?<values>[^\]]{1,512})\]",
                    RegexOptions.CultureInvariant,
                    RegexTimeout);
                return bracket.Success && TryParseBox(bracket.Groups["values"].Value, out box);
            }

            return false;
        }

        private static bool TryReadRotate(Dictionary<PdfRef, PdfObject> objects, string body, out int rotate)
        {
            rotate = 0;
            var header = Header(body);
            var direct = Regex.Match(
                header,
                @"/Rotate\s+(?<value>[-+]?\d{1,5})(?![.\d])",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (direct.Success
                && int.TryParse(direct.Groups["value"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate))
            {
                return true;
            }

            if (TryReadReference(header, "Rotate", out var rotateRef)
                && objects.TryGetValue(rotateRef, out var rotateObject))
            {
                var value = Regex.Match(
                    Header(rotateObject.Body),
                    @"[-+]?\d{1,5}",
                    RegexOptions.CultureInvariant,
                    RegexTimeout);
                return value.Success
                    && int.TryParse(value.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out rotate);
            }

            return false;
        }

        private static bool TryReadKids(string body, out IReadOnlyList<PdfRef> kids)
        {
            kids = [];
            var match = Regex.Match(
                Header(body),
                @"/Kids\s*\[(?<kids>[^\]]{1,65536})\]",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (!match.Success)
            {
                return false;
            }

            var matches = Regex.Matches(
                match.Groups["kids"].Value,
                @"(?<number>\d{1,10})\s+(?<generation>\d{1,5})\s+R(?![A-Za-z0-9])",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            var refs = new List<PdfRef>(matches.Count);
            foreach (Match item in matches)
            {
                if (int.TryParse(item.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                    && int.TryParse(item.Groups["generation"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
                {
                    refs.Add(new PdfRef(number, generation));
                }
            }

            kids = refs;
            return refs.Count > 0;
        }

        private static bool TryReadReference(string body, string name, out PdfRef reference)
        {
            reference = default;
            var match = Regex.Match(
                body,
                $@"/{Regex.Escape(name)}\s+(?<number>\d{{1,10}})\s+(?<generation>\d{{1,5}})\s+R(?![A-Za-z0-9])",
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

        private static bool TryParseBox(string text, out PdfPageBox box)
        {
            box = default;
            var numbers = NumberRegex.Matches(text);
            if (numbers.Count < 4)
            {
                return false;
            }

            var values = new double[4];
            for (var i = 0; i < 4; i++)
            {
                if (!double.TryParse(numbers[i].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out values[i])
                    || !double.IsFinite(values[i]))
                {
                    return false;
                }
            }

            box = new PdfPageBox(values[0], values[1], values[2], values[3]);
            return box.IsValid;
        }
    }
}
