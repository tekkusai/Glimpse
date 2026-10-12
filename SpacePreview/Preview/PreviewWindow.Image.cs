using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Wpf;
using SpacePreview.Services;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

public partial class PreviewWindow
{
    // ---------------------------------------------------------------------
    // Images: the header is read once and names the true pixels. The first
    // frame is the quickest image that is sharp at the stage's size, and a
    // giant image decodes in full a beat later for zoom. One viewer owns the
    // gestures (docs/DESIGN.md, Zoom and pan).
    // ---------------------------------------------------------------------

    // The window's keyboard zoom reaches whichever image is on screen.
    private IZoomableImage? _imageZoom;

    private sealed record ImageFirstFrame(
        BitmapSource Bitmap,
        int Orientation,
        bool IsFullDecode,
        bool CoversStage,
        ImageFileStamp? Stamp,
        int CacheGeneration);

    // The header read on the way to placing the window, and the load it
    // was read for.
    private (long Version, ImageHeader Header)? _stageHeader;

    /// <summary>
    /// The one read of an image's header, made where the stage takes its
    /// shape, and kept for the decode of the same load. It is not kept
    /// while a rotation of the file is on its way to disk: by the time the
    /// decode asked, the file would no longer be the one it describes.
    /// </summary>
    private ImageHeader? ReadStageHeader(string path, long version)
    {
        var settled = ImageRotationSession.WhenSaved(path).IsCompleted;
        var header = ImageHeader.TryRead(path);
        _stageHeader = settled && header is not null ? (version, header) : null;
        return header;
    }

    /// <summary>The header the stage read for this load, or null when it read none.</summary>
    private ImageHeader? StageHeader(long version) =>
        _stageHeader is { } read && read.Version == version ? read.Header : null;

    /// <summary>
    /// <paramref name="known"/> is the header the stage read for this load
    /// (<see cref="StageHeader"/>), and it is not read a second time. Null
    /// when the stage read none, as for a file whose bytes are not at hand:
    /// then it is read here, off the UI thread.
    /// </summary>
    private async Task<FrameworkElement?> TryBuildImagePreviewAsync(
        string path,
        long version,
        CancellationToken token,
        ImageHeader? known)
    {
        // A rotation of this same file still on its way to disk lands
        // first, so the preview opens on what the file is and not on what
        // it was a moment ago. What was read before the write is stale.
        var saved = ImageRotationSession.WhenSaved(path);
        if (!saved.IsCompleted)
        {
            await saved.WaitAsync(token);
            known = null;
        }

        var stage = StageContentPixels();
        var (header, decoded, persistence) = await Task.Run(() =>
        {
            // A save cut short by the app's end left its working file
            // beside the picture. Opening the picture removes it, at the
            // cost of one file stat.
            ImageRotationSession.RemoveStrandedTemp(path);
            var read = known ?? ImageHeader.TryRead(path);
            var first = DecodeImageFirstFrame(path, read, stage);
            return (read, first, first is null ? RotationPersistence.ViewOnly : ImageRotationService.Classify(path));
        }, token);
        if (decoded is null)
        {
            return null;
        }

        // Camera photos carry their upright direction as metadata; Explorer
        // and its thumbnails honor it, so the preview must agree with what
        // sits next to it.
        var session = new ImageRotationSession(path, decoded.Bitmap, decoded.Orientation, persistence);
        if (!token.IsCancellationRequested)
        {
            // A superseded load must not hand its session to the preview
            // that replaced it.
            _rotationSession = session;
        }

        if (!decoded.IsFullDecode)
        {
            // The session holds a turn back from the disk until this ends.
            session.FullDecode = CompleteLargeImageDecodeAsync(path, header, decoded, session, version, token);
        }

        // The header is the geometry truth, and a local image gave it to the
        // stage before decoding. A first frame may be a downsampled stand-in,
        // so it sizes the stage only when nothing else did.
        if (_stageFitVersion != version)
        {
            SetStageFitHint(
                header is null
                    ? StageFitHint.ImagePixels(session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight)
                    : StageFitHint.ImagePixels(header.Width, header.Height),
                version);
        }

        var viewer = new ImageViewer(session.CurrentBitmap, header?.LongSide ?? 0, session);
        session.NotSaved += result => OnRotationNotSaved(session, viewer, result);
        RegisterImageZoom(viewer.Element, viewer);
        return viewer.Element;
    }

