using System.Globalization;
using System.IO;
using SpacePreview.Native;
using SpacePreview.Utilities;

namespace SpacePreview.Preview;

/// <summary>
/// The audio and video player. One page per kind and theme is cached; the
/// file, its tags, its artwork and the saved volume travel in the query
/// string, so neither a new track nor a volume change rewrites the page.
/// The page reads them with textContent and attributes, never as markup.
/// </summary>
internal static class MediaPlayerPage
{
    /// <summary>One skip step, for the on-screen buttons and the pinned player's arrow keys.</summary>
    public const int SkipSeconds = 10;

    /// <summary>
    /// The player's address for a file, opening at the saved volume. Shell
    /// metadata and artwork are slow first reads, so this runs on the thread
    /// pool, never on the UI thread.
    /// </summary>
    public static Uri Build(string mediaPath, ThemePalette palette, double savedVolume, bool muted)
    {
        var isAudio = FileKinds.IsAudio(mediaPath);
        var pagePath = HtmlCache.Get(
            "MediaHtml",
            $"player|{(isAudio ? "audio" : "video")}|{(palette.IsDark ? "dark" : "light")}",
            () => Page(isAudio, palette))!;

        // Settings are user-editable; an out-of-range value must not abort the page.
        var volume = double.IsFinite(savedVolume) ? Math.Clamp(savedVolume, 0, 1) : 0.75;
        var query = new List<string>
        {
            Pair("src", new Uri(mediaPath, UriKind.Absolute).AbsoluteUri),
            Pair("volume", volume.ToString("0.###", CultureInfo.InvariantCulture)),
            Pair("muted", muted ? "1" : "0")
        };

        if (isAudio)
        {
            ShellMediaProperties.TryGetAudioMetadata(mediaPath, out var tags);
            query.Add(Pair("title", tags.Title));
            query.Add(Pair("artist", tags.DisplayArtist));
            query.Add(Pair("album", tags.AlbumTitle));
            query.Add(Pair("duration", tags.Duration?.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)));
            var stamp = File.GetLastWriteTimeUtc(mediaPath).Ticks;
            query.Add(Pair("art", ShellThumbnailProvider.TryWriteThumbnailPng(mediaPath, $"{mediaPath}|{stamp}")?.AbsoluteUri));
        }

