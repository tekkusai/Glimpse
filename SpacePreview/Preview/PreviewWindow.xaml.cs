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

        _ = ShowLoadingOverlayLaterAsync(version, freshOpen ? 240 : 420, token);

        // A conversion still running after a beat gets named, nothing more.
        if (FileKinds.IsOffice(contentPath) && OfficeDocumentPdfConverter.EngineName is { } engine)
        {
            _ = ShowLoadingOverlayLaterAsync(version, 1200, token, ($"Converting with {engine}", string.Empty));
        }

        _ = LoadPreviewAsync(path, contentPath, version, token);
        return presented;
    }

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
            Background = Theme.Brush(palette.RaisedBg),
            BorderBrush = Theme.Brush(palette.RaisedBorder),
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
            Background = Theme.Brush(palette.CanvasBg),
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
                else if (FileKinds.GetOfficeKind(contentPath) is not OfficeKind.None and var officeKind)
                {
                    content = await LoadOfficePreviewAsync(contentPath, officeKind, version, token);
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

        if (FileKinds.GetOfficeKind(zipPackage) is not OfficeKind.None and var officeKind)
        {
            return await LoadOfficePreviewAsync(path, officeKind, version, token);
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

            if (FileKinds.IsOffice(contentPath))
            {
                // Spreadsheets convert to HTML, not pages: no PDF to probe
                // and no meaningful page size to declare. They ask for the
                // broad workspace directly.
                if (FileKinds.IsSpreadsheet(contentPath))
                {
                    SetStageFitHint(StageFitHint.Sheet(), version);
                    return;
                }

                if (OfficeDocumentPdfConverter.TryGetCached(contentPath) is { } cached
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
}
