using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Parts built in code follow a theme change the way the header does: a
/// card and its rows on the real window, and a document rail with its
/// selected tile, repaint without being built again. Every token the
/// source names is one the palette publishes.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class ThemeFollowTests
{
    [Fact]
    public Task AnOpenFolderCardAndItsRowsFollowATheme() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), new byte[200]);
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(folder, IntPtr.Zero);
            await WaitForRevealAsync(window);
            // The walk is over, so no row is built again after the switch.
            await WaitUntilAsync(() => Texts(window).Contains("700 B · 2 files"), TimeSpan.FromSeconds(5));
            Assert.Contains("700 B · 2 files", Texts(window));

            foreach (var palette in new[] { Theme.Light, Theme.Dark })
            {
                Theme.Use(palette.IsDark ? ThemePreference.Dark : ThemePreference.Light);
                var card = Descendants(window).OfType<FileCardLayout>().Single();
                Assert.Equal(palette.CanvasBg, Color(card.Background));
                Assert.Equal(palette.TextPrimary, Color(Text(card, "folder").Foreground));
                Assert.Equal(palette.TextSecondary, Color(Text(card, "700 B · 2 files").Foreground));

                var name = Text(card, "a.bin");
                Assert.Equal(palette.TextPrimary, Color(name.Foreground));
                var row = (Grid)name.Parent;
                Assert.Equal(palette.TextTertiary, Color(row.Children.OfType<TextBlock>().Last().Foreground));
                var meter = row.Children.OfType<Border>().Single();
                Assert.Equal(palette.RaisedBorder, Color(meter.Background));
                Assert.Equal(palette.Accent, Color(((Border)meter.Child).Background));
            }
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            window.Close();
        }
    });

    [Fact]
    public Task AChapterRailAndItsSelectedTileFollowATheme() => WpfApp.RunAsync(() =>
    {
        var contents = new EpubPreviewLoader.EpubContents(
            [new(0, "Opening"), new(1, "Middle"), new(2, "Ending")],
            Truncated: false);
        Border? selected = null;
        var build = typeof(PreviewWindow).GetMethod("BuildEpubChapterRail", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rail = (Border)build.Invoke(null,
        [
            contents,
            1,
            0.0,
            (Action<double>)(_ => { }),
            (Action<int>)(_ => { }),
            (Action<int, Border, bool>)((_, tile, isSelected) =>
            {
                if (isSelected)
                {
                    selected = tile;
                }
            }),
            (Action)(() => { })
        ])!;

        var host = new Window
        {
            Left = -30000,
            Top = 0,
            Width = 240,
            Height = 400,
            WindowStyle = WindowStyle.None,
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = rail
        };
        host.Show();
        try
        {
            Assert.NotNull(selected);
            foreach (var palette in new[] { Theme.Light, Theme.Dark })
            {
                Theme.Use(palette.IsDark ? ThemePreference.Dark : ThemePreference.Light);
                Assert.Equal(palette.ChromeBg, Color(rail.Background));
                Assert.Equal(palette.Border, Color(rail.BorderBrush));
                Assert.Equal(palette.TextTertiary, Color(Text(rail, "Contents").Foreground));
                Assert.Equal(palette.HoverBg, Color(selected!.Background));
                Assert.Equal(palette.Accent, Color(selected.BorderBrush));
                Assert.Equal(palette.TextSecondary, Color(Text(selected, "Middle").Foreground));
            }
        }
        finally
        {
            Theme.Use(ThemePreference.Dark);
            host.Close();
        }

        return Task.CompletedTask;
    });

    // A misspelled token finds no resource and paints nothing at all.
    [Fact]
    public void EveryThemeTokenTheSourceNamesIsPublished()
    {
        var published = new ResourceDictionary();
        Theme.ApplyResources(published);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        var source = new DirectoryInfo(TestSource.FilePath("SpacePreview"));
        foreach (var file in source.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Extension is not (".cs" or ".xaml")
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (Match token in Regex.Matches(File.ReadAllText(file.FullName), @"(?<!cref=)""(Theme\.\w+)""|DynamicResource (Theme\.\w+)"))
            {
                named.Add(token.Groups[1].Success ? token.Groups[1].Value : token.Groups[2].Value);
            }
        }

        Assert.Contains("Theme.CanvasBg", named);
        Assert.All(named, token => Assert.True(published.Contains(token), $"{token} is not a published resource"));
    }

    private static TextBlock Text(DependencyObject root, string text) =>
        Descendants(root).OfType<TextBlock>().Single(block => block.Text == text);

    private static List<string> Texts(PreviewWindow window)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToList();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is FrameworkElement element)
        {
            element.ApplyTemplate();
            element.UpdateLayout();
        }

        return Walk(root);

        static IEnumerable<DependencyObject> Walk(DependencyObject node)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                yield return child;
                foreach (var descendant in Walk(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static PreviewWindow OpenStage(double width, double height)
    {
        // Parked off screen and frozen, like the probe.
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
            using var audit = System.Text.Json.JsonDocument.Parse(window.ProbeAuditState("test"));
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

    private static Color Color(Brush brush) => ((SolidColorBrush)brush).Color;
}
