using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

internal sealed record ConvertedOfficeDocument(
    string DocumentPath,
    string EngineLog,
    IReadOnlyList<SpreadsheetSheet>? Sheets = null,
    string? SourcePath = null,
    bool IsPartial = false,
    int? PartialPageCount = null,
    int? ExpectedPageCount = null);

/// <summary>One sheet of a converted workbook: display name + its own page.</summary>
internal sealed record SpreadsheetSheet(string Name, string Path);

internal sealed record OfficeProgressiveConversionPlan(int FirstPageCount, int ExpectedPageCount, string Filter);

internal static class OfficeDocumentPdfConverter
{
    private const int ConversionTimeoutSeconds = 75;
    private const int ProgressiveFirstPages = 6;
    private const int ProgressivePresentationSlideThreshold = 10;
    private const int ProgressiveDocumentPageThreshold = 25;
    private const long ProgressiveDocumentSizeThreshold = 1_500_000;
    private const long MaxAppPropertiesBytes = 256 * 1024;
    private const string BlockedProxyHost = "127.0.0.1";
    private const int BlockedProxyPort = 9;
    private static readonly XNamespace OfficeRegistry = "http://openoffice.org/2001/registry";
    private static readonly SemaphoreSlim ConversionLane = new(1, 1);

    /// <summary>Whether the conversion engine exists on this machine.</summary>
    public static bool IsAvailable => FindSoffice() is not null;

    public static string? EnginePath => FindSoffice();

    public static string EngineName
    {
        get
        {
            var path = FindSoffice();
            if (path is null)
            {
                return "Basic previews";
            }

            return path.Contains("OpenOffice", StringComparison.OrdinalIgnoreCase)
                ? "OpenOffice"
                : "LibreOffice";
        }
    }

    /// <summary>
    /// The engine is present but has never completed a conversion or warm-up
    /// as THIS installation, so the next conversion pays LibreOffice's
    /// one-time initialization (~15s). The profile folder alone cannot tell:
    /// it survives a LibreOffice uninstall, so a reinstall or upgrade must
    /// count as a first run again. The marker records the engine's identity.
    /// </summary>
    public static bool IsFirstRunPending
    {
        get
        {
            var identity = GetEngineIdentity();
            if (identity is null)
            {
                return false;
            }

            if (!Directory.Exists(GetProfileDirectory()))
            {
                return true;
            }

            try
            {
                var markerPath = GetEngineMarkerPath();
                return !File.Exists(markerPath) || File.ReadAllText(markerPath) != identity;
            }
            catch
            {
                return false;
            }
        }
    }

    private static string GetEngineMarkerPath() => Path.Combine(
        AppPaths.DataRoot,
        "EngineWarmed.txt");

    private static string? GetEngineIdentity()
    {
        var soffice = FindSoffice();
        if (soffice is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(soffice);
            return $"{soffice}|{info.CreationTimeUtc.Ticks}|{info.Length}";
        }
        catch
        {
            return soffice;
        }
    }

    /// <summary>
    /// True after the first real conversion this session. Until then the
    /// engine's process start makes every conversion predictably slow, so
    /// the preview names the wait early instead of spinning in silence.
    /// </summary>
    public static bool HasConvertedThisSession { get; private set; }

    public static OfficeProgressiveConversionPlan? TryCreateProgressivePlan(string path)
    {
        if (FileKinds.IsSpreadsheet(path))
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        var size = SafeFileLength(path);
        if (IsPresentation(extension))
        {
            var slides = TryReadExtendedPropertyCount(path, "Slides");
            if (slides > ProgressivePresentationSlideThreshold
                || (slides is null && size > ProgressiveDocumentSizeThreshold))
            {
                return new OfficeProgressiveConversionPlan(
                    ProgressiveFirstPages,
                    Math.Max(slides ?? ProgressiveFirstPages + 1, ProgressiveFirstPages + 1),
                    BuildPageRangeFilter(extension, ProgressiveFirstPages));
            }
        }

        if (IsWriterDocument(extension))
        {
            var pages = TryReadExtendedPropertyCount(path, "Pages");
            if (pages > ProgressiveDocumentPageThreshold && size > ProgressiveDocumentSizeThreshold)
            {
                return new OfficeProgressiveConversionPlan(
                    ProgressiveFirstPages,
                    Math.Max(pages.Value, ProgressiveFirstPages + 1),
                    BuildPageRangeFilter(extension, ProgressiveFirstPages));
            }
        }

        return null;
    }

