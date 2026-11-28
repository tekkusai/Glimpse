using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SpacePreview.Native;
using SpacePreview.Services;
using SpacePreview.Utilities;
using Windows.Media.Core;
using Windows.Storage.Streams;
using WinRtMediaPlayer = Windows.Media.Playback.MediaPlayer;
using Path = System.Windows.Shapes.Path;

namespace SpacePreview.Preview;

/// <summary>
/// Native WPF audio preview surface matching docs/DESIGN.md. Plays common
/// Windows-supported audio formats (.wav, .mp3, .m4a, .aac, .flac, .wma)
/// directly through WinRT Windows Media Foundation (no legacy Windows Media
/// Player dependency, zero WebView2 startup cost), and populates shell tags
/// and cover art asynchronously after first paint.
/// </summary>
internal sealed class AudioPreviewSurface : Grid, IDisposable
{
    private readonly string _path;
    private readonly CancellationToken _token;
    private readonly Action<Exception?> _onFailed;
    private readonly WinRtMediaPlayer _player;
    private MediaSource? _mediaSource;
    private FileStream? _mediaFileStream;
    private IRandomAccessStream? _mediaRandomAccessStream;
    private readonly DispatcherTimer _tickTimer;
    private readonly DispatcherTimer _volumeSaveTimer;

    private readonly Border _artBorder;
    private readonly Image _artImage;
    private readonly Viewbox _noteGlyph;
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _subtitleBlock;
    private readonly TextBlock _elapsedBlock;
    private readonly TextBlock _totalBlock;
    private readonly AccentTrackBar _seekSlider;
    private readonly Path _playGlyph;
    private readonly Grid _pauseGlyph;
    private readonly Path _volGlyph;
    private readonly Path _volMutedGlyph;
    private readonly AccentTrackBar _volSlider;

    private volatile bool _disposed;
    private volatile bool _retired;
    private bool _fallbackFired;
    private bool _isPlaying;

    public bool IsRetired => _retired || _disposed;
    private bool _isMuted;
    private bool _isScrubbing;
    private double _volume;
    private TimeSpan _knownDuration;
    private TimeSpan _pendingSeekTarget;
    private long _pendingSeekUntilTicks;

    public AudioPreviewSurface(
        string path,
        double savedVolume,
        bool savedMuted,
        CancellationToken token,
        Action<Exception?> onFailed)
    {
        _path = path;
        _token = token;
        _onFailed = onFailed;
        _volume = double.IsFinite(savedVolume) ? Math.Clamp(savedVolume, 0, 1) : 0.75;
        _isMuted = savedMuted;

        this.Themed(BackgroundProperty, "Theme.CanvasBg");

        _tickTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _tickTimer.Tick += (_, _) => SyncTimeline();

        _volumeSaveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            PersistVolume();
        };

