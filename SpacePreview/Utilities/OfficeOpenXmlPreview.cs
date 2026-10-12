using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SpacePreview.Preview;

namespace SpacePreview.Utilities;

/// <summary>
/// The preview of an OOXML file when no engine can convert it: its words,
/// tables and pictures read straight from the package and written as a
/// page. Documents read like a document, decks become one card per slide,
/// and workbooks become real sheets in the same chrome a conversion gets.
/// Pictures are inlined from the zip; nothing outside the file is read.
/// </summary>
internal static class OfficeOpenXmlPreview
{
    private const int MaxTextCharacters = 120_000;
    private const int MaxSlides = 60;
    private const int MaxSheets = 12;
    private const int MaxSheetRows = 500;
    private const int MaxSheetColumns = 52;
    private const long MaxXmlEntryBytes = 8L * 1024 * 1024;
    private const long MaxImageBytes = 8L * 1024 * 1024;
    private const long MaxBookImageBytes = 24L * 1024 * 1024;
    private const string PageVersion = "fallback-v4";

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace PackageRel = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>
    /// Writes the page next to the conversion cache and returns it like a
    /// conversion: one HTML page, plus one page per sheet for a workbook.
    /// </summary>
    public static ConvertedOfficeDocument? TryBuild(string path, OfficeKind kind, CancellationToken cancellationToken)
    {
        try
        {
            var directory = CacheDirectory(path, kind);
            var pagePath = Path.Combine(directory, "page.html");
            if (File.Exists(pagePath))
            {
                return new ConvertedOfficeDocument(pagePath, SheetHtml.TryReadSheetList(pagePath));
            }

            using var archive = ZipFile.OpenRead(path);
            var budget = new Budget();
            var staging = directory + ".tmp-" + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging);
            try
            {
                var written = kind switch
                {
                    OfficeKind.Document => WriteDocument(archive, staging, budget, cancellationToken),
                    OfficeKind.Presentation => WriteDeck(archive, staging, budget, cancellationToken),
                    OfficeKind.Spreadsheet => WriteWorkbook(archive, staging, cancellationToken),
                    _ => false
                };
                if (!written)
                {
                    return null;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
                if (!Directory.Exists(directory))
                {
                    Directory.Move(staging, directory);
                }
            }
            finally
            {
                OfficeDocumentPdfConverter.TryDeleteDirectory(staging);
            }

            return new ConvertedOfficeDocument(pagePath, SheetHtml.TryReadSheetList(pagePath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static bool WriteDocument(ZipArchive archive, string directory, Budget budget, CancellationToken cancellationToken)
    {
        var body = LoadXml(archive, "word/document.xml")?.Root?.Element(W + "body");
        if (body is null)
        {
            return false;
        }

        var images = Relationships(archive, "word/_rels/document.xml.rels", "word");
        var html = new StringBuilder("<div class=\"md-root office\">");
        foreach (var block in Blocks(body))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.TextExhausted)
            {
                break;
            }

            if (block.Name == W + "tbl")
            {
                AppendWordTable(html, block, budget);
                continue;
            }

            var tag = HeadingTag(block);
            var content = new StringBuilder();
            AppendWordRuns(content, block, archive, images, budget);
            if (content.Length > 0)
            {
                html.Append('<').Append(tag).Append('>').Append(content).Append("</").Append(tag).Append('>');
            }
        }

        AppendTruncation(html, budget);
        html.Append("</div>");
        File.WriteAllText(Path.Combine(directory, "page.html"), Shell(html.ToString(), string.Empty), Encoding.UTF8);
        return true;
    }

    /// <summary>
    /// Paragraphs and tables in reading order, through content controls,
    /// which nest no deeper than <see cref="XmlNesting.MaxDepth"/> allows.
    /// </summary>
    private static IEnumerable<XElement> Blocks(XElement container)
    {
        foreach (var element in container.Elements())
        {
            if (element.Name == W + "p" || element.Name == W + "tbl")
            {
                yield return element;
            }
            else if (element.Name == W + "sdt" && element.Element(W + "sdtContent") is { } content)
            {
                foreach (var nested in Blocks(content))
                {
                    yield return nested;
                }
            }
        }
    }

    private static string HeadingTag(XElement paragraph)
    {
        var style = (string?)paragraph.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? string.Empty;
        if (style.Equals("Title", StringComparison.OrdinalIgnoreCase))
        {
            return "h1";
        }

        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style.AsSpan("Heading".Length), out var level))
        {
            return level switch
            {
                1 => "h2",
                2 => "h3",
                _ => "h4"
            };
        }

        return "p";
    }

