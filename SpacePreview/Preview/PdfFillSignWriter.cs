using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Services;

namespace SpacePreview.Preview;

/// <summary>
/// What sign mode learns before the user places anything: the page count
/// the viewer shows, or the sentence that explains why this file cannot be
/// saved.
/// </summary>
internal readonly record struct PdfSignCheck(int PageCount, string? Problem);

/// <summary>
/// A sentence for the user: why this PDF cannot be signed, or why this save
/// did not happen. Its message is shown as it is, so nothing but such a
/// sentence is ever thrown as one.
/// </summary>
internal sealed class SignRefusalException(string sentence) : Exception(sentence);

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

/// <summary>
/// Signs by appending: the file's own bytes are copied and an incremental
/// update follows them. The file is read through <see cref="PdfReader"/>,
/// the reader the page-shape probe uses, which finds pages the way the
/// viewer does, so the ink goes on the page the sign surface showed. A file
/// that can be read more than one way is refused. The source is streamed,
/// never held whole.
/// </summary>
internal static class PdfFillSignWriter
{
    private const int MaxPages = 3000;
    private const int MaxObjects = 200000;
    private const int MaxImagePixels = 24_000_000;
    private const long MaxPdfBytes = 256L * 1024 * 1024;

    // Every reason a PDF cannot be signed is said in one of these sentences.
    // They reach the user unchanged, so they name no app and no parser.
    internal const string UnsupportedStructure = "This PDF is built in a way that can't be signed here yet.";
    internal const string UnreadableItem = "Something placed on the page couldn't be written. Nothing was saved.";

    // A page-level scale moves page content, not every PDF coordinate
    // system or interactive object. A file that names any of these stays on
    // the original viewer path.
    private static readonly string[] UnscalableFeatures = ["PatternType", "ShadingType", "Annots", "ObjStm", "UserUnit", "ByteRange"];

