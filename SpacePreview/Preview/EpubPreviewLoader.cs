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

    // One budget for the whole book, cover included: a comic must not
    // become a page of hundreds of megabytes.
    private const long MaxBookImageBytes = 24 * 1024 * 1024;

    /// <summary>A section the page really contains: its anchor is ch-{SpineIndex}.</summary>
    internal sealed record EpubSection(int SpineIndex, string Title);

    /// <summary>What the rail lists: the page's own titled sections, and whether the book goes on.</summary>
    internal sealed record EpubContents(IReadOnlyList<EpubSection> Sections, bool Truncated);

    public static string? TryBuildBody(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(path);
            var packagePath = FindPackagePath(archive, cancellationToken);
            if (packagePath is null)
            {
                return null;
            }

            var package = LoadXml(archive, packagePath, cancellationToken);
            if (package is null)
            {
                return null;
            }

            var packageBase = PathPrefix(packagePath);
            var manifest = LoadManifest(package, packageBase);
            var spine = LoadSpine(package);
            var titles = LoadNavigationTitles(archive, package, manifest, cancellationToken);
            var title = MetadataValue(package, "title") ?? Path.GetFileNameWithoutExtension(path);
            var creator = MetadataValue(package, "creator");
            var budget = new BookBudget(MaxBookCharacters, MaxBookImageBytes);
            var cover = FindCoverDataUri(archive, package, manifest, budget, cancellationToken);
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
            else
            {
                // The rail is the contents: a section is listed when the
                // book's own navigation names it, or every section when the
                // book has no navigation at all.
                for (var spineIndex = 0; spineIndex < spine.Count && budget.HasText; spineIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!manifest.TryGetValue(spine[spineIndex], out var chapter) || !IsDocumentMedia(chapter.MediaType))
                    {
                        continue;
                    }

                    var preview = BuildChapterPreview(archive, chapter, titles, budget, cancellationToken);
                    if (string.IsNullOrWhiteSpace(preview))
                    {
                        continue;
                    }

                    builder.Append("<section id=\"ch-");
                    builder.Append(spineIndex);
                    builder.Append('"');
                    if (titles.Count == 0 || titles.ContainsKey(chapter.Path))
                    {
                        builder.Append(" data-title=\"");
                        builder.Append(WebUtility.HtmlEncode(TitleFor(chapter.Path, titles)));
                        builder.Append('"');
                    }

                    builder.Append("><hr>");
                    builder.Append(preview);
                    builder.Append("</section>");
                }

                if (!budget.HasText)
                {
                    builder.Append("<p id=\"epub-rest\"><em>Showing the beginning of this book. Open the book for the rest.</em></p>");
                }
            }

            builder.Append("</div>");
            return builder.ToString();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The rail's contents, read from the very page it navigates, so a tile
    /// can never point at an anchor the page does not have.
    /// </summary>
    public static EpubContents ReadContents(string htmlPath)
    {
        try
        {
            var html = File.ReadAllText(htmlPath);
            var sections = SectionRegex().Matches(html)
                .Select(match => new EpubSection(
                    int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    WebUtility.HtmlDecode(match.Groups[2].Value)))
                .ToList();
            return new EpubContents(sections, html.Contains("<p id=\"epub-rest\">", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub contents failed {htmlPath}: {ex.GetType().Name}: {ex.Message}");
            return new EpubContents([], false);
        }
    }

    private static string? FindPackagePath(ZipArchive archive, CancellationToken cancellationToken = default)
    {
        var container = LoadXml(archive, "META-INF/container.xml", cancellationToken);
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
        IReadOnlyDictionary<string, ManifestItem> manifest,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nav = manifest.Values.FirstOrDefault(item =>
            item.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav", StringComparer.OrdinalIgnoreCase));
        if (nav is not null)
        {
            var navXml = LoadXml(archive, nav.Path, cancellationToken);
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
            var ncxXml = LoadXml(archive, ncx.Path, cancellationToken);
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
        BookBudget budget,
        CancellationToken cancellationToken)
    {
        var xml = LoadXml(archive, chapter.Path, cancellationToken);
        if (xml is null)
        {
            return null;
        }

        var body = xml.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
        if (body is null)
        {
            return null;
        }

        var context = new ChapterContext(archive, chapter.Path, budget, cancellationToken);
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
            context.ThrowIfCancellationRequested();
            if (!context.HasTextBudget)
            {
                break;
            }

            AppendSanitizedNode(builder, node, context);
        }

        return builder.ToString();
    }

    private static void AppendSanitizedNode(StringBuilder builder, XNode node, ChapterContext context)
    {
        context.ThrowIfCancellationRequested();
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
        IReadOnlyDictionary<string, ManifestItem> manifest,
        BookBudget budget,
        CancellationToken cancellationToken)
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

        return cover is null ? null : ReadImageDataUri(archive, cover.Path, budget, cancellationToken);
    }

    private static string? ReadImageDataUri(
        ZipArchive archive,
        string entryPath,
        BookBudget budget,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = archive.GetEntry(NormalizeEntryPath(entryPath));
        if (entry is null || entry.Length <= 0 || entry.Length > MaxImageBytes || entry.Length > budget.ImageBytes)
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
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            memory.Write(buffer, 0, read);
        }

        budget.ImageBytes -= memory.Length;
        return $"data:{contentType};base64,{Convert.ToBase64String(memory.GetBuffer(), 0, (int)memory.Length)}";
    }

    private static XDocument? LoadXml(
        ZipArchive archive,
        string entryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
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

    [GeneratedRegex("<section id=\"ch-(\\d+)\" data-title=\"([^\"]*)\">")]
    private static partial Regex SectionRegex();

    private sealed record ManifestItem(string Id, string Path, string MediaType, string Properties);

    /// <summary>What is left of the book's text and picture allowance.</summary>
    private sealed class BookBudget(int characters, long imageBytes)
    {
        public int Characters { get; set; } = characters;
        public long ImageBytes { get; set; } = imageBytes;
        public bool HasText => Characters > 0;
    }

    private sealed class ChapterContext(
        ZipArchive archive,
        string chapterPath,
        BookBudget budget,
        CancellationToken cancellationToken)
    {
        public bool HasTextBudget => budget.HasText;

        public void ThrowIfCancellationRequested() => cancellationToken.ThrowIfCancellationRequested();

        public string TakeText(string text)
        {
            if (budget.Characters <= 0 || text.Length == 0)
            {
                return string.Empty;
            }

            var taken = text.Length <= budget.Characters ? text : text[..budget.Characters];
            budget.Characters -= taken.Length;
            return taken;
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
            return ReadImageDataUri(archive, entryPath, budget, cancellationToken);
        }
    }
}
