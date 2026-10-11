using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The picture of page one that Office and LibreOffice save inside a
/// document. It is the poster a conversion waits behind: read straight from
/// the zip, PNG or JPEG only, size-capped, never a link.
/// </summary>
internal static class OfficeThumbnail
{
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private const long MaxRelationshipsBytes = 256 * 1024;
    private const int MaxSide = 4096;

    private const string ThumbnailRelationshipSuffix = "/metadata/thumbnail";
    private static readonly XNamespace PackageRelationships = "http://schemas.openxmlformats.org/package/2006/relationships";

    // Where the authoring apps put it when the package does not say.
    private static readonly string[] KnownEntries =
    [
        "Thumbnails/thumbnail.png",
        "docProps/thumbnail.jpeg",
        "docProps/thumbnail.jpg",
        "docProps/thumbnail.png"
    ];

    /// <summary>A frozen bitmap, safe to hand across threads, or null.</summary>
    public static BitmapSource? TryRead(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = FindEntry(archive);
            if (entry is null || entry.Length <= 0 || entry.Length > MaxImageBytes)
            {
                return null;
            }

            using var memory = new MemoryStream((int)entry.Length);
            using (var stream = entry.Open())
            {
                // Length is the zip's claim; the copy is capped on what arrives.
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (memory.Length + read > MaxImageBytes)
                    {
                        return null;
                    }

                    memory.Write(buffer, 0, read);
                }
            }

            memory.Position = 0;
            BitmapDecoder? decoder = Sniff(memory) switch
            {
                ImageFormat.Png => new PngBitmapDecoder(memory, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None),
                ImageFormat.Jpeg => new JpegBitmapDecoder(memory, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None),
                _ => null
            };
            if (decoder is null || decoder.Frames.Count == 0)
            {
                return null;
            }

            // The header names the size before anything is decoded: a tiny
            // file can still claim a giant canvas.
            var frame = decoder.Frames[0];
            if (frame.PixelWidth is <= 0 or > MaxSide || frame.PixelHeight is <= 0 or > MaxSide)
            {
                return null;
            }

            var bitmap = new WriteableBitmap(frame);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive)
    {
        if (ThumbnailTarget(archive) is { } target && archive.GetEntry(target) is { } declared)
        {
            return declared;
        }

        return KnownEntries.Select(archive.GetEntry).FirstOrDefault(entry => entry is not null);
    }

    /// <summary>The package's own pointer to its thumbnail part, when it is inside the package.</summary>
    private static string? ThumbnailTarget(ZipArchive archive)
    {
        var rels = archive.GetEntry("_rels/.rels");
        if (rels is null || rels.Length <= 0 || rels.Length > MaxRelationshipsBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxRelationshipsBytes
        };
        var relationship = XmlNesting.Load(rels.Open, settings, LoadOptions.None)
            .Descendants(PackageRelationships + "Relationship")
            .FirstOrDefault(element =>
                ((string?)element.Attribute("Type"))?.EndsWith(ThumbnailRelationshipSuffix, StringComparison.OrdinalIgnoreCase) == true
                && !string.Equals((string?)element.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase));
        var target = (string?)relationship?.Attribute("Target");
        if (string.IsNullOrWhiteSpace(target) || target.Contains(':', StringComparison.Ordinal))
        {
            return null;
        }

        return target.Replace('\\', '/').TrimStart('/');
    }

    private enum ImageFormat
    {
        Unknown,
        Png,
        Jpeg
    }

    private static ImageFormat Sniff(MemoryStream memory)
    {
        var bytes = memory.GetBuffer();
        var length = memory.Length;
        if (length >= 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P' && bytes[2] == (byte)'N' && bytes[3] == (byte)'G')
        {
            return ImageFormat.Png;
        }

        if (length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return ImageFormat.Jpeg;
        }

        return ImageFormat.Unknown;
    }
}
