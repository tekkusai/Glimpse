using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A page chosen in a rail stays chosen, jumps directly without reloading the
/// document, and stays in sync when the reader scrolls the viewer afterwards.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RailChoiceTests
{
    [Fact]
    public Task AChosenPageIsReachedWithoutReloadingTheDocument() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        var viewer = pages.Viewer;

        foreach (var page in new[] { 4, 5, 4, 5, 7, 4, 5, 4, 2, 6, 9, 12, 6, 3, 8, 5 })
        {
            pages.Choose(page);
            await pages.SettledOnAsync(page);

            Assert.Same(viewer, pages.Viewer);
            Assert.Equal(page, await pages.PageBoxAsync());
        }
    });

    [Fact]
    public Task SignOpensOnAPageChosenInTheRail() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);

        pages.Choose(5);

        Assert.Equal((5, 12), await pages.ReadPositionAsync());
        Assert.True(pages.Shows(5));
    });

    [Fact]
    public Task AViewerStaysWhenTheOneStagedToReplaceItCannotLoad() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        pages.Choose(3);
        await pages.SettledOnAsync(3);
        var first = pages.Viewer;
        var missing = pages.Document + ".gone";

        var calledOff = false;
        Fields.Call(
            pages.Window,
            "StagePdfViewerReplacement",
            pages.Rail,
            (Func<Uri>)(() => PdfViewerPage.SourceUri(5)),
            missing,
            "missing replacement test",
            "pdf test",
            null,
            null,
            (Action)(() => calledOff = true),
            null);

        await UntilAsync(() => calledOff && pages.Surface.Children.OfType<WebView2>().Count() == 1, "The broken staged viewer was never called off.");

        Assert.Same(first, pages.Viewer);
        Assert.Equal(3, await pages.PageBoxAsync());
        Assert.True(pages.Shows(3));
    });

    [Fact]
    public Task APageChosenRightAfterAnotherIsNotHeldBack() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        pages.Choose(3);
        await pages.SettledOnAsync(3);

        pages.Choose(8);
        await pages.SettledOnAsync(8);
        Assert.Equal(8, await pages.PageBoxAsync());
    });

    [Fact]
    public Task TheRailFollowsTheReaderAfterAChoice() => WpfApp.RunAsync(async () =>
    {
        using var pages = await Pages.ShowAsync(12);
        pages.Choose(3);
        await pages.SettledOnAsync(3);

        await PdfViewerWheel.ScrollAsync(pages.Viewer.CoreWebView2, 3000);

        var shown = await pages.PageBoxAsync();
        Assert.True(shown > 3, "The wheel never moved the viewer.");
        await UntilAsync(() => pages.CurrentPage == shown, $"The rail stayed on {pages.CurrentPage}, the viewer is on {shown}.");
        Assert.True(pages.Shows(shown));
    });

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

    private sealed class Pages : IDisposable
    {
        private readonly TempDirectory _temp = new();

        public PreviewWindow Window { get; private set; } = null!;
        public object Rail { get; private set; } = null!;
        public string Document { get; private set; } = null!;

        public WebView2 Viewer => Fields.Get<WebView2>(Rail, "WebView");
        public Grid Surface => Fields.Get<Grid>(Rail, "Surface");
        public int CurrentPage => Fields.Get<int>(Rail, "CurrentPage");
        private Dictionary<int, Border> Tiles => Fields.Get<Dictionary<int, Border>>(Rail, "PageTiles");

        public static async Task<Pages> ShowAsync(int count)
        {
            var pages = new Pages();
            var path = pages.Document = Path.Combine(pages._temp.Path, "pages.pdf");
            FillSignBehaviorTests.WritePdf(path, 612, 792, count);
            pages.Window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
            pages.Window.MarkGeometryUserPinnedForProbe();
            pages.Window.ShowPreview(path, IntPtr.Zero);
            pages.Rail = await UntilAsync(
                () => Fields.Get<bool>(pages.Window, "_loadFinished") ? Fields.Get<object?>(pages.Window, "_activePdfRail") : null,
                "The PDF never showed.");

            if (pages.Tiles.Count != count)
            {
                Fields.Call(pages.Window, "ApplyPdfPageCount", pages.Rail, count, 612.0 / 792);
            }

            await UntilAsync(async () => await pages.PageBoxAsync() == 1, "The viewer never said where it was.");
            return pages;
        }

        public void Choose(int page) => Click(Tiles[page]);

        public async Task<(int, int)> ReadPositionAsync()
        {
            var read = (Task)Fields.Call(Window, "ReadPdfPositionAsync")!;
            await read;
            return ((int, int))read.GetType().GetProperty("Result")!.GetValue(read)!;
        }

        public Task SettledOnAsync(int page) =>
            UntilAsync(
                () => Fields.Get<int?>(Rail, "PendingRailJumpTarget") is null && Fields.Get<int>(Rail, "ViewerPage") == page,
                $"The viewer never settled on page {page}.");

        public bool Shows(int page) =>
            Tiles.Where(tile => tile.Value.Tag is true).Select(tile => tile.Key).SequenceEqual([page]);

        public async Task<int> PageBoxAsync()
        {
            var raw = await Viewer.CoreWebView2.ExecuteScriptAsync("document.getElementById('pageInput').value");
            return int.Parse(raw.Trim('"'), System.Globalization.CultureInfo.InvariantCulture);
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
            await Task.Delay(50);
        }
    }
}
