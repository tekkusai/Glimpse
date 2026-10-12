using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// A big deck shows its first slides while the rest converts. When the
/// whole deck lands the rail shows it at once, and the viewer swaps to it
/// in place only while it still stands where it opened. A reader who has
/// scrolled or clicked into the slides is never moved: the note says the
/// rest is ready, and choosing a slide past the first ones makes the swap.
/// A rest that fails says so.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class OfficeProgressiveUpgradeTests
{
    private const string Ready = "The slides after 6 are ready. Choose one to continue.";
    private const string Failed = "Only the first 6 slides can be shown. Open the file for the rest.";

    [Fact]
    public Task AReaderWhoHasNotMovedGetsTheWholeDeckInPlace() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();

        deck.Land();

        Assert.Equal(1, await deck.SwappedAtAsync());
        await UntilAsync(() => deck.Note is null, "The note outlived the swap.");
    });

    // 3.0.0 swapped whenever the rest landed, carrying only the slide
    // number: a reader partway down slide 3 was put back at its top.
    [Fact]
    public Task AReaderWhoHasScrolledIsNotMoved() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        await deck.ScrollToSlideAsync(3);

        deck.Land();

        await deck.HeldAsync(Ready);
        Assert.Equal(deck.WholePath, Fields.Get<string>(deck.Rail, "Path"));

        // Choosing a slide past the first ones is the reader's own move.
        Assert.True(deck.Choose(9));
        Assert.Equal(9, await deck.SwappedAtAsync());
    });

    [Fact]
    public Task AReaderWhoClickedIntoTheSlidesIsNotMoved() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        Fields.Set(deck.Window, "_userEngagedContent", true);

        deck.Land();

        await deck.HeldAsync(Ready);
    });

    // The whole deck takes about half a second to stage behind the first
    // slides. 3.0.1 first asked only before that: a reader who began to
    // scroll while it staged was put back at the top of the deck.
    [Fact]
    public Task AReaderWhoStartsToScrollWhileTheWholeDeckStagesIsNotMoved() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        await deck.ProveTheWheelAsync();
        var shown = deck.Viewer;

        deck.Land();
        var staged = await deck.StagedAsync();
        await PdfViewerWheel.TurnAsync(shown.CoreWebView2, 600);

        await deck.CalledOffAsync(shown, staged);

        // Choosing a slide past the first ones is still the reader's own move.
        Assert.True(deck.Choose(9));
        Assert.Equal(9, await deck.SwappedAtAsync());
    });

    [Fact]
    public Task AReaderWhoClicksIntoTheSlidesWhileTheWholeDeckStagesIsNotMoved() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        var shown = deck.Viewer;

        deck.Land();
        var staged = await deck.StagedAsync();
        Fields.Set(deck.Window, "_userEngagedContent", true);

        await deck.CalledOffAsync(shown, staged);
    });

    // A slide chosen while the whole deck stages is the reader asking for
    // it, whatever else they did meanwhile.
    [Fact]
    public Task ASlideChosenWhileTheWholeDeckStagesStillGetsTheWholeDeck() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        var shown = deck.Viewer;

        deck.Land();
        await deck.StagedAsync();
        Fields.Set(deck.Window, "_userEngagedContent", true);
        Assert.True(deck.Choose(9));

        await UntilAsync(() => !ReferenceEquals(deck.Viewer, shown) || deck.Note == Ready, "The whole deck never settled.");
        Assert.NotSame(shown, deck.Viewer);
        await UntilAsync(() => deck.Note is null, "The note outlived the swap.");
        await deck.ShowsSlideAsync(9);
    });

    [Fact]
    public Task ASlideAskedForBeforeItArrivedOpensThereWhenTheRestLands() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        Assert.True(deck.Choose(9));

        deck.Land();

        Assert.Equal(9, await deck.SwappedAtAsync());
    });

    [Fact]
    public Task ARestThatFailsSaysSoAndTheRailStopsPromisingIt() => WpfApp.RunAsync(async () =>
    {
        using var deck = await Deck.ShowAsync();
        var slot = new PdfPreviewPage(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4), 400, 300);
        Fields.Set(deck.Rail, "Document", new PdfPreviewDocument([.. Enumerable.Repeat(slot, 12)], 12));
        Assert.StartsWith("Slides 7 to 12", deck.Note, StringComparison.Ordinal);

        deck.Fail();

        await deck.HeldAsync(Failed);
        await UntilAsync(
            () => Fields.Get<PdfPreviewDocument?>(deck.Rail, "Document") is { TotalPages: 6, Pages.Count: 6 },
            "The rail still promised slides that will not come.");
        // A slide past the first ones is no longer something to wait for.
        Assert.Null(Fields.Get<Delegate?>(deck.Rail, "TryHandleJump"));
    });

    [Theory]
    [InlineData(false, "Pages 7 to 40\nare on the way", "The pages after 6 are ready. Choose one to continue.", "Only the first 6 pages can be shown. Open the file for the rest.")]
    [InlineData(true, "Slides 7 to 40\nare on the way", "The slides after 6 are ready. Choose one to continue.", "Only the first 6 slides can be shown. Open the file for the rest.")]
    public void TheNoteSaysWhatBecameOfTheRest(bool deck, string onTheWay, string ready, string failed)
    {
        var kind = deck ? OfficeKind.Presentation : OfficeKind.Document;
        var partial = new ConvertedOfficeDocument("first.pdf", PartialPages: 6, ExpectedPages: 40);

        Assert.Equal(onTheWay, PreviewWindow.ProgressiveNote(PreviewWindow.ProgressiveRest.OnTheWay, partial, kind));
        Assert.Equal(ready, PreviewWindow.ProgressiveNote(PreviewWindow.ProgressiveRest.Ready, partial, kind));
        Assert.Equal(failed, PreviewWindow.ProgressiveNote(PreviewWindow.ProgressiveRest.Failed, partial, kind));
    }

    /// <summary>
    /// The first six slides of a twelve-slide deck, shown and followed the
    /// way a conversion's partial is, with the job in the test's hands.
    /// </summary>
    private sealed class Deck : IDisposable
    {
        private readonly TempDirectory _temp = new();
        private readonly List<string?> _said = [];
        private OfficeConversionJob _job = null!;

        public PreviewWindow Window { get; private set; } = null!;
        public object Rail { get; private set; } = null!;
        public string WholePath { get; private set; } = null!;
        public string? Note => _said.LastOrDefault();

        public static async Task<Deck> ShowAsync()
        {
            var deck = new Deck();
            var partialPath = Path.Combine(deck._temp.Path, "first slides.pdf");
            deck.WholePath = Path.Combine(deck._temp.Path, "whole deck.pdf");
            FillSignBehaviorTests.WritePdf(partialPath, 400, 300, pages: 6);
            FillSignBehaviorTests.WritePdf(deck.WholePath, 400, 300, pages: 12);
            deck.Window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
            deck.Window.MarkGeometryUserPinnedForProbe();
            deck.Window.ShowPreview(partialPath, IntPtr.Zero);
            deck.Rail = await UntilAsync(
                () => Fields.Get<bool>(deck.Window, "_loadFinished") ? Fields.Get<object?>(deck.Window, "_activePdfRail") : null,
                "The PDF never showed.");

            // The rail's thumbnails come from a helper this host cannot start.
            if (Fields.Get<object?>(deck.Rail, "Document") is null)
            {
                Fields.Set(deck.Rail, "Document", new PdfPreviewDocument([], 6));
            }

            deck._job = new OfficeConversionJob("progressive test", Path.Combine(deck._temp.Path, "deck.pptx"), OfficeKind.Presentation);
            var partial = new ConvertedOfficeDocument(partialPath, PartialPages: 6, ExpectedPages: 12);
            Fields.Call(deck.Window, "FollowProgressiveConversion", partial, deck._job, deck.Rail, (Action<string?>)deck._said.Add);
            return deck;
        }

        public void Land() => _job.PublishComplete(new ConvertedOfficeDocument(WholePath));

        public void Fail() => _job.PublishComplete(null);

        /// <summary>A click on a slide in the rail, as the rail hands it on.</summary>
        public bool Choose(int slide) => (bool)Fields.Get<Delegate>(Rail, "TryHandleJump").DynamicInvoke(Rail, slide)!;

        /// <summary>Moves the viewer to a slide and waits until the viewer itself says it is there.</summary>
        public async Task ScrollToSlideAsync(int slide)
        {
            var first = Fields.Get<WebView2>(Rail, "WebView");
            Fields.Call(Window, "StagePdfViewerJump", Rail, slide);
            await UntilAsync(
                () => Fields.Get<WebView2>(Rail, "WebView") is var shown && !ReferenceEquals(shown, first) ? shown : null,
                "The viewer never moved.");
            await ShowsSlideAsync(slide);
        }

        /// <summary>The viewer itself comes to say it is on the slide.</summary>
        public Task ShowsSlideAsync(int slide) =>
            UntilAsync(async () => await ReadPageAsync() == slide, "The viewer never said where it was.");

        /// <summary>The slide the whole deck is opened at, read from the address of the viewer that replaces the first slides.</summary>
        public async Task<int> SwappedAtAsync()
        {
            var replacement = await UntilAsync(Replacement, "The whole deck never took the viewer's place.");
            return int.Parse(
                System.Text.RegularExpressions.Regex.Match(replacement.Source.Fragment, @"page=(\d+)").Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>The viewer the reader is looking at.</summary>
        public WebView2 Viewer => Fields.Get<WebView2>(Rail, "WebView");

        /// <summary>
        /// Shows that the viewer takes the wheel, and leaves it at the top
        /// where it opened. A viewer still loading swallows the wheel.
        /// </summary>
        public async Task ProveTheWheelAsync()
        {
            await PdfViewerWheel.ScrollAsync(Viewer.CoreWebView2, 600);
            await PdfViewerWheel.ScrollAsync(Viewer.CoreWebView2, -6000);
        }

        /// <summary>The whole deck's viewer while it loads behind the one on screen.</summary>
        public Task<WebView2> StagedAsync()
        {
            var shown = Viewer;
            return UntilAsync(
                () => Fields.Get<Grid>(Rail, "Surface").Children.OfType<WebView2>().FirstOrDefault(view => !ReferenceEquals(view, shown)),
                "The whole deck never staged behind the first slides.");
        }

        /// <summary>
        /// The swap stood down, and left what a reader who had already
        /// moved is left with: their viewer where it was, the staged one
        /// gone, the whole deck in the rail and the note saying it is ready.
        /// </summary>
        public async Task CalledOffAsync(WebView2 shown, WebView2 staged)
        {
            await UntilAsync(
                () => Note == Ready || !ReferenceEquals(Viewer, shown),
                "The whole deck neither took the viewer's place nor stood down.");
            Assert.True(ReferenceEquals(Viewer, shown), "The reader was moved to the top of the whole deck.");
            Assert.Equal(Ready, Note);
            Assert.Equal(new[] { shown }, Fields.Get<Grid>(Rail, "Surface").Children.OfType<WebView2>());
            Assert.Throws<ObjectDisposedException>(() => staged.CoreWebView2);
            Assert.Equal(Visibility.Visible, shown.Visibility);
            Assert.Equal(WholePath, Fields.Get<string>(Rail, "Path"));
            Assert.NotNull(Fields.Get<Delegate?>(Rail, "TryHandleJump"));
        }

        /// <summary>The note comes to say <paramref name="words"/>, and no viewer was staged on the way there.</summary>
        public async Task HeldAsync(string words)
        {
            await UntilAsync(
                () =>
                {
                    Assert.Null(Replacement());
                    return Note == words;
                },
                $"The note never said: {words}");
            Assert.Null(Replacement());
        }

        public void Dispose()
        {
            Window.Close();
            _temp.Dispose();
        }

        private WebView2? Replacement()
        {
            var whole = new Uri(WholePath).AbsoluteUri;
            return Fields.Get<Grid>(Rail, "Surface").Children.OfType<WebView2>()
                .FirstOrDefault(view => view.Source?.AbsoluteUri.StartsWith(whole, StringComparison.OrdinalIgnoreCase) == true);
        }

        private async Task<int> ReadPageAsync()
        {
            var read = (Task)Fields.Call(Window, "ReadPdfPositionAsync")!;
            await read;
            return (((int Page, int TotalPages))read.GetType().GetProperty("Result")!.GetValue(read)!).Page;
        }
    }

    private static async Task<T> UntilAsync<T>(Func<T?> value, string failure)
        where T : class
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            if (value() is { } found)
            {
                return found;
            }

            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(20);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string failure) =>
        await UntilAsync(() => condition() ? string.Empty : null, failure);

    private static async Task UntilAsync(Func<Task<bool>> condition, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, failure);
            await Task.Delay(100);
        }
    }
}
