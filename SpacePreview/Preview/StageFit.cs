using System;
using System.Windows;

namespace SpacePreview.Preview;

/// <summary>
/// How a preview item asks the stage to take its shape. Pixel content
/// (images, video) is honest at its native size and is never inflated past
/// it. Page content (documents) has an aspect but no native pixels, so it
/// takes generous room at that aspect. Compact content (audio) wants the
/// floor. Formats with no intrinsic shape keep the default stage.
/// </summary>
internal readonly struct StageFitHint
{
    internal enum FitKind
    {
        Pixels,
        ImagePixels,
        PageAspect,
        Sheet,
        Compact
    }

    private StageFitHint(FitKind kind, double width, double height, double aspect, bool singlePage = false)
    {
        Kind = kind;
        Width = width;
        Height = height;
        Aspect = aspect;
        SinglePage = singlePage;
    }

    public FitKind Kind { get; }

    /// <summary>Native pixel width; meaningful for pixel-based fits.</summary>
    public double Width { get; }

    public double Height { get; }

    /// <summary>Width over height; meaningful for <see cref="FitKind.PageAspect"/>.</summary>
    public double Aspect { get; }

    /// <summary>A one-page document: its viewer shows no page rail, so the stage reserves none.</summary>
    public bool SinglePage { get; }

    public static StageFitHint Pixels(double pixelWidth, double pixelHeight) =>
        new(FitKind.Pixels, SafePixelDimension(pixelWidth), SafePixelDimension(pixelHeight), 0);

    public static StageFitHint ImagePixels(double pixelWidth, double pixelHeight) =>
        new(FitKind.ImagePixels, SafePixelDimension(pixelWidth), SafePixelDimension(pixelHeight), 0);

    /// <summary>
    /// A page shape, clamped to sane paper: nothing real is wider than 5:1
    /// or taller than 1:5, and a malformed aspect must not fold the stage.
    /// </summary>
    public static StageFitHint PageAspect(double widthOverHeight, bool singlePage = false) =>
        new(FitKind.PageAspect, 0, 0,
            double.IsFinite(widthOverHeight) ? Math.Clamp(widthOverHeight, 0.2, 5) : 1,
            singlePage);

    /// <summary>
    /// A spreadsheet: unbounded content with no page shape and no native
    /// pixels. It earns a broad workspace, not the generic default.
    /// </summary>
    public static StageFitHint Sheet() => new(FitKind.Sheet, 0, 0, 0);

    public static StageFitHint Compact() => new(FitKind.Compact, 0, 0, 0);

    private static double SafePixelDimension(double value) =>
        double.IsFinite(value) ? Math.Max(1, value) : 1;

    public string Describe() => Kind switch
    {
        FitKind.Pixels or FitKind.ImagePixels => $"{Width:0}x{Height:0} px",
        FitKind.PageAspect => SinglePage ? $"page {Aspect:0.00}, one page" : $"page {Aspect:0.00}",
        FitKind.Sheet => "sheet",
        _ => "compact"
    };
}

/// <summary>
/// The fit math: hint in, window size out. Pure and testable; the window
/// owns when a fit may actually be applied.
/// </summary>
internal static class StageFit
{
    /// <summary>Largest fraction of the work area content may claim.</summary>
    internal const double Band = 0.85;

    /// <summary>
    /// Large images look better with a little more room around them than
    /// documents do. Native-size images still stay native; this only affects
    /// oversized pixel content that has to be scaled down.
    /// </summary>
    internal const double PixelBand = 0.82;

    /// <summary>Still images get a little more breathing room than video.</summary>
    internal const double ImagePixelBand = 0.74;

    /// <summary>
    /// Landscape pages (decks, wide sheets) use a tighter band: at 0.85 a
    /// 16:9 deck reads as accidental-fullscreen; framed margins make the
    /// same shape feel deliberate.
    /// </summary>
    internal const double LandscapeBand = 0.68;

    /// <summary>Aspect above which a page counts as landscape.</summary>
    internal const double LandscapeAspect = 1.15;

    /// <summary>
    /// Spreadsheets scroll in both directions, so more window is simply
    /// more data on screen. They take a broad workspace, capped so a huge
    /// monitor still shows a preview rather than an application.
    /// </summary>
    internal const double SheetBand = 0.72;

    internal const double SheetHeightBand = 0.82;

    internal const double SheetMaxWidth = 1200;

    internal const double SheetMaxHeight = 900;

