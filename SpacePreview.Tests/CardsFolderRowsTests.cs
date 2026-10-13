using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A folder on the real window: the size it reads once its walk is over.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CardsFolderRowsTests
{
    // The list's scrollbar was the one thing that still faded with Windows
    // animations turned off: it never asked the switch.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheListsScrollbarFadesOnlyWhenMotionIsOn(bool motion) => Sta.Run(() =>
    {
        Motion.Override = motion;
        try
        {
            var scroll = new ScrollViewer { Content = new Border { Height = 2000 } };
            scroll.Measure(new Size(300, 200));
            scroll.Arrange(new Rect(0, 0, 300, 200));
            scroll.UpdateLayout();
            var bar = (ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
            bar.Opacity = 0;

            typeof(PreviewWindow).GetMethod("FadeScrollBars", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [scroll, 1.0, 120]);

            Assert.Equal(motion, bar.HasAnimatedProperties);
            if (!motion)
            {
                Assert.Equal(1, bar.Opacity);
            }
        }
        finally
        {
            Motion.Override = null;
        }
    });

    // The walk used to stop after 25,000 entries, long before its clock, so
    // every large folder read "At least".
    [Fact]
    public async Task AFolderPastTheOldEntryBudgetReadsItsRealSize()
    {
        using var temp = new TempDirectory();
        var folder = temp.CreateDirectory("large");
        Parallel.For(0, 26, d =>
        {
            var directory = Directory.CreateDirectory(Path.Combine(folder, $"part{d:00}")).FullName;
            for (var f = 0; f < 1000; f++)
            {
                File.WriteAllBytes(Path.Combine(directory, $"{f}.bin"), []);
            }
        });
        File.WriteAllBytes(Path.Combine(folder, "a.bin"), new byte[700]);

        try
        {
            await WpfApp.RunAsync(async () =>
            {
                var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
                window.MarkGeometryUserPinnedForProbe();
                try
                {
                    window.ShowPreview(folder, IntPtr.Zero);
                    await WaitUntilAsync(() => Texts(window).Any(text => text.EndsWith(" files", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));

                    // Counts are grouped the region's way, so the expectation is too.
                    Assert.Contains($"700 B · 26 folders · {26001:N0} files", Texts(window));
                }
                finally
                {
                    window.Close();
                }
            });
        }
        finally
        {
            // One at a time, 26,000 files take seconds to delete.
            Parallel.ForEach(Directory.GetDirectories(folder), TestDataRoot.TryDelete);
        }
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

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }
}