    private static void AppendWordRuns(
        StringBuilder html,
        XElement paragraph,
        ZipArchive archive,
        IReadOnlyDictionary<string, string> images,
        Budget budget)
    {
        foreach (var run in paragraph.Descendants(W + "r"))
        {
            var properties = run.Element(W + "rPr");
            var bold = IsOn(properties?.Element(W + "b"));
            var italic = IsOn(properties?.Element(W + "i"));
            var text = new StringBuilder();
            foreach (var node in run.Elements())
            {
                if (node.Name == W + "t")
                {
                    text.Append(WebUtility.HtmlEncode(budget.TakeText(node.Value)));
                }
                else if (node.Name == W + "tab")
                {
                    text.Append(' ');
                }
                else if (node.Name == W + "br")
                {
                    text.Append("<br>");
                }
            }

            if (text.Length > 0)
            {
                html.Append(bold ? "<strong>" : string.Empty)
                    .Append(italic ? "<em>" : string.Empty)
                    .Append(text)
                    .Append(italic ? "</em>" : string.Empty)
                    .Append(bold ? "</strong>" : string.Empty);
            }

            foreach (var blip in run.Descendants(A + "blip"))
            {
                AppendImage(html, archive, images, (string?)blip.Attribute(R + "embed"), budget);
            }
        }
    }

