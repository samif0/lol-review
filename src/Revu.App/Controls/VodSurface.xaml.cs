#nullable enable

using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;
using MediaPlaybackState = Windows.Media.Playback.MediaPlaybackState;

namespace Revu.App.Controls;

/// <summary>
/// Self-contained VOD playback surface — video + transport + timeline. The
/// playback internals (MediaPlayer setup, stable-file load, duration probe,
/// seek, position tick, cleanup) are lifted verbatim from VodPlayerPage so
/// playback behaves identically wherever this control is hosted. The host
/// drives it imperatively (LoadAsync / SeekTo) and listens to PositionChanged /
/// MediaReady; this control has no ViewModel dependency.
/// </summary>
public sealed partial class VodSurface : UserControl
{
    private MediaPlayer? _mediaPlayer;
    private MediaPlayerElement? _playerElement;
    private DispatcherTimer? _positionTimer;
    private bool _isDisposed;
    private string? _loadedMediaPath;
    private int? _pendingSeekTimeS;
    private double _lastKnownDurationS;
    private bool _scrubbing;     // user is pressing/dragging the scrub bar
    private double _scrubWidth;  // measured width of the scrub hit area

    /// <summary>Current playback position, in seconds (fires ~4x/sec while loaded).</summary>
    public event Action<double>? PositionChanged;

    /// <summary>Fires once a loaded recording has a decodable video track and known duration.</summary>
    public event Action<double>? MediaReady;

    /// <summary>Fires when the loaded recording can't be played (missing track / decode failure).</summary>
    public event Action? MediaFailed;

