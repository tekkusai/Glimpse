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

    [Fact]
    public void PageFoldersDropStaleFilesThenTheOldestOverTheCap()
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var stale = Touch(temp.CreateFile("stale.html", "x"), now - TimeSpan.FromDays(20));
        var older = Touch(temp.CreateFile("older.html", new string('x', 600)), now - TimeSpan.FromDays(3));
        var newer = Touch(temp.CreateFile("newer.html", new string('x', 600)), now - TimeSpan.FromDays(1));

        var deleted = CacheHousekeeping.PruneFiles(new DirectoryInfo(temp.Path), now, maxBytes: 1000);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(stale));
        Assert.False(File.Exists(older));
        Assert.True(File.Exists(newer));
    }

    [Fact]
    public void HousekeepingCoversEveryFolderOfPagesAndArtwork()
    {
        foreach (var folder in new[] { "MarkdownHtml", "CodeHtml", "EpubHtml", "MediaHtml", "ImageHtml", "MediaArtwork" })
        {
            Assert.Contains(folder, CacheHousekeeping.PageFolders);
        }
    }

    private static string Touch(string path, DateTime writeTimeUtc)
    {
        File.SetLastWriteTimeUtc(path, writeTimeUtc);
        return path;
    }
}
