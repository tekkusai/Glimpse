using System.IO.Compression;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A page chosen in a rail stays chosen. The rail follows the document when
/// the reader moves it, and only then: in 3.0.3 a choice could jump back to
/// the page before, with a flash, when an answer about the document from
/// before the choice, or about Glimpse's own move, read as the reader moving.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RailChoiceTests
{
    // The viewer's page box reads the typed page a few milliseconds after
    // Enter. Asked at once, one jump in four found the page before, counted
    // the jump as missed and reloaded the whole document: the flash.
    [Fact]
    public Task AChosenPageIsReachedWithoutReloadingTheDocument() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        var viewer = pages.Viewer;

        foreach (var page in new[] { 4, 5, 4, 5, 7, 4, 5, 4, 2, 6, 9, 12, 6, 3, 8, 5 })
        {
            pages.Choose(page);
            await pages.SettledAsync();

            Assert.Same(viewer, pages.Viewer);
            Assert.Equal(page, await pages.PageBoxAsync());
        }
    });

    // A jump that missed stages a viewer at its page, and the staged
    // viewer's address names that page. In 3.0.3 the page sync read the
    // address as the reader moving, and took the rail back there after the
    // reader had chosen another page. The jump holds the sync off while the
    // viewer stages, as it does here.
    [Fact]
    public Task AStagedViewerDoesNotTakeTheRailBackToItsPage() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        pages.Choose(3);
        await pages.SettledAsync();
        var first = pages.Viewer;

        Fields.Set(pages.Rail, "JumpInFlight", true);
        Fields.Call(pages.Window, "SetPdfCurrentPageFromViewer", pages.Rail, 5, false);
        Fields.Call(pages.Window, "StagePdfViewerJump", pages.Rail, 5);
        await UntilAsync(() => !ReferenceEquals(first, pages.Viewer), "The staged viewer never took the place of the first.");

        // Long enough for the staged viewer to take typing.
        await Task.Delay(800);
        Fields.Set(pages.Rail, "JumpInFlight", false);
        pages.Choose(9);

        Assert.Equal(new[] { 9 }, await pages.RailShowsAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(9, await pages.PageBoxAsync());
    });

    // A staged viewer lands on the page it was sent to, half a second or
    // more later. A page chosen meanwhile is where the reader ends.
    [Fact]
    public Task APageChosenWhileAViewerStagesIsWhereTheReaderEnds() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        var first = pages.Viewer;

        Fields.Call(pages.Window, "SetPdfCurrentPageFromViewer", pages.Rail, 7, false);
        Fields.Call(pages.Window, "StagePdfViewerJump", pages.Rail, 7);
        pages.Choose(9);
        await pages.SettledAsync();
        await UntilAsync(() => pages.Surface.Children.OfType<WebView2>().Count() == 1, "The staged viewer was never let go.");

        Assert.Same(first, pages.Viewer);
        Assert.Equal(9, await pages.PageBoxAsync());
        Assert.Equal(new[] { 9 }, await pages.RailShowsAsync(TimeSpan.FromSeconds(1)));
    });

    // Sign opens on the page the reader is on. A page chosen in the rail is
    // where they are going, even before the viewer has got there: Sign asked
    // the viewer, still on the page before, and took the rail back with it.
    [Fact]
    public Task SignOpensOnAPageChosenBeforeTheViewerGotThere() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);

        // A jump still typing into the viewer holds this one back.
        Fields.Set(pages.Rail, "JumpInFlight", true);
        pages.Choose(5);

        Assert.Equal((5, 12), await pages.ReadPositionAsync());
        Assert.True(pages.Shows(5));
    });

    // The same, with the page chosen while Sign's question waits its turn:
    // the answer describes the viewer before the choice.
    [Fact]
    public Task SignOpensOnAPageChosenWhileItAskedTheViewer() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        var turn = Fields.Get<SemaphoreSlim>(pages.Rail, "ViewerTurn");
        await turn.WaitAsync();
        Task<(int, int)> asked;
        try
        {
            asked = pages.ReadPositionAsync();
            Fields.Set(pages.Rail, "JumpInFlight", true);
            pages.Choose(5);
        }
        finally
        {
            turn.Release();
        }

        Assert.Equal((5, 12), await asked);
        Assert.True(pages.Shows(5));
    });

    // The rail still follows a reader who moves the pages after a choice.
    [Fact]
    public Task TheRailFollowsTheReaderAfterAChoice() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        pages.Choose(3);
        await pages.SettledAsync();

        await PdfViewerWheel.ScrollAsync(pages.Viewer.CoreWebView2, 3000);

        var shown = await pages.PageBoxAsync();
        Assert.True(shown > 3, "The wheel never moved the viewer.");
        await UntilAsync(() => pages.CurrentPage == shown, $"The rail stayed on {pages.CurrentPage}, the viewer is on {shown}.");
        Assert.True(pages.Shows(shown));
    });

    // A short last chapter cannot scroll to the top of the view, so the page
    // still names the chapter before it after a jump there. The rail took
    // that for the reader moving back.
    [Fact]
    public Task AShortLastChapterStaysChosen() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "book.epub");
        string[] titles = ["Long one", "Long two", "Long three", "Short end"];
        WriteBook(path, titles);
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        try
        {
            window.MarkGeometryUserPinnedForProbe();
            window.ShowPreview(path, IntPtr.Zero);
            var tiles = await UntilAsync(() => ChapterTiles(window, titles), "The contents never showed.");
            CoreWebView2? page = null;
            await UntilAsync(
                async () => (page = FindDescendants<WebView2>(window).FirstOrDefault(view => view.IsVisible)?.CoreWebView2) is not null
                    && await page.ExecuteScriptAsync("document.getElementById('ch-3')!==null") == "true",
                "The book never loaded.");

            Click(tiles["Short end"]);
            var seen = new List<string>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                seen.Add(string.Join(",", tiles.Where(tile => tile.Value.Tag is true).Select(tile => tile.Key)));
                await Task.Delay(10);
            }

            Assert.Equal(new[] { "Short end" }, seen.Distinct());
            Assert.NotEqual("0", await page!.ExecuteScriptAsync("Math.round(window.scrollY)"));

            // And the rail follows the reader who scrolls back from there.
            await PdfViewerWheel.ScrollAsync(page!, -300);
            await UntilAsync(() => tiles["Long three"].Tag is true, "The rail stopped following the page.");
        }
        finally
        {
            window.Close();
        }
    });

    private static void Click(Border tile) =>
        tile.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });

    /// <summary>The window's chapter tiles by title, once the rail has one for each.</summary>
    private static Dictionary<string, Border>? ChapterTiles(PreviewWindow window, string[] titles)
    {
        var tiles = FindDescendants<Border>(window)
            .Where(border => border.ToolTip is string title && titles.Contains(title))
            .ToList();
        return tiles.Count == titles.Length ? tiles.ToDictionary(tile => (string)tile.ToolTip) : null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found)
            {
                yield return found;
            }

            foreach (var deeper in FindDescendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    /// <summary>A book whose chapters each fill the view several times, but for a last one of a line.</summary>
    private static void WriteBook(string path, string[] titles)
    {
        using var stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        void Entry(string name, string text)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }

        Entry("mimetype", "application/epub+zip");
        Entry("META-INF/container.xml", """
<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles><rootfile full-path="content.opf" media-type="application/oebps-package+xml"/></rootfiles>
</container>
""");
        var manifest = "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>";
        var spine = string.Empty;
        var links = string.Empty;
        for (var i = 0; i < titles.Length; i++)
        {
            manifest += $"<item id=\"c{i}\" href=\"c{i}.xhtml\" media-type=\"application/xhtml+xml\"/>";
            spine += $"<itemref idref=\"c{i}\"/>";
            links += $"<li><a href=\"c{i}.xhtml\">{titles[i]}</a></li>";
            var body = i == titles.Length - 1
                ? "<p>The end.</p>"
                : string.Concat(Enumerable.Repeat("<p>A paragraph long enough to take a line or two of the page, and then some more words.</p>", 120));
            Entry($"c{i}.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\"><body><h1>{titles[i]}</h1>{body}</body></html>");
        }

        Entry("nav.xhtml", $"<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><body><nav epub:type=\"toc\"><ol>{links}</ol></nav></body></html>");
        Entry("content.opf",
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:title>Book</dc:title></metadata>"
            + $"<manifest>{manifest}</manifest><spine>{spine}</spine></package>");
    }

    /// <summary>A PDF in a real viewer with its page rail, on a window that stays off screen.</summary>
    private sealed class Pages : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public PreviewWindow Window { get; private set; } = null!;
        public object Rail { get; private set; } = null!;

        public WebView2 Viewer => Fields.Get<WebView2>(Rail, "WebView");
        public Grid Surface => Fields.Get<Grid>(Rail, "Surface");
        public int CurrentPage => Fields.Get<int>(Rail, "CurrentPage");
        private Dictionary<int, Border> Tiles => Fields.Get<Dictionary<int, Border>>(Rail, "PageTiles");

        public static async Task<Pages> ShowAsync(int count)
        {
            var pages = new Pages();
            var path = Path.Combine(pages._temp.Path, "pages.pdf");
            FillSignBehaviorTests.WritePdf(path, 612, 792, count);
            pages.Window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
            pages.Window.MarkGeometryUserPinnedForProbe();
            pages.Window.ShowPreview(path, IntPtr.Zero);
            pages.Rail = await UntilAsync(
                () => Fields.Get<bool>(pages.Window, "_loadFinished") ? Fields.Get<object?>(pages.Window, "_activePdfRail") : null,
                "The PDF never showed.");

            // The rail's thumbnails come from a helper this host may not start.
            if (pages.Tiles.Count != count)
            {
                Fields.Call(pages.Window, "ApplyPdfPageCount", pages.Rail, count, 612.0 / 792);
            }

            // The viewer has its page box once it has answered.
            await UntilAsync(async () => await pages.PageBoxAsync() == 1, "The viewer never said where it was.");
            return pages;
        }

        /// <summary>A click on a page's tile, the way the rail takes it.</summary>
        public void Choose(int page) => Click(Tiles[page]);

        /// <summary>The page and count Fill &amp; Sign opens on, asked the way it asks.</summary>
        public async Task<(int, int)> ReadPositionAsync()
        {
            var read = (Task)Fields.Call(Window, "ReadPdfPositionAsync")!;
            await read;
            return ((int, int))read.GetType().GetProperty("Result")!.GetValue(read)!;
        }

        /// <summary>Waits until the last jump is done with the viewer.</summary>
        public Task SettledAsync() =>
            UntilAsync(() => !Fields.Get<bool>(Rail, "JumpInFlight"), "The jump never finished.");

        /// <summary>Whether the rail highlights <paramref name="page"/> and only it.</summary>
        public bool Shows(int page) =>
            Tiles.Where(tile => tile.Value.Tag is true).Select(tile => tile.Key).SequenceEqual([page]);

        /// <summary>Every page the rail highlighted over <paramref name="span"/>.</summary>
        public async Task<int[]> RailShowsAsync(TimeSpan span)
        {
            var seen = new List<int>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < span)
            {
                seen.AddRange(Tiles.Where(tile => tile.Value.Tag is true).Select(tile => tile.Key));
                seen.Add(CurrentPage);
                await Task.Delay(10);
            }

            return [.. seen.Distinct()];
        }

        /// <summary>What the viewer's own page box says, read without the product's asker.</summary>
        public async Task<int> PageBoxAsync()
        {
            var hwnd = new WindowInteropHelper(Window).Handle;
            return await Task.Run(() =>
            {
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
                while (true)
                {
                    foreach (AutomationElement edit in AutomationElement.FromHandle(hwnd).FindAll(
                        TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
                    {
                        if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                            && int.TryParse(((ValuePattern)pattern).Current.Value, out var page))
                        {
                            return page;
                        }
                    }

                    Assert.True(DateTime.UtcNow < deadline, "The viewer never showed a page box.");
                    Thread.Sleep(50);
                }
            });
        }

        public void Dispose()
        {
            Window.Close();
            _temp.Dispose();
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> value, string failure)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (value() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string failure) =>
        await UntilAsync(() => condition() ? string.Empty : null, failure);

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100);
        }
    }
}
