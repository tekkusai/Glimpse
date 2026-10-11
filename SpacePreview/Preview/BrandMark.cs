using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// Shared vector mark used by the welcome screen, tray icon, and app icon.
/// Keeping it in code avoids bitmap scaling artifacts.
/// </summary>
internal static class BrandMark
{
    // Coordinates are normalized to a -50..50 box and scaled by callers.
    internal const string SparkPath =
        "M 0,-50 C 4.5,-16 16,-4.5 50,0 C 16,4.5 4.5,16 0,50 C -4.5,16 -16,4.5 -50,0 C -16,-4.5 -4.5,-16 0,-50 Z";

    /// <summary>The spark and its small companion up and to the right, in the accent.</summary>
    public static DrawingImage CreateImage()
    {
        var accent = Theme.Current.Accent;
        var spark = Geometry.Parse(SparkPath);
        var companion = spark.Clone();
        companion.Transform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform(0.3, 0.3),
                new TranslateTransform(52, -41.5)
            }
        };

        var image = new DrawingImage(new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing(Theme.Brush(accent), null, spark),
                new GeometryDrawing(Theme.Brush(Color.FromArgb(150, accent.R, accent.G, accent.B)), null, companion)
            }
        });
        image.Freeze();
        return image;
    }

    /// <summary>The mark fitted to a square of <paramref name="size"/>.</summary>
    public static FrameworkElement Create(double size) => new Image
    {
        Source = CreateImage(),
        Width = size,
        Height = size,
        Stretch = Stretch.Uniform
    };
}
