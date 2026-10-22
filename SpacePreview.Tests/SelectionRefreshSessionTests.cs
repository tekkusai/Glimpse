using System.Diagnostics;
using System.Windows.Interop;
using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Services;

namespace SpacePreview.Tests;

/// <summary>
/// Explorer is asked what is selected, and the glance can end and a new one
/// open before it answers. The answer belongs to the glance that asked.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SelectionRefreshSessionTests
{
    /// <summary>
    /// Space closes the glance on first.txt and opens one on second.txt in
    /// the same Explorer window while the old question is still out. Its
    /// answer, first.txt, must not take the new glance back.
    /// </summary>
    [Fact]
    public Task AnAnswerToAnEarlierGlanceDoesNotReplaceTheFileOpenedSince() => WithController(async (controller, window, temp) =>
    {
        using var owner = Hidden();
        var first = temp.CreateFile("first.txt");
        var second = temp.CreateFile("second.txt");
        Show(controller, first, owner.Handle);
        var answer = new TaskCompletionSource<ExplorerSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Fields.Set(controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) => answer.Task));
        Refresh(controller, 1);

        Fields.Call(controller, "HidePreview");
        Show(controller, second, owner.Handle);
        answer.SetResult(new ExplorerSelection(first, owner.Handle));
        await RefreshFinished(controller);

        Assert.Equal(second, window.CurrentPath);
    });

    /// <summary>
    /// The new glance opens in another Explorer window and asks there while
    /// the old question is out. Dropping the old answer must not drop the
    /// new question with it.
    /// </summary>
    [Fact]
    public Task AQuestionTheNewGlanceAskedIsAnsweredAfterTheOldAnswerIsDropped() => WithController(async (controller, window, temp) =>
    {
        using var firstOwner = Hidden();
        using var secondOwner = Hidden();
        var first = temp.CreateFile("first.txt");
        var second = temp.CreateFile("second.txt");
        var third = temp.CreateFile("third.txt");
        Show(controller, first, firstOwner.Handle);
        var answer = new TaskCompletionSource<ExplorerSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var asked = 0;
        Fields.Set(controller, "_lookUpSelection", (SpacePreviewController.SelectionLookup)((_, _, _, _, _) =>
            ++asked == 1 ? answer.Task : Task.FromResult<ExplorerSelection?>(new ExplorerSelection(third, secondOwner.Handle))));
        Refresh(controller, 1);

        Fields.Call(controller, "HidePreview");
        Show(controller, second, secondOwner.Handle);
        Refresh(controller, 2);
        answer.SetResult(new ExplorerSelection(first, firstOwner.Handle));
        await RefreshFinished(controller);

        Assert.Equal(2, asked);
        Assert.Equal(third, window.CurrentPath);
    });

    private static void Show(SpacePreviewController controller, string path, IntPtr owner) =>
        Fields.Call(controller, "ShowPreview", new ExplorerSelection(path, owner), 0L);

    /// <summary>Explorer is read again, as after a click in it or a key released.</summary>
    private static void Refresh(SpacePreviewController controller, long generation)
    {
        var request = typeof(SpacePreviewController).GetNestedType("SelectionRefreshRequest", System.Reflection.BindingFlags.NonPublic)!;
        Fields.Call(controller, "RequestSelectionRefresh", Activator.CreateInstance(request, generation, false, 0L));
    }

    private static async Task RefreshFinished(SpacePreviewController controller)
    {
        var waited = Stopwatch.StartNew();
        while (Fields.Get<bool>(controller, "_refreshInFlight") && waited.Elapsed < TimeSpan.FromSeconds(5))
        {
            await Task.Delay(10);
        }

        Assert.False(Fields.Get<bool>(controller, "_refreshInFlight"), "The refresh never finished.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    /// <summary>An invisible, unowned top-level window in this process.</summary>
    private static HwndSource Hidden() => new(new HwndSourceParameters("Glimpse refresh test")
    {
        WindowStyle = unchecked((int)0x80000000), // WS_POPUP
        Width = 1,
        Height = 1
    });

    private static Task WithController(Func<SpacePreviewController, PreviewWindow, TempDirectory, Task> test) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var controller = new SpacePreviewController(Dispatcher.CurrentDispatcher);

        // As if a fullscreen app were in front, so no test installs a real
        // mouse hook. The window is parked off screen and frozen, like the probe.
        Fields.Set(Fields.Get<FullscreenInputSuspension>(controller, "_fullscreenInputSuspension"), "_suspended", true);
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        Fields.Set(controller, "_previewWindow", window);
        try
        {
            await test(controller, window, temp);
        }
        finally
        {
            controller.Dispose();
        }
    });
}
