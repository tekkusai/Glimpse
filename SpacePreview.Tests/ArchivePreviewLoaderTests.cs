using System.IO.Compression;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class ArchivePreviewLoaderTests
{
    [Theory]
    [InlineData("A.txt", "a.txt")]
    [InlineData("Docs/x.txt", "docs/y.txt")]
    public void EntriesThatDifferOnlyByCaseKeepEveryRow(string first, string second)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "case.zip");
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            using (var stream = archive.CreateEntry(first).Open())
            {
                stream.Write(new byte[2]);
            }

            using (var stream = archive.CreateEntry(second).Open())
            {
                stream.Write(new byte[3]);
            }
        }

        var listing = ArchivePreviewLoader.TryLoad(path);

        Assert.NotNull(listing);
        Assert.Equal(2, listing.TotalEntries);
        Assert.Equal(5, listing.TotalUncompressed);
        Assert.Equal(2, listing.Roots.Count);
        foreach (var (name, size) in new[] { (first, 2L), (second, 3L) })
        {
            var parts = name.Split('/');
            var root = Assert.Single(listing.Roots, node => node.Name == parts[0]);
            Assert.Equal(size, root.Size);
            Assert.Equal(parts.Length > 1, root.IsDirectory);
            if (parts.Length > 1)
            {
                var child = Assert.Single(root.Children);
                Assert.Equal(parts[1], child.Name);
                Assert.False(child.IsDirectory);
                Assert.Equal(size, child.Size);
            }
        }
    }

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
