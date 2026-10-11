using System.IO.Compression;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Folders and archives on the real window: one listing card for both, and
/// no animation clock left running once a folder's walk is over.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CardsListingTests
{
    [Fact]
    public Task TheFolderSizePulseStopsWhenItsWalkEnds() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        File.WriteAllBytes(Path.Combine(temp.Path, "a.bin"), new byte[1000]);
        var earlier = ForeverClocks();
        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(temp.Path, IntPtr.Zero);
            if (Motion.Enabled)
            {
                // The size line pulses while the walk waits its turn.
                await WaitUntilAsync(() => ForeverClocks().Except(earlier).Any(), TimeSpan.FromSeconds(5));
                Assert.NotEmpty(ForeverClocks().Except(earlier));
            }

            // The walk ends within its budget; its pulse ends with it, and no
            // clock is left ticking for a line that no longer moves.
            await WaitUntilAsync(() => !ForeverClocks().Except(earlier).Any(), TimeSpan.FromSeconds(5));
            Assert.Empty(ForeverClocks().Except(earlier));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnArchiveIsAFolderCardWithItsOwnNameFactsAndBars() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var zip = Path.Combine(temp.Path, "bundle.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, bytes) in new[] { ("README.md", 100), ("docs/guide.md", 300), ("src/app.cs", 1200), ("src/lib/util.cs", 800) })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(new byte[bytes]);
            }
        }

        var folder = Directory.CreateDirectory(Path.Combine(temp.Path, "folder")).FullName;
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(folder, "b.bin"), new byte[200]);

        var window = OpenStage(900, 650);
        try
        {
            window.ShowPreview(folder, IntPtr.Zero);
            await WaitForRevealAsync(window);
            await WaitUntilAsync(() => Texts(window).Contains("700 B · 2 files"), TimeSpan.FromSeconds(5));
            var folderCard = Listing(window, "folder");

            window.ShowPreview(zip, IntPtr.Zero);
            await WaitForRevealAsync(window);
            var archiveCard = Listing(window, "bundle.zip");

            // The archive's name as the title, what it holds as the one fact.
            var texts = Texts(window);
            Assert.Contains("bundle.zip", texts);
            Assert.Contains("2.3 KB uncompressed · 4 files", texts);

            // The folder's rows, largest first, each with its size and bar;
            // the folder inside opens to its own rows.
            Assert.Equal(["src", "docs", "README.md"], archiveCard.Rows.Select(row => row.Name));
            Assert.Equal(["2 KB", "300 B", "100 B"], archiveCard.Rows.Select(row => row.Detail));
            Assert.Equal(1.0, archiveCard.Rows[0].Bar, 3);
            Assert.Equal(300.0 / 2000, archiveCard.Rows[1].Bar, 3);
            Assert.Equal([true, true, false], archiveCard.Rows.Select(row => row.Opens));

            // One component for both: the same column, rows and bars.
            Assert.Equal(folderCard.ListWidth, archiveCard.ListWidth);
            Assert.All(folderCard.Rows.Concat(archiveCard.Rows), row => Assert.True(row.Bar > 0));
        }
        finally
        {
            window.Close();
        }
    });

    private sealed record ListingRow(string Name, string Detail, double Bar, bool Opens);

    private sealed record ListingCard(double ListWidth, List<ListingRow> Rows);

    /// <summary>
    /// The card titled <paramref name="title"/>, read the way a user sees
    /// it: the list column under the card, and per top-level row its name,
    /// size and how full its bar is.
    /// </summary>
    private static ListingCard Listing(PreviewWindow window, string title)
    {
        window.UpdateLayout();
        var card = Descendants(window).OfType<FileCardLayout>()
            .Single(layout => Descendants(layout).OfType<TextBlock>().Any(text => text.Text == title));
        var scroll = Descendants(card).OfType<ScrollViewer>().First();
        var rows = ((StackPanel)scroll.Content).Children.OfType<Border>()
            .Select(row => (Grid)row.Child)
            .Select(grid =>
            {
                // An opening row leads with a chevron; name then size are
                // every row's last two lines of text.
                var texts = grid.Children.OfType<TextBlock>().ToList();
                var meter = grid.Children.OfType<Border>().Single();
                var bar = meter.Visibility == Visibility.Visible ? ((Border)meter.Child).Width / meter.Width : 0;
                return new ListingRow(texts[^2].Text, texts[^1].Text, bar, texts[0].Text == "\uE76C");
            })
            .ToList();
        return new ListingCard(scroll.Width, rows);
    }

    private static List<string> Texts(PreviewWindow window)
    {
        window.UpdateLayout();
        return Descendants(window).OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToList();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task WaitForRevealAsync(PreviewWindow window)
    {
        await WaitUntilAsync(() =>
        {
            using var audit = System.Text.Json.JsonDocument.Parse(window.ProbeAuditState("test"));
            return audit.RootElement.GetProperty("LoadFinished").GetBoolean();
        }, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Forever clocks still running on this thread. WPF keeps every root
    /// clock in its time manager's root group, by weak reference; one that
    /// is active keeps the render loop going.
    /// </summary>
    private static List<Clock> ForeverClocks()
    {
        const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
        var contextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
        var context = contextType.GetMethod("From", Flags)!.Invoke(null, [Dispatcher.CurrentDispatcher]);
        var timeManager = contextType.GetProperty("TimeManager", Flags)!.GetValue(context)!;
        var root = timeManager.GetType().GetField("_timeManagerClock", Flags)!.GetValue(timeManager)!;
        var roots = (List<WeakReference>)typeof(ClockGroup).GetField("_rootChildren", Flags)!.GetValue(root)!;
        return roots
            .Select(reference => reference.Target)
            .OfType<Clock>()
            .Where(clock => clock.CurrentState == ClockState.Active && clock.Timeline.RepeatBehavior == RepeatBehavior.Forever)
            .ToList();
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
