using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A folder on the real window: the size it reads once its walk is over.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class CardsFolderRowsTests
{
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

                    Assert.Contains("700 B · 26 folders · 26,001 files", Texts(window));
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