    /// <summary>
    /// Reads the document exactly as a save would, without writing. Run on
    /// entry, so a file the writer cannot update says so before any work is
    /// placed on it instead of after.
    /// </summary>
    public static PdfSignCheck Preflight(string path)
    {
        try
        {
            using var document = PdfSignDocument.Open(path);
            return new PdfSignCheck(document.PageCount, null);
        }
        catch (SignRefusalException ex)
        {
            return new PdfSignCheck(0, ex.Message);
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
            throw new SignRefusalException(UnreadableItem);
        }

        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
        {
            throw new SignRefusalException("The signed PDF must be written to a different file first.");
        }

        var created = false;
        try
        {
            // The source stays open, closed to writers, from the read to the
            // copy: the bytes the pages were found in are the bytes copied.
            using var document = PdfSignDocument.Open(sourcePath);
            var objects = BuildUpdateObjects(document, placements);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            // A file already at this name is someone else's: never written over.
            using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            created = true;
            document.CopyTo(output);
            WriteIncrementalUpdate(output, document, objects);

            // On the disk before the caller renames it over the original:
            // a power cut must not leave that name on an empty file.
            output.Flush(flushToDisk: true);
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
        using var document = PdfSignDocument.Open(sourcePath, UnscalableFeatures);
        if (document.Scan.FirstOf(UnscalableFeatures) is { } unsupported)
        {
            DiagnosticLog.Write($"pdf scale skipped unsupported /{unsupported}");
            return false;
        }

        var objects = new List<PdfWriteObject>();
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
            var open = document.Allocate();
            var close = document.Allocate();
            objects.Add(new PdfWriteObject(open, StreamObject("<<", AsLatin1($"q {scaleText} 0 0 {scaleText} 0 0 cm\n"))));
            objects.Add(new PdfWriteObject(close, StreamObject("<<", AsLatin1("\nQ"))));

            if (WrapPageContents(document, page.Dictionary, open, close) is not { } contents
                || ScalePageBoxes(document, page, page.Dictionary.With("Contents", contents), scale) is not { } body)
            {
                return false;
            }

            objects.Add(new PdfWriteObject(page.Ref, body));
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
            document.CopyTo(output);
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
    private static string? WrapPageContents(PdfSignDocument document, PdfDictionary page, PdfRef open, PdfRef close)
    {
        if (!page.Has("Contents"))
        {
            return $"[{open} R {close} R]";
        }

        var existing = page.Get("Contents") switch
        {
            PdfArray direct => string.Join(" ", direct.Raw()),
            PdfRef reference => document.Reader.Read(reference.Number) switch
            {
                PdfArray referenced => string.Join(" ", referenced.Raw()),
                PdfStream => $"{reference} R",

                // Neither a stream nor an array: not something to splice.
                _ => null
            },
            _ => null
        };
        return existing is null ? null : $"[{open} R {existing} {close} R]";
    }

    /// <summary>
    /// The page's text with every box it has, or inherits, written at the
    /// new scale. The print boxes are the page's own entries: inheriting a
    /// non-standard parent value would change the source.
    /// </summary>
    private static byte[]? ScalePageBoxes(PdfSignDocument document, PdfPageInfo page, byte[]? body, double scale)
    {
        // With no MediaBox of its own the viewer frames the page as US Letter.
        body = ScalePageBox(body, "MediaBox", page.Frame.MediaBox ?? new PdfBox(0, 0, 612, 792), scale);
        body = ScalePageBox(body, "CropBox", page.Frame.CropBox, scale);
        foreach (var name in new[] { "BleedBox", "TrimBox", "ArtBox" })
        {
            body = ScalePageBox(body, name, document.Reader.BoxOf(page.Dictionary, name, inherited: false), scale);
        }

        return body;
    }

    private static byte[]? ScalePageBox(byte[]? body, string name, PdfBox? box, double scale)
    {
        if (body is null || box is not { } value)
        {
            return body;
        }

        var scaled = $"[{Format(value.Left * scale)} {Format(value.Bottom * scale)} "
            + $"{Format(value.Right * scale)} {Format(value.Top * scale)}]";
        return PdfDictionary.Parse(body)?.With(name, scaled);
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
        return MapDisplayRectToPageRect(left, top, width, height, new PdfBox(x1, y1, x2, y2), rotate);
    }

    private static List<PdfWriteObject> BuildUpdateObjects(PdfSignDocument document, IReadOnlyList<SignPlacement> placements)
    {
        var newObjects = new List<PdfWriteObject>();
        var annotationsByPage = new Dictionary<PdfRef, List<PdfRef>>();
        var imagesByHash = new Dictionary<string, PdfRef>(StringComparer.Ordinal);
        PdfRef? fontRef = null;

        PdfRef Allocate() => document.Allocate();

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
                throw new SignRefusalException(UnreadableItem);
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
            newObjects.Add(new PdfWriteObject(page.Ref, UpdatePageAnnotations(document, page, annotationRefs)));
        }

        return newObjects;
    }

    private static void ValidatePlacement(PdfSignDocument document, SignPlacement placement)
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
            throw new SignRefusalException(UnreadableItem);
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

    private static byte[] UpdatePageAnnotations(PdfSignDocument document, PdfPageInfo page, IReadOnlyList<PdfRef> newAnnotations)
    {
        var oldEntries = ExistingAnnotations(document.Reader, page.Dictionary);
        var added = string.Join(" ", newAnnotations.Select(item => $"{item} R"));
        var combined = string.IsNullOrWhiteSpace(oldEntries)
            ? added
            : $"{oldEntries} {added}";
        return page.Dictionary.With("Annots", $"[{combined}]") ?? throw new SignRefusalException(UnsupportedStructure);
    }

    /// <summary>
    /// The page's annotations as they are written, to be carried into its
    /// new revision. /Annots is an array or a reference to one, named once:
    /// the viewer reads the last of two, and replacing one would leave the
    /// other.
    /// </summary>
    private static string ExistingAnnotations(PdfReader reader, PdfDictionary page)
    {
        if (!page.Has("Annots"))
        {
            return string.Empty;
        }

        return !page.IsRepeated("Annots") && reader.Resolve(page.Get("Annots")) is PdfArray annotations
            ? string.Join(" ", annotations.Raw())
            : throw new SignRefusalException(UnsupportedStructure);
    }

    private static PdfPageRect MapDisplayRectToPageRect(
        double left,
        double topEdge,
        double width,
        double height,
        PdfBox box,
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

    private static void WriteIncrementalUpdate(Stream output, PdfSignDocument document, IReadOnlyList<PdfWriteObject> objects)
    {
        if (output.Length > 0 && output.Position == output.Length)
        {
            WriteAscii(output, "\n");
        }

        // The viewer counts offsets from the file's "%PDF-", which a few
        // files do not start with.
        long Offset() => output.Position - document.Reader.HeaderOffset;

        var offsets = new SortedDictionary<PdfRef, long>(PdfRefComparer.Instance);
        foreach (var obj in objects)
        {
            offsets[obj.Ref] = Offset();
            WriteAscii(output, $"{obj.Ref.Number} {obj.Ref.Generation} obj\n");
            output.Write(obj.Body);
            WriteAscii(output, "\nendobj\n");
        }

        if (document.XrefIsStream)
        {
            WriteCrossReferenceStream(output, document, offsets, Offset());
            return;
        }

        var xrefOffset = Offset();
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

    /// <summary>
    /// The update's index as a cross-reference stream, for a file whose
    /// newest section is one. pdfium does not let a table override an object
    /// an older stream packed away, so ink placed on such a page vanished
    /// in Chrome, Edge and Glimpse itself.
    /// </summary>
    private static void WriteCrossReferenceStream(Stream output, PdfSignDocument document, SortedDictionary<PdfRef, long> offsets, long selfOffset)
    {
        var self = new PdfRef(Math.Max(document.MaxObjectNumber, offsets.Keys.Max(item => item.Number)) + 1, 0);
        offsets[self] = selfOffset;
        var index = new StringBuilder();
        using var entries = new MemoryStream();
        foreach (var group in ConsecutiveGroups(offsets.Keys))
        {
            index.Append(CultureInfo.InvariantCulture, $"{group[0].Number} {group.Count} ");
            foreach (var reference in group)
            {
                // Type 1, an 8-byte offset, a 2-byte generation: /W [1 8 2].
                entries.WriteByte(1);
                for (var shift = 56; shift >= 0; shift -= 8)
                {
                    entries.WriteByte((byte)(offsets[reference] >> shift));
                }

                entries.WriteByte((byte)(reference.Generation >> 8));
                entries.WriteByte((byte)reference.Generation);
            }
        }

        var dictionary = new StringBuilder();
        dictionary.Append(CultureInfo.InvariantCulture, $"<< /Type /XRef /Size {self.Number + 1} /W [1 8 2] /Index [{index.ToString().TrimEnd()}] /Root {document.Root} R");
        if (document.Info is { } info)
        {
            dictionary.Append(CultureInfo.InvariantCulture, $" /Info {info} R");
        }

        if (!string.IsNullOrWhiteSpace(document.IdText))
        {
            dictionary.Append(CultureInfo.InvariantCulture, $" /ID {document.IdText}");
        }

        dictionary.Append(CultureInfo.InvariantCulture, $" /Prev {document.PreviousXrefOffset} /Length {entries.Length} >>");
        WriteAscii(output, $"{self.Number} 0 obj\n{dictionary}\nstream\n");
        output.Write(entries.GetBuffer(), 0, (int)entries.Length);
        WriteAscii(output, $"\nendstream\nendobj\nstartxref\n{offsets[self]}\n%%EOF\n");
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
            throw new SignRefusalException(UnreadableItem);
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
            throw new SignRefusalException(UnreadableItem);
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

    private sealed record PdfPageInfo(PdfRef Ref, PdfDictionary Dictionary, PdfPageFrame Frame)
    {
        public PdfBox Box => Frame.Box;

        public int Rotate => Frame.Quarters * 90;
    }

    /// <summary>
    /// One file, open for the length of a read or a save: what the reader
    /// found in it, and what an update appended to it has to say.
    /// </summary>
    private sealed class PdfSignDocument : IDisposable
    {
        private readonly FileStream _source;
        private int _nextObject;

        private PdfSignDocument(FileStream source, PdfReader reader, PdfFileScan scan)
        {
            _source = source;
            Reader = reader;
            Scan = scan;
        }

        public PdfReader Reader { get; }

        public PdfFileScan Scan { get; }

        public PdfRef Root { get; private set; }

        public PdfRef? Info { get; private set; }

        public string? IdText { get; private set; }

        public long PreviousXrefOffset { get; private set; }

        /// <summary>The newest cross-reference section is a stream, not a table.</summary>
        public bool XrefIsStream { get; private set; }

        public int MaxObjectNumber { get; private set; }

        /// <summary>The pages a mark can go on, in the viewer's order.</summary>
        public List<PdfPageInfo> Pages { get; } = [];

        /// <summary>The count the viewer reports, which a file's /Count can set apart from its pages.</summary>
        public int PageCount { get; private set; }

        /// <summary>A number no object in the file carries, for an object the update adds.</summary>
        public PdfRef Allocate() =>
            _nextObject < PdfReader.MaxObjectNumber
                ? new PdfRef(_nextObject++, 0)
                : throw new SignRefusalException(UnsupportedStructure);

        public void CopyTo(Stream output)
        {
            _source.Seek(0, SeekOrigin.Begin);
            _source.CopyTo(output, 1024 * 1024);
        }

        public void Dispose() => _source.Dispose();

        /// <summary>
        /// Opens the file closed to writers and reads everything a save
        /// needs. <paramref name="names"/> are extra names for the pass over
        /// the whole file to look for.
        /// </summary>
        public static PdfSignDocument Open(string path, params string[] names)
        {
            var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try
            {
                return Read(source, names);
            }
            catch (Exception ex) when (ex is not (SignRefusalException or IOException or UnauthorizedAccessException or OutOfMemoryException))
            {
                // The reader ran out of time, met a file that reads two ways,
                // or tripped on bytes nobody planned for: all one answer.
                source.Dispose();
                DiagnosticLog.Write($"fill-sign read refused: {ex.GetType().Name}");
                throw new SignRefusalException(UnsupportedStructure);
            }
            catch
            {
                source.Dispose();
                throw;
            }
        }

        private static PdfSignDocument Read(FileStream source, string[] names)
        {
            if (source.Length > MaxPdfBytes)
            {
                throw new SignRefusalException("This PDF is too large to sign here.");
            }

            var head = new byte[(int)Math.Min(source.Length, 1024)];
            source.ReadExactly(head);
            if (head.Length < 8 || head.AsSpan().IndexOf("%PDF-"u8) < 0)
            {
                throw new SignRefusalException("This file isn't a PDF that can be signed here.");
            }

            // The index, when the viewer is certain to follow it. Otherwise
            // only what the whole file defines exactly once.
            var reader = PdfReader.ForSigning(source);
            var followed = reader.TryLoadIndex() && reader.TryFollowAsViewer();
            var scan = PdfFileScan.Run(source, keepDefinitions: !followed, [.. names, "Encrypt", "DocMDP"]);
            if (scan.Has("Encrypt"))
            {
                throw new SignRefusalException("This PDF is encrypted, so it can't be signed here.");
            }

            if ((!followed && !reader.ReadOnlyWhatIsDefinedOnce(scan))
                || reader.Root is not { } root
                || reader.StartXref < 0
                || reader.ListedObjects() > MaxObjects
                || scan.Definitions.Count > MaxObjects)
            {
                throw new SignRefusalException(UnsupportedStructure);
            }

            // Ink is a change a no-changes certification forbids: the saved
            // copy would carry a broken certificate.
            if (scan.Has("DocMDP") && CertificationPermission(reader, root) is not 3)
            {
                throw new SignRefusalException("This PDF is certified against changes, so it can't be signed here.");
            }

            var document = new PdfSignDocument(source, reader, scan)
            {
                Root = root,
                Info = reader.TrailerWith("Info")?.Get("Info") as PdfRef?,
                IdText = reader.TrailerWith("ID")?.Raw("ID"),
                PreviousXrefOffset = reader.StartXref,
                XrefIsStream = reader.NewestIsStream,
                MaxObjectNumber = Math.Max(reader.HighestListedNumber(), scan.HighestNumber)
            };
            document._nextObject = document.MaxObjectNumber + 1;
            document.ReadPages();
            return document;
        }

        /// <summary>
        /// Every page is read or the file is refused: a page skipped is a
        /// page the viewer counts, and every mark after it lands a page early.
        /// </summary>
        private void ReadPages()
        {
            if (Reader.PageTree() is not { } tree)
            {
                throw new SignRefusalException(UnsupportedStructure);
            }

            Reader.WalkPages(tree.Number, tree.Node, (number, page) =>
            {
                // A page has to be framed as the viewer frames it, its
                // annotations have to carry over, and the viewer has to take
                // its new revision in place of the old.
                if (Pages.Count >= MaxPages
                    || Reader.FrameOf(page) is not { } frame
                    || frame.Box.Width is < 1 or > 20000
                    || frame.Box.Height is < 1 or > 20000
                    || !Reader.CanBeReplaced(number))
                {
                    throw new SignRefusalException(UnsupportedStructure);
                }

                ExistingAnnotations(Reader, page);
                Pages.Add(new PdfPageInfo(new PdfRef(number, Reader.GenerationOf(number)), page, frame));
                return true;
            });
            if (Pages.Count == 0)
            {
                throw new SignRefusalException(UnsupportedStructure);
            }

            // A /Count below the number of pages hides the rest from the
            // viewer; one above it counts pages that are not there.
            PageCount = Reader.DeclaredPageCount(tree.Node) ?? Pages.Count;
            if (PageCount < Pages.Count)
            {
                Pages.RemoveRange(PageCount, Pages.Count - PageCount);
            }
        }

        /// <summary>
        /// The certification's /P, found as a viewer finds it: catalog
        /// /Perms /DocMDP, then the signature's DocMDP reference. Unset is 2.
        /// Null when a step is out of reach; only a /P read as 3 allows ink.
        /// </summary>
        private static int? CertificationPermission(PdfReader reader, PdfRef root)
        {
            var permissions = reader.Resolve((reader.Read(root.Number) as PdfDictionary)?.Get("Perms")) as PdfDictionary;
            var signature = reader.Resolve(permissions?.Get("DocMDP")) as PdfDictionary;
            foreach (var item in (reader.Resolve(signature?.Get("Reference")) as PdfArray)?.Items ?? [])
            {
                if (reader.Resolve(item) is not PdfDictionary reference
                    || (reference.Get("TransformMethod") as PdfName?)?.Value != "DocMDP")
                {
                    continue;
                }

                var parameters = reader.Resolve(reference.Get("TransformParams")) as PdfDictionary;
                return reader.Resolve(parameters?.Get("P")) switch
                {
                    null => 2,
                    double permission when permission == Math.Floor(permission) && Math.Abs(permission) < int.MaxValue => (int)permission,
                    _ => null
                };
            }

            return null;
        }
    }
}
