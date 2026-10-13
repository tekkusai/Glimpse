using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
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
    public Task TheSignPillStaysBesideTheNameAtTheSmallestStageWithOrWithoutADraft() => WpfApp.RunAsync(async () =>
    {
        // Multi-page portrait PDFs open on a 640 px stage on 1080p laptops
        // at 125 and 150 percent, with the browse arrows showing.
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "cover.png", 400, 300);
        var pdf = Path.Combine(temp.Path, "Lease for the flat on Elm Street.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 a lease");
        var window = OpenStage(640, 480);
        try
        {
            // A real stage, then a PDF's header on it; the PDF viewer itself
            // never starts in a test.
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);
            var version = Fields.Get<long>(window, "_loadVersion");
            Fields.Call(window, "ResetForNewItem");
            Fields.Call(window, "ShowHeader", pdf, pdf, 1, false, version, CancellationToken.None);
            Fields.Set(window, "_fillSignPath", pdf);
            Assert.Equal(Visibility.Visible, window.BrowseControls.Visibility);

            var widths = new List<double>();
            foreach (var draft in new[] { false, true })
            {
                if (draft)
                {
                    SignDrafts.Write(pdf, FileStamp.Of(pdf)!.Value, 0, [new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026")]);
                }

                await (Task)Fields.Call(window, "FindSignDraftAsync", pdf, version)!;
                window.UpdateLayout();

                Assert.Equal(draft ? "Resume signing" : "Fill and sign", AutomationProperties.GetName(window.SignButton));
                Assert.Equal(Visibility.Visible, window.ContentToolsSlot.Visibility);
                Assert.True(window.SignButton.IsVisible);
                Assert.True(window.IdentityPanel.ActualWidth >= Metrics.HeaderNameMinWidth, $"{window.IdentityPanel.ActualWidth} with draft={draft}");
                widths.Add(window.SignButton.ActualWidth);
            }

            // One width for both labels: finding a draft never moves the header.
            Assert.Equal(widths[0], widths[1]);
        }
        finally
        {
            SignDrafts.Delete(pdf);
            window.Close();
        }
    });

    [Fact]
    public Task ResumeBelongsToTheFileItWasFoundFor() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "cover.png", 400, 300);
        var first = Path.Combine(temp.Path, "first.pdf");
        var second = Path.Combine(temp.Path, "second.pdf");
        File.WriteAllText(first, "%PDF-1.4 first");
        File.WriteAllText(second, "%PDF-1.4 second");
        SignDrafts.Write(first, FileStamp.Of(first)!.Value, 0, [new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026")]);
        var window = OpenStage(900, 650);
        try
        {
            // The first PDF is on screen and its draft was found.
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);
            Fields.Call(window, "ResetForNewItem");
            Fields.Call(window, "ShowTool", HeaderTool.Sign);
            Fields.Set(window, "_fillSignPath", first);
            await (Task)Fields.Call(window, "FindSignDraftAsync", first, Fields.Get<long>(window, "_loadVersion"))!;
            Assert.Equal("Resume signing", AutomationProperties.GetName(window.SignButton));

            // The next PDF offers to sign from its first frame; only its own draft could say otherwise.
            window.ShowPreview(second, IntPtr.Zero);
            Assert.Equal("Fill and sign", AutomationProperties.GetName(window.SignButton));
            Assert.Equal(Visibility.Visible, window.SignLabel.Visibility);
            Assert.Equal(Visibility.Hidden, window.ResumeLabel.Visibility);
        }
        finally
        {
            // Called off before the PDF viewer would start.
            window.HidePreview();
            SignDrafts.Delete(first);
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
    public Task TheDetailsGiveSizeAFactAndWhenWithTheFullTimeOnHover() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var image = WritePng(temp.Path, "cover.png", 400, 300);
        var pdf = Path.Combine(temp.Path, "Lease.pdf");
        File.WriteAllText(pdf, "%PDF-1.4 a lease");
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(image, IntPtr.Zero);
            await WaitForRevealAsync(window);

            // The name already says PNG; the details say what it cannot.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var modified = File.GetLastWriteTime(image);
            var size = FileKinds.FormatSize(new FileInfo(image).Length);
            Assert.Equal($"{size} · 400×300 · Today {modified.ToString("t", culture)}", window.SubtitleText.Text);
            Assert.Equal($"Modified {modified.ToString("f", culture)}", window.SubtitleText.ToolTip);

            // A PDF's pages join once its content has counted them, and only
            // while that PDF is still the item on screen.
            var version = Fields.Get<long>(window, "_loadVersion");
            Fields.Call(window, "ResetForNewItem");
            Fields.Set(window, "CurrentPath", pdf);
            Fields.Call(window, "ShowHeader", pdf, pdf, 1, false, version, CancellationToken.None);
            var pdfSize = FileKinds.FormatSize(new FileInfo(pdf).Length);
            Assert.StartsWith($"{pdfSize} · Today ", window.SubtitleText.Text, StringComparison.Ordinal);

            Fields.Call(window, "ShowHeaderFact", "81 pages", version - 1);
            Assert.DoesNotContain("pages", window.SubtitleText.Text, StringComparison.Ordinal);

            // The line is rebuilt from what the header already read: the
            // late fact costs no trip to the disk.
            File.Delete(pdf);
            Fields.Call(window, "ShowHeaderFact", "81 pages", version);
            Assert.StartsWith($"{pdfSize} · 81 pages · Today ", window.SubtitleText.Text, StringComparison.Ordinal);
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

            // Empty but for the window's buttons: no mark, no stale hover.
            Assert.NotEqual(Visibility.Visible, window.FileIconImage.Visibility);
            Assert.Null(window.SubtitleText.ToolTip);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheWelcomeKeyIsAPlainKeyThatNeverAnimates() => WpfApp.RunAsync(() =>
    {
        var build = typeof(PreviewWindow).GetMethod("BuildWelcomeContent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var content = (UIElement)build.Invoke(null, null)!;

        var elements = new List<UIElement>();
        void Walk(object node)
        {
            if (node is UIElement element)
            {
                elements.Add(element);
            }

            if (node is DependencyObject parent)
            {
                foreach (var child in LogicalTreeHelper.GetChildren(parent))
                {
                    Walk(child);
                }
            }
        }

        Walk(content);
        Assert.Contains(elements, element => element is Border { Child: TextBlock { Text: "Space" } });

        // No glow, and no clock that would keep WPF rendering while it sits.
        Assert.All(elements, element =>
        {
            Assert.Null(element.Effect);
            Assert.False(element.HasAnimatedProperties);
        });
        return Task.CompletedTask;
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
            // The viewer follows the theme from its Loaded on, which can
            // come a turn after the load is finished. Under load this test
            // used to change the theme in between and read the old squares.
            await WaitUntilAsync(() => ((FrameworkElement)window.PreviewContent.Content).IsLoaded, TimeSpan.FromSeconds(10));
            window.SetPinned(true);

            Theme.Use(ThemePreference.Light);
            Assert.Equal(Theme.Light.ChromeBg, Color(window.HeaderBar.Background));
            Assert.Equal(Theme.Light.CanvasBg, Color(window.RootChrome.Background));
            Assert.Equal(Theme.Light.Accent, Color(window.PinButton.Foreground));
            AssertImageCanvas(window, Theme.Light);

            Theme.Use(ThemePreference.Dark);
            Assert.Equal(Theme.Dark.ChromeBg, Color(window.HeaderBar.Background));
            Assert.Equal(Theme.Dark.Accent, Color(window.PinButton.Foreground));
            AssertImageCanvas(window, Theme.Dark);
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            window.Close();
        }
    });

    [Fact]
    public Task AThemeChangeRepaintsPinnedText() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var notes = temp.CreateFile("notes.txt", "Plain words, no grammar.");
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(notes, IntPtr.Zero);
            await WaitForRevealAsync(window);
            window.SetPinned(true);
            var text = Find<TextBox>(window.PreviewContent)!;

            foreach (var palette in new[] { Theme.Light, Theme.Dark })
            {
                Theme.Use(palette.IsDark ? ThemePreference.Dark : ThemePreference.Light);
                Assert.Equal(palette.CanvasBg, Color(text.Background));
                Assert.Equal(palette.TextPrimary, Color(text.Foreground));
                Assert.Equal(palette.Accent, Color(text.SelectionBrush));
            }
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            window.Close();
        }
    });

    // The browser's scheme turns, and the page Glimpse wrote turns with it:
    // its canvas, its text and its syntax colors, with no reload.
    [Fact]
    public Task AThemeChangeRepaintsAPinnedPage() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var notes = temp.CreateFile("notes.md", "Words and `code`.");
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(notes, IntPtr.Zero);
            var page = await SelectionSheetTests.ShownPageAsync(window);
            window.SetPinned(true);
            await page.CoreWebView2.ExecuteScriptAsync("window.stillHere = true;");

            foreach (var palette in new[] { Theme.Light, Theme.Dark })
            {
                Theme.Use(palette.IsDark ? ThemePreference.Dark : ThemePreference.Light);
                Assert.Equal(
                    palette.IsDark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light,
                    page.CoreWebView2.Profile.PreferredColorScheme);

                var expected = $"{Rgb(palette.CanvasBg)}|{Rgb(palette.TextPrimary)}|{(palette.IsDark ? "#7FA3F0" : "#2F5FD0")}|true";
                await WaitUntilAsync(async () => await PageColorsAsync(page) == expected, TimeSpan.FromSeconds(5));
                Assert.Equal(expected, await PageColorsAsync(page));
            }
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            window.Close();
        }
    });

    [Fact]
    public Task DialogsWearThePreviewFrameAndTheNoticeNeverActivates() => WpfApp.RunAsync(() =>
    {
        const long WS_THICKFRAME = 0x00040000;
        const long WS_MAXIMIZEBOX = 0x00010000;
        using var updates = ReleaseFixture.QuietUpdater();
        var notice = new PreviewAccessWarningWindow(
            new PreviewAccessWarningInfo("key", "Notepad", 1, PreviewAccessWarningKind.ConfirmedElevationMismatch),
            finalReminder: false);
        Window[] dialogs =
        [
            new SettingsWindow(updates, _ => { }, () => []),
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

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    }

    /// <summary>The image canvas and the checkerboard under transparent pixels.</summary>
    private static void AssertImageCanvas(PreviewWindow window, ThemePalette palette)
    {
        var canvas = (Panel)window.PreviewContent.Content;
        Assert.Equal(palette.CanvasBg, Color(canvas.Background));
        var frame = (Border)((Canvas)canvas.Children[0]).Children[0];
        var squares = (DrawingGroup)((DrawingBrush)frame.Background).Drawing;
        Assert.Equal(palette.RaisedBg, Color(((GeometryDrawing)squares.Children[0]).Brush));
        Assert.Equal(palette.RaisedBorder, Color(((GeometryDrawing)squares.Children[1]).Brush));
    }

    /// <summary>The page's canvas, text and keyword colors, and whether it is the same page.</summary>
    private static async Task<string> PageColorsAsync(Microsoft.Web.WebView2.Wpf.WebView2 page) =>
        JsonSerializer.Deserialize<string>(await page.CoreWebView2.ExecuteScriptAsync(
            "(() => { const s = getComputedStyle(document.documentElement);"
            + " return [s.backgroundColor, s.color, s.getPropertyValue('--tk-keyword').trim(), window.stillHere === true].join('|'); })()"))!;

    private static string Rgb(System.Windows.Media.Color c) => $"rgb({c.R}, {c.G}, {c.B})";

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
