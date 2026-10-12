using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// A portrait PDF opens in a 640 px window, and that is where most signing
/// happens. Sign mode's bars leave their line about 130 px there. 3.0.0 cut
/// every sentence that said why a save failed, and hid the line that said
/// what came back of earlier work. A sentence that carries news is read
/// whole at the narrowest window: when the bar has no room for it, it gets
/// a row of its own under the bar.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class FillSignNarrowWindowTests
{
    // The stage of a 640 x 480 window: its 1 px edge and 52 px header taken off.
    private const double StageWidth = 638;
    private const double StageHeight = 426;

    private const int SharingViolation = unchecked((int)0x80070020);
    private const int DiskFull = unchecked((int)0x80070070);

    // The longest name a file can have and still take a signing copy beside it.
    private static readonly string LongestName = new string('n', 233) + ".pdf";

    /// <summary>Every sentence a save can end in.</summary>
    public static TheoryData<string> WhyASaveFailed => new()
    {
        PreviewWindow.CopyNeedsAnotherFolder,
        PreviewWindow.ChangedSinceSigningBegan,
        PreviewWindow.SaveFailureReason(new UnauthorizedAccessException(), replacing: true),
        PreviewWindow.SaveFailureReason(new UnauthorizedAccessException(), replacing: false),
        PreviewWindow.SaveFailureReason(new IOException("in use", SharingViolation), replacing: true),
        PreviewWindow.SaveFailureReason(new IOException("full", DiskFull), replacing: false),
        PreviewWindow.SaveFailureReason(new InvalidOperationException(), replacing: false),
        "That is the original. Pick another name for the signed copy.",
        SavedUnderItsWorkingName("Lease 2026.pdf"),
        SavedUnderItsWorkingName(LongestName),
        PdfFillSignWriter.UnreadableItem,
        PdfFillSignWriter.UnsupportedStructure,
        "The signed PDF must be written to a different file first.",
        "This PDF is too large to sign here.",
        "This file isn't a PDF that can be signed here.",
        "This PDF is encrypted, so it can't be signed here.",
        "This PDF is certified against changes, so it can't be signed here."
    };

    /// <summary>Every reason a PDF, or the page it opened on, cannot be signed.</summary>
    public static TheoryData<string> WhyItCannotBeSigned => new()
    {
        PdfFillSignWriter.UnsupportedStructure,
        "This PDF can't be read right now.",
        "This PDF is too large to sign here.",
        "This file isn't a PDF that can be signed here.",
        "This PDF is encrypted, so it can't be signed here.",
        "This PDF is certified against changes, so it can't be signed here."
    };

    [Theory]
    [MemberData(nameof(WhyASaveFailed))]
    public void WhyASaveFailedIsReadWhole(string sentence) => Sta.Run(() =>
    {
        var surface = Narrow();
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        surface.ShowSaveChoice();
        surface.SetSaving(true);

        surface.ShowSaveFailure(sentence);

        AssertReadWhole(surface, sentence);

        // The choices it brings back are all still there to press.
        foreach (var choice in new[] { "Keep editing", "Discard", "Replace the original", "Save a signed copy" })
        {
            AssertWhole(surface, Pill(surface, choice));
        }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(48)]
    public void WhatCameBackOfEarlierWorkIsReadWhole(int lost) => Sta.Run(() =>
    {
        var surface = Narrow();

        surface.Restore([new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026")], lost);

        AssertReadWhole(surface, FillSignSurface.RestoredNoticeFor(lost));
    });

    [Theory]
    [MemberData(nameof(WhyItCannotBeSigned))]
    public void WhyAPdfCannotBeSignedIsReadWhole(string problem) => Sta.Run(() =>
    {
        var surface = Narrow();

        surface.ApplyCheck(new PdfSignCheck(0, problem));

        AssertReadWhole(surface, problem + " Open it in another app to sign it.");
    });

    [Fact]
    public void APageThatCannotOpenForSigningSaysSoWhole() => Sta.Run(() =>
    {
        var surface = Narrow();

        surface.ShowProblem(PreviewWindow.SignPageUnavailable);

        AssertReadWhole(surface, PreviewWindow.SignPageUnavailable);
    });

    [Theory]
    [InlineData("Opening page 1200\u2026")]
    [InlineData("Page 1200 didn't open. Try again.")]
    public void WhatBecameOfAPageTurnIsReadWhole(string notice) => Sta.Run(() =>
    {
        var surface = Narrow();
        surface.ApplyCheck(new PdfSignCheck(3000, null));

        surface.ShowNotice(notice);

        AssertReadWhole(surface, notice);
    });

    // The row under the bar is for news. It goes when the news does, and
    // the words that stay fit where they always were.
    [Fact]
    public void TheRowUnderTheBarGoesWithItsNews() => Sta.Run(() =>
    {
        var surface = Narrow();
        surface.Place(SignPlacementKind.Date, 0.5, 0.5);
        surface.ShowSaveChoice();
        surface.ShowSaveFailure(PreviewWindow.ChangedSinceSigningBegan);
        AssertReadWhole(surface, PreviewWindow.ChangedSinceSigningBegan);

        // Trying again says so in the bar, which has room for one word.
        surface.SetSaving(true);
        AssertReadWhole(surface, "Saving\u2026");
        Assert.Empty(Lines(surface, PreviewWindow.ChangedSinceSigningBegan));

        // Back among the tools, nothing of the failed save is left on screen.
        surface.ShowSaveFailure(PreviewWindow.ChangedSinceSigningBegan);
        surface.HandleEscape();
        Lay(surface);
        Assert.Empty(Lines(surface, PreviewWindow.ChangedSinceSigningBegan));
    });

    // A work area narrower than 640 px lets the window go narrower too,
    // past the width of the bar's own buttons. A wide one gives the bar
    // room for the sentence again. One surface is dragged through both.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NewsIsReadWholeAtEveryWidthTheWindowCanTake(bool afterAFailedSave) => Sta.Run(() =>
    {
        var surface = Narrow();
        var sentence = FillSignSurface.RestoredNoticeFor(2);
        surface.Restore([new SignPlacement(SignPlacementKind.Date, 0, 0.4, 0.2, 0.14, 0.028, "10/10/2026")], 2);
        if (afterAFailedSave)
        {
            sentence = PreviewWindow.ChangedSinceSigningBegan;
            surface.ShowSaveChoice();
            surface.ShowSaveFailure(sentence);
        }

        for (var width = 320.0; width <= 1600; width += 7)
        {
            AssertReadWhole(surface, sentence, width);
        }

        AssertReadWhole(surface, sentence);
    });

    /// <summary>The sentence ReplaceWithRetriesAsync ends in when the signed file is the only copy left.</summary>
    private static string SavedUnderItsWorkingName(string name) =>
        $"The signed PDF was saved as \"{Path.GetFileName(PreviewWindow.SigningTempPath(Path.Combine(@"C:\docs", name)))}\" in the same folder. Rename it to finish.";

    /// <summary>Sign mode on a document of many pages, laid out as the narrowest window lays it out.</summary>
    private static FillSignSurface Narrow()
    {
        var bitmap = BitmapSource.Create(400, 600, 96, 96, PixelFormats.Bgra32, null, new byte[400 * 600 * 4], 400 * 4);
        bitmap.Freeze();
        var surface = new FillSignSurface(0, 128, new PdfPreviewPage(bitmap, 400, 600)) { UseLayoutRounding = true };
        TextElement.SetFontFamily(surface, new FontFamily("Segoe UI Variable, Segoe UI"));
        surface.ApplyCheck(new PdfSignCheck(128, null));
        Lay(surface);
        return surface;
    }

    private static void Lay(FillSignSurface surface, double width = StageWidth)
    {
        surface.Measure(new Size(width, StageHeight));
        surface.Arrange(new Rect(0, 0, width, StageHeight));
        surface.UpdateLayout();
    }

    /// <summary>
    /// Exactly one line on screen says the sentence, and it has every
    /// word: nothing trims it, it is as tall as its words need at the
    /// width it was given, and all of it lies inside the stage.
    /// </summary>
    private static void AssertReadWhole(FillSignSurface surface, string sentence, double width = StageWidth)
    {
        Lay(surface, width);
        var line = Assert.Single(Lines(surface, sentence));
        Assert.Equal(TextTrimming.None, line.TextTrimming);

        var words = new TextBlock
        {
            Text = sentence,
            FontSize = line.FontSize,
            FontFamily = line.FontFamily,
            FontWeight = line.FontWeight,
            TextWrapping = line.TextWrapping
        };
        // A centered line is as wide as its words, to the pixel; the pixel is rounding's.
        words.Measure(new Size(line.ActualWidth + 1, double.PositiveInfinity));
        Assert.True(
            line.ActualHeight + 1 >= words.DesiredSize.Height,
            $"\"{sentence}\" needs {words.DesiredSize.Height:0} px of height at {line.ActualWidth:0} px wide and got {line.ActualHeight:0}.");
        AssertWhole(surface, line, width);
    }

    /// <summary>All of the element lies inside the stage.</summary>
    private static void AssertWhole(FillSignSurface surface, FrameworkElement element, double width = StageWidth)
    {
        Assert.True(Shown(element), "It is not on screen.");
        var box = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
        Assert.True(
            box.Width > 0 && box.Left >= 0 && box.Top >= 0 && box.Right <= width && box.Bottom <= StageHeight,
            $"It lies at {box}, outside the {width} x {StageHeight} stage.");
    }

    /// <summary>The lines on screen that say exactly this.</summary>
    private static List<TextBlock> Lines(FillSignSurface surface, string sentence) =>
        Descendants(surface).OfType<TextBlock>().Where(block => block.Text == sentence && Shown(block)).ToList();

    private static FrameworkElement Pill(FrameworkElement root, string label) =>
        Descendants(root).OfType<ButtonBase>().First(button => button.Content as string == label);

    /// <summary>Visible as far as the tree says: the element and every ancestor are Visible.</summary>
    private static bool Shown(FrameworkElement element)
    {
        for (FrameworkElement? current = element; current is not null; current = current.Parent as FrameworkElement)
        {
            if (current.Visibility != Visibility.Visible)
            {
                return false;
            }
        }

        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
