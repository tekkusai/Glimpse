using System.IO;
using System.Text;

namespace SpacePreview.Utilities;

internal static class FileKinds
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bmp", ".dib", ".gif", ".ico", ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".tif", ".tiff", ".webp", ".heic", ".heif"
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

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

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

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".wav", ".m4a", ".aac", ".flac", ".wma", ".ogg", ".opus"
    };

    public static bool IsMedia(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    public static bool IsGif(string path) => Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);

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

    public static bool IsStl(string path) => Path.GetExtension(path).Equals(".stl", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> MeshExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".stl", ".obj", ".ply", ".3mf"
    };

    public static bool IsMesh(string path) => MeshExtensions.Contains(Path.GetExtension(path));

    public static bool IsPhotoshop(string path) => Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase);

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

    public static bool IsSpreadsheet(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xls", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsm", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".xlsb", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".ods", StringComparison.OrdinalIgnoreCase);
    }

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
            && !OfficeOpenXmlPreviewLoader.IsSupported(path);
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

    public static string FriendlyKind(string path)
    {
        if (Directory.Exists(path))
        {
            return "Folder";
        }

        var extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        return string.IsNullOrEmpty(extension) ? "File" : $"{extension} file";
    }

    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{size:0.#} {units[unit]}";
    }

    public static string BuildSubtitle(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var info = new DirectoryInfo(path);
                return $"Folder · Modified {info.LastWriteTime:g}";
            }

            var file = new FileInfo(path);
            return $"{FriendlyKind(path)} · {FormatSize(file.Length)} · Modified {file.LastWriteTime:g}";
        }
        catch
        {
            return FriendlyKind(path);
        }
    }
}

internal sealed record TextPreview(string Text, bool Truncated, long TotalBytes);

internal static class TextPreviewLoader
{
    public const int MaxPreviewBytes = 2 * 1024 * 1024;

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
                var offset = 0;
                while (offset < bytesToRead)
                {
                    var read = stream.Read(buffer, offset, bytesToRead - offset);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset != bytesToRead)
                {
                    Array.Resize(ref buffer, offset);
                }
            }

            var encoding = DetectEncoding(buffer);
            if (encoding is null && LooksBinary(buffer))
            {
                return null;
            }

            encoding ??= Encoding.UTF8;
            var text = encoding.GetString(buffer);
            text = text.Replace("\0", string.Empty);

            var truncated = file.Length > buffer.Length;
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
