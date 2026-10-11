using System.Collections;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The parked browser costs six processes and about 80 MB. A window pays
/// for it only once it loads a page or shows an item beside one, and never
/// while pinned: a pinned window serves no further glance.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class WarmBrowserTests
{
    [Fact]
    public Task APhotoStartsNoBrowser() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(Png(temp, "photo.png"), IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Fields.Get<bool>(window, "_loadFinished"))
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the photo.");
                await Task.Delay(10);
            }

            Assert.False(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheFirstWebSurfaceStartsTheBrowser() => Sta.Run(() =>
    {
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            Assert.False(WarmBrowserStarted(window));

            CreateWebSurface(window);

            Assert.True(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }

        Assert.False(WarmBrowserStarted(window));
    });

    // The load leaves before it reads the file, so no page is ever built:
    // a page known by its name launches its browser beside the reading and
    // the build, and anything else starts nothing yet.
    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("guide.pdf", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("report.docx", true)]
    [InlineData("book.epub", true)]
    [InlineData("page.html", true)]
    [InlineData("logo.svg", true)]
    [InlineData("photo.jpg", false)]
    [InlineData("readme.txt", false)]
    [InlineData("README", false)]
    [InlineData("code.cs", false)]
    public async Task APageKnownByItsNameStartsTheBrowserBeforeTheFileIsRead(string name, bool page) =>
        Assert.Equal(page, await StartedBeforeReadingAsync(name));

    // Code is a page only once a grammar takes it. The browser starts then,
    // while the code is still being highlighted.
    [Fact]
    public Task CodeStartsTheBrowserWhileItIsHighlighted() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var code = string.Concat(Enumerable.Repeat("public static int Twice(int value) => value * 2; // " + Guid.NewGuid() + "\n", 2500));
        var path = temp.CreateFile("big.cs", code);
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            var load = (Task)Fields.Call(window, "BuildPreviewContentAsync", path, FileSignatureKind.Unknown, false, 1L, CancellationToken.None)!;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!WarmBrowserStarted(window))
            {
                Assert.True(DateTime.UtcNow < deadline, "The browser never started.");
                await Task.Delay(1);
            }

            Assert.False(load.IsCompleted, "The page was built before the browser started.");
            await load;
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData("README")]
    [InlineData("notes.txt")]
    public Task TextWithoutAGrammarStartsNoBrowser(string name) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var path = temp.CreateFile(name, "Plain words, no grammar.");
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            await (Task)Fields.Call(window, "BuildPreviewContentAsync", path, FileSignatureKind.Unknown, false, 1L, CancellationToken.None)!;

            Assert.False(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // A photo beside a page starts the browser once it shows, so the arrow
    // to the page finds it warm.
    [Fact]
    public Task APhotoBesideAPageStartsTheBrowserOnceItShows() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        temp.CreateFile("notes.md", "# Notes");
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(Png(temp, "photo.png"), IntPtr.Zero);
            await WaitAsync(() => WarmBrowserStarted(window), "The browser never started.");

            Assert.True(Fields.Get<bool>(window, "_loadFinished"), "The browser started before the photo showed.");
        }
        finally
        {
            window.Close();
        }
    });

    // A page by name, as the load knows one before reading. Text and code
    // become pages only once read, so beside them a photo starts nothing.
    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("guide.pdf", true)]
    [InlineData("clip.mp4", true)]
    [InlineData("report.docx", true)]
    [InlineData("page.html", true)]
    [InlineData("hero.png", false)]
    [InlineData("readme.txt", false)]
    [InlineData("code.cs", false)]
    public Task APageInTheFolderStartsTheBrowser(string neighbour, bool page) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("photo.png");
        temp.CreateFile(neighbour);
        var window = NewWindow();
        try
        {
            AskAbout(window, photo);
            await ScanAnsweredAsync(window);

            Assert.Equal(page, WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // The arrows walk a multi-selection's set, so the set is asked, not the folder.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AMultiSelectionAsksItsSet(bool setHoldsPage) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var photos = temp.CreateDirectory("photos");
        var mixed = temp.CreateDirectory("mixed");
        foreach (var path in new[] { Path.Combine(photos, "photo.png"), Path.Combine(photos, "hero.png"), Path.Combine(mixed, "art.png"), Path.Combine(mixed, "notes.md") })
        {
            File.WriteAllText(path, "x");
        }

        string[] set = setHoldsPage
            ? [Path.Combine(photos, "photo.png"), Path.Combine(mixed, "notes.md")]
            : [Path.Combine(mixed, "art.png"), Path.Combine(photos, "photo.png")];
        var window = NewWindow();
        try
        {
            window.SetMultiSelection(set, 0);
            AskAbout(window, set[0]);
            await ScanAnsweredAsync(window);

            Assert.Equal(setHoldsPage, WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // A hide stops the scan. The hidden window still asks at its next show.
    [Fact]
    public Task AHideStopsTheScan() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("photo.png");
        temp.CreateFile("notes.md");
        var window = NewWindow();
        try
        {
            AskAbout(window, photo);
            var scan = Fields.Get<CancellationTokenSource>(window, "_pageScan");
            window.HidePreview();
            Assert.True(scan.IsCancellationRequested);
            await ScanEndedAsync(window);
            Assert.False(WarmBrowserStarted(window));

            AskAbout(window, photo);
            await ScanAnsweredAsync(window);
            Assert.True(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // An answer that lands after the window closed, was pinned, or moved on
    // to another folder starts nothing.
    [Theory]
    [InlineData("close")]
    [InlineData("pin")]
    [InlineData("move")]
    public Task AStaleAnswerStartsNothing(string change) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var photo = temp.CreateFile("photo.png");
        temp.CreateFile("notes.md");
        var elsewhere = Path.Combine(temp.CreateDirectory("photos"), "hero.png");
        File.WriteAllText(elsewhere, "x");
        var window = NewWindow();
        try
        {
            AskAbout(window, photo);
            switch (change)
            {
                case "close":
                    window.Close();
                    break;
                case "pin":
                    window.SetPinned(true);
                    break;
                default:
                    Fields.Set(window, "CurrentPath", elsewhere);
                    break;
            }

            await ScanEndedAsync(window);
            Assert.False(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // The scan is bounded: a few thousand names, a fifth of a second, a cancel.
    [Theory]
    [InlineData(4999, true)]
    [InlineData(5000, false)]
    public void TheScanReadsAFewThousandNames(int photos, bool found)
    {
        var names = Enumerable.Range(0, photos).Select(i => $"photo{i}.jpg").Append("notes.md").ToArray();

        Assert.Equal(found, HoldsPage(names, CancellationToken.None));
    }

    [Fact]
    public void ASlowListingGivesUpAfterAFifthOfASecond() =>
        Assert.False(HoldsPage(new SlowNames(10), CancellationToken.None));

    [Fact]
    public void ACancelledScanStops() =>
        Assert.False(HoldsPage(new[] { "photo.jpg", "notes.md" }, new CancellationToken(canceled: true)));

    // The desktop's virtual items, a zip's inside, a path with no folder:
    // no page, and nothing thrown.
    [Fact]
    public void AFolderThatCannotBeListedHoldsNoPage()
    {
        using var temp = new TempDirectory();
        temp.CreateFile("notes.md");
        foreach (var folder in new[]
        {
            Path.Combine(temp.Path, "missing"),
            temp.CreateFile("archive.zip"),
            "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            string.Empty
        })
        {
            Assert.False(HoldsPage(folder, CancellationToken.None));
        }
    }

    [Fact]
    public void APinnedWindowLetsItsBrowserGoUntilItIsTheGlanceAgain() => Sta.Run(() =>
    {
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            Fields.Call(Fields.Get<object>(window, "_warmBrowser"), "Start", window.Hwnd);
            Assert.True(WarmBrowserStarted(window));

            window.SetPinned(true);
            Assert.False(WarmBrowserStarted(window));

            // A pinned window's own pages still load; they park nothing.
            CreateWebSurface(window);
            Assert.False(WarmBrowserStarted(window));

            window.SetPinned(false);
            CreateWebSurface(window);
            Assert.True(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // Unpinned, the window is the glance again. Its page holds the browser
    // now; without a parked one too, the hide lets it go and the next page
    // starts cold.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnpinningAWindowThatShowsAPageParksTheBrowser(bool page) => Sta.Run(() =>
    {
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            window.SetPinned(true);
            window.PreviewContent.Content = page
                ? new Grid { Children = { new StagedWebView(WebSurfaceKind.Page) } }
                : new Grid();

            window.SetPinned(false);

            Assert.Equal(page, WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    // Stop can land while Start waits for its controller. The late
    // controller is closed, and with no other the browser exits.
    [Fact]
    public Task AStopDuringAStartClosesTheLateController() => WpfApp.RunAsync(async () =>
    {
        var environment = await WebViewEnvironment.GetAsync();
        await NoBrowserRunningAsync(environment);
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult();
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        try
        {
            // The environment is ready, so Start asks for its controller now.
            var browser = Fields.Get<object>(window, "_warmBrowser");
            Fields.Call(browser, "Start", window.Hwnd);
            Fields.Call(browser, "Stop");

            await exited.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(Fields.Get<object?>(browser, "_controller"));
            Assert.False(WarmBrowserStarted(window));
        }
        finally
        {
            window.Close();
        }
    });

    private static async Task NoBrowserRunningAsync(CoreWebView2Environment environment)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (environment.GetProcessInfos().Count > 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "An earlier browser never exited.");
            await Task.Delay(50);
        }
    }

    /// <summary>Runs a load that leaves before it reads the file and says whether it started the browser.</summary>
    private static async Task<bool> StartedBeforeReadingAsync(string name)
    {
        var started = false;
        await WpfApp.RunAsync(async () =>
        {
            var window = new PreviewWindow { Left = -30000, Top = 0 };
            new WindowInteropHelper(window).EnsureHandle();
            try
            {
                var path = Path.Combine(Path.GetTempPath(), name);
                var load = (Task)Fields.Call(window, "LoadPreviewAsync", path, path, 1L, new CancellationToken(canceled: true), false)!;
                started = WarmBrowserStarted(window);
                await load;
            }
            finally
            {
                window.Close();
            }
        });

        return started;
    }

    private static PreviewWindow NewWindow()
    {
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        new WindowInteropHelper(window).EnsureHandle();
        return window;
    }

    /// <summary>What a reveal does: the window asks whether the shown item's folder holds a page.</summary>
    private static void AskAbout(PreviewWindow window, string shown)
    {
        Fields.Set(window, "CurrentPath", shown);
        Fields.Call(window, "WarmBrowserForPagesNearby");
    }

    private static Task ScanAnsweredAsync(PreviewWindow window) =>
        WaitAsync(() => Fields.Get<object?>(window, "_pageScope") is not null, "The scan never answered.");

    private static Task ScanEndedAsync(PreviewWindow window) =>
        WaitAsync(() => Fields.Get<CancellationTokenSource?>(window, "_pageScan") is null, "The scan never ended.");

    private static async Task WaitAsync(Func<bool> done, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(10);
        }
    }

    private static bool HoldsPage(object scope, CancellationToken token) =>
        (bool)typeof(PreviewWindow)
            .GetMethod("HoldsPage", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [scope, token])!;

    /// <summary>A listing that takes 60 ms a name, like a share far away. Its last name is a page.</summary>
    private sealed class SlowNames(int count) : IReadOnlyList<string>
    {
        public int Count => count;

        public string this[int index] => throw new NotSupportedException();

        public IEnumerator<string> GetEnumerator()
        {
            for (var i = 0; i < count; i++)
            {
                Thread.Sleep(60);
                yield return i == count - 1 ? "notes.md" : $"photo{i}.jpg";
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static bool WarmBrowserStarted(PreviewWindow window) =>
        Fields.Get<bool>(Fields.Get<object>(window, "_warmBrowser"), "_started");

    private static void CreateWebSurface(PreviewWindow window) =>
        Fields.Call(
            window,
            "CreateWebView",
            WebSurfaceKind.Page,
            (Func<Uri>)(() => new Uri("about:blank")),
            "warm browser test",
            null,
            null,
            false);

    private static string Png(TempDirectory temp, string name)
    {
        const int width = 400;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(temp.Path, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
