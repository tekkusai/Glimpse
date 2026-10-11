using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// The reveal's decisions, run on a fake clock: what the user sees while a
/// load waits, who may reveal it, and how the item on screen makes way.
/// </summary>
public sealed class RevealPlanTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private sealed class Clock
    {
        public TimeSpan Now { get; set; }

        public Func<TimeSpan> Read => () => Now;
    }

    private static RevealPlan Open(Clock clock, bool motion = true) => new(1, freshOpen: true, clock.Read, motion);

    private static RevealPlan Browse(Clock clock, SwapStyle swap = SwapStyle.FadeOut, bool beatOver = true)
    {
        var plan = new RevealPlan(2, freshOpen: false, clock.Read, motion: true) { Swap = swap };
        if (beatOver)
        {
            plan.EndBeat();
        }

        return plan;
    }

    [Fact]
    public void SupersededLoadsNeverReveal()
    {
        var plan = Open(new Clock());
        plan.Supersede();

        Assert.False(plan.TryReveal());
        Assert.False(plan.TryShowWindow());
        Assert.Equal(WaitView.Nothing, plan.WaitAt(RevealPlan.SheenAt, shaped: true));
    }

    [Fact]
    public void EachLoadRevealsOnce()
    {
        var plan = Open(new Clock());

        Assert.True(plan.TryReveal());
        Assert.False(plan.TryReveal());
    }

    [Fact]
    public void TheWindowFadesInOnce()
    {
        var open = Open(new Clock());
        Assert.True(open.TryShowWindow());
        Assert.False(open.TryShowWindow());

        // A browse step's window is already on screen.
        Assert.False(Browse(new Clock()).TryShowWindow());
    }

    [Fact]
    public void ALateTimerNeverBringsTheStandInBackOverRevealedContent()
    {
        var clock = new Clock();
        var plan = Open(clock);
        clock.Now = 80 * Ms;
        Assert.True(plan.TryReveal());

        foreach (var moment in new[] { RevealPlan.StandInAt, RevealPlan.SheenAt, RevealPlan.ReasonAt })
        {
            clock.Now = moment;
            Assert.Equal(WaitView.Nothing, plan.WaitAtMoment(moment, shaped: true));
        }
    }

    [Fact]
    public void TheWaitShowsNothingNewForTheFirst120Ms()
    {
        var plan = Open(new Clock());

        Assert.Equal(WaitView.Nothing, plan.WaitAt(119 * Ms, shaped: true));
        Assert.Equal(WaitView.StandIn, plan.WaitAt(RevealPlan.StandInAt, shaped: true));
    }

    [Fact]
    public void ATimerFiringATickEarlyStillCountsAsItsMoment()
    {
        // Windows timers fire on 15.6 ms ticks: the 120 ms timer can run at
        // 113 ms. Read as 113 ms the stand-in waited for the one-second
        // timer instead.
        var clock = new Clock { Now = 113 * Ms };
        var plan = Open(clock);

        Assert.Equal(WaitView.StandIn, plan.WaitAtMoment(RevealPlan.StandInAt, shaped: true));
    }

    [Fact]
    public void TheSheenComesAfterASecondAndOnlyWithMotion()
    {
        Assert.Equal(WaitView.StandIn, Open(new Clock()).WaitAt(999 * Ms, shaped: true));
        Assert.Equal(WaitView.StandIn | WaitView.Sheen, Open(new Clock()).WaitAt(RevealPlan.SheenAt, shaped: true));
        Assert.Equal(WaitView.StandIn, Open(new Clock(), motion: false).WaitAt(RevealPlan.SheenAt, shaped: true));
    }

    [Fact]
    public void TheReasonLineNeedsAReasonAndItsMoment()
    {
        var plan = Open(new Clock());
        Assert.False(plan.WaitAt(RevealPlan.ReasonAt, shaped: true).HasFlag(WaitView.Reason));

        plan.Reason = "Converting with LibreOffice";
        Assert.False(plan.WaitAt(RevealPlan.ReasonAt - Ms, shaped: true).HasFlag(WaitView.Reason));
        Assert.True(plan.WaitAt(RevealPlan.ReasonAt, shaped: true).HasFlag(WaitView.Reason));
    }

    [Fact]
    public void NothingWaitsOverAnItemThatIsStillLeaving()
    {
        var plan = Browse(new Clock(), beatOver: false);
        Assert.Equal(WaitView.Nothing, plan.WaitAt(RevealPlan.SheenAt, shaped: true));

        plan.EndBeat();
        Assert.NotEqual(WaitView.Nothing, plan.WaitAt(RevealPlan.SheenAt, shaped: true));
    }

    [Fact]
    public void AHeldItemIsNotReplacedByABlankStandIn()
    {
        var plan = Browse(new Clock(), SwapStyle.Hold);

        // A stand-in in the content's shape may dissolve over the held item.
        Assert.Equal(WaitView.StandIn, plan.WaitAt(RevealPlan.StandInAt, shaped: true));

        // A shapeless one would be a blank beat until the wait needs a sign of life.
        Assert.Equal(WaitView.Nothing, plan.WaitAt(RevealPlan.SheenAt - Ms, shaped: false));
        Assert.True(plan.WaitAt(RevealPlan.SheenAt, shaped: false).HasFlag(WaitView.StandIn));
    }

    [Fact]
    public void AFreshOpenHasNoBeatToWaitFor()
    {
        Assert.True(Open(new Clock()).BeatEnded.IsCompleted);
        Assert.False(new RevealPlan(2, freshOpen: false, () => TimeSpan.Zero, motion: true).BeatEnded.IsCompleted);
    }

    [Fact]
    public void ASecondBeatInOneLoadWaitsAgain()
    {
        var plan = Browse(new Clock());
        Assert.True(plan.BeatDone);

        plan.BeginBeat();
        Assert.False(plan.BeatDone);
        Assert.False(plan.BeatEnded.IsCompleted);

        plan.EndBeat();
        Assert.True(plan.BeatEnded.IsCompleted);
    }

    [Fact]
    public void SupersedingWakesAnythingWaitingOnTheBeat()
    {
        var plan = Browse(new Clock(), beatOver: false);
        var waiting = plan.BeatEnded;

        plan.Supersede();

        Assert.True(waiting.IsCompleted);
        Assert.False(plan.TryReveal());
    }

    [Theory]
    [InlineData(false, false, false, "Clear")]
    [InlineData(true, false, false, "Hold")]
    [InlineData(true, false, true, "FadeOut")]
    [InlineData(true, false, null, "FadeOut")]
    [InlineData(true, true, false, "FadeOut")]
    public void TheItemOnScreenHoldsOnlyWhenNothingForcesItOff(bool visible, bool web, bool? stageChanges, string expected)
    {
        // A browser window cannot be drawn over, and a new shape needs the
        // stage to move while nothing shows.
        Assert.Equal(Enum.Parse<SwapStyle>(expected), RevealPlan.ChooseSwap(visible, web, stageChanges));
    }

    [Fact]
    public void ARevealTicketHasOneOwner()
    {
        var current = true;
        var ticket = new RevealTicket(() => current);

        Assert.True(ticket.TryTake());
        Assert.False(ticket.TryTake());

        var stale = new RevealTicket(() => current);
        current = false;
        Assert.False(stale.TryTake());
    }

    [Fact]
    public async Task GatesAreCapped()
    {
        Task Instant(TimeSpan _) => Task.CompletedTask;

        Assert.True(await RevealPlan.WaitCappedAsync(Task.CompletedTask, RevealPlan.RailGateCap, Instant));
        Assert.False(await RevealPlan.WaitCappedAsync(new TaskCompletionSource().Task, RevealPlan.RailGateCap, Instant));
    }
}