    public static ConvertedOfficeDocument? TryGetCachedPdfConversion(string path)
    {
        if (FileKinds.IsSpreadsheet(path))
        {
            return null;
        }

        return TryGetCachedOutput(path, "pdf");
    }

    private static void MarkEngineWarmed()
    {
        try
        {
            if (GetEngineIdentity() is { } identity)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(GetEngineMarkerPath())!);
                File.WriteAllText(GetEngineMarkerPath(), identity);
            }
        }
        catch
        {
            // Worst case the first-run message shows once more.
        }
    }

    public static ConvertedOfficeDocument? TryConvertFirstPages(
        string path,
        OfficeProgressiveConversionPlan plan,
        CancellationToken cancellationToken)
    {
        var soffice = FindSoffice();
        if (soffice is null)
        {
            DiagnosticLog.Write("office progressive skipped: compatible soffice.exe not found");
            return null;
        }

        var tempDirectory = Path.Combine(
            AppPaths.DataRoot,
            "DocumentCache",
            ".partial-" + Guid.NewGuid().ToString("N"));

        try
        {
            ConversionLane.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryDeleteDirectory(tempDirectory);
            return null;
        }

        try
        {
            var converted = RunConversion(
                soffice,
                path,
                plan.Filter,
                cancellationToken,
                background: false,
                outputDirectory: tempDirectory,
                useCache: false);
            if (converted is null)
            {
                TryDeleteDirectory(tempDirectory);
                return null;
            }

            var outputPages = CountPdfPages(converted.DocumentPath, cancellationToken);
            if (outputPages is null)
            {
                TryDeleteDirectory(tempDirectory);
                return null;
            }

            if (outputPages.Value != plan.FirstPageCount)
            {
                var promoted = PromotePdfToCache(path, converted.DocumentPath, converted.EngineLog);
                TryDeleteDirectory(tempDirectory);
                return promoted;
            }

            DiagnosticLog.Write(
                $"office progressive partial ready {path} pages={outputPages.Value}/{plan.ExpectedPageCount}");
            return converted with
            {
                SourcePath = path,
                IsPartial = true,
                PartialPageCount = outputPages.Value,
                ExpectedPageCount = plan.ExpectedPageCount
            };
        }
        finally
        {
            ConversionLane.Release();
        }
    }

    public static ConvertedOfficeDocument? TryConvert(string path, CancellationToken cancellationToken, bool background = false)
    {
        var soffice = FindSoffice();
        if (soffice is null)
        {
            DiagnosticLog.Write("office conversion skipped: compatible soffice.exe not found");
            return null;
        }

        try
        {
            ConversionLane.Wait(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            if (FileKinds.IsSpreadsheet(path))
            {
                // Calc's print/PDF pipeline paginates wide sheets into strips and
                // clips text that overflows into empty neighbour cells. The HTML
                // export renders the sheet the way Calc shows it on screen.
                return RunConversion(soffice, path, "html", cancellationToken, background)
                    ?? RunConversion(soffice, path, "pdf", cancellationToken, background);
            }

            return RunConversion(soffice, path, "pdf", cancellationToken, background);
        }
        finally
        {
            ConversionLane.Release();
        }
    }

    private static ConvertedOfficeDocument? TryGetCachedOutput(string path, string convertTo)
    {
        var outputExtension = convertTo.StartsWith("html", StringComparison.OrdinalIgnoreCase) ? ".html" : ".pdf";
        var cacheDirectory = GetCacheDirectory(path, convertTo);
        var expectedOutputPath = Path.Combine(cacheDirectory, Path.GetFileNameWithoutExtension(path) + outputExtension);
        if (!File.Exists(expectedOutputPath) || new FileInfo(expectedOutputPath).Length <= 0)
        {
            return null;
        }

        try
        {
            Directory.SetLastWriteTimeUtc(cacheDirectory, DateTime.UtcNow);
        }
        catch
        {
            // Timestamp refresh is best-effort.
        }

        return new ConvertedOfficeDocument(
            expectedOutputPath,
            "cache hit",
            expectedOutputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? TryReadSheetList(expectedOutputPath) : null);
    }

    private static ConvertedOfficeDocument? PromotePdfToCache(string sourcePath, string convertedPath, string engineLog)
    {
        try
        {
            var cacheDirectory = GetCacheDirectory(sourcePath, "pdf");
            Directory.CreateDirectory(cacheDirectory);
            foreach (var stale in Directory.EnumerateFiles(cacheDirectory, "*.pdf"))
            {
                TryDelete(stale);
            }

            var target = Path.Combine(cacheDirectory, Path.GetFileNameWithoutExtension(sourcePath) + ".pdf");
            File.Copy(convertedPath, target, overwrite: true);
            DiagnosticLog.Write($"office progressive promoted {sourcePath}");
            return new ConvertedOfficeDocument(target, engineLog);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office progressive promote failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static string GetProfileDirectory() => Path.Combine(
        AppPaths.DataRoot,
        "LibreOfficeProfile");

    internal static void PrepareNetworkIsolatedProfile(string profileDirectory)
    {
        var userDirectory = Path.Combine(profileDirectory, "user");
        Directory.CreateDirectory(userDirectory);
        var settingsPath = Path.Combine(userDirectory, "registrymodifications.xcu");

        XDocument document;
        if (File.Exists(settingsPath))
        {
            document = XDocument.Load(settingsPath, LoadOptions.PreserveWhitespace);
        }
        else
        {
            document = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XElement(
                    OfficeRegistry + "items",
                    new XAttribute(XNamespace.Xmlns + "oor", OfficeRegistry)));
        }

        var root = document.Root ?? throw new InvalidDataException("LibreOffice profile has no settings root.");
        const string settingsNode = "/org.openoffice.Inet/Settings";
        var values = new (string Name, string Value)[]
        {
            ("ooInetProxyType", "2"),
            ("ooInetHTTPProxyName", BlockedProxyHost),
            ("ooInetHTTPProxyPort", BlockedProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ooInetHTTPSProxyName", BlockedProxyHost),
            ("ooInetHTTPSProxyPort", BlockedProxyPort.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ooInetNoProxy", string.Empty)
        };

        var protectedNames = values.Select(value => value.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in root.Elements("item")
                     .Where(item => (string?)item.Attribute(OfficeRegistry + "path") == settingsNode)
                     .ToList())
        {
            foreach (var property in item.Elements("prop")
                         .Where(property => protectedNames.Contains((string?)property.Attribute(OfficeRegistry + "name") ?? string.Empty))
                         .ToList())
            {
                property.Remove();
            }

            if (!item.Elements().Any())
            {
                item.Remove();
            }
        }

        var networkItem = new XElement("item", new XAttribute(OfficeRegistry + "path", settingsNode));
        foreach (var (name, value) in values)
        {
            networkItem.Add(
                new XElement(
                    "prop",
                    new XAttribute(OfficeRegistry + "name", name),
                    new XAttribute(OfficeRegistry + "op", "fuse"),
                    new XElement("value", value)));
        }

        root.Add(networkItem);
        var tempPath = settingsPath + ".tmp";
        document.Save(tempPath);
        File.Move(tempPath, settingsPath, overwrite: true);
    }

    private static ConvertedOfficeDocument? RunConversion(
        string soffice,
        string path,
        string convertTo,
        CancellationToken cancellationToken,
        bool background,
        string? outputDirectory = null,
        bool useCache = true)
    {
        var outputExtension = convertTo.StartsWith("html", StringComparison.OrdinalIgnoreCase) ? ".html" : ".pdf";
        var cacheDirectory = outputDirectory ?? GetCacheDirectory(path, convertTo);
        Directory.CreateDirectory(cacheDirectory);
        var expectedOutputPath = Path.Combine(cacheDirectory, Path.GetFileNameWithoutExtension(path) + outputExtension);
        if (useCache && File.Exists(expectedOutputPath) && new FileInfo(expectedOutputPath).Length > 0)
        {
            // Refresh the entry's clock so housekeeping's LRU stays current.
            try
            {
                Directory.SetLastWriteTimeUtc(cacheDirectory, DateTime.UtcNow);
            }
            catch
            {
                // Timestamp refresh is best-effort.
            }

            return new ConvertedOfficeDocument(
                expectedOutputPath,
                "cache hit",
                expectedOutputPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? TryReadSheetList(expectedOutputPath) : null);
        }

        foreach (var stale in Directory.EnumerateFiles(cacheDirectory, "*" + outputExtension))
        {
            TryDelete(stale);
        }

        var profileDirectory = GetProfileDirectory();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(ConversionTimeoutSeconds));

        try
        {
            using var preparedSource = OfficePreviewSourceSanitizer.Prepare(
                path,
                cacheDirectory,
                timeout.Token);
            if (preparedSource.ExternalRelationshipsRemoved > 0)
            {
                DiagnosticLog.Write(
                    $"office external relationships removed count={preparedSource.ExternalRelationshipsRemoved} {path}");
            }

            PrepareNetworkIsolatedProfile(profileDirectory);
            using var process = new Process();
            process.StartInfo.FileName = soffice;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            var blockedProxy = $"http://{BlockedProxyHost}:{BlockedProxyPort}";
            foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            {
                process.StartInfo.Environment[key] = blockedProxy;
            }

            process.StartInfo.Environment["NO_PROXY"] = string.Empty;
            process.StartInfo.Environment["no_proxy"] = string.Empty;
            process.StartInfo.ArgumentList.Add("--headless");
            process.StartInfo.ArgumentList.Add("--nologo");
            process.StartInfo.ArgumentList.Add("--nodefault");
            process.StartInfo.ArgumentList.Add("--nofirststartwizard");
            process.StartInfo.ArgumentList.Add("--norestore");
            process.StartInfo.ArgumentList.Add("--nolockcheck");
            process.StartInfo.ArgumentList.Add("-env:UserInstallation=" + new Uri(profileDirectory + Path.DirectorySeparatorChar).AbsoluteUri);
            process.StartInfo.ArgumentList.Add("--convert-to");
            process.StartInfo.ArgumentList.Add(convertTo);
            process.StartInfo.ArgumentList.Add("--outdir");
            process.StartInfo.ArgumentList.Add(cacheDirectory);
            process.StartInfo.ArgumentList.Add(preparedSource.Path);

            DiagnosticLog.Write($"office conversion start {path}");
            process.Start();
            if (background)
            {
                try
                {
                    process.PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal;
                }
                catch
                {
                    // Priority is best effort; conversion still stays cancellable.
                }
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Disposing a Process does not terminate the child; an orphaned
                // soffice.exe would keep converting (and hold its profile)
                // long after the preview moved on.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // It may have exited between the cancel and the kill.
                }

                throw;
            }

            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            var log = (output + Environment.NewLine + error).Trim();

            if (process.ExitCode != 0)
            {
                DiagnosticLog.Write($"office conversion failed exit={process.ExitCode}: {log}");
                return null;
            }

            var outputPath = File.Exists(expectedOutputPath)
                ? expectedOutputPath
                : Directory.EnumerateFiles(cacheDirectory, "*" + outputExtension).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();

            if (outputPath is null || new FileInfo(outputPath).Length == 0)
            {
                DiagnosticLog.Write($"office conversion produced no output: {log}");
                return null;
            }

            if (outputExtension == ".html")
            {
                NormalizeExportedHtml(outputPath);
            }

            MarkEngineWarmed();
            HasConvertedThisSession = true;
            DiagnosticLog.Write($"office conversion ok {outputPath}");
            return new ConvertedOfficeDocument(
                outputPath,
                log,
                outputExtension == ".html" ? TryReadSheetList(outputPath) : null);
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Write($"office conversion timed out/cancelled {path}");
            return null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office conversion exception {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Calc's HTML export defaults to cramped, dated typography on a bare
    /// white page. Normalization makes it work like a spreadsheet in a viewer:
    /// the sheet sits on the app's canvas (following the system theme, like
    /// the Chromium PDF viewer), the page scrolls inside a viewport-height
    /// container so both scrollbars always live at the preview's edges (a
    /// wide sheet must not bury its horizontal bar at the bottom of a tall
    /// document), and every sheet table gains sticky row numbers and column
    /// letters. Pure CSS + markup: the export stays script-free. Fonts the
    /// sheet chose for itself stay untouched (they arrive as inline
    /// attributes).
    /// </summary>
    // Canvas colours mirror Theme.DeepBg (light E7E8EC / dark 121316);
    // header chrome mirrors the badge palette.
    private const string SheetStyle = "<style>"
                + ":root{color-scheme:light}"
                // Each sheet page (and every tab switch) resolves onto the
                // canvas instead of popping; pairs with the preview's fade-out.
                + "html{animation:sp-reveal 160ms ease-out}"
                + "@keyframes sp-reveal{from{opacity:0}}"
                + "html,body{height:100%;margin:0;overflow:hidden}"
                + "body{background:#E7E8EC;font-family:\"Segoe UI\",system-ui,sans-serif}"
                + "#sp-wrap{height:100%;overflow:auto}"
                // A sheet viewer keeps its scrollbars: slim, themed, always
                // grabbable (overlay bars vanish mid-reach and hover in the
                // wrong color scheme).
                + "#sp-wrap::-webkit-scrollbar{width:12px;height:12px}"
                + "#sp-wrap::-webkit-scrollbar-button{display:none}"
                + "#sp-wrap::-webkit-scrollbar-track,#sp-wrap::-webkit-scrollbar-corner{background:transparent}"
                + "#sp-wrap::-webkit-scrollbar-thumb{background:#B4B8C2;border-radius:6px;border:3px solid #E7E8EC;background-clip:padding-box}"
                + "#sp-wrap::-webkit-scrollbar-thumb:hover{background:#9AA0AB}"
                + "table{background:#fff;color:#000;box-shadow:0 1px 12px rgba(0,0,0,.20);margin:24px}"
                + "td,th{padding:2px 8px}"
                // Cell comments export as inline <comment> text; Excel shows a
                // red corner marker instead. Match that.
                + "comment{display:none}"
                + "a.comment-indicator{float:right;width:0;height:0;border-top:6px solid #C42B1C;border-left:6px solid transparent}"
                + "td.sp-h{position:sticky;background:#F3F3F6;color:#5C6270;font:600 10.5px \"Segoe UI\",sans-serif;"
                + "text-align:center;vertical-align:middle;padding:2px 7px;box-shadow:inset 0 0 0 .5px #D8DADF}"
                + "td.sp-cl{top:0;z-index:3}"
                + "td.sp-rn{left:0;z-index:2;min-width:26px}"
                + "td.sp-corner{top:0;left:0;z-index:4}"
                + "@media (prefers-color-scheme:dark){:root{color-scheme:dark}"
                + "body{background:#121316}table{box-shadow:0 2px 16px rgba(0,0,0,.55)}"
                + "td.sp-h{background:#26282F;color:#A9ADB8;box-shadow:inset 0 0 0 .5px #4B4E58}"
                + "#sp-wrap::-webkit-scrollbar-thumb{background:#5C616E;border-color:#121316}"
                + "#sp-wrap::-webkit-scrollbar-thumb:hover{background:#6E7480}}"
                + "</style>";

    private static void NormalizeExportedHtml(string htmlPath)
    {
        try
        {
            var html = File.ReadAllText(htmlPath);
            var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = headEnd >= 0 ? html.Insert(headEnd, SheetStyle) : SheetStyle + html;

            // Sheets often carry image anchors whose pictures were not included
            // into the export (images linked from another machine export as
            // a 32x32 generic placeholder). Calc renders those as nothing;
            // the preview should too. Keeping only real local files also
            // guarantees a preview does not fetch remote images.
            var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
            html = System.Text.RegularExpressions.Regex.Replace(html, "<img[^>]*>", match =>
            {
                var src = System.Text.RegularExpressions.Regex.Match(match.Value, "src=\"([^\"]*)\"").Groups[1].Value;
                if (src.Length == 0 || src.Contains("://") || src.StartsWith("//", StringComparison.Ordinal))
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
    /// A multi-sheet workbook becomes one page per sheet, so the preview can
    /// show a tab strip instead of the export's stacked tables and its
    /// "Overview" link block. Each page carries its sheet name in its title.
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
        var names = System.Text.RegularExpressions.Regex.Matches(
                html, "<h1>Sheet \\d+: <em>(.*?)</em></h1>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(match => match.Groups[1].Value)
            .ToList();

        var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(htmlPath);
        for (var i = 0; i < tables.Count; i++)
        {
            var name = i < names.Count && !string.IsNullOrWhiteSpace(names[i]) ? names[i] : $"Sheet {i + 1}";
            var page = "<!doctype html><html><head><meta charset=\"utf-8\"><title>"
                + name
                + "</title>"
                + SheetStyle
                + "</head><body><div id=\"sp-wrap\">"
                + tables[i]
                + "</div></body></html>";
            File.WriteAllText(Path.Combine(directory, $"{baseName}.sheet{i}.html"), page);
        }
    }

    /// <summary>
    /// The sheet pages a normalized workbook export left next to itself, in
    /// order, named from their titles. Null when there is only one sheet.
    /// </summary>
    private static IReadOnlyList<SpreadsheetSheet>? TryReadSheetList(string htmlPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(htmlPath) ?? string.Empty;
            var baseName = Path.GetFileNameWithoutExtension(htmlPath);
            var pages = new SortedList<int, SpreadsheetSheet>();
            var head = new char[2048];
            foreach (var file in Directory.GetFiles(directory, baseName + ".sheet*.html"))
            {
                var match = System.Text.RegularExpressions.Regex.Match(
                    Path.GetFileName(file), "\\.sheet(\\d+)\\.html$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                using var reader = new StreamReader(file);
                Array.Clear(head);
                var read = reader.ReadBlock(head, 0, head.Length);
                var title = System.Text.RegularExpressions.Regex.Match(
                    new string(head, 0, read), "<title>(.*?)</title>", System.Text.RegularExpressions.RegexOptions.Singleline);
                var index = int.Parse(match.Groups[1].Value);
                var name = title.Success ? System.Net.WebUtility.HtmlDecode(title.Groups[1].Value) : $"Sheet {index + 1}";
                pages[index] = new SpreadsheetSheet(name, file);
            }

            return pages.Count >= 2 ? pages.Values.ToList() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Wraps the document in the scroll container and gives every sheet
    /// table sticky column letters (A, B, C...) and row numbers, counted
    /// from the export's own colgroup declarations.
    /// </summary>
    private static string AddSpreadsheetChrome(string html)
    {
        var bodyOpen = System.Text.RegularExpressions.Regex.Match(html, "<body[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
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
            if (tableStart < 0)
            {
                builder.Append(html, position, html.Length - position);
                break;
            }

            var tableEnd = html.IndexOf("</table>", tableStart, StringComparison.OrdinalIgnoreCase);
            if (tableEnd < 0)
            {
                builder.Append(html, position, html.Length - position);
                break;
            }

            builder.Append(html, position, tableStart - position);
            var table = html[tableStart..(tableEnd + "</table>".Length)];
            builder.Append(DecorateSheetTable(table));
            position = tableEnd + "</table>".Length;
        }

        return builder.ToString();
    }

    private static string DecorateSheetTable(string table)
    {
        // Column count and total width from the export's own colgroup
        // declarations. The width feeds table-layout:fixed below.
        var columns = 0;
        var totalWidth = 0;
        var widthsComplete = true;
        foreach (System.Text.RegularExpressions.Match colgroup in System.Text.RegularExpressions.Regex.Matches(
            table, "<colgroup([^>]*)>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            var attributes = colgroup.Groups[1].Value;
            var span = System.Text.RegularExpressions.Regex.Match(attributes, "span=\"?(\\d+)");
            var count = span.Success ? int.Parse(span.Groups[1].Value) : 1;
            columns += count;
            var width = System.Text.RegularExpressions.Regex.Match(attributes, "width=\"?(\\d+)");
            if (width.Success)
            {
                totalWidth += count * int.Parse(width.Groups[1].Value);
            }
            else
            {
                widthsComplete = false;
            }
        }

        var firstRow = table.IndexOf("<tr", StringComparison.OrdinalIgnoreCase);
        if (columns == 0 || columns > 1024 || firstRow < 0)
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
        var body = System.Text.RegularExpressions.Regex.Replace(
            table[firstRow..],
            "<tr[^>]*>",
            match =>
            {
                row++;
                return match.Value + "<td class=\"sp-h sp-rn\">" + row + "</td>";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        // The export's colgroup widths map to columns in order; the number
        // gutter needs its own colgroup so theirs stay aligned.
        var openTagEnd = table.IndexOf('>');
        if (openTagEnd < 0)
        {
            return table;
        }

        // Fixed layout renders the sheet's true column widths in a single
        // pass: a large sheet must not visibly renegotiate its columns
        // while it parses, and overflow into empty neighbors behaves like
        // Calc's screen instead of silently widening the column.
        var layout = widthsComplete
            ? $" style=\"table-layout:fixed;width:{totalWidth + 34}px\""
            : string.Empty;

        return table[..openTagEnd]
            + layout
            + ">\n\t<colgroup width=\"34\"></colgroup>"
            + table[(openTagEnd + 1)..firstRow]
            + header
            + body;
    }

    private static string ColumnLetters(int index)
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

    private static string? FindSoffice()
    {
        var overridePath = Environment.GetEnvironmentVariable("SPACEPREVIEW_SOFFICE");
        if (string.Equals(overridePath, "off", StringComparison.OrdinalIgnoreCase))
        {
            // Simulates a machine without a document engine (tests the degraded
            // preview and its suggestion pill on a machine that has it).
            return null;
        }

        foreach (var candidate in GetDefaultSofficeCandidates(
                     overridePath,
                     AppPaths.LocalAppData))
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var candidate in GetRegistrySofficeCandidates())
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return candidate;
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "soffice.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    internal static IReadOnlyList<string> GetDefaultSofficeCandidates(string? overridePath, string localAppData) =>
    [
        overridePath ?? string.Empty,
        @"C:\Program Files\LibreOffice\program\soffice.exe",
        @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
        Path.Combine(localAppData, @"Programs\LibreOffice\program\soffice.exe"),
        @"C:\Program Files\OpenOffice 4\program\soffice.exe",
        @"C:\Program Files (x86)\OpenOffice 4\program\soffice.exe",
        Path.Combine(localAppData, @"Programs\OpenOffice 4\program\soffice.exe")
    ];

    private static IEnumerable<string> GetRegistrySofficeCandidates()
    {
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var subkey in new[]
                     {
                         @"SOFTWARE\LibreOffice\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\LibreOffice\UNO\InstallPath",
                         @"SOFTWARE\OpenOffice\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\OpenOffice\UNO\InstallPath",
                         @"SOFTWARE\OpenOffice.org\UNO\InstallPath",
                         @"SOFTWARE\WOW6432Node\OpenOffice.org\UNO\InstallPath"
                     })
            {
                using var key = root.OpenSubKey(subkey);
                if (key?.GetValue(null) is string installPath && !string.IsNullOrWhiteSpace(installPath))
                {
                    yield return Path.Combine(installPath, "soffice.exe");
                }
            }
        }
    }

    private static long SafeFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static bool IsPresentation(string extension) =>
        extension.Equals(".pptx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".ppt", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".pptm", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".odp", StringComparison.OrdinalIgnoreCase);

    private static bool IsWriterDocument(string extension) =>
        extension.Equals(".docx", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".doc", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".docm", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".rtf", StringComparison.OrdinalIgnoreCase)
        || extension.Equals(".odt", StringComparison.OrdinalIgnoreCase);

    private static string BuildPageRangeFilter(string extension, int firstPages)
    {
        var filter = IsPresentation(extension) ? "impress_pdf_Export" : "writer_pdf_Export";
        return $"pdf:{filter}:{{\"PageRange\":{{\"type\":\"string\",\"value\":\"1-{firstPages}\"}}}}";
    }

    private static int? TryReadExtendedPropertyCount(string path, string propertyName)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("docProps/app.xml");
            if (entry is null || entry.Length <= 0 || entry.Length > MaxAppPropertiesBytes)
            {
                return null;
            }

            using var stream = entry.Open();
            var document = XDocument.Load(stream, LoadOptions.None);
            var text = document.Descendants()
                .FirstOrDefault(element => element.Name.LocalName.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                ?.Value;
            return int.TryParse(text, out var value) && value > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static int? CountPdfPages(string path, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var bytes = File.ReadAllBytes(path);
            token.ThrowIfCancellationRequested();
            var text = Encoding.Latin1.GetString(bytes);
            var count = Regex.Matches(text, @"/Type\s*/Page\b", RegexOptions.CultureInvariant).Count;
            return count > 0 ? count : null;
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

    private static string GetCacheDirectory(string path, string convertTo)
    {
        var file = new FileInfo(path);
        // The conversion format and normalization version are part of the identity:
        // without them a cached conversion would keep serving stale styling
        // after the pipeline changes.
        var input = $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}|{convertTo}|v12";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];
        return Path.Combine(
            AppPaths.DataRoot,
            "DocumentCache",
            hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Best effort cache cleanup.
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cache cleanup.
        }
    }
}
