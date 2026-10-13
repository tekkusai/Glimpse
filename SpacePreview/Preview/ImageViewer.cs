using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpacePreview.Services;
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

    /// <summary>Blown up this far at fit, an image is an icon or pixel art, and its pixels stay square.</summary>
    public const double PixelScale = 3;

    /// <summary>
    /// Fit fills the view, as 2.0 did: an icon or a small screenshot shows
    /// big enough to read instead of as a speck in the window.
    /// </summary>
    public static double Fit(Size image, Size view) =>
        image.Width <= 0 || image.Height <= 0
            ? 1
            : Math.Min(view.Width / image.Width, view.Height / image.Height);

    /// <summary>Down to fit or 1:1, whichever is smaller, so a small image's real size stays in reach.</summary>
    public static double Clamp(double scale, double fit) => Math.Clamp(scale, Math.Min(fit, 1), Math.Max(fit, MaxScale));

    public static bool IsAt(double scale, double target) => Math.Abs(scale - target) <= Tolerance * Math.Max(target, Tolerance);

    /// <summary>From fit to 1:1, or to 2x when fit is 1:1; from anywhere else back to fit.</summary>
    public static double DoubleClickTarget(double scale, double fit) =>
        !IsAt(scale, fit) ? fit
        : IsAt(fit, 1) ? 2
        : 1;

    /// <summary>
    /// Square pixels once the user zooms in past 1:1, or at a fit of
    /// <see cref="PixelScale"/> or more; a gentle fit, a small screenshot
    /// filling the window, stays smooth.
    /// </summary>
    public static bool ShowsPixels(double scale, bool atFit) =>
        scale > 1 + Tolerance && (!atFit || scale >= PixelScale - Tolerance);

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
    // A sentence needs longer on screen than a number does.
    private static readonly TimeSpan NoticeHold = TimeSpan.FromSeconds(4);

    // A picture is read for its words once it has stayed on screen this
    // long. Arrowing through a folder never gets there, and costs nothing.
    private static readonly TimeSpan ReadDelay = TimeSpan.FromMilliseconds(500);

    // A press on a word becomes a selection once the pointer has gone this
    // far, in device pixels, or reached another word.
    private const double SelectSlop = 3;

    private readonly Grid _root;
    private readonly Border _frame;
    private readonly Image _image;
    private readonly Border _hud;
    private readonly TextBlock _hudText;
    private readonly PictureTextLayer _text = new();
    private readonly DispatcherTimer _readDelay = new() { Interval = ReadDelay };
    private readonly int _sourceLongSide;
    private readonly bool _photograph;
    private BitmapSource _bitmap;

    // A larger decode of the same view, shown only while the picture is on
    // screen larger than the one above. At fit and below, the first decode
    // is the picture, drawn exactly as it always was.
    private BitmapSource? _sharp;
    private bool _askedForSharper;
    private double _scale = 1;
    private double _x;
    private double _y;
    private bool _atFit = true;
    private bool _canPan;
    private bool _dragging;
    private Point _lastDrag;
    private CancellationTokenSource? _reading;
    private int? _anchorWord;
    private Point _pressedAt;
    private bool _overText;

    public ImageViewer(BitmapSource bitmap, int sourceLongSide, ImageRotationSession? rotation, bool photograph = false)
    {
        _photograph = photograph;
        _bitmap = bitmap;
        _sourceLongSide = sourceLongSide;
        _image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        _frame = new Border { Child = _image };
        var canvas = new Canvas { Children = { _frame } };
        _hudText = new TextBlock
        {
            FontSize = Metrics.FontLabel,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Brush(Theme.OverlayText),
            // The readout is a few digits; a notice is a sentence, and
            // wraps inside a narrow window instead of running off it.
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        };
        _hud = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(18, 0, 18, 18),
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
            Children = { canvas, _hud, _text }
        };

        // The notice speaks over a selection, not under it.
        Panel.SetZIndex(_hud, 1);

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
        _root.MouseLeftButtonDown += (_, e) =>
        {
            // A plain click on a picture that cannot move is still not
            // this viewer's: it passes on, as it always has.
            var held = PointerDown(DevicePosition(e), e.ClickCount);
            if (held)
            {
                _root.CaptureMouse();
            }

            e.Handled = held || e.ClickCount == 2;
        };
        _root.MouseMove += (_, e) => PointerMove(DevicePosition(e));
        _root.MouseLeftButtonUp += (_, _) => _root.ReleaseMouseCapture();
        // The capture owns the drag: it ends on button-up or when the system
        // takes the capture away. A MouseLeave cancel would kill drags on
        // stale over-state, as the mesh viewer learned.
        _root.LostMouseCapture += (_, _) => PointerUp();
        _root.MouseLeave += (_, _) =>
        {
            _overText = false;
            ShowCursor();
        };

        // The words are read while the picture is on screen and for the
        // picture that is on screen: a turn or a sharper decode starts over.
        _readDelay.Tick += (_, _) =>
        {
            _readDelay.Stop();
            _ = ReadWordsAsync();
        };
        _root.Loaded += (_, _) => _readDelay.Start();
        _root.Unloaded += (_, _) => ForgetWords();

        if (rotation is not null)
        {
            // A quarter turn is a new composition: re-fit rather than spin a
            // zoomed crop. A quality upgrade keeps the composition, so zoom
            // and pan stay where they are.
            rotation.ViewChanged += () =>
            {
                ReplaceBitmap(rotation.CurrentBitmap);
                _sharp = rotation.SharpBitmap;
                _atFit = true;
                Layout();
            };
            rotation.Sharpened += () =>
            {
                _sharp = rotation.SharpBitmap;
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

    /// <summary>
    /// The picture is on screen larger than its decode and has more pixels
    /// of its own. Raised once. The window decodes it again, larger.
    /// </summary>
    public event Action? SharperWanted;

    /// <summary>True while the decode is stretched on screen, the picture has more pixels, and no larger decode is here.</summary>
    internal bool WantsSharper =>
        _sharp is null && _sourceLongSide > Math.Max(_bitmap.PixelWidth, _bitmap.PixelHeight) && Stretched;

    /// <summary>The window found the picture sharp enough when it looked. A later zoom may ask again.</summary>
    internal void SharperDeclined() => _askedForSharper = false;

    // On screen, the picture's long side is more device pixels than the
    // first decode has.
    private bool Stretched =>
        _scale * _sourceLongSide > Math.Max(_bitmap.PixelWidth, _bitmap.PixelHeight) * (1 + ZoomGestures.Tolerance);

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

        // The selection lies exactly over the picture, wherever it is.
        _text.Width = _frame.Width;
        _text.Height = _frame.Height;
        _text.Margin = new Thickness(Canvas.GetLeft(_frame), Canvas.GetTop(_frame), 0, 0);

        var shown = _sharp is not null && Stretched ? _sharp : _bitmap;
        if (!ReferenceEquals(_image.Source, shown))
        {
            _image.Source = shown;
        }

        // Zoomed past 1:1 the pixels stay square, so pixel art reads as
        // pixels; a gentle fit and anything at or below 1:1 is filtered,
        // which also keeps a capped decode shown at 100% soft, not blocky.
        // The larger decode is never far from its own size on screen, so
        // the graphics card's filter does: the careful one runs on the
        // processor over every pixel, at every step of a zoom. A photograph
        // has no pixels worth showing as squares, at any zoom.
        var magnified = !_photograph && ZoomGestures.ShowsPixels(_scale, _atFit);
        RenderOptions.SetBitmapScalingMode(
            _image,
            magnified ? BitmapScalingMode.NearestNeighbor
            : ReferenceEquals(shown, _sharp) ? BitmapScalingMode.Linear
            : BitmapScalingMode.HighQuality);
        _canPan = width > view.Width + 0.5 || height > view.Height + 0.5;
        ShowCursor();

        if (!_askedForSharper && WantsSharper)
        {
            _askedForSharper = true;
            SharperWanted?.Invoke();
        }
    }

    // Over a word the pointer is a text cursor; elsewhere it says whether
    // the picture can be moved.
    private void ShowCursor() =>
        _root.Cursor = _anchorWord is not null || _overText ? Cursors.IBeam : _canPan ? Cursors.SizeAll : null;

    /// <summary>
    /// A press at a point of the view, in device pixels. On a word it
    /// begins a selection; twice on a word it selects the word. Anywhere
    /// else it is what it was before pictures had words: twice zooms, once
    /// takes hold of a picture larger than its view. True when the press
    /// began something a release must end.
    /// </summary>
    internal bool PointerDown(Point position, int clicks)
    {
        var word = WordAt(position);
        if (clicks == 2)
        {
            if (word is { } twice)
            {
                _text.Selection = (twice, twice);
            }
            else
            {
                ZoomAt(position, ZoomGestures.DoubleClickTarget(_scale, Fit()));
            }

            return false;
        }

        _text.Selection = null;
        if (word is { } pressed)
        {
            _anchorWord = pressed;
            _pressedAt = position;
            ShowCursor();
            return true;
        }

        if (!_canPan)
        {
            return false;
        }

        _dragging = true;
        _lastDrag = position;
        return true;
    }

    /// <summary>The pointer at a point of the view: a selection grows, a held picture moves, or the cursor is chosen.</summary>
    internal void PointerMove(Point position)
    {
        if (_anchorWord is { } anchor)
        {
            var reached = WordAt(position) ?? _text.Words.Nearest(ToPicture(position));
            if (reached != anchor
                || _text.Selection is not null
                || Math.Abs(position.X - _pressedAt.X) >= SelectSlop
                || Math.Abs(position.Y - _pressedAt.Y) >= SelectSlop)
            {
                _text.Selection = (anchor, reached);
            }

            return;
        }

        if (_dragging)
        {
            _x += position.X - _lastDrag.X;
            _y += position.Y - _lastDrag.Y;
            _lastDrag = position;
            Layout();
            return;
        }

        var over = WordAt(position) is not null;
        if (over != _overText)
        {
            _overText = over;
            ShowCursor();
        }
    }

    /// <summary>The press has ended, by release or because the system took the pointer.</summary>
    internal void PointerUp()
    {
        _dragging = false;
        _anchorWord = null;
        ShowCursor();
    }

    /// <summary>The words Windows found in the picture on screen. Empty until it has been read.</summary>
    internal PictureWords Words => _text.Words;

    /// <summary>The words the reader has selected, as they read, or null.</summary>
    public string? SelectedText =>
        _text.Selection is { } selection ? _text.Words.TextOf(selection.One, selection.Other) : null;

    /// <summary>Selects every word. False when the picture has none, or has not been read yet.</summary>
    public bool SelectAllText()
    {
        if (_text.Words.Count == 0)
        {
            return false;
        }

        _text.Selection = (0, _text.Words.Count - 1);
        return true;
    }

    /// <summary>Where a word's middle is in the view, in device pixels.</summary>
    internal Point WordCenter(int index)
    {
        var box = _text.Words[index].Box;
        var source = Source;
        var middle = new Point(box.X + box.Width / 2, box.Y + box.Height / 2);
        if (_text.Words.Angle != 0)
        {
            // The boxes lie in the engine's level frame; the view shows the
            // picture as it is.
            var turn = new RotateTransform(_text.Words.Angle, source.Width / 2, source.Height / 2);
            var turned = turn.Transform(new Point(middle.X * source.Width, middle.Y * source.Height));
            middle = new Point(turned.X / source.Width, turned.Y / source.Height);
        }

        return new Point(_x + middle.X * source.Width * _scale, _y + middle.Y * source.Height * _scale);
    }

    /// <summary>A point of the view as fractions of the picture.</summary>
    private Point ToPicture(Point position)
    {
        var source = Source;
        return new Point((position.X - _x) / (source.Width * _scale), (position.Y - _y) / (source.Height * _scale));
    }

    private int? WordAt(Point position) =>
        _text.Words.Count == 0 ? null : _text.Words.WordAt(ToPicture(position));

    private async Task ReadWordsAsync()
    {
        var bitmap = _bitmap;
        var reading = _reading = new CancellationTokenSource();
        try
        {
            var words = await PictureText.RecognizeAsync(bitmap, reading.Token);
            if (ReferenceEquals(bitmap, _bitmap) && !reading.IsCancellationRequested)
            {
                _text.Words = words;
            }
        }
        catch (OperationCanceledException)
        {
            // The picture changed or left before it was read.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"picture words failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ForgetWords()
    {
        _readDelay.Stop();
        _reading?.Cancel();
        _reading = null;
        _anchorWord = null;
        _overText = false;
        _text.Words = PictureWords.None;
    }

    private void ReplaceBitmap(BitmapSource bitmap)
    {
        _bitmap = bitmap;
        _image.Source = bitmap;
        ShowTransparency();

        // The words belonged to the picture that was here.
        ForgetWords();
        if (_root.IsLoaded)
        {
            _readDelay.Start();
        }
    }

    // Transparent pixels sit on a quiet two-tone checkerboard, so dark art
    // on a dark canvas (or light art on a light one) still shows its shape.
    private void ShowTransparency()
    {
        _frame.Background = HasAlpha(_bitmap) ? Checkerboard(Theme.Current) : null;

        // The selection is drawn in the theme's accent.
        _text.InvalidateVisual();
    }

    /// <summary>
    /// A sentence about this image, such as a rotation that did not save,
    /// said where the zoom readout speaks and gone the same way.
    /// </summary>
    public void ShowNotice(string text) => ShowHud(text, NoticeHold);

    /// <summary>What the notice last said.</summary>
    internal string NoticeText => _hudText.Text;

    private void ShowHud() => ShowHud($"{Math.Round(_scale * 100)}%", ZoomGestures.HudHold);

    private void ShowHud(string text, TimeSpan hold)
    {
        _hudText.Text = text;
        _hud.BeginAnimation(UIElement.OpacityProperty, null);
        _hud.Opacity = 1;
        var fade = Motion.Enabled ? ZoomGestures.HudFade : TimeSpan.Zero;
        _hud.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0, fade) { BeginTime = hold });
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
