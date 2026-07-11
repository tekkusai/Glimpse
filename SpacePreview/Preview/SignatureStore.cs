using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The user's reusable ink: a small library of signatures and one stamp,
/// stored as PNGs with alpha in the app's data root. Local by construction,
/// never synced, each removable in one call. Ink strokes render at 3x and
/// let the display scale them down, which reads as smoothing without curve
/// fitting.
/// </summary>
internal static class SignatureStore
{
    private const double InkScale = 3.0;
    private const double InkPenWidth = 2.2;

    public static string StampPath => AppPaths.InDataRoot("stamp.png");

    public static bool HasStamp => File.Exists(StampPath);

    /// <summary>The single-signature file from before the library existed.</summary>
    private static string LegacySignaturePath => AppPaths.InDataRoot("signature.png");

    private static string SignaturesDirectory => AppPaths.InDataRoot("signatures");

    /// <summary>
    /// Saved signatures, newest first. A pre-library signature.png still
    /// counts: it lists last and deletes like any other entry.
    /// </summary>
    public static IReadOnlyList<string> ListSignatures()
    {
        var signatures = new List<string>();
        try
        {
            if (Directory.Exists(SignaturesDirectory))
            {
                signatures.AddRange(Directory
                    .EnumerateFiles(SignaturesDirectory, "*.png")
                    .OrderByDescending(File.GetLastWriteTimeUtc));
            }
        }
        catch
        {
            // An unreadable directory lists as empty.
        }

        try
        {
            if (File.Exists(LegacySignaturePath))
            {
                signatures.Add(LegacySignaturePath);
            }
        }
        catch
        {
            // Same.
        }

        return signatures;
    }

    public static BitmapSource? TryLoad(string path)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes a placement's snapshotted pixels.</summary>
    public static BitmapSource? TryLoad(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return Decode(stream);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource Decode(Stream stream)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static void Remove(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // Already gone is the goal state.
        }
    }

    /// <summary>
    /// Renders captured strokes and saves them as a NEW library entry.
    /// Strokes are in the capture pad's own coordinates; the output is
    /// cropped to the ink with a small quiet margin. Returns the saved path.
    /// </summary>
    public static string? SaveSignatureFromStrokes(IReadOnlyList<IReadOnlyList<Point>> strokes, Color inkColor)
    {
        var ink = RenderStrokes(strokes, inkColor);
        return ink is null ? null : SaveNewSignature(ink);
    }

    /// <summary>Saves rendered ink as a new library entry, newest by mtime.</summary>
    public static string? SaveNewSignature(BitmapSource ink)
    {
        try
        {
            Directory.CreateDirectory(SignaturesDirectory);
            var path = Path.Combine(SignaturesDirectory, $"sig-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ink));
            using var stream = File.Create(path);
            encoder.Save(stream);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Copies a user-picked image into the stamp slot.</summary>
    public static bool TryImportStamp(string sourcePath)
    {
        try
        {
            var image = TryLoad(sourcePath);
            if (image is null)
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(StampPath)!);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(StampPath);
            encoder.Save(stream);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static BitmapSource? RenderStrokes(IReadOnlyList<IReadOnlyList<Point>> strokes, Color inkColor) =>
        RenderComposite(baseImage: null, baseRect: Rect.Empty, strokes, inkColor);

    /// <summary>
    /// Renders an existing signature image with new strokes drawn over it,
    /// all in the capture pad's coordinates, cropped to the combined ink.
    /// A null base renders strokes alone; empty strokes re-render the base.
    /// </summary>
    internal static BitmapSource? RenderComposite(
        BitmapSource? baseImage,
        Rect baseRect,
        IReadOnlyList<IReadOnlyList<Point>> strokes,
        Color inkColor)
    {
        try
        {
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            var points = 0;
            foreach (var stroke in strokes)
            {
                foreach (var point in stroke)
                {
                    points++;
                    minX = Math.Min(minX, point.X);
                    minY = Math.Min(minY, point.Y);
                    maxX = Math.Max(maxX, point.X);
                    maxY = Math.Max(maxY, point.Y);
                }
            }

            var hasInk = points >= 2 && (maxX - minX >= 4 || maxY - minY >= 4);
            var hasBase = baseImage is not null && baseRect.Width > 0 && baseRect.Height > 0;
            if (!hasInk && !hasBase)
            {
                return null;
            }

            if (hasBase)
            {
                minX = hasInk ? Math.Min(minX, baseRect.Left) : baseRect.Left;
                minY = hasInk ? Math.Min(minY, baseRect.Top) : baseRect.Top;
                maxX = hasInk ? Math.Max(maxX, baseRect.Right) : baseRect.Right;
                maxY = hasInk ? Math.Max(maxY, baseRect.Bottom) : baseRect.Bottom;
            }

            const double margin = 6;
            var width = (int)Math.Ceiling((maxX - minX + margin * 2) * InkScale);
            var height = (int)Math.Ceiling((maxY - minY + margin * 2) * InkScale);
            if (width < 1 || height < 1 || width > 4000 || height > 4000)
            {
                return null;
            }

            var pen = new Pen(new SolidColorBrush(inkColor), InkPenWidth * InkScale)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            pen.Freeze();

            Point Map(Point p) => new(
                (p.X - minX + margin) * InkScale,
                (p.Y - minY + margin) * InkScale);

            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                if (hasBase)
                {
                    var topLeft = Map(baseRect.TopLeft);
                    context.DrawImage(baseImage, new Rect(
                        topLeft.X,
                        topLeft.Y,
                        baseRect.Width * InkScale,
                        baseRect.Height * InkScale));
                }

                if (hasInk)
                {
                    foreach (var stroke in strokes)
                    {
                        if (stroke.Count == 0)
                        {
                            continue;
                        }

                        if (stroke.Count == 1)
                        {
                            context.DrawEllipse(pen.Brush, null, Map(stroke[0]), pen.Thickness / 2, pen.Thickness / 2);
                            continue;
                        }

                        var geometry = new StreamGeometry();
                        using (var figure = geometry.Open())
                        {
                            figure.BeginFigure(Map(stroke[0]), isFilled: false, isClosed: false);
                            for (var i = 1; i < stroke.Count; i++)
                            {
                                figure.LineTo(Map(stroke[i]), isStroked: true, isSmoothJoin: true);
                            }
                        }

                        geometry.Freeze();
                        context.DrawGeometry(null, pen, geometry);
                    }
                }
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
