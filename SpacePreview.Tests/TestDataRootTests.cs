using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

public sealed class TestDataRootTests
{
    [Fact]
    public void TheSuiteNeverWritesIntoTheUsersDataRoot()
    {
        Assert.StartsWith(TestDataRoot.Path, AppPaths.DataRoot, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(TestDataRoot.Path, DiagnosticLog.Pathname, StringComparison.OrdinalIgnoreCase);
    }
}
