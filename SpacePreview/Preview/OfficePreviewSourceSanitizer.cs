using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

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

internal static class OfficePreviewSourceSanitizer
{
    private const long MaxRelationshipsBytes = 4L * 1024 * 1024;
    private const int CopyBufferBytes = 128 * 1024;
    private static readonly XNamespace PackageRelationships =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    private static readonly HashSet<string> OpenXmlExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".docm", ".dotx", ".dotm",
        ".xlsx", ".xlsm", ".xltx", ".xltm",
        ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm"
    };

    internal static PreparedOfficeSource Prepare(
        string sourcePath,
        string stagingParent,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OpenXmlExtensions.Contains(System.IO.Path.GetExtension(sourcePath)))
        {
            return new PreparedOfficeSource(sourcePath, null, 0);
        }

        int externalRelationships;
        using (var source = ZipFile.OpenRead(sourcePath))
        {
            externalRelationships = source.Entries
                .Where(IsRelationshipsEntry)
                .Sum(entry => CountExternalRelationships(entry, cancellationToken));
        }

        if (externalRelationships == 0)
        {
            return new PreparedOfficeSource(sourcePath, null, 0);
        }

        var stagingRoot = System.IO.Path.Combine(
            stagingParent,
            $".glimpse-source-{Guid.NewGuid():N}");
        var sanitizedPath = System.IO.Path.Combine(stagingRoot, System.IO.Path.GetFileName(sourcePath));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            using var source = ZipFile.OpenRead(sourcePath);
            using var destination = ZipFile.Open(sanitizedPath, ZipArchiveMode.Create);
            foreach (var sourceEntry in source.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destinationEntry = destination.CreateEntry(
                    sourceEntry.FullName,
                    CompressionLevel.Fastest);
                destinationEntry.LastWriteTime = sourceEntry.LastWriteTime;
                destinationEntry.ExternalAttributes = sourceEntry.ExternalAttributes;

                using var output = destinationEntry.Open();
                if (IsRelationshipsEntry(sourceEntry))
                {
                    var document = LoadRelationships(sourceEntry, cancellationToken);
                    foreach (var relationship in ExternalRelationships(document).ToList())
                    {
                        relationship.Remove();
                    }

                    document.Save(output, SaveOptions.DisableFormatting);
                    continue;
                }

                using var input = sourceEntry.Open();
                CopyTo(input, output, cancellationToken);
            }

            return new PreparedOfficeSource(
                sanitizedPath,
                stagingRoot,
                externalRelationships);
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

    private static bool IsRelationshipsEntry(ZipArchiveEntry entry) =>
        entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase);

    private static int CountExternalRelationships(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken) =>
        ExternalRelationships(LoadRelationships(entry, cancellationToken)).Count();

    private static IEnumerable<XElement> ExternalRelationships(XDocument document) =>
        document.Descendants(PackageRelationships + "Relationship")
            .Where(relationship => string.Equals(
                (string?)relationship.Attribute("TargetMode"),
                "External",
                StringComparison.OrdinalIgnoreCase));

    private static XDocument LoadRelationships(
        ZipArchiveEntry entry,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (entry.Length > MaxRelationshipsBytes)
        {
            throw new InvalidDataException("Office relationships exceeded the preview safety limit.");
        }

        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRelationshipsBytes
        });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
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
