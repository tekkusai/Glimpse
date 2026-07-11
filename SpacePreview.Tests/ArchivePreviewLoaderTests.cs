using System.IO.Compression;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class ArchivePreviewLoaderTests
{
    [Fact]
    public void DeepSingleEntryIsTruncatedBeforeItCanBuildRunawayTree()
    {
        using var temp = new TempDirectory();
        var zipPath = Path.Combine(temp.Path, "deep.zip");
        var parts = Enumerable.Range(0, ArchivePreviewLoader.MaxPathDepth + 100)
            .Select(i => $"d{i}")
            .Concat(["file.txt"]);
        var entryName = string.Join("/", parts);

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write("safe");
        }

        var listing = ArchivePreviewLoader.TryLoad(zipPath);

        Assert.NotNull(listing);
        Assert.True(listing!.Truncated);
        Assert.True(MaxDepth(listing.Roots) <= ArchivePreviewLoader.MaxPathDepth);
    }

    private static int MaxDepth(IReadOnlyList<ArchiveNode> nodes)
    {
        if (nodes.Count == 0)
        {
            return 0;
        }

        return nodes.Max(node => 1 + MaxDepth(node.Children));
    }
}
