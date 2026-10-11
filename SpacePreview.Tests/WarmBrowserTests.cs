using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The parked browser costs six processes and about 80 MB. A window pays
/// for it only once it has shown a web surface, and never while pinned: a
/// pinned window serves no further glance.
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
