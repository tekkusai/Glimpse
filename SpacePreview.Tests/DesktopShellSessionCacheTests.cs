using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class DesktopShellSessionCacheTests
{
    [Theory]
    [InlineData(1, 1, true)]
    [InlineData(0, 0, false)]
    [InlineData(1, 0, false)]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, false)]
    [InlineData(1, 2, false)]
    public void NativeFastPathIsLimitedToOneFilesystemSelection(
        int selectedCount,
        int fileSystemPathCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            DesktopSelectionReaderPolicy.CanUseNativePath(
                selectedCount,
                fileSystemPathCount));
    }

    [Theory]
    [InlineData("Progman")]
    [InlineData("WorkerW")]
    public void IdentityRequiresLiveDesktopWindowAndSameExplorerProcess(string className)
    {
        Assert.True(DesktopShellSessionIdentity.IsValid(
            windowExists: true,
            expectedProcessId: 42,
            currentProcessId: 42,
            className));

        Assert.False(DesktopShellSessionIdentity.IsValid(false, 42, 42, className));
        Assert.False(DesktopShellSessionIdentity.IsValid(true, 0, 0, className));
        Assert.False(DesktopShellSessionIdentity.IsValid(true, 42, 43, className));
        Assert.False(DesktopShellSessionIdentity.IsValid(true, 42, 42, "CabinetWClass"));
    }

    [Fact]
    public void CacheRejectsUseFromAnyThreadExceptItsOwner()
    {
        using var cache = new DesktopShellSessionCache();

        Exception? failure = null;
        var otherThread = new Thread(() =>
        {
            try
            {
                cache.Dispose();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        otherThread.Start();
        otherThread.Join();

        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void ProductionCacheKeepsSnapshotsFreshAndBoundsRecovery()
    {
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var cache = TestSource.Read(@"SpacePreview\Services\DesktopShellSessionCache.cs");
        var sta = TestSource.Read(@"SpacePreview\Services\StaTask.cs");

        Assert.Contains("for (var attempt = 0; attempt < 2; attempt++)", service, StringComparison.Ordinal);
        Assert.Contains("StaTask.IsWorkerThread", service, StringComparison.Ordinal);
        Assert.Contains("_desktopSessionCache.GetOrCreate", service, StringComparison.Ordinal);
        Assert.Contains("ReadPreferredDesktopSelectionSnapshot(", service, StringComparison.Ordinal);
        Assert.Contains("DesktopSelectionReaderPolicy.CanUseNativePath", service, StringComparison.Ordinal);
        Assert.Contains("return ReadDesktopAutomationSnapshot(desktop, diagnostics)", service, StringComparison.Ordinal);
        Assert.Contains("DesktopShellSession.ReleaseComObject(selectedItems)", service, StringComparison.Ordinal);
        Assert.Contains("DesktopShellSession.ReleaseComObject(document)", service, StringComparison.Ordinal);
        Assert.Contains("Selection collections are deliberately never cached", cache, StringComparison.Ordinal);
        Assert.Contains("IsIdentityValid", cache, StringComparison.Ordinal);
        Assert.Contains("Environment.CurrentManagedThreadId != _ownerThreadId", cache, StringComparison.Ordinal);
        Assert.Contains("internal static bool IsWorkerThread", sta, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAbRestoresSelectionAndProvesAForcedRebuild()
    {
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");
        var service = TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs");
        var shell = TestSource.Read(@"SpacePreview\Native\ShellComInterop.cs");

        Assert.Contains("--desktop-selection-ab", app, StringComparison.Ordinal);
        Assert.Contains("DesktopSelectionAbTestAsync", app, StringComparison.Ordinal);
        Assert.Contains("finally", service, StringComparison.Ordinal);
        Assert.Contains("RestoreDesktopSelection(", service, StringComparison.Ordinal);
        Assert.Contains("iteration % 2 == 0", service, StringComparison.Ordinal);
        Assert.Contains("benchmark-forced-rebuild", service, StringComparison.Ordinal);
        Assert.Contains("ForcedRebuildSucceeded", service, StringComparison.Ordinal);
        Assert.Contains("TryGetSelectionSnapshot", shell, StringComparison.Ordinal);
        Assert.Contains("TryDeselectIndex", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Volatile.Read(ref _desktopSessionCache)", service, StringComparison.Ordinal);
        Assert.Contains("ReleaseDesktopSessionAsync().Wait", app, StringComparison.Ordinal);
    }
}
