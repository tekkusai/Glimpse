using System.Diagnostics;
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
    /// up to 50 ms for it. 3.0.0 waited 20 ms. The step races a 40 ms timer
    /// started at the same moment, so a busy machine moves both together:
    /// a 20 ms wait is always a timer tick ahead of it, a 50 ms wait never.
    /// </summary>
    [Fact]
    public Task AStepGivesABusyLoadNoLongerThan3Point0Did() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = new PreviewWindow();
        try
        {
            var busy = Task.Delay(2000);
            Fields.Set(window, "_loadAtWork", busy);

            var longer = Task.Delay(40);
            var load = (Task)Fields.Call(window, "LoadPreviewAsync", temp.Path, ItemFacts.ReadAsync(temp.Path, CancellationToken.None), true, 1L, CancellationToken.None, true)!;
            Assert.Same(busy, Fields.Get<Task>(window, "_loadAtWork"));
            while (ReferenceEquals(Fields.Get<Task>(window, "_loadAtWork"), busy) && !load.IsCompleted)
            {
                Assert.False(longer.IsCompleted, "The step was still waiting for the busy load after 40 ms.");
                await Task.Yield();
            }

            await load;
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AStepWithNothingAtWorkStartsInsideTheCall() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = new PreviewWindow();
        try
        {
            var idle = Task.CompletedTask;
            Fields.Set(window, "_loadAtWork", idle);

            var load = (Task)Fields.Call(window, "LoadPreviewAsync", temp.Path, ItemFacts.ReadAsync(temp.Path, CancellationToken.None), true, 1L, CancellationToken.None, true)!;

            Assert.NotSame(idle, Fields.Get<Task>(window, "_loadAtWork"));
            await load;
        }
        finally
        {
            window.Close();
        }
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

    private static bool IsLayered(IntPtr hwnd) =>
        (NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64() & NativeMethods.WS_EX_LAYERED) != 0;
}