public sealed class StandInTests
{
    [Fact]
    public void AGhostPageLandsWhereTheViewerDrawsItsFirstPage()
    {
        // Measured from the Chromium viewer: 5 px in from the rail, 8 px
        // under its 40 px toolbar, 20 px short of its right edge.
        var landing = StandIn.LandingIn(LandingKind.ViewerPage, 0.75, 132, 40, new Size(1000, 800));

        Assert.Equal(new Rect(137, 48, 843, 843 / 0.75), landing);
    }

    [Fact]
    public void ALonePageThatFitsHasNoScrollbarBesideIt()
    {
        // Measured on an A4 landscape page in an 1139 px area under the 40 px
        // toolbar: the viewer drops its scrollbar at 813 px of viewport, and
        // at 812 keeps the narrow fit, centered.
        const double a4 = 297.0 / 210;
        Rect At(double viewport, double railWidth = 0) =>
            StandIn.LandingIn(LandingKind.ViewerPage, a4, railWidth, 40, new Size(1139, viewport + 40))!.Value;

        Assert.Equal(new Rect(5, 48, 1128, 1128 / a4), At(813));
        Assert.Equal(new Rect(12.5, 48, 1114, 1114 / a4), At(812));
        Assert.Equal(new Rect(5, 48, 1114, 1114 / a4), At(700));
        Assert.Equal(new Rect(137, 48, 982, 982 / a4), At(813, railWidth: 132));
    }

