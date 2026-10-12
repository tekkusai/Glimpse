using SpacePreview.Preview;

namespace SpacePreview.Tests;

public sealed class UntrustedHtmlPolicyTests
{
    [Theory]
    [InlineData("file:///C:/Users/PreviewUser/Downloads/report.html")]
    [InlineData("file://localhost/C:/Users/PreviewUser/Downloads/report.html")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    public void AllowsOnlyLocalFileAndDataResources(string uri)
    {
        Assert.True(PreviewWindow.IsAllowedUntrustedHtmlResource(uri));
    }

    [Theory]
    [InlineData("file://attacker.example.com/share/pixel.png")]
    [InlineData("file:////attacker.example.com/share/pixel.png")]
    [InlineData("https://example.com/pixel.png")]
    [InlineData("http://example.com/pixel.png")]
    [InlineData("edge://settings/")]
    [InlineData("about:blank")]
    [InlineData("not a uri")]
    public void BlocksNetworkInternalAndMalformedResources(string uri)
    {
        Assert.False(PreviewWindow.IsAllowedUntrustedHtmlResource(uri));
    }

    [Fact]
    public void UntrustedHtmlBlocksPopupNavigationToo()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var hardener = TestSource.MethodFrom(source, "internal static void HardenForUntrustedHtml");

        Assert.Contains("core.NewWindowRequested", hardener, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", hardener, StringComparison.Ordinal);
        Assert.Contains("webview blocked popup", hardener, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\Users\PreviewUser\Downloads\report.pdf")]
    [InlineData(@"\\?\C:\Users\PreviewUser\Downloads\report.pdf")]
    [InlineData("file:///C:/Users/PreviewUser/Downloads/report.pdf")]
    [InlineData("file://localhost/C:/Users/PreviewUser/Downloads/report.pdf")]
    public void AllowsShortcutTargetsOnlyWhenLocal(string target)
    {
        Assert.True(PreviewWindow.IsSafeLocalPreviewTarget(target));
    }

    [Theory]
    [InlineData(@"\\attacker.example.com\share\report.pdf")]
    [InlineData(@"\\?\UNC\attacker.example.com\share\report.pdf")]
    [InlineData("file://attacker.example.com/share/report.pdf")]
    [InlineData("https://example.com/report.pdf")]
    public void BlocksShortcutTargetsThatWouldTouchNetwork(string target)
    {
        Assert.False(PreviewWindow.IsSafeLocalPreviewTarget(target));
    }

    [Fact]
    public void PdfNavigationAllowsOnlyOriginalFileAndExternalizesHttp()
    {
        const string source = @"C:\Users\PreviewUser\Downloads\report.pdf";

        Assert.Equal(
            PreviewWindow.PdfNavigationAction.Allow,
            PreviewWindow.DecidePdfNavigation(source, "file:///C:/Users/PreviewUser/Downloads/report.pdf#page=2"));
        Assert.Equal(
            PreviewWindow.PdfNavigationAction.Allow,
            PreviewWindow.DecidePdfNavigation(source, "file:///C:/Users/PreviewUser/Downloads/report.pdf?toolbar=0#page=3"));
        Assert.Equal(
            PreviewWindow.PdfNavigationAction.OpenExternally,
            PreviewWindow.DecidePdfNavigation(source, "https://example.com/"));
        Assert.Equal(
            PreviewWindow.PdfNavigationAction.Cancel,
            PreviewWindow.DecidePdfNavigation(source, "file://attacker.example.com/share/pixel.png"));
        Assert.Equal(
            PreviewWindow.PdfNavigationAction.Cancel,
            PreviewWindow.DecidePdfNavigation(source, "edge://settings/"));
    }

    [Fact]
    public void PdfNavigationAllowsSameEscapedFileWithFragment()
    {
        const string source = @"C:\Users\PreviewUser\Downloads\GhostGlides 310526 Rich Lock 50%.pdf";

        Assert.Equal(
            PreviewWindow.PdfNavigationAction.Allow,
            PreviewWindow.DecidePdfNavigation(
                source,
                "file:///C:/Users/PreviewUser/Downloads/GhostGlides%20310526%20Rich%20Lock%2050%25.pdf#page=2"));
    }

    [Fact]
    public void GeneratedHtmlResourcesAllowOnlyShellLocalDataAndMappedFolder()
    {
        const string htmlPath = @"C:\Users\PreviewUser\AppData\Local\Glimpse\MarkdownHtml\preview.html";
        const string resourceHost = "glimpse-md-test.local";

        Assert.True(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "file:///C:/Users/PreviewUser/AppData/Local/Glimpse/MarkdownHtml/preview.html"));
        Assert.True(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "https://glimpse-md-test.local/images/local.png"));
        Assert.True(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "data:image/png;base64,iVBORw0KGgo="));

        Assert.False(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "file:///C:/Users/PreviewUser/Downloads/notes.md"));
        Assert.False(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "file://attacker.example.com/share/pixel.png"));
        Assert.False(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "https://example.com/pixel.png"));
        Assert.False(PreviewWindow.IsAllowedGeneratedHtmlResource(htmlPath, resourceHost, "edge://settings/"));
    }

    [Fact]
    public void GeneratedHtmlNavigationExternalizesOnlyNormalWebLinks()
    {
        const string htmlPath = @"C:\Users\PreviewUser\AppData\Local\Glimpse\MarkdownHtml\preview.html";
        const string resourceHost = "glimpse-md-test.local";

        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.Allow,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "file:///C:/Users/PreviewUser/AppData/Local/Glimpse/MarkdownHtml/preview.html"));
        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.Allow,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "file:///C:/Users/PreviewUser/AppData/Local/Glimpse/MarkdownHtml/preview.html#getting-started"));
        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.OpenExternally,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "https://example.com/docs"));
        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.Cancel,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "https://glimpse-md-test.local/relative-link.md"));
        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.Cancel,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "file:///C:/Users/PreviewUser/Downloads/notes.md"));
        Assert.Equal(
            PreviewWindow.GeneratedHtmlNavigationAction.Cancel,
            PreviewWindow.DecideGeneratedHtmlNavigation(htmlPath, resourceHost, "data:text/html,hello"));
    }

    [Fact]
    public void GeneratedHtmlOpensExternalLinksOnlyFromUserInput()
    {
        var source = TestSource.Read(@"SpacePreview\Preview\PreviewWindow.xaml.cs");
        var hardener = TestSource.MethodFrom(source, "internal static void HardenForGeneratedHtml");

        Assert.Contains("args.IsUserInitiated", hardener, StringComparison.Ordinal);
        Assert.Equal(2, hardener.Split("args.IsUserInitiated", StringSplitOptions.None).Length - 1);
        Assert.Contains("args.Cancel = true", hardener, StringComparison.Ordinal);
        Assert.Contains("args.Handled = true", hardener, StringComparison.Ordinal);
    }
}
