using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePreview.Preview;

internal enum PdfFillSignPlacementKind
{
    Text,
    Date,
    Signature,
    Stamp
}

internal readonly record struct PdfNormalizedRect(double Left, double Top, double Width, double Height)
{
    public bool IsValid =>
        double.IsFinite(Left)
        && double.IsFinite(Top)
        && double.IsFinite(Width)
        && double.IsFinite(Height)
        && Left >= 0
        && Top >= 0
        && Width > 0
        && Height > 0
        && Left + Width <= 1.0001
        && Top + Height <= 1.0001;
}

internal sealed record PdfFillSignPlacement(
    int PageNumber,
    PdfFillSignPlacementKind Kind,
    PdfNormalizedRect Bounds,
    string? Text = null,
    byte[]? PngBytes = null,
    double FontSize = 12);

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

    public static string SignPdf(string sourcePath, IReadOnlyList<PdfFillSignPlacement> placements)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var outputDirectory = string.IsNullOrWhiteSpace(sourceDirectory) ? Path.GetTempPath() : sourceDirectory;
        var outputPath = Path.Combine(outputDirectory, $"{name}.signed-{Guid.NewGuid():N}.pdf");
        WriteSignedCopy(sourcePath, outputPath, placements);
        return outputPath;
    }

    public static void WriteSignedCopy(string sourcePath, string outputPath, IReadOnlyList<PdfFillSignPlacement> placements)
    {
        if (placements.Count == 0)
        {
            throw new InvalidOperationException("No PDF fill items were provided.");
        }

        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The signed PDF must be written to a different file first.");
        }

        try
        {
            var document = PdfDocumentModel.Read(sourcePath);
            var objects = BuildUpdateObjects(document, placements);
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
    }

    internal static PdfPageRect MapDisplayRectToPageRectForTest(
        PdfNormalizedRect normalized,
        double x1,
        double y1,
        double x2,
        double y2,
        int rotate)
    {
        return MapDisplayRectToPageRect(normalized, new PdfPageBox(x1, y1, x2, y2), rotate);
    }

    private static List<PdfWriteObject> BuildUpdateObjects(PdfDocumentModel document, IReadOnlyList<PdfFillSignPlacement> placements)
    {
        var nextObject = document.MaxObjectNumber + 1;
        var newObjects = new List<PdfWriteObject>();
        var annotationsByPage = new Dictionary<PdfRef, List<PdfRef>>();
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

        foreach (var placement in placements)
        {
            ValidatePlacement(document, placement);
            var page = document.Pages[placement.PageNumber - 1];
            var rect = MapDisplayRectToPageRect(placement.Bounds, page.Box, page.Rotate);
            if (!rect.IsValid)
            {
                throw new InvalidOperationException("The PDF fill item is outside the page.");
            }

            var annotationRef = placement.Kind is PdfFillSignPlacementKind.Text or PdfFillSignPlacementKind.Date
                ? AddTextAnnotation(newObjects, Allocate, EnsureFont(), placement, rect)
                : AddImageAnnotation(newObjects, Allocate, placement, rect);

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

    private static void ValidatePlacement(PdfDocumentModel document, PdfFillSignPlacement placement)
    {
        if (placement.PageNumber < 1 || placement.PageNumber > document.Pages.Count)
        {
            throw new InvalidOperationException("The PDF fill item refers to a page that does not exist.");
        }

        if (!placement.Bounds.IsValid)
        {
            throw new InvalidOperationException("The PDF fill item has invalid bounds.");
        }

        if (placement.Kind is PdfFillSignPlacementKind.Text or PdfFillSignPlacementKind.Date)
        {
            if (string.IsNullOrWhiteSpace(placement.Text))
            {
                throw new InvalidOperationException("The PDF text item is empty.");
            }
        }
        else if (placement.PngBytes is null || placement.PngBytes.Length == 0)
        {
            throw new InvalidOperationException("The PDF image item is empty.");
        }
    }

    private static PdfRef AddTextAnnotation(
        List<PdfWriteObject> objects,
        Func<PdfRef> allocate,
        PdfRef fontRef,
        PdfFillSignPlacement placement,
        PdfPageRect rect)
    {
        var appearanceRef = allocate();
        var annotationRef = allocate();
        var text = placement.Text ?? string.Empty;
        var fontSize = ResolveFontSize(placement, rect);
        var baseline = Math.Max(2, rect.Height - fontSize - 2);
        var stream = AsLatin1(
            $"q\nBT\n/Helv {Format(fontSize)} Tf\n0 0 0 rg\n2 {Format(baseline)} Td\n{PdfLiteral(text)} Tj\nET\nQ\n");
        objects.Add(new PdfWriteObject(
            appearanceRef,
            StreamObject(
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 {Format(rect.Width)} {Format(rect.Height)}] /Resources << /Font << /Helv {fontRef} R >> >>",
                stream)));
        objects.Add(new PdfWriteObject(
            annotationRef,
            AsLatin1(
                "<< /Type /Annot /Subtype /FreeText "
                + $"/Rect {PdfRect(rect)} /Contents {PdfLiteral(text)} /F 4 /Border [0 0 0] "
                + $"/DA {PdfLiteral($"/Helv {Format(fontSize)} Tf 0 0 0 rg")} /AP << /N {appearanceRef} R >> >>")));
        return annotationRef;
    }

    private static PdfRef AddImageAnnotation(
        List<PdfWriteObject> objects,
        Func<PdfRef> allocate,
        PdfFillSignPlacement placement,
        PdfPageRect rect)
    {
        var image = DecodePng(placement.PngBytes!);
        var alphaRef = image.HasAlpha ? allocate() : (PdfRef?)null;
        var imageRef = allocate();
        var appearanceRef = allocate();
        var annotationRef = allocate();

        if (alphaRef is { } maskRef)
        {
            objects.Add(new PdfWriteObject(
                maskRef,
                ImageObject(image.Width, image.Height, "/DeviceGray", image.AlphaBytes, null)));
        }

        objects.Add(new PdfWriteObject(
            imageRef,
            ImageObject(image.Width, image.Height, "/DeviceRGB", image.RgbBytes, alphaRef)));
        var formStream = AsLatin1($"q\n{Format(rect.Width)} 0 0 {Format(rect.Height)} 0 0 cm\n/Im1 Do\nQ\n");
        objects.Add(new PdfWriteObject(
            appearanceRef,
            StreamObject(
                $"<< /Type /XObject /Subtype /Form /BBox [0 0 {Format(rect.Width)} {Format(rect.Height)}] /Resources << /XObject << /Im1 {imageRef} R >> >>",
                formStream)));

        var label = placement.Kind == PdfFillSignPlacementKind.Signature ? "Signature" : "Stamp";
        objects.Add(new PdfWriteObject(
            annotationRef,
            AsLatin1(
                "<< /Type /Annot /Subtype /Stamp "
                + $"/Rect {PdfRect(rect)} /Contents {PdfLiteral(label)} /F 4 /Border [0 0 0] "
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

        throw new InvalidOperationException("This PDF stores annotations in a shape Glimpse cannot safely update yet.");
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

    private static PdfPageRect MapDisplayRectToPageRect(PdfNormalizedRect normalized, PdfPageBox box, int rotate)
    {
        var normalizedRotate = NormalizeRotation(rotate);
        var displayWidth = normalizedRotate is 90 or 270 ? box.Height : box.Width;
        var displayHeight = normalizedRotate is 90 or 270 ? box.Width : box.Height;
        var sx = normalized.Left * displayWidth;
        var top = normalized.Top * displayHeight;
        var sw = normalized.Width * displayWidth;
        var sh = normalized.Height * displayHeight;
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
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
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
            throw new InvalidOperationException("The PDF image item could not be read.");
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
            throw new InvalidOperationException("The PDF image item is too large.");
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

    private static double ResolveFontSize(PdfFillSignPlacement placement, PdfPageRect rect)
    {
        if (double.IsFinite(placement.FontSize) && placement.FontSize > 0)
        {
            return Math.Clamp(placement.FontSize, 6, 72);
        }

        return Math.Clamp(rect.Height * 0.72, 6, 72);
    }

    private static string PdfLiteral(string value)
    {
        var builder = new StringBuilder("(");
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '(':
                    builder.Append(@"\(");
                    break;
                case ')':
                    builder.Append(@"\)");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                default:
                    builder.Append(ch <= 255 ? ch : '?');
                    break;
            }
        }

        builder.Append(')');
        return builder.ToString();
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

    private sealed record PdfPageInfo(PdfRef Ref, string Body, PdfPageBox Box, int Rotate);

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
                throw new InvalidOperationException("This PDF is too large to fill safely in Glimpse.");
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 8 || !Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 1024)).Contains("%PDF-", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("This file is not a PDF.");
            }

            var text = Encoding.Latin1.GetString(bytes);
            if (text.Contains("/Encrypt", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Encrypted PDFs cannot be filled in Glimpse yet.");
            }

            var previousXref = ReadPreviousXrefOffset(text);
            var objects = ReadObjects(text);
            if (!TryReadLastReference(text, "Root", out var root))
            {
                throw new InvalidOperationException("This PDF has no readable document root.");
            }

            TryReadLastReference(text, "Info", out var info);
            var idText = TryReadLastId(text, out var foundId) ? foundId : null;
            var objectMax = objects.Keys.Select(item => item.Number).DefaultIfEmpty(0).Max();
            var trailerMax = TryReadLastSize(text, out var trailerSize) ? Math.Max(0, trailerSize - 1) : 0;
            var maxObjectNumber = Math.Max(objectMax, trailerMax);
            var pages = ReadPages(objects, root);
            if (pages.Count == 0)
            {
                throw new InvalidOperationException("This PDF has no readable pages.");
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
                    throw new InvalidOperationException("This PDF has too many objects to update safely.");
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
                throw new InvalidOperationException("This PDF has no readable xref pointer.");
            }

            var match = Regex.Match(
                text[index..],
                @"startxref\s+(?<offset>\d+)",
                RegexOptions.CultureInvariant,
                RegexTimeout);
            if (!match.Success
                || !long.TryParse(match.Groups["offset"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
            {
                throw new InvalidOperationException("This PDF has no readable xref pointer.");
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
            TraversePageTree(objects, pagesRoot, null, null, 0, [], pages);
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

                var box = TryReadBox(objects, obj.Body, "CropBox", out var crop)
                    ? crop
                    : TryReadBox(objects, obj.Body, "MediaBox", out var media)
                        ? media
                        : default;
                if (!box.IsValid)
                {
                    continue;
                }

                var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : 0;
                pages.Add(new PdfPageInfo(obj.Ref, obj.Body, box, rotate));
            }

            return pages;
        }

        private static void TraversePageTree(
            Dictionary<PdfRef, PdfObject> objects,
            PdfRef reference,
            PdfPageBox? inheritedBox,
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

            var box = TryReadBox(objects, obj.Body, "CropBox", out var crop)
                ? crop
                : TryReadBox(objects, obj.Body, "MediaBox", out var media)
                    ? media
                    : inheritedBox;
            var rotate = TryReadRotate(objects, obj.Body, out var ownRotate) ? ownRotate : inheritedRotate;

            if (type == "Page")
            {
                if (box is { IsValid: true } finalBox)
                {
                    pages.Add(new PdfPageInfo(reference, obj.Body, finalBox, rotate ?? 0));
                }

                return;
            }

            if (type != "Pages" || !TryReadKids(obj.Body, out var kids))
            {
                return;
            }

            foreach (var kid in kids)
            {
                TraversePageTree(objects, kid, box, rotate, depth + 1, seen, pages);
            }
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
