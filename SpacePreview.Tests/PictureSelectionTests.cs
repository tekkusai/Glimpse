using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;

namespace SpacePreview.Tests;

/// <summary>
/// The words in a picture can be selected where they are: the pointer is a
/// text cursor over them, a drag selects from word to word, and the
/// selection copies like any other. The windowed tests draw their own
/// pictures and pass without a word on a PC whose Windows can read none.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class PictureSelectionTests
{
    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    // Two lines: "a b c" over "d e", each word a tenth of the picture wide.
    private static PictureWords TwoLines(double angle = 0) => new(
        [
            new("a", new Rect(0.1, 0.2, 0.1, 0.1), 0),
            new("b", new Rect(0.3, 0.2, 0.1, 0.1), 0),
            new("c", new Rect(0.5, 0.2, 0.1, 0.1), 0),
            new("d", new Rect(0.1, 0.6, 0.1, 0.1), 1),
            new("e", new Rect(0.3, 0.6, 0.1, 0.1), 1),
        ],
        angle,
        aspect: 1,
        joiner: " ");

    [Fact]
    public void ASelectionReadsAsTheWordsRunWhicheverEndItStartedFrom()
    {
        var words = TwoLines();

        Assert.Equal("b c" + Environment.NewLine + "d", words.TextOf(1, 3));
        Assert.Equal(words.TextOf(1, 3), words.TextOf(3, 1));
        Assert.Equal("a b c" + Environment.NewLine + "d e", words.Text);
        Assert.Equal(string.Empty, PictureWords.None.Text);
    }

    [Fact]
    public void ASelectionIsDrawnAsOneBarPerLine()
    {
        var bars = TwoLines().Highlights(1, 3).ToArray();

        Assert.Equal(2, bars.Length);
        Assert.Equal(new Rect(0.3, 0.2, 0.3, 0.1), bars[0], Near);
        Assert.Equal(new Rect(0.1, 0.6, 0.1, 0.1), bars[1], Near);
    }

    [Fact]
    public void APointFindsTheWordUnderItAndADragOffTheWordsFindsTheNearest()
    {
        var words = TwoLines();

        Assert.Equal(1, words.WordAt(new Point(0.35, 0.25)));
        Assert.Null(words.WordAt(new Point(0.8, 0.25)));
        Assert.Null(words.WordAt(new Point(0.35, 0.45)));

        // Past the end of the first line, and under the second.
        Assert.Equal(2, words.Nearest(new Point(0.9, 0.25)));
        Assert.Equal(4, words.Nearest(new Point(0.9, 0.95)));
        Assert.Equal(3, words.Nearest(new Point(0.0, 0.62)));
    }

    // Text photographed at a slant: the engine reads it level and says by
    // how much it was turned. A word is found where it is in the picture.
    [Fact]
    public void AWordInSlantedTextIsFoundWhereItIsInThePicture()
    {
        var words = TwoLines(angle: 90);

        // Turned a quarter clockwise about the middle, the box at
        // (0.3 to 0.4, 0.2 to 0.3) is shown at (0.7 to 0.8, 0.3 to 0.4).
        Assert.Equal(1, words.WordAt(new Point(0.75, 0.35)));
        Assert.Null(words.WordAt(new Point(0.35, 0.25)));
    }

    [Fact]
    public Task TheWordsAreFoundWhereTheyWereDrawn() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        var words = await PictureText.RecognizeAsync(Draw(0, "Invoice 2048 for October", "Total due 315 dollars"));

        Assert.Equal(0, words.Angle, 1.0);
        var number = words[Find(words, "2048")];
        Assert.Equal(0, number.Line);
        Assert.Equal(1, words[Find(words, "dollars")].Line);

        // "2048" was drawn after "Invoice " on the first line.
        var before = Measure("Invoice ");
        var wide = Measure("2048");
        var drawn = new Rect((40 + before) / 900, 50.0 / 300, wide / 900, 58.0 / 300);
        Assert.True(drawn.Contains(Middle(number.Box)), $"2048 was found at {number.Box}, drawn at {drawn}");
    });

    [Fact]
    public Task SlantedTextIsReadAndItsWordsAreStillUnderThePointer() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        var words = await PictureText.RecognizeAsync(Draw(12, "Invoice 2048 for October", "Total due 315 dollars"));

        Assert.Equal(12, words.Angle, 3.0);

        // Where the middle of "2048" lies once the page is turned 12 degrees.
        var level = new Point(40 + Measure("Invoice ") + Measure("2048") / 2, 50 + 29);
        var shown = new RotateTransform(12, 450, 150).Transform(level);
        Assert.Equal(Find(words, "2048"), words.WordAt(new Point(shown.X / 900, shown.Y / 300)));
    });

    [Fact]
    public Task WordsInAPictureAreSelectedByDraggingAndCopied() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Save(Draw(0, "Invoice 2048 for October", "Total due 315 dollars"), temp, "receipt.png");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is not null);
            var viewer = Viewer(window)!;

            // Not read yet: a press where a word will be selects nothing.
            Assert.Equal(0, viewer.Words.Count);
            await WaitUntilAsync(() => viewer.Words.Count > 0);
            var words = viewer.Words;
            var blank = ToView(viewer, new Point(0.9, 0.25));

            // The pointer says what is under it.
            viewer.PointerMove(viewer.WordCenter(Find(words, "2048")));
            Assert.Same(Cursors.IBeam, viewer.Element.Cursor);
            viewer.PointerMove(blank);
            Assert.NotSame(Cursors.IBeam, viewer.Element.Cursor);

            // A drag from one word to another selects from one to the other.
            Assert.True(viewer.PointerDown(viewer.WordCenter(Find(words, "2048")), clicks: 1));
            viewer.PointerMove(viewer.WordCenter(Find(words, "October")));
            viewer.PointerUp();
            Assert.Equal("2048 for October", viewer.SelectedText);

            // Across lines, and backwards.
            viewer.PointerDown(viewer.WordCenter(Find(words, "due")), clicks: 1);
            viewer.PointerMove(viewer.WordCenter(Find(words, "for")));
            viewer.PointerUp();
            Assert.Equal("for October" + Environment.NewLine + "Total due", viewer.SelectedText);

            // The menu's first Copy is the selection, and the file's is named.
            var menu = new ContextMenu();
            window.FillContextMenu(menu, viewer.SelectedText);
            var labels = menu.Items.OfType<MenuItem>().Select(item => (string)item.Header).ToArray();
            Assert.Equal("Copy", labels[0]);
            Assert.Contains("Copy file", labels);

            // A press on a word that goes nowhere selects nothing, and a
            // press off the words lets go of what was selected.
            viewer.PointerDown(viewer.WordCenter(Find(words, "Total")), clicks: 1);
            viewer.PointerUp();
            Assert.Null(viewer.SelectedText);
            viewer.PointerDown(viewer.WordCenter(Find(words, "Total")), clicks: 2);
            Assert.Equal("Total", viewer.SelectedText);
            viewer.PointerDown(blank, clicks: 1);
            viewer.PointerUp();
            Assert.Null(viewer.SelectedText);

            Assert.True(viewer.SelectAllText());
            Assert.Equal("Invoice 2048 for October" + Environment.NewLine + "Total due 315 dollars", viewer.SelectedText);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task TwiceOnAWordSelectsItAndTwiceElsewhereStillZooms() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Save(Draw(0, "Invoice 2048 for October", "Total due 315 dollars"), temp, "receipt.png");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is { Words.Count: > 0 });
            var viewer = Viewer(window)!;

            viewer.PointerDown(viewer.WordCenter(Find(viewer.Words, "October")), clicks: 2);
            Assert.Equal("October", viewer.SelectedText);
            Assert.Equal(string.Empty, viewer.NoticeText);

            // Off the words the gesture is the zoom it always was, and the
            // readout says so.
            viewer.PointerDown(ToView(viewer, new Point(0.9, 0.25)), clicks: 2);
            Assert.EndsWith("%", viewer.NoticeText, StringComparison.Ordinal);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    [Fact]
    public Task ATurnedPictureIsReadAgain() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Save(Draw(0, "Invoice 2048 for October", "Total due 315 dollars"), temp, "receipt.png");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is { Words.Count: > 0 } && window.CanRotateFromKeyboard);
            var viewer = Viewer(window)!;
            Assert.True(viewer.SelectAllText());

            // Turned on its side, the picture is another picture: the words
            // and the selection of the one before are not its own.
            Fields.Call(window, "RotateCurrentImage", 1);
            Assert.Equal(0, viewer.Words.Count);
            Assert.Null(viewer.SelectedText);
            Assert.False(viewer.SelectAllText());

            // Turned back, it is read again.
            Fields.Call(window, "RotateCurrentImage", -1);
            await WaitUntilAsync(() => viewer.Words.Count > 0);
            Assert.True(viewer.SelectAllText());
            Assert.Equal("Invoice 2048 for October" + Environment.NewLine + "Total due 315 dollars", viewer.SelectedText);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    // A large picture opens on a first frame and its full decode lands a
    // moment later, the same picture, sharper. In 3.0.3 a selection made in
    // between vanished when it landed, and Copy then copied the file.
    [Fact]
    public Task ASelectionStaysWhenTheSharperPictureArrives() => WpfApp.RunAsync(async () =>
    {
        if (!PictureText.IsAvailable)
        {
            return;
        }

        using var temp = new TempDirectory();
        var path = Save(Draw(0, "Invoice 2048 for October", "Total due 315 dollars"), temp, "receipt.png");
        var window = Park();
        try
        {
            window.ShowPreview(path, IntPtr.Zero);
            await WaitUntilAsync(() => Revealed(window) && Viewer(window) is { Words.Count: > 0 });
            var viewer = Viewer(window)!;
            var first = viewer.Words;
            viewer.PointerDown(viewer.WordCenter(Find(first, "2048")), clicks: 1);
            viewer.PointerMove(viewer.WordCenter(Find(first, "October")));
            viewer.PointerUp();

            var session = Fields.Get<ImageRotationSession>(window, "_rotationSession");
            Assert.True(session.TryUpgradeDecoded(
                Draw(0, "Invoice 2048 for October", "Total due 315 dollars"),
                Fields.Get<int>(session, "_baseOrientation")));
            Assert.Equal("2048 for October", viewer.SelectedText);

            // The sharper picture is read meanwhile; the selection outlasts it.
            await Task.Delay(1500);
            Assert.Equal("2048 for October", viewer.SelectedText);

            // The next press lets go of the selection, and the sharper
            // picture's words take over.
            viewer.PointerDown(viewer.WordCenter(Find(first, "Total")), clicks: 1);
            viewer.PointerUp();
            Assert.Null(viewer.SelectedText);
            Assert.NotSame(first, viewer.Words);
            Assert.Equal(first.Text, viewer.Words.Text);
        }
        finally
        {
            window.HidePreview();
            window.Close();
        }
    });

    private static readonly IEqualityComparer<Rect> Near = EqualityComparer<Rect>.Create((a, b) =>
        Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9 && Math.Abs(a.Width - b.Width) < 1e-9 && Math.Abs(a.Height - b.Height) < 1e-9);

    private static int Find(PictureWords words, string text) =>
        Enumerable.Range(0, words.Count).First(i => words[i].Text == text);

    private static Point Middle(Rect box) => new(box.X + box.Width / 2, box.Y + box.Height / 2);

    /// <summary>
    /// A point of the picture, as fractions, in the view's device pixels:
    /// measured off two words whose places in both are known.
    /// </summary>
    private static Point ToView(ImageViewer viewer, Point fraction)
    {
        var words = viewer.Words;
        var (a, b) = (Find(words, "Invoice"), Find(words, "dollars"));
        var (fromA, fromB) = (Middle(words[a].Box), Middle(words[b].Box));
        var (toA, toB) = (viewer.WordCenter(a), viewer.WordCenter(b));
        return new Point(
            toA.X + (fraction.X - fromA.X) * (toB.X - toA.X) / (fromB.X - fromA.X),
            toA.Y + (fraction.Y - fromA.Y) * (toB.Y - toA.Y) / (fromB.Y - fromA.Y));
    }

    private static FormattedText Line(string text) => new(
        text,
        CultureInfo.InvariantCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        44,
        Brushes.Black,
        1.0);

    private static double Measure(string text) => Line(text).WidthIncludingTrailingWhitespace;

    /// <summary>Black lines of text on a white page, the whole page turned by a number of degrees.</summary>
    private static BitmapSource Draw(double degrees, params string[] lines)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 300));
            context.PushTransform(new RotateTransform(degrees, 450, 150));
            for (var i = 0; i < lines.Length; i++)
            {
                context.DrawText(Line(lines[i]), new Point(40, 50 + i * 100));
            }

            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(900, 300, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Save(BitmapSource picture, TempDirectory temp, string name)
    {
        var path = Path.Combine(temp.Path, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
        return path;
    }

    private static PreviewWindow Park()
    {
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        return new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
    }

    private static bool Revealed(PreviewWindow window) => Fields.Get<bool>(window, "_loadFinished");

    private static ImageViewer? Viewer(PreviewWindow window) =>
        Fields.Get<object?>(window, "_imageZoom") as ImageViewer;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }
}
