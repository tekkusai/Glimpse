using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SpacePreview.Utilities;

internal enum OfficeKind
{
    None,
    Document,
    Presentation,
    Spreadsheet
}

internal static partial class FileKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".dib", ".gif", ".ico", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".webp", ".heic", ".heif"
    };

    // What cameras save before any processing, as Windows' Raw Image
    // Extension reads it. The extensions that also name other things are
    // left out: ".raw" is any unprocessed data and ".cap" a network capture.
    private static readonly HashSet<string> CameraRawExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".3fr", ".arw", ".cr2", ".cr3", ".crw", ".dcr", ".dng", ".erf", ".fff", ".iiq", ".kdc", ".mef", ".mrw",
        ".nef", ".nrw", ".orf", ".pef", ".raf", ".rw2", ".rwl", ".sr2", ".srf", ".srw", ".x3f"
    };

    private static readonly HashSet<string> BrowserImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".avif", ".webp"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mov", ".avi", ".wmv", ".mkv", ".webm", ".mpg", ".mpeg", ".ogv",
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".psv", ".json", ".jsonc", ".jsonl", ".ndjson", ".ipynb", ".xml", ".xaml",
        ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config", ".env", ".gitignore", ".gitattributes", ".editorconfig",
        ".http", ".rest",
        ".cs", ".fs", ".vb", ".sln", ".csproj", ".props", ".targets", ".resx",
        ".c", ".cc", ".cpp", ".cxx", ".h", ".hpp", ".hh", ".m", ".mm",
        ".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".map", ".css", ".scss", ".sass", ".less", ".html", ".htm",
        ".py", ".pyi", ".ps1", ".psm1", ".psd1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".nsh",
        ".sql", ".rs", ".go", ".java", ".kt", ".kts", ".swift", ".rb", ".php", ".lua", ".r", ".pl",
        ".vue", ".svelte", ".astro", ".graphql", ".gql", ".proto", ".prisma", ".zig", ".dart", ".ex", ".exs",
        ".dockerfile", ".gradle", ".lock", ".reg", ".res", ".str", ".meta", ".info", ".version", ".nfo"
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path)) || IsCameraRaw(path);

    public static bool IsCameraRaw(string path) => CameraRawExtensions.Contains(Path.GetExtension(path));

    public static bool IsBrowserImage(string path) => BrowserImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsText(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TextExtensions.Contains(extension))
        {
            return true;
        }

        var name = Path.GetFileName(path);
        return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Containerfile", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("README", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsMarkdown(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase);
    }

    public static bool UsesNativeTextPreview(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".text", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A page in the browser, by name alone, so the window can start the
    /// browser before it reads the file. Code is a page only once a grammar
    /// takes it, and Illustrator files only when they hold a PDF.
    /// </summary>
    public static bool OpensAsPage(string path) =>
        Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || IsOffice(path)
        || IsMedia(path)
        || IsGif(path)
        || (IsBrowserImage(path) && !IsImage(path))
        || IsEpub(path)
        || IsHtml(path)
        || IsMarkdown(path);

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus"
    };

    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> PhotographExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".heic", ".heif", ".avif"
    };

    /// <summary>
    /// A format cameras and phones write, or the one photographs and
    /// paintings are worked on in. Zoomed in, such a picture is smoothed
    /// and never drawn as squares: closer up it is still a photograph. A
    /// PNG, a GIF or an icon keeps its pixels.
    /// </summary>
    public static bool IsPhotograph(string path) =>
        IsCameraRaw(path) || IsPhotoshop(path) || PhotographExtensions.Contains(Path.GetExtension(path));

    public static bool IsSvg(string path) => Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase);

    public static bool IsHeif(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heif", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsHtml(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".html", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
        || IsPdfBackedIllustrator(path);

    public static bool IsIllustrator(string path) => Path.GetExtension(path).Equals(".ai", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".ply", ".3mf", ".glb"
    };

    public static bool IsMesh(string path) => MeshExtensions.Contains(Path.GetExtension(path));

    public static bool IsPhotoshop(string path)
    {
        // A large document (.psb) is the same file with wider counts.
        var extension = Path.GetExtension(path);
        return extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStep(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".stp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".step", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsFont(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ttc", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsShortcut(string path) => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);

    public static bool IsInternetShortcut(string path) => Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase);

    public static bool IsExecutableLike(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz",
        ".jar", ".war", ".ear", ".apk", ".nupkg", ".vsix", ".xpi"
    };

    public static bool IsArchive(string path) => ArchiveExtensions.Contains(Path.GetExtension(path));

    public static bool IsEpub(string path) => Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    public static bool IsWindowsAppPackage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".appx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msix", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".appxbundle", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".msixbundle", StringComparison.OrdinalIgnoreCase);
    }

    // The one Office table: every lane that converts, probes, sanitizes or
    // routes an Office file asks it. Templates, shows and macro variants
    // preview like their documents. .dot and .pot stay out: Graphviz and
    // gettext own those names on developer machines.
    private static readonly Dictionary<string, OfficeKind> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = OfficeKind.Document, [".docm"] = OfficeKind.Document,
        [".dotx"] = OfficeKind.Document, [".dotm"] = OfficeKind.Document,
        [".doc"] = OfficeKind.Document, [".rtf"] = OfficeKind.Document,
        [".odt"] = OfficeKind.Document, [".ott"] = OfficeKind.Document,

        [".pptx"] = OfficeKind.Presentation, [".pptm"] = OfficeKind.Presentation,
        [".potx"] = OfficeKind.Presentation, [".potm"] = OfficeKind.Presentation,
        [".ppsx"] = OfficeKind.Presentation, [".ppsm"] = OfficeKind.Presentation,
        [".ppt"] = OfficeKind.Presentation, [".pps"] = OfficeKind.Presentation,
        [".odp"] = OfficeKind.Presentation, [".otp"] = OfficeKind.Presentation,

        [".xlsx"] = OfficeKind.Spreadsheet, [".xlsm"] = OfficeKind.Spreadsheet,
        [".xltx"] = OfficeKind.Spreadsheet, [".xltm"] = OfficeKind.Spreadsheet,
        [".xlsb"] = OfficeKind.Spreadsheet, [".xls"] = OfficeKind.Spreadsheet,
        [".xlt"] = OfficeKind.Spreadsheet,
        [".ods"] = OfficeKind.Spreadsheet, [".ots"] = OfficeKind.Spreadsheet
    };

    public static OfficeKind GetOfficeKind(string path) =>
        OfficeExtensions.GetValueOrDefault(Path.GetExtension(path));

    /// <summary>OOXML found by its content, whatever the file is called.</summary>
    public static OfficeKind GetOfficeKind(ZipPackageKind package) => package switch
    {
        ZipPackageKind.OfficeWord => OfficeKind.Document,
        ZipPackageKind.OfficePowerPoint => OfficeKind.Presentation,
        ZipPackageKind.OfficeExcel => OfficeKind.Spreadsheet,
        _ => OfficeKind.None
    };

    public static bool IsOffice(string path) => GetOfficeKind(path) != OfficeKind.None;

    public static bool IsSpreadsheet(string path) => GetOfficeKind(path) == OfficeKind.Spreadsheet;

    public static bool CanUseSignatureRouting(string path)
    {
        if (IsText(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return true;
        }

        return !IsImage(path)
            && !IsBrowserImage(path)
            && !IsMedia(path)
            && !IsPdf(path)
            && !IsIllustrator(path)
            && !IsMesh(path)
            && !IsPhotoshop(path)
            && !IsStep(path)
            && !IsFont(path)
            && !IsShortcut(path)
            && !IsInternetShortcut(path)
            && !IsExecutableLike(path)
            && !IsArchive(path)
            && !IsEpub(path)
            && !IsWindowsAppPackage(path)
            && !IsOffice(path);
    }

    private static bool IsPdfBackedIllustrator(string path)
    {
        if (!IsIllustrator(path))
        {
            return false;
        }

        return TryFindPdfHeaderOffset(path, out var offset) && offset <= 1024;
    }

    public static bool TryFindPdfHeaderOffset(string path, out long offset)
    {
        offset = -1;
        try
        {
            var buffer = new byte[64 * 1024];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytesRead = stream.Read(buffer, 0, buffer.Length);
            var index = buffer.AsSpan(0, bytesRead).IndexOf("%PDF-"u8);
            if (index < 0)
            {
                return false;
            }

            offset = index;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A size in the region's digits: 5.3 KB here, 5,3 KB in Berlin.</summary>
    public static string FormatSize(long bytes, CultureInfo? culture = null)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size.ToString("0.#", culture ?? CultureInfo.CurrentCulture)} {units[unit]}";
    }

    /// <summary>
    /// The header's details: a kind only where the name shows none (a
    /// folder, a file without an extension), the size, the one
    /// <paramref name="fact"/> the content knows, and when it last changed.
    /// </summary>
    public static string Describe(FileSystemInfo item, string? fact, DateTime now, CultureInfo culture) =>
        Describe(item is not FileInfo, item.Extension, item is FileInfo file ? file.Length : 0, item.LastWriteTime, fact, now, culture);

    /// <summary>The same line from what was read of the item earlier, with no word to the disk.</summary>
    public static string Describe(bool isFolder, string extension, long length, DateTime modified, string? fact, DateTime now, CultureInfo culture)
    {
        var parts = new List<string>(4);
        if (isFolder)
        {
            parts.Add("Folder");
        }
        else
        {
            if (string.IsNullOrEmpty(extension))
            {
                parts.Add("File");
            }

            parts.Add(FormatSize(length, culture));
        }

        if (!string.IsNullOrEmpty(fact))
        {
            parts.Add(fact);
        }

        parts.Add(DescribeWhen(modified, now, culture));
        return string.Join(" · ", parts);
    }

    /// <summary>
    /// A time as short as it can be: "Today 4:35 PM", "Yesterday 9:12 AM",
    /// the weekday within a week, then "Aug 27", with the year only when it
    /// is not this one. The words are the interface's English; only the
    /// clock is <paramref name="culture"/>'s, so a 24-hour reader sees 16:35.
    /// </summary>
    public static string DescribeWhen(DateTime when, DateTime now, CultureInfo culture)
    {
        // A one-letter pattern ("H") would be read as a standard format and
        // throw; the empty quoted prefix keeps it custom and prints nothing.
        var time = when.ToString("''" + culture.DateTimeFormat.ShortTimePattern, English);
        return (now.Date - when.Date).Days switch
        {
            0 => $"Today {time}",
            1 => $"Yesterday {time}",
            > 1 and < 7 => $"{when.ToString("dddd", English)} {time}",
            _ => DescribeDate(when, now)
        };
    }

    /// <summary>A day in the interface's English: "Aug 27", with the year only when it is not this one.</summary>
    public static string DescribeDate(DateTime when, DateTime now) =>
        when.ToString(when.Year == now.Year ? "MMM d" : "MMM d, yyyy", English);

    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");
}

