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
    public void HidePreview()
    {
        _rotationSession?.Commit();
        _rotationSession = null;
        CloseGridView();
        StopSlideshow(restoreSize: false);
        Interlocked.Increment(ref _loadVersion);
        _loadCancellation?.Cancel();
        CurrentPath = null;
        _shortcutTarget = null;
        _escapeYieldsToContent = false;
        _lastContentClickInViewerChrome = false;
        _userEngagedContent = false;
        HideLoadingOverlay();
        HideContentSwapScrim();

        if (!IsVisible)
        {
            ClearPreviewContent();
            return;
        }

        var generation = ++_fadeGeneration;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            CompleteHide();
            return;
        }

        AnimateWindowFade(hwnd, from: 255, to: 0, TimeSpan.FromMilliseconds(90), easeOut: false, () =>
        {
            if (generation == _fadeGeneration)
            {
                CompleteHide();
            }
        });
    }

    private void CompleteHide()
    {
        // A maximize (ours or the OS's) dies with the open: the next Space
        // must always open a normal stage. This has to happen while the window
        // still exists on screen. Setting it on a hidden window moves only the
        // property, so the stage would come back maximized while the property
        // and the header button both said otherwise, and the next click on
        // that button would be swallowed reconciling the two. The window is
        // already faded out here, so the restore is not visible.
        WindowState = WindowState.Normal;
        Hide();
        ClearPreviewContent();
        SetBrowseControlsVisible(false);
        EnsureOpaque();
        // The user's dragged size dies with the open; content rules again.
        _itemStageSize = null;
        _recenterStage = false;
        _contentDrivenMaximize = false;
        // Insurance for the paths that reach here already hidden, where the
        // restore above raises no StateChanged to move the glyph.
        UpdateMaximizeChrome();
    }

    // ---------------------------------------------------------------------
    // Entrance/exit animation. The window fades via a temporary layered
    // style (children composite too on Win8+) plus a subtle scale pop, then
    // drops the layered bit so normal rendering has zero overhead.
    // ---------------------------------------------------------------------

    private bool ShouldScaleEntrance() =>
        _stageFitVersion != _loadVersion
        || _stageFitHint is not { Kind: StageFitHint.FitKind.PageAspect };

    private bool ShowWithEntrance(bool useScale = true)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        SetWindowAlpha(hwnd, 0);
        Show();

        if (!TryConfirmNativePresentation(out var failureReason))
        {
            LastPresentationFailureReason = failureReason;
            // A rejected stage was never presented. Hide it while it is still
            // transparent so the controller's cleanup cannot briefly flash a
            // window that Windows refused to place where the user expects it.
            Hide();
            RemoveLayeredStyle(hwnd);
            return false;
        }

        LastPresentationFailureReason = string.Empty;

        AnimateWindowFade(hwnd, from: 0, to: 255, TimeSpan.FromMilliseconds(140), easeOut: true, () =>
        {
            RemoveLayeredStyle(hwnd);
            if (!TryConfirmNativePresentation(out var completionFailure))
            {
                LastPresentationFailureReason = completionFailure;
                PresentationFailed?.Invoke(this, completionFailure);
                return;
            }

            DiagnosticLog.Write(
                $"preview presentation confirmed hwnd=0x{hwnd.ToInt64():X} owner=0x{_placementOwnerHwnd.ToInt64():X}");
        });

        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        if (!useScale)
        {
            RootScale.ScaleX = 1;
            RootScale.ScaleY = 1;
            return true;
        }

        RootScale.ScaleX = 0.965;
        RootScale.ScaleY = 0.965;
        var pop = new DoubleAnimation(0.965, 1.0, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        return true;
    }

    private bool TryConfirmNativePresentation(out string failureReason)
    {
        var hwnd = Hwnd != IntPtr.Zero ? Hwnd : new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !IsVisible || !NativeMethods.IsWindowVisible(hwnd))
        {
            failureReason = "window-not-visible";
            return false;
        }

        var insertAfter = EffectiveTopmost ? new IntPtr(-1) : new IntPtr(-2);
        if (!NativeMethods.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NativeMethods.SWP_NOMOVE
                | NativeMethods.SWP_NOSIZE
                | NativeMethods.SWP_NOACTIVATE))
        {
            failureReason = $"z-order-rejected-{Marshal.GetLastWin32Error()}";
            return false;
        }

        if (EffectiveTopmost
            && _placementOwnerHwnd != IntPtr.Zero
            && NativeMethods.IsWindow(_placementOwnerHwnd)
            && IsWindowAbove(hwnd, _placementOwnerHwnd))
        {
            failureReason = "owner-remained-above-preview";
            return false;
        }

        failureReason = string.Empty;
        return true;
    }

    private static bool IsWindowAbove(IntPtr window, IntPtr candidateAbove)
    {
        var cursor = NativeMethods.GetWindow(window, NativeMethods.GW_HWNDPREV);
        for (var visited = 0; cursor != IntPtr.Zero && visited < 2048; visited++)
        {
            if (cursor == candidateAbove)
            {
                return true;
            }

            cursor = NativeMethods.GetWindow(cursor, NativeMethods.GW_HWNDPREV);
        }

        return false;
    }

    private void AnimateWindowFade(IntPtr hwnd, byte from, byte to, TimeSpan duration, bool easeOut, Action onDone)
    {
        _fadeTimer?.Stop();
        var generation = _fadeGeneration;
        SetWindowAlpha(hwnd, from);

        var stopwatch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _fadeTimer = timer;
        timer.Tick += (_, _) =>
        {
            if (generation != _fadeGeneration)
            {
                timer.Stop();
                return;
            }

            var progress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
            var eased = easeOut ? 1 - (1 - progress) * (1 - progress) : progress * progress;
            var alpha = (byte)Math.Round(from + (to - from) * eased);
            SetWindowAlpha(hwnd, alpha);

            if (progress >= 1)
            {
                timer.Stop();
                onDone();
            }
        };
        timer.Start();
    }

    private void EnsureOpaque()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            return;
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);
        RemoveLayeredStyle(hwnd);
    }

    private static void SetWindowAlpha(IntPtr hwnd, byte alpha)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) == 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | NativeMethods.WS_EX_LAYERED));
        }

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, alpha, NativeMethods.LWA_ALPHA);
    }

    private static void RemoveLayeredStyle(IntPtr hwnd)
    {
        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        if ((exStyle & NativeMethods.WS_EX_LAYERED) != 0)
        {
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle & ~NativeMethods.WS_EX_LAYERED));
        }
    }

    // ---------------------------------------------------------------------
    // Loading overlay: the previous preview stays put while the next one
    // loads; the spinner only appears when a load is actually slow.
    // ---------------------------------------------------------------------

    private async Task ShowLoadingOverlayLaterAsync(long version, int delayMilliseconds, CancellationToken token, (string Title, string Subtitle)? message = null)
    {
        try
        {
            await Task.Delay(delayMilliseconds, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (version != _loadVersion || _loadFinished)
        {
            return;
        }

        if (message is { } copy)
        {
            LoadingTitle.Text = copy.Title;
            LoadingSubtitle.Text = copy.Subtitle;
            LoadingMessagePanel.Visibility = Visibility.Visible;
        }
        else
        {
            LoadingMessagePanel.Visibility = Visibility.Collapsed;
        }

        ShowLoadingOverlayNow();
    }

    private void ShowLoadingOverlayNow()
    {
        // A reveal from the previous load may still be fading this same
        // element. Retake ownership and stop that animation before showing
        // the new load's state; its Completed callback is generation-guarded.
        _loadingOverlayGeneration++;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            LoadingOverlay.Opacity = 0;
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
        else
        {
            LoadingOverlay.Opacity = 1;
        }
        if (ShimmerBand.Visibility == Visibility.Visible)
        {
            return;
        }

        // A faint band of light sweeps the canvas, stays a beat, sweeps
        // again. Alive without machinery, and identical for every file.
        // The band scales with the stage: a fixed sliver that read well on
        // the old capped window disappears on a deck-shaped canvas.
        ShimmerPeak.Color = Theme.Current.IsDark
            ? Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x16, 0x1B, 0x1E, 0x26);
        ShimmerBand.Width = Math.Clamp(ActualWidth * 0.4, 420, 980);
        ShimmerBand.Visibility = Visibility.Visible;

        var travel = Math.Max(ActualWidth, 600) + ShimmerBand.Width;
        var sweep = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(2400),
            RepeatBehavior = RepeatBehavior.Forever
        };
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(
            -ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.Zero)));
        sweep.KeyFrames.Add(new EasingDoubleKeyFrame(
            travel - ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500)),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        sweep.KeyFrames.Add(new DiscreteDoubleKeyFrame(
            -ShimmerBand.Width,
            KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2400))));
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, sweep);
    }

    private void HideLoadingOverlay()
    {
        _loadingOverlayGeneration++;
        LoadingOverlay.BeginAnimation(OpacityProperty, null);
        ShimmerTranslate.BeginAnimation(TranslateTransform.XProperty, null);
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingMessagePanel.Visibility = Visibility.Collapsed;
        ShimmerBand.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The reveal-time exit: the ghost hands off to the real preview with a
    /// short dissolve instead of vanishing between frames.
    /// </summary>
    private void FadeOutLoadingOverlay()
    {
        if (LoadingOverlay.Visibility != Visibility.Visible)
        {
            HideLoadingOverlay();
            return;
        }

        var generation = ++_loadingOverlayGeneration;
        var fade = new DoubleAnimation(LoadingOverlay.Opacity, 0, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            if (generation == _loadingOverlayGeneration)
            {
                HideLoadingOverlay();
            }
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowContentSwapScrim(bool instant = false)
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        if (ContentSwapScrim.Visibility != Visibility.Visible)
        {
            ContentSwapScrim.Opacity = 0;
            ContentSwapScrim.Visibility = Visibility.Visible;
        }

        if (instant)
        {
            ContentSwapScrim.Opacity = 1;
            return;
        }

        ContentSwapScrim.BeginAnimation(OpacityProperty, new DoubleAnimation(ContentSwapScrim.Opacity, 1, TimeSpan.FromMilliseconds(70))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    private void RevealContentSwapScrim(bool animate)
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        if (ContentSwapScrim.Visibility != Visibility.Visible)
        {
            return;
        }

        if (!animate)
        {
            HideContentSwapScrim();
            return;
        }

        var fade = new DoubleAnimation(ContentSwapScrim.Opacity, 0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        fade.Completed += (_, _) =>
        {
            ContentSwapScrim.Visibility = Visibility.Collapsed;
            ContentSwapScrim.Opacity = 0;
        };
        ContentSwapScrim.BeginAnimation(OpacityProperty, fade);
    }

    private void HideContentSwapScrim()
    {
        ContentSwapScrim.BeginAnimation(OpacityProperty, null);
        ContentSwapScrim.Opacity = 0;
        ContentSwapScrim.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Once the outgoing content has finished dissolving (in-page fade for
    /// web content, the scrim for WPF content), take it out of layout
    /// entirely. Geometry changes happen while the canvas is blank, so the
    /// user sees stable states instead of a relayout in motion.
    /// </summary>
    private void CollapseOutgoingContentSoon()
    {
        if (PreviewContent.Content is not UIElement outgoing)
        {
            return;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(110) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Only if it is still the same content: a fast load may have
            // already swapped the incoming preview into this slot.
            if (ReferenceEquals(PreviewContent.Content, outgoing))
            {
                outgoing.Visibility = Visibility.Collapsed;
            }
        };
        timer.Start();
    }

    private void RetargetDefaultStageDuringBlankBeat(long version)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(112) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (version != _loadVersion || !IsVisible || _stageFitVersion == version)
            {
                return;
            }

            RetargetStageToFit();
        };
        timer.Start();
    }

    private static bool UsesDefaultStageWithoutProbe(string path)
    {
        if (Directory.Exists(path))
        {
            return true;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        return !FileKinds.IsAudio(path)
            && !FileKinds.IsImage(path)
            && !FileKinds.IsBrowserImage(path)
            && !FileKinds.IsPhotoshop(path)
            && !FileKinds.IsMedia(path)
            && !FileKinds.IsPdf(path)
            && !FileKinds.IsEpub(path)
            && !FileKinds.IsOffice(path);
    }

    /// <summary>
    /// The scrim cannot cover a webview (the browser hwnd paints over every
    /// WPF sibling), so web content dissolves itself: an in-page fade to the
    /// view's own background color, which the templates and converted pages
    /// keep matched to the app canvas. ExecuteScriptAsync runs even on
    /// hardened script-off pages. A page that refuses keeps its last frame.
    /// </summary>
    private void FadeOutWebContent()
    {
        if (FindVisualChild<WebView2>(PreviewContent) is not { CoreWebView2: { } core })
        {
            return;
        }

        try
        {
            _ = core.ExecuteScriptAsync(
                "document.documentElement.style.transition='opacity 90ms ease-out';"
                + "document.documentElement.style.opacity='0';");
        }
        catch
        {
            // Best-effort only.
        }
    }

    private const string ZoomableContentTag = "zoomable";

    private static readonly TimeSpan HtmlWebPaintReadyFallback = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan PdfPluginSettleDelay = TimeSpan.FromMilliseconds(180);

    private static TimeSpan WebPaintReadyFallback(FrameworkElement content) =>
        content.Tag is PdfPageRailState
            ? PdfPluginSettleDelay
            : HtmlWebPaintReadyFallback;

    /// <summary>
    /// Resolves through the narrowest readiness signal each renderer can give.
    /// Hardened static HTML cannot run page script, so a host script flushes
    /// style and layout while the WebView is hidden, then the host shows the
    /// laid-out page. Interactive pages are primed transparent, cross two
    /// animation frames, and report a nonce through WebMessage. The PDF plugin
    /// gets a short DOM-free settle: outer frames cannot observe its embedded
    /// raster. Every lane is bounded.
    /// </summary>
    private static async Task<bool> WaitForWebPaintReadyAsync(
        WebView2 webView,
        TimeSpan fallback,
        CancellationToken token,
        WebPaintReadinessMode mode)
    {
        if (webView.CoreWebView2 is not { } core)
        {
            return false;
        }

        if (mode == WebPaintReadinessMode.PdfPlugin)
        {
            try
            {
                await Task.Delay(fallback, token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        // One deadline governs event hookup, script dispatch, and frame wait.
        // A slow COM subscription must not grant a late script a fresh window
        // in which it can hide content after the host fallback revealed it.
        var deadlineUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            + Math.Max(1L, (long)Math.Ceiling(fallback.TotalMilliseconds));
        var attached = false;
        EventHandler<CoreWebView2WebMessageReceivedEventArgs>? handler = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(fallback);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (mode == WebPaintReadinessMode.StaticLayout)
            {
                var layoutScript = $$"""
                    (() => {
                      // ExecuteScriptAsync itself is not cancelable. If the
                      // renderer services this command after the host fallback,
                      // do not hide content that has already been revealed.
                      if (Date.now() > {{deadlineUnixMilliseconds}}) return false;

                      const root = document.documentElement;
                      if (!root) return false;

                      // These reads synchronously flush computed style and
                      // layout. The view itself is still hidden, so this lane
                      // never mutates page presentation just to acknowledge it.
                      const computedDisplay = getComputedStyle(root).display;
                      const body = document.body;
                      const rootBounds = root.getBoundingClientRect();
                      const bodyBounds = body ? body.getBoundingClientRect() : null;
                      const layout = [
                        rootBounds.width,
                        rootBounds.height,
                        root.scrollWidth,
                        root.scrollHeight,
                        bodyBounds ? bodyBounds.width : 0,
                        bodyBounds ? bodyBounds.height : 0,
                        body ? body.scrollWidth : 0,
                        body ? body.scrollHeight : 0
                      ];
                      return computedDisplay.length > 0
                        && layout.every(Number.isFinite);
                    })();
                    """;

                var layoutReady = await core.ExecuteScriptAsync(layoutScript).WaitAsync(timeout.Token);
                return string.Equals(layoutReady, "true", StringComparison.Ordinal);
            }

            var signal = $"glimpse-paint-ready:{Guid.NewGuid():N}";
            var paintReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            handler = (_, args) =>
            {
                try
                {
                    if (string.Equals(args.TryGetWebMessageAsString(), signal, StringComparison.Ordinal))
                    {
                        paintReady.TrySetResult(true);
                    }
                }
                catch
                {
                    // Other page messages are objects (media state, dimensions,
                    // errors). They belong to their normal handlers, not this gate.
                }
            };

            // A renderer can exit between the CoreWebView2 lookup and event
            // hookup. Keep subscription inside the protected scope so a
            // disposed COM event source degrades to the bounded fallback.
            core.WebMessageReceived += handler;
            attached = true;
            timeout.Token.ThrowIfCancellationRequested();

            var encodedSignal = JsonSerializer.Serialize(signal);
            var frameScript = $$"""
                (() => {
                  // ExecuteScriptAsync itself is not cancelable. If the
                  // renderer only services this command after the host-side
                  // fallback elapsed, never hide content that is now visible.
                  if (Date.now() > {{deadlineUnixMilliseconds}}) return;

                  const root = document.documentElement;
                  if (root) {
                    root.style.setProperty('opacity', '0', 'important');
                    root.style.setProperty('transition', 'opacity 180ms ease-out');
                  }

                  const report = () => {
                    try { window.chrome.webview.postMessage({{encodedSignal}}); } catch { }
                  };
                  const afterPaint = () => {
                    if (typeof requestAnimationFrame !== 'function') {
                      setTimeout(report, 0);
                      return;
                    }

                    requestAnimationFrame(() => requestAnimationFrame(report));
                  };

                  if (document.readyState === 'loading') {
                    document.addEventListener('DOMContentLoaded', afterPaint, { once: true });
                  } else {
                    afterPaint();
                  }
                })();
                """;

            await core.ExecuteScriptAsync(frameScript).WaitAsync(timeout.Token);
            await paintReady.Task.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            // A hardened/plugin document that refuses the probe still gets the
            // bounded fallback and reveals fully laid out, as before.
            return false;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    core.WebMessageReceived -= handler!;
                }
                catch
                {
                    // The renderer may have exited while the bounded gate waited.
                }
            }
        }
    }

    private static async Task WaitForDocumentRevealGateAsync(FrameworkElement content)
    {
        if (content.Tag is PdfPageRailState state)
        {
            await Task.WhenAny(state.FirstRailReady.Task, Task.Delay(520));
            return;
        }

        if (content.Tag is MediaStageState media)
        {
            if (!media.DimensionsKnown)
            {
                // Metadata for a local file lands well inside the cap; the
                // cap exists so a slow source degrades to the default stage,
                // never to a blank window.
                await Task.WhenAny(media.DimsReady.Task, Task.Delay(600));
            }
        }
    }

    private void SetPreviewContent(FrameworkElement content, IDisposable? disposable, bool animate = true)
    {
        if (!animate || !IsVisible)
        {
            SetPreviewContentNow(content, disposable, animate: false);
            return;
        }

        // The outgoing content spends the first beat of a swap dissolving to
        // the canvas; attaching the successor earlier would rip it out
        // mid-fade.
        var version = _loadVersion;
        RunAfter(_swapStartedAt + TimeSpan.FromMilliseconds(110) - DateTime.UtcNow, () =>
        {
            if (version != _loadVersion || !IsVisible)
            {
                disposable?.Dispose();
                return;
            }

            StagePreviewContent(content, disposable, version);
        });
    }

    /// <summary>
    /// Attaches the new content HIDDEN: a browser view starts its engine,
    /// navigates and lays out its document offstage (a PDF viewer builds
    /// its whole UI in stages - watched live, that is a flash and a resize).
    /// The canvas holds, and the preview reveals in one piece once geometry
    /// has settled and the document has had a beat to paint.
    /// </summary>
    private void StagePreviewContent(FrameworkElement content, IDisposable? disposable, long version)
    {
        // _loadFinished stays false: the load is done but nothing is on
        // screen yet; the reveal marks finished. The timestamp names UI
        // thread stalls: a stage arriving far past its scheduled beat means
        // something blocked the dispatcher mid-swap.
        DiagnosticLog.Write($"stage +{ElapsedSinceSwap():0}ms");

        // Hide the outgoing hwnd in the same tick it is destroyed: a live
        // browser view being disposed paints a black frame on its way out.
        if (PreviewContent.Content is UIElement outgoingContent)
        {
            outgoingContent.Visibility = Visibility.Collapsed;
        }

        // The blank beat: outgoing content is gone, the successor is not
        // attached yet. The stage takes the new item's shape in one step,
        // so the successor lays out exactly once, at its final size.
        RetargetStageToFit();

        ClearPreviewContent();
        PreviewContent.Content = content;
        _currentDisposable = disposable;
        SetBrowseControlsVisible(CurrentPath is not null && !IsPinned);

        var stagedWebView = FindVisualChild<WebView2>(content);

        // A browser view stages Hidden so the document lays out once at its
        // final size. WPF content stages
        // Collapsed instead: Hidden elements still MEASURE at attach, and a
        // 400K-character text preview measuring during a swap freezes the whole
        // choreography. Collapsed defers that layout to the reveal, after
        // geometry has settled - and a file the user scrubs straight past
        // never pays for its layout at all.
        content.Visibility = stagedWebView is not null ? Visibility.Hidden : Visibility.Collapsed;

        var revealed = false;
        bool MayReveal()
        {
            if (revealed || version != _loadVersion || !ReferenceEquals(PreviewContent.Content, content))
            {
                return false;
            }

            revealed = true;
            return true;
        }

        // Documents wait for their first rail decision before reveal. That
        // keeps the PDF surface from appearing, then shifting sideways when
        // thumbnails arrive a beat later.
        if (stagedWebView is { } webView)
        {
            var revealToken = _loadCancellation?.Token ?? CancellationToken.None;

            bool StillOwnsContent() =>
                !revealToken.IsCancellationRequested
                && version == _loadVersion
                && !revealed
                && ReferenceEquals(PreviewContent.Content, content);

            var revealAttemptStarted = false;
            async Task TryRevealWebAsync(bool waitForDocumentGate, string reason)
            {
                // Navigation and the 1.4s escape hatch share one owner. Two
                // probes can both prime opacity=0; if the loser finishes after
                // the winner reveals, it hides the winner again.
                if (!StillOwnsContent() || revealAttemptStarted)
                {
                    return;
                }

                revealAttemptStarted = true;
                if (waitForDocumentGate)
                {
                    await WaitForDocumentRevealGateAsync(content);
                    if (!StillOwnsContent())
                    {
                        return;
                    }

                    DiagnosticLog.Write($"web reveal gate +{ElapsedSinceSwap():0}ms");
                }

                var paintClock = Stopwatch.StartNew();
                var readinessMode = PaintReadinessMode(webView);
                var paintReady = await WaitForWebPaintReadyAsync(
                    webView,
                    WebPaintReadyFallback(content),
                    revealToken,
                    readinessMode);
                if (!StillOwnsContent())
                {
                    return;
                }

                DiagnosticLog.Write(
                    $"web paint gate +{ElapsedSinceSwap():0}ms ready={paintReady} wait={paintClock.Elapsed.TotalMilliseconds:0}ms mode={PaintReadinessModeName(readinessMode)} reason={reason}");
                if (MayReveal())
                {
                    RevealStagedWebContent(content, webView);
                }
            }

            webView.NavigationCompleted += async (_, args) =>
            {
                if (!StillOwnsContent())
                {
                    return;
                }

                DiagnosticLog.Write($"web navigation +{ElapsedSinceSwap():0}ms success={args.IsSuccess}");
                if (!args.IsSuccess)
                {
                    return;
                }

                await TryRevealWebAsync(waitForDocumentGate: true, reason: "navigation");
            };

            // A view that never finishes must not leave a blank window.
            RunAfter(TimeSpan.FromMilliseconds(1400), () =>
            {
                if (StillOwnsContent())
                {
                    _ = TryRevealWebAsync(waitForDocumentGate: false, reason: "timeout");
                }
            });
        }
        else
        {
            RunAfter(TimeSpan.Zero, () =>
            {
                if (!MayReveal())
                {
                    return;
                }

                _loadFinished = true;
                DiagnosticLog.Write($"reveal +{ElapsedSinceSwap():0}ms");
                UpdateRotateButtons();
                content.Visibility = Visibility.Visible;
                content.Opacity = 0;
                content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
                FadeOutLoadingOverlay();
                RevealContentSwapScrim(animate: true);
                ContentReady?.Invoke(this, EventArgs.Empty);
            });
        }
    }

    /// <summary>
    /// Frame-rendered web content dissolves in after its paint gate primes the
    /// page transparent. Static layout content is never page-mutated and simply
    /// becomes visible once its synchronous layout acknowledgement returns.
    /// </summary>
    private void RevealStagedWebContent(FrameworkElement content, WebView2 webView)
    {
        if (!ReferenceEquals(PreviewContent.Content, content))
        {
            return;
        }

        _loadFinished = true;
        DiagnosticLog.Write($"reveal web +{ElapsedSinceSwap():0}ms");
        if (Equals(content.Tag, ZoomableContentTag))
        {
            ShowZoomControls(webView);
        }

        UpdateRotateButtons();
        content.Visibility = Visibility.Visible;

        // The wrapper syncs the browser's bounds lazily while hidden; force
        // the arrange NOW, in the same tick as the show, so the first
        // presented frame cannot appear at stale bounds.
        content.UpdateLayout();
        if (PaintReadinessMode(webView) == WebPaintReadinessMode.AnimationFrames)
        {
            RevealPrimedWebDocument(webView);
        }

        FadeOutLoadingOverlay();
        RevealContentSwapScrim(animate: true);
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    private static void RevealPrimedWebDocument(WebView2 webView)
    {
        try
        {
            if (webView.CoreWebView2 is { } core)
            {
                _ = core.ExecuteScriptAsync(
                    "document.documentElement?.style.setProperty('opacity','1','important');");
            }
        }
        catch
        {
            // Best-effort only.
        }
    }

    private double ElapsedSinceSwap()
    {
        var startedAt = _swapStartedAt == DateTime.MinValue ? _loadStartedAt : _swapStartedAt;
        return startedAt == DateTime.MinValue
            ? -1
            : (DateTime.UtcNow - startedAt).TotalMilliseconds;
    }

    /// <summary>One-shot dispatcher delay; a non-positive delay runs now.</summary>
    private static void RunAfter(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            action();
            return;
        }

        // This is presentation work, not idle work. DispatcherTimer's default
        // Background priority let normal-priority icon/scan continuations hold
        // a ready folder card offscreen for hundreds of milliseconds.
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }

    private void SetPreviewContentNow(FrameworkElement content, IDisposable? disposable, bool animate)
    {
        RetargetStageToFit();
        _loadFinished = true;
        HideLoadingOverlay();
        ClearPreviewContent();
        PreviewContent.Content = content;
        _currentDisposable = disposable;
        SetBrowseControlsVisible(CurrentPath is not null && !IsPinned);

        if (Equals(content.Tag, ZoomableContentTag) && FindVisualChild<WebView2>(content) is { } zoomable)
        {
            ShowZoomControls(zoomable);
        }

        UpdateRotateButtons();

        if (animate && IsVisible)
        {
            content.Opacity = 0;
            content.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }

        RevealContentSwapScrim(animate && IsVisible);
        ContentReady?.Invoke(this, EventArgs.Empty);
    }

    private void ClearPreviewContent()
    {
        ReleaseZoomTarget();
        // Mode state dies with the content; the path does NOT - the staging
        // beat clears the outgoing content AFTER the new item's lane tagged
        // it, and ShowPreview owns the per-item reset.
        _fillSignSurface = null;
        _fillSignParkedContent = null;
        _deferredWebFailure = null;
        SyncHeaderTools();
        _folderHover?.Reset();
        _folderHover = null;
        _activePdfRail = null;
        PreviewContent.Content = null;
        _currentDisposable?.Dispose();
        _currentDisposable = null;
    }
}
