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

    private async Task<FrameworkElement?> TryBuildImagePreviewAsync(string path, long version, CancellationToken token)
    {
        var stage = StageContentPixels();
        var (header, decoded) = await Task.Run(() =>
        {
            var read = ImageHeader.TryRead(path);
            return (read, DecodeImageFirstFrame(path, read, stage));
        }, token);
        if (decoded is null)
        {
            return null;
        }

        // Camera photos carry their upright direction as metadata; Explorer
        // and its thumbnails honor it, so the preview must agree with what
        // sits next to it.
        var session = new ImageRotationSession(path, decoded.Bitmap, decoded.Orientation);
        if (!token.IsCancellationRequested)
        {
            // A superseded load must not hand its session to the preview
            // that replaced it.
            _rotationSession = session;
        }

        if (!decoded.IsFullDecode)
        {
            _ = CompleteLargeImageDecodeAsync(path, header, decoded, session, version, token);
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

        return BuildZoomableImageViewer(session.CurrentBitmap, header?.LongSide ?? 0, session);
    }

    private async Task<FrameworkElement?> TryBuildPsdPreviewAsync(string path, long version, CancellationToken token)
    {
        var (header, composite) = await Task.Run(
            () => (ImageHeader.TryRead(path), PsdPreviewLoader.TryLoad(path, token)),
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

        return BuildZoomableImageViewer(composite, header?.LongSide ?? 0);
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
            if (Path.GetExtension(path).Equals(".ico", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var frame = ImageHeader.LargestFrame(
                    BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad));
                frame.Freeze();
                return frame;
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // Our stamp-aware one-entry cache owns reuse. Bypass WPF's URI
            // cache so an externally changed file can never inherit pixels
            // held alive by the previous entry.
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.IgnoreImageCache;
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

    private FrameworkElement BuildZoomableImageViewer(BitmapSource bitmap, int sourceLongSide, ImageRotationSession? rotation = null)
    {
        var viewer = new ImageViewer(bitmap, sourceLongSide, rotation);
        RegisterImageZoom(viewer.Element, viewer);
        return viewer.Element;
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
        var suffix = _rotationSession?.Persistence == RotationPersistence.ViewOnly
            ? ". Not saved for this file type."
            : string.Empty;
        RotateLeftButton.ToolTip = $"Rotate left (Ctrl+Shift+R){suffix}";
        RotateRightButton.ToolTip = $"Rotate right (Ctrl+R){suffix}";
        SyncHeaderTools();
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

    private FrameworkElement BuildBrowserImagePreview(string path)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        var webView = CreateWebView(
            WebSurfaceKind.Page,
            () => WriteImageHtml(path),
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

    /// <summary>
    /// The page for images the web engine draws (animated GIF, SVG, AVIF).
    /// It follows the same gesture spec as the native viewer, written in from
    /// <see cref="ZoomGestures"/>: raster images measure in device pixels;
    /// an SVG measures in its own units, which are CSS pixels.
    /// </summary>
    internal static Uri WriteImageHtml(string imagePath)
    {
        static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        static string Css(Color color) =>
            $"rgba({color.R},{color.G},{color.B},{(color.A / 255.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})";
        static string Number(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        var palette = Theme.Current;
        var isSvg = FileKinds.IsSvg(imagePath);
        var svgSize = isSvg && SvgSizeProbe.TryReadCanvasSize(imagePath, out var canvasSize) ? canvasSize : new Size(0, 0);
        var checker = isSvg || (FileKinds.IsGif(imagePath) && GifHasTransparency(imagePath));
        var recipe = string.Join(
            '|',
            imagePath,
            Hex(palette.CanvasBg),
            Hex(palette.RaisedBg),
            Hex(palette.RaisedBorder),
            Number(svgSize.Width),
            Number(svgSize.Height),
            checker,
            "v9");
        // The key carries the full recipe, so an existing file is already
        // correct, and skipping the rewrite avoids racing another window's
        // navigation read of the same file.
        var htmlPath = TempHtmlPath("ImageHtml", recipe);
        if (File.Exists(htmlPath))
        {
            return new Uri(htmlPath, UriKind.Absolute);
        }

        var source = WebUtility.HtmlEncode(new Uri(imagePath, UriKind.Absolute).AbsoluteUri);
        var html = $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
html,body{margin:0;width:100%;height:100%;background:{{Hex(palette.CanvasBg)}};overflow:hidden;}
html{animation:glimpse-in 160ms cubic-bezier(.2,0,0,1) backwards;}
@keyframes glimpse-in{from{opacity:0} }
@media (prefers-reduced-motion:reduce){html{animation:none} }
#pic{position:absolute;left:0;top:0;user-select:none;-webkit-user-drag:none;}
#pic.checker{background:repeating-conic-gradient({{Hex(palette.RaisedBg)}} 0 25%,{{Hex(palette.RaisedBorder)}} 0 50%) 0 0/16px 16px;}
#pic.pixelated{image-rendering:pixelated;}
#hud{position:fixed;left:50%;bottom:18px;transform:translateX(-50%);padding:6px 12px 7px;border-radius:{{Number(Metrics.RadiusSurface)}}px;background:{{Css(Theme.OverlayScrim)}};color:{{Hex(Theme.OverlayText)}};font:600 {{Number(Metrics.FontLabel)}}px "Segoe UI Variable Text","Segoe UI",sans-serif;opacity:0;pointer-events:none;}
#hud.fade{transition:opacity {{Number(ZoomGestures.HudFade.TotalMilliseconds)}}ms ease {{Number(ZoomGestures.HudHold.TotalMilliseconds)}}ms;}
@media (prefers-reduced-motion:reduce){#hud.fade{transition:opacity 0s linear {{Number(ZoomGestures.HudHold.TotalMilliseconds)}}ms;} }
</style>
</head>
<body>
<img id="pic" src="{{source}}"{{(checker ? " class=\"checker\"" : "")}}>
<div id="hud"></div>
<script>
const STEP = {{Number(ZoomGestures.WheelStep)}}, MAX = {{Number(ZoomGestures.MaxScale)}}, EPS = {{Number(ZoomGestures.Tolerance)}}, PIXEL = {{Number(ZoomGestures.PixelScale)}};
const VECTOR = {{(isSvg ? "true" : "false")}}, HINT_W = {{Number(svgSize.Width)}}, HINT_H = {{Number(svgSize.Height)}};
const pic = document.getElementById('pic');
const hud = document.getElementById('hud');
let natW = 0, natH = 0, scale = 1, fit = 1, x = 0, y = 0, atFit = true;

// A raster pixel is one device pixel at 1:1; an SVG unit is a CSS pixel.
const unit = () => VECTOR ? 1 : 1 / (window.devicePixelRatio || 1);
const isAt = (a, b) => Math.abs(a - b) <= EPS * Math.max(b, EPS);
const clampScale = s => Math.min(Math.max(s, Math.min(fit, 1)), Math.max(fit, MAX));
const clampOffset = (o, content, view) => content <= view ? (view - content) / 2 : Math.min(0, Math.max(view - content, o));

function measure() {
  // A percentage-sized SVG can report a browser default instead of its
  // viewBox; the probed canvas carries the drawing's real shape.
  natW = HINT_W > 0 ? HINT_W : pic.naturalWidth;
  natH = HINT_H > 0 ? HINT_H : pic.naturalHeight;
  if (!natW || !natH) {
    const box = pic.getBoundingClientRect();
    natW = box.width || 300;
    natH = box.height || 150;
  }
}

function layout() {
  if (!natW) return;
  const baseW = natW * unit(), baseH = natH * unit();
  fit = Math.min(innerWidth / baseW, innerHeight / baseH);
  scale = atFit ? fit : clampScale(scale);
  const w = baseW * scale, h = baseH * scale;
  x = clampOffset(x, w, innerWidth);
  y = clampOffset(y, h, innerHeight);
  // Whole device pixels at the origin keep 1:1 crisp.
  const r = window.devicePixelRatio || 1;
  pic.style.width = w + 'px';
  pic.style.height = h + 'px';
  pic.style.transform = `translate(${Math.round(x * r) / r}px, ${Math.round(y * r) / r}px)`;
  pic.classList.toggle('pixelated', !VECTOR && scale > 1 + EPS && (!atFit || scale >= PIXEL - EPS));
  document.body.style.cursor = w > innerWidth + 0.5 || h > innerHeight + 0.5 ? 'move' : '';
}

function showHud() {
  hud.textContent = Math.round(scale * 100) + '%';
  hud.classList.remove('fade');
  hud.style.opacity = 1;
  void hud.offsetWidth;
  hud.classList.add('fade');
  hud.style.opacity = 0;
}

function zoomAt(cx, cy, target) {
  target = clampScale(target);
  x = cx - (cx - x) * target / scale;
  y = cy - (cy - y) * target / scale;
  scale = target;
  atFit = isAt(scale, fit);
  layout();
  showHud();
}

const doubleClickTarget = () => !isAt(scale, fit) ? fit : isAt(fit, 1) ? 2 : 1;
window.glimpseZoom = d => {
  if (d === 0) { atFit = true; layout(); showHud(); return; }
  zoomAt(innerWidth / 2, innerHeight / 2, scale * Math.pow(STEP, Math.sign(d)));
};
window.glimpseToggle = () => zoomAt(innerWidth / 2, innerHeight / 2, doubleClickTarget());

pic.addEventListener('load', () => { measure(); layout(); });
if (pic.complete) { measure(); layout(); }
window.addEventListener('resize', layout);

window.addEventListener('wheel', e => {
  e.preventDefault();
  const steps = Math.max(-3, Math.min(3, -e.deltaY / (e.deltaMode === 1 ? 3 : 100)));
  zoomAt(e.clientX, e.clientY, scale * Math.pow(STEP, steps));
}, { passive: false });

window.addEventListener('dblclick', e => zoomAt(e.clientX, e.clientY, doubleClickTarget()));

window.addEventListener('keydown', e => {
  if (!e.ctrlKey || e.altKey) return;
  const d = e.key === '+' || e.key === '=' ? 1 : e.key === '-' ? -1 : e.key === '0' ? 0 : null;
  if (d === null) return;
  e.preventDefault();
  window.glimpseZoom(d);
});

let dragging = false, lastX = 0, lastY = 0;
window.addEventListener('pointerdown', e => {
  if (e.button !== 0 || document.body.style.cursor !== 'move') return;
  dragging = true;
  lastX = e.clientX; lastY = e.clientY;
  document.body.setPointerCapture(e.pointerId);
});
window.addEventListener('pointermove', e => {
  if (!dragging) return;
  x += e.clientX - lastX;
  y += e.clientY - lastY;
  lastX = e.clientX; lastY = e.clientY;
  layout();
});
const endDrag = () => { dragging = false; };
window.addEventListener('pointerup', endDrag);
document.body.addEventListener('lostpointercapture', endDrag);
</script>
</body>
</html>
""";
        File.WriteAllText(htmlPath, html);
        return new Uri(htmlPath, UriKind.Absolute);
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