    /// <summary>
    /// A Word table with its merged cells kept: gridSpan becomes colspan, and
    /// a vertical merge becomes one cell spanning the rows it continues into.
    /// </summary>
    private static void AppendWordTable(StringBuilder html, XElement table, Budget budget)
    {
        var rows = table.Elements(W + "tr")
            .Select(row =>
            {
                var column = 0;
                return row.Elements(W + "tc").Select(cell =>
                {
                    var properties = cell.Element(W + "tcPr");
                    var span = int.TryParse((string?)properties?.Element(W + "gridSpan")?.Attribute(W + "val"), out var value) ? Math.Max(1, value) : 1;
                    var merge = properties?.Element(W + "vMerge");
                    var continues = merge is not null && (string?)merge.Attribute(W + "val") is null or "continue";
                    var placed = (Cell: cell, Column: column, Span: span, Continues: continues);
                    column += span;
                    return placed;
                }).ToList();
            })
            .ToList();

        html.Append("<table>");
        for (var r = 0; r < rows.Count; r++)
        {
            html.Append("<tr>");
            foreach (var (cell, column, span, continues) in rows[r])
            {
                if (continues)
                {
                    continue;
                }

                var rowSpan = 1;
                while (r + rowSpan < rows.Count
                       && rows[r + rowSpan].Any(below => below.Column == column && below.Continues))
                {
                    rowSpan++;
                }

                var lines = cell.Descendants(W + "p")
                    .Select(paragraph => string.Concat(paragraph.Descendants(W + "t").Select(text => text.Value)))
                    .Where(line => line.Length > 0)
                    .Select(line => WebUtility.HtmlEncode(budget.TakeText(line)));
                html.Append("<td")
                    .Append(span > 1 ? $" colspan=\"{span}\"" : string.Empty)
                    .Append(rowSpan > 1 ? $" rowspan=\"{rowSpan}\"" : string.Empty)
                    .Append('>')
                    .AppendJoin("<br>", lines)
                    .Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</table>");
    }

    private static bool WriteDeck(ZipArchive archive, string directory, Budget budget, CancellationToken cancellationToken)
    {
        var presentation = LoadXml(archive, "ppt/presentation.xml");
        if (presentation is null)
        {
            return false;
        }

        var slideTargets = Relationships(archive, "ppt/_rels/presentation.xml.rels", "ppt");
        var slideIds = presentation.Descendants(P + "sldId")
            .Select(element => (string?)element.Attribute(R + "id"))
            .Where(id => id is not null && slideTargets.ContainsKey(id))
            .Select(id => slideTargets[id!])
            .ToList();
        var size = presentation.Descendants(P + "sldSz").FirstOrDefault();
        var aspect = size is not null
                     && long.TryParse((string?)size.Attribute("cx"), out var cx)
                     && long.TryParse((string?)size.Attribute("cy"), out var cy)
                     && cx > 0 && cy > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{cx} / {cy}")
            : "16 / 9";

        var html = new StringBuilder("<div class=\"md-root office deck\">");
        for (var index = 0; index < Math.Min(slideIds.Count, MaxSlides); index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.TextExhausted)
            {
                break;
            }

            var slidePath = slideIds[index];
            var slide = LoadXml(archive, slidePath);
            if (slide is null)
            {
                continue;
            }

            var slideImages = Relationships(archive, RelationshipsPathFor(slidePath), Path.GetDirectoryName(slidePath)!.Replace('\\', '/'));
            html.Append("<section class=\"slide\" style=\"aspect-ratio:").Append(aspect).Append("\">");
            html.Append("<p class=\"slide-number\">").Append(index + 1).Append("</p>");
            foreach (var shape in slide.Descendants().Where(element => element.Name == P + "sp" || element.Name == P + "pic" || element.Name == A + "tbl"))
            {
                if (shape.Name == P + "pic")
                {
                    AppendImage(html, archive, slideImages, (string?)shape.Descendants(A + "blip").FirstOrDefault()?.Attribute(R + "embed"), budget);
                    continue;
                }

                if (shape.Name == A + "tbl")
                {
                    AppendDrawingTable(html, shape, budget);
                    continue;
                }

                var placeholder = (string?)shape.Descendants(P + "ph").FirstOrDefault()?.Attribute("type");
                var isTitle = placeholder is "title" or "ctrTitle";
                foreach (var paragraph in shape.Descendants(A + "p"))
                {
                    var text = string.Concat(paragraph.Descendants(A + "t").Select(element => element.Value)).Trim();
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var tag = isTitle ? "h2" : "p";
                    html.Append('<').Append(tag).Append('>')
                        .Append(WebUtility.HtmlEncode(budget.TakeText(text)))
                        .Append("</").Append(tag).Append('>');
                }
            }

            html.Append("</section>");
        }

        if (slideIds.Count > MaxSlides)
        {
            html.Append("<p class=\"notice\">Showing the first ")
                .Append(MaxSlides)
                .Append(" of ")
                .Append(slideIds.Count)
                .Append(" slides. Open the file for the rest.</p>");
        }

        AppendTruncation(html, budget);
        html.Append("</div>");

        // Colors are the shell's tokens, never this moment's palette: an
        // open preview follows a theme change, and so must its cards.
        var style = "<style>"
            + ".deck .slide{background:var(--raised);border:1px solid var(--raised-border);border-radius:12px;"
            + "padding:20px 28px 24px;margin:0 0 24px}"
            + ".deck .slide-number{margin:0 0 8px;font-size:11.5px;color:var(--text-3)}"
            + ".deck .slide h2{border:0;margin:0 0 .6em;padding:0}"
            + ".deck .slide img{max-height:240px;width:auto;margin:.4em 0}"
            + "</style>";
        File.WriteAllText(Path.Combine(directory, "page.html"), Shell(html.ToString(), style), Encoding.UTF8);
        return true;
    }

    private static void AppendDrawingTable(StringBuilder html, XElement table, Budget budget)
    {
        html.Append("<table>");
        foreach (var row in table.Elements(A + "tr"))
        {
            html.Append("<tr>");
            foreach (var cell in row.Elements(A + "tc"))
            {
                var text = string.Concat(cell.Descendants(A + "t").Select(element => element.Value));
                html.Append("<td>").Append(WebUtility.HtmlEncode(budget.TakeText(text))).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</table>");
    }

    private static bool WriteWorkbook(ZipArchive archive, string directory, CancellationToken cancellationToken)
    {
        var workbook = LoadXml(archive, "xl/workbook.xml");
        if (workbook is null)
        {
            return false;
        }

        var targets = Relationships(archive, "xl/_rels/workbook.xml.rels", "xl");
        var strings = LoadSharedStrings(archive);
        var all = workbook.Descendants(S + "sheet")
            .Select(sheet => (
                Name: ((string?)sheet.Attribute("name"))?.Trim() is { Length: > 0 } name ? name : "Sheet",
                Target: (string?)sheet.Attribute(R + "id") is { } id && targets.TryGetValue(id, out var target) ? target : null))
            .Where(sheet => sheet.Target is not null)
            .ToList();
        var sheets = all.Take(MaxSheets).ToList();
        if (sheets.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < sheets.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (table, rows, columns) = SheetTable(LoadXml(archive, sheets[index].Target!), strings);

            // One notice per cut, under the sheet it was made in. The tabs
            // are the only place the sheet count shows, so every page says it.
            var notices = new StringBuilder();
            AppendCutNotice(notices, MaxSheetRows, rows, "rows");
            AppendCutNotice(notices, MaxSheetColumns, columns, "columns");
            AppendCutNotice(notices, MaxSheets, all.Count, "sheets");
            var page = SheetHtml.Page(sheets[index].Name, table + notices);
            // The first sheet is the page; a workbook also gets one page per
            // sheet beside it, which is what the tab strip reads.
            if (index == 0)
            {
                File.WriteAllText(Path.Combine(directory, "page.html"), page, Encoding.UTF8);
            }

            if (sheets.Count > 1)
            {
                File.WriteAllText(Path.Combine(directory, $"page.sheet{index}.html"), page, Encoding.UTF8);
            }
        }

        return true;
    }

    /// <summary>
    /// A sheet's used range as a table, values placed by their cell
    /// references, and how many rows and columns of values the sheet has.
    /// The table stops at the row and column limits; the counts do not, so
    /// a cut can be told from a sheet that simply ends.
    /// </summary>
    private static (string Table, int Rows, int Columns) SheetTable(XDocument? sheet, IReadOnlyList<string> strings)
    {
        var rows = new SortedDictionary<int, Dictionary<int, (string Text, bool Number)>>();
        var lastColumn = 0;
        var usedRows = 0;
        var usedColumns = 0;
        var position = 0;
        foreach (var row in sheet?.Descendants(S + "row") ?? [])
        {
            var rowIndex = int.TryParse((string?)row.Attribute("r"), out var r) ? r : position + 1;
            position = rowIndex;
            var cells = new Dictionary<int, (string Text, bool Number)>();
            var nextColumn = 0;
            foreach (var cell in row.Elements(S + "c"))
            {
                var column = ColumnIndex((string?)cell.Attribute("r")) ?? nextColumn;
                nextColumn = column + 1;
                var value = CellValue(cell, strings);
                if (value.Text.Length == 0)
                {
                    continue;
                }

                usedRows = Math.Max(usedRows, rowIndex);
                usedColumns = Math.Max(usedColumns, column + 1);
                if (rowIndex <= MaxSheetRows && column < MaxSheetColumns)
                {
                    cells[column] = value;
                    lastColumn = Math.Max(lastColumn, column);
                }
            }

            if (cells.Count > 0)
            {
                rows[rowIndex] = cells;
            }
        }

        var widths = ColumnWidths(sheet, lastColumn + 1);
        var html = new StringBuilder("<table cellspacing=\"0\" border=\"0\">");
        foreach (var width in widths)
        {
            html.Append("<colgroup width=\"").Append(width).Append("\"></colgroup>");
        }

        var lastRow = rows.Count == 0 ? 0 : rows.Keys.Last();
        for (var rowIndex = 1; rowIndex <= Math.Max(lastRow, 1); rowIndex++)
        {
            html.Append("<tr>");
            rows.TryGetValue(rowIndex, out var cells);
            for (var column = 0; column <= lastColumn; column++)
            {
                if (cells is not null && cells.TryGetValue(column, out var value))
                {
                    html.Append(value.Number ? "<td align=\"right\">" : "<td>")
                        .Append(WebUtility.HtmlEncode(value.Text))
                        .Append("</td>");
                }
                else
                {
                    html.Append("<td></td>");
                }
            }

            html.Append("</tr>");
        }

        html.Append("</table>");
        return (html.ToString(), usedRows, usedColumns);
    }

    /// <summary>
    /// The deck's words for a cut, on a sheet page: that page has no notice
    /// style of its own, so the line takes its color from the page's label
    /// token and lines up with the table above it.
    /// </summary>
    private static void AppendCutNotice(StringBuilder html, int shown, int total, string things)
    {
        if (total > shown)
        {
            html.Append("<p style=\"margin:0 24px 8px;color:var(--text-2)\">Showing the first ")
                .Append(shown.ToString("N0", CultureInfo.InvariantCulture))
                .Append(" of ")
                .Append(total.ToString("N0", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(things)
                .Append(". Open the file for the rest.</p>");
        }
    }

    /// <summary>Column widths in pixels from the sheet's own cols, Excel's default elsewhere.</summary>
    private static List<int> ColumnWidths(XDocument? sheet, int count)
    {
        var widths = Enumerable.Repeat(64, count).ToList();
        foreach (var col in sheet?.Descendants(S + "col") ?? [])
        {
            if (!int.TryParse((string?)col.Attribute("min"), out var min)
                || !int.TryParse((string?)col.Attribute("max"), out var max)
                || !double.TryParse((string?)col.Attribute("width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var characters))
            {
                continue;
            }

            var pixels = (int)Math.Clamp(Math.Round(characters * 7 + 5), 16, 600);
            for (var column = Math.Max(min, 1); column <= Math.Min(max, count); column++)
            {
                widths[column - 1] = pixels;
            }
        }

        return widths;
    }

    private static (string Text, bool Number) CellValue(XElement cell, IReadOnlyList<string> strings)
    {
        var type = (string?)cell.Attribute("t");
        if (type == "inlineStr")
        {
            return (string.Concat(cell.Descendants(S + "t").Select(text => text.Value)), false);
        }

        var raw = cell.Element(S + "v")?.Value ?? string.Empty;
        return type switch
        {
            "s" when int.TryParse(raw, out var index) && index >= 0 && index < strings.Count => (strings[index], false),
            "b" => (raw == "1" ? "TRUE" : "FALSE", false),
            "str" or "e" => (raw, false),
            _ => (raw, raw.Length > 0)
        };
    }

    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        var column = 0;
        var letters = 0;
        foreach (var character in reference)
        {
            if (character is < 'A' or > 'Z')
            {
                break;
            }

            column = column * 26 + (character - 'A' + 1);
            letters++;
        }

        return letters == 0 ? null : column - 1;
    }

    private static List<string> LoadSharedStrings(ZipArchive archive) =>
        LoadXml(archive, "xl/sharedStrings.xml")?
            .Descendants(S + "si")
            .Select(item => string.Concat(item.Descendants(S + "t").Select(text => text.Value)))
            .ToList()
        ?? [];

    private static void AppendImage(
        StringBuilder html,
        ZipArchive archive,
        IReadOnlyDictionary<string, string> targets,
        string? relationshipId,
        Budget budget)
    {
        if (relationshipId is null || !targets.TryGetValue(relationshipId, out var target))
        {
            return;
        }

        var type = Path.GetExtension(target).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            _ => null
        };
        var entry = archive.GetEntry(target);
        if (type is null || entry is null || entry.Length <= 0 || entry.Length > MaxImageBytes || !budget.TakeImage(entry.Length))
        {
            return;
        }

        // The zip's length is a claim: read one byte past it to catch a lie.
        using var stream = entry.Open();
        var bytes = new byte[entry.Length + 1];
        var length = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        if (length != entry.Length)
        {
            return;
        }

        html.Append("<img alt=\"\" src=\"data:").Append(type).Append(";base64,")
            .Append(System.Convert.ToBase64String(bytes, 0, length))
            .Append("\">");
    }

    /// <summary>Relationship id to package path, for the parts inside the package only.</summary>
    private static Dictionary<string, string> Relationships(ZipArchive archive, string relsPath, string baseFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var relationship in LoadXml(archive, relsPath)?.Descendants(PackageRel + "Relationship") ?? [])
        {
            var id = (string?)relationship.Attribute("Id");
            var target = (string?)relationship.Attribute("Target");
            if (id is null
                || string.IsNullOrWhiteSpace(target)
                || string.Equals((string?)relationship.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result[id] = NormalizeZipPath(baseFolder, target);
        }

        return result;
    }

    private static string RelationshipsPathFor(string partPath)
    {
        var folder = Path.GetDirectoryName(partPath)?.Replace('\\', '/') ?? string.Empty;
        return $"{folder}/_rels/{Path.GetFileName(partPath)}.rels";
    }

    private static string NormalizeZipPath(string root, string target)
    {
        target = target.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var part in (target.StartsWith('/') ? target : $"{root}/{target}").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "..")
            {
                if (parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
            }
            else if (part != ".")
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    private static XDocument? LoadXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null || entry.Length <= 0)
        {
            return null;
        }

        if (entry.Length > MaxXmlEntryBytes)
        {
            throw new InvalidDataException("Office XML entry is too large for preview.");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxXmlEntryBytes
        };
        return XmlNesting.Load(entry.Open, settings, LoadOptions.None);
    }

    private static bool IsOn(XElement? toggle) =>
        toggle is not null && (string?)toggle.Attribute(W + "val") is not ("0" or "false" or "none");

    private static void AppendTruncation(StringBuilder html, Budget budget)
    {
        if (budget.TextExhausted)
        {
            html.Append("<p class=\"notice\">Showing the beginning of this file. Open it for the rest.</p>");
        }
    }

    /// <summary>
    /// The generated-page shell. Tables leave the reading column for the
    /// window's width, centered, because Office tables are laid out for a
    /// page, not a paragraph.
    /// </summary>
    private static string Shell(string body, string style)
    {
        var tables = "<style>"
            + ".office table{position:relative;left:50%;transform:translateX(-50%);width:max-content;max-width:calc(100vw - 64px)}"
            + ".office td{border:1px solid var(--raised-border)}"
            + "</style>";
        return GeneratedHtmlPreview.BuildShell(tables + style + body);
    }

    // The page carries both palettes, so one entry serves either theme.
    private static string CacheDirectory(string path, OfficeKind kind)
    {
        var file = new FileInfo(path);
        var input = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{kind}|{PageVersion}";
        var hash = System.Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
        return Path.Combine(AppPaths.DataRoot, "DocumentCache", "f-" + hash);
    }

    /// <summary>One page's share of text and pictures, so a big file stays a readable page.</summary>
    private sealed class Budget
    {
        private int _characters = MaxTextCharacters;
        private long _imageBytes = MaxBookImageBytes;

        public bool TextExhausted => _characters <= 0;

        public string TakeText(string text)
        {
            if (_characters <= 0)
            {
                return string.Empty;
            }

            var taken = text.Length <= _characters ? text : text[.._characters];
            _characters -= taken.Length;
            return taken;
        }

        public bool TakeImage(long bytes)
        {
            if (bytes > _imageBytes)
            {
                return false;
            }

            _imageBytes -= bytes;
            return true;
        }
    }
}
