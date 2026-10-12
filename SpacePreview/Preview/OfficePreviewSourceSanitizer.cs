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
/// templates, OLE links, external workbooks) as external relationships, and
/// OpenDocument names them where they are used, as an xlink:href that
/// leaves the package. Both are cut from a private copy before the engine
/// sees the file. Hyperlinks stay: nothing loads them, and the preview
/// keeps them clickable. Files that are not zips (.doc, .xls, .ppt, .rtf)
/// reach the engine as they are, and its profile is what stops their links.
/// </summary>
internal static class OfficePreviewSourceSanitizer
{
    private const long MaxRelationshipsBytes = 4L * 1024 * 1024;
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
            // Legacy binary formats have no package to cut links from.
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
