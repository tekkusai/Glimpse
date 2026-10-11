using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>Where a preview's content will land, so its stand-in can wait there.</summary>
internal enum LandingKind
{
    /// <summary>No known shape: the stand-in is the bare canvas.</summary>
    None,

    /// <summary>A page in the Chromium PDF viewer, FitH under its toolbar, right of the rail.</summary>
    ViewerPage,

    /// <summary>Pixels fitted and centered: images and video.</summary>
    Pixels
}

/// <summary>
/// The best image of content that is not ready yet (docs/DESIGN.md,
/// Waiting): a poster blurred up to where the content will land, or a ghost
/// in its exact shape. Still at first; a slow sheen after a second; one
/// line of reason when a wait has one.
/// </summary>
internal sealed class StandIn : Panel
{
    // The Chromium viewer's FitH page, measured: 5 px from the viewer's left
    // edge, 8 px under its 40 px toolbar, 20 px short of its right edge with
    // a scrollbar beside it and 6 px short with none, 7 px of shadow under it.
    private const double ViewerPageLeft = 5;
    private const double ViewerPageTop = 8;
    private const double ViewerPageRightInset = 20;
    private const double ViewerPageRightMargin = 6;
    private const double ViewerPageFoot = 7;

    // A poster is honest when it is a picture, not an icon, and has the
    // content's shape.
    private const int MinPosterEdge = 192;
    private const double PosterAspectTolerance = 0.04;

    private readonly LandingKind _kind;
    private readonly double _aspect;
    private readonly Size _nativeSize;
    private readonly double _railWidth;
    private readonly double _toolbarHeight;
    private readonly double _dpiScale;
    private readonly Grid _visual = new() { ClipToBounds = true };
    private readonly Rectangle _sheen;
    private readonly TranslateTransform _sheenShift = new(-1, 0);
    private AnimationClock? _sheenClock;
    private readonly BlurEffect? _posterBlur;
    private readonly int _posterWidth;
    private readonly TextBlock _reason;

