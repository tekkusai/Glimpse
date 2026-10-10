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
    /// <summary>
    /// Space over a media preview always means play/pause. The host toggles
    /// it directly (the hook sees Space before the browser does), so the
    /// page never needs keyboard-focus games and the player's controls keep
    /// their own focus.
    /// </summary>
    public bool TryToggleMediaPlayback()
    {
        if (!IsMediaPreview
            || FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return false;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "(m => { if (!m) return; if (window.glimpseToggleMedia) { window.glimpseToggleMedia(); return; } if (m.paused) m.play().catch(e => { try { window.chrome.webview.postMessage({ error: m.error ? m.error.code : 'play', reason: e && e.name ? e.name : 'play' }); } catch {} }); else m.pause(); })(document.getElementById('media'))");
        }
        catch
        {
            // A torn-down view cannot toggle; Space then simply does nothing.
        }

        return true;
    }

    /// <summary>
    /// Media arrows go through the host, never the native controls (whose
    /// focus-walking is what "stuck" the keys). The active preview yields
    /// only up/down (volume) - left/right stay browsing. A pinned player
    /// browses nothing, so it gets keyboard seek on left/right too.
    /// </summary>
    public bool TryNudgeMediaFromArrow(int vk)
    {
        if (!IsMediaPreview || !(_userEngagedContent || IsPinned))
        {
            return false;
        }

        if (!IsPinned && vk is NativeMethods.VK_LEFT or NativeMethods.VK_RIGHT)
        {
            return false;
        }

        var nudge = vk switch
        {
            NativeMethods.VK_LEFT => "if(m&&isFinite(m.duration))m.currentTime=Math.max(0,Math.min(m.duration,m.currentTime-5))",
            NativeMethods.VK_RIGHT => "if(m&&isFinite(m.duration))m.currentTime=Math.max(0,Math.min(m.duration,m.currentTime+5))",
            NativeMethods.VK_UP => "if(m)m.volume=Math.max(0,Math.min(1,m.volume+0.05))",
            NativeMethods.VK_DOWN => "if(m)m.volume=Math.max(0,Math.min(1,m.volume-0.05))",
            _ => null
        };

        if (nudge is null || FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return false;
        }

        try
        {
            _ = core.ExecuteScriptAsync($"(m => {{ {nudge}; }})(document.getElementById('media'))");
        }
        catch
        {
            // A torn-down view cannot nudge; the key then does nothing.
        }

        return true;
    }

    /// <summary>
    /// Video reveal needs its shape before it appears. MP4 headers normally
    /// provide that shape up front; formats the probe cannot read wait for
    /// loadedmetadata instead, so a phone video never reveals sideways-boxed.
    /// </summary>
    private sealed class MediaStageState
    {
        public MediaStageState(bool dimensionsKnown)
        {
            DimensionsKnown = dimensionsKnown;
            if (dimensionsKnown)
            {
                DimsReady.TrySetResult(true);
            }
        }

        public bool DimensionsKnown { get; }

        public TaskCompletionSource<bool> DimsReady { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private FrameworkElement BuildMediaPreview(string path, long version, CancellationToken token)
    {
        var grid = new Grid { Background = Brushes.Transparent };
        if (!FileKinds.IsAudio(path))
        {
            var dimensionsKnown = _stageFitVersion == version
                && _stageFitHint is { Kind: StageFitHint.FitKind.Pixels };
            grid.Tag = new MediaStageState(dimensionsKnown);
        }
        var webView = CreateWebView(
            WebSurfaceKind.Page,
            () => WriteMediaHtml(path),
            $"web media {path}",
            _ =>
            {
                if (IsCurrentContentPath(path))
                {
                    SetPreviewContent(BuildErrorPreview("Glimpse can't play this file", MediaOpenInsteadText(path)), null);
                }
            },
            configure: core =>
            {
                core.WebMessageReceived += (_, args) =>
                    OnMediaMessage(path, version, token, args.WebMessageAsJson);

                // The video template hides the native fullscreen control, but
                // still handle browser fullscreen if a runtime exposes another
                // route into it.
                core.ContainsFullScreenElementChanged += (_, _) =>
                {
                    if (token.IsCancellationRequested || version != _loadVersion || !IsCurrentContentPath(path))
                    {
                        return;
                    }

                    var fullscreen = core.ContainsFullScreenElement;
                    _escapeYieldsToContent = fullscreen;
                    if (fullscreen != IsStageMaximized)
                    {
                        ToggleMaximize();
                        // Browsing away mid-fullscreen would otherwise leave
                        // the maximize latched with no one to release it.
                        _contentDrivenMaximize = fullscreen;
                    }
                };
            });
        grid.Children.Add(webView);
        return grid;
    }

    /// <summary>
    /// The player reports volume moves; the setting follows so the next
    /// preview opens at whatever loudness the user last chose.
    /// </summary>
    private void OnMediaMessage(string path, long version, CancellationToken token, string json)
    {
        if (token.IsCancellationRequested || version != _loadVersion || !IsCurrentContentPath(path))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (document.RootElement.TryGetProperty("dims", out var dimsElement)
                && dimsElement.ValueKind == JsonValueKind.Object)
            {
                if (IsCurrentContentPath(path)
                    && dimsElement.TryGetProperty("w", out var widthElement)
                    && dimsElement.TryGetProperty("h", out var heightElement)
                    && widthElement.TryGetDouble(out var videoWidth)
                    && heightElement.TryGetDouble(out var videoHeight)
                    && videoWidth >= 8 && videoHeight >= 8)
                {
                    // Chromium reports display dimensions with the rotation
                    // matrix already applied, so portrait phone video
                    // arrives portrait here.
                    SetStageFitHint(StageFitHint.Pixels(videoWidth, videoHeight), _loadVersion);
                }

                // Zero dims (audio-only in a video container) still release
                // the gate; the default stage is the right answer for them.
                if (PreviewContent.Content is FrameworkElement mediaContent
                    && mediaContent.Tag is MediaStageState mediaStage)
                {
                    mediaStage.DimsReady.TrySetResult(true);
                }

                return;
            }

            if (document.RootElement.TryGetProperty("error", out var errorElement))
            {
                var detail = errorElement.ValueKind switch
                {
                    JsonValueKind.String => errorElement.GetString(),
                    JsonValueKind.Number => errorElement.GetRawText(),
                    _ => "decode"
                };
                var reason = document.RootElement.TryGetProperty("reason", out var reasonElement)
                    && reasonElement.ValueKind == JsonValueKind.String
                        ? reasonElement.GetString()
                        : null;
                var codecs = Mp4CodecSniffer.TryRead(path);
                var codecText = codecs is null ? string.Empty : $" codecs {codecs.DiagnosticText}";
                DiagnosticLog.Write($"media decode failed {path}: {detail}{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $" {reason}")}{codecText}");
                if (IsCurrentContentPath(path))
                {
                    SetPreviewContent(
                        BuildMediaFailurePreview(path, codecs),
                        null);
                }

                return;
            }

            if (!document.RootElement.TryGetProperty("volume", out var volumeElement)
                || !volumeElement.TryGetDouble(out var volume))
            {
                return;
            }

            volume = Math.Clamp(volume, 0, 1);
            var muted = document.RootElement.TryGetProperty("muted", out var mutedElement) && mutedElement.ValueKind == JsonValueKind.True;
            if (Math.Abs(Settings.Current.MediaVolume - volume) < 0.005 && Settings.Current.MediaMuted == muted)
            {
                return;
            }

            Settings.Update(settings => settings with { MediaVolume = volume, MediaMuted = muted });
            DiagnosticLog.Write($"media volume saved {volume:0.00} muted={muted}");
        }
        catch
        {
            // A malformed message must not break the preview.
        }
    }

    // The player is dark glass in both themes, so this template does not
    // follow the app theme; its hex values mirror Theme.cs Dark (TextPrimary,
    // TextSecondary, TextTertiary, Accent). If those tokens move, move these.
    private static Uri WriteMediaHtml(string mediaPath)
    {
        var source = WebUtility.HtmlEncode(new Uri(mediaPath, UriKind.Absolute).AbsoluteUri);
        // Settings are user-editable; an out-of-range value would abort page setup.
        var savedVolume = Settings.Current.MediaVolume;
        var safeVolume = double.IsFinite(savedVolume) ? Math.Clamp(savedVolume, 0, 1) : 0.75;
        var volume = safeVolume.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var muted = Settings.Current.MediaMuted ? "true" : "false";
        var stamp = File.GetLastWriteTimeUtc(mediaPath).Ticks;
        // Include presentation state and template version so parallel previews
        // can reuse a complete page without rewriting it under another WebView.
        var htmlPath = TempHtmlPath("MediaHtml", $"{mediaPath}|{volume}|{muted}|{stamp}|v20");
        if (File.Exists(htmlPath))
        {
            return new Uri(htmlPath, UriKind.Absolute);
        }

        var isAudio = FileKinds.IsAudio(mediaPath);
        var modeClass = isAudio ? "audio" : "video";
        var isVideoLiteral = isAudio ? "false" : "true";
        string bodyHtml;

        if (isAudio)
        {
            ShellMediaProperties.TryGetAudioMetadata(mediaPath, out var audio);
            var title = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(audio.Title)
                ? Path.GetFileNameWithoutExtension(mediaPath)
                : audio.Title!.Trim());
            var line2 = string.Join(" · ", new[] { audio.DisplayArtist, audio.AlbumTitle }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => WebUtility.HtmlEncode(part!.Trim())));
            var line2Html = line2.Length == 0 ? "" : $"""<div id="line2">{line2}</div>""";
            var artUri = ShellThumbnailProvider.TryWriteThumbnailPng(mediaPath, $"{mediaPath}|{stamp}");
            var artHtml = artUri is null
                ? """<div id="tile"><span class="note">&#9835;</span><span class="spark">&#10022;</span></div>"""
                : $"""<img id="art" src="{WebUtility.HtmlEncode(artUri.AbsoluteUri)}" alt="">""";
            var durationText = audio.Duration is { } known
                ? known.TotalHours >= 1
                    ? $"{(int)known.TotalHours}:{known.Minutes:00}:{known.Seconds:00}"
                    : $"{(int)known.TotalMinutes}:{known.Seconds:00}"
                : "--:--";

            bodyHtml = $$"""
<div id="wrap">
{{artHtml}}
<div id="title">{{title}}</div>
{{line2Html}}
{{BuildMediaTransportHtml(durationText)}}
</div>
<audio id="media" src="{{source}}" autoplay></audio>
""";
        }
        else
        {
            bodyHtml = $$"""
<video id="media" src="{{source}}" autoplay playsinline disablepictureinpicture></video>
<div id="bar">
{{BuildMediaTransportHtml("--:--")}}
</div>
<div id="blocked">Click play to start</div>
""";
        }

        var html = $$"""
<!doctype html>
<html class="{{modeClass}}">
<head>
<meta charset="utf-8">
<style>
:root{color-scheme:dark;}
html.audio{
  --page-bg:radial-gradient(120% 120% at 50% 0%,#1d1f26 0%,#0c0d11 70%);
  --time-color:#8f95a0;
  --time-shadow:none;
  --seek-rest:rgba(255,255,255,.14);
  --seek-shadow:rgba(0,0,0,.5);
  --button-color:#c7cbd4;
  --button-hover-bg:rgba(255,255,255,.07);
  --button-hover-color:#f4f4f5;
  --button-active-bg:rgba(255,255,255,.12);
  --button-gap:22px;
  --skip-size:44px;
  --play-size:58px;
  --play-bg:rgba(255,255,255,.08);
  --play-hover-bg:rgba(255,255,255,.15);
  --mute-size:34px;
  --mute-radius:9px;
  --volume-width:92px;
  --volume-fill:#a9adb8;
  --volume-rest:rgba(255,255,255,.14);
  --volume-thumb:#e6e8ee;
  --scrub-margin:30px;
  --controls-margin:12px;
  --skip-icon:26px;
  --play-icon:24px;
  --volume-icon:18px;
}
html.video{
  --page-bg:#000;
  --time-color:#cdd1da;
  --time-shadow:0 1px 2px rgba(0,0,0,.7);
  --seek-rest:rgba(255,255,255,.22);
  --seek-shadow:rgba(0,0,0,.6);
  --button-color:#dde1e8;
  --button-hover-bg:rgba(255,255,255,.10);
  --button-hover-color:#fff;
  --button-active-bg:rgba(255,255,255,.16);
  --button-gap:18px;
  --skip-size:40px;
  --play-size:48px;
  --play-bg:rgba(255,255,255,.10);
  --play-hover-bg:rgba(255,255,255,.17);
  --mute-size:32px;
  --mute-radius:8px;
  --volume-width:88px;
  --volume-fill:#cdd1da;
  --volume-rest:rgba(255,255,255,.22);
  --volume-thumb:#f0f2f6;
  --scrub-margin:0;
  --controls-margin:2px;
  --skip-icon:24px;
  --play-icon:22px;
  --volume-icon:17px;
}
html,body{margin:0;width:100%;height:100%;background:var(--page-bg);overflow:hidden;font-family:"Segoe UI Variable","Segoe UI",sans-serif;user-select:none;}
html{animation:sp-reveal 160ms ease-out;}
@keyframes sp-reveal{from{opacity:0} }
#scrub{display:flex;align-items:center;gap:12px;width:100%;margin-top:var(--scrub-margin);}
.time{color:var(--time-color);font-size:12px;font-variant-numeric:tabular-nums;min-width:42px;text-shadow:var(--time-shadow);}
#elapsed{text-align:right;}
input[type=range]{-webkit-appearance:none;appearance:none;background:transparent;height:20px;cursor:pointer;margin:0;}
#seek{flex:1;}
#seek::-webkit-slider-runnable-track{height:4px;border-radius:2px;background:linear-gradient(90deg,#4e80f5 var(--p,0%),var(--seek-rest) var(--p,0%));}
#seek::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:12px;border-radius:50%;background:#f4f4f5;margin-top:-4px;box-shadow:0 1px 4px var(--seek-shadow);}
#controls{display:grid;grid-template-columns:1fr auto 1fr;align-items:center;width:100%;margin-top:var(--controls-margin);}
#buttons{display:flex;align-items:center;gap:var(--button-gap);grid-column:2;}
button{all:unset;cursor:pointer;display:flex;align-items:center;justify-content:center;border-radius:50%;color:var(--button-color);}
button:hover{background:var(--button-hover-bg);color:var(--button-hover-color);}
button:active{background:var(--button-active-bg);}
.skip{width:var(--skip-size);height:var(--skip-size);}
#play{width:var(--play-size);height:var(--play-size);background:var(--play-bg);}
#play:hover{background:var(--play-hover-bg);}
#vol{display:flex;align-items:center;gap:8px;justify-self:end;grid-column:3;}
#mute{width:var(--mute-size);height:var(--mute-size);border-radius:var(--mute-radius);}
#volume{width:var(--volume-width);}
#volume::-webkit-slider-runnable-track{height:3px;border-radius:1.5px;background:linear-gradient(90deg,var(--volume-fill) var(--p,0%),var(--volume-rest) var(--p,0%));}
#volume::-webkit-slider-thumb{-webkit-appearance:none;width:10px;height:10px;border-radius:50%;background:var(--volume-thumb);margin-top:-3.5px;}
svg{display:block;}
.skip svg{width:var(--skip-icon);height:var(--skip-icon);}
#play svg{width:var(--play-icon);height:var(--play-icon);}
#mute svg{width:var(--volume-icon);height:var(--volume-icon);}
[hidden]{display:none !important;}
.audio body{display:flex;align-items:center;justify-content:center;}
.audio #wrap{display:flex;flex-direction:column;align-items:center;width:min(620px,88vw);}
.audio #art,.audio #tile{width:min(300px,44vh);height:min(300px,44vh);border-radius:16px;box-shadow:0 24px 64px rgba(0,0,0,.55),0 2px 8px rgba(0,0,0,.4);}
.audio #art{object-fit:cover;}
.audio #tile{position:relative;display:flex;align-items:center;justify-content:center;background:linear-gradient(135deg,#262833 0%,#15161c 100%);}
.audio #tile .note{font-size:88px;color:#8f95a0;}
.audio #tile .spark{position:absolute;top:20px;right:26px;font-size:20px;color:#4e80f5;}
.audio #title{margin-top:26px;color:#f4f4f5;font-size:20px;font-weight:600;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.audio #line2{margin-top:6px;color:#a9adb8;font-size:13.5px;max-width:100%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
.video video{width:100%;height:100%;object-fit:contain;background:#000;display:block;}
.video #bar{position:fixed;left:0;right:0;bottom:0;padding:36px 22px 12px;background:linear-gradient(180deg,rgba(0,0,0,0) 0%,rgba(0,0,0,.6) 100%);transition:opacity 200ms ease;}
.video body.idle #bar{opacity:0;}
.video body.idle{cursor:none;}
.video #blocked{position:absolute;left:50%;top:50%;transform:translate(-50%,-50%);padding:10px 14px;border-radius:18px;background:rgba(20,21,25,.86);color:#f4f4f5;font-size:13px;display:none;}
.video body.blocked #blocked{display:block;}
</style>
</head>
<body>
{{bodyHtml}}
<script>
const media = document.getElementById('media');
const isVideo = {{isVideoLiteral}};
const seek = document.getElementById('seek');
const elapsed = document.getElementById('elapsed');
const total = document.getElementById('total');
const playButton = document.getElementById('play');
const icoPlay = document.getElementById('icoPlay');
const icoPause = document.getElementById('icoPause');
const volume = document.getElementById('volume');
const icoVol = document.getElementById('icoVol');
const icoVolX = document.getElementById('icoVolX');
media.volume = {{volume}};
media.muted = {{muted}};
function reportPlayFailure(err) {
  const reason = err && err.name ? err.name : 'play';
  try { window.chrome.webview.postMessage({ error: media.error ? media.error.code : 'play', reason }); } catch { }
}
function playMedia(autoplay) {
  media.play().catch(err => {
    if (err && err.name === 'AbortError') return;
    if (autoplay && err && err.name === 'NotAllowedError') {
      if (isVideo) document.body.classList.add('blocked');
      return;
    }
    reportPlayFailure(err);
  });
}
window.glimpseToggleMedia = function() {
  if (media.paused) {
    playMedia(false);
  } else {
    media.pause();
  }
};
if (isVideo) {
  const start = () => playMedia(true);
  media.addEventListener('canplay', start, { once: true });
  window.addEventListener('load', start);
  media.addEventListener('play', () => document.body.classList.remove('blocked'));
} else {
  playMedia(true);
}
function fmt(s) {
  if (!isFinite(s)) return '--:--';
  s = Math.max(0, Math.round(s));
  const m = Math.floor(s / 60), r = s % 60, h = Math.floor(m / 60);
  return h > 0
    ? h + ':' + String(m % 60).padStart(2, '0') + ':' + String(r).padStart(2, '0')
    : m + ':' + String(r).padStart(2, '0');
}
let scrubbing = false;
function paint() {
  const d = media.duration;
  if (isFinite(d) && d > 0) {
    if (!scrubbing) {
      seek.value = Math.round(media.currentTime / d * 1000);
    }
    seek.style.setProperty('--p', (seek.value / 10) + '%');
    total.textContent = fmt(d);
  }
  elapsed.textContent = fmt(scrubbing && isFinite(d) ? seek.value / 1000 * d : media.currentTime);
}
media.addEventListener('timeupdate', paint);
media.addEventListener('loadedmetadata', paint);
media.addEventListener('durationchange', paint);
seek.addEventListener('input', () => {
  scrubbing = true;
  paint();
});
seek.addEventListener('change', () => {
  const d = media.duration;
  if (isFinite(d)) {
    media.currentTime = seek.value / 1000 * d;
  }
  scrubbing = false;
});
function syncPlay() {
  icoPlay.style.display = media.paused ? 'block' : 'none';
  icoPause.style.display = media.paused ? 'none' : 'block';
}
media.addEventListener('play', syncPlay);
media.addEventListener('pause', syncPlay);
syncPlay();
if (isVideo) {
  media.addEventListener('loadedmetadata', () => {
    try { window.chrome.webview.postMessage({ dims: { w: media.videoWidth || 0, h: media.videoHeight || 0 } }); } catch { }
  }, { once: true });
}
// Keyboard input stays host-owned; the page transport is pointer-only.
playButton.addEventListener('click', window.glimpseToggleMedia);
if (isVideo) media.addEventListener('click', window.glimpseToggleMedia);
document.getElementById('back').addEventListener('click', () => {
  media.currentTime = Math.max(0, media.currentTime - 15);
});
document.getElementById('fwd').addEventListener('click', () => {
  const d = media.duration;
  media.currentTime = isFinite(d) ? Math.min(d, media.currentTime + 15) : media.currentTime + 15;
});
function syncVolume() {
  volume.value = Math.round((media.muted ? 0 : media.volume) * 100);
  volume.style.setProperty('--p', volume.value + '%');
  icoVol.style.display = media.muted ? 'none' : 'block';
  icoVolX.style.display = media.muted ? 'block' : 'none';
}
volume.addEventListener('input', () => {
  media.muted = false;
  media.volume = volume.value / 100;
});
document.getElementById('mute').addEventListener('click', () => {
  media.muted = !media.muted;
});
syncVolume();
if (isVideo) {
  let idleTimer;
  const wake = () => {
    document.body.classList.remove('idle');
    clearTimeout(idleTimer);
    if (!media.paused) {
      idleTimer = setTimeout(() => document.body.classList.add('idle'), 2400);
    }
  };
  window.addEventListener('mousemove', wake);
  media.addEventListener('play', wake);
  media.addEventListener('pause', wake);
  wake();
}
let volumeTimer;
media.addEventListener('volumechange', () => {
  syncVolume();
  clearTimeout(volumeTimer);
  volumeTimer = setTimeout(() => {
    try { window.chrome.webview.postMessage({ volume: media.volume, muted: media.muted }); } catch { }
  }, 250);
});
media.addEventListener('error', () => reportPlayFailure({ name: media.error ? String(media.error.code) : 'decode' }));
</script>
</body>
</html>
""";

        File.WriteAllText(htmlPath, html);
        return new Uri(htmlPath, UriKind.Absolute);
    }

    private static string BuildMediaTransportHtml(string durationText) => $$"""
<div id="scrub">
<div class="time" id="elapsed">0:00</div>
<input id="seek" type="range" min="0" max="1000" value="0">
<div class="time" id="total">{{durationText}}</div>
</div>
<div id="controls">
<div id="buttons">
<button class="skip" id="back" title="Back 15 seconds">
<svg viewBox="0 0 24 24">
<path d="M 7.99 6.72 A 7 7 0 1 1 5.31 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 4.55 6.6 L 8.6 3.7 L 8.7 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">15</text>
</svg>
</button>
<button id="play" title="Play or pause (Space)">
<svg id="icoPlay" viewBox="0 0 24 24">
<path d="M8 4.6 L19 12 L8 19.4 Z" fill="currentColor"/>
</svg>
<svg id="icoPause" style="display:none" viewBox="0 0 24 24">
<rect x="6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
<rect x="13.6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
</svg>
</button>
<button class="skip" id="fwd" title="Forward 15 seconds">
<svg viewBox="0 0 24 24">
<path d="M 16.01 6.72 A 7 7 0 1 0 18.69 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 19.45 6.6 L 15.4 3.7 L 15.3 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">15</text>
</svg>
</button>
</div>
<div id="vol">
<button id="mute" title="Mute">
<svg id="icoVol" viewBox="0 0 24 24">
<path d="M4 9.5 L8 9.5 L13 5 L13 19 L8 14.5 L4 14.5 Z" fill="currentColor"/>
<path d="M16 9 A 4.4 4.4 0 0 1 16 15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M18.4 6.6 A 7.6 7.6 0 0 1 18.4 17.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
</svg>
<svg id="icoVolX" style="display:none" viewBox="0 0 24 24">
<path d="M4 9.5 L8 9.5 L13 5 L13 19 L8 14.5 L4 14.5 Z" fill="currentColor"/>
<path d="M16.4 9.6 L21.2 14.4 M21.2 9.6 L16.4 14.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
</svg>
</button>
<input id="volume" type="range" min="0" max="100" value="0">
</div>
</div>
""";

    /// <summary>
    /// The dead-control rule: an unplayable file never keeps a dead player.
    /// The card names the outcome and points at the exit, using the same
    /// default-app name the header's Open pill shows.
    /// </summary>
    private static string MediaFailureNextStep(string path, Mp4CodecInfo? codecs = null)
    {
        // With a Store action on the card, the subtitle states the fact and
        // lets the pill be the instruction; two competing imperatives read
        // as noise. The header's Open pill still names the app either way.
        if (codecs?.HasHevcVideo == true)
        {
            return "This video uses HEVC.";
        }

        return codecs?.HasAv1Video == true
            ? "This video uses AV1."
            : MediaOpenInsteadText(path);
    }

    private static FrameworkElement BuildMediaFailurePreview(string path, Mp4CodecInfo? codecs)
    {
        return BuildErrorPreview(
            "Glimpse can't play this file",
            MediaFailureNextStep(path, codecs),
            MediaCodecStoreAction(codecs));
    }

    private static FrameworkElement BuildHeifFailurePreview(string path)
    {
        var usesHevc = FileSignatures.HasHevcCodedHeifBrand(path)
            || Path.GetExtension(path).Equals(".heic", StringComparison.OrdinalIgnoreCase);
        var action = usesHevc
            ? new PreviewAction(
                "Get HEVC support",
                "Opens Microsoft Store",
                HevcVideoExtensionStoreUri,
                "Open HEVC Video Extensions in Microsoft Store")
            : new PreviewAction(
                "Get HEIF support",
                "Opens Microsoft Store",
                HeifImageExtensionStoreUri,
                "Open HEIF Image Extensions in Microsoft Store");

        return BuildErrorPreview(
            "Glimpse can't preview this image",
            usesHevc ? "This image uses HEVC." : "This image uses HEIF.",
            action);
    }

    private static PreviewAction? MediaCodecStoreAction(Mp4CodecInfo? codecs)
    {
        if (codecs?.HasHevcVideo == true)
        {
            return new PreviewAction(
                "Get HEVC support",
                "Opens Microsoft Store",
                HevcVideoExtensionStoreUri,
                "Open HEVC Video Extensions in Microsoft Store");
        }

        if (codecs?.HasAv1Video == true)
        {
            return new PreviewAction(
                "Get AV1 support",
                "Opens Microsoft Store",
                Av1VideoExtensionStoreUri,
                "Open AV1 Video Extension in Microsoft Store");
        }

        return null;
    }

    private static string MediaOpenInsteadText(string path)
    {
        var extension = Path.GetExtension(path);
        var app = string.IsNullOrEmpty(extension) ? null : NativeMethods.TryGetFriendlyAppName(extension);
        return string.IsNullOrWhiteSpace(app) || app.Equals("Pick an app", StringComparison.OrdinalIgnoreCase)
            ? "Open it in a media player instead."
            : $"Open it with {app} instead.";
    }
}
