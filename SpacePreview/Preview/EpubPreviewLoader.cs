using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal static partial class EpubPreviewLoader
{
    private const int MaxBookCharacters = 500_000;
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private const long MaxEmbeddedImageBytes = 8 * 1024 * 1024;

    public static IReadOnlyList<EpubChapterEntry>? TryGetChapters(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.GetEntry("META-INF/encryption.xml") is not null)
            {
                return Array.Empty<EpubChapterEntry>();
            }

            var packagePath = FindPackagePath(archive);
            if (packagePath is null)
            {
                return null;
            }

            var package = LoadXml(archive, packagePath);
            if (package is null)
            {
                return null;
            }

            var packageBase = PathPrefix(packagePath);
            var manifest = LoadManifest(package, packageBase);
            var spine = LoadSpine(package);
            var titles = LoadNavigationTitles(archive, package, manifest);
            var chapters = new List<EpubChapterEntry>();

            for (var spineIndex = 0; spineIndex < spine.Count; spineIndex++)
            {
                if (!manifest.TryGetValue(spine[spineIndex], out var item) || !IsDocumentMedia(item.MediaType))
                {
                    continue;
                }

                chapters.Add(new EpubChapterEntry(
                    path,
                    spineIndex,
                    TitleFor(item.Path, titles),
                    item.Path));
            }

            return chapters;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub chapter model failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static string? TryBuildBody(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var packagePath = FindPackagePath(archive);
            if (packagePath is null)
            {
                return null;
            }

            var package = LoadXml(archive, packagePath);
            if (package is null)
            {
                return null;
            }

            var packageBase = PathPrefix(packagePath);
            var manifest = LoadManifest(package, packageBase);
            var spine = LoadSpine(package);
            var titles = LoadNavigationTitles(archive, package, manifest);
            var chapters = spine
                .Select(id => manifest.TryGetValue(id, out var item) ? item : null)
                .Where(item => item is not null && IsDocumentMedia(item.MediaType))
                .Select(item => item!)
                .ToList();

            var title = MetadataValue(package, "title") ?? Path.GetFileNameWithoutExtension(path);
            var creator = MetadataValue(package, "creator");
            var cover = FindCoverDataUri(archive, package, manifest);
            var protectedBook = archive.GetEntry("META-INF/encryption.xml") is not null;

            var builder = new StringBuilder(32_000);
            builder.Append("<div class=\"md-root\">");
            builder.Append("<h1>");
            builder.Append(WebUtility.HtmlEncode(title));
            builder.Append("</h1>");
            if (!string.IsNullOrWhiteSpace(creator))
            {
                builder.Append("<p><strong>");
                builder.Append(WebUtility.HtmlEncode(creator));
                builder.Append("</strong></p>");
            }

            if (!string.IsNullOrWhiteSpace(cover))
            {
                builder.Append("<p><img class=\"cover\" alt=\"Cover\" src=\"");
                builder.Append(cover);
                builder.Append("\"></p>");
            }

            if (protectedBook)
            {
                builder.Append("<p><strong>Protected book.</strong></p>");
            }
            else if (chapters.Count > 0)
            {
                builder.Append("<h2>Contents</h2><ol>");
                foreach (var chapter in chapters.Take(60))
                {
                    builder.Append("<li>");
                    builder.Append(WebUtility.HtmlEncode(TitleFor(chapter.Path, titles)));
                    builder.Append("</li>");
                }

                if (chapters.Count > 60)
                {
                    builder.Append("<li>");
                    builder.Append(WebUtility.HtmlEncode($"{chapters.Count - 60:N0} more chapters"));
                    builder.Append("</li>");
                }

                builder.Append("</ol>");

                var remaining = MaxBookCharacters;
                for (var spineIndex = 0; spineIndex < spine.Count; spineIndex++)
                {
                    if (remaining <= 0)
                    {
                        break;
                    }

                    if (!manifest.TryGetValue(spine[spineIndex], out var chapter) || !IsDocumentMedia(chapter.MediaType))
                    {
                        continue;
                    }

                    var preview = BuildChapterPreview(archive, chapter, titles, ref remaining);
                    if (!string.IsNullOrWhiteSpace(preview))
                    {
                        // The id matches EpubChapterEntry.SpineIndex so the
                        // chapter rail can scroll straight to the section.
                        builder.Append("<section id=\"ch-");
                        builder.Append(spineIndex);
                        builder.Append("\">");
                        builder.Append("<hr>");
                        builder.Append(preview);
                        builder.Append("</section>");
                    }
                }

                if (remaining <= 0)
                {
                    builder.Append("<p><em>Showing the beginning of this book. Open the book for the rest.</em></p>");
                }
            }

            builder.Append("</div>");
            return builder.ToString();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string? FindPackagePath(ZipArchive archive)
    {
        var container = LoadXml(archive, "META-INF/container.xml");
        var rootFile = container?
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("rootfile", StringComparison.OrdinalIgnoreCase));
        var path = rootFile?.Attribute("full-path")?.Value;
        if (!string.IsNullOrWhiteSpace(path) && archive.GetEntry(NormalizeEntryPath(path)) is not null)
        {
            return NormalizeEntryPath(path);
        }

        return archive.Entries
            .Select(entry => entry.FullName)
            .FirstOrDefault(name => name.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
    }

    private static Dictionary<string, ManifestItem> LoadManifest(XDocument package, string packageBase)
    {
        var result = new Dictionary<string, ManifestItem>(StringComparer.Ordinal);
        foreach (var item in package.Descendants().Where(e => e.Name.LocalName.Equals("item", StringComparison.OrdinalIgnoreCase)))
        {
            var id = item.Attribute("id")?.Value;
            var href = item.Attribute("href")?.Value;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            result[id] = new ManifestItem(
                id,
                NormalizeEntryPath(CombineEntryPath(packageBase, href)),
                item.Attribute("media-type")?.Value ?? string.Empty,
                item.Attribute("properties")?.Value ?? string.Empty);
        }

        return result;
    }

    private static List<string> LoadSpine(XDocument package)
    {
        return package
            .Descendants()
            .Where(e => e.Name.LocalName.Equals("itemref", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("idref")?.Value)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToList();
    }

    private static Dictionary<string, string> LoadNavigationTitles(
        ZipArchive archive,
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nav = manifest.Values.FirstOrDefault(item =>
            item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.OrdinalIgnoreCase));
        if (nav is not null)
        {
            var navXml = LoadXml(archive, nav.Path);
            if (navXml is not null)
            {
                foreach (var link in navXml.Descendants().Where(e => e.Name.LocalName.Equals("a", StringComparison.OrdinalIgnoreCase)))
                {
                    AddNavigationTitle(result, nav.Path, link.Attribute("href")?.Value, CollapsedText(link));
                }
            }
        }

        var tocId = package
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals("spine", StringComparison.OrdinalIgnoreCase))
            ?.Attribute("toc")?.Value;
        if (!string.IsNullOrWhiteSpace(tocId) && manifest.TryGetValue(tocId, out var ncx))
        {
            var ncxXml = LoadXml(archive, ncx.Path);
            if (ncxXml is not null)
            {
                foreach (var point in ncxXml.Descendants().Where(e => e.Name.LocalName.Equals("navPoint", StringComparison.OrdinalIgnoreCase)))
                {
                    var content = point.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("content", StringComparison.OrdinalIgnoreCase));
                    var label = point.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase))?.Value;
                    AddNavigationTitle(result, ncx.Path, content?.Attribute("src")?.Value, label);
                }
            }
        }

        return result;
    }

    private static string? BuildChapterPreview(
        ZipArchive archive,
        ManifestItem chapter,
        IReadOnlyDictionary<string, string> titles,
        ref int remainingCharacters)
    {
        var xml = LoadXml(archive, chapter.Path);
        if (xml is null)
        {
            return null;
        }

        var body = xml.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null)
        {
            return null;
        }

        var context = new ChapterContext(archive, chapter.Path, remainingCharacters);
        var builder = new StringBuilder(32_000);
        var hasHeading = body.Descendants().Any(element => IsHeading(element.Name.LocalName));
        if (!hasHeading)
        {
            builder.Append("<h2>");
            builder.Append(WebUtility.HtmlEncode(TitleFor(chapter.Path, titles)));
            builder.Append("</h2>");
        }

        foreach (var node in body.Nodes())
        {
            if (!context.HasTextBudget)
            {
                break;
            }

            AppendSanitizedNode(builder, node, context);
        }

        remainingCharacters = context.RemainingCharacters;
        return builder.ToString();
    }

    private static string? TryRenderChapterBody(string sourcePath, string entryPath, string title)
    {
        try
        {
            using var archive = ZipFile.OpenRead(sourcePath);
            if (archive.GetEntry("META-INF/encryption.xml") is not null)
            {
                return null;
            }

            var chapter = new ManifestItem(
                string.Empty,
                NormalizeEntryPath(entryPath),
                "application/xhtml+xml",
                string.Empty);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [chapter.Path] = title
            };
            var remaining = MaxBookCharacters;
            return BuildChapterPreview(archive, chapter, titles, ref remaining);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub chapter render failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static void AppendSanitizedNode(StringBuilder builder, XNode node, ChapterContext context)
    {
        switch (node)
        {
            case XText text:
                var value = context.TakeText(text.Value);
                if (value.Length > 0)
                {
                    builder.Append(WebUtility.HtmlEncode(value));
                }

                return;
            case XElement element:
                AppendSanitizedElement(builder, element, context);
                return;
        }
    }

    private static void AppendSanitizedElement(StringBuilder builder, XElement element, ChapterContext context)
    {
        var name = element.Name.LocalName.ToLowerInvariant();
        if (name is "script" or "style" or "iframe" or "object" or "embed" or "link" or "form" or "input" or "button")
        {
            return;
        }

        if (name == "img")
        {
            AppendImage(builder, element, context);
            return;
        }

        var tag = IsHeading(name) ? DemoteHeading(name) : AllowedTag(name) ? name : "span";
        builder.Append('<');
        builder.Append(tag);
        builder.Append('>');
        foreach (var child in element.Nodes())
        {
            AppendSanitizedNode(builder, child, context);
        }

        builder.Append("</");
        builder.Append(tag);
        builder.Append('>');
    }

    private static void AppendImage(StringBuilder builder, XElement element, ChapterContext context)
    {
        var source = element.Attribute("src")?.Value
            ?? element.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("href", StringComparison.OrdinalIgnoreCase))?.Value;
        var alt = element.Attribute("alt")?.Value ?? "Image";
        var data = context.TryReadImageDataUri(source);
        if (data is null)
        {
            builder.Append("<p><em>");
            builder.Append(WebUtility.HtmlEncode(alt));
            builder.Append("</em></p>");
            return;
        }

        builder.Append("<p><img alt=\"");
        builder.Append(WebUtility.HtmlEncode(alt));
        builder.Append("\" src=\"");
        builder.Append(data);
        builder.Append("\"></p>");
    }

    private static string? FindCoverDataUri(
        ZipArchive archive,
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest)
    {
        var cover = manifest.Values.FirstOrDefault(item =>
            item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.OrdinalIgnoreCase));
        if (cover is null)
        {
            var coverId = package
                .Descendants()
                .FirstOrDefault(e =>
                    e.Name.LocalName.Equals("meta", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Attribute("name")?.Value, "cover", StringComparison.OrdinalIgnoreCase))
                ?.Attribute("content")?.Value;
            if (!string.IsNullOrWhiteSpace(coverId))
            {
                manifest.TryGetValue(coverId, out cover);
            }
        }

        return cover is null ? null : ReadImageDataUri(archive, cover.Path, 0, out _);
    }

    private static string? ReadImageDataUri(ZipArchive archive, string entryPath, long alreadyEmbedded, out long bytesRead)
    {
        bytesRead = 0;
        var entry = archive.GetEntry(NormalizeEntryPath(entryPath));
        if (entry is null || entry.Length <= 0 || entry.Length > MaxImageBytes || alreadyEmbedded + entry.Length > MaxEmbeddedImageBytes)
        {
            return null;
        }

        var contentType = ContentTypeFor(entry.FullName);
        if (contentType is null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        bytesRead = memory.Length;
        return $"data:{contentType};base64,{Convert.ToBase64String(memory.ToArray())}";
    }

    private static XDocument? LoadXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(NormalizeEntryPath(entryPath));
        if (entry is null || entry.Length > 5 * 1024 * 1024)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }

    private static string? MetadataValue(XDocument package, string name)
    {
        return package
            .Descendants()
            .FirstOrDefault(e => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.Value
            .Trim();
    }

    private static void AddNavigationTitle(Dictionary<string, string> titles, string basePath, string? href, string? title)
    {
        if (string.IsNullOrWhiteSpace(href) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var path = NormalizeEntryPath(CombineEntryPath(PathPrefix(basePath), StripFragment(href)));
        titles.TryAdd(path, title.Trim());
    }

    private static string TitleFor(string path, IReadOnlyDictionary<string, string> titles)
    {
        return titles.TryGetValue(path, out var title) && !string.IsNullOrWhiteSpace(title)
            ? title
            : Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Replace('-', ' ');
    }

    private static string CollapsedText(XElement element)
    {
        return WhitespaceRegex().Replace(element.Value, " ").Trim();
    }

    private static bool IsDocumentMedia(string mediaType)
    {
        return mediaType.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool AllowedTag(string name)
    {
        return name is "p" or "div" or "section" or "article" or "header" or "footer"
            or "h1" or "h2" or "h3" or "h4" or "h5" or "h6"
            or "span" or "em" or "strong" or "b" or "i" or "u" or "sup" or "sub" or "small"
            or "blockquote" or "ul" or "ol" or "li" or "br" or "hr"
            or "table" or "thead" or "tbody" or "tr" or "th" or "td"
            or "pre" or "code";
    }

    private static bool IsHeading(string name)
    {
        return name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";
    }

    private static string DemoteHeading(string name)
    {
        return name switch
        {
            "h1" => "h2",
            "h2" => "h3",
            "h3" => "h4",
            "h4" => "h5",
            _ => "h6"
        };
    }

    private static string? ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => null
        };
    }

    private static string CombineEntryPath(string basePath, string href)
    {
        href = WebUtility.UrlDecode(StripFragment(href)).Replace('\\', '/');
        return string.IsNullOrWhiteSpace(basePath)
            ? href
            : $"{basePath.TrimEnd('/')}/{href}";
    }

    private static string StripFragment(string href)
    {
        var index = href.IndexOf('#', StringComparison.Ordinal);
        return index >= 0 ? href[..index] : href;
    }

    private static string NormalizeEntryPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>();
        foreach (var part in parts)
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                continue;
            }

            stack.Add(part);
        }

        return string.Join('/', stack);
    }

    private static string PathPrefix(string path)
    {
        path = NormalizeEntryPath(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);

    internal sealed record EpubChapterEntry(
        string SourcePath,
        int SpineIndex,
        string Title,
        string EntryPath)
    {
        public string? TryRenderBody() => TryRenderChapterBody(SourcePath, EntryPath, Title);
    }

    private sealed class ChapterContext(ZipArchive archive, string chapterPath, int remainingCharacters)
    {
        private long _embeddedBytes;

        public int RemainingCharacters { get; private set; } = remainingCharacters;

        public bool HasTextBudget => RemainingCharacters > 0;

        public string TakeText(string text)
        {
            if (RemainingCharacters <= 0 || text.Length == 0)
            {
                return string.Empty;
            }

            if (text.Length <= RemainingCharacters)
            {
                RemainingCharacters -= text.Length;
                return text;
            }

            var result = text[..RemainingCharacters];
            RemainingCharacters = 0;
            return result;
        }

        public string? TryReadImageDataUri(string? source)
        {
            if (string.IsNullOrWhiteSpace(source)
                || Uri.TryCreate(source, UriKind.Absolute, out var parsed)
                    && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                        || parsed.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }

            var entryPath = NormalizeEntryPath(CombineEntryPath(PathPrefix(chapterPath), source));
            var data = ReadImageDataUri(archive, entryPath, _embeddedBytes, out var bytesRead);
            _embeddedBytes += bytesRead;
            return data;
        }
    }
}
