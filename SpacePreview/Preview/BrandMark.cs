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

    public static FrameworkElement Create(double size)
    {
        var accent = Theme.Current.Accent;
        var geometry = Geometry.Parse(SparkPath);

        var spark = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = Theme.Brush(accent),
            Stretch = Stretch.Uniform,
            Width = size,
            Height = size
        };

        var companion = new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = Theme.Brush(Color.FromArgb(150, accent.R, accent.G, accent.B)),
            Stretch = Stretch.Uniform,
            Width = size * 0.30,
            Height = size * 0.30,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, size * -0.04, size * -0.12, 0)
        };

        return new Grid
        {
            Width = size * 1.1,
            Height = size * 1.05,
            Children = { spark, companion }
        };
    }
}