    [Fact]
    public void ALonePageLandsWithNoRailBesideIt() => Sta.Run(() =>
    {
        // The stage reserves no rail for a one-page document and the viewer
        // shows none, so the ghost takes the viewer's whole width.
        Assert.Equal(new Rect(5, 48, 975, 975 / 0.75), Ghost(StageFitHint.PageAspect(0.75, singlePage: true)));
        Assert.Equal(new Rect(137, 48, 843, 843 / 0.75), Ghost(StageFitHint.PageAspect(0.75)));
    });

    [Fact]
    public void AOnePageDocumentIsKnownWithoutARailCache()
    {
        // One page renders no rail, so no rail cache ever counts it. The
        // probe the stage fit ran as the item opened does.
        using var temp = new TempDirectory();
        var one = WritePdf(temp, "one.pdf", pages: 1);
        var three = WritePdf(temp, "three.pdf", pages: 3);
        PdfPageSizeProbe.Read(one);
        PdfPageSizeProbe.Read(three);

        Assert.True(PreviewWindow.KnownSinglePage(one));
        Assert.False(PreviewWindow.KnownSinglePage(three));
    }

    [Fact]
    public void PixelsLandCenteredFillingTheirBox()
    {
        var area = new Size(1000, 800);

        Assert.Equal(new Rect(100, 0, 800, 800), StandIn.LandingIn(LandingKind.Pixels, 1, 0, 0, area));
    }

    [Fact]
    public void ContentWithoutAShapeHasNoLanding()
    {
        Assert.Null(StandIn.LandingIn(LandingKind.None, 1, 0, 0, new Size(1000, 800)));
        Assert.Null(StandIn.LandingIn(LandingKind.Pixels, double.NaN, 0, 0, new Size(1000, 800)));
        Assert.False(StandIn.KnowsShape(null));
        Assert.False(StandIn.KnowsShape(StageFitHint.Scroll()));
        Assert.True(StandIn.KnowsShape(StageFitHint.PageAspect(0.77)));
    }

    [Fact]
    public void APosterIsHonestOnlyWhenItIsAPictureInTheContentsShape()
    {
        Assert.True(StandIn.IsHonestPoster(Bitmap(768, 1024), 0.75));
        Assert.False(StandIn.IsHonestPoster(Bitmap(96, 128), 0.75));
        Assert.False(StandIn.IsHonestPoster(Bitmap(1024, 1024), 0.75));
        Assert.False(StandIn.IsHonestPoster(null, 0.75));
    }

