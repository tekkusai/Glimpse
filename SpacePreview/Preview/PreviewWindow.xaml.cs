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

internal enum PreviewBrowseDirection
{
    Previous = -1,
    Next = 1
}

public partial class PreviewWindow : Window
{
    private const double HeaderHeight = 52;
    // The embedded PDF viewer's own toolbar strip; clicks there mean typing.
    private const double ViewerChromeBandHeight = 40;

    // The stage never gets small enough to stop being functional and
    // beautiful: header chrome intact, content with room to breathe.
    internal const double StageMinWidth = 640;
    internal const double StageMinHeight = 480;
    private const double HeaderOpenMaxWidth = 190;
    private const double HeaderOpenMinWidth = 78;
    private const double HeaderOpenMenuMinWidth = 104;
    private const double HeaderOpenLabelMaxWidth = 162;
    private const double HeaderOpenLabelWithMenuMaxWidth = 136;
    private const double FileCardWidth = 600;
    private const double FileCardVisualSlotWidth = 560;
    private const double FileCardVisualSlotHeight = 220;
    private const double FileCardTitleSlotHeight = 58;
    private const double FileCardInfoSlotHeight = 124;
    private const double FileCardFooterSlotHeight = 168;
    private const double FileCardIconSide = 148;
    private const double FolderArtworkSlotWidth = 280;
    private const double FolderArtworkSlotHeight = 220;
    private const string HevcVideoExtensionStoreUri = "ms-windows-store://pdp/?ProductId=9NMZLZ57R3T7";
    private const string Av1VideoExtensionStoreUri = "ms-windows-store://pdp/?ProductId=9MVZQVXJBQ9V";
    private const string HeifImageExtensionStoreUri = "ms-windows-store://pdp/?ProductId=9PMMSR1CGPWG";
    private const double PdfPageRailWidth = 132;
    private const double PdfPageRailCollapsedWidth = 32;
    private const int PdfPageRailInitialPages = 10;
    private static readonly TimeSpan PdfPageAutomationTimeout = TimeSpan.FromSeconds(2);
    // Wider than the thumbnail rail: chapter titles are words, not pictures.
    private const double EpubChapterRailWidth = 168;
    private const int PdfPageRailMaxPages = 120;
    private const int MaxImageDecodePixels = 4096;
    private const int LargeImageFirstFramePixels = 2048;
    private static readonly TimeSpan LargeImageFullDecodeDelay = TimeSpan.FromMilliseconds(420);
    private const string PinGlyph = "\uE718";
    private const string UnpinGlyph = "\uE77A";
    private const string MaximizeGlyph = "\uE922";
    private const string RestoreGlyph = "\uE923";
    private const string PlayGlyph = "\uE768";
    private const string StopSlideshowGlyph = "\uE71A";

    private IDisposable? _currentDisposable;
    private IDisposable? _nextDisposable;
    private CancellationTokenSource? _loadCancellation;
    private long _loadVersion;
    private readonly SemaphoreSlim _shellIconGate = new(1, 1);
    private readonly SemaphoreSlim _folderSizeScanGate = new(1, 1);
    private readonly SemaphoreSlim _imageDecodeGate = new(1, 1);
    private readonly RecentImageDecodeCache _imageDecodeCache = new();

    // Content-fit sizing state. A border-drag holds its size for the rest
    // of this open and then dies with it; the hint belongs to one load
    // version and is ignored once that item has revealed.
    private Size? _itemStageSize;
    private bool _recenterStage;
    private StageFitHint? _stageFitHint;
    private long _stageFitVersion;

    private Border? _fillSignPill;
    private FillSignSurface? _fillSignSurface;
    private FrameworkElement? _fillSignParkedContent;
    private Action? _deferredWebFailure;
    private string? _fillSignPath;
    private ThemePalette? _appliedResources;
    private DateTime _flyoutClosedAt;
    private string? _shortcutTarget;
    private ContextMenu? _contextMenu;
    private readonly object _popupSync = new();
    private readonly Dictionary<ContextMenu, IntPtr> _ownedPopupHandles = [];
    private readonly HashSet<uint> _webViewProcessIds = [];
    private long _webContextMenuArmedUntilTicks;
    private long _suppressOutsideDismissUntilTicks;
    private bool _userResizingStage;
    private bool _stageFrozenForProbe;
    private DateTime _swapStartedAt = DateTime.MinValue;
    private DateTime _loadStartedAt = DateTime.MinValue;
    private WebView2? _zoomWebView;
    private bool _loadFinished;
    private bool _escapeYieldsToContent;
    private bool _lastContentClickInViewerChrome;
    private bool _userEngagedContent;
    private bool _contentDrivenMaximize;
    private int _fadeGeneration;
    private long _loadingOverlayGeneration;
    private DispatcherTimer? _fadeTimer;
    private ImageRotationSession? _rotationSession;
    private FolderHoverThumbnail? _folderHover;
    private PdfPageRailState? _activePdfRail;
    private EpubRailState? _activeEpubRail;
    private IReadOnlyList<string>? _multiSelectionPaths;
    private int _multiSelectionIndex;
    private SelectionGridView? _gridView;
    private DispatcherTimer? _slideshowTimer;
    private bool _slideshowRestoresSize;
    private bool _isFileDialogMode;
    private IntPtr _placementOwnerHwnd;