    public VodSurface()
    {
        InitializeComponent();
        VideoContainer.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(OnVideoPointerPressed),
            handledEventsToo: true);
        Loaded += (_, _) => EnsurePlayer();
        Unloaded += (_, _) => Cleanup();
    }

    /// <summary>Whether a media source is currently loaded.</summary>
    public bool HasMedia => _mediaPlayer?.Source is not null;

    /// <summary>Current playback position in seconds (0 when nothing is loaded).</summary>
    public double CurrentPositionSeconds =>
        _mediaPlayer?.PlaybackSession.Position.TotalSeconds ?? 0;

    // ── Public API ──────────────────────────────────────────────────────────

    /// <summary>
    /// Load a recording. Shows the loading overlay, waits for the file to stop
    /// growing (recorder may still be flushing), probes duration, and sets the
    /// source. No-op if the same file is already loaded (avoids a position
    /// reset). After load, the host should set the timeline Duration.
    /// </summary>
    public async Task LoadAsync(string filePath)
    {
        EnsurePlayer();
        if (_mediaPlayer == null) return;

        if (string.IsNullOrWhiteSpace(filePath) || !System.IO.File.Exists(filePath))
        {
            ShowNoVod("No recording for this game.");
            return;
        }

        if (string.Equals(_loadedMediaPath, filePath, StringComparison.OrdinalIgnoreCase)
            && _mediaPlayer.Source is not null)
        {
            return;
        }

        NoVodBorder.Visibility = Visibility.Collapsed;
        LoadingOverlay.Visibility = Visibility.Visible;

        try
        {
            await WaitForStableFileAsync(filePath);
            var file = await StorageFile.GetFileFromPathAsync(filePath);

            var probedDurationS = await ProbeMediaDurationSecondsAsync(file);
            if (probedDurationS > 0)
            {
                _lastKnownDurationS = probedDurationS;
                TotalTimeText.Text = FormatTime(probedDurationS);
            }

            var source = MediaSource.CreateFromStorageFile(file);
            _mediaPlayer.Source = source;
            _loadedMediaPath = filePath;
        }
        catch (Exception ex)
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            ShowNoVod($"Failed to load VOD: {ex.Message}");
        }
    }

    /// <summary>Seek to an absolute time. Queues the seek if the media isn't ready yet.</summary>
    public void SeekTo(double seconds)
    {
        if (_mediaPlayer == null) return;
        var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total <= 0)
        {
            _pendingSeekTimeS = (int)Math.Max(0, seconds);
            return;
        }
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, total));
    }

    public void Pause() => _mediaPlayer?.Pause();

    public void TogglePlayPause()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    // ── Player setup (lifted from VodPlayerPage) ────────────────────────────

    private void EnsurePlayer()
    {
        if (_mediaPlayer != null || _isDisposed) return;

        _mediaPlayer = new MediaPlayer { AutoPlay = true };
        _playerElement = new MediaPlayerElement
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Stretch.Uniform,
            AreTransportControlsEnabled = false,
            IsHitTestVisible = true,
            AllowFocusOnInteraction = true,
            IsTabStop = false,
        };
        _playerElement.SetMediaPlayer(_mediaPlayer);
        VideoContainer.Children.Insert(0, _playerElement);

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += OnPositionTick;
        _positionTimer.Start();

        _mediaPlayer.MediaOpened += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mediaPlayer == null) return;
            LoadingOverlay.Visibility = Visibility.Collapsed;
            var totalS = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;

            if (_mediaPlayer.PlaybackSession.NaturalVideoHeight == 0)
            {
                ShowNoVod("This recording has no playable video track.");
                MediaFailed?.Invoke();
                return;
            }

            if (totalS > 0)
            {
                _lastKnownDurationS = totalS;
                TotalTimeText.Text = FormatTime(totalS);
                ApplyPendingSeek();
                MediaReady?.Invoke(totalS);
            }
        });
        _mediaPlayer.MediaFailed += (s, ev) => DispatcherQueue.TryEnqueue(() =>
        {
            System.Diagnostics.Debug.WriteLine($"VodSurface playback failed: {ev.Error} — {ev.ErrorMessage}");
            ShowNoVod("Could not play this recording.");
            MediaFailed?.Invoke();
        });
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mediaPlayer == null) return;
            var playing = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            UpdatePlayPauseButtons(playing);
        });

        UpdatePlayPauseButtons(playing: false);
    }

    private static async Task<double> ProbeMediaDurationSecondsAsync(StorageFile file)
    {
        try
        {
            var properties = await file.Properties.GetVideoPropertiesAsync();
            if (properties.Duration.TotalSeconds > 0)
            {
                return properties.Duration.TotalSeconds;
            }
        }
        catch
        {
            // Fall through to MediaClip; some recordings don't expose duration
            // through shell file properties even though the pipeline can.
        }

        try
        {
            var clip = await MediaClip.CreateFromFileAsync(file);
            return clip.OriginalDuration.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task WaitForStableFileAsync(string filePath)
    {
        const int maxWaitMs = 3000;
        const int pollIntervalMs = 400;
        long lastSize = -1;
        var elapsed = 0;
        while (elapsed < maxWaitMs)
        {
            long size;
            try { size = new System.IO.FileInfo(filePath).Length; }
            catch { return; }
            if (size > 0 && size == lastSize) return;
            lastSize = size;
            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;
        }
    }

    private void OnPositionTick(object? sender, object e)
    {
        if (_mediaPlayer == null || _isDisposed) return;
        try
        {
            var session = _mediaPlayer.PlaybackSession;
            var currentS = session.Position.TotalSeconds;
            CurrentTimeText.Text = FormatTime(currentS);

            // Reflect progress on the scrub bar unless the user is dragging it.
            if (!_scrubbing)
            {
                var total = session.NaturalDuration.TotalSeconds;
                UpdateScrubVisual(total > 0 ? Math.Clamp(currentS / total, 0, 1) : 0);
            }

            PositionChanged?.Invoke(currentS);
        }
        catch { }
    }

    private void ApplyPendingSeek()
    {
        if (!_pendingSeekTimeS.HasValue || _mediaPlayer == null) return;
        var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total <= 0) return;
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(_pendingSeekTimeS.Value, 0, total));
        _pendingSeekTimeS = null;
    }

    private void Cleanup()
    {
        _isDisposed = true;
        _positionTimer?.Stop();
        if (_playerElement != null)
        {
            VideoContainer.Children.Remove(_playerElement);
            _playerElement.SetMediaPlayer(null);
            _playerElement = null;
        }
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }
        _loadedMediaPath = null;
    }

    // ── UI handlers ─────────────────────────────────────────────────────────

    private void ShowNoVod(string message)
    {
        LoadingOverlay.Visibility = Visibility.Collapsed;
        NoVodText.Text = message;
        NoVodBorder.Visibility = Visibility.Visible;
        VideoPlayPauseButton.Visibility = Visibility.Collapsed;
    }

    private void UpdatePlayPauseButtons(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "" : "";
        VideoPlayPauseIcon.Glyph = playing ? "" : "";
        VideoPlayPauseButton.Visibility =
            HasMedia && NoVodBorder.Visibility != Visibility.Visible && !playing
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    // ── Scrub bar (primitive-built, HUD-styled) ─────────────────────────────
    //
    // Click or drag anywhere on the bar seeks to that fraction. We compute the
    // fraction from the pointer's X within the bar, so a single click works
    // (unlike the default Slider, which needed thumb interaction).

    private void OnScrubSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _scrubWidth = e.NewSize.Width;
        // Re-place fill/thumb for the current position after a resize.
        if (_mediaPlayer != null && !_scrubbing)
        {
            var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            var cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
            UpdateScrubVisual(total > 0 ? Math.Clamp(cur / total, 0, 1) : 0);
        }
    }

    private void OnScrubPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _scrubbing = true;
        ScrubBar.CapturePointer(e.Pointer);
        SeekFromPointer(e);
    }

    private void OnScrubPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_scrubbing) SeekFromPointer(e);
    }

    private void OnScrubPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        try { ScrubBar.ReleasePointerCapture(e.Pointer); } catch { }
    }

    private void SeekFromPointer(PointerRoutedEventArgs e)
    {
        if (_mediaPlayer == null || _scrubWidth <= 0) return;
        var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total <= 0) return;

        var x = e.GetCurrentPoint(ScrubBar).Position.X;
        var fraction = Math.Clamp(x / _scrubWidth, 0, 1);
        UpdateScrubVisual(fraction);     // immediate visual feedback while dragging
        SeekTo(fraction * total);
    }

    private void UpdateScrubVisual(double fraction)
    {
        if (_scrubWidth <= 0) return;
        var w = Math.Clamp(fraction, 0, 1) * _scrubWidth;
        ScrubFill.Width = w;
        ScrubThumb.Margin = new Thickness(Math.Max(0, w - 1.5), 0, 0, 0);
    }

    private void OnSeekBackClick(object sender, RoutedEventArgs e) => StepSeek(-5);
    private void OnSeekForwardClick(object sender, RoutedEventArgs e) => StepSeek(5);

    private void StepSeek(double deltaSeconds)
    {
        if (_mediaPlayer == null) return;
        var cur = _mediaPlayer.PlaybackSession.Position.TotalSeconds;
        SeekTo(cur + deltaSeconds);
    }

    private void OnTransportPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();
    private void OnVideoPlayPauseClick(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void OnVideoPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        TogglePlayPause();
        VideoContainer.Focus(FocusState.Programmatic);
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
        => VideoContainer.Focus(FocusState.Programmatic);

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        if (SpeedCombo.SelectedItem is ComboBoxItem item
            && double.TryParse(item.Tag?.ToString(), out var speed))
        {
            _mediaPlayer.PlaybackSession.PlaybackRate = speed;
        }
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Volume = e.NewValue / 100.0;
        VolumeIcon.Glyph = e.NewValue <= 0 ? "" : "";
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.IsMuted = !_mediaPlayer.IsMuted;
        VolumeIcon.Glyph = _mediaPlayer.IsMuted ? "" : "";
    }

    private void OnVideoBorderPointerEntered(object sender, PointerRoutedEventArgs e)
        => VideoBorder.BorderBrush = (Brush)Application.Current.Resources["BrightBorderBrush"];

    private void OnVideoBorderPointerExited(object sender, PointerRoutedEventArgs e)
        => VideoBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];

    private static string FormatTime(double seconds)
    {
        if (seconds < 0 || double.IsNaN(seconds)) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.Hours > 0
            ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }
}
