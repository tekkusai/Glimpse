using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The page for images the browser draws: styled by the shared stylesheet,
/// cached like every other page Glimpse writes.
/// </summary>
public sealed class WebPagesImageTests
{
    private const string Logo = """<svg xmlns="http://www.w3.org/2000/svg" width="120" height="40"><rect width="120" height="40"/></svg>""";

    [Fact]
    public void TheImagePageTakesItsColorsFromTheSharedTokens()
    {
        using var temp = new TempDirectory();

        var html = File.ReadAllText(ImagePage.Build(temp.CreateFile("logo.svg", Logo)).LocalPath);

        WebPagesStyleTests.AssertSharedPageChrome(html);
        // The checkerboard under a transparent picture turns with the theme.
        Assert.Contains("repeating-conic-gradient(var(--raised) 0 25%, var(--raised-border) 0 50%)", html, StringComparison.Ordinal);
        // The zoom readout is the fixed dark glass in both themes.
        Assert.Contains($"color: {WebPagesStyleTests.Hex(Theme.OverlayText)};", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheImagePageKeepsItsGestureNumbersAndShape()
    {
        using var temp = new TempDirectory();

        var html = File.ReadAllText(ImagePage.Build(temp.CreateFile("logo.svg", Logo)).LocalPath);

        Assert.Contains("const STEP = 1.15, MAX = 32,", html, StringComparison.Ordinal);
        Assert.Contains("const VECTOR = true, HINT_W = 120, HINT_H = 40;", html, StringComparison.Ordinal);
        Assert.Contains("class=\"checker\"", html, StringComparison.Ordinal);
        Assert.Contains("window.glimpseZoom =", html, StringComparison.Ordinal);
        Assert.Contains("window.glimpseToggle =", html, StringComparison.Ordinal);
    }

    // The same cache as every other page: a complete file under the build
    // that wrote it, served again without a rewrite another window could
    // be reading, and a new page when the image's shape changes.
    [Fact]
    public void TheImagePageIsCachedLikeEveryOtherPage()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("logo.svg", Logo);

        var first = ImagePage.Build(path).LocalPath;

        Assert.True(HtmlCache.IsComplete(first));
        Assert.Equal(AppPaths.InDataRoot("ImageHtml"), Path.GetDirectoryName(first));
        const string marker = "<html>cache-sentinel</html>";
        File.WriteAllText(first, marker);
        Assert.Equal(first, ImagePage.Build(path).LocalPath);
        Assert.Equal(marker, File.ReadAllText(first));

        File.WriteAllText(path, Logo.Replace("width=\"120\"", "width=\"240\"", StringComparison.Ordinal));
        var reshaped = ImagePage.Build(path).LocalPath;
        Assert.NotEqual(first, reshaped);
        Assert.Contains("HINT_W = 240", File.ReadAllText(reshaped), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNameNeverBecomesMarkup()
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile("a'b&c #1.svg", Logo);

        var html = File.ReadAllText(ImagePage.Build(path).LocalPath);

        Assert.Contains("a&#39;b&amp;c%20%231.svg\" class=\"checker\">", html, StringComparison.Ordinal);
    }
}
