using System.Windows.Media;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Every page Glimpse writes starts from one stylesheet generated from the
/// palettes, so a web page and the WPF chrome beside it cannot drift apart.
/// </summary>
public sealed class WebPagesStyleTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheStylesheetCarriesBothPalettesLightFirstAndDarkBehindTheScheme(bool dark)
    {
        var p = dark ? Theme.Dark : Theme.Light;
        var css = GeneratedHtmlTheme.PageCss;
        var split = css.IndexOf("@media (prefers-color-scheme: dark)", StringComparison.Ordinal);
        Assert.True(split > 0);
        var tokens = dark ? css[split..] : css[..split];

        Assert.Contains($"color-scheme: {(dark ? "dark" : "light")};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--canvas: {Hex(p.CanvasBg)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--chrome: {Hex(p.ChromeBg)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--raised: {Hex(p.RaisedBg)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--raised-border: {Hex(p.RaisedBorder)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--border: {Hex(p.Border)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--text: {Hex(p.TextPrimary)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--text-2: {Hex(p.TextSecondary)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--text-3: {Hex(p.TextTertiary)};", tokens, StringComparison.Ordinal);
        Assert.Contains($"--accent: {Hex(p.Accent)};", tokens, StringComparison.Ordinal);

        // Same thumb and selection as PreviewScrollBar and the WPF text views.
        var thumb = p.ScrollThumb;
        var accent = p.Accent;
        Assert.Contains($"--thumb: rgba({thumb.R},{thumb.G},{thumb.B},0.6);", tokens, StringComparison.Ordinal);
        Assert.Contains($"--thumb-hover: rgba({thumb.R},{thumb.G},{thumb.B},0.95);", tokens, StringComparison.Ordinal);
        Assert.Contains($"--selection: rgba({accent.R},{accent.G},{accent.B},0.55);", tokens, StringComparison.Ordinal);

        // No second media query: the browser's scheme has one thing to pick.
        Assert.Equal(1, css.Split("prefers-color-scheme").Length - 1);
    }

    [Fact]
    public void EveryPageGlimpseWritesStartsFromTheOneStylesheet()
    {
        using var temp = new TempDirectory();
        var markdown = GeneratedHtmlPreview.TryBuildMarkdown(temp.CreateFile("notes.md", "# Notes\n\nBody"));
        var code = GeneratedHtmlPreview.TryBuildCodeForPreview(temp.CreateFile("app.json", "{\"a\":1}")).Document;
        var image = ImagePage.Build(temp.CreateFile("logo.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"8\" height=\"8\"/>"));
        Assert.NotNull(markdown);
        Assert.NotNull(code);

        AssertSharedPageChrome(File.ReadAllText(markdown.HtmlPath));
        AssertSharedPageChrome(File.ReadAllText(code.HtmlPath));
        AssertSharedPageChrome(File.ReadAllText(image.LocalPath));
        AssertSharedPageChrome(MediaPlayerPage.Page(isAudio: true));
        AssertSharedPageChrome(MediaPlayerPage.Page(isAudio: false));
        AssertSharedPageChrome(SheetHtml.Page("Budget", "<table><colgroup width=\"80\"></colgroup><tr><td>1</td></tr></table>"));
        AssertSharedPageChrome(GeneratedHtmlPreview.BuildShell("<div class=\"md-root\"></div>"));
    }

    // One name per token: a page spells a color the way the stylesheet
    // does, and no page keeps a palette of its own beside it.
    [Fact]
    public void NoPageDefinesAThemeOfItsOwn()
    {
        var own = new[]
        {
            SheetHtml.Style,
            MediaPlayerPage.Page(isAudio: true),
            ImagePage.Page(@"C:\pictures\logo.svg", isSvg: true, new System.Windows.Size(8, 8), checker: true)
        };

        foreach (var page in own)
        {
            var rest = page.Replace(GeneratedHtmlTheme.PageCss, string.Empty, StringComparison.Ordinal);
            Assert.NotEqual(page, rest);
            Assert.DoesNotContain("prefers-color-scheme", rest, StringComparison.Ordinal);
            Assert.DoesNotContain("glimpse-in", rest, StringComparison.Ordinal);
            Assert.DoesNotContain("::selection", rest, StringComparison.Ordinal);
            Assert.DoesNotContain("-webkit-scrollbar", rest, StringComparison.Ordinal);
            foreach (var palette in new[] { Theme.Light, Theme.Dark })
            {
                Assert.DoesNotContain(Hex(palette.CanvasBg), rest, StringComparison.OrdinalIgnoreCase);
            }
        }

        // The names the sheet used to keep for the same three tokens.
        Assert.DoesNotContain("--edge", SheetHtml.Style, StringComparison.Ordinal);
        Assert.DoesNotContain("--label", SheetHtml.Style, StringComparison.Ordinal);
        Assert.DoesNotContain("--select:", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains("var(--raised-border)", SheetHtml.Style, StringComparison.Ordinal);
        Assert.Contains("var(--text-2)", SheetHtml.Style, StringComparison.Ordinal);
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

    /// <summary>The stylesheet every page carries, and the rules it relies on.</summary>
    internal static void AssertSharedPageChrome(string page)
    {
        Assert.Contains(GeneratedHtmlTheme.PageCss, page, StringComparison.Ordinal);

        // The page fades itself in. A forward fill would pin opacity and turn
        // the host's in-page exit fade into a cut.
        Assert.Contains("animation: glimpse-in 160ms cubic-bezier(.2,0,0,1) backwards;", page, StringComparison.Ordinal);
        Assert.Contains("@keyframes glimpse-in { from { opacity: 0; } }", page, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", page, StringComparison.Ordinal);
        Assert.Contains("::-webkit-scrollbar-thumb", page, StringComparison.Ordinal);
        Assert.Contains("::selection { background: var(--selection); }", page, StringComparison.Ordinal);

        // Chromium lets the standard properties override the pseudo-elements.
        Assert.DoesNotContain("scrollbar-color", page, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollbar-width", page, StringComparison.Ordinal);
        Assert.DoesNotContain("sp-reveal", page, StringComparison.Ordinal);
    }

    internal static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
