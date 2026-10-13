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
/// The reveal's decisions as pure functions of a moment on a load's clock:
/// what the user sees while a load waits, and how the item on screen makes
/// way. Who may reveal, and when, is state the window keeps; the window
/// tests in RevealSequencingTests cover it.
/// </summary>
public sealed class RevealPlanTests
{
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);

    private static WaitView Wait(
        TimeSpan elapsed,
        bool overItem = false,
        bool poster = false,
        bool reason = false,
        bool motion = true) =>
        RevealPlan.WaitAt(elapsed, overItem, poster, reason, motion);

    [Fact]
    public void TheWaitShowsNothingNewForTheFirst120Ms()
    {
        Assert.Equal(WaitView.Nothing, Wait(119 * Ms));
        Assert.Equal(WaitView.StandIn, Wait(RevealPlan.StandInAt));
    }

    [Fact]
    public void ATimerFiringATickEarlyStillCountsAsItsMoment()
    {
        // Windows timers fire on 15.6 ms ticks: the 120 ms timer can run at
        // 113 ms. Read as 113 ms the stand-in waited for the one-second
        // timer instead.
        Assert.Equal(WaitView.Nothing, Wait(113 * Ms));
        Assert.Equal(WaitView.StandIn, Wait(RevealPlan.Moment(113 * Ms, RevealPlan.StandInAt)));

        // A timer that fires late reads the clock as it is.
        Assert.Equal(140 * Ms, RevealPlan.Moment(140 * Ms, RevealPlan.StandInAt));
    }

    [Fact]
    public void TheSheenComesAfterASecondAndOnlyWithMotion()
    {
        Assert.Equal(WaitView.StandIn, Wait(999 * Ms));
        Assert.Equal(WaitView.StandIn | WaitView.Sheen, Wait(RevealPlan.SheenAt));
        Assert.Equal(WaitView.StandIn, Wait(RevealPlan.SheenAt, motion: false));
    }

    [Fact]
    public void TheReasonLineNeedsAReasonAndItsMoment()
    {
        Assert.False(Wait(RevealPlan.ReasonAt).HasFlag(WaitView.Reason));
        Assert.False(Wait(RevealPlan.ReasonAt - Ms, reason: true).HasFlag(WaitView.Reason));
        Assert.True(Wait(RevealPlan.ReasonAt, reason: true).HasFlag(WaitView.Reason));
    }

    [Fact]
    public void OverAnItemOnScreenOnlyAPosterTakesItsPlaceBeforeASecond()
    {
        // A real image of the next item may dissolve over the one on screen.
        Assert.Equal(WaitView.StandIn, Wait(RevealPlan.StandInAt, overItem: true, poster: true));

        // A ghost or the bare canvas would trade a picture for a flat
        // rectangle, so it waits until the wait needs a sign of life.
        Assert.Equal(WaitView.Nothing, Wait(RevealPlan.SheenAt - Ms, overItem: true));
        Assert.Equal(WaitView.StandIn | WaitView.Sheen, Wait(RevealPlan.SheenAt, overItem: true));

        // With nothing on screen there is nothing to trade away.
        Assert.Equal(WaitView.StandIn, Wait(RevealPlan.StandInAt));
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
    public void AOnePageDocumentIsKnownWithoutARailCache() => Sta.Run(() =>
    {
        // One page renders no rail, so no rail cache ever counts it. The
        // probe the stage fit runs as the item opens does, and its hint is
        // what tells the stand-in.
        using var temp = new TempDirectory();
        var one = WritePdf(temp, "one.pdf", pages: 1);
        var three = WritePdf(temp, "three.pdf", pages: 3);
        PdfPageSizeProbe.Read(one);
        PdfPageSizeProbe.Read(three);

        Assert.True(IntrinsicFit(one) is { Kind: StageFitHint.FitKind.PageAspect, SinglePage: true });
        Assert.True(IntrinsicFit(three) is { Kind: StageFitHint.FitKind.PageAspect, SinglePage: false });
    });

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
            await TickedTo(ClockState.Active);
            Assert.Equal(ClockState.Active, clock.CurrentState);

            standIn.Stop();
            await TickedTo(ClockState.Stopped);

            // A clock changes state at WPF's next tick. A fixed 150 ms was
            // not always enough on a busy PC, and the test read the old state.
            async Task TickedTo(ClockState state)
            {
                for (var waited = 0; waited < 5000 && clock.CurrentState != state; waited += 25)
                {
                    await Task.Delay(25);
                }
            }

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
    public void EveryWaitAndBeatTimerRunsAtRenderPriority()
    {
        // Background priority let icon and scan work hold a ready card off
        // screen for hundreds of milliseconds.
        var runAfter = TestSource.CodeMethod(@"SpacePreview\Preview\PreviewWindow.xaml.cs", "RunAfter");

        Assert.Contains("DispatcherPriority.Render", runAfter, StringComparison.Ordinal);
    }
}
