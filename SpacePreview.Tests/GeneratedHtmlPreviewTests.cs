using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class GeneratedHtmlPreviewTests
{
    [Fact]
    public void StripYamlFrontMatterRemovesMetadataBlock()
    {
        var source = """
---
title: Draft
tags:
  - preview
---
# Heading
""";

        var stripped = GeneratedHtmlPreview.StripYamlFrontMatter(source);

        Assert.DoesNotContain("title: Draft", stripped, StringComparison.Ordinal);
        Assert.StartsWith("# Heading", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public void StripYamlFrontMatterLeavesNormalMarkdownAlone()
    {
        const string source = "# Heading\n\nBody";

        Assert.Equal(source, GeneratedHtmlPreview.StripYamlFrontMatter(source));
    }

    [Fact]
    public void NormalizeMarkdownHtmlUsesAppCodeBlockShapeAndBlocksRemoteImages()
    {
        const string source = """
<h1>Hello</h1>
<pre><code class="language-cs">Console.WriteLine("hi");</code></pre>
<p><img src="https://example.com/pixel.png" alt="remote"></p>
""";

        var html = GeneratedHtmlPreview.NormalizeMarkdownHtml(source);

        Assert.Contains("<pre class=\"block\"><code class=\"language-cs\">", html, StringComparison.Ordinal);
        Assert.Contains("Remote image blocked", html, StringComparison.Ordinal);
        Assert.DoesNotContain("https://example.com/pixel.png", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownTreatsRawHtmlAsTextInsteadOfBrowserInstructions()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "refresh.md");
        File.WriteAllText(path, "<meta http-equiv=\"refresh\" content=\"0;url=https://example.com/\">\n\n# Notes");

        var document = GeneratedHtmlPreview.TryBuildMarkdown(path);

        Assert.NotNull(document);
        var html = File.ReadAllText(document!.HtmlPath);
        Assert.DoesNotContain("<meta http-equiv", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Notes", html, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownCacheChangesWhenContentChangesButModifiedTimeDoesNot()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "notes.md");
        File.WriteAllText(path, "# First\n\nAAAA");
        var modified = File.GetLastWriteTimeUtc(path);
        var first = GeneratedHtmlPreview.TryBuildMarkdown(path);

        File.WriteAllText(path, "# First\n\nBBBB");
        File.SetLastWriteTimeUtc(path, modified);
        var second = GeneratedHtmlPreview.TryBuildMarkdown(path);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.HtmlPath, second!.HtmlPath);
        Assert.Contains("BBBB", File.ReadAllText(second.HtmlPath), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSourceWithoutLoaderNoticeKeepsTruncationOutOfRenderedMarkdown()
    {
        const long totalBytes = TextPreviewLoader.MaxPreviewBytes + 1024;
        var preview = new TextPreview(
            "# Hello" + TextPreviewLoader.TruncationNotice(TextPreviewLoader.MaxPreviewBytes, totalBytes),
            Truncated: true,
            TotalBytes: totalBytes);

        var source = GeneratedHtmlPreview.MarkdownSourceWithoutLoaderNotice(preview);
        var notice = GeneratedHtmlPreview.MarkdownTruncationNotice(preview);

        Assert.Equal("# Hello", source);
        Assert.Contains("Showing the first", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing the first", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRenderCodeUsesGeneratedHtmlCodeShape()
    {
        var html = GeneratedHtmlPreview.TryRenderCode(
            "sample.cs",
            "using System;\nvar value = \"hello\";");

        Assert.NotNull(html);
        Assert.Contains("class=\"code-doc\"", html!, StringComparison.Ordinal);
        Assert.Contains("class=\"gutter\"", html, StringComparison.Ordinal);
        Assert.Contains("tk-keyword", html, StringComparison.Ordinal);
        Assert.Contains("tk-string", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TryRenderCodeDeclinesUnknownFiles()
    {
        Assert.Null(GeneratedHtmlPreview.TryRenderCode("sample.unknown", "hello"));
    }

    [Fact]
    public void TryBuildCodeLeavesJsonByteShapeAlone()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(path, "{\"b\":1,\"a\":[2,3]}");

        var document = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;

        Assert.NotNull(document);
        var html = File.ReadAllText(document!.HtmlPath);
        var visibleText = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty));
        Assert.Contains("{\"b\":1,\"a\":[2,3]}", visibleText, StringComparison.Ordinal);
        Assert.DoesNotContain("\n  &quot;b&quot;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildCodeReusesItsDeterministicHtmlCache()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cached.json");
        File.WriteAllText(path, "{\"ready\":true}");
        var first = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;
        Assert.NotNull(first);
        const string marker = "<html>cache-sentinel</html>";
        File.WriteAllText(first!.HtmlPath, marker);

        var second = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;

        Assert.NotNull(second);
        Assert.Equal(first.HtmlPath, second!.HtmlPath);
        Assert.Equal(marker, File.ReadAllText(second.HtmlPath));
    }

    [Fact]
    public void CodeCacheChangesWhenContentChangesButModifiedTimeDoesNot()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "preserved.json");
        File.WriteAllText(path, "{\"value\":\"AAAA\"}");
        var modified = File.GetLastWriteTimeUtc(path);
        var first = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;

        File.WriteAllText(path, "{\"value\":\"BBBB\"}");
        File.SetLastWriteTimeUtc(path, modified);
        var second = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.HtmlPath, second!.HtmlPath);
        Assert.Contains("BBBB", File.ReadAllText(second.HtmlPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TryBuildCodeHonorsCancellationBeforeSyntaxWork()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cancelled.json");
        File.WriteAllText(path, "{\"ready\":true}");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            GeneratedHtmlPreview.TryBuildCodeForPreview(path, cancellation.Token));
    }

    [Fact]
    public void PreviewAttemptHandsNativeFallbackTheAlreadyLoadedSnapshot()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "notes.txt");
        File.WriteAllText(path, "one\ntwo\nthree");

        var attempt = GeneratedHtmlPreview.TryBuildCodeForPreview(path);

        Assert.Null(attempt.Document);
        Assert.NotNull(attempt.FallbackText);
        Assert.Equal("one\ntwo\nthree", attempt.FallbackText!.Text);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(300_000, true)]
    [InlineData(300_001, false)]
    public void SyntaxBudgetRejectsWorkBeforeAllocatingLargeFixtures(int length, bool expected)
    {
        Assert.Equal(expected, GeneratedHtmlPreview.CanHighlightCodeLength(length));
    }

    [Fact]
    public void PathologicalSingleLineGetsTheTighterNativeDisplayBudget()
    {
        var source = new string('x', PreviewWindow.LongLineDisplayLimit) + "TAIL";
        var preview = new TextPreview(source, Truncated: false, TotalBytes: source.Length);

        var formatted = PreviewWindow.FormatTextPreview("sample.map", preview);

        Assert.NotNull(formatted);
        Assert.False(formatted.Value.Wrap);
        Assert.DoesNotContain("TAIL", formatted.Value.Text, StringComparison.Ordinal);
        Assert.Contains("Showing the first", formatted.Value.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sample.torrent")]
    [InlineData("README.torrent")]
    [InlineData("LICENSE.torrent")]
    [InlineData(".env.backup.torrent")]
    public void TorrentMetadataUsesTheLightweightFileCardInsteadOfRawBencodedText(string path)
    {
        Assert.False(FileKinds.IsText(path));
        Assert.True(FileKinds.CanUseSignatureRouting(path));
    }

    [Fact]
    public void OrdinaryMultilineTextKeepsTheLargerDisplayBudget()
    {
        var source = string.Join('\n', Enumerable.Repeat("row", 31_000));
        Assert.True(source.Length > PreviewWindow.LongLineDisplayLimit);
        var preview = new TextPreview(source, Truncated: false, TotalBytes: source.Length);

        var formatted = PreviewWindow.FormatTextPreview("sample.log", preview);

        Assert.NotNull(formatted);
        Assert.Equal(source, formatted.Value.Text);
    }

    [Fact]
    public void TryBuildCodeRepairsAnIncompleteCacheFile()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "repair.json");
        File.WriteAllText(path, "{\"ready\":true}");
        var first = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;
        Assert.NotNull(first);
        File.WriteAllText(first!.HtmlPath, "<html>truncated");

        var repaired = GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document;

        Assert.NotNull(repaired);
        Assert.EndsWith("</html>", File.ReadAllText(repaired!.HtmlPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("events.log")]
    [InlineData("notes.txt")]
    [InlineData("rows.csv")]
    public void TryBuildCodeDeclinesNativeTextFiles(string fileName)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, fileName);
        File.WriteAllText(path, "one,two\nthree,four");

        Assert.Null(GeneratedHtmlPreview.TryBuildCodeForPreview(path).Document);
    }
}
