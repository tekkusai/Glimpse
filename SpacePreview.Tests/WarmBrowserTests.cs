using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The parked browser costs six processes and about 80 MB. A window pays
/// for it only once it loads a page, and never while pinned: a pinned
/// window serves no further glance.
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
