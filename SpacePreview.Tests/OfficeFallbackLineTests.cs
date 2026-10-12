using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A file the engine could not convert shows as its words only. 3.0.0 gave
/// no line about it unless the engine was missing, so the page looked like
/// the document. The line names what happened and what to do, on the first
/// look and on a revisit that does not ask the engine again.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class OfficeFallbackLineTests
{
    [Fact]
    public Task AFileTheEngineCouldNotConvertSaysSo() => WpfApp.RunAsync(async () =>
    {
        if (OfficeDocumentPdfConverter.EngineName is not { } name)
        {
            return;
        }

        using var temp = new TempDirectory();
        using var engine = new OfficeConversionJobTests.FakeEngine();
        var path = Path.Combine(temp.Path, "report.docx");
        OfficeDocumentPdfConverterTests.WriteLinkedImageDocument(path, "https://example.com/", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink");
        var words = $"{name} couldn't convert this file. Open it in its app instead.";
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            (await engine.RunAsync(0)).Fail();
            await SaysAsync(window, words);

            // The next look shows the same page and line at once.
            window.ShowPreview(Path.Combine(temp.Path, "missing.txt"), IntPtr.Zero);
            window.ShowPreview(path, IntPtr.Zero);
            await SaysAsync(window, words);
            Assert.Equal(1, engine.RunCount);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>The page is up, with <paramref name="words"/> under it.</summary>
    private static async Task SaysAsync(PreviewWindow window, string words)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!Fields.Get<bool>(window, "_loadFinished") || !Texts(window).Contains(words))
        {
            Assert.True(DateTime.UtcNow < deadline, $"The preview never said: {words}");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<string> Texts(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock text)
            {
                yield return text.Text;
            }

            foreach (var nested in Texts(child))
            {
                yield return nested;
            }
        }
    }
}
