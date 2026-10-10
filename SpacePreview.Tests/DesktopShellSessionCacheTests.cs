using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class DesktopShellSessionCacheTests
{
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

    // The cache lives on the shell STA. A UI-thread peek could race a queued
    // lookup that creates it, so release always queues behind outstanding
    // work, and exit waits for that release.
    [Fact]
    public void TheSessionIsReleasedOnTheShellStaBehindQueuedWork()
    {
        var service = TestSource.CodeMethod(@"SpacePreview\Services\ExplorerSelectionService.cs", "ReleaseDesktopSessionAsync");
        var app = TestSource.Read(@"SpacePreview\App.xaml.cs");

        Assert.Contains("StaTask.Run", service, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Volatile.Read(ref _desktopSessionCache)",
            TestSource.Read(@"SpacePreview\Services\ExplorerSelectionService.cs"),
            StringComparison.Ordinal);
        Assert.Contains("ReleaseDesktopSessionAsync().Wait", app, StringComparison.Ordinal);
    }
}
