using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace SpacePreview.Utilities;

internal enum OfficePreviewType
{
    Word,
    PowerPoint,
    Excel
}

internal sealed record OfficeOpenXmlPreview(OfficePreviewType Type, string Text, IReadOnlyList<string> ImagePaths);

internal static class OfficeOpenXmlPreviewLoader
{
    private const int MaxTextLength = 80_000;
    private const long MaxXmlEntryBytes = 8L * 1024 * 1024;
    private const long MaxImageEntryBytes = 12L * 1024 * 1024;
    private const long MaxExtractedImageBytes = 48L * 1024 * 1024;
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Drawing = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Presentation = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace Spreadsheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    internal static string? MediaCacheRootOverride { get; set; }

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".docm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".odt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".pptm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".odp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ods", StringComparison.OrdinalIgnoreCase);
    }

    public static OfficeOpenXmlPreview? TryLoad(string path)
    {
        return TryLoad(path, null);
    }

    public static OfficeOpenXmlPreview? TryLoad(string path, OfficePreviewType? forcedType)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var extension = Path.GetExtension(path);
            if (forcedType == OfficePreviewType.Word || extension.Equals(".docx", StringComparison.OrdinalIgnoreCase))
            {
                return new OfficeOpenXmlPreview(OfficePreviewType.Word, LoadWordText(archive), ExtractImages(archive, path, "word/media/"));
            }

            if (forcedType == OfficePreviewType.PowerPoint || extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase))
            {
                return new OfficeOpenXmlPreview(OfficePreviewType.PowerPoint, LoadPowerPointText(archive), ExtractImages(archive, path, "ppt/media/"));
            }

            if (forcedType == OfficePreviewType.Excel || extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return new OfficeOpenXmlPreview(OfficePreviewType.Excel, LoadExcelText(archive), ExtractImages(archive, path, "xl/media/"));
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string LoadWordText(ZipArchive archive)
    {
        var entry = archive.GetEntry("word/document.xml");
        if (entry is null)
        {
            return "No document body found.";
        }

        var document = LoadXml(entry);
        var builder = new StringBuilder();
        foreach (var paragraph in document.Descendants(Word + "p").Take(700))
        {
            var text = ExtractWordParagraph(paragraph);
            if (string.IsNullOrWhiteSpace(text))
            {
                if (builder.Length > 0 && !builder.ToString().EndsWith($"{Environment.NewLine}{Environment.NewLine}", StringComparison.Ordinal))
                {
                    builder.AppendLine();
                }

                continue;
            }

            AppendLimitedLine(builder, text.Trim());
            if (builder.Length >= MaxTextLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "No readable text found." : builder.ToString().TrimEnd();
    }

    private static string ExtractWordParagraph(XElement paragraph)
    {
        var builder = new StringBuilder();
        foreach (var node in paragraph.Descendants())
        {
            if (node.Name == Word + "t")
            {
                builder.Append(node.Value);
            }
            else if (node.Name == Word + "tab")
            {
                builder.Append('\t');
            }
            else if (node.Name == Word + "br")
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private static string LoadPowerPointText(ZipArchive archive)
    {
        var presentationEntry = archive.GetEntry("ppt/presentation.xml");
        var relationshipsEntry = archive.GetEntry("ppt/_rels/presentation.xml.rels");
        if (presentationEntry is null || relationshipsEntry is null)
        {
            return "No slide deck structure found.";
        }

        var presentation = LoadXml(presentationEntry);
        var relationships = LoadXml(relationshipsEntry)
            .Root?
            .Elements(PackageRel + "Relationship")
            .Where(element => (string?)element.Attribute("Id") is not null)
            .ToDictionary(
                element => (string)element.Attribute("Id")!,
                element => (string?)element.Attribute("Target") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var slideIds = presentation
            .Descendants(Presentation + "sldId")
            .Select(element => (string?)element.Attribute(OfficeRel + "id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Cast<string>()
            .ToList();

        if (slideIds.Count == 0)
        {
            return "No slides found.";
        }

        var builder = new StringBuilder();
        for (var i = 0; i < Math.Min(slideIds.Count, 40); i++)
        {
            if (!relationships.TryGetValue(slideIds[i], out var target) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var slidePath = NormalizeZipPath("ppt", target);
            var slideEntry = archive.GetEntry(slidePath);
            if (slideEntry is null)
            {
                continue;
            }

            var slide = LoadXml(slideEntry);
            var paragraphs = ExtractDrawingParagraphs(slide)
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => text.Trim())
                .Distinct(StringComparer.Ordinal)
                .Take(25)
                .ToList();

            AppendLimitedLine(builder, $"Slide {i + 1}");
            if (paragraphs.Count == 0)
            {
                AppendLimitedLine(builder, "  No readable text.");
            }
            else
            {
                foreach (var paragraph in paragraphs)
                {
                    AppendLimitedLine(builder, $"  {paragraph}");
                }
            }

            builder.AppendLine();
            if (builder.Length >= MaxTextLength)
            {
                break;
            }
        }

        if (slideIds.Count > 40)
        {
            AppendLimitedLine(builder, $"- Showing first 40 of {slideIds.Count} slides -");
        }

        return builder.Length == 0 ? "No readable slide text found." : builder.ToString().TrimEnd();
    }

    private static IEnumerable<string> ExtractDrawingParagraphs(XDocument document)
    {
        foreach (var paragraph in document.Descendants(Drawing + "p"))
        {
            var parts = paragraph
                .Descendants(Drawing + "t")
                .Select(text => text.Value)
                .Where(text => !string.IsNullOrWhiteSpace(text));

            var value = string.Join(" ", parts).Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private static string LoadExcelText(ZipArchive archive)
    {
        var workbookEntry = archive.GetEntry("xl/workbook.xml");
        var relationshipsEntry = archive.GetEntry("xl/_rels/workbook.xml.rels");
        if (workbookEntry is null || relationshipsEntry is null)
        {
            return "No workbook structure found.";
        }

        var sharedStrings = LoadSharedStrings(archive);
        var workbook = LoadXml(workbookEntry);
        var relationships = LoadXml(relationshipsEntry)
            .Root?
            .Elements(PackageRel + "Relationship")
            .Where(element => (string?)element.Attribute("Id") is not null)
            .ToDictionary(
                element => (string)element.Attribute("Id")!,
                element => (string?)element.Attribute("Target") ?? string.Empty,
                StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sheets = workbook
            .Descendants(Spreadsheet + "sheet")
            .Select(sheet => new
            {
                Name = ((string?)sheet.Attribute("name") ?? "Sheet").Trim(),
                RelationshipId = (string?)sheet.Attribute(OfficeRel + "id")
            })
            .Where(sheet => !string.IsNullOrWhiteSpace(sheet.RelationshipId))
            .ToList();

        if (sheets.Count == 0)
        {
            return "No sheets found.";
        }

        var builder = new StringBuilder();
        foreach (var sheet in sheets.Take(6))
        {
            if (!relationships.TryGetValue(sheet.RelationshipId!, out var target) || string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var sheetPath = NormalizeZipPath("xl", target);
            var sheetEntry = archive.GetEntry(sheetPath);
            if (sheetEntry is null)
            {
                continue;
            }

            AppendLimitedLine(builder, sheet.Name);
            var sheetXml = LoadXml(sheetEntry);
            var rows = sheetXml
                .Descendants(Spreadsheet + "row")
                .Take(40)
                .Select(row => row
                    .Elements(Spreadsheet + "c")
                    .Take(16)
                    .Select(cell => ExtractCellValue(cell, sharedStrings))
                    .ToList())
                .Where(values => values.Any(value => !string.IsNullOrWhiteSpace(value)))
                .ToList();

            if (rows.Count == 0)
            {
                AppendLimitedLine(builder, "  No visible cell values.");
            }
            else
            {
                foreach (var row in rows)
                {
                    AppendLimitedLine(builder, "  " + string.Join("    ", row.Select(value => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim())));
                }
            }

            builder.AppendLine();
            if (builder.Length >= MaxTextLength)
            {
                break;
            }
        }

        if (sheets.Count > 6)
        {
            AppendLimitedLine(builder, $"- Showing first 6 of {sheets.Count} sheets -");
        }

        return builder.Length == 0 ? "No readable workbook values found." : builder.ToString().TrimEnd();
    }

    private static List<string> LoadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
        {
            return [];
        }

        var document = LoadXml(entry);
        return document
            .Descendants(Spreadsheet + "si")
            .Select(item => string.Concat(item.Descendants(Spreadsheet + "t").Select(text => text.Value)))
            .ToList();
    }

    private static string ExtractCellValue(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return string.Concat(cell.Descendants(Spreadsheet + "t").Select(text => text.Value));
        }

        var raw = cell.Element(Spreadsheet + "v")?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        if (type == "s" && int.TryParse(raw, out var sharedStringIndex) && sharedStringIndex >= 0 && sharedStringIndex < sharedStrings.Count)
        {
            return sharedStrings[sharedStringIndex];
        }

        if (type == "b")
        {
            return raw == "1" ? "TRUE" : "FALSE";
        }

        return raw;
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxXmlEntryBytes)
        {
            throw new InvalidDataException("Office XML entry is too large for preview.");
        }

        using var stream = entry.Open();
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            MaxCharactersInDocument = MaxXmlEntryBytes
        };
        using var reader = System.Xml.XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static IReadOnlyList<string> ExtractImages(ZipArchive archive, string sourcePath, string mediaPrefix)
    {
        try
        {
            var source = new FileInfo(sourcePath);
            var cacheRoot = GetMediaCacheRoot(source);
            Directory.CreateDirectory(cacheRoot);

            var images = new List<string>();
            long extractedBytes = 0;
            foreach (var entry in archive.Entries
                         .Where(entry => entry.FullName.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase))
                         .Where(IsSupportedImageEntry)
                         .Take(10))
            {
                if (entry.Length <= 0 || entry.Length > MaxImageEntryBytes || extractedBytes + entry.Length > MaxExtractedImageBytes)
                {
                    continue;
                }

                var output = Path.Combine(cacheRoot, Path.GetFileName(entry.FullName));
                entry.ExtractToFile(output, overwrite: true);

                extractedBytes += entry.Length;
                images.Add(output);
            }

            return images;
        }
        catch
        {
            return [];
        }
    }

    internal static string GetMediaCacheRoot(FileInfo source)
    {
        var identity = $"{source.FullName.ToUpperInvariant()}\n{source.Length}\n{source.LastWriteTimeUtc.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var root = MediaCacheRootOverride ?? Path.Combine(
            AppPaths.DataRoot,
            "OfficeMedia");

        return Path.Combine(
            root,
            $"{SanitizeFileName(source.Name)}-{hash[..24]}");
    }

    private static bool IsSupportedImageEntry(ZipArchiveEntry entry)
    {
        var extension = Path.GetExtension(entry.FullName);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }

    private static string NormalizeZipPath(string root, string target)
    {
        target = target.Replace('\\', '/');
        if (target.StartsWith("/", StringComparison.Ordinal))
        {
            return target.TrimStart('/');
        }

        var combined = $"{root}/{target}";
        var parts = new Stack<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.Pop();
                }

                continue;
            }

            parts.Push(part);
        }

        return string.Join("/", parts.Reverse());
    }

    private static void AppendLimitedLine(StringBuilder builder, string text)
    {
        if (builder.Length >= MaxTextLength)
        {
            return;
        }

        if (builder.Length + text.Length + Environment.NewLine.Length > MaxTextLength)
        {
            var remaining = MaxTextLength - builder.Length - Environment.NewLine.Length;
            if (remaining > 0)
            {
                builder.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
            }

            builder.AppendLine();
            builder.Append("- Preview truncated -");
            return;
        }

        builder.AppendLine(text);
    }
}
