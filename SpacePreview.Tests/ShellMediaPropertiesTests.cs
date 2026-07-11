using SpacePreview.Native;

namespace SpacePreview.Tests;

public sealed class ShellMediaPropertiesTests
{
    [Fact]
    public void AudioPropertyKeysMatchWindowsSdk()
    {
        Assert.Equal(2u, ShellMediaProperties.TitlePropertyId);
        Assert.Equal(3u, ShellMediaProperties.MediaDurationPropertyId);
        Assert.Equal(4u, ShellMediaProperties.AlbumTitlePropertyId);
        Assert.Equal(100u, ShellMediaProperties.DisplayArtistPropertyId);
    }
}
