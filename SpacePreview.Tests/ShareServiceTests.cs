using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class ShareServiceTests
{
    [Theory]
    [InlineData(@"C:\Users\PreviewUser\Downloads\file.pdf")]
    [InlineData(@"\\?\C:\Users\PreviewUser\Downloads\file.pdf")]
    public void ShareAcceptsLocalDrivePaths(string path)
    {
        Assert.True(ShareService.IsShareableLocalFilePath(path));
    }

    [Theory]
    [InlineData(@"\\server\share\file.pdf")]
    [InlineData(@"\\?\UNC\server\share\file.pdf")]
    [InlineData("file://server/share/file.pdf")]
    [InlineData("")]
    public void ShareRejectsNonLocalPaths(string path)
    {
        Assert.False(ShareService.IsShareableLocalFilePath(path));
    }
}