        // Cover art / eighth-note tile (208x208, 12px radius)
        _artImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_artImage, BitmapScalingMode.HighQuality);

        _noteGlyph = new Viewbox
        {
            Width = 68,
            Height = 68,
            Child = BuildNoteIcon()
        };

        var coverHost = new Grid();
        coverHost.Children.Add(_noteGlyph);
        coverHost.Children.Add(_artImage);

        _artBorder = new Border
        {
            Width = 208,
            Height = 208,
            CornerRadius = Metrics.CardCorner,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 24),
            ClipToBounds = true,
            Child = coverHost
        }.Themed(Border.BackgroundProperty, "Theme.RaisedBg")
         .Themed(Border.BorderBrushProperty, "Theme.RaisedBorder");
        _artBorder.SizeChanged += (_, _) =>
            _artImage.Clip = new RectangleGeometry(
                new Rect(0, 0, Math.Max(0, _artBorder.ActualWidth - 2), Math.Max(0, _artBorder.ActualHeight - 2)),
                Metrics.RadiusCard - 1,
                Metrics.RadiusCard - 1);

        // Title & artist/album lines (hidden until metadata arrives)
        _titleBlock = new TextBlock
        {
            FontSize = Metrics.FontTitle,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextPrimary");

        _subtitleBlock = new TextBlock
        {
            FontSize = Metrics.FontBody,
            Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Collapsed
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary");

        // Scrub bar row
        _elapsedBlock = BuildTimeText("0:00", TextAlignment.Right);
        _totalBlock = BuildTimeText("--:--", TextAlignment.Left);
        _seekSlider = new AccentTrackBar(0, 1000, 0, "Position", smallChange: 10, largeChange: 100);
        _seekSlider.Margin = new Thickness(12, 0, 12, 0);
        _seekSlider.DragStarted += () =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            _isScrubbing = true;
            _pendingSeekUntilTicks = 0;
        };
        _seekSlider.UserValueChanged += val =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            if (EffectiveDuration() is { TotalSeconds: > 0 } dur)
            {
                _elapsedBlock.Text = FormatTime(TimeSpan.FromSeconds(Math.Clamp(val / 1000.0, 0, 1) * dur.TotalSeconds));
            }
        };
        _seekSlider.DragCompleted += CommitScrub;

        var scrubGrid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        scrubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        scrubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        scrubGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_elapsedBlock, 0);
        Grid.SetColumn(_seekSlider, 1);
        Grid.SetColumn(_totalBlock, 2);
        scrubGrid.Children.Add(_elapsedBlock);
        scrubGrid.Children.Add(_seekSlider);
        scrubGrid.Children.Add(_totalBlock);

        // Transport controls row
        var backBtn = BuildTransportButton(40, false, $"Back {MediaPlayerPage.SkipSeconds} seconds", BuildSkipIcon(forward: false), () => Skip(-1));
        (_playGlyph, _pauseGlyph) = BuildPlayPauseIcons();
        var playIconHost = new Grid { Children = { _playGlyph, _pauseGlyph } };
        var playBtn = BuildTransportButton(52, true, "Play or pause (Space)", playIconHost, TogglePlayback);
        playBtn.Margin = new Thickness(16, 0, 16, 0);
        var fwdBtn = BuildTransportButton(40, false, $"Forward {MediaPlayerPage.SkipSeconds} seconds", BuildSkipIcon(forward: true), () => Skip(1));

        var centerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { backBtn, playBtn, fwdBtn }
        };

        (_volGlyph, _volMutedGlyph) = BuildVolumeIcons();
        var volIconHost = new Grid { Children = { _volGlyph, _volMutedGlyph } };
        var muteBtn = BuildMuteButton(volIconHost, ToggleMute);

        _volSlider = new AccentTrackBar(0, 100, _isMuted ? 0 : Math.Round(_volume * 100), "Volume", smallChange: 5, largeChange: 10);
        _volSlider.Width = 88;
        _volSlider.Margin = new Thickness(4, 0, 0, 0);
        _volSlider.UserValueChanged += val =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            _isMuted = false;
            _volume = Math.Clamp(val / 100.0, 0, 1);
            ApplyPlayerVolume();
            SyncMuteGlyph();
            ScheduleVolumeSave();
        };

        var volPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { muteBtn, _volSlider }
        };

        var controlsGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controlsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(centerButtons, 1);
        Grid.SetColumn(volPanel, 2);
        controlsGrid.Children.Add(centerButtons);
        controlsGrid.Children.Add(volPanel);

        var wrap = new StackPanel
        {
            MaxWidth = 560,
            Margin = new Thickness(32, 0, 32, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { _artBorder, _titleBlock, _subtitleBlock, scrubGrid, controlsGrid }
        };
        Children.Add(wrap);

        // Initialize WinRT MediaPlayer inside guarded setup so Windows N (missing
        // Media Feature Pack) or activation failures dispose any partial instance
        // and let BuildMediaPreviewAsync fall straight through to the web player.
        WinRtMediaPlayer? player = null;
        try
        {
            player = new WinRtMediaPlayer();
            try
            {
                // Do not hijack system media keys (SMTC) for a transient preview.
                player.CommandManager.IsEnabled = false;
            }
            catch
            {
                // Ignore if SMTC command manager is unavailable.
            }

            // Wire WinRT MediaPlayer events (raised on background threads -> marshal to Dispatcher).
            player.MediaOpened += OnMediaOpened;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            player.PlaybackSession.SeekCompleted += OnSeekCompleted;
            _player = player;
        }
        catch
        {
            if (player is not null)
            {
                try { player.MediaOpened -= OnMediaOpened; } catch { }
                try { player.MediaEnded -= OnMediaEnded; } catch { }
                try { player.MediaFailed -= OnMediaFailed; } catch { }
                try { player.PlaybackSession.SeekCompleted -= OnSeekCompleted; } catch { }
                try { player.Dispose(); } catch { }
            }

            throw;
        }

        Unloaded += (_, _) => Dispose();

        ApplyPlayerVolume();
        SyncVolumeUi();
        StartPlayback();
        _ = LoadMetadataAsync();
    }

    public void TogglePlayback()
    {
        if (_disposed || _retired)
        {
            return;
        }

        if (_isPlaying)
        {
            try { _player.Pause(); } catch { }
            _isPlaying = false;
            _tickTimer.Stop();
        }
        else
        {
            try { _player.Play(); } catch { }
            _isPlaying = true;
            _tickTimer.Start();
        }

        SyncPlayGlyph();
    }

    public void Skip(int direction)
    {
        if (_disposed || _retired)
        {
            return;
        }

        TimeSpan basePos;
        if (Environment.TickCount64 < _pendingSeekUntilTicks)
        {
            basePos = _pendingSeekTarget;
        }
        else
        {
            try
            {
                basePos = _player.PlaybackSession.Position;
            }
            catch
            {
                return;
            }
        }

        var next = basePos + TimeSpan.FromSeconds(direction * MediaPlayerPage.SkipSeconds);
        SeekTo(next, EffectiveDuration());
    }

    public void AdjustVolume(double delta)
    {
        if (_disposed || _retired)
        {
            return;
        }

        _isMuted = false;
        _volume = Math.Clamp(_volume + delta, 0, 1);
        ApplyPlayerVolume();
        SyncVolumeUi();
        ScheduleVolumeSave();
    }

    /// <summary>
    /// Terminally stops audio and flushes any pending volume change when the
    /// user browses to another item, while keeping the surface's visuals intact
    /// during the crossfade dissolve.
    /// </summary>
    public void RetirePlayback()
    {
        if (_disposed || _retired)
        {
            return;
        }

        _retired = true;
        _isScrubbing = false;
        _pendingSeekUntilTicks = 0;
        _seekSlider.CancelDrag();
        _volSlider.CancelDrag();
        _tickTimer.Stop();
        if (_volumeSaveTimer.IsEnabled)
        {
            _volumeSaveTimer.Stop();
            PersistVolume();
        }

        _isPlaying = false;
        SyncPlayGlyph();
        ReleaseMediaSource();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retired = true;
        _isScrubbing = false;
        _pendingSeekUntilTicks = 0;
        _seekSlider.CancelDrag();
        _volSlider.CancelDrag();
        _tickTimer.Stop();
        if (_volumeSaveTimer.IsEnabled)
        {
            _volumeSaveTimer.Stop();
            PersistVolume();
        }

        try { _player.MediaOpened -= OnMediaOpened; } catch { }
        try { _player.MediaEnded -= OnMediaEnded; } catch { }
        try { _player.MediaFailed -= OnMediaFailed; } catch { }
        try { _player.PlaybackSession.SeekCompleted -= OnSeekCompleted; } catch { }
        ReleaseMediaSource();
        try { _player.Dispose(); } catch { }
    }

    private void ReleaseMediaSource()
    {
        try { _player.Pause(); } catch { }
        try { _player.Source = null; } catch { }
        try { _mediaSource?.Dispose(); } catch { }
        _mediaSource = null;
        try { _mediaRandomAccessStream?.Dispose(); } catch { }
        _mediaRandomAccessStream = null;
        try { _mediaFileStream?.Dispose(); } catch { }
        _mediaFileStream = null;
    }

    private void OnMediaOpened(WinRtMediaPlayer sender, object args)
    {
        if (_disposed || _retired)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            if (ReadNaturalDuration() is { } dur)
            {
                _knownDuration = dur;
                _totalBlock.Text = FormatTime(dur);
            }

            SyncTimeline();
        });
    }

    private void OnMediaEnded(WinRtMediaPlayer sender, object args)
    {
        if (_disposed || _retired)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            _isPlaying = false;
            _pendingSeekUntilTicks = 0;
            _tickTimer.Stop();
            try
            {
                _player.Pause();
                _player.PlaybackSession.Position = TimeSpan.Zero;
            }
            catch
            {
                // Player torn down.
            }

            SyncPlayGlyph();
            SyncTimeline();
        });
    }

    private void OnSeekCompleted(Windows.Media.Playback.MediaPlaybackSession sender, object args)
    {
        if (_disposed || _retired)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || _retired)
            {
                return;
            }

            _pendingSeekUntilTicks = 0;
            SyncTimeline();
        });
    }

    private void OnMediaFailed(WinRtMediaPlayer sender, Windows.Media.Playback.MediaPlayerFailedEventArgs args)
    {
        if (_disposed || _retired)
        {
            return;
        }

        var reason = string.IsNullOrWhiteSpace(args.ErrorMessage)
            ? args.Error.ToString()
            : $"{args.Error}: {args.ErrorMessage}";
        var error = args.ExtendedErrorCode;
        _ = Dispatcher.BeginInvoke(() => HandlePlaybackFailure(reason, error));
    }

    private void StartPlayback()
    {
        try
        {
            // Open via FileStream so long (>260 char) paths and filenames with
            // literal percent sequences ("%20", "%23") always open the exact
            // selected file without URI escaping or MAX_PATH issues.
            var fileStream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64 * 1024,
                FileOptions.RandomAccess);
            _mediaFileStream = fileStream;
            _mediaRandomAccessStream = fileStream.AsRandomAccessStream();
            _mediaSource = MediaSource.CreateFromStream(_mediaRandomAccessStream, AudioMimeType(_path));
            _player.Source = _mediaSource;
            _player.Play();
            _isPlaying = true;
            _tickTimer.Start();
            SyncPlayGlyph();
        }
        catch (Exception ex)
        {
            ReleaseMediaSource();
            _isPlaying = false;
            SyncPlayGlyph();
            var reason = $"{ex.GetType().Name}: {ex.Message}";
            _ = Dispatcher.BeginInvoke(() => HandlePlaybackFailure(reason, ex));
        }
    }

    private static string AudioMimeType(string path) =>
        System.IO.Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".aac" => "audio/aac",
            ".flac" => "audio/flac",
            ".wma" => "audio/x-ms-wma",
            _ => string.Empty
        };

    private void HandlePlaybackFailure(string reason, Exception? error)
    {
        if (_disposed || _retired || _fallbackFired)
        {
            return;
        }

        _fallbackFired = true;
        RetirePlayback();
        DiagnosticLog.Write($"native audio failed {_path}: {reason}");
        _onFailed(error);
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            var result = await Task.Run(() =>
            {
                ShellMediaProperties.TryGetAudioMetadata(_path, out var tags);
                BitmapSource? art = null;
                try
                {
                    var thumb = ShellThumbnailProvider.TryGetCachedThumbnail(_path, 512)
                        ?? ShellThumbnailProvider.TryGetThumbnail(_path, 512);
                    if (thumb is { IsThumbnail: true })
                    {
                        art = thumb.Bitmap;
                    }
                }
                catch
                {
                    // Missing shell artwork falls back to the note tile.
                }

                return (tags, art);
            }, _token);

            if (_disposed || _token.IsCancellationRequested)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.tags.Title))
            {
                _titleBlock.Text = result.tags.Title.Trim();
                _titleBlock.Visibility = Visibility.Visible;
            }

            var line2 = string.Join(
                " · ",
                new[] { result.tags.DisplayArtist?.Trim(), result.tags.AlbumTitle?.Trim() }
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            if (line2.Length > 0)
            {
                _subtitleBlock.Text = line2;
                _subtitleBlock.Visibility = Visibility.Visible;
            }

            if (result.tags.Duration is { TotalSeconds: > 0 } metaDuration && _knownDuration <= TimeSpan.Zero)
            {
                _knownDuration = metaDuration;
                _totalBlock.Text = FormatTime(metaDuration);
            }

            if (result.art is { } bitmap)
            {
                _artImage.Source = bitmap;
                _artImage.Visibility = Visibility.Visible;
                _noteGlyph.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Metadata is enrichment; playback continues without it.
        }
    }

    private void ToggleMute()
    {
        if (_disposed || _retired)
        {
            return;
        }

        _isMuted = !_isMuted;
        ApplyPlayerVolume();
        SyncVolumeUi();
        ScheduleVolumeSave();
    }

    private void ApplyPlayerVolume()
    {
        try
        {
            _player.IsMuted = _isMuted;
            _player.Volume = _volume;
        }
        catch
        {
            // Player already disposed.
        }
    }

    private void SyncVolumeUi()
    {
        _volSlider.Value = Math.Round((_isMuted ? 0 : _volume) * 100);
        SyncMuteGlyph();
    }

    private void SyncMuteGlyph()
    {
        _volGlyph.Visibility = _isMuted ? Visibility.Collapsed : Visibility.Visible;
        _volMutedGlyph.Visibility = _isMuted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ScheduleVolumeSave()
    {
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    private void PersistVolume()
    {
        if (Math.Abs(Settings.Current.MediaVolume - _volume) < 0.005 && Settings.Current.MediaMuted == _isMuted)
        {
            return;
        }

        Settings.Update(settings => settings with { MediaVolume = _volume, MediaMuted = _isMuted });
        DiagnosticLog.Write($"media volume saved {_volume:0.00} muted={_isMuted}");
    }

    private TimeSpan? ReadNaturalDuration()
    {
        try
        {
            var dur = _player.PlaybackSession.NaturalDuration;
            return dur > TimeSpan.Zero && dur < TimeSpan.FromDays(365) ? dur : null;
        }
        catch
        {
            return null;
        }
    }

    private TimeSpan? EffectiveDuration() =>
        ReadNaturalDuration() ?? (_knownDuration > TimeSpan.Zero ? _knownDuration : null);

    private void CommitScrub(double sliderValue)
    {
        _isScrubbing = false;
        if (_disposed || _retired)
        {
            return;
        }

        if (EffectiveDuration() is { TotalSeconds: > 0 } dur)
        {
            var ratio = Math.Clamp(sliderValue / 1000.0, 0, 1);
            SeekTo(TimeSpan.FromSeconds(ratio * dur.TotalSeconds), dur);
        }
        else
        {
            SyncTimeline();
        }
    }

    private void SeekTo(TimeSpan target, TimeSpan? duration)
    {
        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }

        if (duration is { TotalSeconds: > 0 } cap && target > cap)
        {
            target = cap;
        }

        try
        {
            if (!_player.PlaybackSession.CanSeek)
            {
                SyncTimeline();
                return;
            }

            _pendingSeekTarget = target;
            _pendingSeekUntilTicks = Environment.TickCount64 + 250;
            _player.PlaybackSession.Position = target;
        }
        catch
        {
            _pendingSeekUntilTicks = 0;
            SyncTimeline();
            return;
        }

        if (duration is { TotalSeconds: > 0 } dur)
        {
            _seekSlider.Value = Math.Clamp(target.TotalSeconds / dur.TotalSeconds * 1000.0, 0, 1000);
            _totalBlock.Text = FormatTime(dur);
        }

        _elapsedBlock.Text = FormatTime(target);
    }

    private void SyncTimeline()
    {
        if (_disposed || _retired)
        {
            return;
        }

        TimeSpan pos;
        try
        {
            pos = _player.PlaybackSession.Position;
        }
        catch
        {
            return;
        }

        var holdPosition = _isScrubbing || Environment.TickCount64 < _pendingSeekUntilTicks;
        if (EffectiveDuration() is { TotalSeconds: > 0 } dur)
        {
            if (!holdPosition)
            {
                _seekSlider.Value = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds * 1000.0, 0, 1000);
            }

            _totalBlock.Text = FormatTime(dur);
        }

        if (!holdPosition)
        {
            _elapsedBlock.Text = FormatTime(pos);
        }
    }

    private void SyncPlayGlyph()
    {
        _playGlyph.Visibility = _isPlaying ? Visibility.Collapsed : Visibility.Visible;
        _pauseGlyph.Visibility = _isPlaying ? Visibility.Visible : Visibility.Collapsed;
    }

    internal static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        var totalSeconds = (int)Math.Round(value.TotalSeconds);
        var seconds = totalSeconds % 60;
        var totalMinutes = totalSeconds / 60;
        var hours = totalMinutes / 60;
        return hours > 0
            ? $"{hours}:{totalMinutes % 60:00}:{seconds:00}"
            : $"{totalMinutes}:{seconds:00}";
    }

    private static TextBlock BuildTimeText(string initial, TextAlignment align)
    {
        var tb = new TextBlock
        {
            Text = initial,
            MinWidth = 40,
            FontSize = Metrics.FontCaption,
            TextAlignment = align,
            VerticalAlignment = VerticalAlignment.Center
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextTertiary");
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        return tb;
    }

    /// <summary>
    /// Purpose-built 1-axis pointer track for seek and volume. Unlike WPF's
    /// Slider with IsMoveToPointEnabled, clicking anywhere on the bar jumps
    /// immediately, captures the mouse, and drags smoothly even when the
    /// cursor drifts vertically outside the 28 px hit strip.
    /// </summary>
    internal sealed class AccentTrackBar : Grid
    {
        private const double ThumbDiameter = 14.0;
        private const double ThumbRadius = ThumbDiameter / 2.0;

        private readonly Border _fill;
        private readonly Ellipse _innerDot;
        private readonly TranslateTransform _thumbTransform = new();
        private double _value;

        public AccentTrackBar(
            double minimum,
            double maximum,
            double initialValue,
            string accessibleName,
            double smallChange = 1,
            double largeChange = 10)
        {
            Minimum = minimum;
            Maximum = maximum;
            SmallChange = smallChange;
            LargeChange = largeChange;
            Height = 28;
            Background = Brushes.Transparent;
            VerticalAlignment = VerticalAlignment.Center;
            Cursor = Cursors.Hand;
            Focusable = false;
            AutomationProperties.SetName(this, accessibleName);

            var rail = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            }.Themed(Border.BackgroundProperty, "Theme.RaisedBorder");

            _fill = new Border
            {
                Height = 4,
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 0,
                IsHitTestVisible = false
            }.Themed(Border.BackgroundProperty, "Theme.Accent");

            var outerDot = new Ellipse
            {
                Width = ThumbDiameter,
                Height = ThumbDiameter,
                StrokeThickness = 1
            }.Themed(Shape.FillProperty, "Theme.RaisedBg")
             .Themed(Shape.StrokeProperty, "Theme.RaisedBorder");

            _innerDot = new Ellipse
            {
                Width = 6,
                Height = 6
            }.Themed(Shape.FillProperty, "Theme.Accent");

            var thumb = new Grid
            {
                Width = ThumbDiameter,
                Height = ThumbDiameter,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                RenderTransform = _thumbTransform,
                Children = { outerDot, _innerDot }
            };

            Children.Add(rail);
            Children.Add(_fill);
            Children.Add(thumb);

            _value = ClampValue(initialValue);
            SizeChanged += (_, _) => SyncVisuals();
            Loaded += (_, _) => SyncVisuals();
        }

        public double Minimum { get; }
        public double Maximum { get; }
        public double SmallChange { get; }
        public double LargeChange { get; }
        public bool IsDragging { get; private set; }

        public double Value
        {
            get => _value;
            set
            {
                var clamped = ClampValue(value);
                var old = _value;
                if (Math.Abs(clamped - old) > 1e-9)
                {
                    _value = clamped;
                    NotifyAutomationValueChanged(old, clamped);
                }

                SyncVisuals();
            }
        }

        public event Action? DragStarted;
        public event Action<double>? UserValueChanged;
        public event Action<double>? DragCompleted;

        protected override AutomationPeer OnCreateAutomationPeer() =>
            new AccentTrackBarAutomationPeer(this);

        internal void SetValueFromAutomation(double value)
        {
            var old = _value;
            var clamped = ClampValue(value);
            _value = clamped;
            SyncVisuals();
            if (Math.Abs(clamped - old) > 1e-9)
            {
                NotifyAutomationValueChanged(old, clamped);
            }

            UserValueChanged?.Invoke(_value);
            DragCompleted?.Invoke(_value);
        }

        public void CancelDrag()
        {
            if (!IsDragging)
            {
                return;
            }

            IsDragging = false;
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            SyncHoverDot();
        }

        internal void BeginPointerDrag(double x)
        {
            IsDragging = true;
            var old = _value;
            _value = ValueFromX(x);
            if (Math.Abs(_value - old) > 1e-9)
            {
                NotifyAutomationValueChanged(old, _value);
            }

            SyncVisuals();
            SyncHoverDot();
            DragStarted?.Invoke();
            UserValueChanged?.Invoke(_value);
        }

        internal void UpdatePointerDrag(double x)
        {
            if (!IsDragging)
            {
                return;
            }

            var old = _value;
            _value = ValueFromX(x);
            if (Math.Abs(_value - old) > 1e-9)
            {
                NotifyAutomationValueChanged(old, _value);
            }

            SyncVisuals();
            UserValueChanged?.Invoke(_value);
        }

        internal void EndPointerDrag(double? x)
        {
            if (!IsDragging)
            {
                return;
            }

            var changed = false;
            if (x.HasValue)
            {
                var old = _value;
                var next = ValueFromX(x.Value);
                changed = Math.Abs(next - old) > 1e-9;
                _value = next;
                if (changed)
                {
                    NotifyAutomationValueChanged(old, next);
                }

                SyncVisuals();
            }

            IsDragging = false;
            SyncHoverDot();
            if (changed)
            {
                UserValueChanged?.Invoke(_value);
            }

            DragCompleted?.Invoke(_value);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);
            BeginPointerDrag(e.GetPosition(this).X);
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!IsDragging)
            {
                return;
            }

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndPointerDrag(null);
                if (IsMouseCaptured)
                {
                    ReleaseMouseCapture();
                }

                return;
            }

            UpdatePointerDrag(e.GetPosition(this).X);
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            if (!IsDragging)
            {
                return;
            }

            EndPointerDrag(e.GetPosition(this).X);
            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }

            e.Handled = true;
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            base.OnLostMouseCapture(e);
            if (IsDragging)
            {
                EndPointerDrag(null);
            }
        }

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            SyncHoverDot();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            SyncHoverDot();
        }

        private double ClampValue(double value) =>
            double.IsFinite(value) ? Math.Clamp(value, Minimum, Maximum) : Minimum;

        private double ValueFromX(double x)
        {
            var width = ActualWidth;
            if (width <= 0)
            {
                return Minimum;
            }

            var travel = width - ThumbDiameter;
            var ratio = travel > 1e-6
                ? Math.Clamp((x - ThumbRadius) / travel, 0, 1)
                : Math.Clamp(x / width, 0, 1);
            return Minimum + ratio * (Maximum - Minimum);
        }

        private void SyncVisuals()
        {
            var width = ActualWidth;
            if (width <= 0)
            {
                return;
            }

            var span = Math.Max(1e-6, Maximum - Minimum);
            var ratio = Math.Clamp((_value - Minimum) / span, 0, 1);
            var travel = Math.Max(0, width - ThumbDiameter);
            var thumbX = travel * ratio;
            _thumbTransform.X = thumbX;
            _fill.Width = ratio <= 0 ? 0 : Math.Min(width, thumbX + ThumbRadius);
        }

        private void SyncHoverDot()
        {
            var size = IsMouseOver || IsDragging ? 8.0 : 6.0;
            _innerDot.Width = size;
            _innerDot.Height = size;
        }

        private void NotifyAutomationValueChanged(double oldValue, double newValue)
        {
            if (UIElementAutomationPeer.FromElement(this) is AccentTrackBarAutomationPeer peer)
            {
                peer.RaisePropertyChangedEvent(RangeValuePatternIdentifiers.ValueProperty, oldValue, newValue);
            }
        }

        private sealed class AccentTrackBarAutomationPeer(AccentTrackBar owner)
            : FrameworkElementAutomationPeer(owner), IRangeValueProvider
        {
            private AccentTrackBar OwnerBar => (AccentTrackBar)Owner;

            protected override AutomationControlType GetAutomationControlTypeCore() =>
                AutomationControlType.Slider;

            protected override string GetClassNameCore() => nameof(AccentTrackBar);

            public override object? GetPattern(PatternInterface patternInterface) =>
                patternInterface == PatternInterface.RangeValue ? this : base.GetPattern(patternInterface);

            public bool IsReadOnly => !OwnerBar.IsEnabled;
            public double Minimum => OwnerBar.Minimum;
            public double Maximum => OwnerBar.Maximum;
            public double Value => OwnerBar.Value;
            public double SmallChange => OwnerBar.SmallChange;
            public double LargeChange => OwnerBar.LargeChange;

            public void SetValue(double value)
            {
                if (!IsEnabled())
                {
                    throw new ElementNotEnabledException();
                }

                if (!double.IsFinite(value) || value < OwnerBar.Minimum || value > OwnerBar.Maximum)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                OwnerBar.SetValueFromAutomation(value);
            }
        }
    }

    private static Button BuildTransportButton(double size, bool primary, string tooltip, UIElement icon, Action onClick)
    {
        var button = new Button
        {
            Width = size,
            Height = size,
            ToolTip = tooltip,
            Focusable = false,
            Cursor = Cursors.Hand,
            Content = icon
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(size / 2));
        if (primary)
        {
            border.SetResourceReference(Border.BackgroundProperty, "Theme.RaisedBg");
            border.SetResourceReference(Border.BorderBrushProperty, "Theme.RaisedBorder");
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        }
        else
        {
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        }

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Theme.HoverBg"), "Chrome"));
        template.Triggers.Add(hoverTrigger);
        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Theme.PressedBg"), "Chrome"));
        template.Triggers.Add(pressedTrigger);

        button.Template = template;
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Button BuildMuteButton(UIElement icon, Action onClick)
    {
        var button = new Button
        {
            Width = 30,
            Height = 30,
            ToolTip = "Mute",
            Focusable = false,
            Cursor = Cursors.Hand,
            Content = icon
        };
        System.Windows.Automation.AutomationProperties.SetName(button, "Mute");

        var border = new FrameworkElementFactory(typeof(Border));
        border.Name = "Chrome";
        border.SetValue(Border.CornerRadiusProperty, Metrics.IconCorner);
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Theme.HoverBg"), "Chrome"));
        template.Triggers.Add(hoverTrigger);
        var pressedTrigger = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("Theme.PressedBg"), "Chrome"));
        template.Triggers.Add(pressedTrigger);
        button.Template = template;
        button.Click += (_, _) => onClick();
        return button;
    }

    private static UIElement BuildNoteIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        var stem = new Path
        {
            Data = Geometry.Parse("M9.5,17.5 V6.2 L18.5,4.5 V15.5"),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round
        }.Themed(Shape.StrokeProperty, "Theme.TextTertiary");
        var head1 = new Ellipse { Width = 5, Height = 4 }.Themed(Shape.FillProperty, "Theme.TextTertiary");
        Canvas.SetLeft(head1, 4.5);
        Canvas.SetTop(head1, 15.5);
        var head2 = new Ellipse { Width = 5, Height = 4 }.Themed(Shape.FillProperty, "Theme.TextTertiary");
        Canvas.SetLeft(head2, 13.5);
        Canvas.SetTop(head2, 13.5);
        canvas.Children.Add(stem);
        canvas.Children.Add(head1);
        canvas.Children.Add(head2);
        return canvas;
    }

    private static (Path Play, Grid Pause) BuildPlayPauseIcons()
    {
        var play = new Path
        {
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            Margin = new Thickness(2, 0, 0, 0),
            Data = Geometry.Parse("M8,4.6 L19,12 L8,19.4 Z")
        }.Themed(Shape.FillProperty, "Theme.TextPrimary");

        var pause = new Grid { Width = 22, Height = 22, Visibility = Visibility.Collapsed };
        var bar1 = new Rectangle { Width = 4.4, Height = 14.8, RadiusX = 1.4, RadiusY = 1.4, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0) }
            .Themed(Shape.FillProperty, "Theme.TextPrimary");
        var bar2 = new Rectangle { Width = 4.4, Height = 14.8, RadiusX = 1.4, RadiusY = 1.4, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0) }
            .Themed(Shape.FillProperty, "Theme.TextPrimary");
        pause.Children.Add(bar1);
        pause.Children.Add(bar2);
        return (play, pause);
    }

    private static UIElement BuildSkipIcon(bool forward)
    {
        var grid = new Grid { Width = 24, Height = 24 };
        var arc = new Path
        {
            Data = Geometry.Parse(forward
                ? "M 16.01,6.72 A 7,7 0 1 0 18.69,10.40"
                : "M 7.99,6.72 A 7,7 0 1 1 5.31,10.40"),
            StrokeThickness = 1.7,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        }.Themed(Shape.StrokeProperty, "Theme.TextSecondary");
        var arrow = new Path
        {
            Data = Geometry.Parse(forward
                ? "M 19.45,6.6 L 15.4,3.7 L 15.3,9.2 Z"
                : "M 4.55,6.6 L 8.6,3.7 L 8.7,9.2 Z")
        }.Themed(Shape.FillProperty, "Theme.TextSecondary");
        var label = new TextBlock
        {
            Text = MediaPlayerPage.SkipSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontSize = 7.5,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        }.Themed(TextBlock.ForegroundProperty, "Theme.TextSecondary");
        grid.Children.Add(arc);
        grid.Children.Add(arrow);
        grid.Children.Add(label);
        return grid;
    }

    private static (Path Loud, Path Muted) BuildVolumeIcons()
    {
        var loud = new Path
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Data = Geometry.Parse("M4,9.5 L8,9.5 L13,5 L13,19 L8,14.5 L4,14.5 Z M16,9 A4.4,4.4 0 0 1 16,15 M18.4,6.6 A7.6,7.6 0 0 1 18.4,17.4"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        }.Themed(Shape.StrokeProperty, "Theme.TextSecondary")
         .Themed(Shape.FillProperty, "Theme.TextSecondary");

        var muted = new Path
        {
            Width = 18,
            Height = 18,
            Stretch = Stretch.Uniform,
            Visibility = Visibility.Collapsed,
            Data = Geometry.Parse("M4,9.5 L8,9.5 L13,5 L13,19 L8,14.5 L4,14.5 Z M16.4,9.6 L21.2,14.4 M21.2,9.6 L16.4,14.4"),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round
        }.Themed(Shape.StrokeProperty, "Theme.TextSecondary")
         .Themed(Shape.FillProperty, "Theme.TextSecondary");

        return (loud, muted);
    }
}
