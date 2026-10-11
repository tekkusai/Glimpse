using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The selection sheet over a browser preview. The browser's own window
/// paints over every WPF sibling, so the sheet shows only while that window
/// is hidden, and the page comes back as it was, never reloaded.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class SelectionSheetTests
{
    [Fact]
    public Task TheSheetHidesAShownPageAndGivesTheSamePageBack() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile("first.md", "# First\n\n" + string.Concat(Enumerable.Repeat("A paragraph of prose.\n\n", 300)));
        var second = temp.CreateFile("second.md", "# Second");
        var window = OpenStage();
        try
        {
            window.ShowPreview(first, IntPtr.Zero);
            var page = await ShownPageAsync(window);
            await page.CoreWebView2.ExecuteScriptAsync("window.scrollTo(0, 400); window.stillHere = true;");
            Assert.True(NativeMethods.IsWindowVisible(page.Handle));

            window.SetMultiSelection([first, second], 0);
            window.ToggleGridView();
            Assert.True(window.IsGridViewOpen);
            Assert.False(await ShowsAfterLayoutAsync(window, page), "The page paints over the sheet.");

            window.ToggleGridView();
            Assert.Same(page, PageIn(window));
            Assert.True(await ShowsAfterLayoutAsync(window, page));
            Assert.Equal("[true,400]", await page.CoreWebView2.ExecuteScriptAsync("[window.stillHere === true, window.scrollY]"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APageThatArrivesUnderTheSheetStaysHiddenUntilItCloses() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var first = temp.CreateFile("first.md", "# First");
        var second = temp.CreateFile("second.md", "# Second");
        var window = OpenStage();
        try
        {
            window.ShowPreview(first, IntPtr.Zero);
            window.SetMultiSelection([first, second], 0);
            window.ToggleGridView();
            Assert.Null(PageIn(window));

            var page = await ShownPageAsync(window);
            Assert.True(window.IsGridViewOpen);
            Assert.False(await ShowsAfterLayoutAsync(window, page), "The page arrived over the sheet.");

            window.ToggleGridView();
            Assert.True(await ShowsAfterLayoutAsync(window, page));
        }
        finally
        {
            window.Close();
        }
    });

    private static PreviewWindow OpenStage()
    {
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        return window;
    }

    /// <summary>The page once its load has finished and its navigation completed.</summary>
    private static async Task<StagedWebView> ShownPageAsync(PreviewWindow window)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            if (Fields.Get<bool>(window, "_loadFinished")
                && PageIn(window) is { CoreWebView2: not null, NavigationCompletedAt: not null } page)
            {
                return page;
            }

            Assert.True(DateTime.UtcNow < deadline, "The page never finished loading.");
            await Task.Delay(20);
        }
    }

    /// <summary>The browser's own window, once layout has told it where it stands.</summary>
    private static async Task<bool> ShowsAfterLayoutAsync(PreviewWindow window, StagedWebView page)
    {
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        return NativeMethods.IsWindowVisible(page.Handle);
    }

    private static StagedWebView? PageIn(PreviewWindow window) => Find<StagedWebView>(window.PreviewContent);

    private static T? Find<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? Find<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
