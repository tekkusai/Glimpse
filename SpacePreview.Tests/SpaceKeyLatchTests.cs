using SpacePreview.Native;
using SpacePreview.Services;

namespace SpacePreview.Tests;

public sealed class SpaceKeyLatchTests
{
    [Fact]
    public void SuspendedSpaceKeyUpStillClearsTheNextPress()
    {
        var latch = new SpaceKeyLatch(_ => true);

        Assert.True(latch.TryPress());

        latch.Release(NativeMethods.VK_SPACE);

        Assert.True(latch.TryPress());
    }

    [Fact]
    public void HeldSpaceDoesNotRepeatUntilReleased()
    {
        var latch = new SpaceKeyLatch(_ => true);

        Assert.True(latch.TryPress());
        Assert.False(latch.TryPress());

        latch.Release(NativeMethods.VK_SPACE);

        Assert.True(latch.TryPress());
    }

    [Fact]
    public void DroppedSpaceKeyUpRepairsWhenPhysicalKeyIsReleased()
    {
        var latch = new SpaceKeyLatch(_ => false);

        Assert.True(latch.TryPress());

        latch.RepairIfReleased();

        Assert.True(latch.TryPress());
    }


    [Fact]
    public void ControllerRepairsDroppedKeyUpBeforeTreatingSpaceAsRepeat()
    {
        var source = TestSource.Read(@"SpacePreview\Services\SpacePreviewController.cs");
        var repair = source.IndexOf("_spaceKey.RepairIfReleased();", StringComparison.Ordinal);
        var repeat = source.IndexOf("space repeat ignored", StringComparison.Ordinal);

        Assert.True(repair >= 0);
        Assert.True(repair < repeat);
    }

}
