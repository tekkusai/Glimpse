using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A browse step and the load before it, and a fade a browse step cuts short.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class BrowseLoadAndFadeTests
{
    /// <summary>
    /// The fault: a step that found the load before it still at work waited
    /// up to 50 ms for it, held key or not. 3.0.0 waited 20 ms. Both waits
    /// are measured here on the same machine at the same moment, so a busy
    /// machine moves them together.
    /// </summary>
    [Fact]
    public Task ATapLeavesABusyLoadSoonerThanAHeldKeyDoes() => WpfApp.RunAsync(async () =>
    {
        var tap = await StepAsync(busyMilliseconds: 2000, () => false);
        var held = await StepAsync(busyMilliseconds: 2000, () => true);

        Assert.True(held.Waited.TotalMilliseconds >= 49, $"A held key gave up on the busy load after {held.Waited.TotalMilliseconds:0} ms.");
        Assert.True(
            tap.Waited.TotalMilliseconds + 5 < held.Waited.TotalMilliseconds,
            $"A tap waited {tap.Waited.TotalMilliseconds:0} ms for a busy load and a held key {held.Waited.TotalMilliseconds:0} ms.");
    });

    [Fact]
    public Task AHeldKeyWaitsForTheBusyLoadToLetGo() => WpfApp.RunAsync(async () =>
    {
        var step = await StepAsync(busyMilliseconds: 35, () => true);

        Assert.True(step.Waited.TotalMilliseconds >= 34, $"The step left after {step.Waited.TotalMilliseconds:0} ms with the load still at work.");
    });

    [Fact]
    public Task LettingTheKeyGoEndsTheWait() => WpfApp.RunAsync(async () =>
    {
        var reads = 0;
        var step = await StepAsync(busyMilliseconds: 2000, () => ++reads < 2);

        // Read once at 20 ms and found down, read again and found up. A
        // wait the cap ended would have read the key until the cap.
        Assert.True(reads == 2 || step.Waited.TotalMilliseconds >= 50, $"The key was read {reads} times in {step.Waited.TotalMilliseconds:0} ms.");
    });

    [Fact]
    public Task AStepWithNothingAtWorkNeverAsksTheKeyboard() => WpfApp.RunAsync(async () =>
    {
        await StepAsync(busyMilliseconds: 0, () => throw new InvalidOperationException("An idle step read the keyboard."));
    });

    [Fact]
    public Task ACancelledOpeningFadeAtFullAlphaDropsTheLayeredStyle() => WpfApp.RunAsync(async () =>
    {
        var window = new PreviewWindow();
        using var native = TestWindow.Root("GlimpseFadeTest");
        var hwnd = native.Handle;
        Fields.Set(window, "Hwnd", hwnd);
        Motion.Override = true;
        try
        {
            Fields.Call(window, "SetWindowAlpha", hwnd, (byte)254);
            Assert.True(IsLayered(hwnd));
            var presentation = Fields.Call(window, "BeginPresentation", true, 0L, Stopwatch.GetTimestamp(), null)!;
            Fields.Call(window, "FadeWindowIn", presentation);
            var timer = Fields.Get<DispatcherTimer>(window, "_fadeTimer");
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            timer.Tick += (_, _) =>
            {
                if (!cancelled.Task.IsCompleted && timer.IsEnabled && Fields.Get<byte>(window, "_windowAlpha") == 255)
                {
                    var layered = IsLayered(hwnd);
                    // A browse invalidates the opening timer without starting another fade.
                    Fields.Set(window, "_fadeGeneration", Fields.Get<int>(window, "_fadeGeneration") + 1);
                    cancelled.SetResult(layered);
                }
            };

            Assert.True(await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            while (timer.IsEnabled)
            {
                await Task.Delay(1);
            }

            Assert.False(IsLayered(hwnd));
            Assert.False(NativeMethods.IsWindowVisible(hwnd));
        }
        finally
        {
            Fields.Get<DispatcherTimer>(window, "_fadeTimer")?.Stop();
            Motion.Override = null;
            window.Close();
        }
    });

    /// <summary>One browse step onto an empty folder, with the load before it busy for that long.</summary>
    private static async Task<(TimeSpan Waited, TimeSpan Total)> StepAsync(int busyMilliseconds, Func<bool> held)
    {
        using var temp = new TempDirectory();
        var window = new PreviewWindow();
        try
        {
            var busy = busyMilliseconds == 0 ? Task.CompletedTask : Task.Delay(busyMilliseconds);
            Fields.Set(window, "_loadAtWork", busy);

            // By name and forgiving, so the same test runs against the code
            // from before the keyboard was asked.
            typeof(PreviewWindow)
                .GetField("_browseKeyHeld", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(window, held);

            var clock = Stopwatch.StartNew();
            var load = (Task)Fields.Call(window, "LoadPreviewAsync", temp.Path, temp.Path, 1L, CancellationToken.None, true)!;

            // The step is under way once it has put its own load in place.
            while (ReferenceEquals(Fields.Get<Task>(window, "_loadAtWork"), busy) && !load.IsCompleted)
            {
                await Task.Yield();
            }

            var waited = clock.Elapsed;
            await load;
            return (waited, clock.Elapsed);
        }
        finally
        {
            window.Close();
        }
    }

    private static bool IsLayered(IntPtr hwnd) =>
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_LAYERED) != 0;
}