    /// <summary>As for an image: <paramref name="known"/> is the header the stage read, or null.</summary>
    private async Task<FrameworkElement?> TryBuildPsdPreviewAsync(
        string path,
        long version,
        CancellationToken token,
        ImageHeader? known)
    {
        var (header, composite) = await Task.Run(
            () => (known ?? ImageHeader.TryRead(path), PsdPreviewLoader.TryLoad(path, token)),
            token);
        if (composite is null)
        {
            return null;
        }

        // The PSD header names the full canvas before its composite decodes.
        // A large composite is downsampled, so it sizes the stage only when
        // nothing else did.
        if (_stageFitVersion != version)
        {
            SetStageFitHint(
                header is null
                    ? StageFitHint.ImagePixels(composite.PixelWidth, composite.PixelHeight)
                    : StageFitHint.ImagePixels(header.Width, header.Height),
                version);
        }

        var viewer = new ImageViewer(composite, header?.LongSide ?? 0, rotation: null);
        RegisterImageZoom(viewer.Element, viewer);
        return viewer.Element;
    }

    /// <summary>The content area this item's stage will have, in device pixels.</summary>
    private Size StageContentPixels()
    {
        var dpi = DpiScaleFor(Hwnd);
        var window = Hwnd == IntPtr.Zero || IsStageMaximized || _itemStageSize is not null
            ? new Size(ActualWidth, ActualHeight)
            : ComputeStageTarget(GetWorkAreaDips(Hwnd), Hwnd);
        return new Size(
            Math.Max(1, (window.Width - StageFit.ChromeWidth) * dpi),
            Math.Max(1, (window.Height - StageFit.ChromeHeight) * dpi));
    }

