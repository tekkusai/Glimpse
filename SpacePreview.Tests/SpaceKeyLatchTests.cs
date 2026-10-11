using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// A held Space must never machine-gun open/hide. The latch decides repeats
/// from the hook's own events, never from GetAsyncKeyState: a key the hook
/// swallowed never becomes logically down, so that read says "up" while the
/// key is still held.
/// </summary>
public sealed class SpaceKeyLatchTests
{
    private const uint Window = 700;

    [Fact]
    public void AHeldSpaceThatOpenedThePreviewStaysARepeatThroughItsAutoRepeat()
    {
        var latch = new SpaceKeyLatch(Window);
        latch.Press(time: 1000, acted: true);

        // First repeat after the keyboard delay, then at the repeat rate.
        for (var time = 1500u; time < 3500; time += 33)
        {
            Assert.True(latch.IsRepeat(time));
            latch.Press(time, acted: false);
        }
    }

    [Fact]
    public void KeyUpEndsTheHold()
    {
        var latch = new SpaceKeyLatch(Window);
        latch.Press(1000, acted: true);
        latch.Release();

        Assert.False(latch.IsRepeat(1100));
    }

    [Fact]
    public void ALostKeyUpExpiresOnceRepeatsStop()
    {
        var latch = new SpaceKeyLatch(Window);
        latch.Press(1000, acted: true);

        Assert.True(latch.IsRepeat(1000 + Window));
        Assert.False(latch.IsRepeat(1000 + Window + 1));
    }

    [Fact]
    public void APressTheKeymapLetThroughDoesNotLatch()
    {
        // Space typed into a rename box or a picker's name field passes;
        // its repeats must keep passing as fresh presses.
        var latch = new SpaceKeyLatch(Window);
        latch.Press(1000, acted: false);

        Assert.False(latch.IsRepeat(1500));
    }

    [Fact]
    public void ResetForgetsAHoldTheHookDidNotSeeEnd()
    {
        var latch = new SpaceKeyLatch(Window);
        latch.Press(1000, acted: true);
        latch.Reset();

        Assert.False(latch.IsRepeat(1033));
    }

    [Fact]
    public void TheTickCounterWrappingDoesNotBreakAHold()
    {
        var latch = new SpaceKeyLatch(Window);
        latch.Press(uint.MaxValue - 10, acted: true);

        Assert.True(latch.IsRepeat(20));
    }

    [Theory]
    [InlineData(0, 450u)]
    [InlineData(1, 700u)]
    [InlineData(3, 1200u)]
    [InlineData(9, 1200u)]
    public void TheRepeatWindowCoversTheMachinesKeyboardDelay(int setting, uint expected)
    {
        Assert.Equal(expected, SpaceKeyLatch.RepeatWindowFor(setting));
    }
}