        return new UriBuilder(new Uri(pagePath, UriKind.Absolute))
        {
            Query = string.Join('&', query.Where(pair => pair.Length > 0))
        }.Uri;
    }

    private static string Argb(System.Windows.Media.Color color) =>
        GeneratedHtmlTheme.Rgba(color, color.A / 255.0);

    private static string Pair(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : $"{name}={Uri.EscapeDataString(value.Trim())}";

    internal static string Page(bool isAudio, ThemePalette p)
    {
        // Video always plays on black under the fixed dark HUD; audio wears the theme.
        var controls = isAudio
            ? """
              --ctl-text: var(--text);
              --ctl-text-2: var(--text-2);
              --ctl-text-3: var(--text-3);
              --ctl-rest: var(--raised-border);
              --ctl-hover: var(--hover);
              --ctl-pressed: var(--pressed);
              --ctl-raised: var(--raised);
              --ctl-ring: var(--raised-border);
              --ctl-scrim: transparent;
              """
            : $$"""
              color-scheme: dark;
              --ctl-text: {{GeneratedHtmlTheme.Hex(Theme.OverlayText)}};
              --ctl-text-2: {{GeneratedHtmlTheme.Hex(Theme.OverlayTextSecondary)}};
              --ctl-text-3: {{GeneratedHtmlTheme.Hex(Theme.OverlayTextTertiary)}};
              --ctl-rest: {{Argb(Theme.OverlayBorder)}};
              --ctl-hover: {{Argb(Theme.Dark.HoverBg)}};
              --ctl-pressed: {{Argb(Theme.Dark.PressedBg)}};
              --ctl-raised: {{Argb(Theme.OverlayScrim)}};
              --ctl-ring: {{Argb(Theme.OverlayBorder)}};
              --ctl-scrim: {{Argb(Theme.OverlayScrim)}};
              """;

        var body = isAudio
            ? $$"""
<div id="wrap">
<div id="cover">
<img id="art" alt="" hidden>
<div id="tile">{{NoteGlyph}}</div>
</div>
<div id="title" hidden></div>
<div id="line2" hidden></div>
{{Transport}}
</div>
<audio id="media" autoplay></audio>
"""
            : $$"""
<video id="media" autoplay playsinline disablepictureinpicture></video>
<div id="bar">
{{Transport}}
</div>
""";

        return $$"""
<!doctype html>
<html class="{{(isAudio ? "audio" : "video")}}">
<head>
<meta charset="utf-8">
<style>
{{GeneratedHtmlTheme.PageCss(p)}}
:root {
{{controls}}
}
html, body { margin: 0; width: 100%; height: 100%; overflow: hidden; user-select: none; }
html.video { background: #000; }
body { font-size: 13px; }
[hidden] { display: none !important; }
svg { display: block; }

#scrub { display: flex; align-items: center; gap: 12px; width: 100%; }
.time { min-width: 40px; color: var(--ctl-text-3); font-size: 11.5px; font-variant-numeric: tabular-nums; }
#elapsed { text-align: right; }

input[type=range] { -webkit-appearance: none; appearance: none; height: 20px; margin: 0; background: transparent; }
input[type=range]::-webkit-slider-runnable-track {
  height: 4px;
  border-radius: 2px;
  background: linear-gradient(90deg, var(--fill) var(--p, 0%), var(--ctl-rest) var(--p, 0%));
}
input[type=range]::-webkit-slider-thumb {
  -webkit-appearance: none;
  width: 14px;
  height: 14px;
  margin-top: -5px;
  border-radius: 50%;
  background: radial-gradient(circle, var(--fill) 0 3px, var(--ctl-raised) 3.5px);
  box-shadow: 0 0 0 1px var(--ctl-ring);
}
input[type=range]:hover::-webkit-slider-thumb {
  background: radial-gradient(circle, var(--fill) 0 4px, var(--ctl-raised) 4.5px);
}
#seek { flex: 1; --fill: var(--accent); }
#volume { width: 88px; --fill: var(--ctl-text-2); }

#controls { display: grid; grid-template-columns: 1fr auto 1fr; align-items: center; width: 100%; margin-top: 8px; }
#buttons { display: flex; align-items: center; gap: 16px; grid-column: 2; }
#vol { display: flex; align-items: center; gap: 4px; justify-self: end; grid-column: 3; }

button {
  all: unset;
  display: flex;
  align-items: center;
  justify-content: center;
  border-radius: 50%;
  color: var(--ctl-text-2);
}
button:hover { background: var(--ctl-hover); color: var(--ctl-text); }
button:active { background: var(--ctl-pressed); }
.skip { width: 40px; height: 40px; }
.skip svg { width: 24px; height: 24px; }
#play {
  width: 52px;
  height: 52px;
  color: var(--ctl-text);
  background: var(--ctl-raised);
  box-shadow: inset 0 0 0 1px var(--ctl-ring);
}
#play:hover { background: linear-gradient(var(--ctl-hover), var(--ctl-hover)), var(--ctl-raised); }
#play:active { background: linear-gradient(var(--ctl-pressed), var(--ctl-pressed)), var(--ctl-raised); }
#play svg { width: 22px; height: 22px; }
#mute { width: 30px; height: 30px; border-radius: 6px; }
#mute svg { width: 18px; height: 18px; }

/* Audio: the track's cover, its tags, the transport, on the canvas. */
.audio body { display: flex; align-items: center; justify-content: center; }
.audio #wrap { display: flex; flex-direction: column; align-items: center; width: min(560px, 86vw); }
.audio #cover { width: min(208px, 40vh); height: min(208px, 40vh); margin-bottom: 24px; }
.audio #art, .audio #tile { width: 100%; height: 100%; border-radius: 12px; }
.audio #art { object-fit: cover; box-shadow: 0 0 0 1px var(--border); }
.audio #tile {
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--raised);
  box-shadow: inset 0 0 0 1px var(--raised-border);
  color: var(--text-3);
}
.audio #tile svg { width: 33%; height: 33%; }
.audio #title, .audio #line2 { max-width: 100%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.audio #title { color: var(--text); font-family: var(--font-display); font-size: 20px; font-weight: 600; line-height: 28px; }
.audio #line2 { margin-top: 2px; color: var(--text-2); font-size: 13px; line-height: 20px; }
.audio #scrub { margin-top: 20px; }

/* Video: the HUD rests at the bottom and steps aside while it plays. */
.video video { display: block; width: 100%; height: 100%; object-fit: contain; background: #000; }
.video #bar {
  position: fixed;
  left: 0;
  right: 0;
  bottom: 0;
  padding: 36px 20px 12px;
  background: linear-gradient(180deg, transparent 0%, var(--ctl-scrim) 100%);
  transition: opacity 200ms ease;
}
.video .time { color: var(--ctl-text-2); }
.video #controls { margin-top: 2px; }
.video #play { width: 48px; height: 48px; }
.video body.idle #bar { opacity: 0; }
.video body.idle { cursor: none; }
@media (prefers-reduced-motion: reduce) {
  .video #bar { transition: none; }
}
</style>
</head>
<body>
{{body}}
<script>
const SKIP = {{SkipSeconds}};
const query = new URLSearchParams(location.search);
const media = document.getElementById('media');
const isVideo = media.tagName === 'VIDEO';
const seek = document.getElementById('seek');
const elapsed = document.getElementById('elapsed');
const total = document.getElementById('total');
const volume = document.getElementById('volume');

function fmt(s) {
  if (!isFinite(s)) return '--:--';
  s = Math.max(0, Math.round(s));
  const m = Math.floor(s / 60), r = s % 60, h = Math.floor(m / 60);
  return h > 0
    ? h + ':' + String(m % 60).padStart(2, '0') + ':' + String(r).padStart(2, '0')
    : m + ':' + String(r).padStart(2, '0');
}

function post(message) {
  try { window.chrome.webview.postMessage(message); } catch { }
}

// Query values are text and URLs; they never reach markup.
function showText(id, value) {
  const element = document.getElementById(id);
  element.textContent = value || '';
  element.hidden = !value;
}

const savedVolume = parseFloat(query.get('volume'));
media.volume = isFinite(savedVolume) ? Math.min(1, Math.max(0, savedVolume)) : 0.75;
media.muted = query.get('muted') === '1';
if (!isVideo) {
  showText('title', query.get('title'));
  showText('line2', [query.get('artist'), query.get('album')].filter(Boolean).join(' · '));
  const art = document.getElementById('art');
  const tile = document.getElementById('tile');
  const artSource = query.get('art');
  if (artSource) {
    art.addEventListener('error', () => { art.hidden = true; tile.hidden = false; });
    art.src = artSource;
    art.hidden = false;
    tile.hidden = true;
  }

  const knownDuration = parseFloat(query.get('duration'));
  if (isFinite(knownDuration)) total.textContent = fmt(knownDuration);
}

// The autoplay attribute starts playback; a file the engine cannot play
// surfaces here, once, and the host swaps in its failure card.
media.addEventListener('error', () => post({ error: media.error ? media.error.code : 'decode' }));
media.src = query.get('src') || '';

window.glimpseToggleMedia = () => {
  if (media.paused) media.play().catch(() => { });
  else media.pause();
};

window.glimpseSkip = direction => {
  const d = media.duration;
  const next = media.currentTime + direction * SKIP;
  media.currentTime = Math.max(0, isFinite(d) ? Math.min(d, next) : next);
};

let scrubbing = false;
function paint() {
  const d = media.duration;
  if (isFinite(d) && d > 0) {
    if (!scrubbing) seek.value = Math.round(media.currentTime / d * 1000);
    seek.style.setProperty('--p', (seek.value / 10) + '%');
    total.textContent = fmt(d);
  }
  elapsed.textContent = fmt(scrubbing && isFinite(d) ? seek.value / 1000 * d : media.currentTime);
}
media.addEventListener('timeupdate', paint);
media.addEventListener('loadedmetadata', paint);
media.addEventListener('durationchange', paint);
seek.addEventListener('input', () => { scrubbing = true; paint(); });
seek.addEventListener('change', () => {
  const d = media.duration;
  if (isFinite(d)) media.currentTime = seek.value / 1000 * d;
  scrubbing = false;
});

// SVG elements take the hidden attribute, not the HTML property.
function syncPlay() {
  document.getElementById('icoPlay').toggleAttribute('hidden', !media.paused);
  document.getElementById('icoPause').toggleAttribute('hidden', media.paused);
}
media.addEventListener('play', syncPlay);
media.addEventListener('pause', syncPlay);
syncPlay();

// Keyboard input stays host-owned; the page transport is pointer-only.
document.getElementById('play').addEventListener('click', window.glimpseToggleMedia);
document.getElementById('back').addEventListener('click', () => window.glimpseSkip(-1));
document.getElementById('fwd').addEventListener('click', () => window.glimpseSkip(1));
document.getElementById('mute').addEventListener('click', () => { media.muted = !media.muted; });

function syncVolume() {
  volume.value = Math.round((media.muted ? 0 : media.volume) * 100);
  volume.style.setProperty('--p', volume.value + '%');
  document.getElementById('icoVol').toggleAttribute('hidden', media.muted);
  document.getElementById('icoVolX').toggleAttribute('hidden', !media.muted);
}
volume.addEventListener('input', () => {
  media.muted = false;
  media.volume = volume.value / 100;
});
syncVolume();

// The player reports volume moves; the host saves them for the next file.
let volumeTimer;
media.addEventListener('volumechange', () => {
  syncVolume();
  clearTimeout(volumeTimer);
  volumeTimer = setTimeout(() => post({ volume: media.volume, muted: media.muted }), 250);
});

if (isVideo) {
  media.addEventListener('click', window.glimpseToggleMedia);
  media.addEventListener('loadedmetadata', () => {
    post({ dims: { w: media.videoWidth || 0, h: media.videoHeight || 0 } });
  }, { once: true });

  let idleTimer;
  const wake = () => {
    document.body.classList.remove('idle');
    clearTimeout(idleTimer);
    if (!media.paused) idleTimer = setTimeout(() => document.body.classList.add('idle'), 2400);
  };
  window.addEventListener('mousemove', wake);
  media.addEventListener('play', wake);
  media.addEventListener('pause', wake);
  wake();
}
</script>
</body>
</html>
""";
    }

    // A quiet stand-in for missing cover art: one eighth note.
    private const string NoteGlyph = """
<svg viewBox="0 0 24 24" aria-hidden="true">
<path d="M9.5 17.5V6.2l9-1.7v11" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linejoin="round"/>
<ellipse cx="7" cy="17.5" rx="2.5" ry="2" fill="currentColor"/>
<ellipse cx="16" cy="15.5" rx="2.5" ry="2" fill="currentColor"/>
</svg>
""";

    private static readonly string Transport = $$"""
<div id="scrub">
<div class="time" id="elapsed">0:00</div>
<input id="seek" type="range" min="0" max="1000" value="0" aria-label="Position">
<div class="time" id="total">--:--</div>
</div>
<div id="controls">
<div id="buttons">
<button class="skip" id="back" title="Back {{SkipSeconds}} seconds">
<svg viewBox="0 0 24 24">
<path d="M 7.99 6.72 A 7 7 0 1 1 5.31 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 4.55 6.6 L 8.6 3.7 L 8.7 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">{{SkipSeconds}}</text>
</svg>
</button>
<button id="play" title="Play or pause (Space)">
<svg id="icoPlay" viewBox="0 0 24 24">
<path d="M8 4.6 L19 12 L8 19.4 Z" fill="currentColor"/>
</svg>
<svg id="icoPause" viewBox="0 0 24 24" hidden>
<rect x="6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
<rect x="13.6" y="4.6" width="4.4" height="14.8" rx="1.4" fill="currentColor"/>
</svg>
</button>
<button class="skip" id="fwd" title="Forward {{SkipSeconds}} seconds">
<svg viewBox="0 0 24 24">
<path d="M 16.01 6.72 A 7 7 0 1 0 18.69 10.40" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
<path d="M 19.45 6.6 L 15.4 3.7 L 15.3 9.2 Z" fill="currentColor"/>
<text x="12" y="12.45" text-anchor="middle" dominant-baseline="central" font-size="7.1" font-weight="700" fill="currentColor">{{SkipSeconds}}</text>
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
<svg id="icoVolX" viewBox="0 0 24 24" hidden>
<path d="M4 9.5 L8 9.5 L13 5 L13 19 L8 14.5 L4 14.5 Z" fill="currentColor"/>
<path d="M16.4 9.6 L21.2 14.4 M21.2 9.6 L16.4 14.4" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round"/>
</svg>
</button>
<input id="volume" type="range" min="0" max="100" value="0" aria-label="Volume">
</div>
</div>
""";
}
