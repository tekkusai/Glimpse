using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class CadPreviewLoaderTests
{
    [Fact]
    public void OversizeAsciiStlReadsOnlyTheBoundedSample()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "large.stl");
        using (var stream = File.Create(path))
        {
            stream.Write("solid sample\nfacet normal 0 0 1\nendfacet\nendsolid\n"u8);
            stream.SetLength(CadPreviewLoader.MaxStlSampleBytes + 1024L);
        }

        var preview = CadPreviewLoader.TryLoadStl(path);

        Assert.Contains("1+ facets", preview.Subtitle, StringComparison.Ordinal);
        Assert.Contains("Facets in first 8 MB: 1", preview.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void StlSamplingHonorsCancellation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "cancelled.stl");
        File.WriteAllText(path, "solid sample\nendsolid\n");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            CadPreviewLoader.TryLoadStl(path, cancellation.Token));
    }
}