    /// <summary>
    /// A cached decode when there is one. A giant image (past the decode
    /// cap) first shows the quickest picture that is sharp at the stage:
    /// Explorer's cached thumbnail when it covers the stage, otherwise a
    /// JPEG decoded straight to stage size. Everything else decodes once,
    /// in full.
    /// </summary>
    private ImageFirstFrame? DecodeImageFirstFrame(string path, ImageHeader? header, Size stagePixels)
    {
        var hasStamp = ImageFileStamp.TryCreate(path, out var stamp);
        if (hasStamp && _imageDecodeCache.TryGet(stamp, out var cached) && cached is not null)
        {
            DiagnosticLog.Write($"image decode cache hit {path}");
            return new ImageFirstFrame(
                cached.Bitmap,
                cached.Orientation,
                IsFullDecode: true,
                CoversStage: true,
                stamp,
                _imageDecodeCache.CaptureGeneration());
        }

        var cacheGeneration = _imageDecodeCache.CaptureGeneration();
        ImageFileStamp? fileStamp = hasStamp ? stamp : null;
        var needed = header is null
            ? 0
            : (int)Math.Ceiling(header.LongSide * Math.Min(1, ZoomGestures.Fit(new Size(header.Width, header.Height), stagePixels)));
        if (header is not null && header.LongSide > MaxImageDecodePixels && needed < MaxImageDecodePixels)
        {
            if (ShellThumbnailProvider.TryGetCachedThumbnail(path, needed) is { } thumbnail)
            {
                // Short of the stage it would be a blurred stand-in. A JPEG
                // gets a sharp frame almost as fast, so it never takes one;
                // other formats would wait on a full inflate, so they take
                // any real thumbnail, but never an icon-sized one.
                var have = Math.Max(thumbnail.Bitmap.PixelWidth, thumbnail.Bitmap.PixelHeight);
                var covers = have >= needed - 1;
                if (covers || (!header.IsJpeg && have * 2 >= needed))
                {
                    DiagnosticLog.Write(
                        $"image cached first frame {thumbnail.Bitmap.PixelWidth}x{thumbnail.Bitmap.PixelHeight} for {needed} {path}");
                    // Explorer already applies EXIF orientation to its thumbnail.
                    return new ImageFirstFrame(
                        thumbnail.Bitmap,
                        ImageOrientation.Normal,
                        IsFullDecode: false,
                        CoversStage: covers,
                        fileStamp,
                        cacheGeneration);
                }
            }

            if (header.IsJpeg && TryDecodeImage(path, header, needed) is { } staged)
            {
                DiagnosticLog.Write($"image stage-size first frame {staged.PixelWidth}x{staged.PixelHeight} {path}");
                return new ImageFirstFrame(
                    staged,
                    header.Orientation,
                    IsFullDecode: false,
                    CoversStage: true,
                    fileStamp,
                    cacheGeneration);
            }
        }

        var bitmap = TryDecodeImage(path, header);
        if (bitmap is null)
        {
            return null;
        }

        var orientation = header?.Orientation ?? ImageOrientation.Normal;
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
            CoversStage: true,
            fileStamp,
            cacheGeneration);
    }

    private async Task CompleteLargeImageDecodeAsync(
        string path,
        ImageHeader? header,
        ImageFirstFrame first,
        ImageRotationSession session,
        long version,
        CancellationToken token)
    {
        try
        {
            // A first frame that is already sharp at fit can wait, so
            // scrubbing past giant images never stacks full decodes. A soft
            // one cannot.
            if (first.CoversStage)
            {
                await Task.Delay(LargeImageFullDecodeDelay, token);
            }

            await _imageDecodeGate.WaitAsync(token);
            BitmapSource? bitmap;
            try
            {
                token.ThrowIfCancellationRequested();
                bitmap = await Task.Run(() => TryDecodeImage(path, header), token);
            }
            finally
            {
                _imageDecodeGate.Release();
            }

            if (bitmap is null
                || token.IsCancellationRequested
                || version != _loadVersion
                || !ReferenceEquals(_rotationSession, session))
            {
                return;
            }

            var decoded = new CachedImageDecode(bitmap, header?.Orientation ?? ImageOrientation.Normal);
            if (first.Stamp is { } fileStamp)
            {
                _imageDecodeCache.Store(fileStamp, decoded, first.CacheGeneration);
            }

            if (session.TryUpgradeDecoded(decoded.Bitmap, decoded.Orientation))
            {
                DiagnosticLog.Write($"image full decode ready {bitmap.PixelWidth}x{bitmap.PixelHeight} {path}");
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

    /// <summary>
    /// Decodes at most <paramref name="longSide"/> pixels on the long side.
    /// A JPEG scales while it decodes, which is what makes a stage-size first
    /// frame cheap. An icon decodes its largest frame.
    /// </summary>
    internal static BitmapSource? TryDecodeImage(string path, ImageHeader? header, int longSide = MaxImageDecodePixels)
    {
        try
        {
            // Opened to share with every reader and writer. Given the path
            // instead, WPF opens the file against writers for as long as the
            // decode runs, and a rotation saved in that time, by this same
            // preview, was refused the file.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                var frame = ImageHeader.LargestFrame(
                    BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad));
                frame.Freeze();
                return frame;
            }

            // A stream has no URI, so WPF's URI cache never answers for it:
            // our stamp-aware one-entry cache owns reuse, and an externally
            // changed file can never inherit pixels held alive by an entry.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (header is not null && header.LongSide > longSide)
            {
                if (header.StoredWidth >= header.StoredHeight)
                {
                    bitmap.DecodePixelWidth = longSide;
                }
                else
                {
                    bitmap.DecodePixelHeight = longSide;
                }
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    // The image on screen owns the keyboard zoom: it claims it when it joins
    // the window and lets go when it leaves, unless a newer image took it.
    private void RegisterImageZoom(FrameworkElement element, IZoomableImage zoom)
    {
        element.Loaded += (_, _) => _imageZoom = zoom;
        element.Unloaded += (_, _) =>
        {
            if (ReferenceEquals(_imageZoom, zoom))
            {
                _imageZoom = null;
            }
        };
    }

    /// <summary>Dev probe: the double-click gesture at the center of the image on screen.</summary>
    internal void ProbeImageToggle() => _imageZoom?.ToggleActualSize();

    private void UpdateRotateButtons()
    {
        var suffix = RotateTooltipSuffix(_rotationSession?.Persistence);
        RotateLeftButton.ToolTip = $"Rotate left (Ctrl+Shift+R){suffix}";
        RotateRightButton.ToolTip = $"Rotate right (Ctrl+R){suffix}";
        SyncHeaderTools();
    }

    /// <summary>What the rotate tooltips add when a turn will show and not be written, with the true reason.</summary>
    internal static string RotateTooltipSuffix(RotationPersistence? persistence) => persistence switch
    {
        RotationPersistence.ViewOnly => ". Not saved for this file type.",
        RotationPersistence.ReadOnly => ". Not saved. This file is read-only.",
        RotationPersistence.CannotCarry => ". Not saved. This file can't store a rotation.",
        _ => string.Empty
    };

    private void RotateLeftButton_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrentImage(-1);
    }

    private void RotateRightButton_Click(object sender, RoutedEventArgs e)
    {
        RotateCurrentImage(1);
    }

    /// <summary>
    /// A window that closes with a turn not yet written hands it to the
    /// disk. The app's end closes its windows without hiding them first,
    /// and then waits for what they handed over (App.OnExit).
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _rotationSession?.Commit();
        _rotationSession = null;
        base.OnClosed(e);
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

    /// <summary>
    /// A rotation that did not reach its file. While its preview is up the
    /// view has turned back and the image says why; once the preview has
    /// closed or moved on, the tray says it.
    /// </summary>
    private void OnRotationNotSaved(ImageRotationSession session, ImageViewer viewer, RotationSaveResult result)
    {
        DiagnosticLog.Write($"rotate not saved result={result} left={session.HasLeft} path={session.Path}");
        if (session.HasLeft)
        {
            var (title, text) = RotationNotSavedTrayNotice(session.Path, result);
            RotationNotSavedUnseen?.Invoke(title, text);
            return;
        }

        // A file found unable to carry a rotation is view-only from here,
        // and the tooltips say so.
        UpdateRotateButtons();
        viewer.ShowNotice(RotationNotSavedNotice(result));
    }

    /// <summary>
    /// A rotation failed to save after its preview had closed or moved on.
    /// Carries a title and one line for the tray's quiet notice.
    /// </summary>
    internal static event Action<string, string>? RotationNotSavedUnseen;

    /// <summary>What happened, then what to do (docs/DESIGN.md, Words).</summary>
    internal static string RotationNotSavedNotice(RotationSaveResult result) => result switch
    {
        RotationSaveResult.InUse => "Rotation not saved. Close the app using this file, then rotate again.",
        RotationSaveResult.ReadOnly => "Rotation not saved. This file is read-only.",
        RotationSaveResult.DiskFull => "Rotation not saved. Free up disk space, then rotate again.",
        RotationSaveResult.CannotCarry => "Rotation not saved. This file can't store it.",
        _ => "Rotation not saved. This file can't be changed."
    };

    /// <summary>The same for a file no longer on screen, which needs its name.</summary>
    internal static (string Title, string Text) RotationNotSavedTrayNotice(string path, RotationSaveResult result)
    {
        var name = Path.GetFileName(path);
        if (name.Length > 48)
        {
            name = $"{name[..30]}\u2026{name[^15..]}";
        }

        return ("Rotation not saved", result switch
        {
            RotationSaveResult.InUse => $"Close the app using {name}, then rotate it again.",
            RotationSaveResult.ReadOnly => $"{name} is read-only.",
            RotationSaveResult.DiskFull => $"Free up disk space, then rotate {name} again.",
            RotationSaveResult.CannotCarry => $"{name} can't store a rotation.",
            _ => $"{name} can't be changed."
        });
    }

    /// <summary>
    /// Whether this image moves: a GIF by its name or signature, a PNG or a
    /// WebP by what its header says. WPF freezes an animation on its first
    /// frame, so all three take one route, to the browser image page.
    /// </summary>
    internal static async Task<bool> IsAnimatedImageAsync(
        string path,
        FileSignatureKind signature,
        bool useSignatureRouting,
        CancellationToken token)
    {
        if (FileKinds.IsGif(path) || (useSignatureRouting && signature == FileSignatureKind.Gif))
        {
            return true;
        }

        return (FileKinds.IsImage(path) || (useSignatureRouting && FileSignatures.IsImage(signature)))
            && await Task.Run(() => ImageHeader.TryRead(path)?.IsAnimated == true, token);
    }

    private FrameworkElement BuildBrowserImagePreview(string path)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        var webView = CreateWebView(
            WebSurfaceKind.Page,
            () => ImagePage.Build(path),
            $"browser image {path}",
            ex => _ = ShowFileCardFallbackAsync(path));
        grid.Children.Add(webView);
        RegisterImageZoom(grid, new WebImageZoom(webView));
        return grid;
    }

    /// <summary>Keyboard zoom for the web image page; the page owns the gesture math.</summary>
    private sealed class WebImageZoom(WebView2 webView) : IZoomableImage
    {
        public void ZoomBy(int direction) => _ = webView.CoreWebView2?.ExecuteScriptAsync($"glimpseZoom({direction})");

        public void ToggleActualSize() => _ = webView.CoreWebView2?.ExecuteScriptAsync("glimpseToggle()");
    }

    /// <summary>Whether a GIF declares a transparent color in a graphic control block near its start.</summary>
    internal static bool GifHasTransparency(string path)
    {
        try
        {
            var buffer = new byte[64 * 1024];
            int read;
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            }

            // 21 F9 04 opens a graphic control block; bit 0 of the next byte
            // flags a transparent color.
            for (var at = 0; at + 3 < read; at++)
            {
                if (buffer[at] == 0x21 && buffer[at + 1] == 0xF9 && buffer[at + 2] == 0x04 && (buffer[at + 3] & 1) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
