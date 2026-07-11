using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class AppPathsTests
{
    [Fact]
    public void DataRootCanBeIsolatedForRegressionRuns()
    {
        using var temp = new TempDirectory();

        Assert.Equal(Path.GetFullPath(temp.Path), AppPaths.ResolveDataRoot(temp.Path));
    }
}
