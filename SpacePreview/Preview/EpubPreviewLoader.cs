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
    // A book is one page, and the browser takes a long one in its stride:
    // eight million characters build in 50 ms and load in 90. That is
    // longer than War and Peace, the Bible or the complete Shakespeare.
    // Past it the page says the book goes on.
    internal const int MaxBookCharacters = 8_000_000;
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private const long MaxXmlBytes = 5 * 1024 * 1024;

    // One budget for the whole book, cover included: a comic must not
    // become a page of hundreds of megabytes.
    private const long MaxBookImageBytes = 24 * 1024 * 1024;

    /// <summary>A section the page really contains: its anchor is ch-{SpineIndex}.</summary>
    internal sealed record EpubSection(int SpineIndex, string Title);

    /// <summary>What the rail lists: the page's own titled sections, and whether the book goes on.</summary>
    internal sealed record EpubContents(IReadOnlyList<EpubSection> Sections, bool Truncated);

    public static string? TryBuildBody(string path, CancellationToken cancellationToken = default, int maxCharacters = MaxBookCharacters)
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
            var budget = new BookBudget(maxCharacters, MaxBookImageBytes);
            var cover = FindCoverDataUri(archive, package, manifest, budget, cancellationToken);
            var protectedBook = archive.GetEntry("META-INF/encryption.xml") is not null;

            var builder = new StringBuilder(32_000);
            var sections = new StringBuilder(32_000);
            var unreadable = false;
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

                    // A chapter that cannot be read costs the book that
                    // chapter, not the whole preview.
                    var preview = TryBuildChapterPreview(archive, chapter, titles, budget, cancellationToken);
                    if (preview is null)
                    {
                        unreadable = true;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(preview))
                    {
                        continue;
                    }

                    sections.Append("<section id=\"ch-");
                    sections.Append(spineIndex);
                    sections.Append('"');
                    if (titles.Count == 0 || titles.ContainsKey(chapter.Path))
                    {
                        sections.Append(" data-title=\"");
                        sections.Append(WebUtility.HtmlEncode(TitleFor(chapter.Path, titles)));
                        sections.Append('"');
                    }

                    sections.Append("><hr>");
                    sections.Append(preview);
                    sections.Append("</section>");
                }

                if (unreadable)
                {
                    // Said once, above the text, however many chapters went.
                    builder.Append("<p class=\"notice\" id=\"epub-unreadable\">");
                    builder.Append(sections.Length > 0
                        ? "Part of this book can't be shown here. Open the book to read all of it."
                        : "This book's text can't be shown here. Open the book to read it.");
                    builder.Append("</p>");
                }

                builder.Append(sections);
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
        var container = TryLoadXml(archive, "META-INF/container.xml", cancellationToken);
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
            var navXml = TryLoadXml(archive, nav.Path, cancellationToken);
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
            var ncxXml = TryLoadXml(archive, ncx.Path, cancellationToken);
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

    /// <summary>
    /// The chapter's sanitized markup, or null when it cannot be read: not
    /// in the book, past the size cap, not XML (a text/html chapter that is
    /// real HTML), nested too deep, or with no body.
    /// </summary>
    private static string? TryBuildChapterPreview(
        ZipArchive archive,
        ManifestItem chapter,
        IReadOnlyDictionary<string, string> titles,
        BookBudget budget,
        CancellationToken cancellationToken)
    {
        var xml = TryLoadXml(archive, chapter.Path, cancellationToken);
        var body = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase));
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
        if (tag == "code" && CollapsedText(element).Length > GeneratedHtmlTheme.LongChip)
        {
            builder.Append(" class=\"long\"");
        }

        builder.Append('>');

        // LoadXml refused anything nested past XmlNesting.MaxDepth, so this
        // recursion ends there.
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

        // The zip's length is a claim: read one byte past it to catch a lie.
        using var stream = entry.Open();
        var bytes = new byte[entry.Length + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length != entry.Length)
        {
            return null;
        }

        budget.ImageBytes -= length;
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes, 0, length)}";
    }

    /// <summary>
    /// A part of the book the preview can do without: its container file,
    /// its navigation, one chapter. Unreadable, it is as good as absent.
    /// </summary>
    private static XDocument? TryLoadXml(ZipArchive archive, string entryPath, CancellationToken cancellationToken)
    {
        try
        {
            return LoadXml(archive, entryPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub part unreadable: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static XDocument? LoadXml(
        ZipArchive archive,
        string entryPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = archive.GetEntry(NormalizeEntryPath(entryPath));
        if (entry is null || entry.Length > MaxXmlBytes)
        {
            return null;
        }

        // The zip's length is a claim: read one byte past it to catch a lie.
        var bytes = new byte[entry.Length + 1];
        int length;
        using (var stream = entry.Open())
        {
            length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        }

        if (length != entry.Length)
        {
            return null;
        }

        var xml = ResolveNamedEntities(bytes.AsSpan(0, length));
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlBytes
        };
        var document = XmlNesting.Load(() => new MemoryStream(xml, writable: false), settings, LoadOptions.PreserveWhitespace);
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    /// <summary>
    /// EPUB 2 writes &amp;nbsp; and &amp;mdash; freely: its chapters may use
    /// the XHTML entity set, which XML knows only through the XHTML DTD. A
    /// book's DTD is never processed, so each name becomes a character
    /// reference before the parse. A name XHTML does not define becomes its
    /// own text, as a browser shows it. Comments and CDATA pass untouched.
    /// </summary>
    internal static byte[] ResolveNamedEntities(ReadOnlySpan<byte> xml)
    {
        // The names are ASCII, so this reads UTF-8 and the one-byte code
        // pages. UTF-16 and UTF-32 start with a mark or a zero byte, and
        // keep XML's own five.
        if (xml.Length >= 2 && (xml[0] is 0xFF or 0xFE or 0x00 || xml[1] == 0x00))
        {
            return xml.ToArray();
        }

        var output = new MemoryStream(xml.Length + 256);
        var copied = 0;
        var at = 0;
        while (at < xml.Length)
        {
            var rest = xml[at..];
            if (rest[0] == (byte)'<')
            {
                var close = rest.StartsWith("<!--"u8) ? "-->"u8 : rest.StartsWith("<![CDATA["u8) ? "]]>"u8 : default;
                var end = close.IsEmpty ? -1 : rest.IndexOf(close);
                at += end < 0 ? 1 : end + close.Length;
                continue;
            }

            var semicolon = rest[0] == (byte)'&' ? rest[..Math.Min(rest.Length, MaxEntityName + 2)].IndexOf((byte)';') : -1;
            if (semicolon < 2 || rest[1..semicolon].IndexOfAnyExcept(EntityNameBytes) >= 0)
            {
                at++;
                continue;
            }

            var name = Encoding.ASCII.GetString(rest[1..semicolon]);
            if (name is not ("amp" or "lt" or "gt" or "quot" or "apos"))
            {
                var decoded = WebUtility.HtmlDecode($"&{name};");
                var reference = decoded.Length > 1 && decoded[0] == '&'
                    ? $"&amp;{name};"
                    : string.Concat(decoded.EnumerateRunes().Select(rune => $"&#{rune.Value};"));
                output.Write(xml[copied..at]);
                output.Write(Encoding.ASCII.GetBytes(reference));
                copied = at + semicolon + 1;
            }

            at += semicolon + 1;
        }

        output.Write(xml[copied..]);
        return output.ToArray();
    }

    // The longest XHTML name is "thetasym", eight letters; a longer run is not a name.
    private const int MaxEntityName = 32;

    private static readonly System.Buffers.SearchValues<byte> EntityNameBytes =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"u8);

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
