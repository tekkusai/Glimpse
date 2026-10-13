using System.Globalization;
using System.Net;
using System.Windows;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The page for images the web engine draws (animated GIF, PNG and WebP, SVG, AVIF). It
/// follows the same gesture spec as the native viewer, written in from
/// <see cref="ZoomGestures"/>: raster images measure in device pixels; an
/// SVG measures in its own units, which are CSS pixels. It starts from the
/// stylesheet every page shares, so it turns with the theme while open, and
/// is cached like the other pages, one per image and shape.
/// </summary>
internal static class ImagePage
{
    public static Uri Build(string imagePath)
    {
        var isSvg = FileKinds.IsSvg(imagePath);
        var svgSize = isSvg && SvgSizeProbe.TryReadCanvasSize(imagePath, out var canvasSize) ? canvasSize : new Size(0, 0);
        var checker = isSvg
            || (FileKinds.IsGif(imagePath)
                ? PreviewWindow.GifHasTransparency(imagePath)
                : FileKinds.IsImage(imagePath) && ImageHeader.TryRead(imagePath)?.HasAlpha == true);
        var pagePath = HtmlCache.Get(
            "ImageHtml",
            $"{imagePath}|{Number(svgSize.Width)}|{Number(svgSize.Height)}|{checker}",
            () => Page(imagePath, isSvg, svgSize, checker))!;
        return new Uri(pagePath, UriKind.Absolute);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string Page(string imagePath, bool isSvg, Size svgSize, bool checker)
    {
        var source = WebUtility.HtmlEncode(new Uri(imagePath, UriKind.Absolute).AbsoluteUri);
        var scrim = Theme.OverlayScrim;
        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
{{GeneratedHtmlTheme.PageCss}}
html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; }
#pic { position: absolute; left: 0; top: 0; user-select: none; -webkit-user-drag: none; }
#pic.checker { background: repeating-conic-gradient(var(--raised) 0 25%, var(--raised-border) 0 50%) 0 0 / 16px 16px; }
#pic.pixelated { image-rendering: pixelated; }
/* The zoom readout is the fixed dark glass of every HUD, in both themes. */
#hud {
  position: fixed;
  left: 50%;
  bottom: 18px;
  transform: translateX(-50%);
  padding: 6px 12px 7px;
  border-radius: {{Number(Metrics.RadiusSurface)}}px;
  background: {{GeneratedHtmlTheme.Rgba(scrim, scrim.A / 255.0)}};
  color: {{GeneratedHtmlTheme.Hex(Theme.OverlayText)}};
  font: 600 {{Number(Metrics.FontLabel)}}px var(--font-ui);
  opacity: 0;
  pointer-events: none;
}
#hud.fade { transition: opacity {{Number(ZoomGestures.HudFade.TotalMilliseconds)}}ms ease {{Number(ZoomGestures.HudHold.TotalMilliseconds)}}ms; }
@media (prefers-reduced-motion: reduce) {
  #hud.fade { transition: opacity 0s linear {{Number(ZoomGestures.HudHold.TotalMilliseconds)}}ms; }
}
</style>
</head>
<body>
<img id="pic" src="{{source}}"{{(checker ? " class=\"checker\"" : "")}}>
<div id="hud"></div>
<script>
const STEP = {{Number(ZoomGestures.WheelStep)}}, MAX = {{Number(ZoomGestures.MaxScale)}}, EPS = {{Number(ZoomGestures.Tolerance)}}, PIXEL = {{Number(ZoomGestures.PixelScale)}};
const VECTOR = {{(isSvg ? "true" : "false")}}, HINT_W = {{Number(svgSize.Width)}}, HINT_H = {{Number(svgSize.Height)}};
const PHOTO = {{(FileKinds.IsPhotograph(imagePath) ? "true" : "false")}};
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
  pic.classList.toggle('pixelated', !VECTOR && !PHOTO && scale > 1 + EPS && (!atFit || scale >= PIXEL - EPS));
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
    }
}
