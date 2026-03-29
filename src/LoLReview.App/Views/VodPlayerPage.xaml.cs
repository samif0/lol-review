#nullable enable

using LoLReview.App.ViewModels;
using LoLReview.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using MediaPlaybackState = Windows.Media.Playback.MediaPlaybackState;

namespace LoLReview.App.Views;

public sealed partial class VodPlayerPage : Page
{
    private MediaPlayer? _mediaPlayer;
    private MediaPlayerElement? _playerElement;
    private DispatcherTimer? _positionTimer;
    private bool _isDisposed;

    // Stored as fields so AddHandler/RemoveHandler use the exact same delegate instances.
    private readonly KeyEventHandler _keyDownHandler;
    private readonly KeyEventHandler _keyUpHandler;

    // The ShellPage (window root content) — we hook KeyDown/KeyUp here so we sit above
    // the Frame in the visual tree and intercept before any button can act on Space.
    private UIElement? _windowRoot;
    private int? _pendingSeekTimeS;

    public VodPlayerViewModel ViewModel { get; }

    public VodPlayerPage()
    {
        ViewModel = App.GetService<VodPlayerViewModel>();
        InitializeComponent();

        _keyDownHandler = new KeyEventHandler(OnGlobalKeyDown);
        _keyUpHandler = new KeyEventHandler(OnGlobalKeyUp);

        ViewModel.SeekRequested += OnSeekRequested;
        ViewModel.SpeedChangeRequested += OnSpeedChangeRequested;
        ViewModel.PlayPauseRequested += OnPlayPauseRequested;
        Timeline.SeekRequested += OnTimelineSeek;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Register global key handlers on the window root (ShellPage).
        // Hooked above the Frame so we intercept before any focused button acts on Space.
        // KeyUp is also blocked for Space because WinUI buttons activate on KeyUp.
        _windowRoot = App.MainWindow?.Content as UIElement;
        _windowRoot?.AddHandler(KeyDownEvent, _keyDownHandler, handledEventsToo: true);
        _windowRoot?.AddHandler(KeyUpEvent, _keyUpHandler, handledEventsToo: true);

        if (_playerElement == null)
            CreatePlayer();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;

        switch (e.Parameter)
        {
            case long gameId:
                _pendingSeekTimeS = null;
                ViewModel.LoadCommand.Execute(gameId);
                break;
            case VodPlayerNavigationRequest request:
                _pendingSeekTimeS = request.SeekTimeS;
                ViewModel.LoadCommand.Execute(request.GameId);
                break;
        }

        TryLoadMedia();

        // Focus the video area so no button holds focus and eats Space.
        DispatcherQueue.TryEnqueue(() => VideoContainer.Focus(FocusState.Programmatic));
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Unregister both handlers.
        _windowRoot?.RemoveHandler(KeyDownEvent, _keyDownHandler);
        _windowRoot?.RemoveHandler(KeyUpEvent, _keyUpHandler);
        _windowRoot = null;

        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        Cleanup();
    }

    // ── Player setup ────────────────────────────────────────────────

