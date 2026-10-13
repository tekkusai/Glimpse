using System.Buffers.Binary;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    // ---------------------------------------------------------------------
    // Presentation. Every load runs on one clock, started by the input that
    // asked for it. Two hosts hold the content: the item on screen sits in
    // the front one, the next item waits hidden in the back one, and they
    // trade roles once it has arrived. One function, Advance, runs after
    // every event and does whatever is due (docs/DESIGN.md, Motion and
    // Waiting).
    // ---------------------------------------------------------------------

    // The order the content area draws in: the stand-in over the item on
    // screen, the next item over both, the selection sheet over everything.
    private const int FrontLayer = 0;
    private const int BackLayer = 2;
    private const int SheetLayer = 3;

    private Presentation? _presentation;
    private ContentControl _front;
    private ContentControl _back;
    private byte _windowAlpha = 255;

    /// <summary>
    /// One load's presentation, as plain state on its own clock. Advance
    /// reads it after every event; nothing else decides anything.
    /// </summary>
    private sealed class Presentation(long inputTimestamp, string? contentPath)
    {
        public long InputTimestamp { get; } = inputTimestamp;

        public string? ContentPath { get; } = contentPath;

        /// <summary>How the item on screen makes way. A fade that has run out becomes Clear.</summary>
        public SwapStyle Swap { get; set; }

        /// <summary>When a fade-out is over, on this load's clock.</summary>
        public TimeSpan BeatEnds { get; set; }

        /// <summary>This load's content, from its hand-over until it has dissolved in.</summary>
        public Arrival? Next { get; set; }

        /// <summary>The stand-in this load put up while it waited.</summary>
        public StandIn? StandIn { get; set; }

        /// <summary>A poster disk already has for the item, looked up off the UI thread.</summary>
        public Task<BitmapSource?>? Poster { get; set; }

        /// <summary>Why the wait is long, when there is a reason worth one line.</summary>
        public string? Reason { get; set; }

        // Milliseconds since the input; -1 for a step this load never took.
        public double Entered { get; set; }
        public double Headed { get; set; }
        public double Probed { get; set; }
        public double BeatEnd { get; set; } = -1;
        public double Built { get; set; } = -1;
        public double Attached { get; set; } = -1;
        public double Shown { get; set; } = -1;

        public TimeSpan Elapsed => Stopwatch.GetElapsedTime(InputTimestamp);

        public double Since() => Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Content in the back host: staged hidden, then dissolving in. The
    /// moments are on its load's clock, and null until each has happened.
    /// </summary>
    private sealed class Arrival(FrameworkElement content, StagedWebView? view)
    {
        public FrameworkElement Content { get; } = content;

        public StagedWebView? View { get; } = view;

        public TimeSpan? NavigationStarted { get; set; }

        public TimeSpan? Navigated { get; set; }

        /// <summary>When a navigated document began to wait for its gate.</summary>
        public TimeSpan? Gated { get; set; }

        public Task<bool>? Paint { get; set; }

        /// <summary>Set by the reveal: when the dissolve is over and what stood under it may go.</summary>
        public TimeSpan? Lands { get; set; }
    }

    /// <summary>
    /// The host of the newest content handed over: the next item's from the
    /// moment it arrives, the item on screen otherwise. The lanes reach the
    /// current content through it.
    /// </summary>
    internal ContentControl PreviewContent => _back.Content is null ? _front : _back;

    /// <summary>What the newest content owns (its browser view). It is kept on the host, so it travels with it.</summary>
    private IDisposable? _currentDisposable
    {
        get => PreviewContent.Tag as IDisposable;
        set => PreviewContent.Tag = value;
    }

    /// <summary>
    /// The load on screen stops being current the moment another begins.
    /// Nothing of it acts again, and until the next load has its own
    /// presentation there is none: a shape learned while that load is being
    /// set up is only recorded, never judged against the one before it.
    /// </summary>
    private Presentation? RetirePresentation()
    {
        var before = _presentation;
        _presentation = null;
        _loadFinished = false;
        return before;
    }

    /// <summary>
    /// Starts a load's presentation. Input older than two seconds is not
    /// this load's input (a file dialog selection, the welcome), so the
    /// clock then starts here.
    /// </summary>
    private Presentation BeginPresentation(bool freshOpen, long inputTimestamp, long entered, string? contentPath)
    {
        var start = inputTimestamp > 0 && Stopwatch.GetElapsedTime(inputTimestamp, entered) < TimeSpan.FromSeconds(2)
            ? inputTimestamp
            : entered;
        var p = new Presentation(start, contentPath)
        {
            Entered = Stopwatch.GetElapsedTime(start, entered).TotalMilliseconds
        };
        _presentation = p;
        DiagnosticLog.Write($"load start {(freshOpen ? "open" : "browse")} +{p.Entered:0}ms");
        return p;
    }

    /// <summary>
    /// Sets the wait's three moments on the load's clock. The disk lookup
    /// for a poster starts now, off the UI thread, so it is ready when the
    /// first of them comes.
    /// </summary>
    private void ScheduleWait(Presentation p)
    {
        if (p.ContentPath is { } path && StandIn.KnowsShape(CurrentShape()) && LooksLikeLocalDrivePath(path))
        {
            p.Poster = Task.Run(() => FindPoster(path));
        }

        Wake(p, RevealPlan.StandInAt);
        Wake(p, RevealPlan.SheenAt);
        Wake(p, RevealPlan.ReasonAt);
    }

    /// <summary>
    /// Runs Advance at a moment still ahead on the load's clock. One already
    /// past is the caller's to act on: every event ends in Advance.
    /// </summary>
    private void Wake(Presentation p, TimeSpan at)
    {
        var wait = at - p.Elapsed;
        if (wait > TimeSpan.Zero)
        {
            RunAfter(wait, () => Advance(p, at));
        }
    }

    /// <summary>Runs Advance when a task ends: a gate that answers is an event like any other.</summary>
    private async void AdvanceAfter(Presentation p, Task task)
    {
        try
        {
            await task;
        }
        catch
        {
            // However it ended, it has ended.
        }

        Advance(p);
    }

    /// <summary>The content's shape, once the current load knows it.</summary>
    private StageFitHint? CurrentShape() => _stageFitVersion == _loadVersion ? _stageFitHint : null;

    /// <summary>
    /// The one step function. It runs after every event (content handed
    /// over, a timer, navigation, a gate answering, a hint) and does what is
    /// due, in order: the old item leaves, the stage takes its shape, the
    /// content reveals, what stood under it goes, the wait shows. Each step
    /// reads the load's state, so running it again changes nothing, and a
    /// load that is no longer current does nothing at all.
    /// </summary>
    private void Advance(Presentation p, TimeSpan setFor = default)
    {
        if (!ReferenceEquals(_presentation, p))
        {
            return;
        }

        var now = RevealPlan.Moment(p.Elapsed, setFor);

        // The beat is over: the old item has faded out and leaves.
        if (p.Swap == SwapStyle.FadeOut && now >= p.BeatEnds)
        {
            ReleaseShown();
            p.Swap = SwapStyle.Clear;
            p.BeatEnd = p.Since();
        }

        // The stage takes the item's shape in one step, whenever it learns
        // it, but only while the old item is gone and nothing of this one
        // shows. A held item stays put.
        if (p.Swap == SwapStyle.Clear && !_loadFinished && p.Next is not { Content.Visibility: Visibility.Visible })
        {
            RetargetStageToFit();
        }

        if (p.Next is { Lands: null } arriving)
        {
            Present(p, arriving, now);

            // A reveal fades the window in, and a window Windows refuses to
            // place is closed on the spot.
            if (!ReferenceEquals(_presentation, p))
            {
                return;
            }
        }

        if (p.Next is { Lands: { } lands } && now >= lands)
        {
            Land(p);
        }

        ShowWait(p, now);
    }

    // ---------------------------------------------------------------------
    // The wait: what shows while the content is not ready.
    // ---------------------------------------------------------------------

    /// <summary>
    /// The stand-in at 120 ms, a sheen after a second, the reason line after
    /// 1.2 s (docs/DESIGN.md, Waiting). Nothing shows once the item has
    /// revealed, or while the old one is still fading out.
    /// </summary>
    private void ShowWait(Presentation p, TimeSpan now)
    {
        if (_loadFinished || p.Swap == SwapStyle.FadeOut)
        {
            return;
        }

        // A lookup still running when its moment comes counts as no poster.
        var shape = CurrentShape();
        var poster = p.Poster is { IsCompletedSuccessfully: true } found ? found.Result : null;
        var overItem = p.Swap == SwapStyle.Hold
            && _front.Content is not null
            && StandInLayer.Visibility != Visibility.Visible;
        var view = RevealPlan.WaitAt(now, overItem, StandIn.ShowsPoster(shape, poster), p.Reason is not null, Motion.Enabled);
        if (view == WaitView.Nothing)
        {
            return;
        }

        if (p.StandIn is null || !ReferenceEquals(StandInLayer.Content, p.StandIn))
        {
            // A fresh open's wait is over: the window shows what there is.
            var withWindow = _windowAlpha == 0;
            ShowStandIn(p, shape, poster, withWindow);
            if (withWindow)
            {
                FadeWindowIn(p);
            }
        }

        if (view.HasFlag(WaitView.Sheen))
        {
            p.StandIn?.StartSheen();
        }

        if (view.HasFlag(WaitView.Reason) && p.Reason is { } reason)
        {
            p.StandIn?.ShowReason(reason);
        }
    }

    /// <summary>The one reason line a slow wait may carry; it shows now if its moment has passed.</summary>
    private void SetWaitReason(long version, string reason)
    {
        if (version == _loadVersion && _presentation is { } p)
        {
            p.Reason = reason;
            Advance(p);
        }
    }

    /// <summary>
    /// Puts up the best image of the load's content: a poster, a ghost in
    /// its shape, or the bare canvas. It belongs to the load that made it.
    /// It dissolves in over what shows, the item on screen included; one
    /// stand-in replaces another where it stands.
    /// </summary>
    private void ShowStandIn(Presentation p, StageFitHint? shape, BitmapSource? poster, bool withWindow)
    {
        var replaces = StandInLayer.Visibility == Visibility.Visible;
        (StandInLayer.Content as StandIn)?.Stop();
        var standIn = StandIn.Create(shape, MonitorFor(Hwnd).Scale, PdfPageRailWidth, ViewerChromeBandHeight, poster);
        p.StandIn = standIn;
        StandInLayer.Content = standIn;
        if (withWindow)
        {
            Motion.Settle(StandInLayer, 1);
        }
        else
        {
            if (!replaces)
            {
                Motion.Settle(StandInLayer, 0);
            }

            Motion.Fade(StandInLayer, 1, Motion.Browse);
        }

        StandInLayer.Visibility = Visibility.Visible;
        DiagnosticLog.Write($"stand-in +{p.Since():0}ms {standIn.Description}");
    }

    private void HideStandIn()
    {
        (StandInLayer.Content as StandIn)?.Stop();
        Motion.Settle(StandInLayer, 1);
        StandInLayer.Content = null;
        StandInLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Where a stand-in's poster may come from, in order of honesty; the
    /// first that answers wins (docs/DESIGN.md, Waiting).
    /// </summary>
    private static readonly Func<string, BitmapSource?>[] PosterSources =
    [
        PdfRailFirstPage,
        OfficeEmbeddedThumbnail,
        ExplorerCachedThumbnail
    ];

    private static BitmapSource? FindPoster(string path)
    {
        foreach (var source in PosterSources)
        {
            try
            {
                if (source(path) is { } poster)
                {
                    return poster;
                }
            }
            catch
            {
                // A source that fails just has no poster.
            }
        }

        return null;
    }

    /// <summary>Page one from the rail cache of a document opened before.</summary>
    private static BitmapSource? PdfRailFirstPage(string path)
    {
        var pdf = RailDocumentPath(path);
        return pdf is null
            ? null
            : TryReadPdfThumbnailManifest(PdfRailCacheDirectory(pdf, PdfPageRailInitialPages, 0)) is { Pages.Count: > 0 } rail
                ? rail.Pages[0].Bitmap
                : null;
    }

    private static BitmapSource? OfficeEmbeddedThumbnail(string path) =>
        FileKinds.IsOffice(path) ? OfficeThumbnail.TryRead(path) : null;

    private static BitmapSource? ExplorerCachedThumbnail(string path) =>
        ShellThumbnailProvider.TryGetCachedThumbnail(path, 1024)?.Bitmap;

    /// <summary>The .pdf the viewer shows for this item, if one was made before.</summary>
    private static string? RailDocumentPath(string path)
    {
        // Artwork reaches the viewer through a .pdf link of its own, and
        // FileKinds counts PDF-backed artwork as a PDF.
        if (FileKinds.IsIllustrator(path))
        {
            return PdfViewerLink.TryGetCached(path);
        }

        if (FileKinds.IsPdf(path))
        {
            return File.Exists(path) ? path : null;
        }

        return FileKinds.IsOffice(path)
            ? OfficeDocumentPdfConverter.TryGetCached(path)?.DocumentPath
            : null;
    }

    // ---------------------------------------------------------------------
    // The beat: how the item on screen makes way for the next one.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Whether the next item needs a new stage: false when its shape is known
    /// and matches (or the stage is held), true when it differs, null when it
    /// is not known yet.
    /// </summary>
    private bool? WillStageChange(string contentPath)
    {
        if (_stageFrozenForProbe || IsStageMaximized || _itemStageSize is not null)
        {
            return false;
        }

        if (_stageFitVersion != _loadVersion && !UsesDefaultStageWithoutProbe(contentPath, _facts))
        {
            return null;
        }

        if (_recenterStage)
        {
            return true;
        }

        var monitor = MonitorFor(Hwnd);
        var target = ComputeStageTarget(monitor.WorkArea, monitor.Scale);
        return Math.Abs(target.Width - Width) > StageFit.Hysteresis
            || Math.Abs(target.Height - Height) > StageFit.Hysteresis;
    }

    /// <summary>
    /// The item on screen makes way. WPF content of the same shape holds
    /// while the next item dissolves over it; anything else fades out (a
    /// browser view fades inside its page, since nothing can be drawn over
    /// it), then the stage takes the new shape in one step. When nothing of
    /// the old item shows, it leaves at once. With motion off nothing fades,
    /// so the beat takes no time.
    /// </summary>
    private void BeginSwap(Presentation p, Presentation? before, bool? stageChanges)
    {
        // The scene settles to one item, in front, and the back host is free
        // for the next one.
        Settle(before);

        var shown = _front.Content as FrameworkElement;
        var standInShows = StandInLayer.Visibility == Visibility.Visible;
        var shownIsWeb = shown is not null && FindVisualChild<WebView2>(shown) is not null;

        // An item already fading out keeps its beat: another press does not
        // start the fade over.
        var fading = before is { Swap: SwapStyle.FadeOut };
        p.Swap = fading
            ? SwapStyle.FadeOut
            : RevealPlan.ChooseSwap(shown is not null || standInShows, shownIsWeb, stageChanges);
        switch (p.Swap)
        {
            case SwapStyle.Clear:
                ReleaseShown();
                p.BeatEnd = p.Since();
                break;

            case SwapStyle.Hold:
                // It is leaving: clicks belong to the next item.
                if (shown is not null)
                {
                    shown.IsHitTestVisible = false;
                }

                p.BeatEnd = p.Since();
                break;

            default:
                if (fading)
                {
                    var left = before!.BeatEnds - before.Elapsed;
                    p.BeatEnds = p.Elapsed + (left > TimeSpan.Zero ? left : TimeSpan.Zero);
                }
                else
                {
                    if (shown is not null)
                    {
                        shown.IsHitTestVisible = false;
                        if (shownIsWeb)
                        {
                            FadeOutWebContent(shown);
                        }
                        else
                        {
                            Motion.Fade(shown, 0, Motion.Leave, easeIn: true);
                        }
                    }

                    if (standInShows)
                    {
                        Motion.Fade(StandInLayer, 0, Motion.Leave, easeIn: true);
                    }

                    p.BeatEnds = p.Elapsed + (Motion.Enabled ? Motion.Leave : TimeSpan.Zero);
                }

                Wake(p, p.BeatEnds);
                break;
        }

        DiagnosticLog.Write($"swap {p.Swap.ToString().ToLowerInvariant()} +{p.Since():0}ms");
    }

    /// <summary>
    /// Empties the back host for the next item. A reveal still dissolving
    /// lands at once, so the item is whole when it starts to make way; an
    /// arrival nobody saw (still staging, or a browser view that failed
    /// while it started) is dropped.
    /// </summary>
    private void Settle(Presentation? owner)
    {
        if (owner?.Next is { Lands: not null })
        {
            Land(owner);
            return;
        }

        Release(_back);
        if (owner is not null)
        {
            owner.Next = null;
        }
    }

    /// <summary>
    /// Empties a host. A browser view being disposed paints a black frame on
    /// its way out, so its content hides in the same tick. Mode state dies
    /// with the newest content only: an item under a newer one has already
    /// given its up.
    /// </summary>
    private void Release(ContentControl host)
    {
        if (ReferenceEquals(host, PreviewContent))
        {
            ResetContentState();
        }

        if (host.Content is UIElement content)
        {
            content.Visibility = Visibility.Collapsed;
        }

        host.Content = null;
        host.ClearValue(WidthProperty);
        host.ClearValue(HeightProperty);
        (host.Tag as IDisposable)?.Dispose();
        host.Tag = null;
    }

    /// <summary>The item on screen leaves now, with whatever stand-in is up.</summary>
    private void ReleaseShown()
    {
        Release(_front);
        HideStandIn();
    }

    /// <summary>Everything on the stage leaves now.</summary>
    private void ReleaseStage()
    {
        Release(_back);
        ReleaseShown();
    }

    /// <summary>
    /// Nothing WPF can be drawn over a webview (the browser hwnd paints over
    /// every sibling), so web content fades itself out: an in-page fade on a
    /// transparent view, which shows the canvas under it. ExecuteScriptAsync
    /// runs even on hardened script-off pages. A page that refuses keeps its
    /// last frame until the beat ends.
    /// </summary>
    private static void FadeOutWebContent(FrameworkElement shown)
    {
        if (FindVisualChild<WebView2>(shown) is not { CoreWebView2: { } core } webView || !Motion.Enabled)
        {
            return;
        }

        try
        {
            webView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='opacity 90ms cubic-bezier(.4,0,1,1)';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            // Best-effort only.
        }
    }

    // ---------------------------------------------------------------------
    // Content arrives.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Every lane hands its content here, first presentation or fallback.
    /// It attaches to the back host at once, hidden, and stays in that host
    /// for as long as it lives: a browser view starts its engine while the
    /// old item is still leaving, and nothing is attached twice. There is
    /// one next slot, so whatever this load handed over before makes way.
    /// The content's own browser view is what it owns, unless the lane names
    /// something else.
    /// </summary>
    private void SetPreviewContent(FrameworkElement content, IDisposable? disposable)
    {
        var view = WebViewIn(content);
        var owned = disposable ?? view;
        if (_presentation is not { } p)
        {
            owned?.Dispose();
            return;
        }

        if (ReferenceEquals(_front.Content, content) || ReferenceEquals(_back.Content, content))
        {
            return;
        }

        if (p.Built < 0)
        {
            p.Built = p.Since();
        }

        Settle(p);

        // A second presentation in a revealed load (a fallback or an error
        // card) makes way like a browse step of the same shape.
        if (_loadFinished && _front.Content is FrameworkElement { Visibility: Visibility.Visible })
        {
            BeginSwap(p, null, stageChanges: false);
        }

        // The item it replaces hands over: its modes die before the new
        // content binds its own.
        ResetContentState();

        var next = new Arrival(content, view);
        p.Next = next;
        p.Attached = p.Since();
        content.Visibility = Visibility.Hidden;
        if (p.Swap == SwapStyle.FadeOut && WillStageChange(p.ContentPath ?? string.Empty) == true)
        {
            // Staged at the size the stage is about to take, so a document
            // lays out once.
            var monitor = MonitorFor(Hwnd);
            var window = ComputeStageTarget(monitor.WorkArea, monitor.Scale);
            _back.Width = Math.Max(1, window.Width - StageFit.ChromeWidth);
            _back.Height = Math.Max(1, window.Height - StageFit.ChromeHeight);
        }

        _back.Content = content;
        _back.Tag = owned;
        if (view is not null)
        {
            view.NavigationStarting += (_, _) => OnNavigationStarting(view);
            view.NavigationCompleted += (_, args) => OnNavigationCompleted(view, args.IsSuccess);
        }

        DiagnosticLog.Write(p.Swap == SwapStyle.FadeOut
            ? $"stage +{p.Since():0}ms (during the beat)"
            : $"stage +{p.Since():0}ms");
        Advance(p);
    }

    private static StagedWebView? WebViewIn(FrameworkElement content) =>
        content as StagedWebView ?? FindVisualChild<StagedWebView>(content);

    /// <summary>
    /// Brings the next item on once the old one has left, or holds. WPF
    /// content dissolves in. A page shows at once on its transparent view
    /// and dissolves itself in over whatever stands under it. A document
    /// (and a video still waiting for its shape) stays hidden until it has
    /// navigated, its gate has answered and it has painted, each capped,
    /// then appears in one cut.
    /// </summary>
    private void Present(Presentation p, Arrival next, TimeSpan now)
    {
        var content = next.Content;
        var view = next.View;
        if (p.Swap == SwapStyle.FadeOut)
        {
            // An opaque view loads its document while the old item leaves.
            // A page waits, so its own fade-in plays where it can be seen.
            if (view is { Kind: not WebSurfaceKind.Page })
            {
                view.AllowNavigation();
            }

            return;
        }

        // The stage has its shape now, and the host follows it again.
        _back.ClearValue(WidthProperty);
        _back.ClearValue(HeightProperty);
        if (view is null)
        {
            RevealNative(p, next, now);
            return;
        }

        var staged = view.Kind != WebSurfaceKind.Page || content.Tag is MediaStageState { DimensionsKnown: false };
        if (!staged)
        {
            content.Visibility = Visibility.Visible;
        }

        view.AllowNavigation();
        if (next.Navigated is null)
        {
            // A view whose navigation never finishes must not leave the wait
            // up forever. The clock starts with the navigation, so a browser
            // that is slow to start is not shown before it has a page.
            if (next.NavigationStarted is { } started && now >= started + RevealPlan.EscapeAfter)
            {
                RevealWeb(p, next, now, TimeSpan.Zero, ready: true, "timeout");
            }

            return;
        }

        var gate = GateOf(content);
        var answered = gate is null || gate.Value.Answer.IsCompleted;
        if (next.Gated is not { } gated)
        {
            next.Gated = gated = now;
            if (!answered)
            {
                AdvanceAfter(p, gate!.Value.Answer);
                Wake(p, gated + gate.Value.Cap);
            }
        }

        if (!answered && now < gated + gate!.Value.Cap)
        {
            return;
        }

        if (next.Paint is null)
        {
            next.Paint = WaitForDocumentPaintAsync(view, _loadCancellation?.Token ?? CancellationToken.None);
            if (!next.Paint.IsCompleted)
            {
                AdvanceAfter(p, next.Paint);
            }
        }

        if (next.Paint.IsCompleted)
        {
            var ready = next.Paint.IsCompletedSuccessfully && next.Paint.Result;
            RevealWeb(p, next, now, now - gated, ready, "navigation");
        }
    }

    /// <summary>
    /// What a navigated document still waits for before it shows, and for
    /// how long at most: a PDF for its first rail decision, a video with no
    /// probed shape for its metadata.
    /// </summary>
    private static (Task Answer, TimeSpan Cap)? GateOf(FrameworkElement content) => content.Tag switch
    {
        PdfPageRailState rail => (rail.FirstRailReady.Task, RevealPlan.RailGateCap),
        MediaStageState { DimensionsKnown: false } media => (media.DimsReady.Task, RevealPlan.MediaShapeCap),
        _ => null
    };

    /// <summary>A view's navigation reaches the load that still holds it, and no other.</summary>
    private void OnNavigationStarting(StagedWebView view)
    {
        if (_presentation is { Next: { NavigationStarted: null } next } p && ReferenceEquals(next.View, view))
        {
            next.NavigationStarted = p.Elapsed;
            Wake(p, next.NavigationStarted.Value + RevealPlan.EscapeAfter);
        }
    }

    private void OnNavigationCompleted(StagedWebView view, bool success)
    {
        if (_presentation is not { Next: { Navigated: null, Lands: null } next } p || !ReferenceEquals(next.View, view))
        {
            return;
        }

        DiagnosticLog.Write($"web navigation +{p.Since():0}ms success={success}");
        if (success)
        {
            next.Navigated = p.Elapsed;
            Advance(p);
        }
    }

    /// <summary>
    /// A staged document appears in one cut at the size it laid out at; a
    /// page has been showing all along and only the bookkeeping is left.
    /// Either way the stand-in goes once nothing can still show through: a
    /// page finishes its own dissolve over it first, an opaque view covers
    /// it at once.
    /// </summary>
    private void RevealWeb(Presentation p, Arrival next, TimeSpan now, TimeSpan waited, bool ready, string reason)
    {
        var view = next.View!;
        DiagnosticLog.Write(
            $"web gate +{p.Since():0}ms wait={waited.TotalMilliseconds:0}ms kind={view.Kind.ToString().ToLowerInvariant()} ready={ready} reason={reason}");
        MarkRevealed(p, "web");
        if (!ReferenceEquals(_presentation, p))
        {
            return;
        }

        var content = next.Content;
        if (Equals(content.Tag, ZoomableContentTag))
        {
            ShowZoomControls(view);
        }

        UpdateRotateButtons();
        if (content.Visibility != Visibility.Visible)
        {
            content.Visibility = Visibility.Visible;

            // The arrange syncs the browser's bounds in the same tick as the
            // show; they already match, so nothing refits.
            content.UpdateLayout();
        }

        next.Lands = now + (view.Kind == WebSurfaceKind.Page
            ? Motion.Reveal + TimeSpan.FromMilliseconds(60)
            : TimeSpan.FromMilliseconds(100));
        Wake(p, next.Lands.Value);
        TellContentReady(p);
    }

    /// <summary>
    /// WPF content dissolves in from the canvas or its stand-in, or a little
    /// quicker over the item it replaces. Before the window has shown, it
    /// arrives with the window in one motion instead.
    /// </summary>
    private void RevealNative(Presentation p, Arrival next, TimeSpan now)
    {
        var overItem = _front.Content is not null && StandInLayer.Visibility != Visibility.Visible;
        var withWindow = MarkRevealed(p, null);
        if (!ReferenceEquals(_presentation, p))
        {
            return;
        }

        UpdateRotateButtons();
        var content = next.Content;
        var dissolve = withWindow || !Motion.Enabled
            ? TimeSpan.Zero
            : overItem ? Motion.Browse : Motion.Reveal;
        content.Visibility = Visibility.Visible;
        Motion.Settle(content, dissolve > TimeSpan.Zero ? 0 : 1);
        if (dissolve > TimeSpan.Zero)
        {
            Motion.Fade(content, 1, dissolve);
        }

        next.Lands = now + dissolve;
        Wake(p, next.Lands.Value);
        TellContentReady(p);
        WarmBrowserForPagesNearby();
    }

    /// <summary>
    /// Tells the controller the item has revealed, once the step that
    /// revealed it is over. A folder's card reveals inside ShowPreview,
    /// before the controller has taken the new item in; told then, it would
    /// take the news for the item before.
    /// </summary>
    private void TellContentReady(Presentation p) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(_presentation, p))
            {
                ContentReady?.Invoke(this, EventArgs.Empty);
            }
        });

    /// <summary>
    /// A reveal's bookkeeping: stage hints die, the first reveal of the load
    /// is logged with its breakdown, and a window still waiting fades in.
    /// True when the window fades in with this reveal.
    /// </summary>
    private bool MarkRevealed(Presentation p, string? lane)
    {
        if (!_loadFinished)
        {
            _loadFinished = true;
            p.Shown = p.Since();
            DiagnosticLog.Write($"reveal{(lane is null ? string.Empty : " " + lane)} +{p.Shown:0}ms");
            DiagnosticLog.Write(DescribeBreakdown(p));
        }

        if (_windowAlpha != 0)
        {
            return false;
        }

        FadeWindowIn(p);
        return true;
    }

    /// <summary>
    /// The revealed content has arrived: what stood under it leaves (the
    /// item it dissolved over, and the stand-in), and the hosts trade roles,
    /// so the back one is free for the next item. Run early, it ends the
    /// dissolve where it would have ended.
    /// </summary>
    private void Land(Presentation p)
    {
        if (p.Next is not { } landed)
        {
            return;
        }

        p.Next = null;
        p.Swap = SwapStyle.Clear;
        Motion.Settle(landed.Content, 1);
        ReleaseShown();
        (_front, _back) = (_back, _front);
        Panel.SetZIndex(_front, FrontLayer);
        Panel.SetZIndex(_back, BackLayer);
    }

    private static string DescribeBreakdown(Presentation p)
    {
        static string Span(double from, double to) => from >= 0 && to >= 0 ? $"{Math.Max(0, to - from):0}ms" : "-";

        double At(long? stamp) =>
            stamp is { } at ? Stopwatch.GetElapsedTime(p.InputTimestamp, at).TotalMilliseconds : -1;

        var view = p.Next?.View;
        var navigated = p.Next?.Navigated?.TotalMilliseconds ?? -1;
        return "load breakdown"
            + $" lookup={p.Entered:0}ms"
            + $" header={Span(p.Entered, p.Headed)}"
            + $" probe={Span(p.Headed, p.Probed)}"
            + $" beat={(p.BeatEnd >= 0 ? Span(p.Probed, p.BeatEnd) : "0ms")}"
            + $" build={Span(p.Probed, p.Built)}"
            + $" engine={Span(p.Attached, At(view?.EngineReady))}"
            + $" navigation={Span(At(view?.NavigationStarted), navigated)}"
            + $" gate={Span(navigated, p.Shown)}"
            + $" reveal={p.Shown:0}ms";
    }

    /// <summary>
    /// Dev probe only: a load that was replaced must leave the stand-in of
    /// the load that replaced it alone, whatever of its own was still due.
    /// Synchronous, and restores the real load, so the probe sequence around
    /// it is undisturbed.
    /// </summary>
    internal bool ProbeStandInRace()
    {
        var real = _presentation;
        var older = new Presentation(Stopwatch.GetTimestamp(), CurrentPath);
        var newer = new Presentation(Stopwatch.GetTimestamp(), CurrentPath);
        _presentation = older;
        ShowStandIn(older, CurrentShape(), null, withWindow: true);
        _presentation = newer;
        ShowStandIn(newer, CurrentShape(), null, withWindow: true);

        // Every moment of the older load comes due at once.
        Advance(older, TimeSpan.FromHours(1));
        var owned = ReferenceEquals(StandInLayer.Content, newer.StandIn);
        var pass = owned && StandInLayer.Visibility == Visibility.Visible;
        DiagnosticLog.Write($"probe overlay-race -> owner={(owned ? "newer" : "other")} visible={StandInLayer.Visibility} pass={pass}");
        HideStandIn();
        _presentation = real;
        return pass;
    }

    // ---------------------------------------------------------------------
    // Readiness for opaque documents.
    // ---------------------------------------------------------------------

    private const string ZoomableContentTag = "zoomable";

    private static readonly TimeSpan SavedPageLayoutCap = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long the PDF viewer takes to load a document after navigation:
    /// 84 to 293 ms over the fixtures, 170 to 180 for an 81-page PDF. Its
    /// document loads while it is hidden, but it lays out and paints only on
    /// screen (hidden, clipped, at opacity 0.01 or under an overlay, it
    /// stalls; measured), so no signal says when it is done.
    /// </summary>
    private static readonly TimeSpan PdfLoadAllowance = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// The narrowest readiness signal each opaque renderer can give. The PDF
    /// viewer stays staged until its document has had time to load, counted
    /// from navigation so the wait overlaps the rail decision and the beat:
    /// shown sooner it shows its bare background, much later its page refits
    /// on screen. A saved page cannot run its own script, so a host script
    /// flushes style and layout while the view is hidden and the page shows
    /// laid out. Pages Glimpse writes need no gate: they dissolve in
    /// themselves. Every lane is bounded.
    /// </summary>
    private static async Task<bool> WaitForDocumentPaintAsync(StagedWebView webView, CancellationToken token)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return false;
        }

        if (webView.Kind == WebSurfaceKind.PdfViewer)
        {
            var loading = webView.NavigationCompletedAt is { } done
                ? PdfLoadAllowance - Stopwatch.GetElapsedTime(done)
                : PdfLoadAllowance;
            try
            {
                if (loading > TimeSpan.Zero)
                {
                    await Task.Delay(loading, token);
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        if (webView.Kind != WebSurfaceKind.SavedPage)
        {
            return true;
        }

        // One deadline governs script dispatch and the wait. A late script
        // must not get a fresh window after the host gave up on it.
        var deadlineUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            + Math.Max(1L, (long)Math.Ceiling(SavedPageLayoutCap.TotalMilliseconds));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(SavedPageLayoutCap);
        try
        {
            var layoutScript = $$"""
                (() => {
                  // ExecuteScriptAsync itself is not cancelable. If the
                  // renderer services this command after the host gave up,
                  // it reports nothing.
                  if (Date.now() > {{deadlineUnixMilliseconds}}) return false;

                  const root = document.documentElement;
                  if (!root) return false;

                  // These reads synchronously flush computed style and
                  // layout. The view itself is still hidden, so this lane
                  // never mutates page presentation just to acknowledge it.
                  const computedDisplay = getComputedStyle(root).display;
                  const body = document.body;
                  const rootBounds = root.getBoundingClientRect();
                  const bodyBounds = body ? body.getBoundingClientRect() : null;
                  const layout = [
                    rootBounds.width,
                    rootBounds.height,
                    root.scrollWidth,
                    root.scrollHeight,
                    bodyBounds ? bodyBounds.width : 0,
                    bodyBounds ? bodyBounds.height : 0,
                    body ? body.scrollWidth : 0,
                    body ? body.scrollHeight : 0
                  ];
                  return computedDisplay.length > 0
                    && layout.every(Number.isFinite);
                })();
                """;

            var layoutReady = await core.ExecuteScriptAsync(layoutScript).WaitAsync(timeout.Token);
            return string.Equals(layoutReady, "true", StringComparison.Ordinal);
        }
        catch
        {
            // A document that refuses the probe still reveals when its cap
            // runs out, fully laid out as far as it got.
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // The window. It fades via a temporary layered style (child windows
    // composite too on Win8+), then drops the layered bit so normal
    // rendering costs nothing.
    // ---------------------------------------------------------------------

    public void HidePreview()
    {
        _rotationSession?.Commit();
        _rotationSession = null;
        CloseGridView();
        StopSlideshow(restoreSize: false);
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        _pageScan?.Cancel();

        // The load ends here. What it had still arriving settles first, so
        // a show that comes before the window has gone finds one item.
        Settle(_presentation);
        _presentation = null;
        CurrentPath = null;
        _shortcutTarget = null;
        _facts = ItemFacts.Unknown;
        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        (StandInLayer.Content as StandIn)?.Stop();

        if (!IsVisible)
        {
            ReleaseStage();
            return;
        }

        var generation = ++_fadeGeneration;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || _windowAlpha == 0)
        {
            // Never faded in: nothing to fade out.
            CompleteHide();
            return;
        }

        AnimateWindowAlpha(hwnd, 0, Motion.Leave, easeIn: true, () =>
        {
            if (generation == _fadeGeneration)
            {
                CompleteHide();
            }
        });
    }

    private void CompleteHide()
    {
        // A maximize (ours or the OS's) dies with the open: the next Space
        // must always open a normal stage. This has to happen while the window
        // still exists on screen. Setting it on a hidden window moves only the
        // property, so the stage would come back maximized while the property
        // and the header button both said otherwise, and the next click on
        // that button would be swallowed reconciling the two. The window is
        // already faded out here, so the restore is not visible.
        WindowState = WindowState.Normal;
        Hide();
        ReleaseStage();
        SetBrowseControlsVisible(false);
        EnsureOpaque();
        // The user's dragged size dies with the open; content rules again.
        _itemStageSize = null;
        _recenterStage = false;
        _contentDrivenMaximize = false;
        // Insurance for the paths that reach here already hidden, where the
        // restore above raises no StateChanged to move the glyph.
        UpdateMaximizeChrome();
    }

    /// <summary>
    /// Shows the window fully transparent: WebView2 needs the hwnd, and the
    /// content gets its 120 ms to arrive with the window in one motion.
    /// </summary>
    private bool ShowTransparent()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowAlpha(hwnd, 0);
        Show();

        if (!TryConfirmNativePresentation(out var failureReason))
        {
            LastPresentationFailureReason = failureReason;
            // A rejected stage was never presented. Hide it while it is still
            // transparent so the controller's cleanup cannot briefly flash a
            // window that Windows refused to place where the user expects it.
            Hide();
            RemoveLayeredStyle(hwnd);
            _windowAlpha = 255;
            return false;
        }

        LastPresentationFailureReason = string.Empty;
        return true;
    }

    private void FadeWindowIn(Presentation p)
    {
        var hwnd = Hwnd != IntPtr.Zero ? Hwnd : new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // Zero alpha means the window still waits and nothing else. One step
        // in, it has begun to show, and no later step fades it in again.
        if (_windowAlpha == 0)
        {
            SetWindowAlpha(hwnd, 1);
        }

        DiagnosticLog.Write($"window in +{p.Since():0}ms");
        AnimateWindowAlpha(hwnd, 255, Motion.Open, easeIn: false, () =>
        {
            RemoveLayeredStyle(hwnd);
            if (!TryConfirmNativePresentation(out var completionFailure))
            {
                LastPresentationFailureReason = completionFailure;
                PresentationFailed?.Invoke(this, completionFailure);
                return;
            }

            DiagnosticLog.Write(
                $"preview presentation confirmed hwnd=0x{hwnd.ToInt64():X} owner=0x{_placementOwnerHwnd.ToInt64():X}");
        });
    }

    private bool TryConfirmNativePresentation(out string failureReason)
    {
        var hwnd = Hwnd != IntPtr.Zero ? Hwnd : new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible || !NativeMethods.IsWindowVisible(hwnd))
        {
            failureReason = "window-not-visible";
            return false;
        }

        var insertAfter = EffectiveTopmost ? new IntPtr(-1) : new IntPtr(-2);
        if (!NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE))
        {
            failureReason = $"z-order-rejected-{Marshal.GetLastWin32Error()}";
            return false;
        }

        if (EffectiveTopmost
            && _placementOwnerHwnd != IntPtr.Zero
            && NativeMethods.IsWindow(_placementOwnerHwnd)
            && IsWindowAbove(hwnd, _placementOwnerHwnd))
        {
            failureReason = "owner-remained-above-preview";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool IsWindowAbove(IntPtr window, IntPtr candidateAbove)
    {
        var cursor = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDPREV);
        for (var visited = 0; cursor != IntPtr.Zero && visited < 2048; visited++)
        {
            if (cursor == candidateAbove)
            {
                return true;
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
        }

        return false;
    }

    /// <summary>Fades the window's layered alpha from wherever it is now.</summary>
    private void AnimateWindowAlpha(IntPtr hwnd, byte to, TimeSpan duration, bool easeIn, Action onDone)
    {
        _fadeTimer?.Stop();
        var generation = _fadeGeneration;
        var from = _windowAlpha;
        if (!Motion.Enabled || from == to)
        {
            SetWindowAlpha(hwnd, to);
            onDone();
            return;
        }

        SetWindowAlpha(hwnd, from);
        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _fadeTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (generation != _fadeGeneration)
            {
                timer.Stop();

                // A browse step ended this fade with the window already
                // fully in. It is done, and its completion still has the
                // layered style to remove and the presentation to confirm.
                if (to == 255 && _windowAlpha == 255)
                {
                    onDone();
                }

                return;
            }

            var progress = stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds;
            var alpha = (byte)Math.Round(from + (to - from) * Motion.Ease(progress, easeIn));
            SetWindowAlpha(hwnd, alpha);
            if (progress >= 1)
            {
                timer.Stop();
                onDone();
            }
        };
        timer.Start();
    }

    private void EnsureOpaque()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _windowAlpha = 255;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            return;
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        RemoveLayeredStyle(hwnd);
    }

    private void SetWindowAlpha(IntPtr hwnd, byte alpha)
    {
        _windowAlpha = alpha;
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_LAYERED));
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    private static void RemoveLayeredStyle(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_LAYERED));
        }
    }

    private static bool UsesDefaultStageWithoutProbe(string path, ItemFacts facts)
    {
        if (facts.IsDirectory)
        {
            return true;
        }

        if (!facts.IsFile)
        {
            return false;
        }

        return !FileKinds.IsAudio(path)
            && !FileKinds.IsImage(path)
            && !FileKinds.IsBrowserImage(path)
            && !FileKinds.IsPhotoshop(path)
            && !FileKinds.IsMedia(path)
            && !FileKinds.IsPdf(path)
            && !FileKinds.IsEpub(path)
            && !FileKinds.IsOffice(path);
    }

    private double ElapsedSinceInput() => _presentation?.Since() ?? -1;

    /// <summary>One-shot dispatcher delay; a non-positive delay runs now.</summary>
    private static void RunAfter(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }

        // This is presentation work, not idle work. DispatcherTimer's default
        // Background priority let normal-priority icon/scan continuations hold
        // a ready folder card offscreen for hundreds of milliseconds.
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    /// <summary>
    /// Mode state that belongs to the newest content; it dies when that
    /// content leaves or newer content takes its place. The path does not:
    /// the lane tagged it before handing over, and ShowPreview owns the
    /// per-item reset.
    /// </summary>
    private void ResetContentState()
    {
        ReleaseZoomTarget();
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        SyncHeaderTools();
        _folderHover?.Reset();
        _folderHover = null;
        _activePdfRail = null;
    }
}
