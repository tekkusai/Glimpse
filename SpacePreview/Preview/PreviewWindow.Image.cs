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
    // giant image decodes to the cap a beat later. Zoomed in past that, it
    // decodes once more at its own size. One viewer owns the gestures
    // (docs/DESIGN.md, Zoom and pan).
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

    /// <summary>
    /// The read of an image's header that shapes the stage, made at a
    /// glance on the UI thread. It answers nothing while a rotation of the
    /// file is on its way to disk: the file is about to change, and the
    /// decode, which waits for the write, names the shape and the size.
    /// The decode asks for the header again by the file's stamp, so this
    /// read is used a second time only for the file it described.
    /// </summary>
    private static ImageHeader? ReadStageHeader(string path) =>
        ImageRotationSession.WhenSaved(path).IsCompleted ? ImageHeader.TryRead(path, atAGlance: true) : null;

    /// <summary>An image's pixel size as the details line gives it: as displayed, after the EXIF turn.</summary>
    private static string PixelSize(ImageHeader header) => $"{header.Width}×{header.Height}";

    private async Task<FrameworkElement?> TryBuildImagePreviewAsync(string path, long version, CancellationToken token)
    {
        // A rotation of this same file still on its way to disk lands
        // first, so the preview opens on what the file is and not on what
        // it was a moment ago.
        var saved = ImageRotationSession.WhenSaved(path);
        if (!saved.IsCompleted)
        {
            await saved.WaitAsync(token);
        }

        var stage = StageContentPixels();
        var (header, decoded, persistence) = await Task.Run(() =>
        {
            // A save cut short by the app's end left its working file
            // beside the picture. Opening the picture queues its removal.
            ImageRotationSession.RemoveStrandedTemp(path);

            // One stamp names the file for the header and for the decode.
            // The header the stage read answers from memory when the file
            // is still the one it described, and is read again when not.
            ImageFileStamp? stamp = ImageFileStamp.TryCreate(path, out var taken) ? taken : null;
            var read = stamp is { } current ? ImageHeader.TryRead(current, token: token) : null;
            var first = DecodeImageFirstFrame(path, read, stamp, stage);
            return (read, first, first is null ? RotationPersistence.ViewOnly : ImageRotationService.Classify(path, token));
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

        // The header the picture was decoded under is the geometry truth.
        // A local image gave it to the stage before decoding. Where that
        // read was skipped (a rotation still being written, a file whose
        // bytes were not at hand) or described a file that has changed
        // since, the stage and the details line take it here. A first frame
        // may be a downsampled stand-in, so with no header it sizes the
        // stage only when nothing else did.
        var staged = _stageFitVersion == version ? _stageFitHint : null;
        if (header is null)
        {
            if (staged is null)
            {
                SetStageFitHint(StageFitHint.ImagePixels(session.CurrentBitmap.PixelWidth, session.CurrentBitmap.PixelHeight), version);
            }
        }
        else if (version == _loadVersion)
        {
            if (staged is not { Kind: StageFitHint.FitKind.ImagePixels } shape || shape.Width != header.Width || shape.Height != header.Height)
            {
                SetStageFitHint(StageFitHint.ImagePixels(header.Width, header.Height), version);
            }

            // The details line was written as the window opened. When the
            // file has been written since, its size and time there are
            // from before, and are read again along with its pixels.
            var written = decoded.Stamp is { } now
                && _headerItem is FileInfo { Exists: true } then
                && string.Equals(then.FullName, now.Path, StringComparison.OrdinalIgnoreCase)
                && (then.Length != now.Length || then.LastWriteTimeUtc.Ticks != now.LastWriteUtcTicks);
            if (written)
            {
                _headerItem = null;
            }

            if (written || _headerFact != PixelSize(header))
            {
                ShowHeaderFact(PixelSize(header), version);
            }
        }

        var viewer = new ImageViewer(session.CurrentBitmap, header?.LongSide ?? 0, session, FileKinds.IsPhotograph(path));
        session.NotSaved += result => OnRotationNotSaved(session, viewer, result);

        // The session holds a turn back from the disk until this ends too,
        // so the decode never reads a file that is being written.
        viewer.SharperWanted += () =>
            session.FullDecode = SharpenImageAsync(path, session, viewer, session.FullDecode, version, token);
        RegisterImageZoom(viewer.Element, viewer);
        return viewer.Element;
    }

    private async Task<FrameworkElement?> TryBuildPsdPreviewAsync(string path, long version, CancellationToken token)
    {
        // The header the stage read answers from memory, while the file is
        // still the one it described.
        var (header, composite) = await Task.Run(
            () => (ImageHeader.TryRead(path, token: token), PsdPreviewLoader.TryLoad(path, token)),
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

        var viewer = new ImageViewer(composite, header?.LongSide ?? 0, rotation: null, FileKinds.IsPhotograph(path));
        viewer.SharperWanted += () => _ = SharpenPhotoshopAsync(path, viewer, version, token);
        RegisterImageZoom(viewer.Element, viewer);
        return viewer.Element;
    }

    /// <summary>
    /// A Photoshop file zoomed past the composite it opened with: the
    /// composite is decoded again, at its own size or the budget's.
    /// </summary>
    private async Task SharpenPhotoshopAsync(string path, ImageViewer viewer, long version, CancellationToken token)
    {
        try
        {
            await _imageDecodeGate.WaitAsync(token);
            BitmapSource? bitmap;
            try
            {
                token.ThrowIfCancellationRequested();
                bitmap = await Task.Run(
                    () => PsdPreviewLoader.TryReadPixelSize(path, out var size)
                        ? PsdPreviewLoader.TryLoad(path, token, SharpLongSide((int)size.Width, (int)size.Height))
                        : null,
                    token);
            }
            finally
            {
                _imageDecodeGate.Release();
            }

            if (bitmap is not null && !token.IsCancellationRequested && version == _loadVersion && viewer.TakeSharper(bitmap))
            {
                DiagnosticLog.Write($"psd sharp decode ready {bitmap.PixelWidth}x{bitmap.PixelHeight} {path}");
            }
        }
        catch (OperationCanceledException)
        {
            // The picture left before it was decoded.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"psd sharp decode failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
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
    /// in full. <paramref name="fileStamp"/> is the file as it was when
    /// <paramref name="header"/> was asked for, and a decode is kept and
    /// found again under it.
    /// </summary>
    private ImageFirstFrame? DecodeImageFirstFrame(string path, ImageHeader? header, ImageFileStamp? fileStamp, Size stagePixels)
    {
        if (fileStamp is { } stamp && _imageDecodeCache.TryGet(stamp, out var cached) && cached is not null)
        {
            DiagnosticLog.Write($"image decode cache hit {path}");
            return new ImageFirstFrame(
                cached.Bitmap,
                cached.Orientation,
                IsFullDecode: true,
                CoversStage: true,
                fileStamp,
                _imageDecodeCache.CaptureGeneration());
        }

        var cacheGeneration = _imageDecodeCache.CaptureGeneration();
        var needed = header is null
            ? 0
            : (int)Math.Ceiling(header.LongSide * Math.Min(1, ZoomGestures.Fit(new Size(header.Width, header.Height), stagePixels)));
        // A RAW has one picture (see TryDecodeRaw). A thumbnail shown first
        // would be the camera's rendering, swapped a moment later for the
        // decoder's: two different-looking photos of one file.
        if (header is not null && header.LongSide > MaxImageDecodePixels && needed < MaxImageDecodePixels && !FileKinds.IsCameraRaw(path))
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
        if (fileStamp is { } decodedUnder)
        {
            _imageDecodeCache.Store(
                decodedUnder,
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
    /// The long side a picture of this size is decoded to when it is zoomed
    /// past the cap: its own, or the one that keeps it inside the budget.
    /// </summary>
    internal static int SharpLongSide(int width, int height)
    {
        var pixels = (long)width * height;
        var scale = pixels > SharpImageDecodePixels ? Math.Sqrt((double)SharpImageDecodePixels / pixels) : 1;
        return (int)Math.Min(Math.Floor(Math.Max(width, height) * scale), SharpImageDecodeLongSide);
    }

    /// <summary>
    /// The viewer is showing more of the picture than its decode holds.
    /// The picture is decoded again, larger, and takes the place of the one
    /// on screen without moving. A turn on its way to the file lands first
    /// and the file is read as it then is. The larger decode is not kept
    /// once the picture leaves: reopened, it starts from the cap again.
    /// </summary>
    private async Task SharpenImageAsync(
        string path,
        ImageRotationSession session,
        ImageViewer viewer,
        Task earlier,
        long version,
        CancellationToken token)
    {
        try
        {
            // A first frame's own decode lands first. It may be all the
            // zoom on screen needs, and the viewer may ask again later.
            await Task.WhenAny(earlier);
            await session.WhenWritten().WaitAsync(token);
            if (!viewer.WantsSharper)
            {
                viewer.SharperDeclined();
                return;
            }

            var landed = session.Landed;
            await _imageDecodeGate.WaitAsync(token);
            ImageHeader? header;
            BitmapSource? bitmap;
            try
            {
                token.ThrowIfCancellationRequested();
                (header, bitmap) = await Task.Run<(ImageHeader?, BitmapSource?)>(() =>
                {
                    if (ImageHeader.TryRead(path, token: token) is not { } read)
                    {
                        return (null, null);
                    }

                    var longSide = SharpLongSide(read.Width, read.Height);
                    return (read, longSide > MaxImageDecodePixels ? TryDecodeImage(path, read, longSide) : null);
                }, token);
            }
            finally
            {
                _imageDecodeGate.Release();
            }

            if (header is null
                || bitmap is null
                || token.IsCancellationRequested
                || version != _loadVersion
                || !ReferenceEquals(_rotationSession, session))
            {
                return;
            }

            if (session.TrySharpen(bitmap, header.Orientation, landed))
            {
                DiagnosticLog.Write($"image sharp decode ready {bitmap.PixelWidth}x{bitmap.PixelHeight} {path}");
            }
        }
        catch (OperationCanceledException)
        {
            // The picture left before it was decoded.
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"image sharp decode failed {path}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes at most <paramref name="longSide"/> pixels on the long side.
    /// A JPEG scales while it decodes, which is what makes a stage-size first
    /// frame cheap. An icon decodes its largest frame.
    /// </summary>
    internal static BitmapSource? TryDecodeImage(string path, ImageHeader? header, int longSide = MaxImageDecodePixels)
    {
        // A RAW decodes only under a header, and only the RAW decoder
        // gives it one: any other decoder would show the thumbnail inside.
        if (header is null && FileKinds.IsCameraRaw(path))
        {
            return null;
        }

        try
        {
            // Opened to share with every reader and writer. Given the path
            // instead, WPF opens the file against writers for as long as the
            // decode runs, and a rotation saved in that time, by this same
            // preview, was refused the file.
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (header is not null && FileKinds.IsCameraRaw(path))
            {
                return DecodeRaw(stream, header, longSide);
            }

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

    /// <summary>
    /// The one picture a RAW shows. A camera saves its own JPEG of the shot
    /// inside the file: when that is as large as the photo would be shown
    /// anyway, it is the picture. It is the look the photographer chose and
    /// what Explorer's thumbnail shows, and it opened in 30 ms where the
    /// sensor data took 450. A small one is a thumbnail, and the decoder's
    /// rendering of the sensor is shown instead. Which of the two is the
    /// picture is settled at the cap, whatever size is asked for: zoomed
    /// in, a photo is the same photo, larger.
    /// </summary>
    private static BitmapSource DecodeRaw(Stream stream, ImageHeader header, int longSide)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        BitmapSource picture = decoder.Preview is { } preview
            && CamerasOwnJpegIsThePicture(Math.Max(preview.PixelWidth, preview.PixelHeight), header.LongSide)
                ? preview
                : decoder.Frames[0];
        var have = Math.Max(picture.PixelWidth, picture.PixelHeight);
        if (have > longSide)
        {
            var scale = (double)longSide / have;
            picture = new TransformedBitmap(picture, new ScaleTransform(scale, scale));
        }

        // Read whole while the file is open: the decoder works on demand.
        var copy = new WriteableBitmap(picture);
        copy.Freeze();
        return copy;
    }

    internal static bool CamerasOwnJpegIsThePicture(int jpegLongSide, int sensorLongSide) =>
        jpegLongSide >= Math.Min(sensorLongSide, MaxImageDecodePixels);

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

    // A picture in the image viewer, on a PC whose Windows can read text.
    private bool CanCopyPictureText => _imageZoom is ImageViewer && _rotationSession is not null && PictureText.IsAvailable;

    /// <summary>
    /// Copies the words in the picture on screen, read as it is shown. The
    /// viewer's notice says what happened. <paramref name="copy"/> is the
    /// clipboard, or a test's stand-in for it.
    /// </summary>
    internal async Task CopyPictureTextAsync(Action<string>? copy = null)
    {
        if (_imageZoom is not ImageViewer viewer || _rotationSession is not { } session)
        {
            return;
        }

        var version = _loadVersion;
        try
        {
            var reading = PictureText.ReadAsync(session.CurrentBitmap);

            // A large photo takes a moment. Past that moment the notice
            // says the click was taken.
            if (await Task.WhenAny(reading, Task.Delay(PictureTextPatience)) != reading)
            {
                viewer.ShowNotice("Reading the text");
            }

            var text = await reading;
            if (version != _loadVersion)
            {
                // The reader has moved to another file. Text arriving on
                // the clipboard now would be a surprise.
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                viewer.ShowNotice("No text found in this picture.");
                return;
            }

            (copy ?? (words => SetClipboard(() => Clipboard.SetText(words), "copy picture text")))(text);
            viewer.ShowNotice("Text copied.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"picture text failed {session.Path}: {ex.GetType().Name}: {ex.Message}");
            viewer.ShowNotice("Couldn't read the text in this picture.");
        }
    }

    private static readonly TimeSpan PictureTextPatience = TimeSpan.FromMilliseconds(300);

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
    /// closed or moved on, the tray says it. A picture left under another
    /// name is said in both places: the sentence on the image is gone in
    /// four seconds, and the name is what the user needs to find it.
    /// </summary>
    private void OnRotationNotSaved(ImageRotationSession session, ImageViewer viewer, RotationSaveResult result)
    {
        DiagnosticLog.Write($"rotate not saved result={result} left={session.HasLeft} path={session.Path}");
        if (session.HasLeft || result == RotationSaveResult.SavedAside)
        {
            var (title, text) = RotationNotSavedTrayNotice(session.Path, result);
            RotationNotSavedUnseen?.Invoke(title, text);
        }

        if (session.HasLeft)
        {
            return;
        }

        // A file found unable to carry a rotation is view-only from here,
        // and the tooltips say so.
        UpdateRotateButtons();
        viewer.ShowNotice(RotationNotSavedNotice(session.Path, result));
    }

    /// <summary>
    /// A rotation that the user cannot be told about in its preview, or not
    /// well enough there: it failed after the preview had closed or moved
    /// on, or it left the picture under another name. Carries a title and
    /// one line for the tray's quiet notice.
    /// </summary>
    internal static event Action<string, string>? RotationNotSavedUnseen;

    /// <summary>What happened, then what to do (docs/DESIGN.md, Words).</summary>
    internal static string RotationNotSavedNotice(string path, RotationSaveResult result) => result switch
    {
        RotationSaveResult.InUse => "Rotation not saved. Close the app using this file, then rotate again.",
        RotationSaveResult.ReadOnly => "Rotation not saved. This file is read-only.",
        RotationSaveResult.DiskFull => "Rotation not saved. Free up disk space, then rotate again.",
        RotationSaveResult.CannotCarry => "Rotation not saved. This file can't store it.",
        RotationSaveResult.SavedAside => SavedAsideNotice(path),
        _ => "Rotation not saved. This file can't be changed."
    };

    /// <summary>The same for a file no longer on screen, which needs its name.</summary>
    internal static (string Title, string Text) RotationNotSavedTrayNotice(string path, RotationSaveResult result)
    {
        if (result == RotationSaveResult.SavedAside)
        {
            return ("Rotation saved under another name", SavedAsideNotice(path));
        }

        var name = ShortName(path);
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
    /// The one sentence for a picture whose own name was lost in the swap:
    /// where the turned picture is now, and how to put it back.
    /// </summary>
    private static string SavedAsideNotice(string path) =>
        $"The rotated picture was saved as \"{ShortName(ImageRotationService.TempPathFor(path))}\" in the same folder. Rename it to \"{ShortName(path)}\".";

    /// <summary>A file's name, cut in the middle when it would push the sentence out of the notice.</summary>
    private static string ShortName(string path)
    {
        var name = Path.GetFileName(path);
        return name.Length > 48 ? $"{name[..30]}\u2026{name[^15..]}" : name;
    }

    /// <summary>
    /// Whether this image moves: a GIF by its name or signature, a PNG or a
    /// WebP by what its header says. WPF freezes an animation on its first
    /// frame, so all three take one route, to the browser image page. The
    /// header is read in full here, off the UI thread: where the glance
    /// that shaped the stage stopped short of a PNG's pixels, this walks
    /// the rest.
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
            && await Task.Run(() => ImageHeader.TryRead(path, token: token)?.IsAnimated == true, token);
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