    private void CreatePlayer()
    {
        _mediaPlayer = new MediaPlayer { AutoPlay = true };

        _playerElement = new MediaPlayerElement
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            AreTransportControlsEnabled = false,
            IsHitTestVisible = false,
            AllowFocusOnInteraction = false,
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
            var totalS = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
            if (totalS > 0)
            {
                ViewModel.UpdatePosition(0, totalS);
                ApplyPendingSeek();
            }
        });
        _mediaPlayer.MediaFailed += (s, ev) => DispatcherQueue.TryEnqueue(() =>
        {
            NoVodText.Text = $"Media error: {ev.Error} — {ev.ErrorMessage}";
            NoVodText.Visibility = Visibility.Visible;
        });
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mediaPlayer == null) return;
            var playing = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            ViewModel.IsPlaying = playing;
            PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        });
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.VodPath) or nameof(ViewModel.HasVod))
            DispatcherQueue.TryEnqueue(TryLoadMedia);

        if (e.PropertyName is nameof(ViewModel.HasFfmpeg))
            DispatcherQueue.TryEnqueue(() =>
                FfmpegWarning.Visibility = ViewModel.HasFfmpeg ? Visibility.Collapsed : Visibility.Visible);
    }

    private void TryLoadMedia()
    {
        if (_mediaPlayer == null) return;
        if (string.IsNullOrEmpty(ViewModel.VodPath) || !ViewModel.HasVod)
        {
            NoVodText.Visibility = ViewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;
            return;
        }

        NoVodText.Visibility = Visibility.Collapsed;

        if (!System.IO.File.Exists(ViewModel.VodPath))
        {
            NoVodText.Text = "VOD file not found";
            NoVodText.Visibility = Visibility.Visible;
            return;
        }

        _ = LoadMediaAsync(ViewModel.VodPath);
    }

    private async Task LoadMediaAsync(string filePath)
    {
        if (_mediaPlayer == null) return;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var source = MediaSource.CreateFromStorageFile(file);
            _mediaPlayer.Source = source;
        }
        catch (Exception ex)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NoVodText.Text = $"Failed to load VOD: {ex.Message}";
                NoVodText.Visibility = Visibility.Visible;
            });
        }
    }

    private void OnPositionTick(object? sender, object e)
    {
        if (_mediaPlayer == null || _isDisposed) return;
        try
        {
            var session = _mediaPlayer.PlaybackSession;
            var currentS = session.Position.TotalSeconds;
            var totalS = session.NaturalDuration.TotalSeconds;
            if (totalS > 0)
                ViewModel.UpdatePosition(currentS, totalS);
        }
        catch { }
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
    }

    // ── ViewModel event handlers ────────────────────────────────────

    private void OnSeekRequested(double seconds)
    {
        if (_mediaPlayer == null) return;
        var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total <= 0) return;
        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(seconds, 0, total));
    }

    private void OnSpeedChangeRequested(double speed)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.PlaybackSession.PlaybackRate = speed;
    }

    private void OnPlayPauseRequested()
    {
        if (_mediaPlayer == null) return;
        if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void OnTimelineSeek(double seconds) => OnSeekRequested(seconds);

    private void ApplyPendingSeek()
    {
        if (!_pendingSeekTimeS.HasValue || _mediaPlayer == null)
        {
            return;
        }

        var total = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;
        if (total <= 0)
        {
            return;
        }

        _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Clamp(_pendingSeekTimeS.Value, 0, total));
        _pendingSeekTimeS = null;
    }

    // ── UI event handlers ───────────────────────────────────────────

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e) => OnPlayPauseRequested();

    private void OnClipButtonClick(object sender, RoutedEventArgs e)
    {
        VideoContainer.Focus(FocusState.Programmatic);
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tagStr &&
            double.TryParse(tagStr, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            ViewModel.SetSpeedCommand.Execute(speed);
        }
    }

    private void OnBookmarkTapped(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem bookmark)
            ViewModel.SeekToBookmarkCommand.Execute(bookmark);
        VideoContainer.Focus(FocusState.Programmatic);
    }

    private void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is long id)
            ViewModel.DeleteBookmarkCommand.Execute(id);
        VideoContainer.Focus(FocusState.Programmatic);
    }

    private void OnBookmarkNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem bookmark)
        {
            ViewModel.SaveBookmarkNoteCommand.Execute(bookmark);
        }
    }

    private void OnQuickBookmarkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        ViewModel.AddBookmarkCommand.Execute(null);
        VideoContainer.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void OnQuickClipKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !ViewModel.HasClipRange)
        {
            return;
        }

        ViewModel.ExtractClipCommand.Execute(null);
        VideoContainer.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void OnBookmarkNoteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        if (sender is FrameworkElement fe && fe.DataContext is BookmarkItem bookmark)
        {
            ViewModel.SaveBookmarkNoteCommand.Execute(bookmark);
            VideoContainer.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.IsMuted = !_mediaPlayer.IsMuted;
        VolumeIcon.Glyph = _mediaPlayer.IsMuted ? "\uE74F" : VolumeGlyph(_mediaPlayer.Volume);
        VideoContainer.Focus(FocusState.Programmatic);
    }

    private void OnVolumeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Volume = e.NewValue / 100.0;
        _mediaPlayer.IsMuted = e.NewValue == 0;
        VolumeIcon.Glyph = _mediaPlayer.IsMuted ? "\uE74F" : VolumeGlyph(_mediaPlayer.Volume);
    }

    private static string VolumeGlyph(double volume) => volume switch
    {
        0 => "\uE74F",       // muted
        < 0.5 => "\uE993",  // low volume
        _ => "\uE767",       // full volume
    };

    // ── Global key handlers (hooked on window root) ─────────────────

    // Suppress Space KeyUp so WinUI buttons can't activate on it while we're active.
    private void OnGlobalKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;
        if (e.Key == Windows.System.VirtualKey.Space)
            e.Handled = true;
    }

    private void OnGlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Only act when this page is actually visible/active.
        if (!IsLoaded) return;

        // Let text boxes keep their own key handling.
        if (XamlRoot != null && FocusManager.GetFocusedElement(XamlRoot) is TextBox) return;

        switch (e.Key)
        {
            case Windows.System.VirtualKey.Space:
                OnPlayPauseRequested();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Left:
                ViewModel.SeekBackwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Right:
                ViewModel.SeekForwardCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Up:
                ViewModel.IncreaseSeekStepCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Down:
                ViewModel.DecreaseSeekStepCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.I:
                ViewModel.SetClipInCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.O:
                ViewModel.SetClipOutCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.B:
                ViewModel.AddBookmarkCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
