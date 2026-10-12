using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// What a saved page may ask for. On a local disk: local files and data:
/// URIs. On a share: its own folder tree and data: URIs, nothing else.
/// </summary>
public sealed class SavedPagePolicyTests
{
    private const string SharePage = @"\\nas\site\docs\index.html";
    private static readonly string? ShareFolder = PreviewWindow.ShareFolderOf(SharePage);

    [Theory]
    [InlineData(@"\\nas\site\docs\index.html", @"\\nas\site\docs\")]
    [InlineData(@"\\nas\site\index.html", @"\\nas\site\")]
    [InlineData(@"\\nas\site\docs\..\index.html", @"\\nas\site\")]
    [InlineData(@"\\192.168.1.20\site\a b\index.html", @"\\192.168.1.20\site\a b\")]
    [InlineData(@"C:\Users\PreviewUser\Downloads\index.html", null)]
    [InlineData(@"\\?\C:\Users\PreviewUser\Downloads\index.html", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void APageOnAShareReadsItsOwnFolder(string? page, string? folder) =>
        Assert.Equal(folder, PreviewWindow.ShareFolderOf(page));

    [Theory]
    [InlineData("file://nas/site/docs/index.html")]
    [InlineData("file://nas/site/docs/index.html#top")]
    [InlineData("file://nas/site/docs/style.css")]
    [InlineData("file://nas/site/docs/index_files/photo.png")]
    [InlineData("file://NAS/Site/Docs/photo.png")]
    [InlineData("file://nas/site/docs/a%20b/photo.png")]
    [InlineData("file://nas/site/docs/sub/../photo.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    public void ASharePageMayLoadWhatSitsInItsFolderTree(string uri) =>
        Assert.True(PreviewWindow.IsAllowedUntrustedHtmlResource(uri, ShareFolder));

    [Theory]
    [InlineData("file://nas/site/photo.png")]
    [InlineData("file://nas/site/other/photo.png")]
    [InlineData("file://nas/site/docs-old/photo.png")]
    [InlineData("file://nas/site/docs/../other/photo.png")]
    [InlineData("file://nas/site/docs/%2e%2e/other/photo.png")]
    [InlineData("file://nas/site/docs/..%5C..%5Cother%5Cphoto.png")]
    [InlineData("file://nas/private/docs/photo.png")]
    [InlineData("file://attacker.example.com/site/docs/photo.png")]
    [InlineData("file://nas.attacker.example.com/site/docs/photo.png")]
    [InlineData("file://192.168.1.20/site/docs/photo.png")]
    [InlineData("file:///C:/Users/PreviewUser/Documents/taxes.pdf")]
    [InlineData("file://localhost/C:/Users/PreviewUser/Documents/taxes.pdf")]
    [InlineData("https://nas/site/docs/photo.png")]
    [InlineData("http://example.com/pixel.png")]
    [InlineData("about:srcdoc")]
    [InlineData("about:blank")]
    [InlineData("ms-msdt:/id")]
    [InlineData("not a uri")]
    public void ASharePageReachesNothingElse(string uri) =>
        Assert.False(PreviewWindow.IsAllowedUntrustedHtmlResource(uri, ShareFolder));

    // The 3.0.0 rule, unchanged: a page on this disk starts no request to a
    // host of its choosing, the share a neighbor page was opened from included.
    [Theory]
    [InlineData("file://nas/site/docs/photo.png")]
    [InlineData("file://nas/site/docs/index.html")]
    [InlineData("file:////nas/site/docs/photo.png")]
    [InlineData("file://127.0.0.1/c$/photo.png")]
    public void ALocalPageReachesNoShare(string uri)
    {
        Assert.False(PreviewWindow.IsAllowedUntrustedHtmlResource(uri));
        Assert.False(PreviewWindow.IsAllowedUntrustedHtmlResource(
            uri,
            PreviewWindow.ShareFolderOf(@"C:\Users\PreviewUser\Downloads\index.html")));
    }

    // A frame is judged by the same rule, so inline markup in a srcdoc frame
    // never loads: sandboxed, its requests reach no resource filter at all.
    [Theory]
    [InlineData("about:srcdoc")]
    [InlineData("about:blank")]
    [InlineData("https://example.com/embed")]
    [InlineData("ms-msdt:/id")]
    public void ALocalPagesFramesAreJudgedLikeItsRequests(string uri) =>
        Assert.False(PreviewWindow.IsAllowedUntrustedHtmlResource(uri));
}
