using System.IO;
using SpacePreview.Native;
using SpacePreview.Preview;
using SpacePreview.Utilities;

namespace SpacePreview.Tests;

/// <summary>
/// Common audio formats play directly in WPF through MediaFoundation with zero
/// WebView2 startup cost, while uncommon formats (.ogg, .opus) stay on the
/// web media player.
/// </summary>
[Collection(WpfCollection.Name)]
public sealed class NativeAudioPreviewTests
{
    [Theory]
    [InlineData("song.mp3", true, false)]
    [InlineData("clip.wav", true, false)]
    [InlineData("track.m4a", true, false)]
    [InlineData("track.aac", true, false)]
    [InlineData("lossless.flac", true, false)]
    [InlineData("voice.wma", true, false)]
    [InlineData("voice.ogg", false, true)]
    [InlineData("note.opus", false, true)]
    [InlineData("clip.mp4", false, true)]
    public void NativeAudioFormatsDoNotOpenAsPages(string name, bool nativeAudio, bool opensAsPage)
    {
        Assert.Equal(nativeAudio, FileKinds.UsesNativeAudioPreview(name));
        Assert.Equal(opensAsPage, FileKinds.OpensAsPage(name));
    }

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(5, "0:05")]
    [InlineData(65, "1:05")]
    [InlineData(3661, "1:01:01")]
    public void FormatTimeFormatsSecondsMinutesAndHours(int seconds, string expected) =>
        Assert.Equal(expected, AudioPreviewSurface.FormatTime(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public Task AWavOpensNativelyWithoutStartingTheBrowserAndDisposesOnHide() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var wav = WriteSilentWav(Path.Combine(temp.Path, "take.wav"));
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 900, Height = 650 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(wav, IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Fields.Get<bool>(window, "_loadFinished"))
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for native audio preview.");
                await Task.Delay(10);
            }

            var surface = Assert.IsType<AudioPreviewSurface>(window.PreviewContent.Content);
            Assert.True(window.IsMediaPreview);
            Assert.True(window.IsMediaPlayable);
            Assert.False(Fields.Get<bool>(Fields.Get<object>(window, "_warmBrowser"), "_started"));

            // Wait for WinRT MediaFoundation to actually open the WAV (and verify no fallback occurs).
            var openDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (Fields.Get<TimeSpan>(surface, "_knownDuration") <= TimeSpan.Zero)
            {
                Assert.False(Fields.Get<bool>(surface, "_fallbackFired"), "Native WinRT audio fell back on a valid WAV.");
                Assert.True(DateTime.UtcNow < openDeadline, "WinRT MediaPlayer never opened the WAV.");
                await Task.Delay(25);
            }

            Assert.Same(surface, window.PreviewContent.Content);
            Assert.False(Fields.Get<bool>(surface, "_fallbackFired"));

            Assert.True(window.TryToggleMediaPlayback());
            Assert.False(Fields.Get<bool>(surface, "_isPlaying"));
            Assert.True(window.TryToggleMediaPlayback());
            Assert.True(Fields.Get<bool>(surface, "_isPlaying"));

            window.SetPinned(true);
            Assert.True(window.TryNudgeMediaFromArrow(NativeMethods.VK_RIGHT));
            Assert.True(window.TryNudgeMediaFromArrow(NativeMethods.VK_UP));

            window.HidePreview();
            var hideDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!Fields.Get<bool>(surface, "_disposed"))
            {
                Assert.True(DateTime.UtcNow < hideDeadline, "Native audio surface never disposed on hide.");
                await Task.Delay(10);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task ScrubbingTrackBarDragsWithoutTimerResetAndSeeksRealMp3() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var mp3 = WriteSilentMp3(Path.Combine(temp.Path, "seek.mp3"), frameCount: 200); // ~5.22 s
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(mp3, IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Fields.Get<bool>(window, "_loadFinished"))
            {
                Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for MP3 preview.");
                await Task.Delay(10);
            }

            var surface = Assert.IsType<AudioPreviewSurface>(window.PreviewContent.Content);
            while (Fields.Get<TimeSpan>(surface, "_knownDuration") <= TimeSpan.Zero)
            {
                Assert.True(DateTime.UtcNow < deadline, "WinRT MediaPlayer never opened seek.mp3.");
                await Task.Delay(25);
            }

            window.UpdateLayout();
            var seekBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_seekSlider");
            var player = Fields.Get<Windows.Media.Playback.MediaPlayer>(surface, "_player");
            Assert.True(seekBar.ActualWidth > 100);

            // InputHitTest across the 20px bar must land directly on AccentTrackBar (no Slider/RepeatButton swallowing).
            var origin = seekBar.TransformToAncestor(window).Transform(new System.Windows.Point(0, 0));
            var hit = window.InputHitTest(
                new System.Windows.Point(origin.X + seekBar.ActualWidth * 0.5, origin.Y + seekBar.ActualHeight * 0.5));
            Assert.Same(seekBar, hit);

            // Press at ~50%, hold longer than the 100 ms tick timer, drag to ~75%, release.
            var midX = 7.0 + (seekBar.ActualWidth - 14.0) * 0.50;
            var targetX = 7.0 + (seekBar.ActualWidth - 14.0) * 0.75;
            seekBar.BeginPointerDrag(midX);
            Assert.True(seekBar.IsDragging);
            Assert.True(Fields.Get<bool>(surface, "_isScrubbing"));
            Assert.InRange(seekBar.Value, 480, 520);

            await Task.Delay(180);
            Assert.InRange(seekBar.Value, 480, 520); // Tick timer must not snap back while dragging.

            seekBar.UpdatePointerDrag(targetX);
            Assert.InRange(seekBar.Value, 730, 770);

            seekBar.EndPointerDrag(targetX);
            Assert.False(seekBar.IsDragging);
            Assert.False(Fields.Get<bool>(surface, "_isScrubbing"));

            var expectedSeconds = player.PlaybackSession.NaturalDuration.TotalSeconds * 0.75;
            Assert.InRange(player.PlaybackSession.Position.TotalSeconds, expectedSeconds - 0.35, expectedSeconds + 0.5);
            await Task.Delay(150);
            Assert.InRange(player.PlaybackSession.Position.TotalSeconds, expectedSeconds - 0.25, expectedSeconds + 0.8);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DraggingVolumeTrackBarUnmutesUpdatesMuteGlyphAndAppliesFinalSample() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var wav = WriteSilentWav(Path.Combine(temp.Path, "vol.wav"));
        var originalVol = SpacePreview.Services.Settings.Current.MediaVolume;
        var originalMuted = SpacePreview.Services.Settings.Current.MediaMuted;
        try
        {
            using (var surface = new AudioPreviewSurface(wav, 0.5, savedMuted: true, CancellationToken.None, _ => { }))
            {
                surface.Measure(new System.Windows.Size(640, 480));
                surface.Arrange(new System.Windows.Rect(0, 0, 640, 480));
                surface.UpdateLayout();

                var volBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_volSlider");
                var volGlyph = Fields.Get<System.Windows.Shapes.Path>(surface, "_volGlyph");
                var volMutedGlyph = Fields.Get<System.Windows.Shapes.Path>(surface, "_volMutedGlyph");
                var player = Fields.Get<Windows.Media.Playback.MediaPlayer>(surface, "_player");

                Assert.Equal(System.Windows.Visibility.Collapsed, volGlyph.Visibility);
                Assert.Equal(System.Windows.Visibility.Visible, volMutedGlyph.Visibility);
                Assert.True(player.IsMuted);

                // Press at 20% and release directly at 80% without an intermediate move:
                // final pointer sample must update player volume, not stay at 20%.
                var x20 = 7.0 + (volBar.ActualWidth - 14.0) * 0.20;
                var x80 = 7.0 + (volBar.ActualWidth - 14.0) * 0.80;
                volBar.BeginPointerDrag(x20);
                volBar.EndPointerDrag(x80);

                Assert.False(player.IsMuted);
                Assert.InRange(player.Volume, 0.75, 0.85);
                Assert.Equal(System.Windows.Visibility.Visible, volGlyph.Visibility);
                Assert.Equal(System.Windows.Visibility.Collapsed, volMutedGlyph.Visibility);
                await Task.CompletedTask;
            }
        }
        finally
        {
            SpacePreview.Services.Settings.Update(s => s with { MediaVolume = originalVol, MediaMuted = originalMuted });
        }
    });

    [Fact]
    public Task AnMp3WithPercentAndSpecialCharactersPlaysSelectedSiblingNotSpaceSibling() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        // Create both "literal%20.%23 #é.mp3" (~4.2 s) and its space sibling "literal .%23 #é.mp3" (~0.8 s).
        var target = WriteSilentMp3(Path.Combine(temp.Path, "literal%20.%23 #é.mp3"), frameCount: 160);
        _ = WriteSilentMp3(Path.Combine(temp.Path, "literal .%23 #é.mp3"), frameCount: 30);
        var failures = 0;
        using var surface = new AudioPreviewSurface(target, 0.5, false, CancellationToken.None, _ => Interlocked.Increment(ref failures));
        var player = Fields.Get<Windows.Media.Playback.MediaPlayer>(surface, "_player");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (player.PlaybackSession.Position <= TimeSpan.Zero || player.PlaybackSession.NaturalDuration <= TimeSpan.Zero)
        {
            Assert.Equal(0, Volatile.Read(ref failures));
            Assert.False(Fields.Get<bool>(surface, "_fallbackFired"), "Native WinRT audio fell back on a valid MP3 with %20/# in its name.");
            Assert.True(DateTime.UtcNow < deadline, "WinRT MediaPlayer never advanced playback on the percent-named MP3.");
            await Task.Delay(25);
        }

        Assert.Equal(0, Volatile.Read(ref failures));
        Assert.False(surface.IsRetired);
        Assert.InRange(player.PlaybackSession.NaturalDuration.TotalSeconds, 3.5, 4.8);
    });

    [Fact]
    public Task LongPathMp3OpensPlaysAndSeeksNatively() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var nested = temp.Path;
        while (nested.Length < 275)
        {
            nested = Path.Combine(nested, "nested-audio-folder-segment");
        }

        Directory.CreateDirectory(nested);
        var longPath = Path.Combine(nested, "plain.mp3");
        Assert.True(longPath.Length >= 280, $"Expected >=280 char path, got {longPath.Length}.");
        WriteSilentMp3(longPath, frameCount: 160); // ~4.18 s

        var failures = 0;
        using var surface = new AudioPreviewSurface(longPath, 0.5, false, CancellationToken.None, _ => Interlocked.Increment(ref failures));
        var player = Fields.Get<Windows.Media.Playback.MediaPlayer>(surface, "_player");

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (player.PlaybackSession.Position <= TimeSpan.Zero || !player.PlaybackSession.CanSeek)
        {
            Assert.Equal(0, Volatile.Read(ref failures));
            Assert.True(DateTime.UtcNow < deadline, "WinRT MediaPlayer never played the 280+ char path MP3.");
            await Task.Delay(25);
        }

        var seekBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_seekSlider");
        surface.Measure(new System.Windows.Size(640, 480));
        surface.Arrange(new System.Windows.Rect(0, 0, 640, 480));
        surface.UpdateLayout();

        var midX = 7.0 + (seekBar.ActualWidth - 14.0) * 0.50;
        seekBar.BeginPointerDrag(midX);
        seekBar.EndPointerDrag(midX);

        var expected = player.PlaybackSession.NaturalDuration.TotalSeconds * 0.50;
        Assert.InRange(player.PlaybackSession.Position.TotalSeconds, expected - 0.4, expected + 0.6);
    });

    [Fact]
    public void MediaPlayerPageLiteralFileUriEscapesPercentSequencesLiterally()
    {
        var uri = MediaPlayerPage.LiteralFileUri(@"C:\Music\literal%20.%23 #é.ogg");
        Assert.Equal("file:///C:/Music/literal%2520.%2523%20%23%C3%A9.ogg", uri);

        var unc = MediaPlayerPage.LiteralFileUri(@"\\nas\share\literal%20track.ogg");
        Assert.Equal("file://nas/share/literal%2520track.ogg", unc);
    }

    [Fact]
    public Task WebPlayerPlaysSelectedPercentNamedSiblingNotSpaceSibling() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var selected = WriteSilentWav(Path.Combine(temp.Path, "literal%20.%23 #é.wav"), durationSeconds: 4);
        _ = WriteSilentWav(Path.Combine(temp.Path, "literal .%23 #é.wav"), durationSeconds: 1);

        var pageUri = MediaPlayerPage.Build(selected, savedVolume: 0.5, muted: true);
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.Show();
            var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
            window.PreviewContent.Content = webView;
            await webView.EnsureCoreWebView2Async();
            var navDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            webView.CoreWebView2.NavigationCompleted += (_, e) => navDone.TrySetResult(e.IsSuccess);
            webView.CoreWebView2.Navigate(pageUri.AbsoluteUri);
            Assert.True(await navDone.Task.WaitAsync(TimeSpan.FromSeconds(10)));

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
            double duration = double.NaN;
            while (DateTime.UtcNow < deadline)
            {
                var raw = await webView.CoreWebView2.ExecuteScriptAsync("document.getElementById('media')?.duration ?? NaN");
                if (double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) && d > 0)
                {
                    duration = d;
                    break;
                }

                await Task.Delay(50);
            }

            Assert.InRange(duration, 3.5, 4.5);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task PlaybackFailureFlushesPendingVolumeBeforeInvokingFallback() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var wav = WriteSilentWav(Path.Combine(temp.Path, "flush.wav"));
        var originalVol = SpacePreview.Services.Settings.Current.MediaVolume;
        var originalMuted = SpacePreview.Services.Settings.Current.MediaMuted;
        double observedVolInCallback = -1;
        bool observedMutedInCallback = true;
        try
        {
            SpacePreview.Services.Settings.Update(s => s with { MediaVolume = 0.2, MediaMuted = true });
            using (var surface = new AudioPreviewSurface(
                wav,
                0.2,
                savedMuted: true,
                CancellationToken.None,
                _ =>
                {
                    observedVolInCallback = SpacePreview.Services.Settings.Current.MediaVolume;
                    observedMutedInCallback = SpacePreview.Services.Settings.Current.MediaMuted;
                }))
            {
                surface.AdjustVolume(0.65); // Schedules debounced 250 ms save to 0.85, unmuted.
                var failMethod = typeof(AudioPreviewSurface).GetMethod(
                    "HandlePlaybackFailure",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                failMethod.Invoke(surface, new object?[] { "simulated failure", null });

                Assert.Equal(0.85, observedVolInCallback, 2);
                Assert.False(observedMutedInCallback);
                await Task.CompletedTask;
            }
        }
        finally
        {
            SpacePreview.Services.Settings.Update(s => s with { MediaVolume = originalVol, MediaMuted = originalMuted });
        }
    });

    [Fact]
    public Task TrackBarsExposeSliderAutomationPeerAndRangeValuePattern() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var mp3 = WriteSilentMp3(Path.Combine(temp.Path, "uia.mp3"), frameCount: 160); // ~4.18 s
        var originalVol = SpacePreview.Services.Settings.Current.MediaVolume;
        var originalMuted = SpacePreview.Services.Settings.Current.MediaMuted;
        try
        {
            using (var surface = new AudioPreviewSurface(mp3, 0.4, savedMuted: true, CancellationToken.None, _ => { }))
            {
                var player = Fields.Get<Windows.Media.Playback.MediaPlayer>(surface, "_player");
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (player.PlaybackSession.NaturalDuration <= TimeSpan.Zero || !player.PlaybackSession.CanSeek)
                {
                    Assert.True(DateTime.UtcNow < deadline, "WinRT MediaPlayer never opened uia.mp3.");
                    await Task.Delay(25);
                }

                var seekBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_seekSlider");
                var volBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_volSlider");

                var seekPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(seekBar);
                var volPeer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(volBar);
                Assert.NotNull(seekPeer);
                Assert.NotNull(volPeer);
                Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.Slider, seekPeer!.GetAutomationControlType());
                Assert.Equal(System.Windows.Automation.Peers.AutomationControlType.Slider, volPeer!.GetAutomationControlType());

                var seekRange = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IRangeValueProvider>(
                    seekPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue));
                var volRange = Assert.IsAssignableFrom<System.Windows.Automation.Provider.IRangeValueProvider>(
                    volPeer.GetPattern(System.Windows.Automation.Peers.PatternInterface.RangeValue));

                seekRange.SetValue(500);
                var expectedSeconds = player.PlaybackSession.NaturalDuration.TotalSeconds * 0.5;
                Assert.InRange(player.PlaybackSession.Position.TotalSeconds, expectedSeconds - 0.4, expectedSeconds + 0.6);

                volRange.SetValue(75);
                Assert.False(player.IsMuted);
                Assert.InRange(player.Volume, 0.72, 0.78);
            }
        }
        finally
        {
            SpacePreview.Services.Settings.Update(s => s with { MediaVolume = originalVol, MediaMuted = originalMuted });
        }
    });

    [Fact]
    public Task RoutedMouseEventsScrubTrackBar() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var mp3 = WriteSilentMp3(Path.Combine(temp.Path, "routed.mp3"), frameCount: 160);
        using var surface = new AudioPreviewSurface(mp3, 0.5, false, CancellationToken.None, _ => { });
        surface.Measure(new System.Windows.Size(640, 480));
        surface.Arrange(new System.Windows.Rect(0, 0, 640, 480));
        surface.UpdateLayout();

        var seekBar = Fields.Get<AudioPreviewSurface.AccentTrackBar>(surface, "_seekSlider");
        var down = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeftButtonDownEvent,
            Source = seekBar
        };
        seekBar.RaiseEvent(down);
        Assert.True(down.Handled);
        Assert.True(seekBar.IsDragging);
        Assert.True(Fields.Get<bool>(surface, "_isScrubbing"));

        var up = new System.Windows.Input.MouseButtonEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
        {
            RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent,
            Source = seekBar
        };
        seekBar.RaiseEvent(up);
        Assert.True(up.Handled);
        Assert.False(seekBar.IsDragging);
        Assert.False(Fields.Get<bool>(surface, "_isScrubbing"));
        await Task.CompletedTask;
    });

    [Fact]
    public void CommonFileDialogEngagedMediaRoutesVerticalArrowsToVolumeAndKeepsOwnerPickerNavigation()
    {
        var baseFacts = new SpacePreview.Services.KeyFacts
        {
            Source = SpacePreview.Services.SelectionSource.CommonFileDialog,
            PreviewVisible = true,
            Browsable = false,
            Media = true,
            ContentEngaged = true,
            ContentOwnsKeyboard = false
        };

        var upInPreview = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with
        {
            Target = SpacePreview.Services.KeyTarget.Preview,
            Vk = NativeMethods.VK_UP
        });
        Assert.True(upInPreview.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.NudgeMedia, upInPreview.Action);

        var downInPreview = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with
        {
            Target = SpacePreview.Services.KeyTarget.Preview,
            Vk = NativeMethods.VK_DOWN
        });
        Assert.True(downInPreview.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.NudgeMedia, downInPreview.Action);

        // When the file picker itself holds focus, Up/Down still belongs to the picker list.
        var upInPicker = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with
        {
            Target = SpacePreview.Services.KeyTarget.Owner,
            PickerListFocus = true,
            Vk = NativeMethods.VK_UP
        });
        Assert.False(upInPicker.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.FollowPicker, upInPicker.Action);
    }

    [Fact]
    public Task LateWebDecodeErrorDoesNotReplaceReopenedPlayableTrack() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var wav = WriteSilentWav(Path.Combine(temp.Path, "reopen.wav"));
        var window = new PreviewWindow { Left = -30000, Top = 0, Width = 640, Height = 480 };
        window.MarkGeometryUserPinnedForProbe();
        try
        {
            window.ShowPreview(wav, IntPtr.Zero);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (!Fields.Get<bool>(window, "_loadFinished"))
            {
                Assert.True(DateTime.UtcNow < deadline);
                await Task.Delay(10);
            }

            var liveSurface = Assert.IsType<AudioPreviewSurface>(window.PreviewContent.Content);
            var currentVersion = Fields.Get<long>(window, "_loadVersion");
            var staleVersion = currentVersion - 1;

            var method = typeof(PreviewWindow).GetMethod(
                "ShowMediaFailureAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var task = (Task)method.Invoke(window, new object?[] { wav, staleVersion, CancellationToken.None, "4", "stale" })!;
            await task;

            // Live reopened surface must NOT be replaced by a failure card from a prior load version.
            Assert.Same(liveSurface, window.PreviewContent.Content);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task CorruptAudioTriggersFallbackAtMostOnce() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var badMp3 = temp.CreateFile("broken.mp3", "not a real mp3 stream");
        var failures = 0;
        using var surface = new AudioPreviewSurface(badMp3, 0.5, false, CancellationToken.None, _ => Interlocked.Increment(ref failures));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (Volatile.Read(ref failures) == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "Corrupt MP3 never raised MediaFailed.");
            await Task.Delay(25);
        }

        await Task.Delay(200);
        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.True(surface.IsRetired);
    });

    [Fact]
    public void UnpinnedNativeAudioClaimsVerticalArrowsForVolumeAndKeepsHorizontalBrowsing()
    {
        // Native WPF audio has no child WebView2 HWND, so ContentOwnsKeyboard is false.
        var baseFacts = new SpacePreview.Services.KeyFacts
        {
            Target = SpacePreview.Services.KeyTarget.Preview,
            PreviewVisible = true,
            Browsable = true,
            Media = true,
            ContentEngaged = true,
            ContentOwnsKeyboard = false
        };

        var up = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with { Vk = NativeMethods.VK_UP });
        Assert.True(up.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.NudgeMedia, up.Action);

        var down = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with { Vk = NativeMethods.VK_DOWN });
        Assert.True(down.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.NudgeMedia, down.Action);

        var right = SpacePreview.Services.PreviewKeyMap.Route(baseFacts with { Vk = NativeMethods.VK_RIGHT });
        Assert.True(right.Swallow);
        Assert.Equal(SpacePreview.Services.KeyAction.Browse, right.Action);
    }

    [Fact]
    public Task RetirePlaybackStopsAudioAndFlushesPendingVolumeSynchronously() => WpfApp.RunAsync(async () =>
    {
        using var temp = new TempDirectory();
        var wav = WriteSilentWav(Path.Combine(temp.Path, "retire.wav"));
        var originalVol = SpacePreview.Services.Settings.Current.MediaVolume;
        var originalMuted = SpacePreview.Services.Settings.Current.MediaMuted;
        using var surface = new AudioPreviewSurface(wav, 0.5, false, CancellationToken.None, _ => { });
        try
        {
            Assert.True(Fields.Get<bool>(surface, "_isPlaying"));
            surface.AdjustVolume(0.25); // Schedules debounced volume save (250 ms)
            surface.RetirePlayback();

            Assert.True(surface.IsRetired);
            Assert.False(Fields.Get<bool>(surface, "_isPlaying"));
            Assert.Equal(0.75, SpacePreview.Services.Settings.Current.MediaVolume, 2);

            // Retirement is terminal: Space / TogglePlayback cannot restart a retired track.
            surface.TogglePlayback();
            Assert.False(Fields.Get<bool>(surface, "_isPlaying"));
            await Task.CompletedTask;
        }
        finally
        {
            SpacePreview.Services.Settings.Update(s => s with { MediaVolume = originalVol, MediaMuted = originalMuted });
        }
    });

    private static string WriteSilentWav(string path, double durationSeconds = 0.5)
    {
        const int sampleRate = 8000;
        var samples = new byte[(int)Math.Round(sampleRate * durationSeconds)];
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate);
        writer.Write((short)1);
        writer.Write((short)8);
        writer.Write("data"u8);
        writer.Write(samples.Length);
        writer.Write(samples);
        return path;
    }

    private static string WriteSilentMp3(string path, int frameCount = 20)
    {
        // MPEG-1 Layer III, 128 kbps, 44.1 kHz, joint stereo, no CRC, no padding -> 417 bytes/frame (~26.12 ms).
        const int frameSize = 417;
        var frame = new byte[frameSize];
        frame[0] = 0xFF;
        frame[1] = 0xFB;
        frame[2] = 0x90;
        frame[3] = 0x64;
        using var fs = File.Create(path);
        for (var i = 0; i < frameCount; i++)
        {
            fs.Write(frame, 0, frame.Length);
        }

        return path;
    }
}
