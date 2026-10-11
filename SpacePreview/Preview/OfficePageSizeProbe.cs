using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal static class OfficePageSizeProbe
{
    private const long MaxXmlBytes = 2 * 1024 * 1024;

    /// <summary>OOXML only; other formats keep the default stage until the PDF lands.</summary>
    public static bool TryReadFirstPageAspect(string path, out double aspect)
    {
        aspect = 0;
        var kind = FileKinds.GetOfficeKind(path);
        if (kind is not (OfficeKind.Presentation or OfficeKind.Document))
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            return kind == OfficeKind.Presentation
                ? TryReadPresentationAspect(archive, out aspect)
                : TryReadWordAspect(archive, out aspect);
        }
        catch
        {
            aspect = 0;
        }

        return false;
    }

    private static bool TryReadPresentationAspect(ZipArchive archive, out double aspect)
    {
        aspect = 0;
        var document = TryLoadXml(archive, "ppt/presentation.xml");
        var size = document?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "sldSz");
        if (size is null
            || !TryReadDoubleAttribute(size, "cx", out var width)
            || !TryReadDoubleAttribute(size, "cy", out var height))
        {
            return false;
        }

        return TrySetAspect(width, height, out aspect);
    }

    private static bool TryReadWordAspect(ZipArchive archive, out double aspect)
    {
        aspect = 0;
        var document = TryLoadXml(archive, "word/document.xml");
        var pageSize = document?
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "pgSz");
        if (pageSize is null
            || !TryReadDoubleAttribute(pageSize, "w", out var width)
            || !TryReadDoubleAttribute(pageSize, "h", out var height))
        {
            return false;
        }

        var orientation = ReadAttribute(pageSize, "orient");
        if (string.Equals(orientation, "landscape", StringComparison.OrdinalIgnoreCase) && width < height)
        {
            (width, height) = (height, width);
        }

        return TrySetAspect(width, height, out aspect);
    }

    private static XDocument? TryLoadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        if (entry is null || entry.Length <= 0 || entry.Length > MaxXmlBytes)
        {
            return null;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxXmlBytes,
            XmlResolver = null
        };

        return XmlNesting.Load(entry.Open, settings, LoadOptions.None);
    }

    private static bool TryReadDoubleAttribute(XElement element, string localName, out double value) =>
        double.TryParse(
            ReadAttribute(element, localName),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out value)
        && double.IsFinite(value);

    private static string? ReadAttribute(XElement element, string localName) =>
        element
            .Attributes()
            .FirstOrDefault(attribute => attribute.Name.LocalName == localName)
            ?.Value;

    private static bool TrySetAspect(double width, double height, out double aspect)
    {
        aspect = 0;
        if (width < 1 || height < 1)
        {
            return false;
        }

        aspect = width / height;
        return double.IsFinite(aspect) && aspect is >= 0.2 and <= 5;
    }

}
