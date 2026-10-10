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

public partial class PreviewWindow
{
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
}