    public PreviewWindow()
    {
        InitializeComponent();
        ApplyTopmost();
        Theme.Refresh();
        Theme.ApplyResources(this);
        _appliedResources = Theme.Current;
        BuildContextMenu();
        ZoomInButton.Click += (_, _) => AdjustZoom(1);
        ZoomOutButton.Click += (_, _) => AdjustZoom(-1);
        ZoomResetButton.Click += (_, _) => ResetZoom();
        StateChanged += (_, _) => SyncNativeWindowState();
        SourceInitialized += (_, _) => ApplyWindowNativeStyle();

        // One line per click: where it landed and who owned the capture.
        // This is the first question for any "clicks stopped working" report.
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            var position = e.GetPosition(this);
            DiagnosticLog.Write(
                $"click {position.X:0},{position.Y:0} source={e.OriginalSource?.GetType().Name} captured={Mouse.Captured?.GetType().Name ?? "none"}");
        };
    }

    public event EventHandler? DismissRequested;

    /// <summary>The pin button asking the controller to change this window's pinned state.</summary>
    public event EventHandler? PinToggleRequested;

    /// <summary>A pinned window's X button: close outright, not just hide.</summary>
    public event EventHandler? CloseRequested;

    internal event EventHandler<PreviewBrowseDirection>? BrowseRequested;

    /// <summary>The index sheet or slideshow asking to jump the preview to a set member.</summary>
    internal event EventHandler<int>? SelectionItemRequested;

    /// <summary>The slideshow timer asking for the next set member, wrapping at the end.</summary>
    internal event EventHandler? SlideshowAdvanceRequested;

    /// <summary>The visible preview has finished its own load and reveal.</summary>
    internal event EventHandler? ContentReady;

    /// <summary>
    /// The WPF tree can finish rendering while Windows still refuses the
    /// non-activating stage its expected z-order. Report that separately so
    /// the controller never leaves a logical "visible" ghost behind.
    /// </summary>
    internal event Action<PreviewWindow, string>? PresentationFailed;

    internal string LastPresentationFailureReason { get; private set; } = string.Empty;

    /// <summary>
    /// A pinned preview stays put: dismissal gestures (Space, Esc, outside
    /// clicks, opening the file) leave it alone. Only its X button or
    /// unpinning releases it.
    /// </summary>
    public bool IsPinned { get; private set; }

    // The one bit the pinned-only Always on top toggle owns. Fresh default
    // (on) at every pin-state change; never persisted.
    private bool _pinnedAlwaysOnTop = true;
    private bool _topmostSuppressed;

    public string? CurrentPath { get; private set; }

    /// <summary>
    /// Ctrl+F reached the browser while it owned the keyboard. Only the PDF
    /// viewer actually opens a find bar for it (plain pages have no find UI
    /// in WebView2), and while that bar is up the next Esc is handled by it -
    /// closing the bar, not the preview. One Esc, one meaning.
    /// </summary>
    public void NoteFindShortcut()
    {
        if (FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
            && IsPdfViewerSource(source))
        {
            _escapeYieldsToContent = true;
        }
    }

    /// <summary>True once per armed find bar: this Esc is the content's.</summary>
    public bool ConsumeEscapeYield()
    {
        if (!_escapeYieldsToContent)
        {
            return false;
        }

        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        return true;
    }

    /// <summary>
    /// While the content's find bar is open, typed keys are the user's words:
    /// Space is a literal space and Enter is "next match". The hook checks
    /// this before claiming either key for its preview meaning.
    /// </summary>
    public bool IsEscapeYieldArmed => _escapeYieldsToContent;

    /// <summary>
    /// Enter's one promise is "open in the default app". The single honest
    /// exception besides the find bar is the PDF viewer's own toolbar: a
    /// user who just clicked its page box and typed a number means "jump",
    /// not "open". A click anywhere in the document body keeps the promise;
    /// only a click in the viewer's chrome band yields the key.
    /// </summary>
    public bool ConsumeEnterYieldToContent()
    {
        if (!_lastContentClickInViewerChrome)
        {
            return false;
        }

        if (!ContentOwnsKeyboard())
        {
            _lastContentClickInViewerChrome = false;
            return false;
        }

        var yield = FindVisualChild<WebView2>(PreviewContent) is { CoreWebView2.Source: { } source }
            && IsPdfViewerSource(source);
        _lastContentClickInViewerChrome = false;
        return yield;
    }

    /// <summary>The viewer's Source may carry open parameters (#view=FitH).</summary>
    private static bool IsPdfViewerSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
            : source.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Media by what is actually playing: a shortcut to a video counts.</summary>
    public bool IsMediaPreview =>
        CurrentPath is not null && FileKinds.IsMedia(_shortcutTarget ?? CurrentPath);

    private bool IsCurrentContentPath(string path) =>
        string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(_shortcutTarget, path, StringComparison.OrdinalIgnoreCase);

    public bool HasHeaderZoom => _zoomWebView is not null;

    /// <summary>
    /// The only Topmost derivation. Transient previews always float (the
    /// quick-look contract); a pinned window floats while its Always on top
    /// toggle is on; fullscreen suppression outranks both.
    /// </summary>
    private bool EffectiveTopmost => (!IsPinned || _pinnedAlwaysOnTop) && !_topmostSuppressed;

    /// <summary>
    /// A pinned window the user took off the top layer is an ordinary window,
    /// and an ordinary window must be recoverable from the taskbar and
    /// Alt+Tab when buried. Floating and transient previews stay out of both.
    /// Fullscreen suppression is temporary, not a choice, so it never adds an
    /// entry.
    /// </summary>
    private bool TaskbarReachable => IsPinned && !_pinnedAlwaysOnTop;

    private void ApplyTopmost()
    {
        Topmost = EffectiveTopmost;
        SyncShellPresence();
    }

    // Last taskbar-tab state actually handed to the shell.
    private bool _taskbarTabShown;

    /// <summary>
    /// Shell presence without touching WPF's ShowInTaskbar at runtime:
    /// flipping that property hides and re-shows the frame, a visible flash
    /// on a live window. The extended style bits govern Alt+Tab (TOOLWINDOW
    /// excludes, APPWINDOW forces inclusion past the hidden owner WPF gives
    /// non-taskbar windows), and ITaskbarList moves the taskbar button.
    /// </summary>
    private void SyncShellPresence()
    {
        if (Hwnd == IntPtr.Zero)
        {
            return;
        }

        var reachable = TaskbarReachable;
        var exStyle = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var desired = reachable
            ? (exStyle & ~NativeMethods.WS_EX_TOOLWINDOW) | NativeMethods.WS_EX_APPWINDOW
            : (exStyle | NativeMethods.WS_EX_TOOLWINDOW) & ~NativeMethods.WS_EX_APPWINDOW;
        if (desired != exStyle)
        {
            NativeMethods.SetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(desired));
        }

        if (reachable == _taskbarTabShown)
        {
            return;
        }

        try
        {
            NativeMethods.SetTaskbarTab(Hwnd, reachable);
            _taskbarTabShown = reachable;
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write($"taskbar tab sync failed {exception.Message}");
        }
    }

    /// <summary>Probe: the shell-presence state matches the derivation.</summary>
    private bool ShellPresenceApplied(bool reachable)
    {
        if (TaskbarReachable != reachable)
        {
            return false;
        }

        if (Hwnd == IntPtr.Zero)
        {
            return true;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(Hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        var toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
        var appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
        return toolWindow == !reachable && appWindow == reachable && _taskbarTabShown == reachable;
    }

    public void SetTopmostSuppressed(bool suppressed)
    {
        _topmostSuppressed = suppressed;
        ApplyTopmost();
    }

    /// <summary>
    /// Space over a media preview always means play/pause. The host toggles
    /// it directly (the hook sees Space before the browser does), so the
    /// page never needs keyboard-focus games and the player's controls keep
    /// their own focus.
    /// </summary>
    public bool TryToggleMediaPlayback()
    {
        if (!IsMediaPreview
            || FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return false;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "(m => { if (!m) return; if (window.glimpseToggleMedia) { window.glimpseToggleMedia(); return; } if (m.paused) m.play().catch(e => { try { window.chrome.webview.postMessage({ error: m.error ? m.error.code : 'play', reason: e && e.name ? e.name : 'play' }); } catch {} }); else m.pause(); })(document.getElementById('media'))");
        }
        catch
        {
            // A torn-down view cannot toggle; Space then simply does nothing.
        }

        return true;
    }

    /// <summary>
    /// Media arrows go through the host, never the native controls (whose
    /// focus-walking is what "stuck" the keys). The active preview yields
    /// only up/down (volume) - left/right stay browsing. A pinned player
    /// browses nothing, so it gets keyboard seek on left/right too.
    /// </summary>
    public bool TryNudgeMediaFromArrow(int vk)
    {
        if (!IsMediaPreview || !(_userEngagedContent || IsPinned))
        {
            return false;
        }

        if (!IsPinned && vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT)
        {
            return false;
        }

        var nudge = vk switch
        {
            NativeMethods.VK_LEFT => "if(m&&isFinite(m.duration))m.currentTime=Math.max(0,Math.min(m.duration,m.currentTime-5))",
            NativeMethods.VK_RIGHT => "if(m&&isFinite(m.duration))m.currentTime=Math.max(0,Math.min(m.duration,m.currentTime+5))",
            NativeMethods.VK_UP => "if(m)m.volume=Math.max(0,Math.min(1,m.volume+0.05))",
            NativeMethods.VK_DOWN => "if(m)m.volume=Math.max(0,Math.min(1,m.volume-0.05))",
            _ => null
        };

        if (nudge is null || FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return false;
        }

        try
        {
            _ = core.ExecuteScriptAsync($"(m => {{ {nudge}; }})(document.getElementById('media'))");
        }
        catch
        {
            // A torn-down view cannot nudge; the key then does nothing.
        }

        return true;
    }

    public bool TryAdjustZoomFromShortcut(int vk)
    {
        if (_zoomWebView is null)
        {
            return false;
        }

        if (IsZoomInKey(vk))
        {
            AdjustZoom(1);
            return true;
        }

        if (IsZoomOutKey(vk))
        {
            AdjustZoom(-1);
            return true;
        }

        if (vk is NativeMethods.VK_0 or NativeMethods.VK_NUMPAD0)
        {
            ResetZoom();
            return true;
        }

        return false;
    }

    public bool ShouldBrowseWithPreviewArrowKeys(bool vertical)
    {
        if (IsPinned || CurrentPath is null)
        {
            return false;
        }

        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase)
        {
            return false;
        }

        // Left/right always browse - the main browsing rule. Media
        // seeking is handled by the scrubber; keys that mean previous/next
        // everywhere must not change meaning on a video. Only up/down yield
        // to content the user clicked into (document scroll, player volume),
        // and engagement means the user clicked the content area - a
        // browser view that grabbed focus on its own does not count.
        if (vertical && _userEngagedContent && ContentOwnsKeyboard())
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when keyboard focus sits in a child hwnd of this window (the
    /// browser view) rather than in the WPF chrome. WPF cannot answer this
    /// reliably for a windowed WebView2, so ask Win32 directly.
    /// </summary>
    private bool ContentOwnsKeyboard()
    {
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(0, ref info)
            || info.hwndFocus == IntPtr.Zero
            || info.hwndFocus == Hwnd)
        {
            return false;
        }

        return NativeMethods.GetAncestor(info.hwndFocus, NativeMethods.GA_ROOT) == Hwnd;
    }

    public bool IsOutsideDismissSuppressed =>
        Interlocked.Read(ref _suppressOutsideDismissUntilTicks) > DateTime.UtcNow.Ticks;

    private void SuppressOutsideDismissFor(TimeSpan duration) =>
        Interlocked.Exchange(ref _suppressOutsideDismissUntilTicks, DateTime.UtcNow.Add(duration).Ticks);

    /// <summary>Cached for the keyboard hook thread; set once on SourceInitialized.</summary>
    public IntPtr Hwnd { get; private set; }

    public bool ShowPreview(
        string path,
        IntPtr explorerHwnd,
        int selectedCount = 1,
        bool fileDialogMode = false)
    {
        _loadStartedAt = DateTime.UtcNow;
        _isFileDialogMode = fileDialogMode;
        _placementOwnerHwnd = fileDialogMode ? explorerHwnd : IntPtr.Zero;
        RootChrome.ContextMenu = fileDialogMode ? null : _contextMenu;
        // The outgoing image writes its pending rotation before anything
        // else touches state; the new file must never inherit it.
        _rotationSession?.Commit();
        _rotationSession = null;
        HideZoomControls();
        HideContentActions();

        var version = Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        var token = _loadCancellation.Token;
        _loadFinished = false;
        RestoreFromNativeMinimize();

        // Compare against the palette these resources were actually built
        // from: the tray may already have refreshed Theme.Current, making
        // Refresh() report "no change" while this window is still stale.
        Theme.Refresh();
        if (!ReferenceEquals(_appliedResources, Theme.Current))
        {
            _appliedResources = Theme.Current;
            Theme.ApplyResources(this);
            if (_contextMenu is not null)
            {
                Theme.ApplyResources(_contextMenu);
            }

            ApplyThemeToDwm();
        }

        CurrentPath = path;
        // Previewing a shortcut should preview what it points at, like Quick
        // Look does for aliases. Opening still goes through the .lnk itself.
        _shortcutTarget = FileKinds.IsShortcut(path) ? TryResolveShortcutTarget(path) : null;
        var contentPath = _shortcutTarget ?? path;

        FileNameText.Text = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(FileNameText.Text))
        {
            FileNameText.Text = path;
        }

        ApplyHeaderSubtitle(path, _shortcutTarget, selectedCount);

        var secondaryActions = new List<(string Label, Action Action)>();
        if (FileKinds.IsExecutableLike(contentPath))
        {
            var exePath = contentPath;
            secondaryActions.Add(("Run as administrator", () => RunAsAdministrator(exePath)));
        }

        if (Directory.Exists(contentPath))
        {
            var folderPath = contentPath;
            secondaryActions.Add(("Open in Terminal", () => OpenInTerminal(folderPath)));
        }

        if (_shortcutTarget is not null)
        {
            var target = _shortcutTarget;
            secondaryActions.Add(("Show target in Explorer", () => RevealInExplorer(target)));
        }

        if (File.Exists(contentPath) && LooksLikeLocalDrivePath(contentPath))
        {
            var sharePath = contentPath;
            secondaryActions.Add(("Share", () => SharePath(sharePath)));
        }

        // Not every previewed item was reached through an Explorer selection
        // (file dialogs, folder browsing), so every preview answers "where is
        // this file".
        if (File.Exists(path) || Directory.Exists(path))
        {
            var revealPath = path;
            secondaryActions.Add(("Show in Explorer", () => RevealInExplorer(revealPath)));
        }

        UpdateOpenControl(GetOpenLabel(contentPath), secondaryActions);
        OpenControl.Visibility = fileDialogMode ? Visibility.Collapsed : Visibility.Visible;
        PinButton.Visibility = fileDialogMode ? Visibility.Collapsed : Visibility.Visible;
        SetBrowseControlsVisible(!fileDialogMode && !IsPinned);
        // The next icon owns this slot. Keeping the lane reserved prevents a
        // late shell lookup from shifting the title, while clearing the source
        // prevents the previous item's glyph from impersonating the new one.
        FileIconImage.Source = null;
        FileIconImage.Visibility = Visibility.Hidden;
        _ = UpdateFileIconAsync(path, version, token);

        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        _fillSignPath = null;
        UpdateFillSignPill();

        // A dragged size is local intent: it lives exactly as long as the
        // item under it. The next item's content rules again, and it
        // retakes the middle of the screen: the resize dragged the window
        // off its anchored center, so that center is not worth keeping.
        if (_itemStageSize is not null)
        {
            _itemStageSize = null;
            _recenterStage = true;
        }

        // The shapes knowable up front, before geometry is placed. Slower
        // shapes (media metadata, conversions) hint later and are applied
        // during the blank beat.
        TrySetIntrinsicStageFit(contentPath, version);
        var hasInitialStageFitHint = _stageFitVersion == version;

        var freshOpen = !IsVisible;
        var presented = true;
        _swapStartedAt = freshOpen ? DateTime.MinValue : DateTime.UtcNow;
        if (!freshOpen)
        {
            var defaultStageWithoutProbe = !hasInitialStageFitHint && UsesDefaultStageWithoutProbe(contentPath);
            var outgoingIsWeb = FindVisualChild<WebView2>(PreviewContent) is not null;

            var stageMoved = defaultStageWithoutProbe && !outgoingIsWeb && RetargetStageToFit();
            ShowContentSwapScrim(instant: stageMoved);
            FadeOutWebContent();
            CollapseOutgoingContentSoon();
            if (defaultStageWithoutProbe && outgoingIsWeb)
            {
                RetargetDefaultStageDuringBlankBeat(version);
            }
        }

        // A maximize the CONTENT requested (video fullscreen) releases when
        // the content goes away; only the user's own F11 survives a swap.
        if (_contentDrivenMaximize && IsStageMaximized)
        {
            ToggleMaximize();
            _contentDrivenMaximize = false;
        }

        // The stage takes the content's shape. A fresh open places it here;
        // while browsing, each item retakes the stage during the blank beat
        // of its swap, never while anything is on screen.
        if (freshOpen && !IsStageMaximized)
        {
            ApplyStageGeometry(explorerHwnd);
        }

        _fadeGeneration++;
        if (freshOpen)
        {
            ClearPreviewContent();
            HideLoadingOverlay();
            presented = ShowWithEntrance(useScale: ShouldScaleEntrance());
        }
        else
        {
            // A rapid hide/show race can leave a partially faded window behind.
            EnsureOpaque();

            // Reassert topmost without activation. This matches Quick Look's "above Finder" feel.
            Topmost = false;
            ApplyTopmost();
            presented = TryConfirmNativePresentation(out var failureReason);
            LastPresentationFailureReason = presented ? string.Empty : failureReason;
        }

        // A first office conversion builds LibreOffice's profile (~15s). It is
        // the wait most likely to feel broken, so name it up front and
        // make clear that it is one-time.
        if (OfficeOpenXmlPreviewLoader.IsSupported(contentPath) && OfficeDocumentPdfConverter.IsFirstRunPending)
        {
            DiagnosticLog.Write("first-run conversion message shown");
            _ = ShowLoadingOverlayLaterAsync(version, 120, token, FirstOfficeRunMessage);
        }
        else
        {
            _ = ShowLoadingOverlayLaterAsync(version, freshOpen ? 240 : 420, token);

            // Prediction is not enough: a large document takes its time even
            // on a warm engine. If a conversion is still running, say so -
            // the cache makes the message true per file. The session's first
            // conversion pays the engine's process start, so it gets named
            // almost immediately instead of spinning in silence.
            if (OfficeOpenXmlPreviewLoader.IsSupported(contentPath))
            {
                var messageDelay = OfficeDocumentPdfConverter.HasConvertedThisSession ? 2500 : 700;
                _ = ShowLoadingOverlayLaterAsync(version, messageDelay, token, SlowConversionMessage);
            }
        }

        _ = LoadPreviewAsync(path, contentPath, version, token);
        return presented;
    }

    private static readonly (string Title, string Subtitle) FirstOfficeRunMessage =
        ("Preparing document previews", "Just this once. After this, they open instantly.");

    private static readonly (string Title, string Subtitle) SlowConversionMessage =
        ("Preparing this document", "Next time it opens instantly.");

    /// <summary>
    /// First-run introduction: the app shows a short built-in introduction. The
    /// preview window opens once after install, teaches the single gesture,
    /// and is dismissed with that same gesture.
    /// </summary>
    public bool ShowWelcome()
    {
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        _isFileDialogMode = false;
        _placementOwnerHwnd = IntPtr.Zero;
        RootChrome.ContextMenu = _contextMenu;
        CurrentPath = null;
        _shortcutTarget = null;
        SetBrowseControlsVisible(false);

        Theme.Refresh();
        if (!ReferenceEquals(_appliedResources, Theme.Current))
        {
            _appliedResources = Theme.Current;
            Theme.ApplyResources(this);
            if (_contextMenu is not null)
            {
                Theme.ApplyResources(_contextMenu);
            }

            ApplyThemeToDwm();
        }

        FileNameText.Text = Branding.Name;
        SubtitleText.Text = Branding.Tagline;
        FileIconImage.Source = null;
        FileIconImage.Visibility = Visibility.Hidden;
        OpenControl.Content = null;
        OpenControl.Visibility = Visibility.Visible;
        PinButton.Visibility = Visibility.Collapsed;

        // The welcome uses the same stage geometry as normal previews.
        ApplyStageGeometry(IntPtr.Zero);

        ClearPreviewContent();
        HideContentSwapScrim();
        HideLoadingOverlay();
        SetPreviewContent(BuildWelcomeContent(), null, animate: false);
        return ShowWithEntrance();
    }

    private static FrameworkElement BuildWelcomeContent()
    {
        var palette = Theme.Current;

        var mark = BrandMark.Create(56);
        mark.HorizontalAlignment = HorizontalAlignment.Center;

        var title = new TextBlock
        {
            Text = Branding.Name,
            FontSize = Metrics.FontDisplay,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Theme.Brush(palette.TextPrimary)
        };

        var tagline = new TextBlock
        {
            Text = Branding.Tagline,
            FontSize = 15,
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary)
        };

        // Animated keycap used by the welcome screen.
        var keycapGlow = new DropShadowEffect
        {
            Color = palette.Accent,
            BlurRadius = 26,
            ShadowDepth = 0,
            Opacity = 0.22
        };
        var keycap = new Border
        {
            Width = 190,
            Height = 52,
            CornerRadius = new CornerRadius(11),
            Margin = new Thickness(0, 44, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1.5),
            Effect = keycapGlow,
            Child = new TextBlock
            {
                Text = "Space",
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary)
            }
        };
        keycapGlow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(0.22, 0.55, TimeSpan.FromSeconds(2.4))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });

        var lesson = new TextBlock
        {
            Text = "Select a file in Explorer or a file picker, then press Space.\nPress it again to close.",
            FontSize = 14,
            LineHeight = 23,
            Margin = new Thickness(0, 20, 0, 0),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary)
        };

        var hints = new TextBlock
        {
            Text = "Arrow keys browse · Enter opens · Pin keeps a preview open",
            FontSize = 12.5,
            Margin = new Thickness(0, 52, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary)
        };

        return new Grid
        {
            Background = Theme.Brush(palette.WindowBg),
            Children =
            {
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 26),
                    Children = { mark, title, tagline, keycap, lesson, hints }
                }
            }
        };
    }

    public void HidePreview()
    {
        _rotationSession?.Commit();
        _rotationSession = null;
        CloseGridView();
        StopSlideshow(restoreSize: false);
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        CurrentPath = null;
        _shortcutTarget = null;
        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        HideLoadingOverlay();
        HideContentSwapScrim();

        if (!IsVisible)
        {
            ClearPreviewContent();
            return;
        }

        var generation = ++_fadeGeneration;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            CompleteHide();
            return;
        }

        AnimateWindowFade(hwnd, from: 255, to: 0, TimeSpan.FromMilliseconds(90), easeOut: false, () =>
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
        ClearPreviewContent();
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

    public bool ContainsScreenPoint(int x, int y)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
    }

    public bool ContainsOwnedPopupScreenPoint(int x, int y, IntPtr hitRoot)
    {
        if (hitRoot == IntPtr.Zero)
        {
            return false;
        }

        lock (_popupSync)
        {
            if (_ownedPopupHandles.ContainsValue(hitRoot))
            {
                return true;
            }
        }

        if (NativeMethods.GetAncestor(hitRoot, NativeMethods.GA_ROOTOWNER) == Hwnd)
        {
            return true;
        }

        var armedUntil = Interlocked.Read(ref _webContextMenuArmedUntilTicks);
        if (armedUntil < DateTime.UtcNow.Ticks || !NativeMethods.IsLikelyWebViewContextMenuWindow(hitRoot))
        {
            return false;
        }

        var processId = NativeMethods.GetWindowProcessId(hitRoot);
        lock (_popupSync)
        {
            return _webViewProcessIds.Contains(processId);
        }
    }

    public bool OwnsForegroundRoot(IntPtr root)
    {
        if (root == IntPtr.Zero)
        {
            return false;
        }

        if (root == Hwnd || NativeMethods.GetAncestor(root, NativeMethods.GA_ROOTOWNER) == Hwnd)
        {
            return true;
        }

        lock (_popupSync)
        {
            if (_ownedPopupHandles.ContainsValue(root))
            {
                return true;
            }

            var armedUntil = Interlocked.Read(ref _webContextMenuArmedUntilTicks);
            return armedUntil >= DateTime.UtcNow.Ticks
                && NativeMethods.IsLikelyWebViewContextMenuWindow(root)
                && _webViewProcessIds.Contains(NativeMethods.GetWindowProcessId(root));
        }
    }

    // ---------------------------------------------------------------------
    // Entrance/exit animation. The window fades via a temporary layered
    // style (children composite too on Win8+) plus a subtle scale pop, then
    // drops the layered bit so normal rendering has zero overhead.
    // ---------------------------------------------------------------------

    private bool ShouldScaleEntrance() =>
        _stageFitVersion != _loadVersion
        || _stageFitHint is not { Kind: StageFitHint.FitKind.PageAspect };

    private bool ShowWithEntrance(bool useScale = true)
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
            return false;
        }

        LastPresentationFailureReason = string.Empty;

        AnimateWindowFade(hwnd, from: 0, to: 255, TimeSpan.FromMilliseconds(140), easeOut: true, () =>
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

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!useScale)
        {
            RootScale.ScaleX = 1;
            RootScale.ScaleY = 1;
            return true;
        }

        RootScale.ScaleX = 0.965;
        RootScale.ScaleY = 0.965;
        var pop = new DoubleAnimation(0.965, 1.0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        return true;
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

    private void AnimateWindowFade(IntPtr hwnd, byte from, byte to, TimeSpan duration, bool easeOut, Action onDone)
    {
        _fadeTimer?.Stop();
        var generation = _fadeGeneration;
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

            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var eased = easeOut ? 1 - (1 - progress) * (1 - progress) : progress * progress;
            var alpha = (byte)Math.Round(from + (to - from) * eased);
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

    private static void SetWindowAlpha(IntPtr hwnd, byte alpha)
    {
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

    // ---------------------------------------------------------------------
    // Loading overlay: the previous preview stays put while the next one
    // loads; the spinner only appears when a load is actually slow.
    // ---------------------------------------------------------------------

    private async Task ShowLoadingOverlayLaterAsync(long version, int delayMilliseconds, CancellationToken token, (string Title, string Subtitle)? message = null)
    {
        try
        {
            await Task.Delay(delayMilliseconds, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _loadVersion || _loadFinished)
        {
            return;
        }

        if (message is { } copy)
        {
            LoadingTitle.Text = copy.Title;
            LoadingSubtitle.Text = copy.Subtitle;
            LoadingMessagePanel.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingMessagePanel.Visibility = Visibility.Collapsed;
        }

        ShowLoadingOverlayNow();
    }

    private void ShowLoadingOverlayNow()
    {
        // A reveal from the previous load may still be fading this same
        // element. Retake ownership and stop that animation before showing
        // the new load's state; its Completed callback is generation-guarded.
        _loadingOverlayGeneration++;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            LoadingOverlay.Opacity = 1;
        }
        if (ShimmerBand.Visibility == Visibility.Visible)
        {
            return;
        }

        // A faint band of light sweeps the canvas, stays a beat, sweeps
        // again. Alive without machinery, and identical for every file.
        // The band scales with the stage: a fixed sliver that read well on
        // the old capped window disappears on a deck-shaped canvas.
        ShimmerPeak.Color = Theme.Current.IsDark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x16, 0x1B, 0x1E, 0x26);
        ShimmerBand.Width = Math.Clamp(ActualWidth * 0.4, 420, 980);
        ShimmerBand.Visibility = Visibility.Visible;

        var travel = Math.Max(ActualWidth, 600) + ShimmerBand.Width;
        var sweep = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2400),
            RepeatBehavior = RepeatBehavior.Forever
        };
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(
            -ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(
            travel - ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            -ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void HideLoadingOverlay()
    {
        _loadingOverlayGeneration++;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingMessagePanel.Visibility = Visibility.Collapsed;
        ShimmerBand.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The reveal-time exit: the ghost hands off to the real preview with a
    /// short dissolve instead of vanishing between frames.
    /// </summary>
    private void FadeOutLoadingOverlay()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            HideLoadingOverlay();
            return;
        }

        var generation = ++_loadingOverlayGeneration;
        var fade = new DoubleAnimation(LoadingOverlay.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (generation == _loadingOverlayGeneration)
            {
                HideLoadingOverlay();
            }
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowContentSwapScrim(bool instant = false)
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        if (ContentSwapScrim.Visibility != Visibility.Visible)
        {
            ContentSwapScrim.Opacity = 0;
            ContentSwapScrim.Visibility = Visibility.Visible;
        }

        if (instant)
        {
            ContentSwapScrim.Opacity = 1;
            return;
        }

        ContentSwapScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(ContentSwapScrim.Opacity, 1, TimeSpan.FromMilliseconds(70))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RevealContentSwapScrim(bool animate)
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        if (ContentSwapScrim.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!animate)
        {
            HideContentSwapScrim();
            return;
        }

        var fade = new DoubleAnimation(ContentSwapScrim.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            ContentSwapScrim.Visibility = Visibility.Collapsed;
            ContentSwapScrim.Opacity = 0;
        };
        ContentSwapScrim.BeginAnimation(OpacityProperty, fade);
    }

    private void HideContentSwapScrim()
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        ContentSwapScrim.Opacity = 0;
        ContentSwapScrim.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Once the outgoing content has finished dissolving (in-page fade for
    /// web content, the scrim for WPF content), take it out of layout
    /// entirely. Geometry changes happen while the canvas is blank, so the
    /// user sees stable states instead of a relayout in motion.
    /// </summary>
    private void CollapseOutgoingContentSoon()
    {
        if (PreviewContent.Content is not UIElement outgoing)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Only if it is still the same content: a fast load may have
            // already swapped the incoming preview into this slot.
            if (ReferenceEquals(PreviewContent.Content, outgoing))
            {
                outgoing.Visibility = Visibility.Collapsed;
            }
        };
        timer.Start();
    }

    private void RetargetDefaultStageDuringBlankBeat(long version)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(112) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version != _loadVersion || !IsVisible || _stageFitVersion == version)
            {
                return;
            }

            RetargetStageToFit();
        };
        timer.Start();
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
            && !OfficeOpenXmlPreviewLoader.IsSupported(path);
    }

    /// <summary>
    /// The scrim cannot cover a webview (the browser hwnd paints over every
    /// WPF sibling), so web content dissolves itself: an in-page fade to the
    /// view's own background color, which the templates and converted pages
    /// keep matched to the app canvas. ExecuteScriptAsync runs even on
    /// hardened script-off pages. A page that refuses keeps its last frame.
    /// </summary>
    private void FadeOutWebContent()
    {
        if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='opacity 90ms ease-out';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            // Best-effort only.
        }
    }

    /// <summary>Dev probe only: capture the webview's rendered content via CDP
    /// (RenderTargetBitmap cannot see the browser's HWND surface).</summary>
    internal async Task ProbeWebShotAsync(string outputPng)
    {
        try
        {
            if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
            {
                DiagnosticLog.Write("probe webshot: no webview");
                return;
            }

            var json = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{}");
            using var document = JsonDocument.Parse(json);
            var data = document.RootElement.GetProperty("data").GetString();
            await File.WriteAllBytesAsync(outputPng, Convert.FromBase64String(data ?? string.Empty));
            DiagnosticLog.Write($"probe webshot -> {outputPng}");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"probe webshot failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Dev-only: runs script inside the current preview's webview.</summary>
    internal async Task ProbeEvalJsAsync(string script)
    {
        if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: not null } webView)
        {
            DiagnosticLog.Write("probe evaljs: no webview ready");
            return;
        }

        var result = await webView.CoreWebView2.ExecuteScriptAsync(script);
        DiagnosticLog.Write($"probe evaljs -> {result}");
        await Task.Delay(300);
    }

    private async Task LoadPreviewAsync(string path, string contentPath, long version, CancellationToken token)
    {
        try
        {
            // Small yield so rapid arrow-key navigation coalesces on the newest file.
            await Task.Delay(20, token);
            FrameworkElement content;
            string? fillSignPath = null;
            if (Directory.Exists(contentPath))
            {
                // A folder's first frame is entirely local UI. Shell icons,
                // recursive sizing, and signature work are enrichment; none
                // may put a live folder-to-folder swap behind the thread pool.
                content = BuildDirectoryCard(contentPath, token);
                DiagnosticLog.Write($"folder card ready +{ElapsedSinceSwap():0}ms");
            }
            else
            {
                var signature = await Task.Run(() => FileSignatures.Detect(contentPath), token);
                var useSignatureRouting = FileKinds.CanUseSignatureRouting(contentPath);
                if (FileKinds.IsPdf(contentPath) || (useSignatureRouting && signature == FileSignatureKind.Pdf))
                {
                    // Everything PDF-backed goes to the Chromium viewer, so
                    // artwork gets the same zoom, scrolling, and page rail a
                    // document gets. Illustrator files reach it through a
                    // linked .pdf name; the static pdfium render is what is
                    // left when even that fails.
                    var nativePdf = Path.GetExtension(contentPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
                    if (nativePdf)
                    {
                        // The one lane fill and sign serves: real .pdf files,
                        // never conversion artifacts.
                        fillSignPath = contentPath;
                    }

                    var viewerPath = nativePdf
                        ? contentPath
                        : await Task.Run(() => PdfViewerLink.TryResolve(contentPath, token), token);

                    content = viewerPath is null
                        ? await BuildPdfViaPdfiumAsync(contentPath, token)
                        : BuildPdfWebPreview(viewerPath, version, token);
                }
                else if (OfficeOpenXmlPreviewLoader.IsSupported(contentPath))
                {
                    var converted = await Task.Run(() =>
                    {
                        if (OfficeDocumentPdfConverter.TryGetCachedPdfConversion(contentPath) is { } cached)
                        {
                            return cached;
                        }

                        var plan = OfficeDocumentPdfConverter.TryCreateProgressivePlan(contentPath);
                        return plan is null
                            ? OfficeDocumentPdfConverter.TryConvert(contentPath, token)
                            : OfficeDocumentPdfConverter.TryConvertFirstPages(contentPath, plan, token)
                                ?? OfficeDocumentPdfConverter.TryConvert(contentPath, token);
                    }, token);
                    if (converted is not null)
                    {
                        content = BuildConvertedOfficePreview(converted, version, token);
                    }
                    else
                    {
                        var office = await Task.Run(() => OfficeOpenXmlPreviewLoader.TryLoad(contentPath), token);
                        content = office is null
                            ? await BuildFileCardAsync(contentPath, token)
                            : BuildOfficePreviewContent(office);

                        // The machine simply lacks the engine (as opposed to a
                        // conversion failing): offer the fix in place. Never a
                        // notification, never required, one click to act.
                        if (!OfficeDocumentPdfConverter.IsAvailable)
                        {
                            content = WithLibreOfficeSuggestion(content);
                        }
                    }
                }
                else
                {
                    content = await BuildPreviewContentAsync(contentPath, signature, useSignatureRouting, version, token);
                }
            }

            var disposable = _nextDisposable;
            _nextDisposable = null;
            if (token.IsCancellationRequested || version != _loadVersion || !string.Equals(CurrentPath, path, StringComparison.OrdinalIgnoreCase))
            {
                disposable?.Dispose();
                return;
            }

            _fillSignPath = fillSignPath;
            UpdateFillSignPill();
            SetPreviewContent(content, disposable);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"preview load failed {path}: {ex.GetType().Name}: {ex.Message}");
            if (!token.IsCancellationRequested && version == _loadVersion)
            {
                _fillSignPath = null;
                UpdateFillSignPill();
                SetPreviewContent(BuildErrorPreview("Glimpse can't preview this file", "Something went wrong while reading it."), null);
            }
        }
    }

    private async Task<FrameworkElement> BuildPreviewContentAsync(
        string path,
        FileSignatureKind signature,
        bool useSignatureRouting,
        long version,
        CancellationToken token)
    {
        if (FileKinds.IsMedia(path))
        {
            return BuildMediaPreview(path, version, token);
        }

        if (FileKinds.IsGif(path) || (useSignatureRouting && signature == FileSignatureKind.Gif))
        {
            // WPF's Image control freezes GIFs on the first frame; the web engine plays them.
            return BuildBrowserImagePreview(path);
        }

        if (FileKinds.IsImage(path) || (useSignatureRouting && FileSignatures.IsImage(signature)))
        {
            var decoded = await Task.Run(() => DecodeImageFirstFrame(path), token);
            if (decoded is not null)
            {
                // Camera photos carry their upright direction as metadata;
                // Explorer and its thumbnails honor it, so the preview must
                // agree with what sits next to it.
                var session = new ImageRotationSession(path, decoded.Bitmap, decoded.Orientation);
                if (!token.IsCancellationRequested)
                {
                    // A superseded load must not hand its session to the
                    // preview that replaced it.
                    _rotationSession = session;
                }

                if (!decoded.IsFullDecode)
                {
                    _ = CompleteLargeImageDecodeAsync(
                        path,
                        decoded.Stamp,
                        decoded.CacheGeneration,
                        session,
                        version,
                        token);
                }

                // A local image's source header already supplied its real,
                // EXIF-oriented dimensions. A giant image may be painted
                // first from whatever size Explorer happened to cache; that
                // thumbnail is not geometry truth and must not shrink the
                // stage. Network and signature-routed images skip the sync
                // probe, so their decoded pixels still provide the fallback.
                if (_stageFitVersion != version)
                {
                    SetStageFitHint(
                        StageFitHint.ImagePixels(session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight),
                        version);
                }
                return BuildZoomableImageViewer(session.CurrentBitmap, session);
            }

            if (FileKinds.IsHeif(path) || signature == FileSignatureKind.Heif)
            {
                DiagnosticLog.Write($"image decode failed {path}: heif brand={FileSignatures.HeifBrandText(path)}");
                return BuildHeifFailurePreview(path);
            }
        }

        if (FileKinds.IsBrowserImage(path) || (useSignatureRouting && FileSignatures.IsBrowserImage(signature)))
        {
            if (FileKinds.IsSvg(path) && SvgSizeProbe.TryReadCanvasSize(path, out var svgSize))
            {
                SetStageFitHint(StageFitHint.ImagePixels(svgSize.Width, svgSize.Height), version);
            }

            return BuildBrowserImagePreview(path);
        }

        if (FileKinds.IsPhotoshop(path))
        {
            var composite = await Task.Run(() => PsdPreviewLoader.TryLoad(path), token);
            if (composite is not null)
            {
                // The PSD header names the full canvas before its composite
                // is decoded. Large composites are deliberately downsampled,
                // so they are only sizing truth when the header probe failed.
                if (_stageFitVersion != version)
                {
                    SetStageFitHint(StageFitHint.ImagePixels(composite.PixelWidth, composite.PixelHeight), version);
                }
                return BuildZoomableImageViewer(composite);
            }

            // No readable composite: the generic card below still shows a
            // shell thumbnail when an installed codec provides one.
        }

        if (FileKinds.IsFont(path))
        {
            return BuildFontPreview(path);
        }

        var zipPackage = useSignatureRouting && signature == FileSignatureKind.Zip
            ? await Task.Run(() => FileSignatures.DetectZipPackageKind(path), token)
            : ZipPackageKind.Unknown;

        if (FileKinds.IsEpub(path) || zipPackage == ZipPackageKind.Epub)
        {
            var epub = await Task.Run(() => GeneratedHtmlPreview.TryBuildEpub(path, token), token);
            if (epub is not null)
            {
                return BuildEpubWebPreview(epub, path, token);
            }
        }

        if (FileKinds.IsWindowsAppPackage(path) || zipPackage == ZipPackageKind.WindowsAppPackage)
        {
            var package = await Task.Run(() => AppPackagePreviewLoader.TryLoad(path), token);
            if (package is not null)
            {
                return BuildAppPackageCard(package);
            }
        }

        if (TryMapOpenXmlPackage(zipPackage, out var officeType))
        {
            var office = await Task.Run(() => OfficeOpenXmlPreviewLoader.TryLoad(path, officeType), token);
            if (office is not null)
            {
                return BuildOfficePreviewContent(office);
            }
        }

        if (FileKinds.IsArchive(path) || (useSignatureRouting && FileSignatures.IsArchive(signature)))
        {
            var listing = await Task.Run(() => ArchivePreviewLoader.TryLoad(path, token), token);
            if (listing is not null)
            {
                return BuildArchivePreview(listing);
            }
        }

        if (FileKinds.IsMesh(path))
        {
            var meshPreview = await Task.Run(() => MeshPreviewLoader.TryLoad(path), token);
            if (meshPreview is not null)
            {
                return BuildMeshPreview(meshPreview);
            }

            if (FileKinds.IsStl(path))
            {
                var cadPreview = await Task.Run(() => CadPreviewLoader.TryLoadStl(path, token), token);
                return BuildCadPreview(cadPreview, "#C8A2FF");
            }

            // Other mesh formats that fail to parse fall through to the
            // generic card below.
        }

        if (FileKinds.IsStep(path))
        {
            // A rotating vertex outline beats a static card. True surfaces
            // would need a CAD kernel; the cloud still shows the shape.
            var outline = await Task.Run(() => MeshPreviewLoader.TryLoadStepOutline(path), token);
            if (outline is not null)
            {
                return BuildMeshPreview(outline);
            }

            var shellImage = await Task.Run(() => ShellThumbnailProvider.TryGetThumbnail(path), token);
            if (shellImage is { IsThumbnail: true })
            {
                return await BuildFileCardAsync(path, token);
            }

            var cadPreview = await Task.Run(() => CadPreviewLoader.TryLoadStep(path), token);
            return BuildCadPreview(cadPreview, "#C8A2FF");
        }

        if (FileKinds.IsHtml(path))
        {
            // Quick Look renders web pages, it doesn't show their source.
            return BuildHtmlPreview(path);
        }

        if (FileKinds.IsInternetShortcut(path))
        {
            return await BuildFileCardAsync(path, token);
        }

        if (FileKinds.IsMarkdown(path))
        {
            var markdown = await Task.Run(() => GeneratedHtmlPreview.TryBuildMarkdown(path), token);
            if (markdown is not null)
            {
                return BuildGeneratedHtmlPreview(markdown, $"markdown preview {path}");
            }
        }

        if (FileKinds.IsText(path))
        {
            var attempt = await Task.Run(
                () => GeneratedHtmlPreview.TryBuildCodeForPreview(path, token),
                token);
            if (attempt.Document is { } code)
            {
                return BuildGeneratedHtmlPreview(code, $"code preview {path}");
            }

            // Syntax detection already loaded the file when it declined the
            // generated lane. Reuse that exact snapshot so uncommon text-like
            // formats do not pay for a second read before first paint.
            var text = await Task.Run(
                () => FormatTextPreview(
                    path,
                    attempt.FallbackText ?? TextPreviewLoader.TryLoad(path)),
                token);
            if (text is not null)
            {
                return BuildTextPreview(new TextPreview(text.Value.Text, Truncated: false, TotalBytes: 0), text.Value.Wrap);
            }
        }

        if (FileKinds.IsIllustrator(path))
        {
            var summary = await Task.Run(() => BuildIllustratorSummary(path), token);
            return BuildLabeledTextPreview(
                "Illustrator artwork",
                Color.FromRgb(255, 157, 72),
                summary,
                new FontFamily("Segoe UI Variable, Segoe UI"),
                fontSize: 14,
                wrap: true);
        }

        if (FileKinds.IsExecutableLike(path))
        {
            var data = await Task.Run(() => CollectFileCardData(path, token), token);
            return BuildFileCard(data, liveLine: null, BuildExecutableToggles(path));
        }

        return await BuildFileCardAsync(path, token);
    }

    private static bool TryMapOpenXmlPackage(ZipPackageKind packageKind, out OfficePreviewType previewType)
    {
        previewType = packageKind switch
        {
            ZipPackageKind.OfficeWord => OfficePreviewType.Word,
            ZipPackageKind.OfficePowerPoint => OfficePreviewType.PowerPoint,
            ZipPackageKind.OfficeExcel => OfficePreviewType.Excel,
            _ => default
        };

        return packageKind is ZipPackageKind.OfficeWord or ZipPackageKind.OfficePowerPoint or ZipPackageKind.OfficeExcel;
    }

    // ---------------------------------------------------------------------
    // Context actions: small pills that give the card real controls for the
    // thing being previewed, without changing the default look.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Persistent per-exe settings, drawn as what they are: switches. Launch
    /// actions live in the header's split Open control, not here.
    /// </summary>
    private static FrameworkElement? BuildExecutableToggles(string path)
    {
        if (!Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(new Border
        {
            Height = 1,
            Width = 200,
            Margin = new Thickness(0, 18, 0, 8),
            Background = Theme.Brush(Theme.Current.Border),
            HorizontalAlignment = HorizontalAlignment.Center
        });
        stack.Children.Add(CreateCompatToggleRow(path, "Always run as administrator", ExecutableTweaks.RunAsAdminFlag));
        stack.Children.Add(CreateCompatToggleRow(path, "Disable fullscreen optimizations", ExecutableTweaks.DisableFullscreenOptimizationsFlag));
        return stack;
    }

    private static FrameworkElement CreateCompatToggleRow(string path, string label, string flag)
    {
        // A machine-wide flag needs elevation to change; showing a switch that
        // snaps back is misleading. Show it on and locked instead.
        var locked = ExecutableTweaks.IsMachineEnforced(path, flag);
        return CreateToggleRow(
            label,
            () => ExecutableTweaks.HasFlag(path, flag),
            () => ExecutableTweaks.ToggleFlag(path, flag),
            lockedReason: locked ? "Set for all users in the file's Properties" : null);
    }

    private static FrameworkElement CreateToggleRow(string label, Func<bool> getState, Action toggle, string? lockedReason = null)
    {
        var palette = Theme.Current;

        var knobTransform = new TranslateTransform();
        var knob = new Border
        {
            Width = 11,
            Height = 11,
            CornerRadius = new CornerRadius(5.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3, 0, 3, 0),
            RenderTransform = knobTransform
        };
        var track = new Border
        {
            Width = 32,
            Height = 17,
            CornerRadius = new CornerRadius(8.5),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = knob
        };

        var row = new Grid { Height = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 0);
        row.Children.Add(text);
        Grid.SetColumn(track, 1);
        row.Children.Add(track);

        var container = new Border
        {
            Width = 320,
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(10, 0, 8, 0),
            Background = Brushes.Transparent,
            Cursor = lockedReason is null ? Cursors.Hand : Cursors.Arrow,
            Child = row
        };

        if (lockedReason is not null)
        {
            container.Opacity = 0.62;
            container.ToolTip = lockedReason;
        }

        void Render(bool animate)
        {
            var on = getState();
            var accent = palette.Accent;
            track.Background = on ? Theme.Brush(accent) : Theme.Brush(palette.SurfaceBg);
            track.BorderBrush = on
                ? Theme.Brush(accent)
                : Theme.Brush(palette.ScrollThumb);
            knob.Background = on ? Brushes.White : Theme.Brush(palette.TextTertiary);

            var targetX = on ? 13.0 : 0.0;
            if (animate)
            {
                knobTransform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(targetX, TimeSpan.FromMilliseconds(140))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }
            else
            {
                knobTransform.X = targetX;
            }
        }

        if (lockedReason is null)
        {
            container.MouseEnter += (_, _) => container.Background = Theme.Brush(palette.HoverBg);
            container.MouseLeave += (_, _) => container.Background = Brushes.Transparent;
            container.MouseLeftButtonDown += (_, e) => e.Handled = true;
            container.MouseLeftButtonUp += (_, e) =>
            {
                toggle();
                Render(animate: true);
                e.Handled = true;
            };
        }

        Render(animate: false);
        return container;
    }

    private void RunAsAdministrator(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas" });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // UAC declined; nothing to do.
            return;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"run as admin failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OpenInTerminal(string folder)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folder}\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    WorkingDirectory = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DiagnosticLog.Write($"open terminal failed {folder}: {ex.GetType().Name}: {ex.Message}");
                return;
            }
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // Images: decode capped to keep 100-megapixel photos cheap, displayed in
    // a zoomable viewer (wheel zooms at the cursor, drag pans, double-click
    // toggles fit/actual size).
    // ---------------------------------------------------------------------

    private sealed record ImageFirstFrame(
        BitmapSource Bitmap,
        int Orientation,
        bool IsFullDecode,
        ImageFileStamp? Stamp,
        int CacheGeneration);

    private ImageFirstFrame? DecodeImageFirstFrame(string path)
    {
        var hasStamp = ImageFileStamp.TryCreate(path, out var stamp);
        if (hasStamp && _imageDecodeCache.TryGet(stamp, out var cached) && cached is not null)
        {
            DiagnosticLog.Write($"image decode cache hit {path}");
            return new ImageFirstFrame(
                cached.Bitmap,
                cached.Orientation,
                IsFullDecode: true,
                stamp,
                _imageDecodeCache.CaptureGeneration());
        }

        var cacheGeneration = _imageDecodeCache.CaptureGeneration();
        if (TryGetImagePixelSize(path, out var pixels)
            && Math.Max(pixels.Width, pixels.Height) > MaxImageDecodePixels
            && ShellThumbnailProvider.TryGetCachedThumbnail(path, LargeImageFirstFramePixels) is { } cachedThumbnail)
        {
            DiagnosticLog.Write(
                $"image cached first frame {cachedThumbnail.Bitmap.PixelWidth}x{cachedThumbnail.Bitmap.PixelHeight} {path}");
            // Explorer already applies EXIF orientation to its thumbnail.
            return new ImageFirstFrame(
                cachedThumbnail.Bitmap,
                ImageOrientation.Normal,
                IsFullDecode: false,
                hasStamp ? stamp : null,
                cacheGeneration);
        }

        var bitmap = TryDecodeImage(path);
        if (bitmap is null)
        {
            return null;
        }

        var orientation = ImageOrientation.Read(path);
        if (hasStamp)
        {
            _imageDecodeCache.Store(
                stamp,
                new CachedImageDecode(bitmap, orientation),
                cacheGeneration);
        }

        return new ImageFirstFrame(
            bitmap,
            orientation,
            IsFullDecode: true,
            hasStamp ? stamp : null,
            cacheGeneration);
    }

    private async Task CompleteLargeImageDecodeAsync(
        string path,
        ImageFileStamp? stamp,
        int cacheGeneration,
        ImageRotationSession session,
        long version,
        CancellationToken token)
    {
        try
        {
            await Task.Delay(LargeImageFullDecodeDelay, token);
            await _imageDecodeGate.WaitAsync(token);
            CachedImageDecode? decoded;
            try
            {
                token.ThrowIfCancellationRequested();
                decoded = await Task.Run(() =>
                {
                    var bitmap = TryDecodeImage(path);
                    return bitmap is null
                        ? null
                        : new CachedImageDecode(bitmap, ImageOrientation.Read(path));
                }, token);
            }
            finally
            {
                _imageDecodeGate.Release();
            }

            if (decoded is null
                || token.IsCancellationRequested
                || version != _loadVersion
                || !ReferenceEquals(_rotationSession, session))
            {
                return;
            }

            if (stamp is { } fileStamp)
            {
                _imageDecodeCache.Store(
                    fileStamp,
                    decoded,
                    cacheGeneration);
            }

            if (session.TryUpgradeDecoded(decoded.Bitmap, decoded.Orientation))
            {
                DiagnosticLog.Write($"image full decode ready {decoded.Bitmap.PixelWidth}x{decoded.Bitmap.PixelHeight} {path}");
            }
        }
        catch (OperationCanceledException)
        {
            // The user scrubbed to another item before the expensive lane began.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"image full decode failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static BitmapSource? TryDecodeImage(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Our stamp-aware one-entry cache owns reuse. Bypass WPF's URI
            // cache so an externally changed file can never inherit pixels
            // held alive by the previous entry.
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
            if (TryGetImagePixelSize(path, out var pixelSize))
            {
                if (pixelSize.Width >= pixelSize.Height && pixelSize.Width > MaxImageDecodePixels)
                {
                    bitmap.DecodePixelWidth = MaxImageDecodePixels;
                }
                else if (pixelSize.Height > pixelSize.Width && pixelSize.Height > MaxImageDecodePixels)
                {
                    bitmap.DecodePixelHeight = MaxImageDecodePixels;
                }
            }

            bitmap.UriSource = new Uri(path, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static FrameworkElement BuildZoomableImageViewer(BitmapSource bitmap, ImageRotationSession? rotation = null)
    {
        var palette = Theme.Current;
        var container = new Grid
        {
            Background = Theme.Brush(palette.CanvasBg),
            ClipToBounds = true
        };

        var scale = new ScaleTransform(1, 1);
        var translate = new TranslateTransform();
        var image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new TransformGroup { Children = { scale, translate } }
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        container.Children.Add(image);

        var hudText = new TextBlock
        {
            FontSize = Metrics.FontLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.OverlayText)
        };
        var hud = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            Padding = new Thickness(12, 6, 12, 7),
            CornerRadius = Metrics.PillCorner,
            Background = Theme.Brush(Theme.OverlayScrim),
            IsHitTestVisible = false,
            Opacity = 0,
            Child = hudText
        };
        container.Children.Add(hud);

        var zoom = 1.0;
        var current = bitmap;

        void ShowHud()
        {
            var fitted = image.ActualWidth;
            var percent = fitted > 0 && current.Width > 0 ? zoom * fitted / current.Width * 100 : zoom * 100;
            hudText.Text = zoom <= 1.0001 ? "Fit" : $"{Math.Round(percent)}%";
            hud.BeginAnimation(OpacityProperty, null);
            hud.Opacity = 1;
            hud.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260))
            {
                BeginTime = TimeSpan.FromMilliseconds(800)
            });
        }

        void ClampTranslate()
        {
            var maxX = Math.Max(0, (image.ActualWidth * zoom - container.ActualWidth) / 2);
            var maxY = Math.Max(0, (image.ActualHeight * zoom - container.ActualHeight) / 2);
            translate.X = Math.Clamp(translate.X, -maxX, maxX);
            translate.Y = Math.Clamp(translate.Y, -maxY, maxY);
        }

        void SetZoom(double next, Point anchor)
        {
            next = Math.Clamp(next, 1.0, 12.0);
            var center = new Point(container.ActualWidth / 2, container.ActualHeight / 2);
            var rel = anchor - center;
            translate.X = rel.X - (rel.X - translate.X) * (next / zoom);
            translate.Y = rel.Y - (rel.Y - translate.Y) * (next / zoom);
            zoom = next;
            scale.ScaleX = zoom;
            scale.ScaleY = zoom;
            if (zoom <= 1.0001)
            {
                translate.X = 0;
                translate.Y = 0;
            }

            ClampTranslate();
            container.Cursor = zoom > 1.0001 ? Cursors.SizeAll : Cursors.Arrow;
            ShowHud();
        }

        container.MouseWheel += (_, e) =>
        {
            var factor = e.Delta > 0 ? 1.18 : 1 / 1.18;
            SetZoom(zoom * factor, e.GetPosition(container));
            e.Handled = true;
        };

        var dragging = false;
        var lastPosition = new Point();
        container.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                if (zoom > 1.0001)
                {
                    SetZoom(1.0, e.GetPosition(container));
                }
                else if (image.ActualWidth > 0)
                {
                    // Zoom to actual pixels at the click point.
                    SetZoom(Math.Max(1.0, current.Width / image.ActualWidth), e.GetPosition(container));
                }

                e.Handled = true;
                return;
            }

            if (zoom <= 1.0001)
            {
                return;
            }

            dragging = true;
            lastPosition = e.GetPosition(container);
            container.CaptureMouse();
            e.Handled = true;
        };
        container.MouseMove += (_, e) =>
        {
            if (!dragging)
            {
                return;
            }

            var position = e.GetPosition(container);
            translate.X += position.X - lastPosition.X;
            translate.Y += position.Y - lastPosition.Y;
            lastPosition = position;
            ClampTranslate();
        };
        container.MouseLeftButtonUp += (_, _) =>
        {
            dragging = false;
            container.ReleaseMouseCapture();
        };
        container.MouseLeave += (_, _) =>
        {
            dragging = false;
            container.ReleaseMouseCapture();
        };
        container.SizeChanged += (_, _) => ClampTranslate();

        if (rotation is not null)
        {
            // A quarter turn is a new composition: swap the source and let
            // the viewer re-fit rather than spinning a zoomed crop.
            rotation.ViewChanged += () =>
            {
                current = rotation.CurrentBitmap;
                image.Source = current;
                zoom = 1.0;
                scale.ScaleX = 1;
                scale.ScaleY = 1;
                translate.X = 0;
                translate.Y = 0;
                container.Cursor = Cursors.Arrow;
            };
            rotation.QualityUpgraded += () =>
            {
                current = rotation.CurrentBitmap;
                image.Source = current;
                ClampTranslate();
            };
        }

        return container;
    }

    // ---------------------------------------------------------------------
    // WebView2-backed previews share one environment (see WebViewEnvironment).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Untrusted HTML gets Quick Look semantics: no scripts, no network.
    /// Navigation blocking alone is not enough; subresources (an image, a
    /// stylesheet, a tracking pixel) must not phone home either. Local data
    /// stays allowed: file content and data: URIs. UNC-shaped file URLs are
    /// blocked because they are network requests, not local files.
    /// </summary>
    private static void HardenForUntrustedHtml(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        core.Settings.IsScriptEnabled = false;
        core.NavigationStarting += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Uri))
            {
                args.Cancel = true;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            DiagnosticLog.Write($"webview blocked popup {DiagnosticLog.DescribeUri(args.Uri)}");
        };
        core.AddWebResourceRequestedFilter("*", Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedUntrustedHtmlResource(args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"webview blocked remote resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    internal static bool IsAllowedUntrustedHtmlResource(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!parsed.IsFile)
        {
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            return !parsed.LocalPath.StartsWith(@"\\", StringComparison.Ordinal);
        }

        return parsed.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSafeLocalPreviewTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (LooksLikeLocalDrivePath(path))
        {
            return true;
        }

        if (Uri.TryCreate(path, UriKind.Absolute, out var parsed))
        {
            return parsed.IsFile && IsLocalFileUri(parsed) && LooksLikeLocalDrivePath(LocalPathFromFileUri(parsed));
        }

        return LooksLikeLocalDrivePath(path);
    }

    private static bool IsLocalFileUri(Uri uri)
    {
        if (!uri.IsFile)
        {
            return false;
        }

        return string.IsNullOrEmpty(uri.Host) || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    private static string LocalPathFromFileUri(Uri uri)
    {
        if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            var path = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            return path.Replace('/', '\\');
        }

        return uri.LocalPath;
    }

    private static bool LooksLikeLocalDrivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        var root = Path.GetPathRoot(path);
        return root is { Length: >= 3 }
            && char.IsLetter(root[0])
            && root[1] == ':'
            && (root[2] == '\\' || root[2] == '/');
    }

    internal enum PdfNavigationAction
    {
        Allow,
        Cancel,
        OpenExternally
    }

    internal enum GeneratedHtmlNavigationAction
    {
        Allow,
        Cancel,
        OpenExternally
    }

    internal static bool IsAllowedGeneratedHtmlResource(string htmlPath, string? resourceHost, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        if (parsed.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(resourceHost)
            && (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && parsed.Host.Equals(resourceHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!parsed.IsFile || !IsLocalFileUri(parsed))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(htmlPath),
                Path.GetFullPath(LocalPathFromFileUri(parsed)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static GeneratedHtmlNavigationAction DecideGeneratedHtmlNavigation(string htmlPath, string? resourceHost, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return GeneratedHtmlNavigationAction.Cancel;
        }

        if (parsed.IsFile && IsLocalFileUri(parsed))
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(htmlPath),
                    Path.GetFullPath(LocalPathFromFileUri(parsed)),
                    StringComparison.OrdinalIgnoreCase)
                    ? GeneratedHtmlNavigationAction.Allow
                    : GeneratedHtmlNavigationAction.Cancel;
            }
            catch
            {
                return GeneratedHtmlNavigationAction.Cancel;
            }
        }

        if ((parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && !parsed.Host.Equals(resourceHost ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return GeneratedHtmlNavigationAction.OpenExternally;
        }

        return GeneratedHtmlNavigationAction.Cancel;
    }

    private static void HardenForGeneratedHtml(CoreWebView2 core, GeneratedHtmlDocument document)
    {
        core.Settings.IsScriptEnabled = false;
        if (!string.IsNullOrWhiteSpace(document.ResourceHost)
            && !string.IsNullOrWhiteSpace(document.ResourceFolder))
        {
            core.SetVirtualHostNameToFolderMapping(
                document.ResourceHost,
                document.ResourceFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        core.NavigationStarting += (_, args) =>
        {
            switch (DecideGeneratedHtmlNavigation(document.HtmlPath, document.ResourceHost, args.Uri))
            {
                case GeneratedHtmlNavigationAction.Allow:
                    break;
                case GeneratedHtmlNavigationAction.OpenExternally:
                    args.Cancel = true;
                    if (args.IsUserInitiated)
                    {
                        OpenExternalUri(args.Uri);
                    }
                    break;
                default:
                    args.Cancel = true;
                    break;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated
                && DecideGeneratedHtmlNavigation(document.HtmlPath, document.ResourceHost, args.Uri) == GeneratedHtmlNavigationAction.OpenExternally)
            {
                OpenExternalUri(args.Uri);
            }
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            if (!IsAllowedGeneratedHtmlResource(document.HtmlPath, document.ResourceHost, args.Request.Uri))
            {
                args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
                DiagnosticLog.Write($"generated html blocked resource {DiagnosticLog.DescribeUri(args.Request.Uri)}");
            }
        };
    }

    internal static PdfNavigationAction DecidePdfNavigation(string sourcePath, string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            return PdfNavigationAction.Cancel;
        }

        if (parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return PdfNavigationAction.OpenExternally;
        }

        if (!parsed.IsFile || !IsLocalFileUri(parsed))
        {
            return PdfNavigationAction.Cancel;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(LocalPathFromFileUri(parsed)),
                StringComparison.OrdinalIgnoreCase)
                ? PdfNavigationAction.Allow
                : PdfNavigationAction.Cancel;
        }
        catch
        {
            return PdfNavigationAction.Cancel;
        }
    }

    // FitH is what keeps the page filling the stage when the window resizes:
    // without it the viewer holds whatever zoom it opened at, so maximizing
    // just adds empty canvas around an unchanged page.
    private static Uri PdfViewerSource(string path, int page = 1) =>
        new(new Uri(path, UriKind.Absolute).AbsoluteUri + $"#page={Math.Max(1, page)}&view=FitH");

    private static void ConfigurePdfViewer(CoreWebView2 core, string sourcePath)
    {
        try
        {
            // The toolbar keeps the zoom pair, the page box, search, and
            // print - a fixed set that fits at every stage width, so the
            // responsive overflow never rearranges it and nothing appears or
            // disappears when the rail resizes the viewer. Hiding more is not
            // an option: removing Search collapses the entire strip, page box
            // included, and the page box is the one native affordance our
            // rail drives for instant jumps.
            //
            // This exact set is load-bearing. JumpPdfWithNativePageBoxAsync
            // clicks the page box at a measured offset from the centre of the
            // viewer, so ANY change here moves the box out from under that
            // click, and the Ctrl+A that follows lands on the document and
            // selects the whole thing instead. Adding Fit did exactly that.
            core.Settings.HiddenPdfToolbarItems |=
                CoreWebView2PdfToolbarItems.Save | CoreWebView2PdfToolbarItems.SaveAs |
                CoreWebView2PdfToolbarItems.Rotate |
                CoreWebView2PdfToolbarItems.FitPage | CoreWebView2PdfToolbarItems.PageLayout |
                CoreWebView2PdfToolbarItems.Bookmarks |
                CoreWebView2PdfToolbarItems.FullScreen | CoreWebView2PdfToolbarItems.MoreSettings;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf toolbar settings unavailable: {ex.GetType().Name}: {ex.Message}");
        }

        // The PDF viewer owns same-file fragments and query changes: its
        // sidebar, page box, and thumbnails all jump by navigating the loaded
        // document to a new in-document target. Other files stay out.
        core.NavigationStarting += (_, args) =>
        {
            switch (DecidePdfNavigation(sourcePath, args.Uri))
            {
                case PdfNavigationAction.Allow:
                    break;
                case PdfNavigationAction.OpenExternally:
                    args.Cancel = true;
                    OpenExternalUri(args.Uri);
                    break;
                default:
                    args.Cancel = true;
                    break;
            }
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (DecidePdfNavigation(sourcePath, args.Uri) == PdfNavigationAction.OpenExternally)
            {
                OpenExternalUri(args.Uri);
            }
        };
    }

    private enum WebPaintReadinessMode
    {
        AnimationFrames,
        StaticLayout,
        PdfPlugin
    }

    private static WebPaintReadinessMode PaintReadinessMode(WebView2 webView) =>
        webView.Tag is WebPaintReadinessMode mode ? mode : WebPaintReadinessMode.AnimationFrames;

    private static string PaintReadinessModeName(WebPaintReadinessMode mode) => mode switch
    {
        WebPaintReadinessMode.StaticLayout => "layout",
        WebPaintReadinessMode.PdfPlugin => "pdf-plugin",
        _ => "frames"
    };

    private WebView2 CreateWebView(
        System.Drawing.Color background,
        Func<Uri> getSource,
        string diagnosticLabel,
        Action<Exception>? onFailed = null,
        Action<Microsoft.Web.WebView2.Core.CoreWebView2>? configure = null,
        bool trackAsPreviewDisposable = true,
        WebPaintReadinessMode readinessMode = WebPaintReadinessMode.AnimationFrames)
    {
        var requestVersion = _loadVersion;
        var requestToken = _loadCancellation?.Token ?? CancellationToken.None;
        var failureReported = 0;
        var initializationStarted = 0;
        var webView = new WebView2
        {
            DefaultBackgroundColor = background,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Tag = readinessMode
        };
        if (trackAsPreviewDisposable)
        {
            _nextDisposable = webView;
        }

        bool IsCurrentRequest() =>
            !requestToken.IsCancellationRequested && requestVersion == _loadVersion;

        void ReportFailure(Exception exception)
        {
            if (!IsCurrentRequest())
            {
                return;
            }

            // The PDF viewer is deliberately parked while Fill & Sign owns
            // the stage. Never replace that surface (and its unsaved work)
            // because the hidden browser failed; apply the fallback after
            // the user exits the mode instead.
            if (_fillSignSurface is not null && onFailed is not null)
            {
                _deferredWebFailure = () => ReportFailure(exception);
                DiagnosticLog.Write($"{diagnosticLabel} failure deferred during fill-sign");
                return;
            }

            if (Interlocked.Exchange(ref failureReported, 1) != 0)
            {
                return;
            }

            DiagnosticLog.Write($"{diagnosticLabel} failed: {exception.GetType().Name}: {exception.Message}");
            onFailed?.Invoke(exception);
        }

        var sourceAssigned = false;
        var initialNavigationPending = true;
        webView.NavigationCompleted += (_, args) =>
        {
            if (!sourceAssigned || !initialNavigationPending)
            {
                return;
            }

            initialNavigationPending = false;
            if (!args.IsSuccess)
            {
                ReportFailure(new InvalidOperationException($"navigation failed ({args.WebErrorStatus})"));
            }
        };

        webView.Loaded += async (_, _) =>
        {
            try
            {
                if (!IsCurrentRequest())
                {
                    return;
                }

                // Loaded can fire again when a PDF is parked for Fill & Sign
                // and then restored. Reconfiguring would duplicate every
                // handler and reset the viewer back to its original source.
                if (Interlocked.CompareExchange(ref initializationStarted, 1, 0) != 0)
                {
                    return;
                }

                var environment = await WebViewEnvironment.GetAsync();
                if (!IsCurrentRequest())
                {
                    return;
                }

                await webView.EnsureCoreWebView2Async(environment);
                if (!IsCurrentRequest())
                {
                    return;
                }

                // The native menu (copy, save, video controls) is the unified
                // right-click experience across every web-backed preview.
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                RememberWebViewProcesses(webView.CoreWebView2);
                webView.CoreWebView2.ContextMenuRequested += (_, _) => ArmWebContextMenu(webView.CoreWebView2);
                webView.CoreWebView2.ProcessFailed += (_, args) =>
                    ReportFailure(new InvalidOperationException($"browser process failed ({args.ProcessFailedKind})"));
                configure?.Invoke(webView.CoreWebView2);
                if (!IsCurrentRequest())
                {
                    return;
                }

                var source = getSource();
                sourceAssigned = true;
                webView.Source = source;
            }
            catch (Exception ex)
            {
                ReportFailure(ex);
            }
        };

        return webView;
    }

    private FrameworkElement BuildBrowserImagePreview(string path)
    {
        var palette = Theme.Current;
        var background = System.Drawing.Color.FromArgb(palette.CanvasBg.R, palette.CanvasBg.G, palette.CanvasBg.B);
        var grid = new Grid { Background = Theme.Brush(palette.CanvasBg) };
        grid.Children.Add(CreateWebView(
            background,
            () => WriteImageHtml(path),
            $"browser image {path}",
            ex => _ = ShowFileCardFallbackAsync(path)));
        return grid;
    }

    private FrameworkElement BuildHtmlPreview(string path)
    {
        var grid = new Grid { Background = Brushes.White };
        grid.Children.Add(CreateWebView(
            System.Drawing.Color.White,
            () => new Uri(path, UriKind.Absolute),
            $"html preview {path}",
            ex => _ = ShowTextOrFileFallbackAsync(path),
            configure: core =>
            {
                HardenForUntrustedHtml(core);
                NormalizeHtmlPageScrolling(core);
            },
            readinessMode: WebPaintReadinessMode.StaticLayout));
        return grid;
    }

    private FrameworkElement BuildGeneratedHtmlPreview(GeneratedHtmlDocument document, string label)
    {
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.ContentBg) };
        grid.Children.Add(CreateWebView(
            document.Background,
            () => document.Source,
            label,
            ex => _ = ShowTextOrFileFallbackAsync(document.SourcePath),
            configure: core => HardenForGeneratedHtml(core, document),
            readinessMode: WebPaintReadinessMode.StaticLayout));
        return grid;
    }

    /// <summary>
    /// Saved pages scroll arbitrarily, and with their scripts off their own
    /// scroll logic is dead. After each load: unlock the root only when
    /// content is trapped and nothing else already scrolls it (otherwise a
    /// phantom root bar appears over the real one), collapse an html+body
    /// double scroller only when both really scroll, style every bar through
    /// the standard scrollbar properties (they override a page's own styling
    /// where the webkit pseudo-elements do not), and follow the page's actual
    /// paint so dark pages get dark bars. ExecuteScriptAsync ignores the
    /// page-script setting, so this still runs on hardened previews.
    /// </summary>
    private static void NormalizeHtmlPageScrolling(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        const string script = """
            (() => {
              const style = document.createElement('style');
              style.textContent = '*{scrollbar-width:thin!important;scrollbar-color:rgba(128,134,148,.65) transparent!important}';
              (document.head || document.documentElement).appendChild(style);

              const de = document.documentElement;
              const body = document.body;

              // Viewport-sized scrollers, per axis: the page's own way of
              // reaching its content.
              let scrollerX = null;
              let scrollerY = null;
              if (body) {
                const all = body.querySelectorAll('*');
                const count = Math.min(all.length, 600);
                for (let i = 0; i < count; i++) {
                  const el = all[i];
                  const cs = getComputedStyle(el);
                  const scrollsY = (cs.overflowY === 'auto' || cs.overflowY === 'scroll') && el.scrollHeight > el.clientHeight + 2;
                  const scrollsX = (cs.overflowX === 'auto' || cs.overflowX === 'scroll') && el.scrollWidth > el.clientWidth + 2;
                  if (!scrollsY && !scrollsX) continue;
                  const box = el.getBoundingClientRect();
                  if (box.width < window.innerWidth * 0.8 || box.height < window.innerHeight * 0.8) continue;
                  // A scroller whose own bar sits outside the viewport (a
                  // layout wider or taller than the window) cannot be used;
                  // release that axis so the content flows and the ROOT
                  // scrolls it, with the bar at the viewport edge.
                  if (scrollsY) {
                    if (box.right > window.innerWidth + 8) {
                      el.style.setProperty('overflow-y', 'visible', 'important');
                    } else if (!scrollerY) {
                      scrollerY = el;
                    }
                  }
                  if (scrollsX) {
                    if (box.bottom > window.innerHeight + 8) {
                      el.style.setProperty('overflow-x', 'visible', 'important');
                    } else if (!scrollerX) {
                      scrollerX = el;
                    }
                  }
                  if (scrollerX && scrollerY) break;
                }
              }

              // Trapped content is unlocked per axis: a hidden root under-
              // reports its size, so the body measures too. Only when no
              // viewport scroller already handles that axis.
              const deStyle = getComputedStyle(de);
              const bodyStyle = body ? getComputedStyle(body) : null;
              const contentWidth = Math.max(de.scrollWidth, body ? body.scrollWidth : 0);
              const contentHeight = Math.max(de.scrollHeight, body ? body.scrollHeight : 0);

              if (contentWidth > de.clientWidth + 2 && !scrollerX) {
                if (deStyle.overflowX === 'hidden') de.style.setProperty('overflow-x', 'auto', 'important');
                if (bodyStyle && bodyStyle.overflowX === 'hidden') body.style.setProperty('overflow-x', 'visible', 'important');
              }
              if (contentHeight > de.clientHeight + 2 && !scrollerY) {
                if (deStyle.overflowY === 'hidden') de.style.setProperty('overflow-y', 'auto', 'important');
                if (bodyStyle && bodyStyle.overflowY === 'hidden') body.style.setProperty('overflow-y', 'visible', 'important');
              }

              // html and body both scrolling = two bars for one page; body yields.
              if (body) {
                const deNow = getComputedStyle(de);
                const bodyNow = getComputedStyle(body);
                const deScrolls = deNow.overflowY === 'auto' || deNow.overflowY === 'scroll';
                const bodyScrolls = (bodyNow.overflowY === 'auto' || bodyNow.overflowY === 'scroll')
                  && body.scrollHeight > body.clientHeight + 2;
                if (deScrolls && bodyScrolls) {
                  body.style.setProperty('overflow', 'visible', 'important');
                }
              }

              // Dead page scripts leave layout slop: a root that overflows by
              // a sliver next to a real full-viewport scroller produces a
              // phantom bar with a few pixels of travel, drawn over the real
              // one. Clip the sliver; keep real root content.
              const rangeY = de.scrollHeight - de.clientHeight;
              if (scrollerY && rangeY > 0 && rangeY <= 32 && getComputedStyle(de).overflowY !== 'hidden') {
                de.style.setProperty('overflow-y', 'hidden', 'important');
              }
              const rangeX = de.scrollWidth - de.clientWidth;
              if (scrollerX && rangeX > 0 && rangeX <= 32 && getComputedStyle(de).overflowX !== 'hidden') {
                de.style.setProperty('overflow-x', 'hidden', 'important');
              }

              // Native bars follow the declared color scheme; apps paint their
              // darkness on wrappers as often as on the body, so look there too.
              const opaque = el => {
                if (!el) return null;
                const c = getComputedStyle(el).backgroundColor;
                const m = c && c.match(/rgba?\((\d+)[, ]+(\d+)[, ]+(\d+)(?:[, ]+([\d.]+))?\)/);
                if (!m) return null;
                const alpha = m[4] === undefined ? 1 : parseFloat(m[4]);
                return alpha > 0.5 ? [+m[1], +m[2], +m[3]] : null;
              };
              const rgb = opaque(body) || opaque(de) || opaque(scrollerY)
                || opaque(body ? body.firstElementChild : null);
              if (rgb) {
                const luminance = 0.299 * rgb[0] + 0.587 * rgb[1] + 0.114 * rgb[2];
                de.style.colorScheme = luminance < 100 ? 'dark' : 'light';
              }
            })();
            """;

        core.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess)
            {
                return;
            }

            try
            {
                await core.ExecuteScriptAsync(script);
            }
            catch
            {
                // A page that refuses normalization still previews as-is.
            }
        };
    }

    /// <summary>
    /// One temp file per source: rapid A-B-A scrubbing must not read a file
    /// another preview just overwrote. The directory is pruned when it grows.
    /// </summary>
    internal static string TempHtmlPath(string folder, string sourcePath)
    {
        var htmlDir = AppPaths.InDataRoot(folder);
        Directory.CreateDirectory(htmlDir);
        try
        {
            PruneTempHtmlDirectory(htmlDir);
        }
        catch
        {
            // Pruning is best-effort housekeeping.
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(sourcePath.ToUpperInvariant())))[..16];
        return Path.Combine(htmlDir, hash + ".html");
    }

    internal static void PruneTempHtmlDirectory(string htmlDir)
    {
        const int maxFiles = 128;
        var keepNewerThan = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        var files = new DirectoryInfo(htmlDir)
            .EnumerateFiles("*.html")
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        var extraCount = files.Count - maxFiles;
        if (extraCount <= 0)
        {
            return;
        }

        foreach (var file in files)
        {
            if (extraCount <= 0)
            {
                return;
            }

            if (file.LastWriteTimeUtc > keepNewerThan)
            {
                continue;
            }

            try
            {
                file.Delete();
                extraCount--;
            }
            catch
            {
                // A file still in use by a live preview stays.
            }
        }
    }

    private static Uri WriteImageHtml(string imagePath)
    {
        var palette = Theme.Current;
        var background = $"#{palette.CanvasBg.R:X2}{palette.CanvasBg.G:X2}{palette.CanvasBg.B:X2}";
        var svgSize = FileKinds.IsSvg(imagePath) && SvgSizeProbe.TryReadCanvasSize(imagePath, out var canvasSize)
            ? canvasSize
            : Size.Empty;
        var svgWidth = svgSize.Width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var svgHeight = svgSize.Height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        // Key carries the full content recipe (v6 = hook handles arrow browse,
        // page listener removed); an existing file is already correct, and
        // skipping the rewrite avoids racing another window's navigation
        // read of the same file.
        var htmlPath = TempHtmlPath("ImageHtml", imagePath + background + $"|svg:{svgWidth}x{svgHeight}|v7");
        if (File.Exists(htmlPath))
        {
            return new Uri(htmlPath, UriKind.Absolute);
        }

        var source = WebUtility.HtmlEncode(new Uri(imagePath, UriKind.Absolute).AbsoluteUri);

        // Same gestures as the native image viewer: wheel zooms at the
        // cursor, drag pans, double-click toggles fit/actual. Zoom resizes
        // the img itself (not a transform) so SVG re-rasterizes crisp.
        var html = $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
html,body{margin:0;width:100%;height:100%;background:{{background}};overflow:hidden;}
html{animation:sp-reveal 160ms ease-out;}
@keyframes sp-reveal{from{opacity:0} }
#pic{position:absolute;left:0;top:0;transform-origin:0 0;cursor:grab;user-select:none;-webkit-user-drag:none;}
</style>
</head>
<body>
<img id="pic" src="{{source}}">
<script>
const pic = document.getElementById('pic');
let scale = 1, fit = 1, x = 0, y = 0, baseW = 0, baseH = 0, interacted = false;
const hintedW = {{svgWidth}};
const hintedH = {{svgHeight}};

// A percentage-sized SVG can report a browser default instead of its viewBox;
// prefer the probed canvas so the first fit sees the drawing's real shape.
// carries the right aspect, so measure that instead of collapsing to 0px.
function measureBase() {
  if (hintedW > 0 && hintedH > 0) {
    baseW = hintedW;
    baseH = hintedH;
    return;
  }

  baseW = pic.naturalWidth;
  baseH = pic.naturalHeight;
  if (!baseW || !baseH) {
    pic.style.width = '';
    pic.style.height = '';
    const box = pic.getBoundingClientRect();
    baseW = box.width || 300;
    baseH = box.height || 150;
  }
}

function layout() {
  pic.style.width = (baseW * scale) + 'px';
  pic.style.height = (baseH * scale) + 'px';
  pic.style.transform = `translate(${x}px, ${y}px)`;
}

function fitToWindow() {
  measureBase();
  fit = Math.min(window.innerWidth / baseW, window.innerHeight / baseH, 1);
  scale = fit;
  x = (window.innerWidth - baseW * scale) / 2;
  y = (window.innerHeight - baseH * scale) / 2;
  layout();
}

function zoomAt(cx, cy, next) {
  next = Math.max(Math.min(next, 40), Math.min(fit, 1) * 0.25);
  x = cx - (cx - x) * next / scale;
  y = cy - (cy - y) * next / scale;
  scale = next;
  interacted = true;
  layout();
}

pic.addEventListener('load', fitToWindow);
if (pic.complete) fitToWindow();
window.addEventListener('resize', () => { if (!interacted) fitToWindow(); });

window.addEventListener('wheel', e => {
  e.preventDefault();
  zoomAt(e.clientX, e.clientY, scale * (e.deltaY < 0 ? 1.15 : 1 / 1.15));
}, { passive: false });

window.addEventListener('dblclick', e => {
  if (Math.abs(scale - fit) < 0.001) {
    zoomAt(e.clientX, e.clientY, Math.max(1, fit * 2));
  } else {
    interacted = false;
    fitToWindow();
  }
});

let dragging = false, lastX = 0, lastY = 0;
window.addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  dragging = true;
  lastX = e.clientX; lastY = e.clientY;
  pic.style.cursor = 'grabbing';
  document.body.setPointerCapture(e.pointerId);
});
window.addEventListener('pointermove', e => {
  if (!dragging) return;
  x += e.clientX - lastX;
  y += e.clientY - lastY;
  lastX = e.clientX; lastY = e.clientY;
  interacted = true;
  layout();
});
window.addEventListener('pointerup', () => { dragging = false; pic.style.cursor = 'grab'; });
</script>
</body>
</html>
""";
        File.WriteAllText(htmlPath, html);
        return new Uri(htmlPath, UriKind.Absolute);
    }

    private FrameworkElement BuildPdfWebPreview(string path, long version, CancellationToken token)
    {
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.DeepBg) };

        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var background = System.Drawing.Color.FromArgb(palette.DeepBg.R, palette.DeepBg.G, palette.DeepBg.B);
        // FitH = fit the page to the window's width, the way PDF apps open
        // documents. The viewer's default fits the whole page instead, which
        // renders portrait documents at ~two-thirds size.
        var webView = CreateWebView(
            background,
            () => PdfViewerSource(path),
            $"pdf preview {path}",
            ex => { _ = PdfWebFallbackAsync(path, version, token); },
            configure: core => ConfigurePdfViewer(core, path),
            readinessMode: WebPaintReadinessMode.PdfPlugin);
        Grid.SetColumn(webView, 1);
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);
        grid.Children.Add(webView);
        var railState = new PdfPageRailState(railHost, railColumn, webView, path, version, token) { Surface = grid };
        grid.Tag = railState;
        TrackPdfRailLifetime(grid, railState);
        _ = LoadPdfPageRailAsync(railState);
        return grid;
    }

    /// <summary>
    /// The probe hook must bind at reveal, not at build: SetPreviewContent
    /// clears the OLD content after the new one is built, so a build-time
    /// assignment gets nulled immediately (the folder-hover lesson).
    /// </summary>
    private void TrackPdfRailLifetime(Grid grid, PdfPageRailState railState)
    {
        grid.Loaded += (_, _) =>
        {
            _activePdfRail = railState;
            StartPdfPageSync(railState);
        };
        grid.Unloaded += (_, _) =>
        {
            StopPdfPageSync(railState);
            if (ReferenceEquals(_activePdfRail, railState))
            {
                _activePdfRail = null;
            }
        };
    }

    private sealed class PdfPageRailState(
        ContentControl host,
        ColumnDefinition railColumn,
        WebView2 webView,
        string path,
        long version,
        CancellationToken token)
    {
        public ContentControl Host { get; } = host;
        public ColumnDefinition RailColumn { get; } = railColumn;
        public WebView2 WebView { get; set; } = webView;
        public Grid? Surface { get; set; }
        public bool JumpInFlight { get; set; }

        /// <summary>The page asked for while a jump was mid-sequence. Last one wins.</summary>
        public int? PendingJumpPage { get; set; }

        public int CurrentPage { get; set; } = 1;
        public string Path { get; set; } = path;
        public long Version { get; } = version;
        public CancellationToken Token { get; } = token;
        public bool IsVisible { get; set; } = true;
        public PdfPreviewDocument? Document { get; set; }
        public Func<PdfPageRailState, int, bool>? TryHandleJump { get; set; }
        public double RailVerticalOffset { get; set; }
        public bool RailNeedsSelectedPageScroll { get; set; }
        public Dictionary<int, Border> PageTiles { get; } = [];
        public Border? SelectedPageTile { get; set; }
        public ScrollViewer? PageRailScrollViewer { get; set; }
        public DispatcherTimer? PageSyncTimer { get; set; }
        public Task<PdfPageAutomationProbeResult>? PageSyncProbeTask { get; set; }
        public bool PageSyncInFlight { get; set; }
        public bool PageSyncDisabled { get; set; }
        public long PageSyncGeneration { get; set; }
        public string? LastPageSource { get; set; }
        public System.Windows.Automation.AutomationElement? PageAutomationElement { get; set; }
        public TaskCompletionSource<bool> FirstRailReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly record struct PdfPageAutomationProbeResult(
        int Page,
        System.Windows.Automation.AutomationElement? PageElement);

    private static ContentControl BuildPdfPageRailHost()
    {
        return new ContentControl
        {
            Width = 0,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
    }

    /// <summary>
    /// The rail is the truth the parser cannot always reach: the first page
    /// thumbnail is rendered display-oriented by the document engine, so its
    /// aspect IS the page's shape. This fires before the reveal gate opens, so
    /// a document whose header defeated the probe still reveals page-shaped;
    /// for probed documents the hint agrees and the hysteresis absorbs it. A
    /// hint that loses the race is dropped by the after-reveal rule.
    /// </summary>
    private void HintStageFromRailTruth(PdfPageRailState state, PdfPreviewDocument document)
    {
        if (document.Pages.Count == 0)
        {
            return;
        }

        var first = document.Pages[0];
        if (first.Width < 1 || first.Height < 1)
        {
            return;
        }

        SetStageFitHint(StageFitHint.PageAspect((double)first.Width / first.Height), state.Version);
    }

    // Direct PDFs use the same short-lived helper as converted Office PDFs.
    // A malformed or incompatible document can crash native PDFium, but it
    // must never take the resident preview process down with it.
    private Task LoadPdfPageRailAsync(PdfPageRailState state) =>
        LoadPdfPageRailOutOfProcessAsync(state);

    private Task LoadPdfPageRailOutOfProcessAsync(PdfPageRailState state) =>
        LoadPdfPageRailCoreAsync(
            state,
            (pageLimit, startPage) => TryRenderPdfThumbnailsOutOfProcessAsync(
                state.Path,
                pageLimit,
                state.Token,
                startPage),
            () => TryRenderPdfThumbnailsOutOfProcessAsync(
                state.Path,
                PdfPageRailMaxPages,
                state.Token),
            "pdf page rail helper");

    private async Task LoadPdfPageRailCoreAsync(
        PdfPageRailState state,
        Func<int, int, Task<PdfPreviewDocument?>> renderRange,
        Func<Task<PdfPreviewDocument?>> renderFallback,
        string diagnosticName)
    {
        try
        {
            var firstPages = await renderRange(PdfPageRailInitialPages, 0);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (firstPages is null || firstPages.TotalPages <= 1)
            {
                state.FirstRailReady.TrySetResult(true);
                HidePdfPageRail(state);
                return;
            }

            state.Document = firstPages;
            HintStageFromRailTruth(state, firstPages);
            RenderPdfPageRail(state);
            state.FirstRailReady.TrySetResult(true);

            if (firstPages.TotalPages <= firstPages.Pages.Count)
            {
                return;
            }

            var remainingPages = await renderRange(
                PdfPageRailMaxPages - firstPages.Pages.Count,
                firstPages.Pages.Count);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (remainingPages is null || remainingPages.TotalPages <= 1)
            {
                var allPages = await renderFallback();
                if (state.Version != _loadVersion
                    || state.Token.IsCancellationRequested
                    || allPages is null
                    || allPages.TotalPages <= 1)
                {
                    return;
                }

                state.Document = allPages;
            }
            else
            {
                state.Document = CombinePdfPreviewDocuments(firstPages, remainingPages);
            }

            RenderPdfPageRail(state);
        }
        catch (OperationCanceledException)
        {
            state.FirstRailReady.TrySetResult(true);
        }
        catch (Exception ex)
        {
            state.FirstRailReady.TrySetResult(true);
            DiagnosticLog.Write(
                $"{diagnosticName} failed {state.Path}: {ex.GetType().Name}: {ex.Message}");
            if (state.Version == _loadVersion)
            {
                HidePdfPageRail(state);
            }
        }
    }

    private static PdfPreviewDocument CombinePdfPreviewDocuments(PdfPreviewDocument first, PdfPreviewDocument remaining)
    {
        var pages = new List<PdfPreviewPage>(first.Pages.Count + remaining.Pages.Count);
        pages.AddRange(first.Pages);
        pages.AddRange(remaining.Pages);
        return new PdfPreviewDocument(pages, Math.Max(first.TotalPages, remaining.TotalPages));
    }

    private static async Task<PdfPreviewDocument?> TryRenderPdfThumbnailsOutOfProcessAsync(
        string path,
        int pageLimit,
        CancellationToken token,
        int startPage = 0)
    {
        var cacheDir = PdfRailCacheDirectory(path, pageLimit, startPage);
        var cached = await Task.Run(() => TryReadPdfThumbnailManifest(cacheDir), token);
        if (cached is not null)
        {
            return cached;
        }

        var helper = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(helper) || !File.Exists(helper))
        {
            return null;
        }

        var root = Path.GetDirectoryName(cacheDir) ?? AppPaths.InDataRoot("PdfRail");
        Directory.CreateDirectory(root);
        var tempDir = Path.Combine(root, ".render-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add("--render-pdf-thumbnails");
            startInfo.ArgumentList.Add(path);
            startInfo.ArgumentList.Add(tempDir);
            startInfo.ArgumentList.Add(pageLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add(startPage.ToString(System.Globalization.CultureInfo.InvariantCulture));

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // The helper rasters every page while Chromium is loading the
            // same document and the entrance is animating. It has until the
            // reveal gate; it must never win CPU from the frames the user is
            // watching.
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // A helper that already exited keeps its default priority.
            }

            try
            {
                await process.WaitForExitAsync(token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            if (process.ExitCode != 0)
            {
                DiagnosticLog.Write($"pdf thumbnail helper exit {process.ExitCode} for {path}");
                return null;
            }

            var rendered = await Task.Run(() => TryReadPdfThumbnailManifest(tempDir), token);
            if (rendered is null)
            {
                return null;
            }

            try
            {
                if (Directory.Exists(cacheDir))
                {
                    Directory.Delete(cacheDir, recursive: true);
                }

                Directory.Move(tempDir, cacheDir);
            }
            catch
            {
                // The live rail already loaded the images; cache reuse is a bonus.
            }

            return rendered;
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    private static string PdfRailCacheDirectory(string path, int pageLimit, int startPage)
    {
        var info = new FileInfo(path);
        var identity = $"{path.ToUpperInvariant()}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{startPage}|{pageLimit}|v2";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..20];
        return Path.Combine(
            AppPaths.DataRoot,
            "PdfRail",
            hash);
    }

    private static PdfPreviewDocument? TryReadPdfThumbnailManifest(string directory)
    {
        try
        {
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            var manifest = JsonSerializer.Deserialize<PdfThumbnailManifest>(File.ReadAllText(manifestPath));
            if (manifest is null || manifest.TotalPages <= 0)
            {
                return null;
            }

            if (manifest.TotalPages == 1)
            {
                return new PdfPreviewDocument([], 1);
            }

            var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var pages = new List<PdfPreviewPage>(manifest.Pages.Count);
            foreach (var page in manifest.Pages.OrderBy(page => page.Number))
            {
                var filePath = Path.GetFullPath(Path.Combine(directory, page.File));
                if (!filePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(filePath))
                {
                    return null;
                }

                pages.Add(new PdfPreviewPage(LoadBitmapFromFile(filePath), page.Width, page.Height));
            }

            return new PdfPreviewDocument(pages, manifest.TotalPages);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource LoadBitmapFromFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The helper already ended.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // A cache cleanup miss is harmless.
        }
    }

    /// <summary>
    /// The rail owns only the space it is actually using. Collapsed means a
    /// small handle, not a fake empty sidebar.
    /// </summary>
    private void RenderPdfPageRail(PdfPageRailState state, bool cloakViewer = false)
    {
        if (state.Document is not { TotalPages: > 1 } document)
        {
            HidePdfPageRail(state);
            return;
        }

        var targetWidth = state.IsVisible ? PdfPageRailWidth : PdfPageRailCollapsedWidth;
        state.RailColumn.Width = new GridLength(targetWidth);
        state.PageTiles.Clear();
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;

        var content = state.IsVisible
            ? BuildPdfPageRail(
                document,
                state.CurrentPage,
                state.RailVerticalOffset,
                offset => state.RailVerticalOffset = offset,
                page =>
                {
                    SetPdfCurrentPageFromViewer(state, page, logChange: false);
                    if (state.TryHandleJump?.Invoke(state, page) == true)
                    {
                        return;
                    }

                    JumpPdfToPage(state, page);
                },
                (page, tile, selected) =>
                {
                    state.PageTiles[page] = tile;
                    if (selected)
                    {
                        state.SelectedPageTile = tile;
                    }
                },
                scroll => state.PageRailScrollViewer = scroll,
                state.RailNeedsSelectedPageScroll,
                tile =>
                {
                    BringPdfPageTileIntoView(state, tile);
                    state.RailNeedsSelectedPageScroll = false;
                },
                () =>
                {
                    state.IsVisible = false;
                    RenderPdfPageRail(state, cloakViewer: true);
                })
            : BuildCollapsedRail(document.TotalPages, "Show pages", () =>
            {
                state.IsVisible = true;
                RenderPdfPageRail(state, cloakViewer: true);
            });

        if (cloakViewer)
        {
            CloakViewerForRailResize(state.WebView, state.Version);
        }

        AnimateRail(state.Host, targetWidth, content);
    }

    private static void AnimateRail(ContentControl host, double targetWidth, FrameworkElement? content)
    {
        host.BeginAnimation(FrameworkElement.WidthProperty, null);
        if (content is not null)
        {
            content.Opacity = 0;
            host.Content = content;
            content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(100))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else if (targetWidth < 1)
        {
            host.Content = null;
        }

        host.Width = targetWidth;
    }

    private void CloakViewerForRailResize(WebView2 webView, long version)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='none';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            return;
        }

        RunAfter(TimeSpan.FromMilliseconds(90), () =>
        {
            if (version != _loadVersion || webView.CoreWebView2 is not { } currentCore)
            {
                return;
            }

            try
            {
                _ = currentCore.ExecuteScriptAsync(
                    "document.documentElement.style.transition='opacity 120ms ease-out';"
                    + "document.documentElement.style.opacity='1';");
            }
            catch
            {
                // The resize itself is still valid; the script is just polish.
            }
        });
    }

    private void HidePdfPageRail(PdfPageRailState state)
    {
        state.PageTiles.Clear();
        state.SelectedPageTile = null;
        state.PageRailScrollViewer = null;
        state.RailColumn.Width = new GridLength(0);
        AnimateRail(state.Host, 0, null);
    }

    private void StartPdfPageSync(PdfPageRailState state)
    {
        if (state.PageSyncTimer is not null)
        {
            return;
        }

        state.PageSyncDisabled = false;
        state.LastPageSource = state.WebView.CoreWebView2?.Source;
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350)
        };
        timer.Tick += (_, _) => SyncPdfPageFromViewer(state);
        state.PageSyncTimer = timer;
        timer.Start();
    }

    private static void StopPdfPageSync(PdfPageRailState state)
    {
        state.PageSyncTimer?.Stop();
        state.PageSyncTimer = null;
        state.PageSyncGeneration++;
        state.PageAutomationElement = null;
    }

    private async void SyncPdfPageFromViewer(PdfPageRailState state)
    {
        if (state.JumpInFlight)
        {
            return;
        }

        if (state.Token.IsCancellationRequested || state.Version != _loadVersion || state.WebView.CoreWebView2 is not { } core)
        {
            StopPdfPageSync(state);
            return;
        }

        var ownsProbe = false;
        try
        {
            if (!string.Equals(core.Source, state.LastPageSource, StringComparison.Ordinal))
            {
                state.LastPageSource = core.Source;
                if (TryReadPdfPageFromSource(core.Source, out var sourcePage))
                {
                    SetPdfCurrentPageFromViewer(state, sourcePage);
                    return;
                }
            }

            if (state.PageSyncInFlight
                || state.PageSyncDisabled
                || state.PageSyncProbeTask is { IsCompleted: false })
            {
                return;
            }

            var totalPages = state.Document?.TotalPages ?? 0;
            var hwnd = TryGetPdfAutomationHwnd(state.WebView);
            if (totalPages <= 1 || hwnd == IntPtr.Zero)
            {
                return;
            }

            var generation = state.PageSyncGeneration;
            var cachedElement = state.PageAutomationElement;
            state.PageSyncInFlight = true;
            ownsProbe = true;

            // UI Automation crosses into Chromium's PDF provider and can
            // block while a large document is scrolling or zooming. Never
            // let that provider own the WPF dispatcher.
            var probeTask = Task.Run(() =>
            {
                try
                {
                    return TryReadPdfPageFromAutomation(hwnd, totalPages, cachedElement);
                }
                catch
                {
                    return default;
                }
            });
            state.PageSyncProbeTask = probeTask;

            var timeoutTask = Task.Delay(PdfPageAutomationTimeout);
            if (await Task.WhenAny(probeTask, timeoutTask) != probeTask)
            {
                // UIA calls cannot be cancelled safely. Keep the single
                // worker isolated and stop polling this viewer rather than
                // stacking more blocked probes behind it.
                if (generation == state.PageSyncGeneration
                    && state.Version == _loadVersion
                    && !state.Token.IsCancellationRequested)
                {
                    state.PageSyncDisabled = true;
                    DiagnosticLog.Write(
                        $"pdf page sync disabled after {PdfPageAutomationTimeout.TotalSeconds:0}s timeout");
                }

                return;
            }

            var result = await probeTask;
            if (generation != state.PageSyncGeneration
                || state.Version != _loadVersion
                || state.Token.IsCancellationRequested)
            {
                return;
            }

            state.PageAutomationElement = result.PageElement;
            if (result.Page > 0)
            {
                SetPdfCurrentPageFromViewer(state, result.Page);
            }
        }
        catch
        {
            state.PageAutomationElement = null;
        }
        finally
        {
            if (ownsProbe)
            {
                state.PageSyncInFlight = false;
            }
        }
    }

    private void SetPdfCurrentPageFromViewer(
        PdfPageRailState state,
        int page,
        bool logChange = true)
    {
        var target = Math.Max(1, page);
        if (state.Document is { TotalPages: > 0 } document)
        {
            target = Math.Min(target, document.TotalPages);
        }

        if (target == state.CurrentPage
            && state.SelectedPageTile is not null
            && state.PageTiles.TryGetValue(target, out var currentTile)
            && ReferenceEquals(state.SelectedPageTile, currentTile))
        {
            return;
        }

        var previousPage = state.CurrentPage;
        state.CurrentPage = target;
        if (logChange && target != previousPage)
        {
            DiagnosticLog.Write($"pdf rail sync -> page {target}");
        }
        if (!state.IsVisible || state.PageTiles.Count == 0)
        {
            if (target != previousPage)
            {
                state.RailNeedsSelectedPageScroll = true;
            }
            return;
        }

        var palette = Theme.Current;
        if (state.SelectedPageTile is not null)
        {
            ApplyRailTileState(state.SelectedPageTile, palette, selected: false);
        }

        state.SelectedPageTile = null;
        if (state.PageTiles.TryGetValue(target, out var tile))
        {
            state.SelectedPageTile = tile;
            ApplyRailTileState(tile, palette, selected: true);

            // A selection nobody can see reads as no selection at all. Keep
            // the highlighted tile in view when the document moves it - but
            // a pointer over the rail owns that scroll, never fight the hand.
            if (!state.Host.IsMouseOver)
            {
                BringPdfPageTileIntoView(state, tile);
            }

            state.RailNeedsSelectedPageScroll = false;
        }
        else
        {
            state.RailNeedsSelectedPageScroll = true;
        }
    }

    private static void BringPdfPageTileIntoView(PdfPageRailState state, FrameworkElement tile)
    {
        var scroll = state.PageRailScrollViewer;
        if (scroll is null || !scroll.IsLoaded || !tile.IsLoaded)
        {
            tile.BringIntoView();
            return;
        }

        try
        {
            var bounds = tile.TransformToAncestor(scroll).TransformBounds(
                new Rect(0, 0, tile.ActualWidth, tile.ActualHeight));
            var topBand = scroll.ViewportHeight * 0.25;
            var bottomBand = scroll.ViewportHeight * 0.75;
            if (bounds.Top >= topBand && bounds.Bottom <= bottomBand)
            {
                return;
            }

            var targetTop = scroll.ViewportHeight / 3;
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + bounds.Top - targetTop);
        }
        catch
        {
            tile.BringIntoView();
        }
    }

    private static PdfPageAutomationProbeResult TryReadPdfPageFromAutomation(
        IntPtr hwnd,
        int totalPages,
        System.Windows.Automation.AutomationElement? cachedElement)
    {
        if (totalPages <= 1)
        {
            return default;
        }

        if (TryReadPdfPageFromAutomationElement(cachedElement, totalPages, out var page))
        {
            return new PdfPageAutomationProbeResult(page, cachedElement);
        }

        var root = TryCreatePdfAutomationRoot(hwnd);
        if (root is null)
        {
            return default;
        }

        var condition = new System.Windows.Automation.OrCondition(
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Edit),
            new System.Windows.Automation.PropertyCondition(
                System.Windows.Automation.AutomationElement.ControlTypeProperty,
                System.Windows.Automation.ControlType.Spinner));

        System.Windows.Automation.AutomationElementCollection candidates;
        try
        {
            candidates = root.FindAll(System.Windows.Automation.TreeScope.Descendants, condition);
        }
        catch
        {
            return default;
        }

        foreach (System.Windows.Automation.AutomationElement candidate in candidates)
        {
            if (TryReadPdfPageFromAutomationElement(candidate, totalPages, out page))
            {
                return new PdfPageAutomationProbeResult(page, candidate);
            }
        }

        return default;
    }

    private IntPtr TryGetPdfAutomationHwnd(WebView2 webView)
    {
        try
        {
            return PresentationSource.FromVisual(webView) is HwndSource source
                ? source.Handle
                : new WindowInteropHelper(this).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    private static System.Windows.Automation.AutomationElement? TryCreatePdfAutomationRoot(IntPtr hwnd)
    {
        try
        {
            return hwnd == IntPtr.Zero
                ? null
                : System.Windows.Automation.AutomationElement.FromHandle(hwnd);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryReadPdfPageFromAutomationElement(
        System.Windows.Automation.AutomationElement? element,
        int totalPages,
        out int page)
    {
        page = 0;
        if (element is null)
        {
            return false;
        }

        try
        {
            if (element.TryGetCurrentPattern(
                    System.Windows.Automation.ValuePattern.Pattern,
                    out var valuePattern)
                && valuePattern is System.Windows.Automation.ValuePattern value
                && TryReadPdfAutomationPageValue(value.Current.Value, totalPages, out page))
            {
                return true;
            }

            if (element.TryGetCurrentPattern(
                    System.Windows.Automation.RangeValuePattern.Pattern,
                    out var rangePattern)
                && rangePattern is System.Windows.Automation.RangeValuePattern range)
            {
                var current = range.Current.Value;
                if (current >= 1
                    && current <= totalPages
                    && Math.Abs(current - Math.Round(current)) < 0.01)
                {
                    page = (int)Math.Round(current);
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static bool TryReadPdfAutomationPageValue(string? value, int totalPages, out int page)
    {
        page = 0;
        if (totalPages <= 1 || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && number >= 1
            && number <= totalPages)
        {
            page = number;
            return true;
        }

        return false;
    }

    internal static bool TryReadPdfPageFromSource(string? source, out int page)
    {
        page = 0;
        if (string.IsNullOrWhiteSpace(source) || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var fragment = uri.Fragment.TrimStart('#');
        foreach (var part in fragment.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length == 2
                && pair[0].Equals("page", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(pair[1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
                && value > 0)
            {
                page = value;
                return true;
            }
        }

        return false;
    }

    private sealed class EpubRailState(
        ContentControl host,
        ColumnDefinition railColumn,
        WebView2 webView,
        long version,
        CancellationToken token)
    {
        public ContentControl Host { get; } = host;
        public ColumnDefinition RailColumn { get; } = railColumn;
        public WebView2 WebView { get; } = webView;
        public long Version { get; } = version;
        public CancellationToken Token { get; } = token;
        public bool IsVisible { get; set; } = true;
        public int CurrentSpineIndex { get; set; } = -1;
        public double RailVerticalOffset { get; set; }
        public Dictionary<int, Border> ChapterTiles { get; } = [];
        public Border? SelectedChapterTile { get; set; }
        public IReadOnlyList<EpubPreviewLoader.EpubChapterEntry> Chapters { get; set; } =
            Array.Empty<EpubPreviewLoader.EpubChapterEntry>();
    }

    /// <summary>
    /// EPUB keeps the whole book as one continuous document; the rail is
    /// orientation, not pagination. Clicking a chapter scrolls the document
    /// to that chapter's anchor. Same idiom and handle as the PDF page rail.
    /// </summary>
    private FrameworkElement BuildEpubWebPreview(GeneratedHtmlDocument document, string path, CancellationToken token)
    {
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.ContentBg) };
        var railColumn = new ColumnDefinition { Width = new GridLength(0) };
        grid.ColumnDefinitions.Add(railColumn);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var webView = CreateWebView(
            document.Background,
            () => document.Source,
            $"epub preview {path}",
            ex => _ = ShowTextOrFileFallbackAsync(document.SourcePath),
            configure: core => HardenForGeneratedHtml(core, document),
            readinessMode: WebPaintReadinessMode.StaticLayout);
        Grid.SetColumn(webView, 1);
        var railHost = BuildPdfPageRailHost();
        Grid.SetColumn(railHost, 0);
        grid.Children.Add(railHost);
        grid.Children.Add(webView);
        var railState = new EpubRailState(railHost, railColumn, webView, _loadVersion, token);
        TrackEpubRailLifetime(grid, railState);
        _ = LoadEpubChapterRailAsync(railState, path);
        return grid;
    }

    /// <summary>Bind at reveal, not at build: the folder-hover lesson.</summary>
    private void TrackEpubRailLifetime(Grid grid, EpubRailState railState)
    {
        grid.Loaded += (_, _) => _activeEpubRail = railState;
        grid.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_activeEpubRail, railState))
            {
                _activeEpubRail = null;
            }
        };
    }

    private async Task LoadEpubChapterRailAsync(EpubRailState state, string path)
    {
        try
        {
            var chapters = await Task.Run(() => EpubPreviewLoader.TryGetChapters(path), state.Token);
            if (state.Version != _loadVersion || state.Token.IsCancellationRequested)
            {
                return;
            }

            if (chapters is null || chapters.Count <= 1)
            {
                HideEpubChapterRail(state);
                return;
            }

            state.Chapters = chapters;
            // Cloak on the first render too: if the document has already
            // revealed, the rail's arrival would otherwise reflow it naked.
            RenderEpubChapterRail(state, cloakViewer: true);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"epub rail failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void RenderEpubChapterRail(EpubRailState state, bool cloakViewer = false)
    {
        if (state.Chapters.Count <= 1)
        {
            HideEpubChapterRail(state);
            return;
        }

        var targetWidth = state.IsVisible ? EpubChapterRailWidth : PdfPageRailCollapsedWidth;
        state.RailColumn.Width = new GridLength(targetWidth);
        state.ChapterTiles.Clear();
        state.SelectedChapterTile = null;

        var content = state.IsVisible
            ? BuildEpubChapterRail(
                state.Chapters,
                state.CurrentSpineIndex,
                state.RailVerticalOffset,
                offset => state.RailVerticalOffset = offset,
                index => JumpEpubToChapter(state, index),
                (index, tile, selected) =>
                {
                    state.ChapterTiles[index] = tile;
                    if (selected)
                    {
                        state.SelectedChapterTile = tile;
                    }
                },
                () =>
                {
                    state.IsVisible = false;
                    RenderEpubChapterRail(state, cloakViewer: true);
                })
            : BuildCollapsedRail(state.Chapters.Count, "Show contents", () =>
            {
                state.IsVisible = true;
                RenderEpubChapterRail(state, cloakViewer: true);
            });

        if (cloakViewer)
        {
            CloakViewerForRailResize(state.WebView, state.Version);
        }

        AnimateRail(state.Host, targetWidth, content);
    }

    private void HideEpubChapterRail(EpubRailState state)
    {
        state.ChapterTiles.Clear();
        state.SelectedChapterTile = null;
        state.RailColumn.Width = new GridLength(0);
        AnimateRail(state.Host, 0, null);
    }

    private void JumpEpubToChapter(EpubRailState state, int spineIndex)
    {
        SetEpubCurrentChapter(state, spineIndex);
        if (state.WebView.CoreWebView2 is not { } core)
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync($"document.getElementById('ch-{spineIndex}')?.scrollIntoView()");
        }
        catch
        {
            // A disposed or navigating view drops the jump; the rail is intact.
        }
    }

    private static void SetEpubCurrentChapter(EpubRailState state, int spineIndex)
    {
        if (spineIndex == state.CurrentSpineIndex
            && state.SelectedChapterTile is not null
            && state.ChapterTiles.TryGetValue(spineIndex, out var currentTile)
            && ReferenceEquals(state.SelectedChapterTile, currentTile))
        {
            return;
        }

        state.CurrentSpineIndex = spineIndex;
        if (!state.IsVisible || state.ChapterTiles.Count == 0)
        {
            return;
        }

        var palette = Theme.Current;
        if (state.SelectedChapterTile is not null)
        {
            ApplyRailTileState(state.SelectedChapterTile, palette, selected: false);
        }

        state.SelectedChapterTile = null;
        if (state.ChapterTiles.TryGetValue(spineIndex, out var tile))
        {
            state.SelectedChapterTile = tile;
            ApplyRailTileState(tile, palette, selected: true);
        }
    }

    private static FrameworkElement BuildEpubChapterRail(
        IReadOnlyList<EpubPreviewLoader.EpubChapterEntry> chapters,
        int selectedSpineIndex,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<int> navigate,
        Action<int, Border, bool> registerTile,
        Action hide)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };

        var header = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Contents",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        var hideButton = BuildRailChromeButton("\uE76B", "Hide contents", hide);
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        foreach (var chapter in chapters)
        {
            var tile = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(8, 6, 8, 7),
                Margin = new Thickness(0, 0, 0, 2),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = chapter.Title,
                Child = new TextBlock
                {
                    Text = chapter.Title,
                    FontSize = 11.5,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            };

            ConfigureRailTile(
                tile,
                chapter.SpineIndex,
                chapter.SpineIndex == selectedSpineIndex,
                palette,
                navigate,
                registerTile);
            stack.Children.Add(tile);
        }

        return BuildRailSurface(header, stack, verticalOffset, rememberScroll);
    }

    private static void ConfigureRailTile(
        Border tile,
        int index,
        bool selected,
        ThemePalette palette,
        Action<int> navigate,
        Action<int, Border, bool> register)
    {
        tile.MouseEnter += (_, _) =>
        {
            if (!IsRailTileSelected(tile))
            {
                tile.Background = Theme.Brush(palette.HoverBg);
            }
        };
        tile.MouseLeave += (_, _) =>
        {
            if (!IsRailTileSelected(tile))
            {
                tile.Background = Brushes.Transparent;
            }
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            navigate(index);
            e.Handled = true;
        };

        if (selected)
        {
            ApplyRailTileState(tile, palette, selected: true);
        }

        register(index, tile, selected);
    }

    private static FrameworkElement BuildRailSurface(
        Grid header,
        StackPanel content,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<ScrollViewer>? registerScroll = null,
        Action<ScrollViewer>? onLoaded = null)
    {
        var palette = Theme.Current;
        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        StyleScrollViewer(scroll);
        registerScroll?.Invoke(scroll);
        scroll.ScrollChanged += (_, e) =>
        {
            if (e.VerticalChange != 0)
            {
                rememberScroll(e.VerticalOffset);
            }
        };
        scroll.Loaded += (_, _) =>
        {
            if (onLoaded is null)
            {
                scroll.ScrollToVerticalOffset(verticalOffset);
            }
            else
            {
                onLoaded(scroll);
            }
        };

        var surface = new Grid();
        surface.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        surface.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        surface.Children.Add(new Border
        {
            Background = Theme.Brush(Color.FromArgb(238, palette.HeaderBg.R, palette.HeaderBg.G, palette.HeaderBg.B)),
            Child = header
        });
        Grid.SetRow(scroll, 1);
        surface.Children.Add(scroll);

        return new Border
        {
            Background = Theme.Brush(Color.FromArgb(180, palette.HeaderBg.R, palette.HeaderBg.G, palette.HeaderBg.B)),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = surface
        };
    }

    private static FrameworkElement BuildCollapsedRail(int count, string tooltip, Action show)
    {
        var palette = Theme.Current;
        var button = BuildRailChromeButton("\uE76C", tooltip, show);
        return new Border
        {
            Background = Theme.Brush(Color.FromArgb(180, palette.HeaderBg.R, palette.HeaderBg.G, palette.HeaderBg.B)),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = new StackPanel
            {
                Margin = new Thickness(4, 12, 4, 0),
                Children =
                {
                    button,
                    new TextBlock
                    {
                        Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Margin = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextTertiary),
                        FontSize = 10
                    }
                }
            }
        };
    }

    private static Border BuildRailChromeButton(string glyph, string tooltip, Action action)
    {
        var palette = Theme.Current;
        var button = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = Metrics.IconCorner,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary)
            }
        };
        button.MouseEnter += (_, _) => button.Background = Theme.Brush(palette.HoverBg);
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += (_, e) =>
        {
            action();
            e.Handled = true;
        };
        return button;
    }

    private static void ApplyRailTileState(Border tile, ThemePalette palette, bool selected)
    {
        tile.Tag = selected;
        tile.Background = selected
            ? Theme.Brush(Color.FromArgb(60, palette.Accent.R, palette.Accent.G, palette.Accent.B))
            : Brushes.Transparent;
        tile.BorderBrush = selected
            ? Theme.Brush(Color.FromArgb(160, palette.Accent.R, palette.Accent.G, palette.Accent.B))
            : Brushes.Transparent;
    }

    private static bool IsRailTileSelected(Border tile) => tile.Tag is true;

    private static FrameworkElement BuildPdfPageRail(
        PdfPreviewDocument document,
        int selectedPage,
        double verticalOffset,
        Action<double> rememberScroll,
        Action<int> navigate,
        Action<int, Border, bool> registerTile,
        Action<ScrollViewer> registerScroll,
        bool alignSelectedOnLoad,
        Action<FrameworkElement> alignLoadedSelection,
        Action hide)
    {
        var palette = Theme.Current;
        var stack = new StackPanel { Margin = new Thickness(10, 0, 10, 12) };
        FrameworkElement? pageToRevealOnLoad = null;

        var header = new Grid { Margin = new Thickness(10, 10, 10, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock
        {
            Text = "Pages",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        });
        var hideButton = BuildRailChromeButton("\uE76B", "Hide pages", hide);
        Grid.SetColumn(hideButton, 1);
        header.Children.Add(hideButton);

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var pageNumber = i + 1;
            var page = document.Pages[i];
            var thumbnail = new Image
            {
                Source = page.Bitmap,
                Width = Math.Min(92, page.Width),
                Stretch = Stretch.Uniform,
                SnapsToDevicePixels = true
            };
            RenderOptions.SetBitmapScalingMode(thumbnail, BitmapScalingMode.HighQuality);

            var imageFrame = new Border
            {
                Background = Brushes.White,
                BorderBrush = Theme.Brush(Color.FromArgb(90, palette.Border.R, palette.Border.G, palette.Border.B)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(2),
                Child = thumbnail
            };

            var label = new TextBlock
            {
                Text = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Margin = new Thickness(0, 5, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = Theme.Brush(palette.TextSecondary),
                FontSize = 11
            };

            var tile = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = new Thickness(7),
                Margin = new Thickness(0, 0, 0, 8),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = $"Go to page {pageNumber}",
                Child = new StackPanel { Children = { imageFrame, label } }
            };

            var selected = pageNumber == selectedPage;
            ConfigureRailTile(tile, pageNumber, selected, palette, navigate, registerTile);
            if (selected)
            {
                pageToRevealOnLoad = tile;
            }

            stack.Children.Add(tile);
        }

        if (document.TotalPages > document.Pages.Count)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"First {document.Pages.Count} of {document.TotalPages}",
                TextAlignment = TextAlignment.Center,
                FontSize = 11,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 4, 4, 0)
            });
        }

        return BuildRailSurface(
            header,
            stack,
            verticalOffset,
            rememberScroll,
            registerScroll,
            scroll =>
            {
                if (!alignSelectedOnLoad || pageToRevealOnLoad is not { } tile)
                {
                    scroll.ScrollToVerticalOffset(verticalOffset);
                    return;
                }

                // The saved offset is stale when the viewer moved while the
                // rail was hidden; reopen around the current page instead.
                _ = scroll.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(() => alignLoadedSelection(tile)));
            });
    }

    private void JumpPdfToPage(PdfPageRailState state, int page) =>
        _ = JumpPdfToPageAsync(state, page);

    /// <summary>
    /// The PDF viewer's own page box jumps instantly, so our rail clicks the
    /// box and types the target page instead of reloading the document. If
    /// WebView stops honoring that path, we fall back to the staged reload
    /// path that kept the previous page visible.
    /// </summary>
    private async Task JumpPdfToPageAsync(PdfPageRailState state, int page)
    {
        if (state.JumpInFlight)
        {
            // The jump types into the viewer's page box, so two sequences
            // cannot overlap without typing over each other. Hold the click
            // rather than dropping it: the rail has already moved its
            // highlight there, and a rail pointing at a page the document
            // never went to is worse than a wait.
            state.PendingJumpPage = page;
            return;
        }

        state.JumpInFlight = true;
        var target = Math.Max(1, page);
        state.CurrentPage = target;
        try
        {
            if (state.Version != _loadVersion || state.WebView.CoreWebView2 is not { } core)
            {
                return;
            }

            // The synthetic click aims at where the page box sits rather than
            // at the box itself, so it can miss for reasons this code cannot
            // see: a runtime that moves the toolbar, a narrow stage, a page
            // count wide enough to change the control's width. A timeout keeps
            // a wedged call from holding the lock, and the viewer is asked
            // afterwards where it actually landed.
            await JumpPdfWithNativePageBoxAsync(state.WebView, core, target)
                .WaitAsync(PdfPageAutomationTimeout);

            if (await LandedOnPdfPageAsync(state, target))
            {
                DiagnosticLog.Write($"pdf rail native jump -> page {target}");
            }
            else
            {
                DiagnosticLog.Write($"pdf rail native jump missed page {target}, staging reload");
                StagePdfViewerJump(state, target);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf rail native jump failed: {ex.GetType().Name}: {ex.Message}");
            StagePdfViewerJump(state, target);
        }
        finally
        {
            RunAfter(TimeSpan.FromMilliseconds(650), () =>
            {
                state.JumpInFlight = false;
                if (state.PendingJumpPage is { } pending)
                {
                    state.PendingJumpPage = null;
                    JumpPdfToPage(state, pending);
                }
            });
        }
    }

    /// <summary>
    /// Asks the viewer, through its own page control, whether the jump landed.
    /// Unknown counts as landed: the probe is a check on the jump, not a
    /// second gate, and a viewer that will not answer must not cost the user a
    /// document reload. UI Automation reaches into Chromium's PDF provider and
    /// can block, so it runs off the dispatcher under the same timeout the
    /// page sync uses.
    /// </summary>
    private async Task<bool> LandedOnPdfPageAsync(PdfPageRailState state, int target)
    {
        if (state.Document is not { TotalPages: > 1 } document)
        {
            return true;
        }

        var hwnd = TryGetPdfAutomationHwnd(state.WebView);
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        var element = state.PageAutomationElement;
        var probe = Task.Run(() =>
        {
            try
            {
                return TryReadPdfPageFromAutomation(hwnd, document.TotalPages, element);
            }
            catch
            {
                return default;
            }
        });

        if (await Task.WhenAny(probe, Task.Delay(PdfPageAutomationTimeout)) != probe)
        {
            return true;
        }

        var result = await probe;
        if (result.PageElement is not null)
        {
            state.PageAutomationElement = result.PageElement;
        }

        return result.Page <= 0 || result.Page == target;
    }

    /// <summary>
    /// Clicks the viewer's own page box and types the target. The offset is
    /// measured against the toolbar set ConfigurePdfViewer keeps: show or hide
    /// one more item and the box slides out from under this click, after which
    /// the Ctrl+A selects the document instead of the box. Change one, retest
    /// the other.
    /// </summary>
    private static async Task JumpPdfWithNativePageBoxAsync(WebView2 webView, CoreWebView2 core, int page)
    {
        var x = Math.Max(160, webView.ActualWidth / 2 + 15);
        await DispatchMouseClickAsync(core, x, 20);
        await Task.Delay(35);
        await DispatchModifiedKeyAsync(core, 65, "KeyA", "a", modifiers: 2);
        await Task.Delay(20);
        await core.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text = page.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        await DispatchPlainKeyAsync(core, 13, "Enter", "Enter");
    }

    private async Task CompleteProgressiveOfficeConversionAsync(
        string sourcePath,
        int partialPageCount,
        PdfPageRailState railState,
        Action<ConvertedOfficeDocument> setFullDocument)
    {
        var started = DateTime.UtcNow;
        try
        {
            var full = await Task.Run(() => OfficeDocumentPdfConverter.TryConvert(sourcePath, railState.Token, background: true), railState.Token);
            if (full is null
                || railState.Version != _loadVersion
                || railState.Token.IsCancellationRequested)
            {
                return;
            }

            setFullDocument(full);
            DiagnosticLog.Write(
                $"office progressive full ready {sourcePath} +{(DateTime.UtcNow - started).TotalMilliseconds:0}ms partial={partialPageCount}");

            if (_userEngagedContent)
            {
                DiagnosticLog.Write($"office progressive upgrade -> none {sourcePath}");
                railState.Path = full.DocumentPath;
                railState.Document = null;
                _ = LoadPdfPageRailOutOfProcessAsync(railState);
                return;
            }

            DiagnosticLog.Write($"office progressive upgrade -> auto {sourcePath}");
            UpgradeConvertedOfficePreview(railState, full, Math.Max(1, railState.CurrentPage));
        }
        catch (OperationCanceledException)
        {
            // Preview moved on.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"office progressive full failed {sourcePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpgradeConvertedOfficePreview(
        PdfPageRailState state,
        ConvertedOfficeDocument fullDocument,
        int targetPage)
    {
        if (state.Version != _loadVersion
            || state.Token.IsCancellationRequested
            || string.Equals(state.Path, fullDocument.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var target = Math.Max(1, targetPage);
        StagePdfViewerReplacement(
            state,
            () => PdfViewerSource(fullDocument.DocumentPath, target),
            fullDocument.DocumentPath,
            $"office progressive upgrade {fullDocument.DocumentPath} -> {target}",
            "office upgrade",
            () =>
            {
                state.Path = fullDocument.DocumentPath;
                state.CurrentPage = target;
                state.Document = null;
                state.TryHandleJump = null;
                _ = LoadPdfPageRailOutOfProcessAsync(state);
            });
    }

    /// <summary>
    /// Fallback only. It is slower, but it never leaves a blank page behind:
    /// the old viewer stays visible until the new one has loaded.
    /// </summary>
    private void StagePdfViewerJump(PdfPageRailState state, int target)
    {
        var uri = new Uri(
            $"{new Uri(state.Path, UriKind.Absolute).AbsoluteUri}?jump={target}#page={target}&view=FitH",
            UriKind.Absolute);
        StagePdfViewerReplacement(
            state,
            () => uri,
            state.Path,
            $"pdf jump {state.Path} -> {target}",
            "pdf jump");
    }

    /// <summary>Keeps the current PDF visible until its owned replacement has painted.</summary>
    private void StagePdfViewerReplacement(
        PdfPageRailState state,
        Func<Uri> source,
        string pdfPath,
        string navigationLabel,
        string paintLabel,
        Action? onSwapped = null)
    {
        if (state.Surface is not { } surface)
        {
            return;
        }

        var palette = Theme.Current;
        var background = System.Drawing.Color.FromArgb(
            palette.DeepBg.R,
            palette.DeepBg.G,
            palette.DeepBg.B);
        var fresh = CreateWebView(
            background,
            source,
            navigationLabel,
            onFailed: null,
            configure: core => ConfigurePdfViewer(core, pdfPath),
            trackAsPreviewDisposable: false,
            readinessMode: WebPaintReadinessMode.PdfPlugin);
        Grid.SetColumn(fresh, 1);
        fresh.Visibility = Visibility.Hidden;
        surface.Children.Add(fresh);

        var finished = false;
        bool FreshIsOwned() =>
            state.Version == _loadVersion
            && !state.Token.IsCancellationRequested
            && ReferenceEquals(fresh.Parent, surface);

        void DiscardFresh()
        {
            finished = true;
            if (ReferenceEquals(fresh.Parent, surface))
            {
                surface.Children.Remove(fresh);
            }

            fresh.Dispose();
        }

        void Swap()
        {
            if (finished)
            {
                return;
            }

            finished = true;
            var old = state.WebView;
            if (state.Version != _loadVersion || !ReferenceEquals(old.Parent, surface))
            {
                DiscardFresh();
                return;
            }

            fresh.Visibility = Visibility.Visible;
            fresh.UpdateLayout();
            old.Visibility = Visibility.Collapsed;
            surface.Children.Remove(old);
            if (ReferenceEquals(_currentDisposable, old))
            {
                _currentDisposable = fresh;
            }

            old.Dispose();
            state.WebView = fresh;
            state.PageSyncGeneration++;
            state.PageSyncDisabled = false;
            state.PageAutomationElement = null;
            onSwapped?.Invoke();
        }

        var paintAttemptStarted = false;
        fresh.NavigationCompleted += async (_, args) =>
        {
            if (!args.IsSuccess || finished || paintAttemptStarted)
            {
                return;
            }

            if (!FreshIsOwned())
            {
                DiscardFresh();
                return;
            }

            paintAttemptStarted = true;
            var paintClock = Stopwatch.StartNew();
            var readinessMode = PaintReadinessMode(fresh);
            var paintReady = await WaitForWebPaintReadyAsync(
                fresh,
                PdfPluginSettleDelay,
                state.Token,
                readinessMode);
            if (finished)
            {
                return;
            }

            if (!FreshIsOwned())
            {
                DiscardFresh();
                return;
            }

            DiagnosticLog.Write(
                $"{paintLabel} paint gate ready={paintReady} wait={paintClock.Elapsed.TotalMilliseconds:0}ms mode={PaintReadinessModeName(readinessMode)}");
            Swap();
        };
        RunAfter(TimeSpan.FromMilliseconds(4000), () =>
        {
            if (!paintAttemptStarted)
            {
                Swap();
            }
        });
    }

    private static void OpenExternalUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch
        {
            // The shell owns this handoff; the preview stays put either way.
        }
    }

    private async Task PdfWebFallbackAsync(string path, long version, CancellationToken token)
    {
        try
        {
            var content = await BuildPdfViaPdfiumAsync(path, token);
            if (version != _loadVersion)
            {
                return;
            }

            SetPreviewContent(content, null, animate: false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer preview; nothing to render.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"pdf fallback failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<FrameworkElement> BuildPdfViaPdfiumAsync(string path, CancellationToken token)
    {
        var pdf = await Task.Run(() => PdfPreviewRenderer.TryRender(path, token), token);
        return pdf is null
            ? BuildErrorPreview("Glimpse can't preview this PDF", "It may be damaged or in use.")
            : BuildPdfPreview(pdf);
    }

    private FrameworkElement BuildConvertedOfficePreview(ConvertedOfficeDocument document, long version, CancellationToken token)
    {
        // Spreadsheets convert to HTML (screen-faithful, wide sheets scroll);
        // everything else converts to PDF for the Chromium viewer. Converted
        // HTML is treated like a user's HTML file: no scripts, no
        // network.
        // Both shapes sit on the same deep canvas: the PDF viewer draws its
        // own page there, and the exported spreadsheet HTML carries a
        // matching canvas in its stylesheet.
        var isHtml = document.DocumentPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
        var palette = Theme.Current;
        var grid = new Grid { Background = Theme.Brush(palette.DeepBg) };
        ColumnDefinition? railColumn = null;
        if (!isHtml)
        {
            railColumn = new ColumnDefinition { Width = new GridLength(0) };
            grid.ColumnDefinitions.Add(railColumn);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var background = System.Drawing.Color.FromArgb(palette.DeepBg.R, palette.DeepBg.G, palette.DeepBg.B);
        var initialPath = isHtml && document.Sheets is { Count: > 1 } tabbedSheets
            ? tabbedSheets[0].Path
            : document.DocumentPath;
        if (!isHtml
            && PdfPageSizeProbe.TryReadFirstPageSize(initialPath, out var pageWidth, out var pageHeight))
        {
            SetStageFitHint(StageFitHint.PageAspect(pageWidth / pageHeight), version);
        }

        var webView = CreateWebView(
            background,
            () => isHtml ? new Uri(initialPath, UriKind.Absolute) : PdfViewerSource(initialPath),
            $"converted document {document.DocumentPath}",
            _ =>
            {
                if (version == _loadVersion)
                {
                    SetPreviewContent(BuildErrorPreview("Glimpse can't preview this document", "The converted document could not be displayed."), null, animate: false);
                }
            },
            configure: core =>
            {
                if (isHtml)
                {
                    HardenForUntrustedHtml(core);
                }
                else
                {
                    ConfigurePdfViewer(core, initialPath);
                }
            },
            readinessMode: isHtml
                ? WebPaintReadinessMode.StaticLayout
                : WebPaintReadinessMode.PdfPlugin);

        // Multi-sheet workbooks get a bottom tab strip - the native language
        // of spreadsheets - one page per sheet, instead of the export's
        // stacked tables and "Overview" link block.
        if (!isHtml)
        {
            Grid.SetColumn(webView, 1);
            var railHost = BuildPdfPageRailHost();
            Grid.SetColumn(railHost, 0);
            grid.Children.Add(railHost);
            grid.Children.Add(webView);
            // Converted Office PDFs use the same rail, but their thumbnails
            // render in a short helper process. If pdfium dislikes a
            // LibreOffice-generated PDF, the helper dies instead of the
            // resident preview process.
            var railState = new PdfPageRailState(railHost, railColumn!, webView, initialPath, version, token) { Surface = grid };
            grid.Tag = railState;
            TrackPdfRailLifetime(grid, railState);

            if (document.IsPartial
                && document.SourcePath is { } sourcePath
                && document.PartialPageCount is { } partialPageCount)
            {
                ConvertedOfficeDocument? fullDocument = null;
                railState.TryHandleJump = (state, page) =>
                {
                    if (fullDocument is null
                        || page <= partialPageCount
                        || string.Equals(state.Path, fullDocument.DocumentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    DiagnosticLog.Write($"office progressive upgrade -> jump page {page} {sourcePath}");
                    UpgradeConvertedOfficePreview(state, fullDocument, page);
                    return true;
                };

                grid.Unloaded += (_, _) =>
                {
                    var directory = Path.GetDirectoryName(document.DocumentPath);
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        OfficeDocumentPdfConverter.TryDeleteDirectory(directory);
                    }
                };

                _ = CompleteProgressiveOfficeConversionAsync(
                    sourcePath,
                    partialPageCount,
                    railState,
                    full => fullDocument = full);
            }

            _ = LoadPdfPageRailOutOfProcessAsync(railState);
        }
        else if (document.Sheets is { Count: > 1 } sheets)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.Children.Add(webView);
            var tabBar = BuildSheetTabBar(sheets, webView);
            Grid.SetRow(tabBar, 1);
            grid.Children.Add(tabBar);
        }
        else
        {
            grid.Children.Add(webView);
        }

        if (isHtml)
        {
            // Converted spreadsheets have no viewer chrome of their own (the
            // PDF path does); the header uses them zoom controls.
            grid.Tag = ZoomableContentTag;
        }

        return grid;
    }

    /// <summary>
    /// Excel-style sheet tabs along the bottom: plain text buttons, the
    /// active one raised on a surface pill. Only built when a workbook has
    /// more than one sheet.
    /// </summary>
    private static FrameworkElement BuildSheetTabBar(IReadOnlyList<SpreadsheetSheet> sheets, WebView2 webView)
    {
        var palette = Theme.Current;
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 5, 10, 6) };
        var tabs = new List<(Border Chrome, TextBlock Label)>();
        var activeIndex = 0;

        void RefreshTabs()
        {
            for (var i = 0; i < tabs.Count; i++)
            {
                var active = i == activeIndex;
                tabs[i].Chrome.Background = active ? Theme.Brush(palette.SurfaceBg) : Brushes.Transparent;
                tabs[i].Chrome.BorderBrush = active ? Theme.Brush(palette.SurfaceBorder) : Brushes.Transparent;
                tabs[i].Label.Foreground = Theme.Brush(active ? palette.TextPrimary : palette.TextSecondary);
                tabs[i].Label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }

        for (var i = 0; i < sheets.Count; i++)
        {
            var index = i;
            var label = new TextBlock
            {
                Text = sheets[i].Name,
                FontSize = 12,
                MaxWidth = 180,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var chrome = new Border
            {
                CornerRadius = Metrics.RowCorner,
                Padding = Metrics.ChipPadding,
                Margin = new Thickness(0, 0, 4, 0),
                BorderThickness = new Thickness(1),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = label
            };
            chrome.MouseEnter += (_, _) =>
            {
                if (index != activeIndex)
                {
                    chrome.Background = Theme.Brush(palette.HoverBg);
                }
            };
            chrome.MouseLeave += (_, _) => RefreshTabs();
            chrome.MouseLeftButtonDown += (_, e) => e.Handled = true;
            chrome.MouseLeftButtonUp += (_, e) =>
            {
                if (index != activeIndex)
                {
                    activeIndex = index;
                    // WebView keeps the outgoing surface until navigation
                    // commits. A fire-and-forget script command can instead
                    // arrive in the incoming document and leave it blank.
                    webView.Source = new Uri(sheets[index].Path, UriKind.Absolute);
                    RefreshTabs();
                }

                e.Handled = true;
            };
            strip.Children.Add(chrome);
            tabs.Add((chrome, label));
        }

        RefreshTabs();

        var scroller = new ScrollViewer
        {
            Content = strip,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        StyleScrollViewer(scroller);

        return new Border
        {
            Background = Theme.Brush(palette.HeaderBg),
            BorderBrush = Theme.Brush(palette.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = scroller
        };
    }

    /// <summary>
    /// Video reveal needs its shape before it appears. MP4 headers normally
    /// provide that shape up front; formats the probe cannot read wait for
    /// loadedmetadata instead, so a phone video never reveals sideways-boxed.
    /// </summary>
    private sealed class MediaStageState
    {
        public MediaStageState(bool dimensionsKnown)
        {
            DimensionsKnown = dimensionsKnown;
            if (dimensionsKnown)
            {
                DimsReady.TrySetResult(true);
            }
        }

        public bool DimensionsKnown { get; }

        public TaskCompletionSource<bool> DimsReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private FrameworkElement BuildMediaPreview(string path, long version, CancellationToken token)
    {
        var grid = new Grid { Background = Brushes.Black };
        if (!FileKinds.IsAudio(path))
        {
            var dimensionsKnown = _stageFitVersion == version
                && _stageFitHint is { Kind: StageFitHint.FitKind.Pixels };
            grid.Tag = new MediaStageState(dimensionsKnown);
        }
        var webView = CreateWebView(
            System.Drawing.Color.Black,
            () => WriteMediaHtml(path),
            $"web media {path}",
            _ =>
            {
                if (IsCurrentContentPath(path))
                {
                    SetPreviewContent(BuildErrorPreview("Glimpse can't play this file", MediaOpenInsteadText(path)), null, animate: false);
                }
            },
            configure: core =>
            {
                core.WebMessageReceived += (_, args) =>
                    OnMediaMessage(path, version, token, args.WebMessageAsJson);

                // The video template hides the native fullscreen control, but
                // still handle browser fullscreen if a runtime exposes another
                // route into it.
                core.ContainsFullScreenElementChanged += (_, _) =>
                {
                    if (token.IsCancellationRequested || version != _loadVersion || !IsCurrentContentPath(path))
                    {
                        return;
                    }

                    var fullscreen = core.ContainsFullScreenElement;
                    _escapeYieldsToContent = fullscreen;
                    if (fullscreen != IsStageMaximized)
                    {
                        ToggleMaximize();
                        // Browsing away mid-fullscreen would otherwise leave
                        // the maximize latched with no one to release it.
                        _contentDrivenMaximize = fullscreen;
                    }
                };
            });
        grid.Children.Add(webView);
        return grid;
    }

    /// <summary>
    /// The player reports volume moves; the setting follows so the next
    /// preview opens at whatever loudness the user last chose.
    /// </summary>
    private void OnMediaMessage(string path, long version, CancellationToken token, string json)
    {
        if (token.IsCancellationRequested || version != _loadVersion || !IsCurrentContentPath(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (document.RootElement.TryGetProperty("dims", out var dimsElement)
                && dimsElement.ValueKind == JsonValueKind.Object)
            {
                if (IsCurrentContentPath(path)
                    && dimsElement.TryGetProperty("w", out var widthElement)
                    && dimsElement.TryGetProperty("h", out var heightElement)
                    && widthElement.TryGetDouble(out var videoWidth)
                    && heightElement.TryGetDouble(out var videoHeight)
                    && videoWidth >= 8 && videoHeight >= 8)
                {
                    // Chromium reports display dimensions with the rotation
                    // matrix already applied, so portrait phone video
                    // arrives portrait here.
                    SetStageFitHint(StageFitHint.Pixels(videoWidth, videoHeight), _loadVersion);
                }

                // Zero dims (audio-only in a video container) still release
                // the gate; the default stage is the right answer for them.
                if (PreviewContent.Content is FrameworkElement mediaContent
                    && mediaContent.Tag is MediaStageState mediaStage)
                {
                    mediaStage.DimsReady.TrySetResult(true);
                }

                return;
            }

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                var detail = errorElement.ValueKind switch
                {
                    JsonValueKind.String => errorElement.GetString(),
                    JsonValueKind.Number => errorElement.GetRawText(),
                    _ => "decode"
                };
                var reason = document.RootElement.TryGetProperty("reason", out var reasonElement)
                    && reasonElement.ValueKind == JsonValueKind.String
                        ? reasonElement.GetString()
                        : null;
                var codecs = Mp4CodecSniffer.TryRead(path);
                var codecText = codecs is null ? string.Empty : $" codecs {codecs.DiagnosticText}";
                DiagnosticLog.Write($"media decode failed {path}: {detail}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" {reason}")}{codecText}");
                if (IsCurrentContentPath(path))
                {
                    SetPreviewContent(
                        BuildMediaFailurePreview(path, codecs),
                        null,
                        animate: false);
                }

                return;
            }

            if (!document.RootElement.TryGetProperty("volume", out var volumeElement)
                || !volumeElement.TryGetDouble(out var volume))
            {
                return;
            }

            volume = Math.Clamp(volume, 0, 1);
            var muted = document.RootElement.TryGetProperty("muted", out var mutedElement) && mutedElement.ValueKind == JsonValueKind.True;
            if (Math.Abs(Settings.Current.MediaVolume - volume) < 0.005 && Settings.Current.MediaMuted == muted)
            {
                return;
            }

            Settings.Update(settings => settings with { MediaVolume = volume, MediaMuted = muted });
            DiagnosticLog.Write($"media volume saved {volume:0.00} muted={muted}");
        }
        catch
        {
            // A malformed message must not break the preview.
        }
    }

    private async Task ShowFileCardFallbackAsync(string path)
    {
        var version = _loadVersion;
        var token = _loadCancellation?.Token ?? CancellationToken.None;
        if (!OwnsCurrentLoad(path, version, token))
        {
            return;
        }

        var card = await BuildFileCardAsync(path);
        if (OwnsCurrentLoad(path, version, token))
        {
            SetPreviewContent(card, null, animate: false);
        }
    }

    private async Task ShowTextOrFileFallbackAsync(string path)
    {
        var version = _loadVersion;
        var token = _loadCancellation?.Token ?? CancellationToken.None;
        if (!OwnsCurrentLoad(path, version, token))
        {
            return;
        }

        var text = await Task.Run(() => LoadFormattedText(path));
        if (!OwnsCurrentLoad(path, version, token))
        {
            return;
        }

        FrameworkElement content;
        if (text is null)
        {
            content = await BuildFileCardAsync(path);
            if (!OwnsCurrentLoad(path, version, token))
            {
                return;
            }
        }
        else
        {
            content = BuildTextPreview(new TextPreview(text.Value.Text, Truncated: false, TotalBytes: 0), text.Value.Wrap);
        }

        SetPreviewContent(content, null, animate: false);
    }

    private bool OwnsCurrentLoad(string contentPath, long version, CancellationToken token) =>
        IsLoadIdentityCurrent(version, _loadVersion, token.IsCancellationRequested)
        && IsCurrentContentPath(contentPath);

    internal static bool IsLoadIdentityCurrent(long requestVersion, long currentVersion, bool cancellationRequested) =>
        !cancellationRequested && requestVersion == currentVersion;

    // The player is dark glass in both themes, so this template does not
    // follow the app theme; its hex values mirror Theme.cs Dark (TextPrimary,
    // TextSecondary, TextTertiary, Accent). If those tokens move, move these.
    private static Uri WriteMediaHtml(string mediaPath)
    {
        var source = WebUtility.HtmlEncode(new Uri(mediaPath, UriKind.Absolute).AbsoluteUri);
        // Settings are user-editable; an out-of-range value would abort page setup.
        var savedVolume = Settings.Current.MediaVolume;
        var safeVolume = double.IsFinite(savedVolume) ? Math.Clamp(savedVolume, 0, 1) : 0.75;
        var volume = safeVolume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var muted = Settings.Current.MediaMuted ? "true" : "false";
        var stamp = File.GetLastWriteTimeUtc(mediaPath).Ticks;
        // Include presentation state and template version so parallel previews
        // can reuse a complete page without rewriting it under another WebView.
        var htmlPath = TempHtmlPath("MediaHtml", $"{mediaPath}|{volume}|{muted}|{stamp}|v20");
        if (File.Exists(htmlPath))
        {
            return new Uri(htmlPath, UriKind.Absolute);
        }

        var isAudio = FileKinds.IsAudio(mediaPath);
        var modeClass = isAudio ? "audio" : "video";
        var isVideoLiteral = isAudio ? "false" : "true";
        string bodyHtml;

        if (isAudio)
        {
            ShellMediaProperties.TryGetAudioMetadata(mediaPath, out var audio);
            var title = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(audio.Title)
                ? Path.GetFileNameWithoutExtension(mediaPath)
                : audio.Title!.Trim());
            var line2 = string.Join(" · ", new[] { audio.DisplayArtist, audio.AlbumTitle }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => WebUtility.HtmlEncode(part!.Trim())));
            var line2Html = line2.Length == 0 ? "" : $"""<div id="line2">{line2}</div>""";
            var artUri = ShellThumbnailProvider.TryWriteThumbnailPng(mediaPath, $"{mediaPath}|{stamp}");
            var artHtml = artUri is null
                ? """<div id="tile"><span class="note">&#9835;</span><span class="spark">&#10022;</span></div>"""
                : $"""<img id="art" src="{WebUtility.HtmlEncode(artUri.AbsoluteUri)}" alt="">""";
            var durationText = audio.Duration is { } known
                ? known.TotalHours >= 1
                    ? $"{(int)known.TotalHours}:{known.Minutes:00}:{known.Seconds:00}"
                    : $"{(int)known.TotalMinutes}:{known.Seconds:00}"
                : "--:--";

            bodyHtml = $$"""
<div id="wrap">
{{artHtml}}
<div id="title">{{title}}</div>
{{line2Html}}
{{BuildMediaTransportHtml(durationText)}}
</div>
<audio id="media" src="{{source}}" autoplay></audio>
""";
        }
        else
        {
            bodyHtml = $$"""
<video id="media" src="{{source}}" autoplay playsinline disablepictureinpicture></video>
<div id="bar">
{{BuildMediaTransportHtml("--:--")}}
</div>
<div id="blocked">Click play to start</div>
""";
        }

        var html = $$"""
<!doctype html>
<html class="{{modeClass}}">
<head>
<meta charset="utf-8">
<style>
:root{color-scheme:dark;}
html.audio{
  --page-bg:radial-gradient(120% 120% at 50% 0%,#1d1f26 0%,#0c0d11 70%);
  --time-color:#8f95a0;
  --time-shadow:none;
  --seek-rest:rgba(255,255,255,.14);
  --seek-shadow:rgba(0,0,0,.5);
  --button-color:#c7cbd4;
  --button-hover-bg:rgba(255,255,255,.07);
  --button-hover-color:#f4f4f5;
  --button-active-bg:rgba(255,255,255,.12);
  --button-gap:22px;
  --skip-size:44px;
  --play-size:58px;
  --play-bg:rgba(255,255,255,.08);
  --play-hover-bg:rgba(255,255,255,.15);
  --mute-size:34px;
  --mute-radius:9px;
  --volume-width:92px;
  --volume-fill:#a9adb8;
  --volume-rest:rgba(255,255,255,.14);
  --volume-thumb:#e6e8ee;
  --scrub-margin:30px;
  --controls-margin:12px;
  --skip-icon:26px;
  --play-icon:24px;
  --volume-icon:18px;
}
html.video{
  --page-bg:#000;
  --time-color:#cdd1da;
  --time-shadow:0 1px 2px rgba(0,0,0,.7);
  --seek-rest:rgba(255,255,255,.22);
  --seek-shadow:rgba(0,0,0,.6);
  --button-color:#dde1e8;
  --button-hover-bg:rgba(255,255,255,.10);
  --button-hover-color:#fff;
  --button-active-bg:rgba(255,255,255,.16);
  --button-gap:18px;
  --skip-size:40px;
  --play-size:48px;
  --play-bg:rgba(255,255,255,.10);
  --play-hover-bg:rgba(255,255,255,.17);
  --mute-size:32px;
  --mute-radius:8px;
  --volume-width:88px;
  --volume-fill:#cdd1da;
  --volume-rest:rgba(255,255,255,.22);
  --volume-thumb:#f0f2f6;
  --scrub-margin:0;
  --controls-margin:2px;
  --skip-icon:24px;
  --play-icon:22px;
  --volume-icon:17px;
}
html,body{margin:0;width:100%;height:100%;background:var(--page-bg);overflow:hidden;font-family:"Segoe UI Variable","Segoe UI",sans-serif;user-select:none;}
html{animation:sp-reveal 160ms ease-out;}
@keyframes sp-reveal{from{opacity:0} }
#scrub{display:flex;align-items:center;gap:12px;width:100%;margin-top:var(--scrub-margin);}
.time{color:var(--time-color);font-size:12px;font-variant-numeric:tabular-nums;min-width:42px;text-shadow:var(--time-shadow);}
#elapsed{text-align:right;}
input[type=range]{-webkit-appearance:none;appearance:none;background:transparent;height:20px;cursor:pointer;margin:0;}
#seek{flex:1;}
#seek::-webkit-slider-runnable-track{height:4px;border-radius:2px;background:linear-gradient(90deg,#4e80f5 var(--p,0%),var(--seek-rest) var(--p,0%));}
#seek::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:12px;border-radius:50%;background:#f4f4f5;margin-top:-4px;box-shadow:0 1px 4px var(--seek-shadow);}
#controls{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;width:100%;margin-top:var(--controls-margin);}
#buttons{display:flex;align-items:center;gap:var(--button-gap);grid-column:2;}
button{all:unset;cursor:pointer;display:flex;align-items:center;justify-content:center;border-radius:50%;color:var(--button-color);}
button:hover{background:var(--button-hover-bg);color:var(--button-hover-color);}
button:active{background:var(--button-active-bg);}
.skip{width:var(--skip-size);height:var(--skip-size);}
#play{width:var(--play-size);height:var(--play-size);background:var(--play-bg);}
#play:hover{background:var(--play-hover-bg);}
#vol{display:flex;align-items:center;gap:8px;justify-self:end;grid-column:3;}
#mute{width:var(--mute-size);height:var(--mute-size);border-radius:var(--mute-radius);}
#volume{width:var(--volume-width);}
#volume::-webkit-slider-runnable-track{height:3px;border-radius:1.5px;background:linear-gradient(90deg,var(--volume-fill) var(--p,0%),var(--volume-rest) var(--p,0%));}
#volume::-webkit-slider-thumb{-webkit-appearance:none;width:10px;height:10px;border-radius:50%;background:var(--volume-thumb);margin-top:-3.5px;}
svg{display:block;}
.skip svg{width:var(--skip-icon);height:var(--skip-icon);}
#play svg{width:var(--play-icon);height:var(--play-icon);}
#mute svg{width:var(--volume-icon);height:var(--volume-icon);}
[hidden]{display:none !important;}
.audio body{display:flex;align-items:center;justify-content:center;}
.audio #wrap{display:flex;flex-direction:column;align-items:center;width:min(620px,88vw);}
.audio #art,.audio #tile{width:min(300px,44vh);height:min(300px,44vh);border-radius:16px;box-shadow:0 24px 64px rgba(0,0,0,.55),0 2px 8px rgba(0,0,0,.4);}
.audio #art{object-fit:cover;}
.audio #tile{position:relative;display:flex;align-items:center;justify-content:center;background:linear-gradient(135deg,#262833 0%,#15161c 100%);}
.audio #tile .note{font-size:88px;color:#8f95a0;}
.audio #tile .spark{position:absolute;top:20px;right:26px;font-size:20px;color:#4e80f5;}
.audio #title{margin-top:26px;color:#f4f4f5;font-size:20px;font-weight:600;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.audio #line2{margin-top:6px;color:#a9adb8;font-size:13.5px;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.video video{width:100%;height:100%;object-fit:contain;background:#000;display:block;}
.video #bar{position:fixed;left:0;right:0;bottom:0;padding:36px 22px 12px;background:linear-gradient(180deg,rgba(0,0,0,0) 0%,rgba(0,0,0,.6) 100%);transition:opacity 200ms ease;}
.video body.idle #bar{opacity:0;}
.video body.idle{cursor:none;}
.video #blocked{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);padding:10px 14px;border-radius:18px;background:rgba(20,21,25,.86);color:#f4f4f5;font-size:13px;display:none;}
.video body.blocked #blocked{display:block;}
</style>
</head>
<body>
{{bodyHtml}}
<script>
const media = document.getElementById('media');
const isVideo = {{isVideoLiteral}};
const seek = document.getElementById('seek');
const elapsed = document.getElementById('elapsed');
const total = document.getElementById('total');
const playButton = document.getElementById('play');
const icoPlay = document.getElementById('icoPlay');
const icoPause = document.getElementById('icoPause');
const volume = document.getElementById('volume');
const icoVol = document.getElementById('icoVol');
const icoVolX = document.getElementById('icoVolX');
media.volume = {{volume}};
media.muted = {{muted}};
function reportPlayFailure(err) {
  const reason = err && err.name ? err.name : 'play';
  try { window.chrome.webview.postMessage({ error: media.error ? media.error.code : 'play', reason }); } catch { }
}
function playMedia(autoplay) {
  media.play().catch(err => {
    if (err && err.name === 'AbortError') return;
    if (autoplay && err && err.name === 'NotAllowedError') {
      if (isVideo) document.body.classList.add('blocked');
      return;
    }
    reportPlayFailure(err);
  });
}
window.glimpseToggleMedia = function() {
  if (media.paused) {
    playMedia(false);
  } else {
    media.pause();
  }
};
if (isVideo) {
  const start = () => playMedia(true);
  media.addEventListener('canplay', start, { once: true });
  window.addEventListener('load', start);
  media.addEventListener('play', () => document.body.classList.remove('blocked'));
} else {
  playMedia(true);
}
function fmt(s) {
  if (!isFinite(s)) return '--:--';
  s = Math.max(0, Math.round(s));
  const m = Math.floor(s / 60), r = s % 60, h = Math.floor(m / 60);
  return h > 0
    ? h + ':' + String(m % 60).padStart(2, '0') + ':' + String(r).padStart(2, '0')
    : m + ':' + String(r).padStart(2, '0');
}
let scrubbing = false;
function paint() {
  const d = media.duration;
  if (isFinite(d) && d > 0) {
    if (!scrubbing) {
      seek.value = Math.round(media.currentTime / d * 1000);
    }
    seek.style.setProperty('--p', (seek.value / 10) + '%');
    total.textContent = fmt(d);
  }
  elapsed.textContent = fmt(scrubbing && isFinite(d) ? seek.value / 1000 * d : media.currentTime);
}
media.addEventListener('timeupdate', paint);
media.addEventListener('loadedmetadata', paint);
media.addEventListener('durationchange', paint);
seek.addEventListener('input', () => {
  scrubbing = true;
  paint();
});
seek.addEventListener('change', () => {
  const d = media.duration;
  if (isFinite(d)) {
    media.currentTime = seek.value / 1000 * d;
  }
  scrubbing = false;
});
function syncPlay() {
  icoPlay.style.display = media.paused ? 'block' : 'none';
  icoPause.style.display = media.paused ? 'none' : 'block';
}
media.addEventListener('play', syncPlay);
media.addEventListener('pause', syncPlay);
syncPlay();
if (isVideo) {
  media.addEventListener('loadedmetadata', () => {
    try { window.chrome.webview.postMessage({ dims: { w: media.videoWidth || 0, h: media.videoHeight || 0 } }); } catch { }
  }, { once: true });
}
// Keyboard input stays host-owned; the page transport is pointer-only.
playButton.addEventListener('click', window.glimpseToggleMedia);
if (isVideo) media.addEventListener('click', window.glimpseToggleMedia);
document.getElementById('back').addEventListener('click', () => {
  media.currentTime = Math.max(0, media.currentTime - 15);
});
document.getElementById('fwd').addEventListener('click', () => {
  const d = media.duration;
  media.currentTime = isFinite(d) ? Math.min(d, media.currentTime + 15) : media.currentTime + 15;
});
function syncVolume() {
  volume.value = Math.round((media.muted ? 0 : media.volume) * 100);
  volume.style.setProperty('--p', volume.value + '%');
  icoVol.style.display = media.muted ? 'none' : 'block';
  icoVolX.style.display = media.muted ? 'block' : 'none';
}
volume.addEventListener('input', () => {
  media.muted = false;
  media.volume = volume.value / 100;
});
document.getElementById('mute').addEventListener('click', () => {
  media.muted = !media.muted;
});
syncVolume();
if (isVideo) {
  let idleTimer;
  const wake = () => {
    document.body.classList.remove('idle');
    clearTimeout(idleTimer);
    if (!media.paused) {
      idleTimer = setTimeout(() => document.body.classList.add('idle'), 2400);
    }
  };
  window.addEventListener('mousemove', wake);
  media.addEventListener('play', wake);
  media.addEventListener('pause', wake);
  wake();
}
let volumeTimer;
media.addEventListener('volumechange', () => {
  syncVolume();
  clearTimeout(volumeTimer);
  volumeTimer = setTimeout(() => {
    try { window.chrome.webview.postMessage({ volume: media.volume, muted: media.muted }); } catch { }
  }, 250);
});
media.addEventListener('error', () => reportPlayFailure({ name: media.error ? String(media.error.code) : 'decode' }));
</script>
</body>
</html>
""";

        File.WriteAllText(htmlPath, html);
        return new Uri(htmlPath, UriKind.Absolute);
    }

    private static string BuildMediaTransportHtml(string durationText) => $$"""
<div id="scrub">
<div class="time" id="elapsed">0:00</div>
<input id="seek" type="range" min="0" max="1000" value="0">
<div class="time" id="total">{{durationText}}</div>
</div>
<div id="controls">
<div id="buttons">
<button class="skip" id="back" title="Back 15 seconds">
<svg viewBox="0 0 24 24">
<path d="M 7.99 6.72 A 7 7 0 1 1 5.31 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 4.55 6.6 L 8.6 3.7 L 8.7 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">15</text>
</svg>
</button>
<button id="play" title="Play or pause (Space)">
<svg id="icoPlay" viewBox="0 0 24 24">
<path d="M8 4.6 L19 12 L8 19.4 Z" fill="currentColor"/>
</svg>
<svg id="icoPause" style="display:none" viewBox="0 0 24 24">
<rect x="6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
<rect x="13.6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
</svg>
</button>
<button class="skip" id="fwd" title="Forward 15 seconds">
<svg viewBox="0 0 24 24">
<path d="M 16.01 6.72 A 7 7 0 1 0 18.69 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 19.45 6.6 L 15.4 3.7 L 15.3 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">15</text>
</svg>
</button>
</div>
<div id="vol">
<button id="mute" title="Mute">
<svg id="icoVol" viewBox="0 0 24 24">
<path d="M4 9.5 L8 9.5 L13 5 L13 19 L8 14.5 L4 14.5 Z" fill="currentColor"/>
<path d="M16 9 A 4.4 4.4 0 0 1 16 15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M18.4 6.6 A 7.6 7.6 0 0 1 18.4 17.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
</svg>
<svg id="icoVolX" style="display:none" viewBox="0 0 24 24">
<path d="M4 9.5 L8 9.5 L13 5 L13 19 L8 14.5 L4 14.5 Z" fill="currentColor"/>
<path d="M16.4 9.6 L21.2 14.4 M21.2 9.6 L16.4 14.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
</svg>
</button>
<input id="volume" type="range" min="0" max="100" value="0">
</div>
</div>
""";

    // ---------------------------------------------------------------------
    // Static content builders. All colors come from the active theme.
    // ---------------------------------------------------------------------

    private static FrameworkElement BuildPdfPreview(PdfPreviewDocument document)
    {
        var palette = Theme.Current;
        var stack = new StackPanel
        {
            Margin = new Thickness(0, 22, 0, 22),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        for (var i = 0; i < document.Pages.Count; i++)
        {
            var page = document.Pages[i];
            var maxWidth = 940.0;
            stack.Children.Add(new Border
            {
                MaxWidth = maxWidth,
                Width = Math.Min(maxWidth, page.Width),
                Margin = new Thickness(24, i == 0 ? 0 : 20, 24, 0),
                Background = Brushes.White,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 18,
                    ShadowDepth = 4,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = palette.IsDark ? 0.24 : 0.16
                },
                Child = new Image
                {
                    Source = page.Bitmap,
                    Stretch = Stretch.Uniform,
                    SnapsToDevicePixels = true
                }
            });

            stack.Children.Add(new TextBlock
            {
                Text = $"Page {i + 1} of {document.TotalPages}",
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 11,
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        if (document.TotalPages > document.Pages.Count)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"Showing first {document.Pages.Count} pages",
                Margin = new Thickness(0, 18, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                Foreground = Theme.Brush(palette.TextSecondary)
            });
        }

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.DeepBg);
    }

    private static FrameworkElement BuildFontPreview(string path)
    {
        var palette = Theme.Current;
        string familyName;
        FontFamily? fontFamily = null;
        try
        {
            var glyph = new GlyphTypeface(new Uri(path, UriKind.Absolute));
            familyName = glyph.FamilyNames.Values.FirstOrDefault() ?? Path.GetFileNameWithoutExtension(path);
            var folder = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                fontFamily = new FontFamily(new Uri(folder + Path.DirectorySeparatorChar, UriKind.Absolute), "./#" + familyName);
            }
        }
        catch
        {
            familyName = Path.GetFileNameWithoutExtension(path);
        }

        var stack = new StackPanel { Margin = new Thickness(34, 28, 34, 34) };
        stack.Children.Add(new TextBlock
        {
            Text = familyName,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        });
        stack.Children.Add(new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(32, 30, 32, 34),
            CornerRadius = Metrics.CardCorner,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "AaBbCc 0123456789",
                        FontFamily = fontFamily ?? new FontFamily("Segoe UI"),
                        FontSize = 46,
                        Foreground = Theme.Brush(palette.TextPrimary),
                        TextWrapping = TextWrapping.Wrap
                    },
                    new TextBlock
                    {
                        Text = "The quick brown fox jumps over the lazy dog.",
                        Margin = new Thickness(0, 18, 0, 0),
                        FontFamily = fontFamily ?? new FontFamily("Segoe UI"),
                        FontSize = 24,
                        Foreground = Theme.Brush(palette.TextSecondary),
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            }
        });

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.ContentBg);
    }

    // ---------------------------------------------------------------------
    // The fallback card for files without a specific preview.
    // Thumbnails scale up; icons never do (an upscaled icon is just blur).
    // ---------------------------------------------------------------------

    private sealed record FileCardData(BitmapSource? Image, bool IsThumbnail, string Title, IReadOnlyList<string> Lines, string BadgeText);

    private static async Task<FrameworkElement> BuildFileCardAsync(string path, CancellationToken token = default)
    {
        var data = await Task.Run(() => CollectFileCardData(path, token), token);
        return BuildFileCard(data);
    }

    private FrameworkElement BuildDirectoryCard(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var data = CollectDirectoryCardData(path);
        var palette = Theme.Current;
        var sizeLine = new TextBlock
        {
            Text = "Calculating size…",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 13,
            Foreground = Theme.Brush(palette.TextTertiary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var breakdown = new StackPanel
        {
            Width = 340,
            Margin = new Thickness(0, 16, 0, 0)
        };

        ContentControl? visualHost = null;
        var card = BuildFileCard(data, sizeLine, breakdown, host =>
        {
            visualHost = host;
            visualHost.Width = FolderArtworkSlotWidth;
            visualHost.Height = FolderArtworkSlotHeight;
            visualHost.VerticalContentAlignment = VerticalAlignment.Center;
            visualHost.Content = BuildFolderArtwork(data);
        });
        if (visualHost is not null)
        {
            var folderArtworkData = data;
            ShellImage? hoveredArtwork = null;
            var hoveredArtworkIsDirectory = false;

            void RenderArtwork() => visualHost.Content = hoveredArtwork is null
                ? BuildFolderArtwork(folderArtworkData)
                : BuildHoverArtwork(folderArtworkData, hoveredArtworkIsDirectory, hoveredArtwork);

            _ = UpdateFolderArtworkAsync(path, icon =>
            {
                folderArtworkData = data with { Image = icon };
                RenderArtwork();
            }, token);
            var hover = new FolderHoverThumbnail((isDirectory, shellImage) =>
            {
                hoveredArtwork = shellImage;
                hoveredArtworkIsDirectory = isDirectory;
                RenderArtwork();
            }, (int)FolderArtworkSlotWidth);
            card.Loaded += (_, _) => _folderHover = hover;
            card.Unloaded += (_, _) =>
            {
                if (ReferenceEquals(_folderHover, hover))
                {
                    hover.Reset();
                    _folderHover = null;
                }
            };
        }

        StartFolderSizeScan(path, sizeLine, breakdown, token);
        return card;
    }

    private sealed record FolderChild(string Name, long Bytes, bool IsDirectory);
    private const int FolderBreakdownRowsCap = 300;
    private const int FolderSizeScanEntryBudget = 25000;
    private static readonly TimeSpan FolderSizeScanStartDelay = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan FolderSizeScanTimeBudget = TimeSpan.FromSeconds(2.5);

    /// <summary>
    /// Walks the folder in the background, updating the card line live and
    /// finishing with a size breakdown of the largest children. One walker
    /// owns the disk at a time. Superseded requests cancel while waiting, so
    /// rapid browsing leaves only the newest folder behind the current walk.
    /// </summary>
    private void StartFolderSizeScan(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever
        };
        target.BeginAnimation(OpacityProperty, pulse);

        _ = RunFolderSizeScanAsync(path, target, breakdown, token);
    }

    private async Task RunFolderSizeScanAsync(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        var entered = false;
        try
        {
            // The first frame wins. Disk enumeration starts only after the
            // 110 ms swap/reveal beat has had an uncontested dispatcher turn.
            await Task.Delay(FolderSizeScanStartDelay, token);
            await _folderSizeScanGate.WaitAsync(token);
            entered = true;
            await Task.Run(() => ScanFolderSize(path, target, breakdown, token), token);
        }
        catch (OperationCanceledException)
        {
            // A newer folder owns the only pending slot.
        }
        finally
        {
            if (entered)
            {
                _folderSizeScanGate.Release();
            }
        }
    }

    private void ScanFolderSize(string path, TextBlock target, StackPanel breakdown, CancellationToken token)
    {
        long totalBytes = 0;
        long files = 0;
        long folders = 0;
        var entriesScanned = 0;
        var partial = false;
        var children = new List<FolderChild>();
        var lastReport = Stopwatch.StartNew();
        var scanClock = Stopwatch.StartNew();

        bool BudgetReached()
        {
            entriesScanned++;
            if (entriesScanned < FolderSizeScanEntryBudget && scanClock.Elapsed < FolderSizeScanTimeBudget)
            {
                return false;
            }

            partial = true;
            return true;
        }

        void ReportProgress()
        {
            if (lastReport.ElapsedMilliseconds <= 150)
            {
                return;
            }

            lastReport.Restart();
            var text = $"Calculating… {FileKinds.FormatSize(totalBytes)} so far";
            target.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested)
                {
                    target.Text = text;
                }
            });
        }

        try
        {
            var recursive = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            var topLevel = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint
            };

            foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", topLevel))
            {
                token.ThrowIfCancellationRequested();
                if (BudgetReached())
                {
                    break;
                }

                if (entry is FileInfo file)
                {
                    totalBytes += file.Length;
                    files++;
                    children.Add(new FolderChild(file.Name, file.Length, IsDirectory: false));
                }
                else if (entry is DirectoryInfo directory)
                {
                    folders++;

                    // AttributesToSkip only filters what enumeration
                    // yields; explicitly recursing into a junction still
                    // follows it. A child junction pointing up the tree
                    // would otherwise walk forever.
                    if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        children.Add(new FolderChild(directory.Name, 0, IsDirectory: true));
                        continue;
                    }

                    long childBytes = 0;
                    foreach (var descendant in directory.EnumerateFileSystemInfos("*", recursive))
                    {
                        token.ThrowIfCancellationRequested();
                        if (BudgetReached())
                        {
                            break;
                        }

                        if (descendant is FileInfo descendantFile)
                        {
                            childBytes += descendantFile.Length;
                            files++;
                        }
                        else
                        {
                            folders++;
                        }

                        ReportProgress();
                    }

                    totalBytes += childBytes;
                    children.Add(new FolderChild(directory.Name, childBytes, IsDirectory: true));
                }

                ReportProgress();

                if (partial)
                {
                    break;
                }
            }

            var summary = files == 0 && folders == 0
                ? partial ? "Still scanning…" : "Empty"
                : $"{(partial ? "At least " : string.Empty)}{FileKinds.FormatSize(totalBytes)} · {folders:N0} {(folders == 1 ? "folder" : "folders")} · {files:N0} {(files == 1 ? "file" : "files")}";
            var sorted = children.Where(c => c.Bytes > 0).OrderByDescending(c => c.Bytes).ToList();

            target.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                target.Text = summary;
                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 1;
                FillFolderBreakdown(breakdown, path, sorted);
            });
        }
        catch (OperationCanceledException)
        {
            // Preview moved on; stop the background scan.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"folder size scan failed {path}: {ex.GetType().Name}: {ex.Message}");
            target.Dispatcher.BeginInvoke(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                target.Text = "Size unavailable";
                target.BeginAnimation(OpacityProperty, null);
                target.Opacity = 1;
            });
        }
    }
    private void FillFolderBreakdown(StackPanel breakdown, string rootPath, IReadOnlyList<FolderChild> children)
    {
        if (children.Count == 0)
        {
            return;
        }

        var palette = Theme.Current;
        var glyphFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
        var maxBytes = children[0].Bytes;

        var rows = new StackPanel();
        var shown = 0;
        foreach (var child in children.Take(FolderBreakdownRowsCap))
        {
            AddFolderBreakdownEntry(rows, rootPath, child, maxBytes, palette, glyphFont);
            shown++;
        }

        _folderHover?.SetWarmPaths(children
            .Where(child => child.IsDirectory)
            .Take(FolderBreakdownRowsCap)
            .Select(child => Path.Combine(rootPath, child.Name))
            .ToList());

        if (children.Count > shown)
        {
            rows.Children.Add(new TextBlock
            {
                Text = $"…and {children.Count - shown:N0} more items",
                FontSize = 12,
                Margin = new Thickness(4, 8, 0, 4),
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        // One list, always scrollable: about four rows tall, with a half-row
        // peek of the fifth so it is obvious there is more below. No mode,
        // no toggle; the wheel is the whole interaction, and the hover
        // scrollbar is the hint for anyone who needs one.
        var scroll = new ScrollViewer
        {
            MaxHeight = 136,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = rows
        };
        StyleScrollViewer(scroll);
        breakdown.Children.Add(scroll);

        breakdown.Opacity = 0;
        breakdown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>
    /// A slim scrollbar that only materializes while the pointer is over the
    /// list (the wheel needs the pointer there anyway) and vanishes after.
    /// </summary>
    private static void StyleScrollViewer(ScrollViewer scroll)
    {
        ApplyScrollBarStyle(scroll);
        scroll.Loaded += (_, _) =>
        {
            foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scroll))
            {
                bar.Width = 7;
                bar.Opacity = 0;
            }
        };
        scroll.MouseEnter += (_, _) =>
        {
            // Only materialize when there is genuinely something to scroll.
            if (scroll.ScrollableHeight > 0 || scroll.ScrollableWidth > 0)
            {
                FadeScrollBars(scroll, 1, 120);
            }
        };
        scroll.MouseLeave += (_, _) => FadeScrollBars(scroll, 0, 350);
    }

    private static void FadeScrollBars(ScrollViewer scroll, double to, int milliseconds)
    {
        foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scroll))
        {
            bar.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }


    private void AddFolderBreakdownEntry(StackPanel host, string rootPath, FolderChild child, long maxBytes, ThemePalette palette, FontFamily glyphFont)
    {
        var row = new Grid { Margin = new Thickness(6, 0, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var glyph = new TextBlock
        {
            Text = child.IsDirectory ? "\uE8B7" : "\uE7C3",
            FontFamily = glyphFont,
            FontSize = 11,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(child.IsDirectory ? palette.Accent : palette.TextTertiary)
        };
        Grid.SetColumn(glyph, 0);
        row.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = child.Name,
            FontSize = 12.5,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var size = new TextBlock
        {
            Text = FileKinds.FormatSize(child.Bytes),
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            Foreground = Theme.Brush(palette.TextTertiary)
        };
        Grid.SetColumn(size, 2);
        row.Children.Add(size);

        var bar = new Border
        {
            Height = 3,
            CornerRadius = new CornerRadius(1.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(6, 328.0 * child.Bytes / Math.Max(1, maxBytes)),
            Margin = new Thickness(6, 3, 6, 5),
            Background = Theme.Brush(Color.FromArgb(150, palette.Accent.R, palette.Accent.G, palette.Accent.B))
        };

        var content = new StackPanel { Children = { row, bar } };

        // Each entry is a doorway: folders open, files get revealed.
        var childPath = Path.Combine(rootPath, child.Name);
        var isDirectory = child.IsDirectory;
        var entry = new Border
        {
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(0, 4, 0, 0),
            Margin = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = isDirectory ? "Open folder" : "Show in Explorer",
            Child = content
        };
        entry.MouseEnter += (_, _) =>
        {
            entry.Background = Theme.Brush(palette.HoverBg);

            _folderHover?.OnRowEnter(entry, childPath, isDirectory);
        };
        entry.MouseLeave += (_, _) =>
        {
            entry.Background = Brushes.Transparent;
            _folderHover?.OnRowLeave(entry);
        };
        entry.MouseLeftButtonDown += (_, e) => e.Handled = true;
        entry.MouseLeftButtonUp += (_, e) =>
        {
            OpenFolderChild(childPath, isDirectory);
            e.Handled = true;
        };

        host.Children.Add(entry);
    }

    private void OpenFolderChild(string path, bool isDirectory)
    {
        try
        {
            var arguments = isDirectory ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open folder child failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private const int ArchiveRootCap = 500;
    private const int ArchiveChildrenCap = 200;

    private static FrameworkElement BuildArchivePreview(ArchiveListing listing)
    {
        var palette = Theme.Current;
        var glyphFont = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 26) };
        var summary = $"{listing.Kind} · {listing.TotalEntries:N0} files · {FileKinds.FormatSize(listing.TotalUncompressed)} uncompressed";
        if (listing.Truncated)
        {
            summary += " · partial contents";
        }

        stack.Children.Add(new TextBlock
        {
            Text = summary,
            Margin = new Thickness(4, 0, 0, 12),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextSecondary)
        });

        // Top level only; folders expand on demand. Deep contents build
        // lazily, so a huge archive costs nothing until it is explored.
        var shown = 0;
        foreach (var node in listing.Roots.Take(ArchiveRootCap))
        {
            AddArchiveNode(stack, node, depth: 0, palette, glyphFont);
            shown++;
        }

        if (listing.Roots.Count > shown)
        {
            stack.Children.Add(new TextBlock
            {
                Text = $"…and {listing.Roots.Count - shown:N0} more items",
                FontSize = 12,
                Margin = new Thickness(31, 4, 0, 8),
                Foreground = Theme.Brush(palette.TextTertiary)
            });
        }

        return BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.ContentBg);
    }

    private static void AddArchiveNode(StackPanel host, ArchiveNode node, int depth, ThemePalette palette, FontFamily glyphFont)
    {
        var indent = Math.Min(depth, 12) * 20;
        var row = new Grid { Height = 27, Margin = new Thickness(indent, 0, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var expandable = node.IsDirectory && node.Children.Count > 0;
        TextBlock? expander = null;
        if (expandable)
        {
            expander = new TextBlock
            {
                Text = "\uE76C",
                FontFamily = glyphFont,
                FontSize = 9,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextTertiary),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform()
            };
            Grid.SetColumn(expander, 0);
            row.Children.Add(expander);
        }

        var glyph = new TextBlock
        {
            Text = node.IsDirectory ? "\uE8B7" : "\uE7C3",
            FontFamily = glyphFont,
            FontSize = 13,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(node.IsDirectory ? palette.Accent : palette.TextTertiary)
        };
        Grid.SetColumn(glyph, 1);
        row.Children.Add(glyph);

        var name = new TextBlock
        {
            Text = node.Name,
            FontSize = 13,
            FontWeight = node.IsDirectory ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var detail = new TextBlock
        {
            Text = node.IsDirectory
                ? $"{node.FileCount:N0} {(node.FileCount == 1 ? "item" : "items")} · {FileKinds.FormatSize(node.Size)}"
                : FileKinds.FormatSize(node.Size),
            FontSize = 12,
            Margin = new Thickness(16, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextTertiary)
        };
        Grid.SetColumn(detail, 3);
        row.Children.Add(detail);

        var entry = new Border
        {
            CornerRadius = Metrics.RowCorner,
            Padding = new Thickness(4, 0, 6, 0),
            Background = Brushes.Transparent,
            Child = row
        };
        host.Children.Add(entry);

        if (!expandable)
        {
            return;
        }

        var childHost = new StackPanel { Visibility = Visibility.Collapsed };
        host.Children.Add(childHost);
        var built = false;
        var expanded = false;

        entry.Cursor = Cursors.Hand;
        entry.ToolTip = null;
        entry.MouseEnter += (_, _) => entry.Background = Theme.Brush(palette.HoverBg);
        entry.MouseLeave += (_, _) => entry.Background = Brushes.Transparent;
        entry.MouseLeftButtonDown += (_, e) => e.Handled = true;
        entry.MouseLeftButtonUp += (_, e) =>
        {
            expanded = !expanded;
            if (expanded && !built)
            {
                built = true;
                var shown = 0;
                foreach (var child in node.Children)
                {
                    if (shown >= ArchiveChildrenCap)
                    {
                        childHost.Children.Add(new TextBlock
                        {
                            Text = $"…and {node.Children.Count - shown:N0} more items",
                            FontSize = 12,
                            Margin = new Thickness(Math.Min(depth + 1, 12) * 20 + 31, 3, 0, 6),
                            Foreground = Theme.Brush(palette.TextTertiary)
                        });
                        break;
                    }

                    AddArchiveNode(childHost, child, depth + 1, palette, glyphFont);
                    shown++;
                }
            }

            childHost.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            if (expanded)
            {
                childHost.Opacity = 0;
                childHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            }

            ((RotateTransform)expander!.RenderTransform).BeginAnimation(
                RotateTransform.AngleProperty,
                new DoubleAnimation(expanded ? 90 : 0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            e.Handled = true;
        };
    }
    private static FileCardData CollectFileCardData(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Directory.Exists(path))
        {
            return CollectDirectoryCardData(path);
        }

        var lines = new List<string>();
        string title;
        var shellImage = ShellThumbnailProvider.TryGetThumbnail(path);
        token.ThrowIfCancellationRequested();

        var file = new FileInfo(path);
        title = file.Name;
        lines.Add($"{FileKinds.FriendlyKind(path)} · {FileKinds.FormatSize(file.Length)}");
        lines.Add($"Modified {file.LastWriteTime:g}");

        if (FileKinds.IsExecutableLike(path))
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(versionInfo.FileDescription)
                    && !versionInfo.FileDescription.Equals(title, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Insert(0, versionInfo.FileDescription.Trim());
                }

                var version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
                if (!string.IsNullOrWhiteSpace(version))
                {
                    version = version.Trim();
                    lines.Add($"Version {(version.Length > 40 ? version[..40] + "…" : version)}");
                }

                if (!string.IsNullOrWhiteSpace(versionInfo.CompanyName))
                {
                    lines.Add(versionInfo.CompanyName.Trim());
                }
            }
            catch
            {
                // Version info is a bonus, optional.
            }
        }

        if (FileKinds.IsInternetShortcut(path))
        {
            var url = TryReadUrlTarget(path);
            if (url is not null)
            {
                lines.Add(url);
            }
        }

        var badge = Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        if (badge.Length is 0 or > 5)
        {
            badge = "FILE";
        }

        return new FileCardData(shellImage?.Bitmap, shellImage?.IsThumbnail ?? false, title, lines, badge);
    }

    /// <summary>
    /// The folder card is the first frame, not the census. Its title comes
    /// from the path; all shell and filesystem enrichment lands later.
    /// </summary>
    private static FileCardData CollectDirectoryCardData(string path)
    {
        var title = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = path;
        }

        return new FileCardData(
            Image: null,
            IsThumbnail: false,
            Title: title,
            Lines: [],
            BadgeText: "Folder");
    }

    private async Task UpdateFolderArtworkAsync(
        string path,
        Action<BitmapSource> apply,
        CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(
                () => ShellThumbnailProvider.TryGetIcon(path, (int)FolderArtworkSlotWidth),
                token);
            if (token.IsCancellationRequested || icon is null)
            {
                return;
            }

            apply(icon);
        }
        catch (OperationCanceledException)
        {
            // A newer preview owns the shell lane now.
        }
        catch
        {
            // The built-in folder mark is the permanent fallback.
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    private static string? TryReadUrlTarget(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path).Take(50))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    var url = line[4..].Trim();
                    return string.IsNullOrWhiteSpace(url) ? null : url;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static FrameworkElement BuildFileCardVisual(
        FileCardData data,
        double thumbnailMaxWidth = 560,
        double thumbnailMaxHeight = 400,
        double iconMinSide = 96,
        double iconMaxSide = 128)
    {
        var palette = Theme.Current;

        if (data.Image is null)
        {
            return new Border
            {
                Width = 96,
                Height = 120,
                CornerRadius = Metrics.CardCorner,
                BorderThickness = new Thickness(1),
                BorderBrush = Theme.Brush(palette.BadgeBorder),
                Background = Theme.Brush(palette.BadgeBg),
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = new TextBlock
                {
                    Text = data.BadgeText,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    FontSize = data.BadgeText.Length > 4 ? 18 : 24,
                    FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextAlignment = TextAlignment.Center
                }
            };
        }

        if (data.IsThumbnail)
        {
            var width = Math.Min(thumbnailMaxWidth, data.Image.Width);
            var height = Math.Min(thumbnailMaxHeight, data.Image.Height);
            var scale = Math.Min(width / data.Image.Width, height / data.Image.Height);
            var rectangle = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1, data.Image.Width * scale),
                Height = Math.Max(1, data.Image.Height * scale),
                RadiusX = Metrics.RadiusSurface,
                RadiusY = Metrics.RadiusSurface,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = new ImageBrush(data.Image) { Stretch = Stretch.Uniform }
            };
            return new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 5,
                    Direction = 270,
                    Color = Colors.Black,
                    Opacity = palette.IsDark ? 0.35 : 0.18
                },
                Child = rectangle
            };
        }

        var side = Math.Clamp(Math.Max(data.Image.Width, data.Image.Height), iconMinSide, iconMaxSide);
        var icon = new Image
        {
            Source = data.Image,
            Width = side,
            Height = side,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        return icon;
    }

    private static FrameworkElement BuildFolderArtwork(FileCardData data)
    {
        if (data.Image is not null)
        {
            return BuildFileCardVisual(data, FolderArtworkSlotWidth, FolderArtworkSlotHeight, 184, 184);
        }

        // A complete code-native folder, not a loading glyph. Shell artwork
        // may replace it, but a cold or unavailable shell can leave this on
        // screen forever without making the preview look unfinished.
        var folder = new Grid
        {
            Width = 184,
            Height = 150,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        folder.Children.Add(new Border
        {
            Width = 84,
            Height = 42,
            Margin = new Thickness(10, 7, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(11, 11, 4, 4),
            Background = Theme.Brush(Color.FromRgb(245, 180, 27)),
            BorderBrush = Theme.Brush(Color.FromRgb(215, 145, 5)),
            BorderThickness = new Thickness(1)
        });
        folder.Children.Add(new Border
        {
            Width = 184,
            Height = 122,
            VerticalAlignment = VerticalAlignment.Bottom,
            CornerRadius = new CornerRadius(12),
            Background = new LinearGradientBrush(
                Color.FromRgb(255, 222, 126),
                Color.FromRgb(255, 197, 56),
                90),
            BorderBrush = Theme.Brush(Color.FromRgb(218, 151, 8)),
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 16,
                ShadowDepth = 4,
                Direction = 270,
                Color = Colors.Black,
                Opacity = Theme.Current.IsDark ? 0.3 : 0.16
            }
        });
        return folder;
    }

    /// <summary>
    /// A hovered FILE with only an icon renders exactly like its own file
    /// card would: same side, no blow-up to the artwork slot. Folders and
    /// real thumbnails keep the large artwork treatment, matching how their
    /// own cards present.
    /// </summary>
    private static FrameworkElement BuildHoverArtwork(FileCardData data, bool isDirectory, ShellImage shellImage)
    {
        if (isDirectory)
        {
            if (data.Image is { } restingFolder)
            {
                shellImage = shellImage with
                {
                    Bitmap = MatchFolderArtworkBounds(shellImage.Bitmap, restingFolder)
                };
            }

            return BuildFolderArtwork(data with { Image = shellImage.Bitmap, IsThumbnail = shellImage.IsThumbnail });
        }

        if (shellImage.IsThumbnail)
        {
            return BuildFolderArtwork(data with { Image = shellImage.Bitmap, IsThumbnail = shellImage.IsThumbnail });
        }

        return BuildFileCardVisual(
            data with { Image = shellImage.Bitmap, IsThumbnail = false },
            FolderArtworkSlotWidth,
            FolderArtworkSlotHeight,
            FileCardIconSide,
            FileCardIconSide);
    }

    internal static BitmapSource MatchFolderArtworkBounds(BitmapSource artwork, BitmapSource restingFolder)
    {
        BitmapSource resized = artwork;
        if (artwork.PixelWidth != restingFolder.PixelWidth
            || artwork.PixelHeight != restingFolder.PixelHeight)
        {
            resized = new TransformedBitmap(
                artwork,
                new ScaleTransform(
                    (double)restingFolder.PixelWidth / artwork.PixelWidth,
                    (double)restingFolder.PixelHeight / artwork.PixelHeight));
            resized.Freeze();
        }

        var hoverBgra = new FormatConvertedBitmap(resized, PixelFormats.Bgra32, null, 0);
        var restingBgra = new FormatConvertedBitmap(restingFolder, PixelFormats.Bgra32, null, 0);
        var stride = restingFolder.PixelWidth * 4;
        var hoverPixels = new byte[stride * restingFolder.PixelHeight];
        var restingPixels = new byte[hoverPixels.Length];
        hoverBgra.CopyPixels(hoverPixels, stride, 0);
        restingBgra.CopyPixels(restingPixels, stride, 0);

        // Windows' preview-folder edge is slightly more opaque than its closed
        // folder edge. Cap only alpha so the child artwork stays intact while
        // every antialiased boundary follows the resting folder exactly.
        for (var alpha = 3; alpha < hoverPixels.Length; alpha += 4)
        {
            hoverPixels[alpha] = Math.Min(hoverPixels[alpha], restingPixels[alpha]);
        }

        var matched = BitmapSource.Create(
            restingFolder.PixelWidth,
            restingFolder.PixelHeight,
            restingFolder.DpiX,
            restingFolder.DpiY,
            PixelFormats.Bgra32,
            null,
            hoverPixels,
            stride);
        matched.Freeze();
        return matched;
    }

    private static FrameworkElement BuildFileCard(
        FileCardData data,
        TextBlock? liveLine = null,
        FrameworkElement? footer = null,
        Action<ContentControl>? visualHostReady = null)
    {
        var palette = Theme.Current;
        var card = new Grid
        {
            Width = FileCardWidth,
            Margin = new Thickness(40, 24, 40, 24),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardVisualSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardTitleSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(FileCardInfoSlotHeight) });
        card.RowDefinitions.Add(new RowDefinition
        {
            Height = new GridLength(footer is null ? 0 : FileCardFooterSlotHeight)
        });

        var visualHost = new ContentControl
        {
            Width = FileCardVisualSlotWidth,
            Height = FileCardVisualSlotHeight,
            Content = BuildFileCardVisual(data, FileCardVisualSlotWidth, FileCardVisualSlotHeight, FileCardIconSide, FileCardIconSide),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        visualHostReady?.Invoke(visualHost);
        Grid.SetRow(visualHost, 0);
        card.Children.Add(visualHost);

        var title = new TextBlock
        {
            Text = data.Title,
            Margin = new Thickness(0, 24, 0, 0),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(title, 1);
        card.Children.Add(title);

        var infoStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Top
        };

        for (var i = 0; i < data.Lines.Count; i++)
        {
            infoStack.Children.Add(new TextBlock
            {
                Text = data.Lines[i],
                Margin = new Thickness(0, i == 0 ? 0 : 4, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(i == 0 ? palette.TextSecondary : palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        if (liveLine is not null)
        {
            infoStack.Children.Add(liveLine);
        }
        Grid.SetRow(infoStack, 2);
        card.Children.Add(infoStack);

        if (footer is not null)
        {
            var footerHost = new ContentControl
            {
                Height = FileCardFooterSlotHeight,
                Content = footer,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            Grid.SetRow(footerHost, 3);
            card.Children.Add(footerHost);
        }

        var surface = (ScrollViewer)BuildScrollSurface(card, new Thickness(0), horizontal: false, palette.ContentBg);
        // A ScrollViewer aligns content to the top by default, which makes a
        // short generic card look visibly high even though the card itself
        // requests centered alignment. Center while there is spare room;
        // oversized rich cards continue to scroll normally.
        surface.VerticalContentAlignment = VerticalAlignment.Center;
        StyleScrollViewer(surface);
        return surface;
    }

    private static readonly Dictionary<string, string> AppCapabilityLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["internetClient"] = "Internet access",
        ["internetClientServer"] = "Accepts inbound internet connections",
        ["privateNetworkClientServer"] = "Local network access",
        ["picturesLibrary"] = "Pictures library",
        ["videosLibrary"] = "Videos library",
        ["musicLibrary"] = "Music library",
        ["documentsLibrary"] = "Documents library",
        ["removableStorage"] = "Removable drives",
        ["webcam"] = "Camera",
        ["microphone"] = "Microphone",
        ["location"] = "Location",
        ["broadFileSystemAccess"] = "Full file system access",
        ["runFullTrust"] = "Runs with full trust",
        ["allowElevation"] = "Can request administrator rights",
        ["packageManagement"] = "Can manage other apps",
        ["inputInjectionBrokered"] = "Can inject input"
    };

    private static readonly HashSet<string> AppCapabilityWarnings = new(StringComparer.OrdinalIgnoreCase)
    {
        "broadFileSystemAccess", "runFullTrust", "allowElevation", "packageManagement", "inputInjectionBrokered"
    };

    internal static (string Label, bool Known, bool Warning) DescribeAppCapability(string capability)
    {
        var warning = AppCapabilityWarnings.Contains(capability);
        return AppCapabilityLabels.TryGetValue(capability, out var label)
            ? (label, true, warning)
            : (HumanizeCapability(capability), false, warning);
    }

    /// <summary>
    /// An unmapped capability still reads as words, not manifest camelCase:
    /// "userNotificationListener" becomes "User notification listener".
    /// </summary>
    internal static string HumanizeCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            return capability;
        }

        var builder = new StringBuilder(capability.Length + 8);
        builder.Append(char.ToUpperInvariant(capability[0]));
        for (var i = 1; i < capability.Length; i++)
        {
            var c = capability[i];
            if (char.IsUpper(c) && !char.IsUpper(capability[i - 1]))
            {
                builder.Append(' ');
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// The package card is a trust moment: identity, who signed it, and what
    /// it can reach once installed. Native like the other file cards so it
    /// stays in that visual family.
    /// </summary>
    private static FrameworkElement BuildAppPackageCard(AppPackageInfo info)
    {
        var palette = Theme.Current;
        var warningBrush = Theme.Brush(palette.Warning);

        var stack = new StackPanel
        {
            Width = FileCardWidth,
            Margin = new Thickness(40, 44, 40, 44),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        stack.Children.Add(BuildAppPackageIcon(info, palette));

        stack.Children.Add(new TextBlock
        {
            Text = info.Title,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        if (!string.IsNullOrWhiteSpace(info.Publisher))
        {
            stack.Children.Add(new TextBlock
            {
                Text = info.Publisher,
                Margin = new Thickness(0, 6, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextSecondary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            meta.Add($"Version {info.Version}");
        }
        if (!string.IsNullOrWhiteSpace(info.Architecture))
        {
            meta.Add(info.Architecture);
        }
        if (info.IsBundle)
        {
            meta.Add("App bundle");
        }
        if (info.ApplicationCount > 1)
        {
            meta.Add($"{info.ApplicationCount:N0} apps");
        }
        if (meta.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", meta),
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        if (!string.IsNullOrWhiteSpace(info.Description))
        {
            stack.Children.Add(new TextBlock
            {
                Text = info.Description,
                Margin = new Thickness(60, 14, 60, 0),
                FontSize = 13,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 60,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }

        stack.Children.Add(BuildAppPackageSignatureLine(info, palette, warningBrush));

        if (info.Capabilities.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Capabilities",
                Margin = new Thickness(0, 26, 0, 0),
                FontSize = 12,
                Foreground = Theme.Brush(palette.TextTertiary),
                TextAlignment = TextAlignment.Center
            });

            var chips = new WrapPanel
            {
                MaxWidth = 480,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            foreach (var capability in info.Capabilities)
            {
                chips.Children.Add(BuildAppCapabilityChip(capability, palette, warningBrush));
            }
            if (info.CapabilityOverflow > 0)
            {
                chips.Children.Add(BuildAppChip($"{info.CapabilityOverflow:N0} more", palette,
                    Theme.Brush(palette.TextTertiary), Theme.Brush(palette.BadgeBorder)));
            }
            stack.Children.Add(chips);
        }

        var surface = (ScrollViewer)BuildScrollSurface(stack, new Thickness(0), horizontal: false, palette.ContentBg);
        StyleScrollViewer(surface);
        return surface;
    }

    private static FrameworkElement BuildAppPackageIcon(AppPackageInfo info, ThemePalette palette)
    {
        var tile = new Border
        {
            Width = 96,
            Height = 96,
            CornerRadius = Metrics.CardCorner,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Theme.Brush(palette.BadgeBg),
            BorderBrush = Theme.Brush(palette.BadgeBorder),
            BorderThickness = new Thickness(1)
        };

        if (info.LogoBytes is { Length: > 0 })
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(info.LogoBytes);
                bitmap.DecodePixelWidth = 192;
                bitmap.EndInit();
                bitmap.Freeze();
                tile.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill };
                tile.BorderThickness = new Thickness(0);
                return tile;
            }
            catch
            {
                // Undecodable logo asset: the glyph tile below stands in.
            }
        }

        tile.Child = new TextBlock
        {
            Text = "\uE7B8",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 40,
            Foreground = Theme.Brush(palette.TextTertiary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        return tile;
    }

    private static FrameworkElement BuildAppPackageSignatureLine(AppPackageInfo info, ThemePalette palette, Brush warningBrush)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        };

        string text;
        Brush brush;
        string? glyph;
        switch (info.Signature)
        {
            case AppPackageSignature.SignedTrusted:
                text = string.IsNullOrWhiteSpace(info.SignerName) ? "Signed" : $"Signed by {info.SignerName}";
                brush = Theme.Brush(palette.TextSecondary);
                glyph = "\uE73E";
                break;
            case AppPackageSignature.Invalid:
                text = "Signature not valid";
                brush = warningBrush;
                glyph = "\uE7BA";
                break;
            default:
                text = "Not signed";
                brush = warningBrush;
                glyph = null;
                break;
        }

        if (glyph is not null)
        {
            row.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 13,
                Foreground = brush,
                Margin = new Thickness(0, 1, 7, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = brush,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 420
        });
        return row;
    }

    private static FrameworkElement BuildAppCapabilityChip(string capability, ThemePalette palette, Brush warningBrush)
    {
        var (label, known, warning) = DescribeAppCapability(capability);
        var foreground = warning
            ? warningBrush
            : Theme.Brush(known ? palette.TextSecondary : palette.TextTertiary);
        var border = warning
            ? warningBrush
            : Theme.Brush(palette.BadgeBorder);
        return BuildAppChip(label, palette, foreground, border);
    }

    private static FrameworkElement BuildAppChip(string label, ThemePalette palette, Brush foreground, Brush border)
    {
        return new Border
        {
            CornerRadius = Metrics.RowCorner,
            Background = Theme.Brush(palette.BadgeBg),
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = Metrics.ChipPadding,
            Margin = new Thickness(3),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = foreground
            }
        };
    }

    private static FrameworkElement BuildCadPreview(CadPreview preview, string accentHex)
    {
        var palette = Theme.Current;
        var accent = (Color)ColorConverter.ConvertFromString(accentHex);
        var header = new StackPanel
        {
            Margin = new Thickness(32, 26, 32, 0),
            Children =
            {
                new TextBlock
                {
                    Text = preview.Title,
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Theme.Brush(palette.TextPrimary)
                },
                new TextBlock
                {
                    Text = preview.Subtitle,
                    Margin = new Thickness(0, 6, 0, 0),
                    FontSize = 13,
                    Foreground = Theme.Brush(palette.TextSecondary),
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Width = 42,
                    Height = 3,
                    Margin = new Thickness(0, 14, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(accent)
                }
            }
        };

        var details = BuildSelectableTextBox(
            preview.Details,
            new FontFamily("Cascadia Mono, Consolas"),
            fontSize: 13,
            wrap: true,
            new Thickness(32, 22, 32, 32),
            palette.ContentBg);

        var grid = new Grid
        {
            Background = Theme.Brush(palette.ContentBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(details, 1);
        grid.Children.Add(header);
        grid.Children.Add(details);
        return grid;
    }

    internal const int TextDisplayLimit = 400_000;
    internal const int LongLineDisplayLimit = 120_000;
    private const int LongLineThreshold = 32_768;

    private static (string Text, bool Wrap)? LoadFormattedText(string path) =>
        FormatTextPreview(path, TextPreviewLoader.TryLoad(path));

    internal static (string Text, bool Wrap)? FormatTextPreview(string path, TextPreview? preview)
    {
        // WPF's TextBox lays out the whole document on the UI thread; past
        // these sizes the first layout visibly stutters, so large previews
        // trade completeness for instant open. A single enormous record is
        // more expensive than ordinary multiline text, so it gets a tighter
        // budget. This covers bencoded metadata, minified source maps/JSON,
        // lockfiles, and malformed CSV/log records without type exceptions.
        const int PrettyPrintLimit = 300_000;

        if (preview is null)
        {
            return null;
        }

        var extension = Path.GetExtension(path);
        var text = preview.Text;
        var wrap = extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jsonc", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".markdown", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".txt", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".log", StringComparison.OrdinalIgnoreCase);

        if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            && !preview.Truncated
            && text.Length <= PrettyPrintLimit)
        {
            text = TryFormatJson(text);
            wrap = true;
        }

        var displayLimit = ContainsPathologicalLine(text)
            ? LongLineDisplayLimit
            : TextDisplayLimit;
        if (text.Length > displayLimit)
        {
            text = text[..displayLimit]
                + $"\n\nShowing the first {FileKinds.FormatSize(displayLimit)} of {FileKinds.FormatSize(preview.TotalBytes)}. Open the file for the rest.";
            wrap = false;
        }

        return (text, wrap);
    }

    private static bool ContainsPathologicalLine(string text)
    {
        var lineLength = 0;
        var scanLength = Math.Min(text.Length, TextDisplayLimit);
        for (var index = 0; index < scanLength; index++)
        {
            if (text[index] is '\r' or '\n')
            {
                lineLength = 0;
                continue;
            }

            if (++lineLength > LongLineThreshold)
            {
                return true;
            }
        }

        return false;
    }

    private static FrameworkElement BuildTextPreview(TextPreview preview, bool wrap)
    {
        return BuildSelectableTextBox(
            preview.Text,
            new FontFamily("Cascadia Mono, Consolas"),
            fontSize: 13,
            wrap,
            new Thickness(24, 20, 24, 24),
            Theme.Current.ContentBg);
    }

    private static string TryFormatJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return text;
        }
    }

    private static FrameworkElement BuildOfficePreviewContent(OfficeOpenXmlPreview preview)
    {
        var palette = Theme.Current;
        var accent = preview.Type switch
        {
            OfficePreviewType.PowerPoint => Color.FromRgb(242, 112, 79),
            OfficePreviewType.Excel => Color.FromRgb(73, 179, 126),
            _ => Color.FromRgb(101, 151, 245)
        };

        var title = preview.Type switch
        {
            OfficePreviewType.PowerPoint => "PowerPoint slide text",
            OfficePreviewType.Excel => "Excel sheet preview",
            _ => "Word document text"
        };

        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(24, 20, 24, 0),
            Padding = Metrics.ChipPadding,
            CornerRadius = Metrics.PillCorner,
            Background = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(palette.TextPrimary)
            }
        };

        var fontFamily = preview.Type == OfficePreviewType.Excel
            ? new FontFamily("Cascadia Mono, Consolas")
            : new FontFamily("Segoe UI Variable, Segoe UI");

        var grid = new Grid
        {
            Background = Theme.Brush(palette.ContentBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(label, 0);

        FrameworkElement body = preview.Type == OfficePreviewType.Excel
            ? BuildSelectableTextBox(
                preview.Text,
                fontFamily,
                fontSize: 12.5,
                wrap: false,
                new Thickness(24, 16, 24, 24),
                palette.ContentBg)
            : BuildOfficeDocumentPreview(preview, fontFamily, accent);

        Grid.SetRow(body, 1);
        grid.Children.Add(label);
        grid.Children.Add(body);
        return grid;
    }

    private static FrameworkElement BuildOfficeDocumentPreview(OfficeOpenXmlPreview preview, FontFamily fontFamily, Color accent)
    {
        var palette = Theme.Current;
        var document = new FlowDocument
        {
            Background = Theme.Brush(palette.ContentBg),
            Foreground = Theme.Brush(palette.TextPrimary),
            FontFamily = fontFamily,
            FontSize = 14,
            PagePadding = new Thickness(24, 14, 24, 24),
            ColumnWidth = 100000
        };

        foreach (var block in preview.Text.Split(["\r\n", "\n"], StringSplitOptions.None).Take(500))
        {
            if (string.IsNullOrWhiteSpace(block))
            {
                document.Blocks.Add(new Paragraph
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    LineHeight = 8
                });
                continue;
            }

            var isHeading = preview.Type == OfficePreviewType.PowerPoint
                && block.StartsWith("Slide ", StringComparison.OrdinalIgnoreCase);
            document.Blocks.Add(new Paragraph(new Run(block.Trim()))
            {
                Margin = new Thickness(0, isHeading ? 16 : 2, 0, isHeading ? 6 : 2),
                Foreground = isHeading ? new SolidColorBrush(accent) : Theme.Brush(palette.TextPrimary),
                FontSize = isHeading ? 15 : 14,
                FontWeight = isHeading ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = isHeading ? 22 : 21
            });
        }

        if (preview.ImagePaths.Count > 0)
        {
            document.Blocks.Add(new Paragraph(new Run("Images"))
            {
                Margin = new Thickness(0, 24, 0, 10),
                FontSize = 15,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(palette.TextPrimary)
            });

            var wrap = new WrapPanel();
            foreach (var imagePath in preview.ImagePaths.Take(24))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(imagePath, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 320;
                    bitmap.EndInit();
                    bitmap.Freeze();

                    wrap.Children.Add(new Border
                    {
                        Width = 220,
                        Height = 150,
                        Margin = new Thickness(0, 0, 12, 12),
                        CornerRadius = new CornerRadius(12),
                        ClipToBounds = true,
                        Background = Theme.Brush(palette.SurfaceBg),
                        BorderBrush = Theme.Brush(palette.SurfaceBorder),
                        BorderThickness = new Thickness(1),
                        Child = new Image
                        {
                            Source = bitmap,
                            Stretch = Stretch.Uniform
                        }
                    });
                }
                catch
                {
                    // Ignore broken embedded media.
                }
            }

            document.Blocks.Add(new BlockUIContainer(wrap)
            {
                Margin = new Thickness(0, 0, 0, 8)
            });
        }

        var richTextBox = new RichTextBox(document)
        {
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Theme.Brush(palette.ContentBg),
            Foreground = Theme.Brush(palette.TextPrimary),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            SelectionBrush = Theme.Brush(palette.Accent),
            SelectionOpacity = 0.55,
            CaretBrush = Theme.Brush(palette.TextPrimary)
        };
        AttachMiddleAutoScroll(richTextBox, () => FindVisualChild<ScrollViewer>(richTextBox), horizontal: false);
        ApplyScrollBarStyle(richTextBox);
        return richTextBox;
    }

    private static FrameworkElement BuildMeshPreview(MeshPreview preview)
    {
        var grid = new Grid
        {
            Background = Theme.Brush(Theme.Current.DeepBg),
            ClipToBounds = true
        };

        var viewport = new Viewport3D
        {
            ClipToBounds = true
        };

        var camera = new PerspectiveCamera
        {
            Position = new Point3D(4.8, -6.4, 3.9),
            LookDirection = new Vector3D(-4.8, 6.4, -3.9),
            UpDirection = new Vector3D(0, 0, 1),
            FieldOfView = 34
        };
        viewport.Camera = camera;

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(69, 165, 255))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(215, 240, 255)), 34));

        var yaw = new AxisAngleRotation3D(new Vector3D(0, 0, 1), -18);
        var pitch = new AxisAngleRotation3D(new Vector3D(1, 0, 0), -8);
        var transform = new Transform3DGroup();
        transform.Children.Add(new RotateTransform3D(pitch));
        transform.Children.Add(new RotateTransform3D(yaw));

        var model = new GeometryModel3D(preview.Mesh, material)
        {
            BackMaterial = material,
            Transform = transform
        };

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(70, 74, 84)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(246, 248, 255), new Vector3D(-0.55, 0.85, -1.1)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(74, 132, 255), new Vector3D(0.75, -0.35, -0.45)));
        scene.Children.Add(model);

        viewport.Children.Add(new ModelVisual3D { Content = scene });
        grid.Children.Add(viewport);

        var triangleText = preview.CountLabel;
        var overlay = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18),
            Padding = new Thickness(12, 8, 12, 9),
            CornerRadius = Metrics.PillCorner,
            Background = Theme.Brush(Theme.OverlayScrim),
            BorderBrush = Theme.Brush(Theme.OverlayBorder),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = preview.FormatName,
                        FontSize = Metrics.FontLabel,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Theme.Brush(Theme.OverlayText)
                    },
                    new TextBlock
                    {
                        Text = triangleText,
                        Margin = new Thickness(0, 2, 0, 0),
                        FontSize = Metrics.FontCaption,
                        Foreground = Theme.Brush(Theme.OverlayTextSecondary)
                    },
                    new TextBlock
                    {
                        Text = "Drag to rotate · wheel to zoom",
                        Margin = new Thickness(0, 5, 0, 0),
                        FontSize = Metrics.FontCaption,
                        Foreground = Theme.Brush(Theme.OverlayTextTertiary)
                    }
                }
            }
        };
        grid.Children.Add(overlay);

        // Handlers live on the grid, not the viewport: a Viewport3D only
        // hit-tests where geometry actually renders, which would make empty
        // space around the model mouse-transparent. The whole preview area
        // should rotate and zoom.
        var lastPosition = new Point();
        var isDragging = false;
        grid.MouseLeftButtonDown += (_, e) =>
        {
            isDragging = true;
            lastPosition = e.GetPosition(grid);
            grid.CaptureMouse();
            grid.Cursor = Cursors.SizeAll;
            e.Handled = true;
        };
        grid.MouseLeftButtonUp += (_, e) =>
        {
            isDragging = false;
            grid.ReleaseMouseCapture();
            grid.Cursor = Cursors.Arrow;
            e.Handled = true;
        };
        grid.MouseMove += (_, e) =>
        {
            if (!isDragging)
            {
                return;
            }

            var position = e.GetPosition(grid);
            var delta = position - lastPosition;
            yaw.Angle += delta.X * 0.42;
            pitch.Angle = Math.Clamp(pitch.Angle + delta.Y * 0.32, -85, 85);
            lastPosition = position;
            e.Handled = true;
        };
        grid.MouseWheel += (_, e) =>
        {
            var factor = e.Delta > 0 ? 0.88 : 1.14;
            var position = camera.Position;
            var vector = new Vector3D(position.X * factor, position.Y * factor, position.Z * factor);
            var length = vector.Length;
            if (length is >= 2.2 and <= 15)
            {
                camera.Position = new Point3D(vector.X, vector.Y, vector.Z);
                camera.LookDirection = new Vector3D(-vector.X, -vector.Y, -vector.Z);
            }

            e.Handled = true;
        };
        // The capture handles the drag: rotation keeps following the pointer
        // wherever it goes, and ends on button-up or when the system takes
        // the capture away (e.g. a window resize starting). A MouseLeave
        // cancel here would kill drags against stale over-state bookkeeping.
        grid.LostMouseCapture += (_, _) =>
        {
            isDragging = false;
            grid.Cursor = Cursors.Arrow;
        };

        return grid;
    }

    private static FrameworkElement BuildLabeledTextPreview(
        string title,
        Color accent,
        string text,
        FontFamily fontFamily,
        double fontSize,
        bool wrap)
    {
        var palette = Theme.Current;
        var label = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(24, 20, 24, 0),
            Padding = Metrics.ChipPadding,
            CornerRadius = Metrics.PillCorner,
            Background = new SolidColorBrush(Color.FromArgb(42, accent.R, accent.G, accent.B)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = title,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = Theme.Brush(palette.TextPrimary)
            }
        };

        var body = BuildSelectableTextBox(
            text,
            fontFamily,
            fontSize,
            wrap,
            new Thickness(24, 16, 24, 24),
            palette.ContentBg);

        var grid = new Grid
        {
            Background = Theme.Brush(palette.ContentBg)
        };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(label, 0);
        Grid.SetRow(body, 1);
        grid.Children.Add(label);
        grid.Children.Add(body);
        return grid;
    }

    private static string BuildIllustratorSummary(string path)
    {
        var file = new FileInfo(path);
        var builder = new StringBuilder();
        builder.AppendLine("Adobe Illustrator artwork");
        builder.AppendLine($"Size: {FileKinds.FormatSize(file.Length)}");
        builder.AppendLine($"Modified: {file.LastWriteTime:g}");

        if (FileKinds.TryFindPdfHeaderOffset(path, out var pdfOffset))
        {
            builder.AppendLine(pdfOffset <= 1024
                ? "PDF-compatible stream: present"
                : $"PDF-compatible stream: found at byte {pdfOffset:N0}, outside the renderer's normal header range");
        }
        else
        {
            builder.AppendLine("PDF-compatible stream: not found");
        }

        var metadata = ReadIllustratorMetadata(path);
        if (metadata.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Metadata:");
            foreach (var (key, value) in metadata)
            {
                builder.AppendLine($"{key}: {value}");
            }
        }

        if (!FileKinds.TryFindPdfHeaderOffset(path, out _))
        {
            builder.AppendLine();
            builder.AppendLine($"This Illustrator file is legacy PostScript artwork, so {Branding.Name} shows its details instead.");
        }

        return builder.ToString().TrimEnd();
    }

    private static IReadOnlyList<(string Key, string Value)> ReadIllustratorMetadata(string path)
    {
        try
        {
            var buffer = new byte[256 * 1024];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var read = stream.Read(buffer, 0, buffer.Length);
            var sample = Encoding.UTF8.GetString(buffer, 0, read).Replace("\0", string.Empty);
            var values = new List<(string Key, string Value)>();
            foreach (var rawLine in sample.Split(["\r\n", "\n"], StringSplitOptions.None).Take(2000))
            {
                var line = rawLine.Trim();
                AddPostScriptMetadata(values, line, "%%Title:", "Title");
                AddPostScriptMetadata(values, line, "%%Creator:", "Creator");
                AddPostScriptMetadata(values, line, "%%CreationDate:", "Created");
                AddPostScriptMetadata(values, line, "%%BoundingBox:", "Bounding box");
                AddPostScriptMetadata(values, line, "%%DocumentProcessColors:", "Process colors");
                AddPostScriptMetadata(values, line, "%%AI8_CreatorVersion:", "Illustrator version");
                if (values.Count >= 8)
                {
                    break;
                }
            }

            return values;
        }
        catch
        {
            return [];
        }
    }

    private static void AddPostScriptMetadata(List<(string Key, string Value)> values, string line, string prefix, string key)
    {
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var value = line[prefix.Length..].Trim();
        if (string.IsNullOrWhiteSpace(value) || values.Any(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Add((key, value));
    }

    private sealed record PreviewAction(string Label, string Detail, string Uri, string Tooltip);

    private static FrameworkElement BuildCenteredBadge(
        string badgeText,
        string title,
        string subtitle,
        string path,
        Color accent,
        PreviewAction? action = null)
    {
        var palette = Theme.Current;

        var badge = new Border
        {
            Width = 116,
            Height = 144,
            CornerRadius = Metrics.CardCorner,
            BorderThickness = new Thickness(1),
            BorderBrush = Theme.Brush(palette.BadgeBorder),
            Background = Theme.Brush(palette.BadgeBg),
            Child = new TextBlock
            {
                Text = badgeText,
                Foreground = new SolidColorBrush(accent),
                FontSize = badgeText.Length > 4 ? 22 : 28,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            }
        };

        var stack = new StackPanel
        {
            Width = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        stack.Children.Add(badge);
        stack.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(0, 22, 0, 0),
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = subtitle,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 13,
            Foreground = Theme.Brush(palette.TextSecondary),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (action is not null)
        {
            stack.Children.Add(BuildPreviewAction(action));
        }

        stack.Children.Add(new TextBlock
        {
            Text = path,
            Margin = new Thickness(0, action is null ? 20 : 16, 0, 0),
            FontSize = 12,
            Foreground = Theme.Brush(palette.TextFaint),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        return new Grid
        {
            Background = Theme.Brush(palette.ContentBg),
            Children = { stack }
        };
    }

    private static Border BuildPreviewAction(PreviewAction action)
    {
        var palette = Theme.Current;
        var label = new TextBlock
        {
            Text = action.Label,
            FontSize = Metrics.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        };
        var detail = new TextBlock
        {
            Text = action.Detail,
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = Metrics.FontCaption,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var arrow = new TextBlock
        {
            Text = "\uE76C",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Margin = new Thickness(14, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var pill = new Border
        {
            Margin = new Thickness(0, 18, 0, 0),
            Padding = new Thickness(16, 10, 15, 11),
            CornerRadius = Metrics.PillCorner,
            HorizontalAlignment = HorizontalAlignment.Center,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = action.Tooltip,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new StackPanel { Children = { label, detail } },
                    arrow
                }
            }
        };
        pill.MouseEnter += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.HoverBg);
            detail.Foreground = Theme.Brush(palette.TextPrimary);
            arrow.Foreground = Theme.Brush(palette.TextPrimary);
        };
        pill.MouseLeave += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.SurfaceBg);
            detail.Foreground = Theme.Brush(palette.TextSecondary);
            arrow.Foreground = Theme.Brush(palette.TextSecondary);
        };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            OpenExternalUri(action.Uri);
            e.Handled = true;
        };
        return pill;
    }

    /// <summary>
    /// The dead-control rule: an unplayable file never keeps a dead player.
    /// The card names the outcome and points at the exit, using the same
    /// default-app name the header's Open pill shows.
    /// </summary>
    private static string MediaFailureNextStep(string path, Mp4CodecInfo? codecs = null)
    {
        // With a Store action on the card, the subtitle states the fact and
        // lets the pill be the instruction; two competing imperatives read
        // as noise. The header's Open pill still names the app either way.
        if (codecs?.HasHevcVideo == true)
        {
            return "This video uses HEVC.";
        }

        return codecs?.HasAv1Video == true
            ? "This video uses AV1."
            : MediaOpenInsteadText(path);
    }

    private static FrameworkElement BuildMediaFailurePreview(string path, Mp4CodecInfo? codecs)
    {
        return BuildErrorPreview(
            "Glimpse can't play this file",
            MediaFailureNextStep(path, codecs),
            MediaCodecStoreAction(codecs));
    }

    private static FrameworkElement BuildHeifFailurePreview(string path)
    {
        var usesHevc = FileSignatures.HasHevcCodedHeifBrand(path)
            || Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase);
        var action = usesHevc
            ? new PreviewAction(
                "Get HEVC support",
                "Opens Microsoft Store",
                HevcVideoExtensionStoreUri,
                "Open HEVC Video Extensions in Microsoft Store")
            : new PreviewAction(
                "Get HEIF support",
                "Opens Microsoft Store",
                HeifImageExtensionStoreUri,
                "Open HEIF Image Extensions in Microsoft Store");

        return BuildErrorPreview(
            "Glimpse can't preview this image",
            usesHevc ? "This image uses HEVC." : "This image uses HEIF.",
            action);
    }

    private static PreviewAction? MediaCodecStoreAction(Mp4CodecInfo? codecs)
    {
        if (codecs?.HasHevcVideo == true)
        {
            return new PreviewAction(
                "Get HEVC support",
                "Opens Microsoft Store",
                HevcVideoExtensionStoreUri,
                "Open HEVC Video Extensions in Microsoft Store");
        }

        if (codecs?.HasAv1Video == true)
        {
            return new PreviewAction(
                "Get AV1 support",
                "Opens Microsoft Store",
                Av1VideoExtensionStoreUri,
                "Open AV1 Video Extension in Microsoft Store");
        }

        return null;
    }

    private static string MediaOpenInsteadText(string path)
    {
        var extension = Path.GetExtension(path);
        var app = string.IsNullOrEmpty(extension) ? null : NativeMethods.TryGetFriendlyAppName(extension);
        return string.IsNullOrWhiteSpace(app) || app.Equals("Pick an app", StringComparison.OrdinalIgnoreCase)
            ? "Open it in a media player instead."
            : $"Open it with {app} instead.";
    }

    private static FrameworkElement BuildErrorPreview(string title, string detail, PreviewAction? action = null)
    {
        return BuildCenteredBadge("!", title, detail, string.Empty, Theme.Current.Warning, action);
    }

    private static TextBox BuildSelectableTextBox(
        string text,
        FontFamily fontFamily,
        double fontSize,
        bool wrap,
        Thickness padding,
        Color background)
    {
        var palette = Theme.Current;
        var textBox = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Theme.Brush(background),
            Foreground = Theme.Brush(palette.TextPrimary),
            CaretBrush = Theme.Brush(palette.TextPrimary),
            SelectionBrush = Theme.Brush(palette.Accent),
            SelectionOpacity = 0.55,
            Padding = padding,
            FontFamily = fontFamily,
            FontSize = fontSize,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            AcceptsReturn = true,
            AcceptsTab = true,
            IsUndoEnabled = false,
            SpellCheck = { IsEnabled = false },
            Focusable = true,
            ClipToBounds = true
        };
        TextOptions.SetTextFormattingMode(textBox, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(textBox, TextRenderingMode.ClearType);
        AttachMiddleAutoScroll(textBox, () => FindVisualChild<ScrollViewer>(textBox), horizontal: !wrap);
        ApplyScrollBarStyle(textBox);
        return textBox;
    }

    /// <summary>
    /// WPF does not push implicit styles into template-internal scrollbars
    /// reliably, so the shared style is assigned directly once the template
    /// has been applied. Keeps every surface's scrollbar identical.
    /// </summary>
    private static void ApplyScrollBarStyle(FrameworkElement host)
    {
        // Staged content attaches COLLAPSED, and a collapsed control has no
        // template yet - at Loaded there are no scrollbars to style, and the
        // preview would show native white bars after its reveal. Style on
        // every visibility gain too, once layout has built the tree.
        var wired = false;
        void Apply()
        {
            if (Application.Current.Resources["PreviewScrollBar"] is not Style style)
            {
                DiagnosticLog.Write("scrollbar style missing from application resources");
                return;
            }

            var found = false;
            foreach (var scrollBar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(host))
            {
                scrollBar.Style = style;
                found = true;
            }

            if (!found || wired)
            {
                return;
            }

            var scrollViewer = host as ScrollViewer ?? FindVisualChild<ScrollViewer>(host);
            if (scrollViewer is not null)
            {
                wired = true;
                scrollViewer.ScrollChanged += (_, _) => ClampThumbSizes(scrollViewer);
                ClampThumbSizes(scrollViewer);
            }
        }

        host.Loaded += (_, _) => Apply();
        host.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                host.Dispatcher.BeginInvoke(Apply, DispatcherPriority.Loaded);
            }
        };
    }

    /// <summary>
    /// WPF's Track only respects the tiny system drag metric, so huge
    /// documents get a near-invisible thumb. Inflating ViewportSize keeps the
    /// thumb grabbable; drag math stays consistent because Track derives
    /// position and size from the same value.
    /// </summary>
    private static void ClampThumbSizes(ScrollViewer scrollViewer)
    {
        const double MinThumb = 30;
        foreach (var bar in FindVisualChildren<System.Windows.Controls.Primitives.ScrollBar>(scrollViewer))
        {
            var vertical = bar.Orientation == Orientation.Vertical;
            var range = vertical ? scrollViewer.ScrollableHeight : scrollViewer.ScrollableWidth;
            var viewport = vertical ? scrollViewer.ViewportHeight : scrollViewer.ViewportWidth;
            var trackLength = vertical ? bar.ActualHeight : bar.ActualWidth;
            if (range <= 0 || viewport <= 0 || trackLength <= MinThumb * 3)
            {
                continue;
            }

            var natural = trackLength * viewport / (range + viewport);
            bar.ViewportSize = natural < MinThumb
                ? MinThumb * range / (trackLength - MinThumb)
                : viewport;
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static FrameworkElement BuildScrollSurface(UIElement child, Thickness padding, bool horizontal, Color background)
    {
        var scrollViewer = new ScrollViewer
        {
            Background = Theme.Brush(background),
            Padding = padding,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            CanContentScroll = false,
            Content = child
        };

        scrollViewer.PreviewMouseWheel += (_, e) =>
        {
            // Preview events tunnel: when this surface has nothing to scroll,
            // stay out of the way so a nested list (folder breakdown) still
            // gets the wheel.
            if (scrollViewer.ScrollableHeight <= 0 && scrollViewer.ScrollableWidth <= 0)
            {
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift && horizontal)
            {
                scrollViewer.ScrollToHorizontalOffset(scrollViewer.HorizontalOffset - e.Delta * 0.9);
            }
            else
            {
                scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta * 0.9);
            }

            e.Handled = true;
        };

        AttachMiddleAutoScroll(scrollViewer, () => scrollViewer, horizontal);
        ApplyScrollBarStyle(scrollViewer);

        return scrollViewer;
    }

    private static void AttachMiddleAutoScroll(FrameworkElement eventSource, Func<ScrollViewer?> getScrollViewer, bool horizontal)
    {
        DispatcherTimer? autoScrollTimer = null;
        Point origin = default;

        void Stop()
        {
            autoScrollTimer?.Stop();
            autoScrollTimer = null;
            eventSource.ReleaseMouseCapture();
            eventSource.Cursor = Cursors.Arrow;
        }

        eventSource.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle)
            {
                if (autoScrollTimer is not null)
                {
                    Stop();
                }

                return;
            }

            if (autoScrollTimer is not null)
            {
                Stop();
                e.Handled = true;
                return;
            }

            var scrollViewer = getScrollViewer();
            if (scrollViewer is null)
            {
                return;
            }

            origin = e.GetPosition(eventSource);
            eventSource.CaptureMouse();
            eventSource.Cursor = Cursors.SizeNS;
            autoScrollTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            autoScrollTimer.Tick += (_, _) =>
            {
                var activeScrollViewer = getScrollViewer();
                if (activeScrollViewer is null)
                {
                    Stop();
                    return;
                }

                var position = Mouse.GetPosition(eventSource);
                var verticalDelta = AutoScrollDelta(position.Y - origin.Y);
                if (Math.Abs(verticalDelta) > 0)
                {
                    activeScrollViewer.ScrollToVerticalOffset(activeScrollViewer.VerticalOffset + verticalDelta);
                }

                if (horizontal)
                {
                    var horizontalDelta = AutoScrollDelta(position.X - origin.X);
                    if (Math.Abs(horizontalDelta) > 0)
                    {
                        activeScrollViewer.ScrollToHorizontalOffset(activeScrollViewer.HorizontalOffset + horizontalDelta);
                    }
                }
            };
            autoScrollTimer.Start();
            e.Handled = true;
        };

        eventSource.Unloaded += (_, _) => Stop();
    }

    private static double AutoScrollDelta(double distance)
    {
        const double deadZone = 8;
        var magnitude = Math.Abs(distance) - deadZone;
        if (magnitude <= 0)
        {
            return 0;
        }

        return Math.Sign(distance) * Math.Min(82, Math.Pow(magnitude, 1.22) * 0.12);
    }

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                return typed;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private const string ZoomableContentTag = "zoomable";

    private static readonly TimeSpan HtmlWebPaintReadyFallback = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PdfPluginSettleDelay = TimeSpan.FromMilliseconds(180);

    private static TimeSpan WebPaintReadyFallback(FrameworkElement content) =>
        content.Tag is PdfPageRailState
            ? PdfPluginSettleDelay
            : HtmlWebPaintReadyFallback;

    /// <summary>
    /// Resolves through the narrowest readiness signal each renderer can give.
    /// Hardened static HTML cannot run page script, so a host script flushes
    /// style and layout while the WebView is hidden, then the host shows the
    /// laid-out page. Interactive pages are primed transparent, cross two
    /// animation frames, and report a nonce through WebMessage. The PDF plugin
    /// gets a short DOM-free settle: outer frames cannot observe its embedded
    /// raster. Every lane is bounded.
    /// </summary>
    private static async Task<bool> WaitForWebPaintReadyAsync(
        WebView2 webView,
        TimeSpan fallback,
        CancellationToken token,
        WebPaintReadinessMode mode)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return false;
        }

        if (mode == WebPaintReadinessMode.PdfPlugin)
        {
            try
            {
                await Task.Delay(fallback, token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // One deadline governs event hookup, script dispatch, and frame wait.
        // A slow COM subscription must not grant a late script a fresh window
        // in which it can hide content after the host fallback revealed it.
        var deadlineUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            + Math.Max(1L, (long)Math.Ceiling(fallback.TotalMilliseconds));
        var attached = false;
        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(fallback);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (mode == WebPaintReadinessMode.StaticLayout)
            {
                var layoutScript = $$"""
                    (() => {
                      // ExecuteScriptAsync itself is not cancelable. If the
                      // renderer services this command after the host fallback,
                      // do not hide content that has already been revealed.
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

            var signal = $"glimpse-paint-ready:{Guid.NewGuid():N}";
            var paintReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = (_, args) =>
            {
                try
                {
                    if (string.Equals(args.TryGetWebMessageAsString(), signal, StringComparison.Ordinal))
                    {
                        paintReady.TrySetResult(true);
                    }
                }
                catch
                {
                    // Other page messages are objects (media state, dimensions,
                    // errors). They belong to their normal handlers, not this gate.
                }
            };

            // A renderer can exit between the CoreWebView2 lookup and event
            // hookup. Keep subscription inside the protected scope so a
            // disposed COM event source degrades to the bounded fallback.
            core.WebMessageReceived += handler;
            attached = true;
            timeout.Token.ThrowIfCancellationRequested();

            var encodedSignal = JsonSerializer.Serialize(signal);
            var frameScript = $$"""
                (() => {
                  // ExecuteScriptAsync itself is not cancelable. If the
                  // renderer only services this command after the host-side
                  // fallback elapsed, never hide content that is now visible.
                  if (Date.now() > {{deadlineUnixMilliseconds}}) return;

                  const root = document.documentElement;
                  if (root) {
                    root.style.setProperty('opacity', '0', 'important');
                    root.style.setProperty('transition', 'opacity 180ms ease-out');
                  }

                  const report = () => {
                    try { window.chrome.webview.postMessage({{encodedSignal}}); } catch { }
                  };
                  const afterPaint = () => {
                    if (typeof requestAnimationFrame !== 'function') {
                      setTimeout(report, 0);
                      return;
                    }

                    requestAnimationFrame(() => requestAnimationFrame(report));
                  };

                  if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', afterPaint, { once: true });
                  } else {
                    afterPaint();
                  }
                })();
                """;

            await core.ExecuteScriptAsync(frameScript).WaitAsync(timeout.Token);
            await paintReady.Task.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            // A hardened/plugin document that refuses the probe still gets the
            // bounded fallback and reveals fully laid out, as before.
            return false;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    core.WebMessageReceived -= handler!;
                }
                catch
                {
                    // The renderer may have exited while the bounded gate waited.
                }
            }
        }
    }

    private static async Task WaitForDocumentRevealGateAsync(FrameworkElement content)
    {
        if (content.Tag is PdfPageRailState state)
        {
            await Task.WhenAny(state.FirstRailReady.Task, Task.Delay(520));
            return;
        }

        if (content.Tag is MediaStageState media)
        {
            if (!media.DimensionsKnown)
            {
                // Metadata for a local file lands well inside the cap; the
                // cap exists so a slow source degrades to the default stage,
                // never to a blank window.
                await Task.WhenAny(media.DimsReady.Task, Task.Delay(600));
            }
        }
    }

    private void SetPreviewContent(FrameworkElement content, IDisposable? disposable, bool animate = true)
    {
        if (!animate || !IsVisible)
        {
            SetPreviewContentNow(content, disposable, animate: false);
            return;
        }

        // The outgoing content spends the first beat of a swap dissolving to
        // the canvas; attaching the successor earlier would rip it out
        // mid-fade.
        var version = _loadVersion;
        RunAfter(_swapStartedAt + TimeSpan.FromMilliseconds(110) - DateTime.UtcNow, () =>
        {
            if (version != _loadVersion || !IsVisible)
            {
                disposable?.Dispose();
                return;
            }

            StagePreviewContent(content, disposable, version);
        });
    }

    /// <summary>
    /// Attaches the new content HIDDEN: a browser view starts its engine,
    /// navigates and lays out its document offstage (a PDF viewer builds
    /// its whole UI in stages - watched live, that is a flash and a resize).
    /// The canvas holds, and the preview reveals in one piece once geometry
    /// has settled and the document has had a beat to paint.
    /// </summary>
    private void StagePreviewContent(FrameworkElement content, IDisposable? disposable, long version)
    {
        // _loadFinished stays false: the load is done but nothing is on
        // screen yet; the reveal marks finished. The timestamp names UI
        // thread stalls: a stage arriving far past its scheduled beat means
        // something blocked the dispatcher mid-swap.
        DiagnosticLog.Write($"stage +{ElapsedSinceSwap():0}ms");

        // Hide the outgoing hwnd in the same tick it is destroyed: a live
        // browser view being disposed paints a black frame on its way out.
        if (PreviewContent.Content is UIElement outgoingContent)
        {
            outgoingContent.Visibility = Visibility.Collapsed;
        }

        // The blank beat: outgoing content is gone, the successor is not
        // attached yet. The stage takes the new item's shape in one step,
        // so the successor lays out exactly once, at its final size.
        RetargetStageToFit();

        ClearPreviewContent();
        PreviewContent.Content = content;
        _currentDisposable = disposable;
        SetBrowseControlsVisible(CurrentPath is not null && !IsPinned);

        var stagedWebView = FindVisualChild<WebView2>(content);

        // A browser view stages Hidden so the document lays out once at its
        // final size. WPF content stages
        // Collapsed instead: Hidden elements still MEASURE at attach, and a
        // 400K-character text preview measuring during a swap freezes the whole
        // choreography. Collapsed defers that layout to the reveal, after
        // geometry has settled - and a file the user scrubs straight past
        // never pays for its layout at all.
        content.Visibility = stagedWebView is not null ? Visibility.Hidden : Visibility.Collapsed;

        var revealed = false;
        bool MayReveal()
        {
            if (revealed || version != _loadVersion || !ReferenceEquals(PreviewContent.Content, content))
            {
                return false;
            }

            revealed = true;
            return true;
        }

        // Documents wait for their first rail decision before reveal. That
        // keeps the PDF surface from appearing, then shifting sideways when
        // thumbnails arrive a beat later.
        if (stagedWebView is { } webView)
        {
            var revealToken = _loadCancellation?.Token ?? CancellationToken.None;

            bool StillOwnsContent() =>
                !revealToken.IsCancellationRequested
                && version == _loadVersion
                && !revealed
                && ReferenceEquals(PreviewContent.Content, content);

            var revealAttemptStarted = false;
            async Task TryRevealWebAsync(bool waitForDocumentGate, string reason)
            {
                // Navigation and the 1.4s escape hatch share one owner. Two
                // probes can both prime opacity=0; if the loser finishes after
                // the winner reveals, it hides the winner again.
                if (!StillOwnsContent() || revealAttemptStarted)
                {
                    return;
                }

                revealAttemptStarted = true;
                if (waitForDocumentGate)
                {
                    await WaitForDocumentRevealGateAsync(content);
                    if (!StillOwnsContent())
                    {
                        return;
                    }

                    DiagnosticLog.Write($"web reveal gate +{ElapsedSinceSwap():0}ms");
                }

                var paintClock = Stopwatch.StartNew();
                var readinessMode = PaintReadinessMode(webView);
                var paintReady = await WaitForWebPaintReadyAsync(
                    webView,
                    WebPaintReadyFallback(content),
                    revealToken,
                    readinessMode);
                if (!StillOwnsContent())
                {
                    return;
                }

                DiagnosticLog.Write(
                    $"web paint gate +{ElapsedSinceSwap():0}ms ready={paintReady} wait={paintClock.Elapsed.TotalMilliseconds:0}ms mode={PaintReadinessModeName(readinessMode)} reason={reason}");
                if (MayReveal())
                {
                    RevealStagedWebContent(content, webView);
                }
            }

            webView.NavigationCompleted += async (_, args) =>
            {
                if (!StillOwnsContent())
                {
                    return;
                }

                DiagnosticLog.Write($"web navigation +{ElapsedSinceSwap():0}ms success={args.IsSuccess}");
                if (!args.IsSuccess)
                {
                    return;
                }

                await TryRevealWebAsync(waitForDocumentGate: true, reason: "navigation");
            };

            // A view that never finishes must not leave a blank window.
            RunAfter(TimeSpan.FromMilliseconds(1400), () =>
            {
                if (StillOwnsContent())
                {
                    _ = TryRevealWebAsync(waitForDocumentGate: false, reason: "timeout");
                }
            });
        }
        else
        {
            RunAfter(TimeSpan.Zero, () =>
            {
                if (!MayReveal())
                {
                    return;
                }

                _loadFinished = true;
                DiagnosticLog.Write($"reveal +{ElapsedSinceSwap():0}ms");
                UpdateRotateButtons();
                content.Visibility = Visibility.Visible;
                content.Opacity = 0;
                content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                FadeOutLoadingOverlay();
                RevealContentSwapScrim(animate: true);
                ContentReady?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// Frame-rendered web content dissolves in after its paint gate primes the
    /// page transparent. Static layout content is never page-mutated and simply
    /// becomes visible once its synchronous layout acknowledgement returns.
    /// </summary>
    private void RevealStagedWebContent(FrameworkElement content, WebView2 webView)
    {
        if (!ReferenceEquals(PreviewContent.Content, content))
        {
            return;
        }

        _loadFinished = true;
        DiagnosticLog.Write($"reveal web +{ElapsedSinceSwap():0}ms");
        if (Equals(content.Tag, ZoomableContentTag))
        {
            ShowZoomControls(webView);
        }

        UpdateRotateButtons();
        content.Visibility = Visibility.Visible;

        // The wrapper syncs the browser's bounds lazily while hidden; force
        // the arrange NOW, in the same tick as the show, so the first
        // presented frame cannot appear at stale bounds.
        content.UpdateLayout();
        if (PaintReadinessMode(webView) == WebPaintReadinessMode.AnimationFrames)
        {
            RevealPrimedWebDocument(webView);
        }

        FadeOutLoadingOverlay();
        RevealContentSwapScrim(animate: true);
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    private static void RevealPrimedWebDocument(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                _ = core.ExecuteScriptAsync(
                    "document.documentElement?.style.setProperty('opacity','1','important');");
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    private double ElapsedSinceSwap()
    {
        var startedAt = _swapStartedAt == DateTime.MinValue ? _loadStartedAt : _swapStartedAt;
        return startedAt == DateTime.MinValue
            ? -1
            : (DateTime.UtcNow - startedAt).TotalMilliseconds;
    }

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

    private void SetPreviewContentNow(FrameworkElement content, IDisposable? disposable, bool animate)
    {
        RetargetStageToFit();
        _loadFinished = true;
        HideLoadingOverlay();
        ClearPreviewContent();
        PreviewContent.Content = content;
        _currentDisposable = disposable;
        SetBrowseControlsVisible(CurrentPath is not null && !IsPinned);

        if (Equals(content.Tag, ZoomableContentTag) && FindVisualChild<WebView2>(content) is { } zoomable)
        {
            ShowZoomControls(zoomable);
        }

        UpdateRotateButtons();

        if (animate && IsVisible)
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        RevealContentSwapScrim(animate && IsVisible);
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPreviewContent()
    {
        HideZoomControls();
        HideContentActions();
        // Mode state dies with the content; the path does NOT - the staging
        // beat clears the outgoing content AFTER the new item's lane tagged
        // it, and ShowPreview owns the per-item reset.
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        UpdateFillSignPill();
        _folderHover?.Reset();
        _folderHover = null;
        _activePdfRail = null;
        _activeEpubRail = null;
        PreviewContent.Content = null;
        _currentDisposable?.Dispose();
        _currentDisposable = null;
    }

    /// <summary>
    /// The rotate control follows the content: visible only while the native
    /// image viewer is up, and honest about persistence in its tooltip.
    /// </summary>
    private void HideContentActions()
    {
        RotateLeftButton.Visibility = Visibility.Collapsed;
        RotateRightButton.Visibility = Visibility.Collapsed;
        ContentActions.Visibility = Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private void UpdateRotateButtons()
    {
        if (_isFileDialogMode || _rotationSession is not { } session)
        {
            HideContentActions();
            return;
        }

        var suffix = session.Persistence == RotationPersistence.ViewOnly
            ? ". Not saved for this file type."
            : string.Empty;
        RotateLeftButton.ToolTip = $"Rotate left (Ctrl+Shift+R){suffix}";
        RotateRightButton.ToolTip = $"Rotate right (Ctrl+R){suffix}";
        RotateLeftButton.Visibility = Visibility.Visible;
        RotateRightButton.Visibility = Visibility.Visible;
        ContentActions.Visibility = Visibility.Visible;
        UpdateContentToolsSlotVisibility();
    }

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrentImage(-1);
    }

    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrentImage(1);
    }

    private void SharePath(string path)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (ShareService.ShareFile(path, hwnd))
        {
            SuppressOutsideDismissFor(TimeSpan.FromSeconds(30));
            DiagnosticLog.Write($"share opened {path}");
            return;
        }

        DiagnosticLog.Write($"share declined {path}");
    }

    private void RotateCurrentImage(int direction)
    {
        if (_isFileDialogMode || _rotationSession is not { } session)
        {
            return;
        }

        _imageDecodeCache.Invalidate();
        session.Rotate(direction);
        DiagnosticLog.Write($"rotate {(direction > 0 ? "cw" : "ccw")} path={session.Path} persist={session.Persistence}");
    }

    private void SetBrowseControlsVisible(bool visible)
    {
        BrowseControls.Visibility = visible && !_isFileDialogMode
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    internal void DetachBrowseContext(bool restoreSlideshowSize = true)
    {
        SetBrowseControlsVisible(false);
        _multiSelectionPaths = null;
        _multiSelectionIndex = 0;
        CloseGridView();
        StopSlideshow(restoreSlideshowSize);
        GridButton.Visibility = Visibility.Collapsed;
        PlayButton.Visibility = Visibility.Collapsed;
        SetBrowsePosition(-1, 0);
    }

    /// <summary>
    /// Chevrons rest at the folder's ends. An unknown position (probe, stale
    /// shell) keeps both live; the shell answers again on the next step.
    /// </summary>
    public void SetBrowsePosition(int index, int count)
    {
        var known = index >= 0 && count > 0;
        BrowsePreviousButton.IsEnabled = !known || index > 0;
        BrowseNextButton.IsEnabled = !known || index < count - 1;
    }

    // ---------------------------------------------------------------------
    // Header: icon, open-with, maximize, drag.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Uses the header zoom controls for a document webview. It drives the
    /// same browser zoom as Ctrl+wheel, and the label follows both.
    /// </summary>
    private void ShowZoomControls(WebView2 webView)
    {
        _zoomWebView = webView;
        webView.ZoomFactorChanged += OnZoomFactorChanged;
        UpdateZoomLabel();
        ZoomControls.Visibility = Visibility.Visible;
        UpdateContentToolsSlotVisibility();
    }

    private void HideZoomControls()
    {
        if (_zoomWebView is not null)
        {
            _zoomWebView.ZoomFactorChanged -= OnZoomFactorChanged;
            _zoomWebView = null;
        }

        UpdateZoomLabel();
        ZoomControls.Visibility = Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private void UpdateContentToolsSlotVisibility()
    {
        ContentToolsSlot.Visibility =
            ZoomControls.Visibility == Visibility.Visible
            || ContentActions.Visibility == Visibility.Visible
            || _fillSignPill?.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Hidden;
    }

    // ---------------------------------------------------------------------
    // Fill and sign. The pen pill appears only after the user engages a
    // native PDF; sign mode swaps in our own page surface (the embedded
    // viewer is a closed world), and saving never touches the original
    // except by the user's explicit second choice.
    // ---------------------------------------------------------------------

    internal bool IsFillSignModeActive => _fillSignSurface is not null;

    internal void RequestFillSignEscape() => _fillSignSurface?.HandleEscape();

    private void UpdateFillSignPill()
    {
        if (_fillSignPill is null)
        {
            _fillSignPill = BuildFillSignPill();
            ContentToolsSlot.Children.Add(_fillSignPill);
        }

        // Visible from the moment a native PDF loads: discoverability beat
        // the engagement gate in tester hands, and the reserved slot means
        // no reflow either way.
        _fillSignPill.Visibility =
            !_isFileDialogMode && _fillSignPath is not null && _fillSignSurface is null
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateContentToolsSlotVisibility();
    }

    private Border BuildFillSignPill()
    {
        var palette = Theme.Current;
        var pill = new Border
        {
            Height = 27,
            Padding = new Thickness(11, 0, 12, 0),
            CornerRadius = Metrics.PillCorner,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Fill and sign",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "",
                        FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                        FontSize = 11,
                        Margin = new Thickness(0, 0, 6, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextSecondary)
                    },
                    new TextBlock
                    {
                        Text = "Sign",
                        FontSize = Metrics.FontLabel,
                        FontWeight = FontWeights.SemiBold,
                        VerticalAlignment = VerticalAlignment.Center,
                        Foreground = Theme.Brush(palette.TextPrimary)
                    }
                }
            }
        };
        pill.MouseEnter += (_, _) => pill.Background = Theme.Brush(palette.HoverBg);
        pill.MouseLeave += (_, _) => pill.Background = Theme.Brush(palette.SurfaceBg);
        pill.MouseLeftButtonDown += (_, e) =>
        {
            pill.Background = Theme.Brush(palette.PressedBg);
            e.Handled = true;
        };
        pill.MouseLeftButtonUp += async (_, e) =>
        {
            pill.Background = Theme.Brush(palette.SurfaceBg);
            e.Handled = true;
            await EnterFillSignModeAsync();
        };
        return pill;
    }

    private async Task EnterFillSignModeAsync()
    {
        if (_fillSignPath is not { } path || _fillSignSurface is not null || !IsCurrentContentPath(path))
        {
            return;
        }

        var version = _loadVersion;
        var pageIndex = Math.Max(0, (_activePdfRail?.CurrentPage ?? 1) - 1);
        var totalPages = _activePdfRail?.Document?.TotalPages ?? pageIndex + 1;
        var page = await Task.Run(() => PdfPreviewRenderer.TryRenderPage(path, pageIndex, CancellationToken.None));
        if (page is null)
        {
            if (version == _loadVersion && IsCurrentContentPath(path) && _fillSignSurface is null)
            {
                DiagnosticLog.Write($"fill-sign entry render failed page={pageIndex + 1}");
            }

            return;
        }

        if (version != _loadVersion || !IsCurrentContentPath(path) || _fillSignSurface is not null
            || PreviewContent.Content is not FrameworkElement current)
        {
            return;
        }

        var surface = new FillSignSurface(path, pageIndex, Math.Max(totalPages, pageIndex + 1), page);
        surface.ExitRequested += ExitFillSignMode;
        surface.SaveRequested += OnFillSignSaveRequested;
        surface.PageChangeRequested += target => _ = ShowFillSignPageAsync(surface, target);

        _fillSignParkedContent = current;
        _fillSignSurface = surface;
        PreviewContent.Content = surface;
        UpdateFillSignPill();
        DiagnosticLog.Write($"fill-sign entered page={pageIndex + 1}");
    }

    private async Task ShowFillSignPageAsync(FillSignSurface surface, int pageIndex)
    {
        if (_fillSignPath is not { } path || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            return;
        }

        var page = await Task.Run(() => PdfPreviewRenderer.TryRenderPage(path, pageIndex, CancellationToken.None));
        if (page is null || !IsCurrentContentPath(path) || !ReferenceEquals(_fillSignSurface, surface))
        {
            DiagnosticLog.Write($"fill-sign page render failed page={pageIndex + 1}");
            return;
        }

        surface.ShowPage(pageIndex, page);
        DiagnosticLog.Write($"fill-sign page -> {pageIndex + 1}");
    }

    private void ExitFillSignMode()
    {
        if (_fillSignSurface is null)
        {
            return;
        }

        _fillSignSurface = null;
        if (_fillSignParkedContent is { } parked)
        {
            PreviewContent.Content = parked;
            parked.Visibility = Visibility.Visible;
            parked.UpdateLayout();
            _fillSignParkedContent = null;
        }

        UpdateFillSignPill();
        DiagnosticLog.Write("fill-sign exited");
        var deferredFailure = _deferredWebFailure;
        _deferredWebFailure = null;
        deferredFailure?.Invoke();
    }

    private async void OnFillSignSaveRequested(SignSession session, bool overwrite)
    {
        if (_fillSignPath is not { } path
            || _fillSignSurface is not { } surface
            || !IsCurrentContentPath(path)
            || !session.HasWork)
        {
            return;
        }

        var version = _loadVersion;
        bool StillOwnsSurface() =>
            version == _loadVersion
            && ReferenceEquals(_fillSignSurface, surface)
            && IsCurrentContentPath(path);

        surface.SetSaving(true);
        try
        {
            var placements = BuildWriterPlacements(session);
            string finalPath;
            if (overwrite)
            {
                var signedPath = await Task.Run(() => PdfFillSignWriter.SignPdf(path, placements));

                // The embedded viewer holds the original open; release it
                // and give Chromium a beat to let go before the swap. The
                // user's placements survive any failure - the surface stays.
                if (StillOwnsSurface())
                {
                    _fillSignParkedContent = null;
                    _currentDisposable?.Dispose();
                    _currentDisposable = null;
                }

                await ReplaceWithRetriesAsync(signedPath, path);
                finalPath = path;
            }
            else
            {
                finalPath = NextSignedCopyPath(path);
                await Task.Run(() => PdfFillSignWriter.WriteSignedCopy(path, finalPath, placements));
            }

            DiagnosticLog.Write($"fill-sign saved {(overwrite ? "in place" : "as copy")} placements={placements.Count}");
            if (!StillOwnsSurface())
            {
                // Saving is file work and still completes; navigation is UI
                // work and belongs only to the preview that started it.
                DiagnosticLog.Write("fill-sign completion left current preview unchanged");
                return;
            }

            _fillSignSurface = null;
            _fillSignParkedContent = null;
            UpdateFillSignPill();

            // The saved file is the honest outcome: show it.
            if (!ShowPreview(finalPath, Hwnd))
            {
                PresentationFailed?.Invoke(this, LastPresentationFailureReason);
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"fill-sign save failed {path}: {ex.GetType().Name}: {ex.Message}");
            surface.SetSaving(false);
        }
    }

    private static async Task ReplaceWithRetriesAsync(string signedPath, string destinationPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Replace(signedPath, destinationPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException) when (attempt < 12)
            {
                await Task.Delay(250);
            }
        }
    }

    private static List<PdfFillSignPlacement> BuildWriterPlacements(SignSession session)
    {
        var placements = new List<PdfFillSignPlacement>(session.Placements.Count);
        foreach (var item in session.Placements)
        {
            var bounds = new PdfNormalizedRect(item.X, item.Y, item.Width, item.Height);
            // Placements carry their pixels from placement time; nothing at
            // save time reaches back into the store.
            placements.Add(item.Kind switch
            {
                SignPlacementKind.Date => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Date, bounds, Text: item.Text, FontSize: 0),
                SignPlacementKind.Stamp => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Stamp, bounds, PngBytes: item.ImagePng),
                _ => new PdfFillSignPlacement(
                    item.PageIndex + 1, PdfFillSignPlacementKind.Signature, bounds, PngBytes: item.ImagePng)
            });
        }

        return placements;
    }

    private static string NextSignedCopyPath(string path)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        var candidate = Path.Combine(directory, $"{name} (signed){extension}");
        for (var i = 2; File.Exists(candidate); i++)
        {
            candidate = Path.Combine(directory, $"{name} (signed {i}){extension}");
        }

        return candidate;
    }

    /// <summary>
    /// Dev probe only: a machine-readable snapshot of the real arranged WPF
    /// tree. Pixel captures find paint failures; this finds clipped controls,
    /// lost hit targets, stale overlays, and missing automation names.
    /// </summary>
    internal string ProbeAuditState(string label)
    {
        UpdateLayout();

        Rect BoundsInHeader(FrameworkElement element)
        {
            var origin = element.TranslatePoint(new Point(0, 0), HeaderBar);
            return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
        }

        bool ButtonOwnsCenter(System.Windows.Controls.Primitives.ButtonBase button)
        {
            if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0)
            {
                return true;
            }

            var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), this);
            var node = InputHitTest(point) as DependencyObject;
            while (node is not null)
            {
                if (ReferenceEquals(node, button))
                {
                    return true;
                }

                node = node is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(node)
                    : LogicalTreeHelper.GetParent(node);
            }

            return false;
        }

        var namedButtons = new (string Name, System.Windows.Controls.Primitives.ButtonBase Button)[]
        {
            (nameof(BrowsePreviousButton), BrowsePreviousButton),
            (nameof(BrowseNextButton), BrowseNextButton),
            (nameof(GridButton), GridButton),
            (nameof(PlayButton), PlayButton),
            (nameof(ZoomOutButton), ZoomOutButton),
            (nameof(ZoomResetButton), ZoomResetButton),
            (nameof(ZoomInButton), ZoomInButton),
            (nameof(RotateLeftButton), RotateLeftButton),
            (nameof(RotateRightButton), RotateRightButton),
            (nameof(AlwaysOnTopButton), AlwaysOnTopButton),
            (nameof(PinButton), PinButton),
            (nameof(MaximizeButton), MaximizeButton),
            (nameof(CloseButton), CloseButton)
        };
        var buttonStates = namedButtons
            .Where(item => item.Button.IsVisible)
            .Select(item => new
            {
                item.Name,
                Enabled = item.Button.IsEnabled,
                AutomationName = AutomationProperties.GetName(item.Button),
                HitTarget = ButtonOwnsCenter(item.Button),
                Bounds = BoundsInHeader(item.Button).ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
            .ToArray();

        var lanes = new (string Name, FrameworkElement Element)[]
        {
            (nameof(BrowseControls), BrowseControls),
            (nameof(FileIconImage), FileIconImage),
            (nameof(IdentityPanel), IdentityPanel),
            (nameof(ContentToolsSlot), ContentToolsSlot),
            (nameof(OpenControl), OpenControl),
            (nameof(WindowActions), WindowActions)
        };
        var arrangedLanes = lanes
            .Where(item => item.Element.Visibility != Visibility.Collapsed && item.Element.ActualWidth > 0)
            .Select(item => (item.Name, Bounds: BoundsInHeader(item.Element)))
            .OrderBy(item => item.Bounds.Left)
            .ToArray();
        var lanesInsideHeader = arrangedLanes.All(item =>
            item.Bounds.Left >= -0.5
            && item.Bounds.Top >= -0.5
            && item.Bounds.Right <= HeaderBar.ActualWidth + 0.5
            && item.Bounds.Bottom <= HeaderBar.ActualHeight + 0.5);
        var lanesDoNotOverlap = true;
        for (var i = 1; i < arrangedLanes.Length; i++)
        {
            if (arrangedLanes[i - 1].Bounds.Right > arrangedLanes[i].Bounds.Left + 0.5)
            {
                lanesDoNotOverlap = false;
                break;
            }
        }

        return JsonSerializer.Serialize(new
        {
            Label = label,
            CurrentName = CurrentPath is null ? null : Path.GetFileName(CurrentPath),
            Window = new { Width = ActualWidth, Height = ActualHeight, State = WindowState.ToString() },
            ContentType = PreviewContent.Content?.GetType().Name,
            LoadFinished = _loadFinished,
            Loading = LoadingOverlay.Visibility.ToString(),
            Scrim = ContentSwapScrim.Visibility.ToString(),
            ScrimBlocksInput = ContentSwapScrim.IsHitTestVisible,
            Icon = new { Visibility = FileIconImage.Visibility.ToString(), HasSource = FileIconImage.Source is not null },
            FileNameWidth = FileNameText.ActualWidth,
            OpenWidth = OpenControl.ActualWidth,
            LanesInsideHeader = lanesInsideHeader,
            LanesDoNotOverlap = lanesDoNotOverlap,
            AllVisibleButtonsNamed = buttonStates.All(item => !string.IsNullOrWhiteSpace(item.AutomationName)),
            AllVisibleButtonCentersHit = buttonStates.All(item => item.HitTarget),
            Buttons = buttonStates,
            Lanes = arrangedLanes.Select(item => new
            {
                item.Name,
                Bounds = item.Bounds.ToString(System.Globalization.CultureInfo.InvariantCulture)
            })
        });
    }

    /// <summary>Dev probe only: exercises visible button wiring without launching external apps.</summary>
    internal string ProbeButtonAudit()
    {
        var previous = 0;
        var next = 0;
        var dismissed = 0;
        var closed = 0;
        EventHandler<PreviewBrowseDirection> browse = (_, direction) =>
        {
            if (direction == PreviewBrowseDirection.Previous) previous++;
            else next++;
        };
        EventHandler dismiss = (_, _) => dismissed++;
        EventHandler close = (_, _) => closed++;
        BrowseRequested += browse;
        DismissRequested += dismiss;
        CloseRequested += close;
        try
        {
            SetBrowsePosition(1, 3);
            BrowsePreviousButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            BrowseNextButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));

            var path = CurrentPath ?? Path.Combine(Path.GetTempPath(), "probe.txt");
            SetMultiSelection([path, path], 0);
            GridButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var gridOpened = IsGridViewOpen;
            GridButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var gridClosed = !IsGridViewOpen;

            CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var pinned = IsPinned;

            // The Always on top toggle only exists pinned: revealed at the
            // user's configured default; unchecking releases Topmost and
            // gains taskbar reachability, rechecking restores the float.
            var defaultOnTop = Services.Settings.Current.PinnedAlwaysOnTopDefault;
            var alwaysOnTopRevealed = pinned
                && AlwaysOnTopButton.Visibility == Visibility.Visible
                && AlwaysOnTopButton.IsChecked == defaultOnTop
                && Topmost == defaultOnTop
                && ShellPresenceApplied(!defaultOnTop);
            AlwaysOnTopButton.IsChecked = false;
            var alwaysOnTopReleased = !Topmost && ShellPresenceApplied(true);
            AlwaysOnTopButton.IsChecked = true;
            var alwaysOnTopRestored = Topmost && ShellPresenceApplied(false);

            CloseButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PinButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            var unpinned = !IsPinned;
            var alwaysOnTopRetired = AlwaysOnTopButton.Visibility == Visibility.Collapsed
                && AlwaysOnTopButton.IsChecked == defaultOnTop
                && Topmost
                && ShellPresenceApplied(false);

            return JsonSerializer.Serialize(new
            {
                Previous = previous,
                Next = next,
                Dismissed = dismissed,
                Closed = closed,
                GridOpened = gridOpened,
                GridClosed = gridClosed,
                Pinned = pinned,
                Unpinned = unpinned,
                AlwaysOnTopRevealed = alwaysOnTopRevealed,
                AlwaysOnTopReleased = alwaysOnTopReleased,
                AlwaysOnTopRestored = alwaysOnTopRestored,
                AlwaysOnTopRetired = alwaysOnTopRetired
            });
        }
        finally
        {
            BrowseRequested -= browse;
            DismissRequested -= dismiss;
            CloseRequested -= close;
        }
    }

    /// <summary>Dev probe only: recreates the cross-load overlay completion race.</summary>
    internal async Task<bool> ProbeLoadingOverlayRaceAsync()
    {
        ShowLoadingOverlayNow();
        FadeOutLoadingOverlay();
        await Task.Delay(90);
        ShowLoadingOverlayNow();
        await Task.Delay(110);
        var newOverlayStillOwnsTheStage =
            LoadingOverlay.Visibility == Visibility.Visible && LoadingOverlay.Opacity >= 0.95;
        DiagnosticLog.Write($"probe overlay-race -> visible={LoadingOverlay.Visibility} opacity={LoadingOverlay.Opacity:0.00} pass={newOverlayStillOwnsTheStage}");
        HideLoadingOverlay();
        return newOverlayStillOwnsTheStage;
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e) => UpdateZoomLabel();

    private void UpdateZoomLabel()
    {
        var factor = _zoomWebView?.ZoomFactor ?? 1;
        ZoomResetButton.Content = $"{Math.Round(factor * 100)}%";
        ZoomOutButton.IsEnabled = _zoomWebView is not null && CanAdjustZoom(factor, -1);
        ZoomInButton.IsEnabled = _zoomWebView is not null && CanAdjustZoom(factor, 1);
        AutomationProperties.SetName(ZoomResetButton, $"Reset zoom, currently {Math.Round(factor * 100)} percent");
    }

    private void ResetZoom()
    {
        if (_zoomWebView is null)
        {
            return;
        }

        _zoomWebView.ZoomFactor = 1.0;
        UpdateZoomLabel();
        DiagnosticLog.Write("zoom reset -> 1");
    }

    // The browser's own zoom stops (what Ctrl+wheel walks). The buttons walk
    // the same ladder, so both controls always agree and the label reads in
    // familiar levels instead of drifting to 196%.
    private static readonly double[] ZoomLevels =
        [0.25, 0.33, 0.5, 0.67, 0.75, 0.8, 0.9, 1.0, 1.1, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0, 4.0, 5.0];

    internal static bool IsZoomInKey(int vk) => vk is NativeMethods.VK_ADD or NativeMethods.VK_OEM_PLUS;

    internal static bool IsZoomOutKey(int vk) => vk is NativeMethods.VK_SUBTRACT or NativeMethods.VK_OEM_MINUS;

    internal static bool IsZoomResetKey(int vk) => vk is NativeMethods.VK_0 or NativeMethods.VK_NUMPAD0;

    internal static double NextZoomLevel(double current, int direction)
    {
        if (!double.IsFinite(current))
        {
            current = 1;
        }

        if (direction > 0)
        {
            foreach (var level in ZoomLevels)
            {
                if (level > current + 0.001)
                {
                    return level;
                }
            }
        }
        else if (direction < 0)
        {
            for (var i = ZoomLevels.Length - 1; i >= 0; i--)
            {
                if (ZoomLevels[i] < current - 0.001)
                {
                    return ZoomLevels[i];
                }
            }
        }

        return Math.Clamp(current, ZoomLevels[0], ZoomLevels[^1]);
    }

    internal static bool CanAdjustZoom(double current, int direction) =>
        Math.Abs(NextZoomLevel(current, direction) - current) > 0.001;

    private void AdjustZoom(int direction)
    {
        if (_zoomWebView is null)
        {
            DiagnosticLog.Write("zoom adjust ignored: no webview");
            return;
        }

        var target = NextZoomLevel(_zoomWebView.ZoomFactor, direction);

        _zoomWebView.ZoomFactor = target;

        // The control's ZoomFactorChanged covers Ctrl+wheel; programmatic
        // sets do not raise it, so the label follows directly here.
        UpdateZoomLabel();
        DiagnosticLog.Write($"zoom adjust -> {target:0.##}");
    }

    private async Task UpdateFileIconAsync(string path, long version, CancellationToken token)
    {
        var entered = false;
        try
        {
            await _shellIconGate.WaitAsync(token);
            entered = true;
            var icon = await Task.Run(() => ShellThumbnailProvider.TryGetIcon(path, 48), token);
            if (version != _loadVersion)
            {
                return;
            }

            FileIconImage.Source = icon;
            FileIconImage.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        }
        catch (OperationCanceledException)
        {
            // A newer preview owns the shell lane now.
        }
        catch
        {
            // Icon is decoration; the preview must not care.
        }
        finally
        {
            if (entered)
            {
                _shellIconGate.Release();
            }
        }
    }

    /// <summary>
    /// A card under a degraded document preview: first names the state
    /// (testers read the reduced view as the app rendering poorly), then the
    /// fix and its payoff. Shown in the preview only.
    /// </summary>
    private static FrameworkElement WithLibreOfficeSuggestion(FrameworkElement content)
    {
        var palette = Theme.Current;
        var title = new TextBlock
        {
            Text = "This is a basic preview",
            FontSize = Metrics.FontBody,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(palette.TextPrimary)
        };
        var text = new TextBlock
        {
            Text = "Get LibreOffice (free) to see documents fully formatted",
            Margin = new Thickness(0, 2, 0, 0),
            FontSize = 12,
            Foreground = Theme.Brush(palette.TextSecondary)
        };
        var glyph = new TextBlock
        {
            Text = "\uE896",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 15,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Theme.Brush(palette.Accent)
        };
        var pill = new Border
        {
            CornerRadius = Metrics.SurfaceCorner,
            Padding = new Thickness(16, 10, 19, 11),
            Margin = new Thickness(0, 0, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Opens libreoffice.org in your browser",
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { glyph, new StackPanel { Children = { title, text } } }
            }
        };
        pill.MouseEnter += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.HoverBg);
            text.Foreground = Theme.Brush(palette.TextPrimary);
        };
        pill.MouseLeave += (_, _) =>
        {
            pill.Background = Theme.Brush(palette.SurfaceBg);
            text.Foreground = Theme.Brush(palette.TextSecondary);
        };
        pill.MouseLeftButtonDown += (_, e) => e.Handled = true;
        pill.MouseLeftButtonUp += (_, e) =>
        {
            OpenExternalUri("https://www.libreoffice.org/download/");
            e.Handled = true;
        };

        var host = new Grid();
        host.Children.Add(content);
        host.Children.Add(pill);
        return host;
    }

    private static string GetOpenLabel(string path)
    {
        if (Directory.Exists(path))
        {
            return "Open";
        }

        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            return "Open";
        }

        // Queried fresh per preview: it is one sub-millisecond registry read,
        // and the user can change a default app while the app stays resident.
        var name = NativeMethods.TryGetFriendlyAppName(extension);
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Open";
        }

        // With no association, Windows offers its app picker as the
        // "app" - and "Open with Pick an app" is not useful. Name the
        // action for what it is.
        if (name.Equals("Pick an app", StringComparison.OrdinalIgnoreCase))
        {
            return "Pick an app";
        }

        return $"Open with {(name.Length > 26 ? name[..25].TrimEnd() + "…" : name)}";
    }

    internal void OpenCurrent()
    {
        if (_isFileDialogMode)
        {
            DiagnosticLog.Write("open suppressed while file dialog owns commit");
            return;
        }

        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"open failed {path}: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Clicking into the preview hands it keyboard focus, so Ctrl+C and the
    /// viewers' own shortcuts land where the user is looking. Only a click in
    /// the CONTENT hands the keyboard to the content: a click on header chrome
    /// (chevron, pin, zoom) activates the window but keeps the keys, or
    /// stepping to a video would give every arrow key to the player.
    /// </summary>
    public void ActivateForInteraction(int screenX, int screenY)
    {
        if (!IsVisible)
        {
            return;
        }

        try
        {
            Activate();
        }
        catch
        {
            // Activation is best-effort; the click itself still lands.
        }

        // A click on web content lands in the browser process, so Windows may
        // deny the host the foreground and leave it with Explorer - where the
        // next Ctrl+C copies Explorer's selected file instead of the
        // highlighted text. The user clicked this window; make it true.
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.ForceForeground(hwnd);
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground != hwnd)
            {
                DiagnosticLog.Write($"activate: foreground stayed 0x{foreground.ToInt64():X} (ours 0x{hwnd.ToInt64():X})");
            }
        }

        // Web-backed content (PDF viewer, docs) needs real keyboard focus in
        // the browser for its own Ctrl+C/Ctrl+F/arrow handling - the PDF
        // plugin's selection is invisible to the WPF side. Don't steal focus
        // from a text preview the user clicked into, and only do it when the
        // click actually landed on the content, not the header.
        bool clickedContent;
        double contentY;
        try
        {
            var y = PointFromScreen(new Point(screenX, screenY)).Y;
            clickedContent = y >= HeaderHeight;
            contentY = y - HeaderHeight;
        }
        catch
        {
            // A window mid-teardown cannot transform; treat as content.
            clickedContent = true;
            contentY = double.MaxValue;
        }

        if (!clickedContent)
        {
            // The browser child can retain Win32 focus after a content click.
            // A later header action means arrows belong to preview browsing
            // again, even though that stale child still reports focus.
            _userEngagedContent = false;
            _lastContentClickInViewerChrome = false;
            return;
        }

        // Only content clicks enable the content-specific key exceptions.
        // A click in the embedded viewer's own toolbar strip (page box,
        // find) is the one place typed keys are the viewer's words.
        _userEngagedContent = true;
        UpdateFillSignPill();
        _lastContentClickInViewerChrome = contentY < ViewerChromeBandHeight;
        if (Keyboard.FocusedElement is not System.Windows.Controls.Primitives.TextBoxBase
            && FindVisualChild<WebView2>(PreviewContent) is { } webView)
        {
            webView.Focus();
        }
    }

    private async Task CopyWebViewSelectionAsync(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                if (IsPdfViewerSource(core.Source ?? string.Empty))
                {
                    // The PDF plugin's selection is invisible to the DOM, so
                    // hand the copy to the viewer itself through the browser's
                    // input pipe. With nothing selected Chromium copies
                    // nothing: over a document, Ctrl+C means "copy the text".
                    DiagnosticLog.Write("ctrl+c forwarded into pdf viewer");
                    await ForwardCtrlCAsync(core);
                    return;
                }

                var json = await core.ExecuteScriptAsync("window.getSelection().toString()");
                var text = JsonSerializer.Deserialize<string>(json);
                if (!string.IsNullOrEmpty(text))
                {
                    SetClipboard(() => Clipboard.SetText(text), "copy web selection");
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"webview selection copy failed: {ex.GetType().Name}: {ex.Message}");
        }

        CopyCurrentFile();
    }

    private static Task ForwardCtrlCAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core) =>
        DispatchModifiedKeyAsync(core, 67, "KeyC", "c", modifiers: 2);

    private static async Task DispatchPlainKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, int virtualKey, string code, string key)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    private static async Task DispatchMouseClickAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, double x, double y)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var sx = x.ToString("0.###", invariant);
        var sy = y.ToString("0.###", invariant);
        var down = $"{{\"type\":\"mousePressed\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":1,\"pointerType\":\"mouse\"}}";
        var up = $"{{\"type\":\"mouseReleased\",\"x\":{sx},\"y\":{sy},\"button\":\"left\",\"clickCount\":1,\"buttons\":0,\"pointerType\":\"mouse\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", down);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", up);
    }

    private static async Task DispatchModifiedKeyAsync(Microsoft.Web.WebView2.Core.CoreWebView2 core, int virtualKey, string code, string key, int modifiers)
    {
        var keyDown = $"{{\"type\":\"keyDown\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        var keyUp = $"{{\"type\":\"keyUp\",\"modifiers\":{modifiers},\"windowsVirtualKeyCode\":{virtualKey},\"nativeVirtualKeyCode\":{virtualKey},\"code\":\"{code}\",\"key\":\"{key}\"}}";
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyDown);
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", keyUp);
    }

    /// <summary>
    /// The clipboard throws transiently when another process holds it open;
    /// for an app that supports Ctrl+C, one silent retry is the
    /// difference between trusted and flaky.
    /// </summary>
    private static void SetClipboard(Action write, string diagnosticLabel)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                write();
                return;
            }
            catch (Exception ex)
            {
                if (attempt >= 2)
                {
                    DiagnosticLog.Write($"{diagnosticLabel} failed: {ex.GetType().Name}: {ex.Message}");
                    return;
                }

                Thread.Sleep(35);
            }
        }
    }

    private void CopyCurrentFile()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetFileDropList(new StringCollection { path }), $"copy file {path}");
    }

    private void CopyCurrentPath()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        SetClipboard(() => Clipboard.SetText(path), $"copy path {path}");
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("PreviewContextMenu") };
        Theme.ApplyResources(menu);
        TrackOwnedPopup(menu);

        MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header, Style = (Style)FindResource("PreviewMenuItem") };
            item.Click += (_, _) => action();
            return item;
        }

        menu.Items.Add(Item("Open", OpenCurrent));
        menu.Items.Add(Item("Copy", CopyCurrentFile));
        menu.Items.Add(Item("Copy path", CopyCurrentPath));
        _contextMenu = menu;
        RootChrome.ContextMenu = menu;
    }

    private void ApplyHeaderSubtitle(string path, string? shortcutTarget, int selectedCount)
    {
        SubtitleText.Inlines.Clear();
        if (shortcutTarget is null)
        {
            SubtitleText.Text = BuildHeaderSubtitle(path, null, selectedCount);
            return;
        }

        // The target is a link: click reveals it in its containing folder.
        var link = new Hyperlink(new Run($"Shortcut → {shortcutTarget}"))
        {
            Foreground = Theme.Brush(Theme.Current.TextSecondary),
            TextDecorations = null,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "Show target in Explorer"
        };
        link.Click += (_, _) => RevealInExplorer(shortcutTarget);
        link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
        link.MouseLeave += (_, _) => link.TextDecorations = null;
        SubtitleText.Inlines.Add(link);

        if (selectedCount > 1)
        {
            SubtitleText.Inlines.Add(new Run($" · {selectedCount} selected"));
        }
    }

    private void RevealInExplorer(string target)
    {
        if (ExplorerRevealService.TryShow(target))
        {
            DismissRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string BuildHeaderSubtitle(string path, string? shortcutTarget, int selectedCount)
    {
        string subtitle;
        if (shortcutTarget is not null)
        {
            subtitle = $"Shortcut → {shortcutTarget}";
        }
        else if ((FileKinds.IsImage(path) || FileKinds.IsPhotoshop(path)) && TryGetImagePixelSize(path, out var pixelSize))
        {
            try
            {
                var file = new FileInfo(path);
                subtitle = $"{FileKinds.FriendlyKind(path)} · {(int)pixelSize.Width}×{(int)pixelSize.Height} · {FileKinds.FormatSize(file.Length)} · Modified {file.LastWriteTime:g}";
            }
            catch
            {
                subtitle = FileKinds.BuildSubtitle(path);
            }
        }
        else
        {
            subtitle = FileKinds.BuildSubtitle(path);
        }

        if (selectedCount > 1)
        {
            subtitle += $" · {selectedCount} selected";
        }

        return subtitle;
    }

    private static string? TryResolveShortcutTarget(string path)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            shortcut = ((dynamic)shell).CreateShortcut(path);
            var target = ((dynamic)shortcut!).TargetPath as string;
            if (string.IsNullOrWhiteSpace(target))
            {
                return null;
            }

            target = Environment.ExpandEnvironmentVariables(target);
            if (!IsSafeLocalPreviewTarget(target))
            {
                return null;
            }

            return File.Exists(target) || Directory.Exists(target) ? target : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? instance)
    {
        try
        {
            if (instance is not null && System.Runtime.InteropServices.Marshal.IsComObject(instance))
            {
                System.Runtime.InteropServices.Marshal.FinalReleaseComObject(instance);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private void UpdateOpenControl(string label, IReadOnlyList<(string Label, Action Action)> secondaryActions)
    {
        var palette = Theme.Current;
        var hasMenu = secondaryActions.Count > 0;

        // A Grid, not a StackPanel: when the header runs out of width the
        // label column shrinks and ellipsizes while the divider and chevron
        // keep their place. A StackPanel measures children unconstrained, so
        // it clips mid-glyph and pushes the chevron out of the pill entirely.
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var container = new Border
        {
            Width = MeasureOpenPillWidth(label, hasMenu),
            CornerRadius = Metrics.PillCorner,
            Background = Theme.Brush(palette.SurfaceBg),
            BorderBrush = Theme.Brush(palette.SurfaceBorder),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = panel
        };

        var openHalf = new Border
        {
            CornerRadius = hasMenu ? Metrics.PillLeftCorner : new CornerRadius(Metrics.RadiusPill - 1),
            Padding = new Thickness(
                Metrics.PillPadding.Left,
                Metrics.PillPadding.Top,
                hasMenu ? Metrics.PillInnerPadding : Metrics.PillPadding.Right,
                Metrics.PillPadding.Bottom),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = "Open (Enter)",
            Child = new TextBlock
            {
                Text = label,
                FontSize = Metrics.FontLabel,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = hasMenu ? HeaderOpenLabelWithMenuMaxWidth : HeaderOpenLabelMaxWidth,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush(palette.TextPrimary)
            }
        };
        AutomationProperties.SetName(openHalf, label);
        AutomationProperties.SetHelpText(openHalf, "Open the previewed item");
        openHalf.MouseEnter += (_, _) => openHalf.Background = Theme.Brush(palette.HoverBg);
        openHalf.MouseLeave += (_, _) => openHalf.Background = Brushes.Transparent;
        openHalf.MouseLeftButtonDown += (_, e) => e.Handled = true;
        openHalf.MouseLeftButtonUp += (_, e) =>
        {
            OpenCurrent();
            e.Handled = true;
        };
        Grid.SetColumn(openHalf, 0);
        panel.Children.Add(openHalf);

        if (hasMenu)
        {
            var divider = new Border
            {
                Width = 1,
                Margin = new Thickness(0, 5, 0, 5),
                Background = Theme.Brush(palette.SurfaceBorder)
            };
            Grid.SetColumn(divider, 1);
            panel.Children.Add(divider);

            var menuHalf = new Border
            {
                CornerRadius = Metrics.PillRightCorner,
                Padding = new Thickness(9, Metrics.PillPadding.Top, Metrics.PillInnerPadding, Metrics.PillPadding.Bottom),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = "More ways to open",
                Child = new TextBlock
                {
                    Text = "\uE70D",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 8,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Theme.Brush(palette.TextSecondary)
                }
            };
            AutomationProperties.SetName(menuHalf, "More open actions");
            menuHalf.MouseEnter += (_, _) => menuHalf.Background = Theme.Brush(palette.HoverBg);
            menuHalf.MouseLeave += (_, _) => menuHalf.Background = Brushes.Transparent;
            menuHalf.MouseLeftButtonDown += (_, e) => e.Handled = true;
            menuHalf.MouseLeftButtonUp += (_, e) =>
            {
                ShowOpenActionsMenu(container, secondaryActions);
                e.Handled = true;
            };
            Grid.SetColumn(menuHalf, 2);
            panel.Children.Add(menuHalf);
        }

        OpenControl.Content = container;

        // Swapping Content while the window is hidden (Space-close, then
        // Space-open on another file) leaves the header Grid's cached measure
        // for this column at the previous pill's width; the new, wider pill
        // then renders clipped into the old slot. Explicitly dirty the
        // control's measure so the header re-measures on the next pass.
        OpenControl.InvalidateMeasure();

        container.Loaded += (_, _) => DiagnosticLog.Write(
            $"openpill label='{label}' container={container.ActualWidth:0} open={openHalf.ActualWidth:0} control={OpenControl.ActualWidth:0}");
    }

    private static double MeasureOpenPillWidth(string label, bool hasMenu)
    {
        var labelCap = hasMenu ? HeaderOpenLabelWithMenuMaxWidth : HeaderOpenLabelMaxWidth;
        var textWidth = Math.Clamp(label.Length * 6.4, 34, labelCap);
        var chromeWidth = hasMenu ? 54 : 28;
        var minimum = hasMenu ? HeaderOpenMenuMinWidth : HeaderOpenMinWidth;
        return Math.Clamp(textWidth + chromeWidth, minimum, HeaderOpenMaxWidth);
    }

    private void ShowOpenActionsMenu(FrameworkElement anchor, IReadOnlyList<(string Label, Action Action)> actions)
    {
        // The outside-click dismissal closes the menu on mouse DOWN; without
        // this guard the mouse UP on the chevron instantly reopens it, so
        // clicking the chevron a second time could fail to collapse the flyout.
        if (DateTime.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(300))
        {
            return;
        }

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("PreviewContextMenu"),
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Custom
        };

        // Own the placement outright: right edge flush with the pill, one gap
        // below it. Post-open offset nudging conflicts with WPF's own edge
        // adjustment and loses.
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) =>
        [
            new System.Windows.Controls.Primitives.CustomPopupPlacement(
                new Point(targetSize.Width - popupSize.Width, targetSize.Height + Metrics.FlyoutGap),
                System.Windows.Controls.Primitives.PopupPrimaryAxis.Horizontal)
        ];

        Theme.ApplyResources(menu);

        foreach (var (label, action) in actions)
        {
            var item = new MenuItem { Header = label, Style = (Style)FindResource("PreviewMenuItem") };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        TrackOwnedPopup(menu);
        menu.Closed += (_, _) => _flyoutClosedAt = DateTime.UtcNow;
        menu.IsOpen = true;
    }

    private void TrackOwnedPopup(ContextMenu menu)
    {
        menu.Opened += (_, _) => RegisterOwnedPopup(menu);
        menu.Closed += (_, _) => UnregisterOwnedPopup(menu);
    }

    private void RegisterOwnedPopup(ContextMenu menu)
    {
        Capture();
        Dispatcher.BeginInvoke(Capture, DispatcherPriority.Loaded);

        void Capture()
        {
            var hwnd = (PresentationSource.FromVisual(menu) as HwndSource)?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            lock (_popupSync)
            {
                _ownedPopupHandles[menu] = hwnd;
            }
        }
    }

    private void UnregisterOwnedPopup(ContextMenu menu)
    {
        lock (_popupSync)
        {
            _ownedPopupHandles.Remove(menu);
        }
    }

    private void RememberWebViewProcesses(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        var processIds = new List<uint>();
        if (core.BrowserProcessId != 0)
        {
            processIds.Add(core.BrowserProcessId);
        }

        try
        {
            foreach (var processInfo in core.Environment.GetProcessInfos())
            {
                if (processInfo.ProcessId > 0)
                {
                    processIds.Add((uint)processInfo.ProcessId);
                }
            }
        }
        catch
        {
            // The browser process id above is enough for normal menu routing.
        }

        if (processIds.Count == 0)
        {
            return;
        }

        lock (_popupSync)
        {
            foreach (var processId in processIds)
            {
                _webViewProcessIds.Add(processId);
            }
        }
    }

    private void ArmWebContextMenu(Microsoft.Web.WebView2.Core.CoreWebView2 core)
    {
        RememberWebViewProcesses(core);
        Interlocked.Exchange(ref _webContextMenuArmedUntilTicks, DateTime.UtcNow.AddSeconds(20).Ticks);
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void HeaderBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        // Position is session-local by design: the stage recenters on the
        // next fresh open. Size is the only geometry a drag can redefine,
        // and the border-resize hook owns that.
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// One maximize system: the OS's. Snap assist, F11, the header button,
    /// and a video's fullscreen request all drive WindowState, so no
    /// private flag can ever disagree with what is on screen, and the OS
    /// owns the restore bounds. WM_GETMINMAXINFO keeps a maximized
    /// borderless window on the work area instead of over the taskbar.
    /// </summary>
    private bool IsStageMaximized => WindowState == WindowState.Maximized;

    private void ToggleMaximize()
    {
        WindowState = IsStageMaximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void RestoreFromNativeMinimize()
    {
        if (WindowState != WindowState.Minimized)
        {
            return;
        }

        WindowState = WindowState.Normal;
        DiagnosticLog.Write("stage restored from native minimize");
    }

    private void SyncNativeWindowState()
    {
        if (WindowState == WindowState.Maximized)
        {
            // However the window got here, a dragged item size no longer
            // applies.
            _itemStageSize = null;
            DiagnosticLog.Write("stage maximized");
        }
        else
        {
            _contentDrivenMaximize = false;
            DiagnosticLog.Write($"stage {(WindowState == WindowState.Minimized ? "minimized" : "restored")}");
        }

        UpdateMaximizeChrome();

        // Idempotent insurance: state changes are the one runtime moment the
        // shell re-evaluates a window, so re-assert the derived presence.
        SyncShellPresence();
    }

    private void UpdateMaximizeChrome()
    {
        MaximizeButton.Content = IsStageMaximized ? RestoreGlyph : MaximizeGlyph;
        MaximizeButton.ToolTip = IsStageMaximized ? "Restore size (F11)" : "Fill screen (F11)";
        AutomationProperties.SetName(MaximizeButton, IsStageMaximized ? "Restore preview size" : "Fill screen");
    }

    // ---------------------------------------------------------------------
    // Geometry. Changes wait for the dissolve and then happen in one step,
    // while the preview is blank: animating a window resize makes WPF title
    // text and browser children relayout at every intermediate size.
    // ---------------------------------------------------------------------

    /// <summary>
    /// Places the stage: centered on the Explorer window's monitor, at the
    /// current item's shape when it is known, otherwise the
    /// resolution-derived default. Runs once per open; while browsing, the
    /// stage retakes each item's shape during the blank beat instead.
    /// </summary>
    private void ApplyStageGeometry(IntPtr explorerHwnd)
    {
        if (_stageFrozenForProbe)
        {
            return;
        }

        var workArea = GetWorkAreaDips(explorerHwnd);
        ApplyFunctionalMinimum(workArea);
        var size = ComputeStageTarget(workArea, explorerHwnd);
        var anchor = _isFileDialogMode
            ? TryGetWindowRectDips(_placementOwnerHwnd)
            : null;
        SetGeometry(StagePlacement.CenterAndClamp(size, workArea, anchor), explorerHwnd);
    }

    /// <summary>
    /// The current item's shape request. One rule governs lateness: a hint
    /// applies only while its item is unrevealed. Before the reveal it may
    /// still retake the stage, since the content is hidden or dissolving;
    /// after the reveal it is dropped and the stage holds until the next
    /// item. A stale version is dropped too, so browsing away cancels a slow
    /// probe's hint by construction.
    /// </summary>
    internal void SetStageFitHint(StageFitHint hint, long version)
    {
        if (version != _loadVersion || _loadFinished)
        {
            return;
        }

        _stageFitHint = hint;
        _stageFitVersion = version;

        // An async hint (media metadata, a finished conversion) lands after
        // the swap started. Retake the stage only once the outgoing content
        // has left the screen; the staging beat consumes the hint otherwise.
        if (IsVisible
            && PreviewContent.Content is UIElement staged
            && staged.Visibility != Visibility.Visible)
        {
            RetargetStageToFit();
        }
    }

    /// <summary>
    /// One-step stage change during a blank beat: scale around the current
    /// center so a window the user moved stays where they put it, clamped
    /// so every edge stays on its monitor.
    /// </summary>
    private bool RetargetStageToFit()
    {
        if (!IsVisible || Hwnd == IntPtr.Zero)
        {
            return false;
        }

        var workArea = GetWorkAreaDips(Hwnd);
        ApplyFunctionalMinimum(workArea);
        var target = ComputeStageTarget(workArea, Hwnd);
        var recenter = _recenterStage;
        if (!recenter
            && Math.Abs(target.Width - Width) <= StageFit.Hysteresis
            && Math.Abs(target.Height - Height) <= StageFit.Hysteresis)
        {
            return false;
        }

        if (_stageFrozenForProbe || IsStageMaximized || _itemStageSize is not null)
        {
            DiagnosticLog.Write($"stage fit held ({(_itemStageSize is not null ? "user size" : IsStageMaximized ? "maximized" : "probe")}) -> {target.Width:0}x{target.Height:0}");
            return false;
        }

        _recenterStage = false;

        // Scale around the current center so a window the user MOVED stays
        // where they put it. After a resize the center is an accident of
        // the drag, so the stage retakes the middle of the work area.
        var recentered = StagePlacement.CenterAndClamp(
            target,
            workArea,
            _isFileDialogMode ? TryGetWindowRectDips(_placementOwnerHwnd) : null);
        var left = recenter ? recentered.Left : Left + (Width - target.Width) / 2;
        var top = recenter ? recentered.Top : Top + (Height - target.Height) / 2;
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - target.Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - target.Height));
        SetGeometry(new Rect(left, top, target.Width, target.Height), Hwnd);
        DiagnosticLog.Write($"stage fit -> {target.Width:0}x{target.Height:0} ({DescribeStageFit()}{(recenter ? ", recentered" : "")})");
        return true;
    }

    /// <summary>The stage size the current item has earned on this monitor.</summary>
    private Size ComputeStageTarget(Rect workArea, IntPtr dpiHwnd)
    {
        var hint = _stageFitVersion == _loadVersion ? _stageFitHint : null;
        if (hint is null)
        {
            return StageSize(workArea);
        }

        return StageFit.WindowFor(
            hint.Value,
            workArea,
            DpiScaleFor(dpiHwnd == IntPtr.Zero ? Hwnd : dpiHwnd),
            PdfPageRailWidth,
            ViewerChromeBandHeight,
            new Size(StageMinWidth, StageMinHeight));
    }

    private string DescribeStageFit() =>
        _stageFitVersion == _loadVersion && _stageFitHint is { } hint ? hint.Describe() : "default";

    private static double DpiScaleFor(IntPtr hwnd)
    {
        var dpi = hwnd == IntPtr.Zero ? 96 : NativeMethods.GetDpiForWindow(hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>
    /// The shapes knowable before anything loads: an image header names its
    /// pixels (transposed when EXIF says the camera was turned), audio is
    /// compact by identity. Network paths skip the header read; the async
    /// lanes hint later, still inside the blank beat. Shape is a nicety -
    /// on any doubt the default stage is always correct.
    /// </summary>
    private void TrySetIntrinsicStageFit(string contentPath, long version)
    {
        try
        {
            if (FileKinds.IsAudio(contentPath))
            {
                SetStageFitHint(StageFitHint.Compact(), version);
                return;
            }

            if (!LooksLikeLocalDrivePath(contentPath) || !File.Exists(contentPath))
            {
                return;
            }

            if (FileKinds.IsSvg(contentPath) && SvgSizeProbe.TryReadCanvasSize(contentPath, out var svgSize))
            {
                SetStageFitHint(StageFitHint.ImagePixels(svgSize.Width, svgSize.Height), version);
                return;
            }

            if ((FileKinds.IsImage(contentPath) || FileKinds.IsBrowserImage(contentPath) || FileKinds.IsPhotoshop(contentPath))
                && TryGetImagePixelSize(contentPath, out var pixels))
            {
                var oriented = ImageOrientation.SwapsAxes(ImageOrientation.Read(contentPath))
                    ? new Size(pixels.Height, pixels.Width)
                    : pixels;
                SetStageFitHint(StageFitHint.ImagePixels(oriented.Width, oriented.Height), version);
                return;
            }

            if (FileKinds.IsMedia(contentPath)
                && !FileKinds.IsAudio(contentPath)
                && Mp4CodecSniffer.TryRead(contentPath) is { DisplayWidth: { } videoWidth, DisplayHeight: { } videoHeight })
            {
                SetStageFitHint(StageFitHint.Pixels(videoWidth, videoHeight), version);
                return;
            }

            if (FileKinds.IsPdf(contentPath)
                && PdfPageSizeProbe.TryReadFirstPageSize(contentPath, out var pageWidth, out var pageHeight))
            {
                SetStageFitHint(StageFitHint.PageAspect(pageWidth / pageHeight), version);
                return;
            }

            if (OfficeOpenXmlPreviewLoader.IsSupported(contentPath))
            {
                // Spreadsheets convert to HTML, not pages: no PDF to probe
                // and no meaningful page size to declare. They ask for the
                // broad workspace directly.
                if (FileKinds.IsSpreadsheet(contentPath))
                {
                    SetStageFitHint(StageFitHint.Sheet(), version);
                    return;
                }

                if (OfficeDocumentPdfConverter.TryGetCachedPdfConversion(contentPath) is { } cached
                    && PdfPageSizeProbe.TryReadFirstPageSize(cached.DocumentPath, out pageWidth, out pageHeight))
                {
                    SetStageFitHint(StageFitHint.PageAspect(pageWidth / pageHeight), version);
                    return;
                }

                if (OfficePageSizeProbe.TryReadFirstPageAspect(contentPath, out var aspect))
                {
                    SetStageFitHint(StageFitHint.PageAspect(aspect), version);
                }
            }
        }
        catch
        {
            // The default stage is always correct.
        }
    }

    private void SetGeometry(Rect rect, IntPtr dpiHwnd)
    {
        if (TrySetWindowRect(rect, dpiHwnd))
        {
            return;
        }

        Left = rect.X;
        Top = rect.Y;
        Width = rect.Width;
        Height = rect.Height;
    }

    private bool TrySetWindowRect(Rect rect, IntPtr dpiHwnd)
    {
        if (Hwnd == IntPtr.Zero)
        {
            return false;
        }

        var referenceHwnd = dpiHwnd == IntPtr.Zero ? Hwnd : dpiHwnd;
        var dpi = referenceHwnd == IntPtr.Zero ? 96 : NativeMethods.GetDpiForWindow(referenceHwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = dpi / 96.0;
        return NativeMethods.SetWindowPos(
            Hwnd,
            IntPtr.Zero,
            (int)Math.Round(rect.X * scale),
            (int)Math.Round(rect.Y * scale),
            (int)Math.Round(rect.Width * scale),
            (int)Math.Round(rect.Height * scale),
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }


    /// <summary>
    /// The default stage: the home for content with no intrinsic shape, and
    /// for every doubt. Clamped to the monitor it opens on, and never small
    /// enough to break the content or the header.
    /// </summary>
    internal static Size StageSize(Rect workArea)
    {
        var maxWidth = Math.Max(1, workArea.Width);
        var maxHeight = Math.Max(1, workArea.Height);
        return new Size(
            Math.Clamp(workArea.Width * 0.54, Math.Min(StageMinWidth, maxWidth), Math.Min(900, maxWidth)),
            Math.Clamp(workArea.Height * 0.615, Math.Min(StageMinHeight, maxHeight), Math.Min(675, maxHeight)));
    }

    private void ApplyFunctionalMinimum(Rect workArea)
    {
        // On a small RDP desktop or a 300%-scaled 1080p monitor, 640x480
        // DIPs can be larger than the usable desktop. The work area wins so
        // the close button and every edge remain reachable; the normal floor
        // returns automatically on the next larger monitor.
        MinWidth = Math.Min(StageMinWidth, Math.Max(1, workArea.Width));
        MinHeight = Math.Min(StageMinHeight, Math.Max(1, workArea.Height));
    }

    internal static bool TryGetImagePixelSize(string path, out Size size)
    {
        size = Size.Empty;
        if (FileKinds.IsPhotoshop(path))
        {
            return PsdPreviewLoader.TryReadPixelSize(path, out size);
        }

        // PNG puts its dimensions in the fixed IHDR header. Asking WIC to
        // construct a decoder just to read these 24 bytes cost ~34 ms on the
        // giant Downloads sample and this method runs in the input path.
        if (TryReadPngPixelSize(path, out size))
        {
            return true;
        }

        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            size = new Size(Math.Max(1, frame.PixelWidth), Math.Max(1, frame.PixelHeight));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadPngPixelSize(string path, out Size size)
    {
        size = Size.Empty;
        if (!Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            Span<byte> header = stackalloc byte[24];
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.ReadExactly(header);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (!header[..8].SequenceEqual(signature)
                || !header[12..16].SequenceEqual("IHDR"u8))
            {
                return false;
            }

            var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
            var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
            if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
            {
                return false;
            }

            size = new Size(width, height);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static Rect GetWorkAreaDips(IntPtr hwnd)
    {
        try
        {
            var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
            };

            if (monitor != IntPtr.Zero && NativeMethods.GetMonitorInfo(monitor, ref info))
            {
                var dpi = hwnd == IntPtr.Zero ? 96 : NativeMethods.GetDpiForWindow(hwnd);
                if (dpi == 0)
                {
                    dpi = 96;
                }

                var scale = 96.0 / dpi;
                return new Rect(
                    info.rcWork.Left * scale,
                    info.rcWork.Top * scale,
                    info.rcWork.Width * scale,
                    info.rcWork.Height * scale);
            }
        }
        catch
        {
            // Fall back to primary work area below.
        }

        return SystemParameters.WorkArea;
    }

    private static Rect? TryGetWindowRectDips(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var scale = 96.0 / dpi;
        return new Rect(
            rect.Left * scale,
            rect.Top * scale,
            rect.Width * scale,
            rect.Height * scale);
    }

    private void ApplyWindowNativeStyle()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        Hwnd = hwnd;

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
        SyncShellPresence();

        // A border-drag holds the stage size for the rest of this open;
        // the next open returns to content rules. WM_SIZING fires only for
        // interactive border resizes (never for layout or DPI changes), and
        // WM_EXITSIZEMOVE commits the final size once the user lets go.
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);

        ApplyThemeToDwm();

        var cornerPreference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SIZING = 0x0214;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            // A borderless window maximizes over the taskbar by default;
            // cap it to the work area so maximized still means "fill the
            // desktop", the same promise the button has always made.
            CapMaximizedBoundsToWorkArea(hwnd, lParam, ref handled);
        }
        else if (msg == WM_SIZING)
        {
            _userResizingStage = true;
        }
        else if (msg == WM_EXITSIZEMOVE && _userResizingStage)
        {
            _userResizingStage = false;
            if (WindowState == WindowState.Normal)
            {
                _itemStageSize = new Size(Width, Height);
                DiagnosticLog.Write($"stage resized to {Width:0}x{Height:0} (held for this item)");
            }
            else
            {
                _itemStageSize = null;
                DiagnosticLog.Write("stage resize ignored after native snap/maximize");
            }
        }

        return IntPtr.Zero;
    }

    private static void CapMaximizedBoundsToWorkArea(IntPtr hwnd, IntPtr minMaxInfo, ref bool handled)
    {
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMethods.MONITORINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        var limits = System.Runtime.InteropServices.Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(minMaxInfo);
        limits.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        limits.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        limits.ptMaxSize.X = info.rcWork.Width;
        limits.ptMaxSize.Y = info.rcWork.Height;
        System.Runtime.InteropServices.Marshal.StructureToPtr(limits, minMaxInfo, fDeleteOld: false);
        handled = true;

        // Only worth a line when the taskbar actually takes space.
        if (info.rcWork.Width != info.rcMonitor.Width || info.rcWork.Height != info.rcMonitor.Height)
        {
            DiagnosticLog.Write($"maximize capped to work area {info.rcWork.Width}x{info.rcWork.Height}");
        }
    }

    /// <summary>Dev probe only: parked probe windows must never re-place themselves.</summary>
    internal void MarkGeometryUserPinnedForProbe() => _stageFrozenForProbe = true;

    private void ApplyThemeToDwm()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var darkMode = Theme.Current.IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsPinned)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        DismissRequested?.Invoke(this, EventArgs.Empty);
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (PinToggleRequested is null)
        {
            // Standalone (probe): no controller to arbitrate, toggle directly.
            SetPinned(!IsPinned);
            return;
        }

        PinToggleRequested.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(bool pinned)
    {
        IsPinned = pinned;

        // Fresh default at every pin-state change: the next pin starts from
        // the user's chosen default, and a window retiring to transient never
        // inherits a pinned window's per-window choice. (Transients float
        // regardless; the bit only matters while pinned.)
        AlwaysOnTopButton.IsChecked = Services.Settings.Current.PinnedAlwaysOnTopDefault;
        SetAlwaysOnTopVisible(pinned);
        ApplyTopmost();

        PinButton.IsEnabled = true;
        PinButton.Content = pinned ? UnpinGlyph : PinGlyph;
        PinButton.ToolTip = pinned ? "Unpin" : "Pin (stays open)";
        AutomationProperties.SetName(PinButton, pinned ? "Unpin preview" : "Pin preview");
        CloseButton.ToolTip = pinned ? "Close (Esc)" : "Close (Esc or Space)";
        if (pinned)
        {
            PinButton.Foreground = Theme.Brush(Theme.Current.Accent);
        }
        else
        {
            // Back to the style's themed foreground.
            PinButton.ClearValue(ForegroundProperty);
        }

        DiagnosticLog.Write($"pin state {(pinned ? "pinned" : "unpinned")} path={CurrentPath}");
        if (pinned)
        {
            // A pinned window leaves browse mode; the set surfaces go with it.
            DetachBrowseContext(restoreSlideshowSize: true);
        }

        SetBrowseControlsVisible(CurrentPath is not null && !pinned && !_isFileDialogMode);
    }

    private void AlwaysOnTopButton_CheckedChanged(object sender, RoutedEventArgs e)
    {
        _pinnedAlwaysOnTop = AlwaysOnTopButton.IsChecked == true;
        ApplyTopmost();
        if (IsPinned)
        {
            DiagnosticLog.Write($"always-on-top {(_pinnedAlwaysOnTop ? "on" : "off")} path={CurrentPath}");
        }
    }

    /// <summary>
    /// Opacity-only reveal into space the pin action just recomposed; the
    /// window controls to its right never move. Honors the Windows animation
    /// preference by snapping instead of fading.
    /// </summary>
    private void SetAlwaysOnTopVisible(bool visible)
    {
        if (visible == (AlwaysOnTopButton.Visibility == Visibility.Visible))
        {
            return;
        }

        AlwaysOnTopButton.BeginAnimation(OpacityProperty, null);
        if (!visible)
        {
            AlwaysOnTopButton.Visibility = Visibility.Collapsed;
            AlwaysOnTopButton.Opacity = 0;
            return;
        }

        AlwaysOnTopButton.Visibility = Visibility.Visible;
        if (!SystemParameters.ClientAreaAnimation)
        {
            AlwaysOnTopButton.Opacity = 1;
            return;
        }

        AlwaysOnTopButton.Opacity = 0;
        AlwaysOnTopButton.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
    }

    private void BrowsePreviousButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Previous);
    }

    private void BrowseNextButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseRequested?.Invoke(this, PreviewBrowseDirection.Next);
    }

    /// <summary>
    /// At the pin cap the button stays until a slot frees up; the tooltip
    /// says how to free one. The unpin control remains available.
    /// </summary>
    public void SetPinLimitReached(bool reached)
    {
        if (IsPinned)
        {
            return;
        }

        PinButton.IsEnabled = !reached;
        PinButton.ToolTip = reached
            ? "Up to 6 previews can stay pinned. Unpin one to pin this."
            : "Pin (stays open)";
    }

    // ---------------------------------------------------------------------
    // Multi-selection surfaces: the index sheet and the slideshow. Both are
    // views over the same captured set the chevrons walk; the controller
    // owns the set, this window owns how it looks.
    // ---------------------------------------------------------------------

    internal bool IsGridViewOpen => _gridView is not null;

    internal bool IsSlideshowActive => _slideshowTimer?.IsEnabled == true;

    internal void SetSelectionCount(int selectedCount)
    {
        if (CurrentPath is { } path)
        {
            ApplyHeaderSubtitle(path, _shortcutTarget, Math.Max(1, selectedCount));
        }
    }

    /// <summary>
    /// The controller hands over the captured selection set (null when
    /// browsing left it or the selection collapsed to one item).
    /// </summary>
    internal void SetMultiSelection(IReadOnlyList<string>? paths, int index)
    {
        var setChanged = !ReferenceEquals(_multiSelectionPaths, paths);
        _multiSelectionPaths = paths;
        _multiSelectionIndex = index;

        if (paths is null)
        {
            CloseGridView();
            StopSlideshow(restoreSize: true);
            GridButton.Visibility = Visibility.Collapsed;
            PlayButton.Visibility = Visibility.Collapsed;
            return;
        }

        GridButton.Visibility = Visibility.Visible;
        PlayButton.Visibility = Visibility.Visible;
        if (setChanged)
        {
            // A different selection means a different sheet.
            CloseGridView();
            StopSlideshow(restoreSize: true);
        }
    }

    /// <summary>The preview moved within the set; sheet cursor and slideshow cadence follow.</summary>
    internal void SetMultiSelectionIndex(int index)
    {
        _multiSelectionIndex = index;
        _gridView?.SetCurrentIndex(index);
        if (IsSlideshowActive)
        {
            // Manual steps restart the beat so the next auto-advance gets a
            // full interval on screen.
            _slideshowTimer!.Stop();
            _slideshowTimer.Start();
        }
    }

    internal void ToggleGridView()
    {
        if (_gridView is not null)
        {
            CloseGridView();
            return;
        }

        if (_multiSelectionPaths is not { Count: > 1 } paths)
        {
            return;
        }

        StopSlideshow(restoreSize: false);
        var grid = new SelectionGridView(paths, _multiSelectionIndex);
        grid.ItemInvoked += index =>
        {
            CloseGridView();
            SelectionItemRequested?.Invoke(this, index);
        };
        ApplyScrollBarStyle(grid.Root);

        var host = (Grid)PreviewContent.Parent;
        host.Children.Add(grid.Root);
        grid.Root.Opacity = 0;
        grid.Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        _gridView = grid;
        GridButton.Foreground = Theme.Brush(Theme.Current.Accent);
        DiagnosticLog.Write($"grid view open ({paths.Count} items)");
    }

    internal void CloseGridView()
    {
        if (_gridView is not { } grid)
        {
            return;
        }

        _gridView = null;
        grid.Detach();
        ((Grid)PreviewContent.Parent).Children.Remove(grid.Root);
        GridButton.ClearValue(ForegroundProperty);
        DiagnosticLog.Write("grid view closed");
    }

    /// <summary>Arrow keys walk the sheet while it is up.</summary>
    internal void MoveGridCursor(int deltaColumns, int deltaRows) => _gridView?.MoveCursor(deltaColumns, deltaRows);

    /// <summary>Enter commits the sheet's cursor to the preview.</summary>
    internal void CommitGridCursor() => _gridView?.Commit();

    private void GridButton_Click(object sender, RoutedEventArgs e) => ToggleGridView();

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsSlideshowActive)
        {
            // The slideshow returns the size it borrowed; leaving the window
            // maximized after stopping latched every following preview to
            // one size.
            StopSlideshow(restoreSize: true);
        }
        else
        {
            StartSlideshow();
        }
    }

    private void StartSlideshow()
    {
        if (_multiSelectionPaths is not { Count: > 1 })
        {
            return;
        }

        CloseGridView();
        if (!IsStageMaximized)
        {
            _slideshowRestoresSize = true;
            ToggleMaximize();
        }

        _slideshowTimer ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(4)
        };
        _slideshowTimer.Tick -= OnSlideshowTick;
        _slideshowTimer.Tick += OnSlideshowTick;
        _slideshowTimer.Start();
        PlayButton.Content = StopSlideshowGlyph;
        PlayButton.ToolTip = "Stop slideshow (Esc)";
        AutomationProperties.SetName(PlayButton, "Stop slideshow");
        DiagnosticLog.Write("slideshow started");
    }

    private void StopSlideshow(bool restoreSize)
    {
        var wasActive = IsSlideshowActive;
        _slideshowTimer?.Stop();
        PlayButton.Content = PlayGlyph;
        PlayButton.ToolTip = "Slideshow";
        AutomationProperties.SetName(PlayButton, "Slideshow");
        if (restoreSize && _slideshowRestoresSize && IsStageMaximized)
        {
            ToggleMaximize();
        }

        _slideshowRestoresSize = false;
        if (wasActive)
        {
            DiagnosticLog.Write("slideshow stopped");
        }
    }

    private void OnSlideshowTick(object? sender, EventArgs e) => SlideshowAdvanceRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Progressive escape: the sheet closes first, then a running slideshow
    /// stops, and only then does Esc mean dismiss. One key, nearest surface.
    /// </summary>
    internal bool TryConsumeEscapeLayer()
    {
        if (_gridView is not null)
        {
            CloseGridView();
            return true;
        }

        if (IsSlideshowActive)
        {
            StopSlideshow(restoreSize: true);
            return true;
        }

        return false;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.C)
        {
            // Selected text copies as text; otherwise Ctrl+C copies the file
            // itself.
            if (Keyboard.FocusedElement is TextBox { SelectionLength: > 0 } textBox)
            {
                textBox.Copy();
            }
            else if (Keyboard.FocusedElement is RichTextBox richTextBox && !richTextBox.Selection.IsEmpty)
            {
                richTextBox.Copy();
            }
            else if (FindVisualChild<WebView2>(PreviewContent) is { } webView)
            {
                DiagnosticLog.Write("ctrl+c reached window with webview present");
                _ = CopyWebViewSelectionAsync(webView);
            }
            else
            {
                CopyCurrentFile();
            }

            e.Handled = true;
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && (Keyboard.Modifiers & ModifierKeys.Alt) != ModifierKeys.Alt
            && TryAdjustZoomFromShortcut(KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key)))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                ToggleGridView();
            }
            else if (IsGridViewOpen)
            {
                CommitGridCursor();
            }
            else
            {
                OpenCurrent();
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.R && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            RotateCurrentImage((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? -1 : 1);
            e.Handled = true;
            return;
        }

        // While the sheet is up the arrows walk its cursor, whatever the
        // content underneath would normally do with them.
        if (IsGridViewOpen && Keyboard.Modifiers == ModifierKeys.None
            && e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            MoveGridCursor(
                e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 },
                e.Key switch { Key.Up => -1, Key.Down => 1, _ => 0 });
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F11)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        // Left and right browse the folder even when the preview holds the
        // keyboard - same as the chevrons, under the same one policy the
        // hook consults (media keeps its seek, text keeps its caret).
        if (e.Key is Key.Left or Key.Right
            && Keyboard.Modifiers == ModifierKeys.None
            && ShouldBrowseWithPreviewArrowKeys(vertical: false))
        {
            BrowseRequested?.Invoke(this, e.Key == Key.Left ? PreviewBrowseDirection.Previous : PreviewBrowseDirection.Next);
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Escape or Key.Space)
        {
            if (e.Key == Key.Escape && TryConsumeEscapeLayer())
            {
                e.Handled = true;
                return;
            }

            if (!IsPinned)
            {
                DismissRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }

            // Pinned: Space stays with the content (play/pause, page down),
            // but Esc is the universal escape hatch - it closes this window
            // the same way as the close button.
            if (e.Key == Key.Escape)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
                return;
            }
        }

        base.OnPreviewKeyDown(e);
    }
}
