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
    // asked for it. The window fades in once, the old item makes way once,
    // the stand-in waits where the content will land, and every lane
    // arrives with the same dissolve (docs/DESIGN.md, Motion and Waiting).
    // ---------------------------------------------------------------------

    private Presentation? _presentation;
    private StandIn? _standIn;
    private Presentation? _standInOwner;
    private IDisposable? _outgoingDisposable;
    private IDisposable? _incomingDisposable;
    private byte _windowAlpha = 255;

    /// <summary>What disk already knows before an item loads: a poster, and whether it is one page.</summary>
    private readonly record struct StandInFacts(BitmapSource? Poster, bool SinglePage);

    /// <summary>The window side of one load: its decisions, its clock, and what waits on its beat.</summary>
    private sealed class Presentation(RevealPlan plan, long inputTimestamp, string? contentPath)
    {
        public RevealPlan Plan { get; } = plan;

        public long InputTimestamp { get; } = inputTimestamp;

        public string? ContentPath { get; } = contentPath;

        /// <summary>What disk already knows about the item, looked up off the UI thread.</summary>
        public Task<StandInFacts>? Facts { get; set; }

        /// <summary>WPF content that arrived before the old item finished leaving.</summary>
        public (FrameworkElement Content, IDisposable? Owned)? Held { get; set; }

        /// <summary>The old item, staying put until this one dissolves over it.</summary>
        public FrameworkElement? Holding { get; set; }

        public StagedWebView? WebView { get; set; }

        // Milliseconds since the input; -1 for a step this load never took.
        public double Entered { get; set; }
        public double Headed { get; set; }
        public double Probed { get; set; }
        public double BeatEnd { get; set; } = -1;
        public double Built { get; set; } = -1;
        public double Attached { get; set; } = -1;
        public double Navigated { get; set; } = -1;
        public double Shown { get; set; } = -1;

        public double Since() => Plan.Elapsed.TotalMilliseconds;
    }

    private bool IsCurrent(Presentation p) =>
        ReferenceEquals(_presentation, p) && !p.Plan.Superseded && p.Plan.Version == _loadVersion;

    private bool Shows(Presentation p, FrameworkElement content) =>
        IsCurrent(p) && ReferenceEquals(PreviewContent.Content, content);

    /// <summary>
    /// Starts a load's presentation. Input older than two seconds is not this
    /// load's input (a file dialog selection, the welcome), so the clock then
    /// starts here.
    /// </summary>
    private Presentation BeginPresentation(long version, bool freshOpen, long inputTimestamp, long entered, string? contentPath)
    {
        _presentation?.Plan.Supersede();
        var start = inputTimestamp > 0 && Stopwatch.GetElapsedTime(inputTimestamp, entered) < TimeSpan.FromSeconds(2)
            ? inputTimestamp
            : entered;
        var plan = new RevealPlan(version, freshOpen, () => Stopwatch.GetElapsedTime(start), Motion.Enabled);
        var p = new Presentation(plan, start, contentPath)
        {
            Entered = Stopwatch.GetElapsedTime(start, entered).TotalMilliseconds
        };
        _presentation = p;
        DiagnosticLog.Write($"load start {(freshOpen ? "open" : "browse")} +{p.Entered:0}ms");
        return p;
    }

    /// <summary>
    /// The wait, on the load's clock: at 120 ms the window shows with a
    /// stand-in if the content is not ready, a sheen after a second, the
    /// reason after 1.2 s. The disk lookup starts now, off the UI thread, so
    /// it is ready when the wait is over.
    /// </summary>
    private void ScheduleWait(Presentation p)
    {
        if (p.ContentPath is { } path && StandIn.KnowsShape(ShapeOf(p)) && LooksLikeLocalDrivePath(path))
        {
            p.Facts = Task.Run(() => new StandInFacts(FindPoster(path), KnownSinglePage(path)));
        }

        RunAt(p, RevealPlan.StandInAt, () => OnWait(p, RevealPlan.StandInAt));
        RunAt(p, RevealPlan.SheenAt, () => OnWait(p, RevealPlan.SheenAt));
        RunAt(p, RevealPlan.ReasonAt, () => OnWait(p, RevealPlan.ReasonAt));
    }

    private void RunAt(Presentation p, TimeSpan at, Action action) =>
        RunAfter(at - p.Plan.Elapsed, () =>
        {
            if (IsCurrent(p))
            {
                action();
            }
        });

    /// <summary>The content's shape, once this load knows it.</summary>
    private StageFitHint? ShapeOf(Presentation p) => _stageFitVersion == p.Plan.Version ? _stageFitHint : null;

    private void OnWait(Presentation p, TimeSpan moment)
    {
        var view = p.Plan.WaitAtMoment(moment, StandIn.KnowsShape(ShapeOf(p)));
        if (view == WaitView.Nothing)
        {
            return;
        }

        // A fresh open's wait is over: the window shows what there is.
        var withWindow = p.Plan.TryShowWindow();
        ShowStandIn(p, withWindow);
        if (withWindow)
        {
            FadeWindowIn(p);
        }

        if (view.HasFlag(WaitView.Sheen))
        {
            _standIn?.StartSheen();
        }

        if (view.HasFlag(WaitView.Reason) && p.Plan.Reason is { } reason)
        {
            _standIn?.ShowReason(reason);
        }
    }

    /// <summary>The one reason line a slow wait may carry; shown now if its moment has passed.</summary>
    private void SetWaitReason(long version, string reason)
    {
        if (_presentation is not { } p || p.Plan.Version != version || !IsCurrent(p))
        {
            return;
        }

        p.Plan.Reason = reason;
        if (ReferenceEquals(_standInOwner, p)
            && p.Plan.WaitAtMoment(TimeSpan.Zero, StandIn.KnowsShape(ShapeOf(p))).HasFlag(WaitView.Reason))
        {
            _standIn?.ShowReason(reason);
        }
    }

    // ---------------------------------------------------------------------
    // Stand-in: the best image of the content while it is not ready.
    // ---------------------------------------------------------------------

    private void ShowStandIn(Presentation p, bool withWindow)
    {
        if (ReferenceEquals(_standInOwner, p))
        {
            return;
        }

        // A lookup still running when the wait ends counts as knowing nothing.
        var facts = p.Facts is { IsCompletedSuccessfully: true } lookup ? lookup.Result : default;
        var standIn = StandIn.Create(
            ShapeOf(p),
            DpiScaleFor(Hwnd),
            facts.SinglePage ? 0 : PdfPageRailWidth,
            ViewerChromeBandHeight,
            facts.Poster);
        _standIn?.Stop();
        _standIn = standIn;
        _standInOwner = p;
        StandInLayer.Content = standIn;

        // A held item steps under the stand-in, which takes its place: the
        // wait and its sheen and reason show over it, not behind it.
        var dissolve = StandInLayer.Visibility != Visibility.Visible;
        if (p.Holding is { } held && ReferenceEquals(PreviewContent.Content, held))
        {
            p.Holding = null;
            MoveShownToOutgoing();
            dissolve = true;
        }

        if (withWindow)
        {
            Motion.Settle(StandInLayer, 1);
        }
        else
        {
            // One stand-in replaces another where it stands; a new one
            // dissolves in over what shows.
            if (dissolve)
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
        _standIn?.Stop();
        _standIn = null;
        _standInOwner = null;
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

    /// <summary>
    /// A one-page document shows no rail, so its page lands further left.
    /// The PDF lane's outline counts the pages; the rail cache alone never
    /// could, since one page renders no rail.
    /// </summary>
    internal static bool KnownSinglePage(string path)
    {
        try
        {
            return RailDocumentPath(path) is { } pdf && KnownPdfOutline(pdf) is { PageCount: 1 };
        }
        catch
        {
            return false;
        }
    }

    // ---------------------------------------------------------------------
    // The beat: how the item on screen makes way for the next one.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Whether the next item needs a new stage: false when its shape is known
    /// and matches (or the stage is held), true when it differs, null when it
    /// is not known yet.
    /// </summary>
    private bool? WillStageChange(long version, string contentPath)
    {
        if (_stageFrozenForProbe || IsStageMaximized || _itemStageSize is not null)
        {
            return false;
        }

        if (_stageFitVersion != version && !UsesDefaultStageWithoutProbe(contentPath))
        {
            return null;
        }

        if (_recenterStage)
        {
            return true;
        }

        var target = ComputeStageTarget(GetWorkAreaDips(Hwnd), Hwnd);
        return Math.Abs(target.Width - Width) > StageFit.Hysteresis
            || Math.Abs(target.Height - Height) > StageFit.Hysteresis;
    }

    /// <summary>
    /// The item on screen makes way. WPF content of the same shape holds
    /// while the next item dissolves over it; anything else fades out (a
    /// browser view fades inside its page, since nothing can be drawn over
    /// it), then the stage takes the new shape in one step. When nothing of
    /// the old item shows, it leaves at once.
    /// </summary>
    private void BeginSwap(Presentation p, bool? stageChanges)
    {
        ReleaseIncoming();

        // A view the user never saw (still staging) has nothing to hand over.
        if (PreviewContent.Content is FrameworkElement unseen && !PreviewShows)
        {
            unseen.Visibility = Visibility.Collapsed;
            ClearPreviewContent();
        }

        // An item held under the next one is on screen too, until that one
        // has dissolved over it.
        var shown = PreviewContent.Content as FrameworkElement;
        var under = OutgoingContent.Content as UIElement;
        var standInShows = StandInLayer.Visibility == Visibility.Visible;
        var shownIsWeb = shown is not null && FindVisualChild<WebView2>(shown) is not null;
        var style = RevealPlan.ChooseSwap(
            outgoingVisible: shown is not null || standInShows || under is not null,
            outgoingIsWeb: shownIsWeb,
            stageChanges);
        p.Plan.Swap = style;
        p.Plan.BeginBeat();

        switch (style)
        {
            case SwapStyle.Clear:
                EndBeat(p);
                break;

            case SwapStyle.Hold:
                // It is leaving: clicks belong to the next item.
                if (shown is not null)
                {
                    shown.IsHitTestVisible = false;
                }

                p.Holding = shown;
                p.BeatEnd = p.Since();
                p.Plan.EndBeat();
                break;

            default:
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

                if (under is not null)
                {
                    Motion.Fade(under, 0, Motion.Leave, easeIn: true);
                }

                RunAfter(Motion.Leave, () => EndBeat(p));
                break;
        }

        DiagnosticLog.Write($"swap {style.ToString().ToLowerInvariant()} +{p.Since():0}ms");
    }

    /// <summary>
    /// The old item is gone; the stage takes the new shape in one step, and
    /// whatever arrived meanwhile takes its place.
    /// </summary>
    private void EndBeat(Presentation p)
    {
        if (!IsCurrent(p) || p.Plan.BeatDone)
        {
            return;
        }

        ReleaseShown();
        p.BeatEnd = p.Since();
        p.Plan.EndBeat();
        RetargetStageToFit();

        if (IncomingContent.Content is FrameworkElement staged)
        {
            var owned = _incomingDisposable;
            _incomingDisposable = null;
            IncomingContent.Content = null;
            IncomingContent.ClearValue(WidthProperty);
            IncomingContent.ClearValue(HeightProperty);
            PlaceContent(p, staged, owned);
        }
        else if (p.Held is { } held)
        {
            p.Held = null;
            PlaceContent(p, held.Content, held.Owned);
        }

        // A wait that passed its moment while the old item left shows now.
        OnWait(p, TimeSpan.Zero);
    }

    /// <summary>Everything of the old item leaves now: content, the item under it, and its stand-in.</summary>
    private void ReleaseShown()
    {
        // A browser view being disposed paints a black frame on its way out;
        // hide it in the same tick.
        if (PreviewContent.Content is UIElement shown)
        {
            shown.Visibility = Visibility.Collapsed;
        }

        ClearPreviewContent();
        ClearOutgoing();
        HideStandIn();
    }

    /// <summary>Moves held WPF content under the next item; it leaves when that one has dissolved in.</summary>
    private void MoveShownToOutgoing()
    {
        if (PreviewContent.Content is not FrameworkElement shown)
        {
            return;
        }

        ClearOutgoing();
        var owned = _currentDisposable;
        _currentDisposable = null;
        ResetContentState();
        PreviewContent.Content = null;
        OutgoingContent.Content = shown;
        _outgoingDisposable = owned;
    }

    private void ClearOutgoing()
    {
        OutgoingContent.Content = null;
        _outgoingDisposable?.Dispose();
        _outgoingDisposable = null;
    }

    private void ReleaseIncoming()
    {
        if (IncomingContent.Content is UIElement staged)
        {
            staged.Visibility = Visibility.Collapsed;
        }

        IncomingContent.Content = null;
        IncomingContent.ClearValue(WidthProperty);
        IncomingContent.ClearValue(HeightProperty);
        _incomingDisposable?.Dispose();
        _incomingDisposable = null;
    }

    /// <summary>Content the user can see: attached and not staged.</summary>
    private bool PreviewShows => PreviewContent.Content is UIElement { Visibility: Visibility.Visible };

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
    /// WPF content that arrives while the old item is still leaving waits
    /// for the beat; a browser view stages at once, hidden, so its engine
    /// starts during the beat instead of after it.
    /// </summary>
    private void SetPreviewContent(FrameworkElement content, IDisposable? disposable)
    {
        var p = _presentation;
        if (p is null || !IsCurrent(p))
        {
            disposable?.Dispose();
            return;
        }

        if (p.Built < 0)
        {
            p.Built = p.Since();
        }

        // A second presentation in a revealed load (a fallback or error
        // card) makes way like a browse step of the same shape.
        if (p.Plan.Revealed
            && p.Plan.BeatDone
            && PreviewContent.Content is FrameworkElement { Visibility: Visibility.Visible } shown
            && !ReferenceEquals(shown, content))
        {
            BeginSwap(p, stageChanges: false);
        }

        if (p.Plan.BeatDone)
        {
            PlaceContent(p, content, disposable);
            return;
        }

        if (WebViewIn(content) is not { } view)
        {
            if (p.Held is { } superseded)
            {
                superseded.Owned?.Dispose();
            }

            p.Held = (content, disposable);
            return;
        }

        ReleaseIncoming();
        var target = WillStageChange(p.Plan.Version, p.ContentPath ?? string.Empty) == true
            ? ComputeStageTarget(GetWorkAreaDips(Hwnd), Hwnd)
            : (Size?)null;
        if (target is { } window)
        {
            // Staged at the size the stage is about to take, so a document
            // lays out once.
            IncomingContent.Width = Math.Max(1, window.Width - StageFit.ChromeWidth);
            IncomingContent.Height = Math.Max(1, window.Height - StageFit.ChromeHeight);
        }

        IncomingContent.Content = content;
        _incomingDisposable = disposable;
        p.Attached = p.Since();
        WatchWebReveal(p, content, view);
        if (view.Kind != WebSurfaceKind.Page)
        {
            view.AllowNavigation();
        }

        DiagnosticLog.Write($"stage +{p.Since():0}ms (during the beat)");
    }

    private static StagedWebView? WebViewIn(FrameworkElement content) =>
        content as StagedWebView ?? FindVisualChild<StagedWebView>(content);

    private void PlaceContent(Presentation p, FrameworkElement content, IDisposable? owned)
    {
        if (PreviewContent.Content is FrameworkElement shown && !ReferenceEquals(shown, content))
        {
            if (ReferenceEquals(shown, p.Holding))
            {
                p.Holding = null;
                MoveShownToOutgoing();
            }
            else
            {
                shown.Visibility = Visibility.Collapsed;
                ClearPreviewContent();
            }
        }

        PreviewContent.Content = content;
        _currentDisposable = owned;
        if (p.Attached < 0)
        {
            p.Attached = p.Since();
        }

        SetBrowseControlsVisible(CurrentPath is not null && !IsPinned);

        // A shape learned while the content was building applies now, while
        // nothing of this item is on screen. A held item stays put.
        if (p.Plan.Swap != SwapStyle.Hold)
        {
            RetargetStageToFit();
        }

        DiagnosticLog.Write($"stage +{p.Since():0}ms");
        if (WebViewIn(content) is { } view)
        {
            AttachWeb(p, content, view);
            return;
        }

        RevealNative(p, content);
    }

    /// <summary>
    /// A page shows at once on its transparent view and dissolves itself in
    /// over whatever stands under it. A document (and a video still waiting
    /// for its shape) stages hidden and appears in one cut.
    /// </summary>
    private void AttachWeb(Presentation p, FrameworkElement content, StagedWebView view)
    {
        p.WebView = view;
        var staged = view.Kind != WebSurfaceKind.Page || content.Tag is MediaStageState { DimensionsKnown: false };
        content.Visibility = staged ? Visibility.Hidden : Visibility.Visible;
        if (!view.RevealWatched)
        {
            WatchWebReveal(p, content, view);
        }

        view.AllowNavigation();
    }

    private void WatchWebReveal(Presentation p, FrameworkElement content, StagedWebView view)
    {
        view.RevealWatched = true;
        p.WebView = view;
        var token = _loadCancellation?.Token ?? CancellationToken.None;
        var ticket = new RevealTicket(() =>
            IsCurrent(p)
            && (ReferenceEquals(PreviewContent.Content, content) || ReferenceEquals(IncomingContent.Content, content)));

        view.NavigationCompleted += async (_, args) =>
        {
            if (ticket.Taken || !IsCurrent(p))
            {
                return;
            }

            p.Navigated = p.Since();
            DiagnosticLog.Write($"web navigation +{p.Navigated:0}ms success={args.IsSuccess}");
            if (args.IsSuccess)
            {
                await RevealWebAsync(p, content, view, ticket, gated: true, "navigation", token);
            }
        };

        // A view that never finishes must not leave the wait up forever.
        RunAfter(RevealPlan.EscapeAfter, () =>
            _ = RevealWebAsync(p, content, view, ticket, gated: false, "timeout", token));
    }

    /// <summary>
    /// Navigation and the escape hatch share one owner: whoever starts first
    /// reveals, once. A view staged during the beat waits for the beat; a
    /// document then waits for its gates (the PDF rail decision and load
    /// allowance, a saved page's layout), each capped.
    /// </summary>
    private async Task RevealWebAsync(
        Presentation p,
        FrameworkElement content,
        StagedWebView view,
        RevealTicket ticket,
        bool gated,
        string reason,
        CancellationToken token)
    {
        if (!ticket.TryTake())
        {
            return;
        }

        await p.Plan.BeatEnded;
        if (!Shows(p, content))
        {
            return;
        }

        var gateClock = Stopwatch.StartNew();
        var ready = true;
        if (gated)
        {
            if (content.Tag is PdfPageRailState rail)
            {
                await RevealPlan.WaitCappedAsync(rail.FirstRailReady.Task, RevealPlan.RailGateCap, Task.Delay);
            }
            else if (content.Tag is MediaStageState { DimensionsKnown: false } media)
            {
                await RevealPlan.WaitCappedAsync(media.DimsReady.Task, RevealPlan.MediaShapeCap, Task.Delay);
            }

            if (!Shows(p, content))
            {
                return;
            }

            ready = await WaitForDocumentPaintAsync(view, token);
            if (!Shows(p, content))
            {
                return;
            }
        }

        DiagnosticLog.Write(
            $"web gate +{p.Since():0}ms wait={gateClock.Elapsed.TotalMilliseconds:0}ms kind={view.Kind.ToString().ToLowerInvariant()} ready={ready} reason={reason}");
        RevealWeb(p, content, view);
    }

    /// <summary>
    /// A staged document appears in one cut at the size it laid out at; a
    /// page has been showing all along and only the bookkeeping is left.
    /// Either way the stand-in goes once nothing can still show through.
    /// </summary>
    private void RevealWeb(Presentation p, FrameworkElement content, StagedWebView view)
    {
        MarkRevealed(p, "web");
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

        // A page finishes its own dissolve over the stand-in; an opaque view
        // covers it at once.
        var linger = view.Kind == WebSurfaceKind.Page
            ? Motion.Reveal + TimeSpan.FromMilliseconds(60)
            : TimeSpan.FromMilliseconds(100);
        RunAfter(linger, () => FinishReveal(p, content));
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// What stood under revealed content leaves: its stand-in and the item it
    /// dissolved over. Only while that content still shows, so a reveal that
    /// finishes after the user moved on cannot take down the next stand-in.
    /// </summary>
    private void FinishReveal(Presentation p, FrameworkElement content)
    {
        if (!Shows(p, content))
        {
            return;
        }

        HideStandIn();
        ClearOutgoing();
    }

    /// <summary>
    /// Dev probe only: a load's reveal finishing after the next load showed
    /// its stand-in must leave that stand-in up. Synchronous, and restores
    /// the real load, so the probe sequence around it is undisturbed.
    /// </summary>
    internal bool ProbeStandInRace()
    {
        var real = _presentation;
        var content = PreviewContent.Content as FrameworkElement ?? new Grid();
        var older = new Presentation(new RevealPlan(_loadVersion, false, () => TimeSpan.Zero, Motion.Enabled), Stopwatch.GetTimestamp(), CurrentPath);
        var newer = new Presentation(new RevealPlan(_loadVersion, false, () => TimeSpan.Zero, Motion.Enabled), Stopwatch.GetTimestamp(), CurrentPath);
        _presentation = older;
        ShowStandIn(older, withWindow: true);
        older.Plan.Supersede();
        _presentation = newer;
        ShowStandIn(newer, withWindow: true);
        FinishReveal(older, content);
        var pass = ReferenceEquals(_standInOwner, newer) && StandInLayer.Visibility == Visibility.Visible;
        DiagnosticLog.Write($"probe overlay-race -> owner={(ReferenceEquals(_standInOwner, newer) ? "newer" : "other")} visible={StandInLayer.Visibility} pass={pass}");
        HideStandIn();
        _presentation = real;
        return pass;
    }

    /// <summary>
    /// WPF content dissolves in over its stand-in, or over the item it is
    /// replacing. Before the window has shown, it arrives with the window in
    /// one motion instead.
    /// </summary>
    private void RevealNative(Presentation p, FrameworkElement content)
    {
        var holding = OutgoingContent.Content is not null;
        var withWindow = MarkRevealed(p, null);
        UpdateRotateButtons();
        content.Visibility = Visibility.Visible;
        if (withWindow)
        {
            Motion.Settle(content, 1);
            FinishReveal(p, content);
        }
        else
        {
            Motion.Settle(content, 0);
            Motion.Fade(content, 1, holding ? Motion.Browse : Motion.Reveal, completed: () => FinishReveal(p, content));
        }

        ContentReady?.Invoke(this, EventArgs.Empty);
        WarmBrowserForPagesNearby();
    }

    /// <summary>
    /// A reveal's bookkeeping: stage hints die, the first reveal of the load
    /// is logged with its breakdown, and the window fades in if it has not.
    /// True when the window fades in with this reveal.
    /// </summary>
    private bool MarkRevealed(Presentation p, string? lane)
    {
        _loadFinished = true;
        if (p.Plan.TryReveal())
        {
            p.Shown = p.Since();
            DiagnosticLog.Write($"reveal{(lane is null ? string.Empty : " " + lane)} +{p.Shown:0}ms");
            DiagnosticLog.Write(DescribeBreakdown(p));
        }

        if (!p.Plan.TryShowWindow())
        {
            return false;
        }

        FadeWindowIn(p);
        return true;
    }

    private static string DescribeBreakdown(Presentation p)
    {
        static string Span(double from, double to) => from >= 0 && to >= 0 ? $"{Math.Max(0, to - from):0}ms" : "-";

        var view = p.WebView;
        var engine = view?.EngineReady is { } ready ? Stopwatch.GetElapsedTime(p.InputTimestamp, ready).TotalMilliseconds : -1;
        var navigation = view?.NavigationStarted is { } started ? Stopwatch.GetElapsedTime(p.InputTimestamp, started).TotalMilliseconds : -1;
        return "load breakdown"
            + $" lookup={p.Entered:0}ms"
            + $" header={Span(p.Entered, p.Headed)}"
            + $" probe={Span(p.Headed, p.Probed)}"
            + $" beat={(p.BeatEnd >= 0 ? Span(p.Probed, p.BeatEnd) : "0ms")}"
            + $" build={Span(p.Probed, p.Built)}"
            + $" engine={Span(p.Attached, engine)}"
            + $" navigation={Span(navigation, p.Navigated)}"
            + $" gate={Span(p.Navigated >= 0 ? p.Navigated : -1, p.Shown)}"
            + $" reveal={p.Shown:0}ms";
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
        _presentation?.Plan.Supersede();
        CurrentPath = null;
        _shortcutTarget = null;
        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        _standIn?.Stop();

        if (!IsVisible)
        {
            ReleaseShown();
            ReleaseIncoming();
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
        ReleaseShown();
        ReleaseIncoming();
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

    private static bool UsesDefaultStageWithoutProbe(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (!File.Exists(path))
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

    /// <summary>Mode state that belongs to the item on screen; it dies with that item.</summary>
    private void ResetContentState()
    {
        ReleaseZoomTarget();
        // Mode state dies with the content; the path does NOT - the staging
        // beat clears the outgoing content AFTER the new item's lane tagged
        // it, and ShowPreview owns the per-item reset.
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        SyncHeaderTools();
        _folderHover?.Reset();
        _folderHover = null;
        _activePdfRail = null;
    }

    private void ClearPreviewContent()
    {
        ResetContentState();
        PreviewContent.Content = null;
        _currentDisposable?.Dispose();
        _currentDisposable = null;
    }
}
