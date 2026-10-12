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
            _ = core.ExecuteScriptAsync("window.glimpseToggleMedia && window.glimpseToggleMedia()");
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

        // Seeking goes through the page so keys and buttons share one step.
        var nudge = vk switch
        {
            NativeMethods.VK_LEFT => "window.glimpseSkip && window.glimpseSkip(-1)",
            NativeMethods.VK_RIGHT => "window.glimpseSkip && window.glimpseSkip(1)",
            NativeMethods.VK_UP => "(m => { if (m) m.volume = Math.min(1, m.volume + 0.05); })(document.getElementById('media'))",
            NativeMethods.VK_DOWN => "(m => { if (m) m.volume = Math.max(0, m.volume - 0.05); })(document.getElementById('media'))",
            _ => null
        };

        if (nudge is null || FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return false;
        }

        try
        {
            _ = core.ExecuteScriptAsync(nudge);
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

    private async Task<FrameworkElement> BuildMediaPreviewAsync(string path, long version, CancellationToken token)
    {
        var settings = Settings.Current;
        var source = await Task.Run(
            () => MediaPlayerPage.Build(path, settings.MediaVolume, settings.MediaMuted),
            token);
        token.ThrowIfCancellationRequested();

        // The page paints its own background (black for video, the canvas
        // for audio) and fades itself in over the stand-in.
        var grid = new Grid { Background = Brushes.Transparent };
        if (!FileKinds.IsAudio(path))
        {
            var dimensionsKnown = _stageFitVersion == version
                && _stageFitHint is { Kind: StageFitHint.FitKind.Pixels };
            grid.Tag = new MediaStageState(dimensionsKnown);
        }
        var webView = CreateWebView(
            WebSurfaceKind.Page,
            () => source,
            $"web media {path}",
            _ =>
            {
                if (IsCurrentContentPath(path))
                {
                    SetPreviewContent(BuildMediaFailurePreview(path, null), null);
                }
            },
            configure: core =>
            {
                core.WebMessageReceived += (_, args) =>
                    OnMediaMessage(path, version, token, args.WebMessageAsJson);

                // The player draws its own controls, but the video context
                // menu's "Show controls" brings back Chromium's fullscreen
                // button, so browser fullscreen still maximizes the stage.
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
        return codecs?.HasHevcVideo == true
            ? "This video uses HEVC."
            : MediaOpenInsteadText(path);
    }

    private static FrameworkElement BuildMediaFailurePreview(string path, Mp4CodecInfo? codecs)
    {
        return BuildErrorPreview(
            "Can't play this file",
            MediaFailureNextStep(path, codecs),
            MediaCodecStoreAction(codecs));
    }

    /// <summary>
    /// A damaged photo is not a missing codec: the Store is offered only when
    /// the file really is HEIF and the decoder did not report bad data.
    /// </summary>
    private static FrameworkElement BuildHeifFailurePreview(HeifFailure failure) => failure switch
    {
        HeifFailure.NeedsHevc => BuildErrorPreview(
            "Can't preview this image",
            "This image uses HEVC.",
            HevcFix),
        HeifFailure.NeedsHeif => BuildErrorPreview(
            "Can't preview this image",
            "This image uses HEIF.",
            HeifFix),
        _ => BuildErrorPreview("Can't preview this image", "This image may be damaged.")
    };

    // One definition per fix: the failure cards offer them, and Settings
    // does too while this PC lacks them.
    internal static readonly PreviewAction HevcFix = new("Get HEVC support", HevcVideoExtensionStoreUri, "Opens HEVC Video Extensions in Microsoft Store");
    internal static readonly PreviewAction HeifFix = new("Get HEIF support", HeifImageExtensionStoreUri, "Opens HEIF Image Extensions in Microsoft Store");
    // HEVC is the one video codec WebView2 borrows from Windows. It has
    // decoded AV1 itself since Edge 121, so no Store page can fix an AV1
    // failure and the card offers none.
    internal static PreviewAction? MediaCodecStoreAction(Mp4CodecInfo? codecs) =>
        codecs?.HasHevcVideo == true ? HevcFix : null;

    // The app is the one the header's Open pill names. A type with no
    // default app has none: Windows answers with its app picker, whose name
    // follows the system's language.
    internal static string MediaOpenInsteadText(string path) =>
        ResolveOpenTarget(path, isDirectory: false).AppName is { } app
            ? $"Open it with {app} instead."
            : "Open it in a media player instead.";
}
