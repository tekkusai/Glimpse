using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The reveal's sequencing on the real window. Each test puts the window in
/// the state a step starts from (an item fading out, a reveal still
/// dissolving, a browser still starting) and hands content over, starts a
/// navigation or presses a key from there. The next item's lane is held
/// back, so the test decides when its content arrives.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RevealSequencingTests
{
    private static readonly Rect ParkedPixels = new(-30000, 0, 1920, 1040);

    // ---------------------------------------------------------------------
    // One next slot, in a host that does not move.
    // ---------------------------------------------------------------------

    [Fact]
    public Task ACardHandedOverDuringTheBeatReplacesTheViewThatFailed() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // A page on screen cannot be held, so the next press fades it out.
            await ShowPageAsync(window, Png(temp, "second.png"));
            Browse(window, Png(temp, "third.png"));

            // A browser view stages while the page leaves. It fails there,
            // and its lane hands over the card.
            var dead = Web(WebSurfaceKind.PdfViewer);
            Hand(window, dead);
            var card = new Border();
            Hand(window, card);

            await WaitUntilAsync(() => Revealed(window));
            Assert.Same(card, window.PreviewContent.Content);
            Assert.Equal(Visibility.Visible, card.Visibility);
            Assert.True(Fields.Get<bool>(ViewIn(dead), "_isDisposed"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ContentAttachesOnceHoweverItArrives() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            await ShowPageAsync(window, Png(temp, "second.png"));
            Browse(window, Png(temp, "third.png"));

            // Staged while the page fades out, shown once it has gone.
            var document = Web(WebSurfaceKind.PdfViewer);
            var loaded = 0;
            document.Loaded += (_, _) => loaded++;
            Hand(window, document);
            var host = window.PreviewContent;
            Navigate(window, document);

            await WaitUntilAsync(() => Settled(window));
            Assert.Same(document, window.PreviewContent.Content);
            Assert.Same(host, window.PreviewContent);
            Assert.Equal(1, loaded);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AnItemHeldUnderAStagedDocumentHoldsForTheNextPress() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));
            var document = Web(WebSurfaceKind.PdfViewer);
            Hand(window, document);
            Assert.Same(held, Front(window).Content);
            Assert.Equal(Visibility.Hidden, document.Visibility);

            // Nobody saw the document, so it is dropped. The held item is
            // all that shows, and it holds on for the next one.
            Browse(window, Png(temp, "third.png"));
            Assert.Same(held, Front(window).Content);
            Assert.Null(Back(window).Content);
            Assert.True(Fields.Get<bool>(ViewIn(document), "_isDisposed"));

            var next = new Border();
            Hand(window, next);
            await WaitUntilAsync(() => Settled(window));
            Assert.Same(next, window.PreviewContent.Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APageThatNeverArrivedIsDroppedAndTheItemUnderItHolds() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = (UIElement)window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));
            var page = Web(WebSurfaceKind.Page);
            Hand(window, page);
            Assert.Same(held, Front(window).Content);
            Assert.Equal(Visibility.Visible, page.Visibility);

            // The page never finished navigating. The next press drops it,
            // and the picture under it stays for the item after.
            Browse(window, Png(temp, "third.png"));
            Assert.Same(held, Front(window).Content);
            Assert.Null(Back(window).Content);
            Assert.Equal(1.0, held.Opacity);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APageOnScreenFadesOutBeforeTheNextItemShows() => WpfApp.RunAsync(async () =>
    {
        if (!Motion.Enabled)
        {
            return;
        }

        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var page = await ShowPageAsync(window, Png(temp, "second.png"));
            Browse(window, Png(temp, "third.png"));

            // The next item waits, attached and hidden, until the page has left.
            var next = new Border();
            Hand(window, next);
            Assert.Same(page, Front(window).Content);
            Assert.Equal(Visibility.Hidden, next.Visibility);

            await WaitUntilAsync(() => Settled(window));
            Assert.Same(next, Front(window).Content);
            Assert.Equal(Visibility.Visible, next.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFallbackCardWaitsForItsPageToFadeOut() => WpfApp.RunAsync(async () =>
    {
        if (!Motion.Enabled)
        {
            return;
        }

        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // The page showed, then its lane gave up on it: same load, a
            // second presentation.
            var page = await ShowPageAsync(window, Png(temp, "second.png"));
            var card = new Border();
            Hand(window, card);
            Assert.Same(page, Front(window).Content);
            Assert.Equal(Visibility.Hidden, card.Visibility);

            await WaitUntilAsync(() => Settled(window));
            Assert.Same(card, Front(window).Content);
            Assert.True(Fields.Get<bool>(ViewIn(page), "_isDisposed"));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task TheItemMakingWayStopsTakingClicksAtOnce() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var shown = (UIElement)window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));

            Assert.False(shown.IsHitTestVisible);
            Assert.Contains("\"ShownTakesInput\":false", window.ProbeAuditState("swap"), StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ADyingBrowserViewIsHiddenBeforeItIsDisposed() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // A WebView2 being disposed paints a black frame on its way out.
            var content = new Border();
            Visibility? atDispose = null;
            Browse(window, Png(temp, "second.png"));
            Fields.Call(window, "SetPreviewContent", content, new Disposer(() => atDispose = content.Visibility));
            await WaitUntilAsync(() => Settled(window));
            Assert.Null(atDispose);

            window.HidePreview();
            await WaitUntilAsync(() => !window.IsVisible);
            Assert.Equal(Visibility.Collapsed, atDispose);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // The stand-in belongs to the load that made it.
    // ---------------------------------------------------------------------

    [Fact]
    public Task AStandInLeavesWithItsItemWhenTheNextPressComesMidDissolve() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = Stage();
        try
        {
            // A slow first item: the window opens on its stand-in.
            Browse(window, Png(temp, "first.png"));
            await WaitUntilAsync(() =>
                window.StandInLayer.Visibility == Visibility.Visible
                && Fields.Get<byte>(window, "_windowAlpha") == 255);

            // It arrives and starts to dissolve in over the stand-in. The
            // next press comes before the dissolve is over.
            var item = new Border();
            Hand(window, item);
            Browse(window, Png(temp, "second.png"));

            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);
            Assert.Same(item, Front(window).Content);
            Assert.Equal(1.0, item.Opacity);
            Assert.Null(Back(window).Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AGhostWaitsASecondOverAnItemOnScreen() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = window.PreviewContent.Content;
            var pressed = Stopwatch.StartNew();
            Browse(window, Png(temp, "second.png"));

            // A ghost is a flat rectangle. The picture on screen keeps its
            // place until the wait itself needs showing.
            await Task.Delay(400);
            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);
            Assert.Same(held, Front(window).Content);

            await WaitUntilAsync(() => window.StandInLayer.Visibility == Visibility.Visible);
            Assert.True(pressed.Elapsed >= RevealPlan.SheenAt - TimeSpan.FromMilliseconds(40));
            Assert.Equal("ghost", ((StandIn)window.StandInLayer.Content).Description);

            // The content dissolves in over the stand-in, then both leave.
            var next = new Border();
            Hand(window, next);
            await WaitUntilAsync(() => Settled(window));
            Assert.Same(next, Front(window).Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APosterTakesTheItemsPlaceAfter120Ms() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));

            // A real image of the next item, in its shape.
            var poster = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Gray8, null, new byte[800 * 600], 800);
            Fields.Set(Presentation(window), "Poster", Task.FromResult<BitmapSource?>(poster));

            await WaitUntilAsync(() => window.StandInLayer.Visibility == Visibility.Visible);
            Assert.Equal("poster", ((StandIn)window.StandInLayer.Content).Description);
            Assert.False(Revealed(window));

            // The item it covers stays in its host until the next one lands.
            Assert.Same(held, Front(window).Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ALateTimerNeverBringsTheStandInBackOverRevealedContent() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = Stage();
        var reveals = 0;
        window.ContentReady += (_, _) => reveals++;
        try
        {
            var opened = Stopwatch.StartNew();
            Browse(window, Png(temp, "first.png"));
            await WaitUntilAsync(() => window.StandInLayer.Visibility == Visibility.Visible);
            Hand(window, new Border());
            await WaitUntilAsync(() => Settled(window));

            // The sheen and reason timers fire after the reveal.
            var rest = RevealPlan.ReasonAt + TimeSpan.FromMilliseconds(100) - opened.Elapsed;
            await Task.Delay(rest > TimeSpan.Zero ? rest : TimeSpan.Zero);
            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);
            Assert.Equal(1, reveals);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task NothingWaitsOverAnItemThatIsStillFadingOut() => WpfApp.RunAsync(async () =>
    {
        if (!Motion.Enabled)
        {
            return;
        }

        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            await ShowPageAsync(window, Png(temp, "second.png"));

            // The press is half a second old by the time its selection is
            // read, so the wait is due at once. It still waits for the beat.
            window.InputStamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 2;
            Browse(window, Png(temp, "third.png"));
            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);

            await WaitUntilAsync(() => window.StandInLayer.Visibility == Visibility.Visible);
            Assert.Null(Front(window).Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AReplacedLoadLeavesTheNextLoadsStandInAlone() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // The probe's own check, as ui-stress.ps1 runs it.
            Assert.True(window.ProbeStandInRace());
            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // The stage takes a shape only for the load it belongs to.
    // ---------------------------------------------------------------------

    [Fact]
    public Task AShapeLearnedAsTheNextItemOpensWaitsForTheOldItemToLeave() => WpfApp.RunAsync(async () =>
    {
        if (!Motion.Enabled)
        {
            return;
        }

        using var temp = new TempDirectory();
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, 96) };
        try
        {
            // The first item is slow: its ghost is up, and it has not shown.
            Browse(window, Png(temp, "wide.png", 800, 600));
            await WaitUntilAsync(() =>
                window.StandInLayer.Visibility == Visibility.Visible
                && Fields.Get<byte>(window, "_windowAlpha") == 255);
            var size = StagePixels(window).Size;
            Assert.Equal(new Size(800 + StageFit.ChromeWidth, 600 + StageFit.ChromeHeight), size);

            // An arrow to a photo of another shape. Its header names the
            // shape as it opens; the stage must not take it with the old
            // ghost still on screen.
            Browse(window, Png(temp, "tall.png", 600, 1500));
            Assert.Equal(size, StagePixels(window).Size);
            Assert.Equal(Visibility.Visible, window.StandInLayer.Visibility);

            // The ghost fades out, then the stage takes the new shape in
            // one step, still on its monitor.
            await WaitUntilAsync(() => StagePixels(window).Height > size.Height + StageFit.Hysteresis);
            Assert.True(ParkedPixels.Contains(StagePixels(window)));
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // Documents: navigation, gates, the escape.
    // ---------------------------------------------------------------------

    [Fact]
    public Task ABrowserStillStartingIsNotRevealedByTheEscape() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = Stage();
        try
        {
            Browse(window, Png(temp, "first.png"));
            var document = Web(WebSurfaceKind.PdfViewer);
            Hand(window, document);

            // The engine takes 400 ms to start; only then does the document
            // begin to navigate. Counted from the hand-over, the escape
            // would fire 1.4 s in, on a healthy view with nothing to show.
            await Task.Delay(400);
            Fields.Call(window, "OnNavigationStarting", ViewIn(document));
            await Task.Delay(1050);
            Assert.False(Revealed(window));
            Assert.Equal(Visibility.Hidden, document.Visibility);
            Assert.Equal(Visibility.Visible, window.StandInLayer.Visibility);

            // A navigation that never finishes still reveals, 1.4 s after it began.
            await WaitUntilAsync(() => Revealed(window));
            Assert.Equal(Visibility.Visible, document.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task NavigationRevealsADocumentOnce() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = Stage();
        var reveals = 0;
        window.ContentReady += (_, _) => reveals++;
        try
        {
            Browse(window, Png(temp, "first.png"));
            var document = Web(WebSurfaceKind.PdfViewer);
            Hand(window, document);
            Assert.Equal(Visibility.Hidden, document.Visibility);

            Fields.Call(window, "OnNavigationStarting", ViewIn(document));
            Navigate(window, document);
            Navigate(window, document);
            await WaitUntilAsync(() => Settled(window));

            // The escape's moment passes after the reveal, and changes nothing.
            await Task.Delay(RevealPlan.EscapeAfter + TimeSpan.FromMilliseconds(100));
            Assert.Equal(1, reveals);
            Assert.Same(document, Front(window).Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task AVideoWaitsForItsShapeAndNeverPastTheCap(bool answered) => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = Stage();
        try
        {
            // A video whose header named no shape: it stays hidden until its
            // metadata says, or until the wait runs out.
            var stage = Activator.CreateInstance(
                typeof(PreviewWindow).GetNestedType("MediaStageState", BindingFlags.NonPublic)!,
                [false])!;
            var video = Web(WebSurfaceKind.Page);
            video.Tag = stage;
            Browse(window, Png(temp, "first.png"));
            Hand(window, video);
            var navigated = Stopwatch.StartNew();
            Navigate(window, video);
            Assert.Equal(Visibility.Hidden, video.Visibility);

            await Task.Delay(150);
            Assert.False(Revealed(window));
            if (answered)
            {
                ((TaskCompletionSource<bool>)stage.GetType().GetProperty("DimsReady")!.GetValue(stage)!).TrySetResult(true);
            }

            await WaitUntilAsync(() => Revealed(window));
            Assert.Equal(Visibility.Visible, video.Visibility);
            if (answered)
            {
                Assert.True(navigated.Elapsed < RevealPlan.MediaShapeCap);
            }
            else
            {
                Assert.True(navigated.Elapsed >= RevealPlan.MediaShapeCap - TimeSpan.FromMilliseconds(40));
            }
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // Loads: a single press starts at once, a held arrow runs one at a time.
    // ---------------------------------------------------------------------

    [Fact]
    public Task ASinglePressStartsItsLoadAtOnce() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // A folder's first frame is local UI, so with nothing in front
            // of the load its card is handed over inside the call.
            var shown = window.PreviewContent.Content;
            window.ShowPreview(temp.CreateDirectory("next"), IntPtr.Zero);
            Assert.NotSame(shown, window.PreviewContent.Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AHeldArrowRunsOneLoadAtATime() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        var reveals = 0;
        window.ContentReady += (_, _) => reveals++;
        try
        {
            // A load is still decoding when three repeats arrive.
            var decoding = new TaskCompletionSource();
            Fields.Set(window, "_loadAtWork", decoding.Task);
            var shown = window.PreviewContent.Content;
            var last = temp.CreateDirectory("c");
            window.ShowPreview(temp.CreateDirectory("a"), IntPtr.Zero);
            window.ShowPreview(temp.CreateDirectory("b"), IntPtr.Zero);
            window.ShowPreview(last, IntPtr.Zero);
            Assert.Same(shown, window.PreviewContent.Content);

            // It lets go: only the newest press is still wanted.
            decoding.SetResult();
            await WaitUntilAsync(() => Settled(window));
            Assert.Equal(last, window.CurrentPath);
            Assert.Equal(1, reveals);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ALoadThatNeverLetsGoHoldsTheNextOneOnlyBriefly() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // Stuck on a dead share, say.
            Fields.Set(window, "_loadAtWork", new TaskCompletionSource().Task);
            var shown = window.PreviewContent.Content;
            var pressed = Stopwatch.StartNew();
            window.ShowPreview(temp.CreateDirectory("next"), IntPtr.Zero);
            Assert.Same(shown, window.PreviewContent.Content);

            await WaitUntilAsync(() => !ReferenceEquals(shown, window.PreviewContent.Content));
            Assert.True(pressed.Elapsed < TimeSpan.FromMilliseconds(500));
        }
        finally
        {
            window.Close();
        }
    });

    // The view itself holds the cache entry it loads from, from the load
    // to its disposal, so no kind of preview has to remember to.
    [Fact]
    public Task AnOpenPreviewKeepsTheCacheEntryItReadsFrom() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var now = DateTime.UtcNow;
        var entry = Directory.CreateDirectory(Path.Combine(temp.Path, "DocumentCache", "book")).FullName;
        var page = Path.Combine(entry, "sheet.html");
        File.WriteAllText(page, "<!DOCTYPE html><html><body><p>one</p></body></html>");
        Directory.SetLastWriteTimeUtc(entry, now.AddDays(-20));
        var window = await OpenAsync(page);
        try
        {
            Assert.Equal(0, SpacePreview.Services.CacheHousekeeping.Run(temp.Path, now));
            Assert.True(File.Exists(page));

            // The glance ends the way every glance does, and the entry is old again.
            window.HidePreview();
            await WaitUntilAsync(() => SpacePreview.Services.CacheHousekeeping.Run(temp.Path, now) == 1);
            Assert.False(Directory.Exists(entry));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AReplacedLoadDisposesNothingItDidNotBuild() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            // The current load's view, as its lane leaves it for the hand-over.
            var view = (StagedWebView)Fields.Call(
                window,
                "CreateWebView",
                WebSurfaceKind.Page,
                (Func<Uri>)(() => new Uri("about:blank")),
                "sequencing test",
                null,
                null)!;

            // A load that was replaced while its last step ran ends late.
            var folder = temp.CreateDirectory("folder");
            await (Task)Fields.Call(window, "LoadPreviewAsync", folder, ItemFacts.ReadAsync(folder, CancellationToken.None), true, 0L, CancellationToken.None, false)!;

            Assert.False(Fields.Get<bool>(view, "_isDisposed"));
            view.Dispose();
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // Shapes known before anything loads.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("readme.md")]
    [InlineData("page.html")]
    [InlineData("notes.txt")]
    [InlineData("budget.xlsx")]
    public void ContentYouScrollThroughOpensLargeOnANetworkPath(string name) => Sta.Run(() =>
    {
        // Loopback, and a share that is not there: nothing leaves this PC.
        var hint = IntrinsicFit(@"\\localhost\glimpse-none\" + name);
        Assert.Equal(StageFitHint.FitKind.Scroll, hint?.Kind);
    });

    [Fact]
    public void AFolderWithADocumentsNameStaysACard() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        Assert.Null(IntrinsicFit(temp.CreateDirectory("notes.md")));
    });

    [Fact]
    public void AFileWhoseBytesAreNotHereIsNotReadToShapeTheStage() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var path = Png(temp, "cloud.png");
        Assert.Equal(StageFitHint.FitKind.ImagePixels, IntrinsicFit(path)?.Kind);

        // What OneDrive leaves for a file it keeps online only.
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);
        try
        {
            Assert.Null(IntrinsicFit(path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    });

    [Fact]
    public void APlaceholderIsKnownByItsAttributes()
    {
        const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

        Assert.True(PreviewWindow.IsPlaceholder(FileAttributes.Archive | FileAttributes.Offline));
        Assert.True(PreviewWindow.IsPlaceholder(FileAttributes.Archive | RecallOnOpen));
        Assert.True(PreviewWindow.IsPlaceholder(FileAttributes.Archive | RecallOnDataAccess));
        Assert.False(PreviewWindow.IsPlaceholder(FileAttributes.Archive | FileAttributes.ReadOnly | FileAttributes.Hidden));
    }

    [Fact]
    public Task AnImagesPixelSizeStillReachesTheDetailsLine() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "sized.png", 640, 360));
        try
        {
            // One header read on the UI thread shapes the stage and names
            // the pixels; the details line no longer reads it again.
            Assert.Contains("640×360", window.SubtitleText.Text, StringComparison.Ordinal);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // Placement.
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    public void TheWelcomeIsCenteredInsideTheWorkAreaAtEveryScale(uint dpi)
    {
        Rect[] workAreas =
        [
            new(0, 0, 1920, 1032),
            new(0, 0, 2560, 1392),
            new(0, 0, 1366, 728),
            new(-1920, 120, 1920, 1040)
        ];
        foreach (var work in workAreas)
        {
            // What the welcome does: the default stage, centered on the
            // monitor it was read from, sent back to that monitor's pixels.
            var monitor = StageMonitor.FromPixels(work, dpi);
            var placed = monitor.ToPixels(StagePlacement.CenterAndClamp(PreviewWindow.StageSize(monitor.WorkArea), monitor.WorkArea));

            Assert.True(placed.Left >= work.Left - 1 && placed.Right <= work.Right + 1, $"{placed} leaves {work} at {dpi} dpi");
            Assert.True(placed.Top >= work.Top - 1 && placed.Bottom <= work.Bottom + 1, $"{placed} leaves {work} at {dpi} dpi");
            Assert.InRange((placed.Left - work.Left) - (work.Right - placed.Right), -2, 2);
            Assert.InRange((placed.Top - work.Top) - (work.Bottom - placed.Bottom), -2, 2);
        }
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    public void TheFirstPlacementIsMadeInTheMonitorsOwnPixels(uint dpi) => Sta.Run(() =>
    {
        // A window that has never been shown, and no Explorer window to
        // follow: the welcome's case. It takes its handle first, so it is
        // placed in pixels, through the scale of the monitor it was read
        // from.
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
        try
        {
            Fields.Call(window, "ApplyStageGeometry", IntPtr.Zero);
            AssertCenteredOnTheParkedMonitor(window);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void TheWelcomeOpensCenteredAndStaysThere() => Sta.Run(() =>
    {
        // At the scale this PC really runs at, so the steps after the
        // placement see the same DIPs WPF does and have nothing to correct.
        var dpi = (uint)Math.Round(PreviewWindow.ReadMonitor(IntPtr.Zero).Scale * 96);
        var window = new PreviewWindow { ParkedMonitor = StageMonitor.FromPixels(ParkedPixels, dpi) };
        try
        {
            window.ShowWelcome();
            AssertCenteredOnTheParkedMonitor(window);
        }
        finally
        {
            window.Close();
        }
    });

    private static void AssertCenteredOnTheParkedMonitor(PreviewWindow window)
    {
        var rect = StagePixels(window);
        Assert.True(rect.Left >= ParkedPixels.Left && rect.Right <= ParkedPixels.Right, $"{rect.Left}..{rect.Right}");
        Assert.True(rect.Top >= ParkedPixels.Top && rect.Bottom <= ParkedPixels.Bottom, $"{rect.Top}..{rect.Bottom}");
        Assert.InRange((rect.Left - ParkedPixels.Left) - (ParkedPixels.Right - rect.Right), -2, 2);
        Assert.InRange((rect.Top - ParkedPixels.Top) - (ParkedPixels.Bottom - rect.Bottom), -2, 2);
    }

    [Fact]
    public void ABorderDragStopsAtTheMinimumSize() => Sta.Run(() =>
    {
        const int WM_GETMINMAXINFO = 0x0024;
        var window = new PreviewWindow { Left = -30000, Top = 0 };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.MINMAXINFO>());
        try
        {
            // The window answers the message itself to keep a maximized
            // stage off the taskbar, so WPF never gets to write the floor.
            Marshal.StructureToPtr(new NativeMethods.MINMAXINFO(), buffer, false);
            NativeMethods.SendMessage(hwnd, WM_GETMINMAXINFO, IntPtr.Zero, buffer);
            var limits = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(buffer);

            var scale = NativeMethods.GetDpiForWindow(hwnd) / 96.0;
            Assert.Equal((int)Math.Ceiling(window.MinWidth * scale), limits.ptMinTrackSize.X);
            Assert.Equal((int)Math.Ceiling(window.MinHeight * scale), limits.ptMinTrackSize.Y);
            Assert.True(limits.ptMaxSize.X > 0);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            window.Close();
        }
    });

    [Fact]
    public void ThePinLimitTooltipNamesNoNumber() => Sta.Run(() =>
    {
        var window = new PreviewWindow();
        try
        {
            // The cap is the controller's; a number here was a second copy.
            window.SetPinLimitReached(true);
            Assert.False(window.PinButton.IsEnabled);
            Assert.DoesNotMatch(@"\d", (string)window.PinButton.ToolTip);
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // With Windows animations off, nothing fades and nothing waits to fade.
    // The override is set and cleared with no await between, since every
    // window test shares one thread.
    // ---------------------------------------------------------------------

    [Fact]
    public void TheSelectionSheetSnapsInWithMotionOff() => Sta.Run(() =>
    {
        using var temp = new TempDirectory();
        var window = new PreviewWindow();
        Motion.Override = false;
        try
        {
            window.SetMultiSelection([Png(temp, "a.png"), Png(temp, "b.png")], 0);
            window.ToggleGridView();
            var sheet = Fields.Get<SelectionGridView>(window, "_gridView").Root;
            Assert.Equal(1.0, sheet.Opacity);
            Assert.False(sheet.HasAnimatedProperties);

            // The sheet draws over both hosts and the stand-in.
            Assert.True(Panel.GetZIndex(sheet) > Panel.GetZIndex(Back(window)));
        }
        finally
        {
            Motion.Override = null;
            window.Close();
        }
    });

    [Fact]
    public void ARailSnapsInWithMotionOff() => Sta.Run(() =>
    {
        var host = new ContentControl();
        var rail = new Border();
        Motion.Override = false;
        try
        {
            typeof(PreviewWindow)
                .GetMethod("AnimateRail", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [host, 132.0, rail]);
            Assert.Same(rail, host.Content);
            Assert.Equal(1.0, rail.Opacity);
            Assert.False(rail.HasAnimatedProperties);
        }
        finally
        {
            Motion.Override = null;
        }
    });

    [Fact]
    public void ACloakedDocumentComesBackInOneCutWithMotionOff()
    {
        Assert.Contains("transition='none'", PreviewWindow.UncloakScript(motion: false), StringComparison.Ordinal);
        Assert.Contains("transition='opacity 120ms ease-out'", PreviewWindow.UncloakScript(motion: true), StringComparison.Ordinal);
        Assert.Contains("opacity='1'", PreviewWindow.UncloakScript(motion: false), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSwitchThumbFollowsTheMotionSwitch(bool motion) => Sta.Run(() =>
    {
        var toggle = new ToggleButton { Style = (Style)Application.Current.FindResource("HouseToggle") };
        toggle.ApplyTemplate();
        var thumb = (UIElement)toggle.Template.FindName("Thumb", toggle);
        Motion.Override = motion;
        try
        {
            toggle.IsChecked = true;
            var slide = (TranslateTransform)thumb.RenderTransform;
            Assert.Equal(20.0, (double)slide.GetAnimationBaseValue(TranslateTransform.XProperty));
            Assert.Equal(motion, slide.HasAnimatedProperties);
            if (!motion)
            {
                Assert.Equal(20.0, slide.X);
            }

            toggle.IsChecked = false;
            Assert.Equal(0.0, (double)((TranslateTransform)thumb.RenderTransform).GetAnimationBaseValue(TranslateTransform.XProperty));
        }
        finally
        {
            Motion.Override = null;
        }
    });

    [Fact]
    public Task TheBeatTakesNoTimeWithMotionOff() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            await ShowPageAsync(window, Png(temp, "second.png"));
            Motion.Override = false;
            try
            {
                // Nothing fades, so the page leaves inside the call.
                Browse(window, Png(temp, "third.png"));
                Assert.Null(window.PreviewContent.Content);
            }
            finally
            {
                Motion.Override = null;
            }
        }
        finally
        {
            window.Close();
        }
    });

    // ---------------------------------------------------------------------
    // The stage and its hosts.
    // ---------------------------------------------------------------------

    /// <summary>A parked, frozen stage, like the probe's.</summary>
    private static PreviewWindow Stage()
    {
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        return window;
    }

    /// <summary>
    /// Opens a stage on an item and waits until it has fully arrived and the
    /// window has faded in, so the next show is a browse step.
    /// </summary>
    private static async Task<PreviewWindow> OpenAsync(string path)
    {
        var window = Stage();
        window.ShowPreview(path, IntPtr.Zero);
        await WaitUntilAsync(() => Settled(window) && Fields.Get<byte>(window, "_windowAlpha") == 255);
        return window;
    }

    /// <summary>A browse step whose lane is still working: it hands its content over only when the test does.</summary>
    private static void Browse(PreviewWindow window, string path)
    {
        window.ShowPreview(path, IntPtr.Zero);
        Fields.Get<CancellationTokenSource>(window, "_loadCancellation").Cancel();
    }

    /// <summary>Browses to a page the test supplies, and waits until it has navigated and landed.</summary>
    private static async Task<Grid> ShowPageAsync(PreviewWindow window, string path)
    {
        Browse(window, path);
        var page = Web(WebSurfaceKind.Page);
        Hand(window, page);
        Navigate(window, page);
        await WaitUntilAsync(() => Settled(window));
        Assert.Same(page, Front(window).Content);
        return page;
    }

    private static void Hand(PreviewWindow window, FrameworkElement content) =>
        Fields.Call(window, "SetPreviewContent", content, null);

    /// <summary>The view finishes navigating, as its engine would report it.</summary>
    private static void Navigate(PreviewWindow window, Grid web) =>
        Fields.Call(window, "OnNavigationCompleted", ViewIn(web), true);

    private static Grid Web(WebSurfaceKind kind) => new() { Children = { new StagedWebView(kind) } };

    private static StagedWebView ViewIn(Grid web) => (StagedWebView)web.Children[0];

    /// <summary>Where the window really is, in pixels, whatever DPI the test machine runs at.</summary>
    private static Rect StagePixels(PreviewWindow window)
    {
        Assert.True(NativeMethods.GetWindowRect(window.Hwnd, out var rect));
        return new Rect(rect.Left, rect.Top, rect.Width, rect.Height);
    }

    private static ContentControl Front(PreviewWindow window) => Fields.Get<ContentControl>(window, "_front");

    private static ContentControl Back(PreviewWindow window) => Fields.Get<ContentControl>(window, "_back");

    private static object Presentation(PreviewWindow window) => Fields.Get<object>(window, "_presentation");

    private static bool Revealed(PreviewWindow window) => Fields.Get<bool>(window, "_loadFinished");

    /// <summary>The load has revealed and landed: one item in front, nothing arriving, no stand-in.</summary>
    private static bool Settled(PreviewWindow window) =>
        Revealed(window)
        && Back(window).Content is null
        && window.StandInLayer.Visibility == Visibility.Collapsed;

    private static StageFitHint? IntrinsicFit(string path)
    {
        var window = new PreviewWindow();
        try
        {
            Fields.Call(window, "TrySetIntrinsicStageFit", path, ItemFacts.ReadAsync(path, CancellationToken.None).Result, 0L);
            return Fields.Get<StageFitHint?>(window, "_stageFitHint");
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }

    private static string Png(TempDirectory temp, string name, int width = 400, int height = 300)
    {
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(temp.Path, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }

    private sealed class Disposer(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
