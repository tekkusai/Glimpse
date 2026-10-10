using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>One sheet of a workbook page set: display name and its own page.</summary>
internal sealed record SpreadsheetSheet(string Name, string Path);

/// <summary>
/// Calc's HTML export, made to read like a spreadsheet: paper cells on the
/// app canvas, sticky column letters and row numbers, one page per sheet.
/// Text in, files out; no engine needed, so the fallback reader writes its
/// tables through the same page.
/// </summary>
internal static partial class SheetHtml
{
    private const int GutterWidth = 40;

    /// <summary>
    /// The page's chrome in both themes. The cache stays theme-neutral:
    /// prefers-color-scheme picks the palette, and the preview sets the
    /// browser's scheme from the app theme. Cells stay paper in both,
    /// because a sheet's own colors were authored for white.
    /// </summary>
    internal static readonly string Style = BuildStyle(Theme.Light, Theme.Dark);

    public static void Normalize(string htmlPath)
    {
        try
        {
            var html = File.ReadAllText(htmlPath);
            var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = headEnd >= 0 ? html.Insert(headEnd, Style) : Style + html;

            // Images linked from another machine export as a 32x32 generic
            // placeholder; Calc draws those as nothing and so do we. Keeping
            // only real local files also means a sheet never fetches.
            var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
            html = ImageTagRegex().Replace(html, match =>
            {
                var src = ImageSourceRegex().Match(match.Value).Groups[1].Value;
                if (src.Length == 0 || src.Contains("://", StringComparison.Ordinal) || src.StartsWith("//", StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                try
                {
                    var file = new FileInfo(Path.Combine(directory, Uri.UnescapeDataString(src)));
                    return file.Exists && file.Length >= 256 ? match.Value : string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            });

            html = AddSpreadsheetChrome(html);
            File.WriteAllText(htmlPath, html);
            WriteSheetPages(htmlPath, html);
        }
        catch
        {
            // The raw export is still a valid preview.
        }
    }

    /// <summary>
    /// A sheet page around one table, in the same chrome as a conversion.
    /// Cells read in the system face; a conversion keeps the sheet's own.
    /// </summary>
    public static string Page(string title, string table) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>"
        + WebUtility.HtmlEncode(title)
        + "</title>"
        + Style
        + "<style>body{font-family:\"Segoe UI Variable Text\",\"Segoe UI\",sans-serif}</style>"
        + "</head><body><div id=\"sp-wrap\">"
        + DecorateSheetTable(table)
        + "</div></body></html>";

    /// <summary>
    /// The sheet pages a normalized export left next to itself, in order,
    /// named from their titles. Null when there is only one sheet.
    /// </summary>
    public static IReadOnlyList<SpreadsheetSheet>? TryReadSheetList(string htmlPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(htmlPath);
            var pages = new SortedList<int, SpreadsheetSheet>();
            var head = new char[2048];
            foreach (var file in Directory.GetFiles(directory, baseName + ".sheet*.html"))
            {
                var match = SheetPageNameRegex().Match(Path.GetFileName(file));
                if (!match.Success)
                {
                    continue;
                }

                using var reader = new StreamReader(file);
                Array.Clear(head);
                var read = reader.ReadBlock(head, 0, head.Length);
                var title = TitleRegex().Match(new string(head, 0, read));
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var name = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value) : $"Sheet {index + 1}";
                pages[index] = new SpreadsheetSheet(name, file);
            }

            return pages.Count >= 2 ? pages.Values.ToList() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string BuildStyle(ThemePalette light, ThemePalette dark)
    {
        static string Css(Color color) => color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : string.Create(CultureInfo.InvariantCulture, $"rgba({color.R},{color.G},{color.B},{color.A / 255.0:0.###})");

        static string Alpha(Color color, double alpha) =>
            string.Create(CultureInfo.InvariantCulture, $"rgba({color.R},{color.G},{color.B},{alpha:0.##})");

        static string Palette(ThemePalette p) =>
            $"--canvas:{Css(p.CanvasBg)};--chrome:{Css(p.ChromeBg)};--edge:{Css(p.RaisedBorder)};"
            + $"--label:{Css(p.TextSecondary)};--thumb:{Alpha(p.ScrollThumb, 0.6)};--thumb-hover:{Alpha(p.ScrollThumb, 0.95)};"
            + $"--select:{Alpha(p.Accent, 0.3)}";

        // Paper is the light palette's: cells keep their authored colors.
        var paper = light;
        return "<style>"
            + $":root{{color-scheme:light;{Palette(light)}}}"
            + $"@media (prefers-color-scheme:dark){{:root{{color-scheme:dark;{Palette(dark)}}}}}"
            // The one reveal: the page dissolves over the canvas, every tab too.
            + "html{animation:glimpse-in 160ms cubic-bezier(.2,0,0,1) both}"
            + "@keyframes glimpse-in{from{opacity:0}}"
            + "@media (prefers-reduced-motion:reduce){html{animation:none}}"
            + "html,body{height:100%;margin:0;overflow:hidden;background:var(--canvas)}"
            // Calc exports x-small text against columns sized for 11 pt.
            + "body,div,table,thead,tbody,tfoot,tr,th,td,p{font-size:13px}"
            + "::selection{background:var(--select)}"
            + "#sp-wrap{height:100%;overflow:auto}"
            + "#sp-wrap::-webkit-scrollbar{width:11px;height:11px}"
            + "#sp-wrap::-webkit-scrollbar-button{display:none}"
            + "#sp-wrap::-webkit-scrollbar-track,#sp-wrap::-webkit-scrollbar-corner{background:transparent}"
            + "#sp-wrap::-webkit-scrollbar-thumb{background:var(--thumb);border:2px solid transparent;border-radius:6px;background-clip:padding-box;min-height:30px}"
            + "#sp-wrap::-webkit-scrollbar-thumb:hover{background:var(--thumb-hover);background-clip:padding-box}"
            + $"table{{background:{Css(paper.RaisedBg)};color:{Css(paper.TextPrimary)};margin:24px;outline:1px solid var(--edge)}}"
            + $"td,th{{padding:3px 8px;box-shadow:inset -1px -1px 0 {Css(paper.Border)}}}"
            + $"a{{color:{Css(paper.Accent)}}}"
            // Cell comments export as inline text; Excel shows a red corner.
            + "comment{display:none}"
            + "a.comment-indicator{float:right;width:0;height:0;border-top:6px solid #C42B1C;border-left:6px solid transparent}"
            + "td.sp-h{position:sticky;background:var(--chrome);color:var(--label);"
            + "font:400 11.5px \"Segoe UI Variable Text\",\"Segoe UI\",sans-serif;"
            + "text-align:center;vertical-align:middle;padding:3px 6px;box-shadow:inset -1px -1px 0 var(--edge);user-select:none}"
            + "td.sp-cl{top:0;z-index:3}"
            + "td.sp-rn{left:0;z-index:2}"
            + "td.sp-corner{top:0;left:0;z-index:4}"
            + "</style>";
    }

    /// <summary>
    /// A multi-sheet workbook becomes one page per sheet, so the preview
    /// shows a tab strip instead of the export's stacked tables and its
    /// "Overview" link block. Each page carries its sheet name as its title.
    /// </summary>
    private static void WriteSheetPages(string htmlPath, string html)
    {
        var tables = new List<string>();
        var position = 0;
        while (true)
        {
            var start = html.IndexOf("<table", position, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
            {
                break;
            }

            var end = html.IndexOf("</table>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            tables.Add(html[start..(end + "</table>".Length)]);
            position = end + "</table>".Length;
        }

        if (tables.Count < 2)
        {
            return;
        }

        // The export names each sheet in a heading right before its table.
        var names = SheetHeadingRegex().Matches(html).Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToList();
        var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(htmlPath);
        for (var i = 0; i < tables.Count; i++)
        {
            var name = i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Sheet {i + 1}";
            // The tables already carry their chrome from the full page.
            var page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>"
                + WebUtility.HtmlEncode(name)
                + "</title>"
                + Style
                + "</head><body><div id=\"sp-wrap\">"
                + tables[i]
                + "</div></body></html>";
            File.WriteAllText(Path.Combine(directory, $"{baseName}.sheet{i}.html"), page);
        }
    }

    /// <summary>
    /// Wraps the document in the scroll container and gives every sheet
    /// table sticky column letters and row numbers.
    /// </summary>
    private static string AddSpreadsheetChrome(string html)
    {
        var bodyOpen = BodyOpenRegex().Match(html);
        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyOpen.Success && bodyClose > bodyOpen.Index)
        {
            html = html.Insert(bodyClose, "</div>")
                       .Insert(bodyOpen.Index + bodyOpen.Length, "<div id=\"sp-wrap\">");
        }

        var builder = new StringBuilder(html.Length + html.Length / 8);
        var position = 0;
        while (true)
        {
            var tableStart = html.IndexOf("<table", position, StringComparison.OrdinalIgnoreCase);
            var tableEnd = tableStart < 0 ? -1 : html.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
            if (tableEnd < 0)
            {
                builder.Append(html, position, html.Length - position);
                break;
            }

            builder.Append(html, position, tableStart - position);
            builder.Append(DecorateSheetTable(html[tableStart..(tableEnd + "</table>".Length)]));
            position = tableEnd + "</table>".Length;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Column letters and row numbers, counted from the table's own colgroup
    /// declarations. Their widths feed a fixed layout, so a large sheet does
    /// not renegotiate its columns while it parses and overflow into empty
    /// neighbors behaves like Calc's screen.
    /// </summary>
    internal static string DecorateSheetTable(string table)
    {
        var columns = 0;
        var totalWidth = 0;
        var widthsComplete = true;
        foreach (Match colgroup in ColgroupRegex().Matches(table))
        {
            var attributes = colgroup.Groups[1].Value;
            var span = SpanRegex().Match(attributes);
            var count = span.Success ? int.Parse(span.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
            columns += count;
            var width = WidthRegex().Match(attributes);
            if (width.Success)
            {
                totalWidth += count * int.Parse(width.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                widthsComplete = false;
            }
        }

        var firstRow = table.IndexOf("<tr", StringComparison.OrdinalIgnoreCase);
        var openTagEnd = table.IndexOf('>');
        if (columns == 0 || columns > 1024 || firstRow < 0 || openTagEnd < 0 || openTagEnd > firstRow)
        {
            return table;
        }

        var header = new StringBuilder("\t<tr>");
        header.Append("<td class=\"sp-h sp-corner\"></td>");
        for (var column = 0; column < columns; column++)
        {
            header.Append("<td class=\"sp-h sp-cl\">").Append(ColumnLetters(column)).Append("</td>");
        }

        header.Append("</tr>\n");

        var row = 0;
        var body = RowOpenRegex().Replace(table[firstRow..], match =>
        {
            row++;
            return match.Value + "<td class=\"sp-h sp-rn\">" + row.ToString(CultureInfo.InvariantCulture) + "</td>";
        });

        var layout = widthsComplete
            ? $" style=\"table-layout:fixed;width:{(totalWidth + GutterWidth).ToString(CultureInfo.InvariantCulture)}px\""
            : string.Empty;

        // The gutter takes its own colgroup so the export's widths stay
        // aligned with their columns.
        return table[..openTagEnd]
            + layout
            + $">\n\t<colgroup width=\"{GutterWidth}\"></colgroup>"
            + table[(openTagEnd + 1)..firstRow]
            + header
            + body;
    }

    internal static string ColumnLetters(int index)
    {
        var letters = string.Empty;
        index++;
        while (index > 0)
        {
            index--;
            letters = (char)('A' + index % 26) + letters;
            index /= 26;
        }

        return letters;
    }

    [GeneratedRegex("<img[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex("src=\"([^\"]*)\"")]
    private static partial Regex ImageSourceRegex();

    [GeneratedRegex("\\.sheet(\\d+)\\.html$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetPageNameRegex();

    [GeneratedRegex("<title>(.*?)</title>", RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<h1>Sheet \\d+: <em>(.*?)</em></h1>", RegexOptions.IgnoreCase)]
    private static partial Regex SheetHeadingRegex();

    [GeneratedRegex("<body[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyOpenRegex();

    [GeneratedRegex("<colgroup([^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex ColgroupRegex();

    [GeneratedRegex("span=\"?(\\d+)")]
    private static partial Regex SpanRegex();

    [GeneratedRegex("width=\"?(\\d+)")]
    private static partial Regex WidthRegex();

    [GeneratedRegex("<tr[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex RowOpenRegex();
}