    [Fact]
    public Task TheSheenStopsTickingWhenTheStandInGoes() => WpfApp.RunAsync(async () =>
    {
        if (!Motion.Enabled)
        {
            return;
        }

        // Clocks tick only where WPF renders: an off-screen window.
        var standIn = StandIn.Create(StageFitHint.PageAspect(0.75), 1, 132, 40, null);
        var host = new Window { Left = -30000, Top = 0, Width = 400, Height = 300, ShowActivated = false, Content = standIn };
        host.Show();
        try
        {
            var shift = Fields.Get<TranslateTransform>(standIn, "_sheenShift");
            standIn.StartSheen();
            var clock = AppliedClock(shift, TranslateTransform.XProperty);
            await Task.Delay(150);
            Assert.Equal(ClockState.Active, clock.CurrentState);

            standIn.Stop();
            await Task.Delay(150);

            // A clock only detached would still be Active, ticking WPF's
            // renderer at 64 Hz until a GC found it.
            Assert.False(shift.HasAnimatedProperties);
            Assert.Equal(ClockState.Stopped, clock.CurrentState);
        }
        finally
        {
            host.Close();
        }
    });

    /// <summary>The clock driving a property, read from WPF's own animation storage.</summary>
    private static AnimationClock AppliedClock(Animatable target, DependencyProperty property)
    {
        const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
        var storageType = typeof(Animatable).Assembly.GetType("System.Windows.Media.Animation.AnimationStorage")!;
        var storage = storageType.GetMethod("GetStorage", Hidden)!.Invoke(null, [target, property])!;
        var clocks = storageType.GetField("_animationClocks", Hidden)!.GetValue(storage)!;
        var count = (int)clocks.GetType().GetProperty("Count")!.GetValue(clocks)!;
        return (AnimationClock)clocks.GetType().GetMethod("get_Item")!.Invoke(clocks, [count - 1])!;
    }

    private static BitmapSource Bitmap(int width, int height) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Gray8, null, new byte[width * height], width);

    /// <summary>Where a stand-in draws its ghost in a 1000 x 800 area under a 40 px toolbar, beside a 132 px rail.</summary>
    private static Rect Ghost(StageFitHint hint)
    {
        var standIn = StandIn.Create(hint, 1, 132, 40, null);
        standIn.Measure(new Size(1000, 800));
        standIn.Arrange(new Rect(0, 0, 1000, 800));
        return LayoutInformation.GetLayoutSlot((FrameworkElement)standIn.Children[0]);
    }

    /// <summary>A document of letter pages with a classic cross-reference table.</summary>
    private static string WritePdf(TempDirectory temp, string name, int pages)
    {
        var kids = string.Join(' ', Enumerable.Range(3, pages).Select(number => $"{number} 0 R"));
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
            .. Enumerable.Repeat("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>", pages)
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xref = text.Length;
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append($"{offset:0000000000} 00000 n \n");
        }

        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        var path = Path.Combine(temp.Path, name);
        File.WriteAllText(path, text.ToString(), Encoding.ASCII);
        return path;
    }
}