    private StandIn(
        LandingKind kind,
        double aspect,
        Size nativeSize,
        double railWidth,
        double toolbarHeight,
        double dpiScale,
        BitmapSource? poster)
    {
        _kind = kind;
        _aspect = aspect;
        _nativeSize = nativeSize;
        _railWidth = railWidth;
        _toolbarHeight = toolbarHeight;
        _dpiScale = Math.Max(1, dpiScale);
        SetResourceReference(BackgroundProperty, "Theme.CanvasBg");
        IsHitTestVisible = false;

        // A page taller than the window runs off its foot, as the real one
        // does, but never over the window's edge.
        ClipToBounds = true;

        if (poster is not null)
        {
            _posterBlur = new BlurEffect { KernelType = KernelType.Gaussian };
            _posterWidth = poster.PixelWidth;
            _visual.Children.Add(new Image { Source = poster, Stretch = Stretch.Fill, Effect = _posterBlur });
        }
        else if (kind != LandingKind.None)
        {
            _visual.Children.Add(new Border { BorderThickness = new Thickness(1) }
                .Themed(Border.BackgroundProperty, "Theme.RaisedBg")
                .Themed(Border.BorderBrushProperty, "Theme.RaisedBorder"));
        }

        // The sheen is a band in the brush, moved in brush space, so it
        // needs no size and sweeps whatever box it lands on.
        var band = new GradientStop(Colors.Transparent, 0.5);
        _sheen = new Rectangle
        {
            Visibility = Visibility.Collapsed,
            Fill = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                RelativeTransform = _sheenShift,
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0.3),
                    band,
                    new GradientStop(Colors.Transparent, 0.7)
                }
            }
        };
        Theme.Follow(_sheen, () => band.Color = Theme.Current.HoverBg);
        _visual.Children.Add(_sheen);
        Children.Add(_visual);

        _reason = new TextBlock
        {
            FontSize = Metrics.FontBody,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        Children.Add(_reason);
    }

    public string Description => _posterBlur is not null ? "poster" : _kind != LandingKind.None ? "ghost" : "canvas";

    /// <summary>
    /// The stand-in for one item: the poster when it is honest, otherwise a
    /// ghost in the content's exact shape, otherwise the bare canvas.
    /// </summary>
    public static StandIn Create(
        StageFitHint? hint,
        double dpiScale,
        double railWidth,
        double toolbarHeight,
        BitmapSource? poster)
    {
        var kind = LandingKind.None;
        double aspect = 0;
        var native = Size.Empty;

        // Every page-shaped preview (PDF, Illustrator, converted documents)
        // lands in the PDF viewer, and a lone page lands with no rail beside
        // it, as the stage reserved none.
        if (hint is { Kind: StageFitHint.FitKind.PageAspect } page)
        {
            kind = LandingKind.ViewerPage;
            aspect = page.Aspect;
            if (page.SinglePage)
            {
                railWidth = 0;
            }
        }
        else if (hint is { Kind: StageFitHint.FitKind.Pixels or StageFitHint.FitKind.ImagePixels } pixels)
        {
            kind = LandingKind.Pixels;
            aspect = pixels.Width / pixels.Height;

            // Images never upscale past their device pixels; video fills its box.
            if (pixels.Kind == StageFitHint.FitKind.ImagePixels)
            {
                native = new Size(pixels.Width / Math.Max(1, dpiScale), pixels.Height / Math.Max(1, dpiScale));
            }
        }

        var honest = kind != LandingKind.None && IsHonestPoster(poster, aspect);
        return new StandIn(kind, aspect, native, railWidth, toolbarHeight, dpiScale, honest ? poster : null);
    }

    /// <summary>Whether a hint says where the content lands: pages, images and video do.</summary>
    internal static bool KnowsShape(StageFitHint? hint) =>
        hint is { Kind: StageFitHint.FitKind.PageAspect or StageFitHint.FitKind.Pixels or StageFitHint.FitKind.ImagePixels };

    internal static bool IsHonestPoster(BitmapSource? poster, double aspect) =>
        poster is { PixelWidth: > 0, PixelHeight: > 0 }
        && aspect > 0
        && Math.Max(poster.PixelWidth, poster.PixelHeight) >= MinPosterEdge
        && Math.Abs((double)poster.PixelWidth / poster.PixelHeight / aspect - 1) < PosterAspectTolerance;

    /// <summary>Starts the slow sheen. With motion off the stand-in stays still.</summary>
    public void StartSheen()
    {
        if (!Motion.Enabled || _sheen.Visibility == Visibility.Visible)
        {
            return;
        }

        _sheen.Visibility = Visibility.Visible;
        var sweep = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2600),
            RepeatBehavior = RepeatBehavior.Forever
        };
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(-1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(
            1,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1700)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));

        // Held so Stop can end it: a forever clock merely detached keeps
        // WPF rendering at 64 Hz until a GC happens to find it, which in an
        // idle tray can take many minutes.
        _sheenClock = sweep.CreateClock();
        _sheenShift.ApplyAnimationClock(TranslateTransform.XProperty, _sheenClock);
    }

    public void ShowReason(string reason)
    {
        _reason.Text = reason;
        if (_reason.Visibility == Visibility.Visible)
        {
            return;
        }

        _reason.Visibility = Visibility.Visible;
        _reason.Opacity = 0;
        Motion.Fade(_reason, 1, Motion.Reveal);
    }

    public void Stop()
    {
        _sheenClock?.Controller.Stop();
        _sheenClock = null;
        _sheenShift.ApplyAnimationClock(TranslateTransform.XProperty, null);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (UIElement child in Children)
        {
            child.Measure(availableSize);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var landing = LandingIn(_kind, _aspect, _nativeSize, _railWidth, _toolbarHeight, finalSize);
        var box = landing ?? new Rect(finalSize);
        _visual.Arrange(box);

        // Blurred up in proportion to how far the poster is stretched: a
        // sharp thumbnail stays sharp, a small one becomes an impression.
        if (_posterBlur is not null && _posterWidth > 0)
        {
            var stretch = box.Width * _dpiScale / _posterWidth;
            _posterBlur.Radius = Math.Clamp((stretch - 1) * 3, 0, 14);
        }

        // One line under the stand-in when there is room, else at the foot
        // of what shows of it, else in the middle of the canvas.
        var lineHeight = Math.Ceiling(_reason.DesiredSize.Height);
        double top;
        if (landing is null)
        {
            top = (finalSize.Height - lineHeight) / 2;
        }
        else if (finalSize.Height - box.Bottom >= lineHeight + 32)
        {
            top = box.Bottom + 16;
        }
        else
        {
            top = Math.Min(box.Bottom, finalSize.Height) - lineHeight - 24;
        }

        _reason.Arrange(new Rect(0, Math.Max(0, top), finalSize.Width, lineHeight));
        return finalSize;
    }

    /// <summary>
    /// Where the content will land in an area of the given size, or null
    /// without a shape. A viewer page with no rail is a lone page: the
    /// viewer shows the rail only for more than one.
    /// </summary>
    internal static Rect? LandingIn(
        LandingKind kind,
        double aspect,
        Size nativeSize,
        double railWidth,
        double toolbarHeight,
        Size area)
    {
        if (area.Width < 1 || area.Height < 1 || aspect <= 0 || !double.IsFinite(aspect))
        {
            return null;
        }

        switch (kind)
        {
            case LandingKind.ViewerPage:
                var viewer = area.Width - railWidth;
                var width = viewer - ViewerPageLeft - ViewerPageRightInset;
                if (width < 1)
                {
                    return null;
                }

                // Many pages overflow, so a scrollbar takes its share. A lone
                // page that fits needs none: it takes the full width where it
                // still fits there, else it keeps the narrow fit, centered.
                var left = ViewerPageLeft;
                bool Fits(double pageWidth) =>
                    ViewerPageTop + pageWidth / aspect + ViewerPageFoot <= area.Height - toolbarHeight;
                if (railWidth <= 0 && Fits(width))
                {
                    var full = viewer - ViewerPageLeft - ViewerPageRightMargin;
                    if (Fits(full))
                    {
                        width = full;
                    }
                    else
                    {
                        left = (viewer - width) / 2;
                    }
                }

                return new Rect(railWidth + left, toolbarHeight + ViewerPageTop, width, width / aspect);

            case LandingKind.Pixels:
                var fitWidth = Math.Min(area.Width, area.Height * aspect);
                if (!nativeSize.IsEmpty && nativeSize.Width > 0)
                {
                    fitWidth = Math.Min(fitWidth, nativeSize.Width);
                }

                var fitHeight = fitWidth / aspect;
                return new Rect((area.Width - fitWidth) / 2, (area.Height - fitHeight) / 2, fitWidth, fitHeight);

            default:
                return null;
        }
    }
}