internal sealed record TextPreview(string Text, bool Truncated, long TotalBytes);

/// <summary>
/// Bytes to display text, once for every text lane: the native text view
/// and the code and markdown pages all read through here, so an encoding or
/// JSON fix lands everywhere at once.
/// </summary>
internal static class TextPreviewLoader
{
    public const int MaxPreviewBytes = 2 * 1024 * 1024;

    /// <summary>JSON up to this many characters is indented before display.</summary>
    public const int JsonPrettyPrintLimit = 300_000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Windows' ANSI code pages are not built into .NET; the provider adds them.
    private static readonly Encoding Ansi = LoadAnsiCodePage();

    /// <summary>
    /// The one truncation sentence, shared with the lanes that need to strip
    /// or restate it: change it here and everywhere agrees.
    /// </summary>
    public static string TruncationNotice(long shownBytes, long totalBytes) =>
        $"\n\nShowing the first {FileKinds.FormatSize(shownBytes)} of {FileKinds.FormatSize(totalBytes)}. Open the file for the rest.";

    public static TextPreview? TryLoad(string path)
    {
        try
        {
            var file = new FileInfo(path);
            var bytesToRead = (int)Math.Min(file.Length, MaxPreviewBytes);
            var buffer = new byte[bytesToRead];

            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var read = stream.ReadAtLeast(buffer, bytesToRead, throwOnEndOfStream: false);
                if (read != bytesToRead)
                {
                    Array.Resize(ref buffer, read);
                }
            }

            var truncated = file.Length > buffer.Length;
            var text = Decode(buffer, truncated);
            if (text is null)
            {
                return null;
            }

            text = text.Replace("\0", string.Empty);
            if (!truncated
                && text.Length <= JsonPrettyPrintLimit
                && Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                text = PrettyPrintJson(text);
            }

            if (truncated)
            {
                text += TruncationNotice(buffer.Length, file.Length);
            }

            return new TextPreview(text, truncated, file.Length);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A byte order mark names the encoding. Without one the bytes are UTF-8
    /// only if all of them are valid UTF-8; anything else is the ANSI code
    /// page, which is what Excel's CSV export and older Notepad write.
    /// Returns null for binary data.
    /// </summary>
    internal static string? Decode(byte[] buffer, bool truncated)
    {
        if (DetectEncoding(buffer) is { } marked)
        {
            var preamble = marked.Preamble.Length;
            return marked.GetString(buffer, preamble, buffer.Length - preamble);
        }

        if (LooksBinary(buffer))
        {
            return null;
        }

        // A read cut at the byte limit can split the last character; that
        // tail is no evidence against UTF-8.
        var length = truncated ? buffer.Length - IncompleteUtf8Tail(buffer) : buffer.Length;
        try
        {
            return StrictUtf8.GetString(buffer, 0, length);
        }
        catch (DecoderFallbackException)
        {
            return Ansi.GetString(buffer);
        }
    }

    /// <summary>
    /// The encoding <see cref="Decode"/> reads these bytes in, and how many
    /// of them are its byte order mark. Null for binary data. A reader that
    /// takes a file a piece at a time settles the encoding here once, from
    /// the file's first bytes, so both agree on every file.
    /// </summary>
    internal static Encoding? EncodingOf(byte[] buffer, bool truncated, out int preamble)
    {
        preamble = 0;
        if (DetectEncoding(buffer) is { } marked)
        {
            preamble = marked.Preamble.Length;
            return marked;
        }

        if (LooksBinary(buffer))
        {
            return null;
        }

        var length = truncated ? buffer.Length - IncompleteUtf8Tail(buffer) : buffer.Length;
        try
        {
            StrictUtf8.GetCharCount(buffer, 0, length);
            return Encoding.UTF8;
        }
        catch (DecoderFallbackException)
        {
            return Ansi;
        }
    }

    /// <summary>
    /// Indents JSON. The relaxed encoder keeps non-ASCII letters and
    /// &lt; &gt; &amp; ' as written, and numbers keep their exact text.
    /// Anything that does not parse (comments, JSON Lines) stays as it is.
    /// </summary>
    internal static string PrettyPrintJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
            {
                document.WriteTo(writer);
            }

            return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
        }
        catch (JsonException)
        {
            return text;
        }
    }

    /// <summary>Bytes at the end that start a UTF-8 character the buffer does not finish.</summary>
    internal static int IncompleteUtf8Tail(ReadOnlySpan<byte> buffer)
    {
        for (var back = 1; back <= Math.Min(3, buffer.Length); back++)
        {
            var value = buffer[^back];
            if ((value & 0xC0) == 0x80)
            {
                continue;
            }

            var needed = (value & 0xE0) == 0xC0 ? 2
                : (value & 0xF0) == 0xE0 ? 3
                : (value & 0xF8) == 0xF0 ? 4
                : 1;
            return needed > back ? back : 0;
        }

        return 0;
    }

    private static Encoding LoadAnsiCodePage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            return Encoding.Latin1;
        }
    }

    private static Encoding? DetectEncoding(byte[] buffer)
    {
        if (buffer.Length >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (buffer.Length >= 2)
        {
            if (buffer[0] == 0xFF && buffer[1] == 0xFE)
            {
                return Encoding.Unicode;
            }

            if (buffer[0] == 0xFE && buffer[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode;
            }
        }

        return null;
    }

    private static bool LooksBinary(byte[] buffer)
    {
        if (buffer.Length == 0)
        {
            return false;
        }

        var control = 0;
        var sampleLength = Math.Min(buffer.Length, 8192);
        for (var i = 0; i < sampleLength; i++)
        {
            var b = buffer[i];
            if (b == 0)
            {
                return true;
            }

            if (b < 32 && b is not (9 or 10 or 13 or 12))
            {
                control++;
            }
        }

        return control > sampleLength / 8;
    }
}