    /// <summary>
    /// Dead zone for rounding churn only. Meaningfully different shapes are
    /// always honored; this exists so DPI math cannot make the window
    /// tremble between two files that want the same stage.
    /// </summary>
    internal const double Hysteresis = 4;

    // The window's fixed chrome around the content viewport: 1px root
    // border on each side, plus the 52px header row (see PreviewWindow.xaml).
    internal const double ChromeWidth = 2;
    internal const double ChromeHeight = 54;

    /// <summary>
    /// The window size for a hint on a given monitor.
    /// <paramref name="dpiScale"/> converts native pixels to DIPs so "never
    /// upscaled" means device pixels, not logical ones.
    /// <paramref name="documentRailWidth"/> and <paramref name="documentToolbarHeight"/>
    /// are the rail and toolbar a document's viewer carries; page fits reserve
    /// both so the page itself gets the promised aspect.
    /// </summary>
    internal static Size WindowFor(
        StageFitHint hint,
        Rect workArea,
        double dpiScale,
        double documentRailWidth,
        double documentToolbarHeight,
        Size minWindow)
    {
        if (dpiScale <= 0 || !double.IsFinite(dpiScale))
        {
            dpiScale = 1;
        }

        var boxWidth = workArea.Width * Band - ChromeWidth;
        var boxHeight = workArea.Height * Band - ChromeHeight;

        double contentWidth;
        double contentHeight;
        var extraWidth = ChromeWidth;
        switch (hint.Kind)
        {
            case StageFitHint.FitKind.Pixels:
            case StageFitHint.FitKind.ImagePixels:
                var pixelBand = hint.Kind == StageFitHint.FitKind.ImagePixels ? ImagePixelBand : PixelBand;
                boxWidth = workArea.Width * pixelBand - ChromeWidth;
                boxHeight = workArea.Height * pixelBand - ChromeHeight;
                var dipWidth = hint.Width / dpiScale;
                var dipHeight = hint.Height / dpiScale;
                var ratio = Math.Min(1, Math.Min(boxWidth / dipWidth, boxHeight / dipHeight));
                contentWidth = dipWidth * ratio;
                contentHeight = dipHeight * ratio;
                break;

            case StageFitHint.FitKind.PageAspect:
                if (hint.Aspect > LandscapeAspect)
                {
                    boxWidth = workArea.Width * LandscapeBand - ChromeWidth;
                    boxHeight = workArea.Height * LandscapeBand - ChromeHeight;
                }

                var railWidth = hint.SinglePage ? 0 : documentRailWidth;
                var pageBoxWidth = boxWidth - railWidth;
                var pageHeight = boxHeight;
                contentWidth = pageHeight * hint.Aspect;
                if (contentWidth > pageBoxWidth)
                {
                    contentWidth = pageBoxWidth;
                    pageHeight = contentWidth / hint.Aspect;
                }

                contentHeight = pageHeight + Math.Max(0, documentToolbarHeight);
                extraWidth += railWidth;
                break;

            case StageFitHint.FitKind.Sheet:
                return Clamp(new Size(
                    Math.Min(workArea.Width * SheetBand, SheetMaxWidth),
                    Math.Min(workArea.Height * SheetHeightBand, SheetMaxHeight)),
                    workArea, minWindow);

            default:
                return Clamp(minWindow, workArea, minWindow);
        }

        var window = new Size(contentWidth + extraWidth, contentHeight + ChromeHeight);
        return Clamp(window, workArea, minWindow);
    }

    private static Size Clamp(Size window, Rect workArea, Size minWindow)
    {
        var maxWidth = Math.Max(1, workArea.Width);
        var maxHeight = Math.Max(1, workArea.Height);
        return new Size(
            Math.Clamp(window.Width, Math.Min(minWindow.Width, maxWidth), maxWidth),
            Math.Clamp(window.Height, Math.Min(minWindow.Height, maxHeight), maxHeight));
    }
}

/// <summary>Pure placement math shared by normal and picker-hosted previews.</summary>
internal static class StagePlacement
{
    internal static Rect CenterAndClamp(Size size, Rect workArea, Rect? anchor = null)
    {
        var center = anchor is { Width: > 0, Height: > 0 } candidate
            ? candidate
            : workArea;
        var left = center.Left + (center.Width - size.Width) / 2;
        var top = center.Top + (center.Height - size.Height) / 2;
        left = Math.Clamp(left, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        top = Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height));
        return new Rect(left, top, size.Width, size.Height);
    }
}
