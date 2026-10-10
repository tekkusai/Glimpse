using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The header measured on the real window: lanes at the smallest stage, the
/// tools and icon holding still while browsing, the welcome starting clean,
/// one right-click menu, and a theme change reaching an open window.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ChromeHeaderTests
{
    [Fact]
    public Task TheNameKeepsItsRoomAtTheSmallestStageWithAMultiSelection() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var paths = Enumerable.Range(1, 4)
            .Select(i => WritePng(temp.Path, $"camera roll export {i:00} from the long weekend.png", 320 + (i * 40), 240))
            .ToArray();
        var window = OpenStage(640, 480);
        try
        {
            window.ShowPreview(paths[0], IntPtr.Zero, selectedCount: 4);
            window.SetMultiSelection(paths, 0);
            window.SetBrowsePosition(0, 4);
            await WaitForRevealAsync(window);

            using var audit = Audit(window);
            var root = audit.RootElement;
            Assert.True(root.GetProperty("LanesDoNotOverlap").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("LanesInsideHeader").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("AllVisibleButtonsNamed").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("AllVisibleButtonCentersHit").GetBoolean(), root.GetRawText());
            Assert.True(root.GetProperty("FileNameWidth").GetDouble() >= 120, root.GetRawText());

            // Open is never squeezed: it gets every pixel it asks for.
            var openMargin = window.OpenControl.Margin;
            Assert.Equal(window.OpenControl.DesiredSize.Width - openMargin.Left - openMargin.Right, window.OpenControl.ActualWidth, 1);

            // The place in the set sits beside the arrows, not in the details.
            Assert.Equal(Visibility.Visible, window.SelectionPositionText.Visibility);
            Assert.Equal("1 of 4", window.SelectionPositionText.Text);
            Assert.DoesNotContain("selected", window.SubtitleText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task BrowsingImagesKeepsTheToolsAndIconInPlace() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var first = WritePng(temp.Path, "first.png", 400, 300);
        var second = WritePng(temp.Path, "second.png", 300, 400);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(first, IntPtr.Zero);
            await WaitForRevealAsync(window);
            await WaitUntilAsync(() => window.FileIconImage.Source is not null, TimeSpan.FromSeconds(10));
            window.UpdateLayout();

            Assert.Equal(Visibility.Visible, window.ContentActions.Visibility);
            Assert.True(window.ContentActions.IsEnabled);
            var tools = BoundsInHeader(window, window.ContentToolsSlot);
            var open = BoundsInHeader(window, window.OpenControl);
            var icon = window.FileIconImage.Source;
            var iconVisibility = window.FileIconImage.Visibility;

            window.ShowPreview(second, IntPtr.Zero);
            foreach (var wait in new[] { 0, 30, 60 })
            {
                await Task.Delay(wait);
                window.UpdateLayout();
                Assert.Equal(Visibility.Visible, window.ContentActions.Visibility);
                Assert.True(window.ContentActions.IsEnabled);
                Assert.Equal(tools, BoundsInHeader(window, window.ContentToolsSlot));
                Assert.Equal(open, BoundsInHeader(window, window.OpenControl));
                if (iconVisibility == Visibility.Visible)
                {
                    // The old icon stays until the new one lands; it never blinks out.
                    Assert.Equal(Visibility.Visible, window.FileIconImage.Visibility);
                    if (wait == 0)
                    {
                        Assert.Same(icon, window.FileIconImage.Source);
                    }
                }
            }

            await WaitForRevealAsync(window);
            window.UpdateLayout();
            Assert.Equal(tools, BoundsInHeader(window, window.ContentToolsSlot));
            Assert.True(window.ContentActions.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFreshOpenShowsItsToolReadyWhileItLoads() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var first = WritePng(temp.Path, "earlier.png", 400, 300);
        var second = WritePng(temp.Path, "later.png", 400, 300);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(first, IntPtr.Zero);
            await WaitForRevealAsync(window);
            window.HidePreview();
            await WaitUntilAsync(() => !window.IsVisible, TimeSpan.FromSeconds(2));
            Assert.False(window.IsVisible);

            window.ShowPreview(second, IntPtr.Zero);
            Assert.Equal(Visibility.Visible, window.ContentActions.Visibility);
            Assert.True(window.ContentActions.IsEnabled);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheWelcomeStartsWithNoToolsFromThePreviousItem() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "before welcome.png", 400, 300);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);
            Assert.Equal(Visibility.Visible, window.ContentActions.Visibility);

            window.ShowWelcome();
            await Task.Delay(50);
            window.UpdateLayout();

            Assert.Equal(Visibility.Collapsed, window.ContentToolsSlot.Visibility);
            Assert.Equal(Visibility.Collapsed, window.ContentActions.Visibility);
            Assert.Equal(Visibility.Collapsed, window.OpenControl.Visibility);
            Assert.Equal(Visibility.Collapsed, window.BrowseControls.Visibility);
            Assert.Equal(string.Empty, window.FileNameText.Text);
            Assert.Equal(Visibility.Visible, window.FileIconImage.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task OneRightClickMenuNamesTheAppAndCopiesTheSelection() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "menu.png", 400, 300);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);

            var menu = new ContextMenu();
            window.FillContextMenu(menu, "selected words");
            var labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.Equal("Copy", labels[0]);
            Assert.Contains("Open with…", labels);
            Assert.Contains("Show in Explorer", labels);
            Assert.Contains("Copy file", labels);
            Assert.Contains("Copy path", labels);

            window.FillContextMenu(menu, selectedText: null);
            labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.DoesNotContain("Copy file", labels);
            Assert.Equal("Copy", labels[^2]);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AThemeChangeRepaintsAnOpenPinnedWindow() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "pinned.png", 400, 300);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);
            window.SetPinned(true);

            Theme.Use(ThemePreference.Light);
            Assert.Equal(Theme.Light.ChromeBg, Color(window.HeaderBar.Background));
            Assert.Equal(Theme.Light.CanvasBg, Color(window.RootChrome.Background));
            Assert.Equal(Theme.Light.Accent, Color(window.PinButton.Foreground));

            Theme.Use(ThemePreference.Dark);
            Assert.Equal(Theme.Dark.ChromeBg, Color(window.HeaderBar.Background));
            Assert.Equal(Theme.Dark.Accent, Color(window.PinButton.Foreground));
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            window.Close();
        }
    });

    [Fact]
    public Task SettingsKeepsTheVersionBesideAnAvailableUpdate() => WpfApp.RunAsync(() =>
    {
        var release = new AppUpdateRelease(
            new Version(9, 1, 0),
            "v9.1.0",
            new Uri("https://example.invalid/setup.exe"),
            1,
            string.Empty,
            new Uri("https://example.invalid/setup.exe.sha256"));
        var settings = new SettingsWindow(() => release, () => { }, _ => { });
        try
        {
            Assert.StartsWith(Branding.Name, settings.VersionText.Text, StringComparison.Ordinal);
            Assert.NotEqual(Visibility.Collapsed, settings.VersionText.Visibility);
            Assert.Equal(Visibility.Visible, settings.UpdateLink.Visibility);
            Assert.Equal("Update to 9.1.0", settings.UpdateLink.Content);
        }
        finally
        {
            settings.Close();
        }

        return Task.CompletedTask;
    });

    private static PreviewWindow OpenStage(double width, double height)
    {
        // Parked off screen and frozen, like the probe: no frame of it ever
        // reaches the desktop.
        var window = new PreviewWindow
        {
            Left = -30000,
            Top = 0,
            Width = width,
            Height = height
        };
        window.MarkGeometryUserPinnedForProbe();
        return window;
    }

    private static async Task WaitForRevealAsync(PreviewWindow window)
    {
        await WaitUntilAsync(() =>
        {
            using var audit = Audit(window);
            return audit.RootElement.GetProperty("LoadFinished").GetBoolean();
        }, TimeSpan.FromSeconds(10));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private static JsonDocument Audit(PreviewWindow window) => JsonDocument.Parse(window.ProbeAuditState("test"));

    // Whole device pixels: layout math leaves 1e-13 crumbs.
    private static Rect BoundsInHeader(PreviewWindow window, FrameworkElement element)
    {
        var origin = element.TranslatePoint(new Point(0, 0), window.HeaderBar);
        return new Rect(Math.Round(origin.X), Math.Round(origin.Y), Math.Round(element.ActualWidth), Math.Round(element.ActualHeight));
    }

    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;

    private static string WritePng(string directory, string name, int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(directory, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
