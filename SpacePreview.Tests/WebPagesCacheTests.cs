using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// The pages Glimpse writes are cached under the build that wrote them and
/// pruned with the other caches.
/// </summary>
public sealed class WebPagesCacheTests
{
    [Fact]
    public void APageCachedByAnotherBuildIsNotServed()
    {
        var key = $"stale-{Guid.NewGuid():N}";
        var stale = HtmlCache.PathFor("CodeHtml", key, buildStamp: "an-older-build");
        File.WriteAllText(stale, "<html>old look</html>");
        var renders = 0;

        var path = HtmlCache.Get("CodeHtml", key, () =>
        {
            renders++;
            return "<html>new look</html>";
        });

        Assert.Equal(1, renders);
        Assert.NotEqual(stale, path);
        Assert.Equal("<html>new look</html>", File.ReadAllText(path!));
        Assert.Equal(path, HtmlCache.Get("CodeHtml", key, () => throw new InvalidOperationException("served from cache")));
    }

    [Fact]
    public void ANullRenderCachesNothing()
    {
        var key = $"none-{Guid.NewGuid():N}";

        Assert.Null(HtmlCache.Get("CodeHtml", key, () => null));
        Assert.False(File.Exists(HtmlCache.PathFor("CodeHtml", key)));
    }

    [Fact]
    public void ACacheHitRestartsThePagesClock()
    {
        var key = $"hit-{Guid.NewGuid():N}";
        var path = HtmlCache.Get("CodeHtml", key, () => "<html>page</html>")!;
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(3));

        HtmlCache.Get("CodeHtml", key, () => throw new InvalidOperationException("served from cache"));

        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1));
    }

    // 2.0 kept Office pictures in a folder nothing has written since. It
    // goes whole, where a listed cache folder would only ever be thinned.
    [Fact]
    public void AFolderOnlyOlderReleasesWroteIsRemoved()
    {
        using var temp = new TempDirectory();
        var leftover = Directory.CreateDirectory(Path.Combine(temp.Path, "OfficeMedia")).FullName;
        File.WriteAllText(Path.Combine(leftover, "image1.png"), "png");

        CacheHousekeeping.Run(temp.Path, DateTime.UtcNow);

        Assert.False(Directory.Exists(leftover));
        Assert.DoesNotContain("OfficeMedia", CacheHousekeeping.Folders);
    }

    [Fact]
    public void HousekeepingCoversEveryFolderOfPagesAndArtwork()
    {
        foreach (var folder in new[] { "MarkdownHtml", "CodeHtml", "EpubHtml", "MediaHtml", "ImageHtml", "MediaArtwork" })
        {
            Assert.Contains(folder, CacheHousekeeping.Folders);
        }
    }
}
