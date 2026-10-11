using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpacePreview.Preview;

/// <summary>
/// The Glimpse mark: the sign for the Space key, an open box, white on an
/// accent tile for the app icon and the welcome, and alone in the tray.
/// This is the only place it is drawn; scripts/render-icon.ps1 builds
/// Glimpse.ico by calling <see cref="RenderTile"/>.
/// </summary>
internal static class BrandMark
{
    /// <summary>The sign's outer edges and its stroke.</summary>
    internal readonly record struct Sign(double Left, double Top, double Right, double Bottom, double Stroke)
    {
        public Sign Scaled(double by, double offset) =>
            new(offset + (Left * by), offset + (Top * by), offset + (Right * by), offset + (Bottom * by), Stroke * by);
    }

    // The shape, in fractions of the tile: a bar a twelfth of the tile
    // thick, ends turned up, 56 % of the tile wide and a hair above center.
    internal static readonly Sign OnTile = new(0.22, 0.35, 0.78, 0.625, 1 / 12.0);

    // Alone in the tray it spans three quarters of the square, as the
    // system's own glyphs do, with the stroke a twelfth. It sits a little
    // above center, as the system's chevron does; centered, the bar at its
    // foot makes it look low.
    internal static readonly Sign Alone = new(0.125, 0.25, 0.875, 0.70, 1 / 12.0);

    // The tile stands on the square keyline of Microsoft's 48 px icon grid,
    // 42 of 48, so it weighs the same as a round icon beside it.
    internal const double TileInset = 3 / 48.0;
    internal const double TileCorner = 0.2;

    // DESIGN.md's two accents, the lighter on top.
    private static readonly Color TileTop = Color.FromRgb(0x4E, 0x80, 0xF5);
    private static readonly Color TileBottom = Color.FromRgb(0x3B, 0x72, 0xE8);

    // Hinted by hand for every size below 256 that Microsoft lists for an
    // app icon: whole-pixel edges, and a stroke that rounds up, so no size
    // draws the sign thinner than the shape. Inset, corner, sign.
    internal static readonly IReadOnlyDictionary<int, (int Inset, int Corner, Sign Sign)> HintedTiles =
        new Dictionary<int, (int, int, Sign)>
        {
            [16] = (1, 3, new(4, 5, 12, 10, 2)),
            [20] = (1, 4, new(5, 7, 15, 12, 2)),
            [24] = (1, 4, new(6, 9, 18, 15, 2)),
            [30] = (2, 5, new(8, 11, 22, 18, 3)),
            [32] = (2, 6, new(8, 12, 24, 20, 3)),
            [36] = (2, 6, new(9, 13, 27, 22, 3)),
            [40] = (2, 7, new(10, 15, 30, 25, 3)),
            [48] = (3, 8, new(12, 18, 36, 29, 4)),
            [60] = (4, 10, new(15, 22, 45, 37, 5)),
            [64] = (4, 11, new(16, 24, 48, 39, 5)),
            [72] = (4, 13, new(18, 27, 54, 44, 6)),
            [80] = (5, 14, new(20, 29, 60, 49, 6)),
            [96] = (6, 17, new(24, 35, 72, 59, 7)),
        };

    // The tray at every scale Windows offers from 100 to 300 percent. The
    // stroke is the nearest whole pixel to a twelfth, and never under 2,
    // so the sign weighs about what the system's glyphs beside it do.
    internal static readonly IReadOnlyDictionary<int, Sign> HintedGlyphs = new Dictionary<int, Sign>
    {
        [16] = new(2, 4, 14, 11, 2),
        [20] = new(2, 5, 18, 14, 2),
        [24] = new(3, 6, 21, 17, 2),
        [28] = new(4, 7, 24, 19, 2),
        [32] = new(4, 8, 28, 22, 3),
        [36] = new(4, 9, 32, 25, 3),
        [40] = new(5, 10, 35, 28, 3),
        [48] = new(6, 12, 42, 34, 4),
    };

    /// <summary>The app tile in a square of <paramref name="size"/>: hinted where a row exists, vectors otherwise.</summary>
    internal static Drawing TileDrawing(double size)
    {
        if (size == Math.Floor(size) && HintedTiles.TryGetValue((int)size, out var hinted))
        {
            return Tile(size, hinted.Inset, hinted.Corner, hinted.Sign, rounded: size >= 48);
        }

        var inset = size * TileInset;
        var side = size - (2 * inset);
        return Tile(size, inset, side * TileCorner, OnTile.Scaled(side, inset), rounded: true);
    }

    /// <summary>One frame of Glimpse.ico.</summary>
    internal static BitmapSource RenderTile(int pixels) => Render(pixels, TileDrawing(pixels));

    /// <summary>The tray glyph: the sign alone in one ink.</summary>
    internal static BitmapSource RenderGlyph(int pixels, Color ink)
    {
        var hinted = HintedGlyphs.TryGetValue(pixels, out var sign);
        var shape = SignGeometry(hinted ? sign : Alone.Scaled(pixels, 0), rounded: !hinted);
        return Render(pixels, new GeometryDrawing(Frozen(new SolidColorBrush(ink)), null, shape));
    }

    /// <summary>The tile fitted to a square of <paramref name="size"/>.</summary>
    public static FrameworkElement Create(double size)
    {
        var image = new DrawingImage(TileDrawing(size));
        image.Freeze();
        return new Image
        {
            Source = image,
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform
        };
    }

    private static DrawingGroup Tile(double size, double inset, double corner, Sign sign, bool rounded)
    {
        var plate = new LinearGradientBrush(TileTop, TileBottom, new Point(0, 0), new Point(0, 1));
        var side = size - (2 * inset);
        var group = new DrawingGroup
        {
            Children =
            {
                // The whole square, so a fitted image keeps the inset.
                new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, size, size))),
                new GeometryDrawing(Frozen(plate), null, new RectangleGeometry(new Rect(inset, inset, side, side), corner, corner)),
                new GeometryDrawing(Brushes.White, null, SignGeometry(sign, rounded))
            }
        };
        group.Freeze();
        return group;
    }

    /// <summary>
    /// The sign as one outline. Inner corners stay square; outer corners
    /// round off by half the stroke where there are pixels enough for a
    /// curve, and stay square where a curve would only blur.
    /// </summary>
    internal static Geometry SignGeometry(Sign s, bool rounded)
    {
        var w = s.Stroke;
        var r = rounded ? w / 2 : 0;
        var arc = new Size(r, r);
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(s.Left, s.Top + r), true, true);
            c.ArcTo(new Point(s.Left + r, s.Top), arc, 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(s.Left + w - r, s.Top), true, false);
            c.ArcTo(new Point(s.Left + w, s.Top + r), arc, 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(s.Left + w, s.Bottom - w), true, false);
            c.LineTo(new Point(s.Right - w, s.Bottom - w), true, false);
            c.LineTo(new Point(s.Right - w, s.Top + r), true, false);
            c.ArcTo(new Point(s.Right - w + r, s.Top), arc, 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(s.Right - r, s.Top), true, false);
            c.ArcTo(new Point(s.Right, s.Top + r), arc, 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(s.Right, s.Bottom - r), true, false);
            c.ArcTo(new Point(s.Right - r, s.Bottom), arc, 0, false, SweepDirection.Clockwise, true, false);
            c.LineTo(new Point(s.Left + r, s.Bottom), true, false);
            c.ArcTo(new Point(s.Left, s.Bottom - r), arc, 0, false, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    // Drawn in pixels into a 96 dpi bitmap, so one unit is one pixel.
    private static BitmapSource Render(int pixels, Drawing drawing)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawDrawing(drawing);
        }

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
