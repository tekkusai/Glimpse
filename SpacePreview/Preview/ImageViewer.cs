using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The one zoom and pan rule set (docs/DESIGN.md, Zoom and pan). The WPF
/// viewer calls it and the web image page is written from the same
/// numbers. A scale is device pixels per image pixel, so 1 is 1:1.
/// </summary>
internal static class ZoomGestures
{
    public const double WheelStep = 1.15;
    public const double MaxScale = 32;
    public const double Tolerance = 0.001;
    public static readonly TimeSpan HudHold = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan HudFade = TimeSpan.FromMilliseconds(260);

    /// <summary>Fit never upscales: an image smaller than the view stays at 1:1.</summary>
    public static double Fit(Size image, Size view) =>
        image.Width <= 0 || image.Height <= 0
            ? 1
            : Math.Min(1, Math.Min(view.Width / image.Width, view.Height / image.Height));

    public static double Clamp(double scale, double fit) => Math.Clamp(scale, fit, Math.Max(fit, MaxScale));

    public static bool IsAt(double scale, double target) => Math.Abs(scale - target) <= Tolerance * Math.Max(target, Tolerance);

    /// <summary>From fit to 1:1, or to twice fit when fit is already 1:1; from anywhere else back to fit.</summary>
    public static double DoubleClickTarget(double scale, double fit) =>
        !IsAt(scale, fit) ? fit
        : fit >= 1 - Tolerance ? 2 * fit
        : 1;

    /// <summary>Content smaller than the view sits centered; larger content keeps its edges outside the view.</summary>
    public static double ClampOffset(double offset, double content, double view) =>
        content <= view ? (view - content) / 2 : Math.Clamp(offset, view - content, 0);
}

/// <summary>A zoomable image on screen; the window's Ctrl+Plus, Ctrl+Minus and Ctrl+0 drive it.</summary>
internal interface IZoomableImage
{
    /// <summary>One step in (1) or out (-1) around the center; 0 returns to fit.</summary>
    void ZoomBy(int direction);

    /// <summary>The double-click gesture, at the center.</summary>
    void ToggleActualSize();
}

/// <summary>
/// The native image viewer. Layout is computed in device pixels, so fit,
/// 1:1 and the percent readout mean real pixels whatever the file's DPI and
/// whatever size was decoded: the header's long side names the source.
/// </summary>
internal sealed class ImageViewer : IZoomableImage
{
    private readonly Grid _root;
    private readonly Border _frame;
    private readonly Image _image;
    private readonly Border _hud;
    private readonly TextBlock _hudText;
    private readonly int _sourceLongSide;
    private BitmapSource _bitmap;
    private double _scale = 1;
    private double _x;
    private double _y;
    private bool _atFit = true;
    private bool _canPan;
    private bool _dragging;
    private Point _lastDrag;

