using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using SpacePreview.Services;
using SpacePreview.Utilities;
using TextMateSharp.Grammars;
using TextMateRegistry = TextMateSharp.Registry.Registry;

namespace SpacePreview.Preview;

/// <summary>
/// A page Glimpse rendered for a file. Markdown maps the file's folder to a
/// virtual host so its relative images resolve; nothing else is reachable.
/// </summary>
internal sealed record GeneratedHtmlDocument(
    string SourcePath,
    string HtmlPath,
    string? ResourceHost = null,
    string? ResourceFolder = null)
{
    public Uri Source => new(HtmlPath, UriKind.Absolute);
}

internal sealed record GeneratedCodeAttempt(
    GeneratedHtmlDocument? Document,
    TextPreview? FallbackText);

internal static partial class GeneratedHtmlPreview
{
    private const int CodeHighlightLimit = 300_000;
    private static readonly SemaphoreSlim SyntaxGate = new(1, 1);
    private static readonly SyntaxState Syntax = new();

    private static readonly MarkdownPipeline MarkdownPipeline = new MarkdownPipelineBuilder()
        .DisableHtml()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .UseAutoIdentifiers()
        .Build();

    public static GeneratedHtmlDocument? TryBuildMarkdown(string path)
    {
        try
        {
            var preview = TextPreviewLoader.TryLoad(path);
            if (preview is null)
            {
                return null;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            var host = directory is null ? null : $"glimpse-md-{StableToken(path)}.local".ToLowerInvariant();
            var htmlPath = HtmlCache.Get(
                "MarkdownHtml",
                $"{path}|{StableTextContentToken(preview)}",
                () =>
                {
                    var rendered = RenderMarkdown(MarkdownSourceWithoutLoaderNotice(preview));
                    var notice = preview.Truncated ? MarkdownTruncationNotice(preview) : string.Empty;
                    return BuildShell($"<div class=\"md-root\">{NormalizeMarkdownHtml(rendered, host)}{notice}</div>");
                });

            return htmlPath is null ? null : new GeneratedHtmlDocument(path, htmlPath, host, directory);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"markdown preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Markdig's HTML, with code spans too long to stay whole marked so they may wrap.</summary>
    internal static string RenderMarkdown(string source)
    {
        var document = Markdown.Parse(source, MarkdownPipeline);
        foreach (var code in document.Descendants<CodeInline>())
        {
            if (code.Content.Length > GeneratedHtmlTheme.LongChip)
            {
                code.GetAttributes().AddClass("long");
            }
        }

        return document.ToHtml(MarkdownPipeline);
    }

    /// <summary>
    /// The window's text lane needs both outcomes from one disk read: either
    /// generated HTML or the already-loaded text to hand to the native
    /// fallback. This avoids reading uncommon/large text-like files twice.
    /// <paramref name="highlighting"/> runs, on this thread, once the text is
    /// known to become a page and before the slow part, its highlighting.
    /// </summary>
    internal static GeneratedCodeAttempt TryBuildCodeForPreview(
        string path,
        CancellationToken cancellationToken = default,
        Action? highlighting = null)
    {
        TextPreview? loadedText = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            loadedText = TextPreviewLoader.TryLoad(path);
            if (loadedText is null || FileKinds.UsesNativeTextPreview(path))
            {
                return new GeneratedCodeAttempt(null, loadedText);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var htmlPath = HtmlCache.Get(
                "CodeHtml",
                $"{path}|{StableTextContentToken(loadedText)}",
                () =>
                {
                    if (!CanHighlightCodeLength(loadedText.Text.Length))
                    {
                        return null;
                    }

                    var body = TryRenderCode(path, loadedText.Text, cancellationToken, highlighting);
                    cancellationToken.ThrowIfCancellationRequested();
                    return body is null ? null : BuildShell(body);
                });

            return htmlPath is null
                ? new GeneratedCodeAttempt(null, loadedText)
                : new GeneratedCodeAttempt(new GeneratedHtmlDocument(path, htmlPath), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"code preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return new GeneratedCodeAttempt(null, loadedText);
        }
    }

    internal static bool CanHighlightCodeLength(int length) =>
        length >= 0 && length <= CodeHighlightLimit;

    public static GeneratedHtmlDocument? TryBuildEpub(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var htmlPath = HtmlCache.Get(
                "EpubHtml",
                $"{path}|{StableEpubContentToken(path, cancellationToken)}",
                () =>
                {
                    var body = EpubPreviewLoader.TryBuildBody(path, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    return body is null ? null : BuildShell(body);
                });

            return htmlPath is null ? null : new GeneratedHtmlDocument(path, htmlPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub html preview failed {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    internal static string MarkdownSourceWithoutLoaderNotice(TextPreview preview)
    {
        var source = preview.Text;
        if (preview.Truncated)
        {
            var marker = TextPreviewLoader.TruncationNotice(
                Math.Min(preview.TotalBytes, TextPreviewLoader.MaxPreviewBytes), preview.TotalBytes);
            if (source.EndsWith(marker, StringComparison.Ordinal))
            {
                source = source[..^marker.Length];
            }
        }

        return StripYamlFrontMatter(source);
    }

    internal static string MarkdownTruncationNotice(TextPreview preview)
    {
        var shown = FileKinds.FormatSize(Math.Min(preview.TotalBytes, TextPreviewLoader.MaxPreviewBytes));
        var total = FileKinds.FormatSize(preview.TotalBytes);
        return $"<p class=\"notice\">Showing the first {WebUtility.HtmlEncode(shown)} of {WebUtility.HtmlEncode(total)}. Open the file for the rest.</p>";
    }

    internal static string StripYamlFrontMatter(string text)
    {
        if (!text.StartsWith("---\r\n", StringComparison.Ordinal)
            && !text.StartsWith("---\n", StringComparison.Ordinal))
        {
            return text;
        }

        var start = text.StartsWith("---\r\n", StringComparison.Ordinal) ? 5 : 4;
        var index = start;
        while (index < text.Length)
        {
            var lineEnd = text.IndexOf('\n', index);
            var length = lineEnd >= 0 ? lineEnd - index : text.Length - index;
            var line = text.Substring(index, length).TrimEnd('\r');
            if (line is "---" or "...")
            {
                return lineEnd >= 0 ? text[(lineEnd + 1)..] : string.Empty;
            }

            if (lineEnd < 0)
            {
                break;
            }

            index = lineEnd + 1;
        }

        return text;
    }

    /// <summary>
    /// Shapes Markdig's output for the page: fenced code takes the app's
    /// block style, remote images never load, and relative images point at
    /// the virtual host that maps the file's folder. The page carries no
    /// &lt;base&gt;, so "#heading" links stay inside the document.
    /// </summary>
    internal static string NormalizeMarkdownHtml(string html, string? resourceHost = null)
    {
        html = FencedCodeRegex().Replace(html, match =>
        {
            var codeAttributes = match.Groups[1].Value;
            return $"<pre class=\"block\"><code{codeAttributes}>";
        });

        html = RemoteImageRegex().Replace(html, "<em>Remote image blocked</em>");
        if (resourceHost is not null)
        {
            html = ImageSourceRegex().Replace(html, match =>
                $"{match.Groups[1].Value}{ResolveImageSource(match.Groups[2].Value, resourceHost)}\"");
        }

        return html;
    }

    // Relative and root-relative sources resolve against the file's folder.
    // Anything with a scheme, or protocol-relative, is left for the resource
    // policy to judge.
    private static string ResolveImageSource(string source, string resourceHost)
    {
        var colon = source.IndexOf(':');
        var slash = source.IndexOf('/');
        var hasScheme = colon > 0 && (slash < 0 || colon < slash);
        if (source.Length == 0 || hasScheme || source.StartsWith("//", StringComparison.Ordinal) || source.StartsWith('#'))
        {
            return source;
        }

        return $"https://{resourceHost}/{source.TrimStart('/')}";
    }

    internal static string? TryRenderCode(string path, string text, CancellationToken cancellationToken = default, Action? highlighting = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SyntaxGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var grammar = Syntax.GetGrammar(path);
            if (grammar is null)
            {
                return null;
            }

            highlighting?.Invoke();
            return RenderCode(text, grammar, cancellationToken);
        }
        finally
        {
            SyntaxGate.Release();
        }
    }

    private static string RenderCode(string text, IGrammar grammar, CancellationToken cancellationToken)
    {
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = text.Split('\n');
        var gutter = new StringBuilder(lines.Length * 4);
        var source = new StringBuilder(text.Length + 1024);
        IStateStack? ruleStack = null;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            if ((lineIndex & 31) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (lineIndex > 0)
            {
                gutter.Append('\n');
                source.Append('\n');
            }

            gutter.Append(lineIndex + 1);
            var line = lines[lineIndex];
            var result = grammar.TokenizeLine(new LineText(line), ruleStack, TimeSpan.FromMilliseconds(150));
            ruleStack = result.RuleStack;
            AppendHighlightedLine(source, line, result.Tokens);
        }

        return $$"""
<div class="code-doc">
<pre class="gutter" aria-hidden="true">{{gutter}}</pre>
<pre class="src"><code>{{source}}</code></pre>
</div>
""";
    }

    public static void WarmSyntax()
    {
        SyntaxGate.Wait();
        try
        {
            _ = Syntax.GetGrammar("preview.json");
        }
        finally
        {
            SyntaxGate.Release();
        }
    }

    private static void AppendHighlightedLine(StringBuilder builder, string line, IToken[] tokens)
    {
        if (tokens.Length == 0)
        {
            builder.Append(WebUtility.HtmlEncode(line));
            return;
        }

        var cursor = 0;
        foreach (var token in tokens)
        {
            var start = Math.Clamp(token.StartIndex, 0, line.Length);
            var end = Math.Clamp(token.EndIndex, start, line.Length);

            if (start > cursor)
            {
                builder.Append(WebUtility.HtmlEncode(line[cursor..start]));
            }

            if (end > start)
            {
                AppendSpan(builder, line[start..end], ClassForScopes(token.Scopes));
            }

            cursor = end;
        }

        if (cursor < line.Length)
        {
            builder.Append(WebUtility.HtmlEncode(line[cursor..]));
        }
    }

    private static void AppendSpan(StringBuilder builder, string text, string? tokenClass)
    {
        var encoded = WebUtility.HtmlEncode(text);
        if (string.IsNullOrEmpty(tokenClass))
        {
            builder.Append(encoded);
            return;
        }

        builder.Append("<span class=\"");
        builder.Append(tokenClass);
        builder.Append("\">");
        builder.Append(encoded);
        builder.Append("</span>");
    }

    private static string? ClassForScopes(IReadOnlyList<string> scopes)
    {
        if (ScopeContains(scopes, "invalid")) return "tk-invalid";
        if (ScopeContains(scopes, "comment")) return "tk-comment";
        if (ScopeContains(scopes, "string")) return "tk-string";
        if (ScopeContains(scopes, "constant.numeric")) return "tk-number";
        if (ScopeContains(scopes, "keyword.operator")) return "tk-operator";
        if (ScopeContains(scopes, "keyword") || ScopeContains(scopes, "storage")) return "tk-keyword";
        if (ScopeContains(scopes, "entity.name.tag")) return "tk-tag";
        if (ScopeContains(scopes, "entity.other.attribute-name")) return "tk-attribute";
        if (ScopeContains(scopes, "entity.name.function") || ScopeContains(scopes, "support.function")) return "tk-function";
        if (ScopeContains(scopes, "property-name")) return "tk-property";
        if (ScopeContains(scopes, "entity.name.type") || ScopeContains(scopes, "support.type")) return "tk-type";
        if (ScopeContains(scopes, "constant")) return "tk-constant";
        if (ScopeContains(scopes, "variable") || ScopeContains(scopes, "property")) return "tk-property";
        if (ScopeContains(scopes, "punctuation")) return "tk-punct";
        return null;
    }

    private static bool ScopeContains(IReadOnlyList<string> scopes, string value)
    {
        for (var i = scopes.Count - 1; i >= 0; i--)
        {
            if (scopes[i].IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    internal static string BuildShell(string body) => $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>
{{GeneratedHtmlTheme.PageCss}}{{GeneratedHtmlTheme.DocumentCss}}</style>
</head>
<body>
{{body}}
</body>
</html>
""";

    // MERGE: OfficeOpenXmlPreview.Shell still passes a palette, and that file
    // is another lane's. This goes with that call's second argument.
    internal static string BuildShell(string body, ThemePalette palette) => BuildShell(body);

    private static string StableToken(string path)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToUpperInvariant()));
        return Convert.ToHexString(bytes)[..12];
    }

    private static string StableTextContentToken(TextPreview preview)
    {
        var content = $"{preview.Truncated}|{preview.TotalBytes}|{preview.Text}";
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }

    internal static string StableEpubContentToken(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var archive = ZipFile.OpenRead(path);
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Span<byte> metadata = stackalloc byte[29];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Encoding.UTF8.GetBytes(entry.FullName);
            BinaryPrimitives.WriteInt32LittleEndian(metadata[..4], name.Length);
            BinaryPrimitives.WriteInt64LittleEndian(metadata.Slice(4, 8), entry.Length);
            BinaryPrimitives.WriteInt64LittleEndian(metadata.Slice(12, 8), entry.CompressedLength);
            BinaryPrimitives.WriteUInt32LittleEndian(metadata.Slice(20, 4), entry.Crc32);
            BinaryPrimitives.WriteInt32LittleEndian(metadata.Slice(24, 4), entry.ExternalAttributes);
            metadata[28] = entry.IsEncrypted ? (byte)1 : (byte)0;
            hash.AppendData(metadata);
            hash.AppendData(name);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    [GeneratedRegex("<pre><code([^>]*)>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex("<img\\b[^>]*\\bsrc\\s*=\\s*(['\"]?)(?:https?:)?//[^'\"\\s>]+\\1[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RemoteImageRegex();

    [GeneratedRegex("(<img\\b[^>]*?\\bsrc=\")([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageSourceRegex();

    private sealed class SyntaxState
    {
        private readonly RegistryOptions _options = new(ThemeName.DarkPlus);
        private readonly TextMateRegistry _registry;
        private readonly Dictionary<string, IGrammar?> _grammars = new(StringComparer.OrdinalIgnoreCase);

        public SyntaxState()
        {
            _registry = new TextMateRegistry(_options);
        }

        public IGrammar? GetGrammar(string path)
        {
            var scope = ResolveScope(path);
            if (string.IsNullOrWhiteSpace(scope))
            {
                return null;
            }

            if (_grammars.TryGetValue(scope, out var cached))
            {
                return cached;
            }

            try
            {
                var grammar = _registry.LoadGrammar(scope);
                _grammars[scope] = grammar;
                return grammar;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"syntax grammar load failed {scope}: {ex.GetType().Name}: {ex.Message}");
                _grammars[scope] = null;
                return null;
            }
        }

        private string? ResolveScope(string path)
        {
            var name = Path.GetFileName(path);
            var languageId = name.ToLowerInvariant() switch
            {
                "dockerfile" or "containerfile" => "dockerfile",
                "makefile" => "makefile",
                ".gitignore" or ".gitignore_global" or ".dockerignore" => "ignore",
                _ => null
            };

            if (languageId is not null)
            {
                return _options.GetScopeByLanguageId(languageId);
            }

            var extension = Path.GetExtension(path);
            return string.IsNullOrWhiteSpace(extension)
                ? null
                : _options.GetScopeByExtension(extension);
        }
    }
}
