using System.Windows.Media;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Every page Glimpse writes draws from one token block generated from the
/// palette, so a web page and the WPF chrome beside it cannot drift apart.
/// </summary>
public sealed class WebPagesStyleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PageTokensComeFromTheLivePalette(bool dark)
    {
        var p = dark ? Theme.Dark : Theme.Light;
        var css = GeneratedHtmlTheme.PageCss(p);

        Assert.Contains($"color-scheme: {(dark ? "dark" : "light")};", css, StringComparison.Ordinal);
        Assert.Contains($"--canvas: {Hex(p.CanvasBg)};", css, StringComparison.Ordinal);
        Assert.Contains($"--raised: {Hex(p.RaisedBg)};", css, StringComparison.Ordinal);
        Assert.Contains($"--raised-border: {Hex(p.RaisedBorder)};", css, StringComparison.Ordinal);
        Assert.Contains($"--border: {Hex(p.Border)};", css, StringComparison.Ordinal);
        Assert.Contains($"--text: {Hex(p.TextPrimary)};", css, StringComparison.Ordinal);
        Assert.Contains($"--text-2: {Hex(p.TextSecondary)};", css, StringComparison.Ordinal);
        Assert.Contains($"--text-3: {Hex(p.TextTertiary)};", css, StringComparison.Ordinal);
        Assert.Contains($"--accent: {Hex(p.Accent)};", css, StringComparison.Ordinal);

        // Same thumb and selection as PreviewScrollBar and the WPF text views.
        var thumb = p.ScrollThumb;
        var accent = p.Accent;
        Assert.Contains($"--thumb: rgba({thumb.R},{thumb.G},{thumb.B},0.6);", css, StringComparison.Ordinal);
        Assert.Contains($"--thumb-hover: rgba({thumb.R},{thumb.G},{thumb.B},0.95);", css, StringComparison.Ordinal);
        Assert.Contains($"--selection: rgba({accent.R},{accent.G},{accent.B},0.55);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedPagesShareOneTokenBlockAndRevealThemselves()
    {
        using var temp = new TempDirectory();
        var markdown = GeneratedHtmlPreview.TryBuildMarkdown(temp.CreateFile("notes.md", "# Notes\n\nBody"));
        var code = GeneratedHtmlPreview.TryBuildCodeForPreview(temp.CreateFile("app.json", "{\"a\":1}")).Document;
        Assert.NotNull(markdown);
        Assert.NotNull(code);

        AssertSharedPageChrome(File.ReadAllText(markdown!.HtmlPath));
        AssertSharedPageChrome(File.ReadAllText(code!.HtmlPath));
    }

    [Fact]
    public void MarkdownHeadingLinksStayInsideTheDocument()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile(
            "guide.md",
            "[Jump](#target)\n\n![local](img/a.png)\n![rooted](/b.png)\n![remote](https://example.com/c.png)\n\n## Target\n");

        var document = GeneratedHtmlPreview.TryBuildMarkdown(path);

        // A <base> pointed "#target" at the virtual host, a different
        // document the navigation policy cancels; without one the link
        // scrolls this page.
        Assert.NotNull(document);
        var host = document!.ResourceHost!;
        var html = File.ReadAllText(document.HtmlPath);
        Assert.DoesNotContain("<base", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"#target\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"target\"", html, StringComparison.Ordinal);

        // Relative images still load from the file's folder; remote ones never do.
        Assert.Contains($"src=\"https://{host}/img/a.png\"", html, StringComparison.Ordinal);
        Assert.Contains($"src=\"https://{host}/b.png\"", html, StringComparison.Ordinal);
        Assert.True(PreviewWindow.IsAllowedGeneratedHtmlResource(document.HtmlPath, host, $"https://{host}/img/a.png"));
        Assert.DoesNotContain("example.com", html, StringComparison.Ordinal);
        Assert.Contains("Remote image blocked", html, StringComparison.Ordinal);
    }

    /// <summary>The block every page carries, and the rules it relies on.</summary>
    internal static void AssertSharedPageChrome(string page)
    {
        Assert.Contains(GeneratedHtmlTheme.PageCss(Theme.Current), page, StringComparison.Ordinal);

        // The page fades itself in. A forward fill would pin opacity and turn
        // the host's in-page exit fade into a cut.
        Assert.Contains("animation: glimpse-in 160ms cubic-bezier(.2,0,0,1) backwards;", page, StringComparison.Ordinal);
        Assert.Contains("@keyframes glimpse-in { from { opacity: 0; } }", page, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", page, StringComparison.Ordinal);
        Assert.Contains("::-webkit-scrollbar-thumb", page, StringComparison.Ordinal);
        Assert.Contains("::selection", page, StringComparison.Ordinal);

        // Chromium lets the standard properties override the pseudo-elements.
        Assert.DoesNotContain("scrollbar-color", page, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollbar-width", page, StringComparison.Ordinal);
        Assert.DoesNotContain("sp-reveal", page, StringComparison.Ordinal);
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