/// <summary>
/// The beat and the wait on the real window. The next item's lane is held
/// back, and hands its content over when the test says, so each moment the
/// user sees can be checked in turn.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class RevealWindowTests
{
    [Fact]
    public Task AStandInTakesTheHeldItemsPlace() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));
            Assert.Same(held, window.PreviewContent.Content);

            // At 120 ms the stand-in shows over the held item, so its sheen
            // and reason line show too.
            await WaitUntilAsync(() => window.StandInLayer.Visibility == Visibility.Visible);
            Assert.Same(held, window.OutgoingContent.Content);
            Assert.Null(window.PreviewContent.Content);

            // The content dissolves in over the stand-in, then both leave.
            var next = new Border();
            Fields.Call(window, "SetPreviewContent", next, null);
            await WaitUntilAsync(() => window.OutgoingContent.Content is null);
            Assert.Same(next, window.PreviewContent.Content);
            Assert.Equal(Visibility.Collapsed, window.StandInLayer.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task APressBeforeTheStandInKeepsTheItemHeldUnderAStagedDocument() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));
            var document = Web(WebSurfaceKind.PdfViewer);
            Fields.Call(window, "SetPreviewContent", document, null);
            Assert.Same(held, window.OutgoingContent.Content);
            Assert.Equal(Visibility.Hidden, document.Visibility);

            // The held item is all that shows, so it holds for the next item
            // instead of dropping to a blank canvas.
            Browse(window, Png(temp, "third.png"));
            Assert.Same(held, window.OutgoingContent.Content);

            var next = new Border();
            Fields.Call(window, "SetPreviewContent", next, null);
            await WaitUntilAsync(() => window.OutgoingContent.Content is null);
            Assert.Same(next, window.PreviewContent.Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task AFadeOutTakesTheItemHeldUnderAPageWithIt() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var window = await OpenAsync(Png(temp, "first.png"));
        try
        {
            var held = (UIElement)window.PreviewContent.Content;
            Browse(window, Png(temp, "second.png"));
            var page = Web(WebSurfaceKind.Page);
            Fields.Call(window, "SetPreviewContent", page, null);
            Assert.Same(held, window.OutgoingContent.Content);
            Assert.Equal(Visibility.Visible, page.Visibility);

            // A page cannot be held, so the next press fades it out. The item
            // under it fades with it rather than showing through.
            Browse(window, Png(temp, "third.png"));
            Assert.Equal(0.0, (double)held.GetAnimationBaseValue(UIElement.OpacityProperty));
            await WaitUntilAsync(() => window.OutgoingContent.Content is null);
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>
    /// Opens a parked, frozen stage on an item and waits until it has fully
    /// arrived and the window has faded in, so the next show is a browse step.
    /// </summary>
    private static async Task<PreviewWindow> OpenAsync(string path)
    {
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        window.ShowPreview(path, IntPtr.Zero);
        await WaitUntilAsync(() =>
            Fields.Get<bool>(window, "_loadFinished")
            && Fields.Get<byte>(window, "_windowAlpha") == 255
            && window.StandInLayer.Visibility == Visibility.Collapsed
            && window.OutgoingContent.Content is null);
        return window;
    }

    /// <summary>A browse step whose lane is still working: it hands its content over only when the test does.</summary>
    private static void Browse(PreviewWindow window, string path)
    {
        window.ShowPreview(path, IntPtr.Zero);
        Fields.Get<CancellationTokenSource>(window, "_loadCancellation").Cancel();
    }

    private static Grid Web(WebSurfaceKind kind) => new() { Children = { new StagedWebView(kind) } };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the window.");
            await Task.Delay(10);
        }
    }

    private static string Png(TempDirectory temp, string name)
    {
        const int width = 400;
        const int height = 300;
        var pixels = Enumerable.Repeat((byte)0x80, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var path = Path.Combine(temp.Path, name);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}

/// <summary>Traps in the reveal path that only a source check can see.</summary>
public sealed class RevealTrapTests
{
    [Fact]
    public void ASavedPageLayoutCheckThatRunsLateDoesNothing()
    {
        // ExecuteScriptAsync cannot be cancelled. The layout check reads the
        // deadline before it touches the page, and never changes the page.
        var body = TestSource.Method(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "WaitForDocumentPaintAsync");
        var deadline = body.IndexOf("if (Date.now() >", StringComparison.Ordinal);
        var firstRead = body.IndexOf("getComputedStyle(root)", StringComparison.Ordinal);

        Assert.True(deadline >= 0 && deadline < firstRead);
        Assert.DoesNotContain("opacity", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transition", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADyingBrowserViewIsHiddenBeforeItIsDisposed()
    {
        // A WebView2 being disposed paints a black frame on its way out.
        var release = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "ReleaseShown");

        Assert.True(
            release.IndexOf("Visibility.Collapsed", StringComparison.Ordinal)
            < release.IndexOf("ClearPreviewContent()", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryWaitAndBeatTimerRunsAtRenderPriority()
    {
        // Background priority let icon and scan work hold a ready card off
        // screen for hundreds of milliseconds.
        var runAfter = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunAfter");

        Assert.Contains("DispatcherPriority.Render", runAfter, StringComparison.Ordinal);
    }
}