    public ImageViewer(BitmapSource bitmap, int sourceLongSide, ImageRotationSession? rotation)
    {
        _bitmap = bitmap;
        _sourceLongSide = sourceLongSide;
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        _frame = new Border { Child = _image };
        var canvas = new Canvas { Children = { _frame } };
        _hudText = new TextBlock
        {
            FontSize = Metrics.FontLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.OverlayText)
        };
        _hud = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 18),
            Padding = new Thickness(12, 6, 12, 7),
            CornerRadius = Metrics.SurfaceCorner,
            Background = Theme.Brush(Theme.OverlayScrim),
            IsHitTestVisible = false,
            Opacity = 0,
            Child = _hudText
        };
        _root = new Grid
        {
            ClipToBounds = true,
            Children = { canvas, _hud }
        };

        // The canvas follows the theme by resource; the checkerboard is
        // drawn from the palette, so a theme change draws it again.
        _root.SetResourceReference(Panel.BackgroundProperty, "Theme.CanvasBg");
        Theme.Follow(_root, ShowTransparency);
        _root.SizeChanged += (_, _) => Layout();
        _image.DpiChanged += (_, _) => Layout();
        _root.MouseWheel += (_, e) =>
        {
            ZoomAt(DevicePosition(e), _scale * Math.Pow(ZoomGestures.WheelStep, e.Delta / 120.0));
            e.Handled = true;
        };
        _root.MouseLeftButtonDown += OnMouseDown;
        _root.MouseMove += OnMouseMove;
        _root.MouseLeftButtonUp += (_, _) => _root.ReleaseMouseCapture();
        // The capture owns the drag: it ends on button-up or when the system
        // takes the capture away. A MouseLeave cancel would kill drags on
        // stale over-state, as the mesh viewer learned.
        _root.LostMouseCapture += (_, _) => _dragging = false;

        if (rotation is not null)
        {
            // A quarter turn is a new composition: re-fit rather than spin a
            // zoomed crop. A quality upgrade keeps the composition, so zoom
            // and pan stay where they are.
            rotation.ViewChanged += () =>
            {
                ReplaceBitmap(rotation.CurrentBitmap);
                _atFit = true;
                Layout();
            };
            rotation.QualityUpgraded += () =>
            {
                ReplaceBitmap(rotation.CurrentBitmap);
                Layout();
            };
        }
    }

    public FrameworkElement Element => _root;

    public void ZoomBy(int direction)
    {
        if (direction == 0)
        {
            _atFit = true;
            Layout();
            ShowHud();
            return;
        }

        ZoomAt(ViewCenter(), _scale * Math.Pow(ZoomGestures.WheelStep, Math.Sign(direction)));
    }

    public void ToggleActualSize() =>
        ZoomAt(ViewCenter(), ZoomGestures.DoubleClickTarget(_scale, Fit()));

    private double Dpi => VisualTreeHelper.GetDpi(_root).DpiScaleX;

    private Size View => new(_root.ActualWidth * Dpi, _root.ActualHeight * Dpi);

    /// <summary>The source image's pixel size in the current composition.</summary>
    private Size Source
    {
        get
        {
            var decodedLongSide = Math.Max(_bitmap.PixelWidth, _bitmap.PixelHeight);
            var ratio = _sourceLongSide > decodedLongSide ? _sourceLongSide / (double)decodedLongSide : 1;
            return new Size(_bitmap.PixelWidth * ratio, _bitmap.PixelHeight * ratio);
        }
    }

    private double Fit() => ZoomGestures.Fit(Source, View);

    private Point ViewCenter() => new(View.Width / 2, View.Height / 2);

    private Point DevicePosition(MouseEventArgs e)
    {
        var position = e.GetPosition(_root);
        return new Point(position.X * Dpi, position.Y * Dpi);
    }

    private void ZoomAt(Point anchor, double target)
    {
        var fit = Fit();
        target = ZoomGestures.Clamp(target, fit);
        _x = anchor.X - (anchor.X - _x) * target / _scale;
        _y = anchor.Y - (anchor.Y - _y) * target / _scale;
        _scale = target;
        _atFit = ZoomGestures.IsAt(target, fit);
        Layout();
        ShowHud();
    }

    private void Layout()
    {
        var view = View;
        if (view.Width <= 0 || view.Height <= 0)
        {
            return;
        }

        var source = Source;
        var fit = ZoomGestures.Fit(source, view);
        _scale = _atFit ? fit : ZoomGestures.Clamp(_scale, fit);
        var width = source.Width * _scale;
        var height = source.Height * _scale;
        _x = ZoomGestures.ClampOffset(_x, width, view.Width);
        _y = ZoomGestures.ClampOffset(_y, height, view.Height);

        // Whole device pixels at the origin keep 1:1 crisp.
        var dpi = Dpi;
        _frame.Width = width / dpi;
        _frame.Height = height / dpi;
        Canvas.SetLeft(_frame, Math.Round(_x) / dpi);
        Canvas.SetTop(_frame, Math.Round(_y) / dpi);

        // Past 1:1 the pixels stay square, so pixel art reads as pixels; at
        // or below it the image is filtered, which also keeps a capped
        // decode shown at 100% soft rather than blocky.
        var magnified = _scale > 1 + ZoomGestures.Tolerance;
        RenderOptions.SetBitmapScalingMode(_image, magnified ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        _canPan = width > view.Width + 0.5 || height > view.Height + 0.5;
        _root.Cursor = _canPan ? Cursors.SizeAll : null;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ZoomAt(DevicePosition(e), ZoomGestures.DoubleClickTarget(_scale, Fit()));
            e.Handled = true;
            return;
        }

        if (!_canPan)
        {
            return;
        }

        _dragging = true;
        _lastDrag = DevicePosition(e);
        _root.CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = DevicePosition(e);
        _x += position.X - _lastDrag.X;
        _y += position.Y - _lastDrag.Y;
        _lastDrag = position;
        Layout();
    }

    private void ReplaceBitmap(BitmapSource bitmap)
    {
        _bitmap = bitmap;
        _image.Source = bitmap;
        ShowTransparency();
    }

    // Transparent pixels sit on a quiet two-tone checkerboard, so dark art
    // on a dark canvas (or light art on a light one) still shows its shape.
    private void ShowTransparency() =>
        _frame.Background = HasAlpha(_bitmap) ? Checkerboard(Theme.Current) : null;

    private void ShowHud()
    {
        _hudText.Text = $"{Math.Round(_scale * 100)}%";
        _hud.BeginAnimation(UIElement.OpacityProperty, null);
        _hud.Opacity = 1;
        var fade = Motion.Enabled ? ZoomGestures.HudFade : TimeSpan.Zero;
        _hud.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, fade) { BeginTime = ZoomGestures.HudHold });
    }

    internal static bool HasAlpha(BitmapSource bitmap)
    {
        var format = bitmap.Format;
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32
            || format == PixelFormats.Rgba64 || format == PixelFormats.Prgba64
            || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
        {
            return true;
        }

        return bitmap.Palette?.Colors.Any(color => color.A < 255) == true;
    }

    private static Brush Checkerboard(ThemePalette palette)
    {
        const double square = 8;
        var brush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, square * 2, square * 2),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(Theme.Brush(palette.RaisedBg), null, new RectangleGeometry(new Rect(0, 0, square * 2, square * 2))),
                    new GeometryDrawing(
                        Theme.Brush(palette.RaisedBorder),
                        null,
                        new GeometryGroup
                        {
                            Children =
                            {
                                new RectangleGeometry(new Rect(0, 0, square, square)),
                                new RectangleGeometry(new Rect(square, square, square, square))
                            }
                        })
                }
            }
        };
        brush.Freeze();
        return brush;
    }
}
