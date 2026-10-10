using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The header measured on the real window: lanes at the smallest stage, the
/// tools and icon holding still while browsing, the welcome starting clean,
/// one right-click menu, a theme change reaching an open window, and the
/// dialogs on the preview's own frame.
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
            Assert.True(root.GetProperty("FileNameWidth").GetDouble() >= Metrics.HeaderNameMinWidth, root.GetRawText());

            // Open is never squeezed: it gets every pixel it asks for.
            var openMargin = window.OpenControl.Margin;
            Assert.Equal(window.OpenControl.DesiredSize.Width - openMargin.Left - openMargin.Right, window.OpenControl.ActualWidth, 1);

            // The place in the set sits beside the arrows, not in the details.
            Assert.Equal(Visibility.Visible, window.SelectionPositionText.Visibility);
            Assert.Equal("1 of 4", window.SelectionPositionText.Text);
            Assert.DoesNotContain("selected", window.SubtitleText.Text, StringComparison.Ordinal);

            // The selection's buttons stepped aside for the name; the tool
            // stayed, and the right-click menu still reaches both commands.
            Assert.Equal(Visibility.Collapsed, window.GridButton.Visibility);
            Assert.Equal(Visibility.Visible, window.ContentToolsSlot.Visibility);
            var menu = new ContextMenu();
            window.FillContextMenu(menu, selectedText: null);
            var labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.Contains("All selected items", labels);
            Assert.Contains("Slideshow", labels);

            // Browsing the set moves nothing.
            var tools = BoundsInHeader(window, window.ContentToolsSlot);
            window.ShowPreview(paths[1], IntPtr.Zero, selectedCount: 4);
            window.SetMultiSelectionIndex(1);
            await WaitForRevealAsync(window);
            window.UpdateLayout();
            Assert.Equal(tools, BoundsInHeader(window, window.ContentToolsSlot));
            Assert.Equal(Visibility.Collapsed, window.GridButton.Visibility);

            // Dragging wider crosses every threshold; the floor holds at each
            // width and no lane flips back and forth.
            for (var width = 640; width <= 1000; width += 3)
            {
                window.Width = width;
                window.UpdateLayout();
                Assert.True(window.IdentityPanel.ActualWidth >= Metrics.HeaderNameMinWidth, $"{window.IdentityPanel.ActualWidth} at {width}");
            }

            // With room again, they come back.
            Assert.Equal(Visibility.Visible, window.GridButton.Visibility);
            Assert.Equal(Visibility.Visible, window.PlayButton.Visibility);
            Assert.True(window.IdentityPanel.ActualWidth >= Metrics.HeaderNameMinWidth);
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

    [Fact]
    public Task DialogsWearThePreviewFrameAndTheNoticeNeverActivates() => WpfApp.RunAsync(() =>
    {
        const long WS_THICKFRAME = 0x00040000;
        const long WS_MAXIMIZEBOX = 0x00010000;
        var root = Path.Combine(Path.GetTempPath(), $"GlimpseChromeTest-{Guid.NewGuid():N}");
        using var client = new HttpClient();
        using var updates = new AppUpdateService(client, new Version(1, 0, 0), root);
        var release = new AppUpdateRelease(
            new Version(9, 1, 0),
            "v9.1.0",
            new Uri("https://example.invalid/setup.exe"),
            1,
            string.Empty,
            new Uri("https://example.invalid/setup.exe.sha256"));
        var notice = new PreviewAccessWarningWindow(
            new PreviewAccessWarningInfo("key", "Notepad", 1, PreviewAccessWarningKind.ConfirmedElevationMismatch),
            finalReminder: false);
        Window[] dialogs =
        [
            new SettingsWindow(() => null, () => { }, _ => { }),
            new AppUpdateWindow(updates, release),
            notice
        ];
        try
        {
            foreach (var dialog in dialogs)
            {
                // A handle, never a frame on screen.
                var hwnd = new WindowInteropHelper(dialog).EnsureHandle();
                var style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
                var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
                var name = dialog.GetType().Name;

                Assert.False(dialog.AllowsTransparency, name);
                Assert.True((exStyle & NativeMethods.WS_EX_LAYERED) == 0, $"{name} is layered");
                Assert.True((style & WS_THICKFRAME) != 0, $"{name} has no frame for the shadow");
                Assert.True((style & WS_MAXIMIZEBOX) == 0, $"{name} can maximize");
                Assert.NotNull(WindowChrome.GetWindowChrome(dialog));
            }

            var noticeStyle = NativeMethods.GetWindowLongPtr(
                new WindowInteropHelper(notice).Handle,
                NativeMethods.GWL_EXSTYLE).ToInt64();
            Assert.True((noticeStyle & NativeMethods.WS_EX_NOACTIVATE) != 0);
            Assert.False(notice.ShowActivated);
        }
        finally
        {
            foreach (var dialog in dialogs)
            {
                dialog.Close();
            }

            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
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
