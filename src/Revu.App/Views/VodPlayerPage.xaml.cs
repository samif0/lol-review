#nullable enable

using Revu.App.Helpers;
using Revu.App.ViewModels;
using Revu.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using MediaPlaybackState = Windows.Media.Playback.MediaPlaybackState;

namespace Revu.App.Views;

public sealed partial class VodPlayerPage : Page
{
    private MediaPlayer? _mediaPlayer;
    private MediaPlayerElement? _playerElement;
    private DispatcherTimer? _positionTimer;
    private bool _isDisposed;

    // v2.17.16: the path currently set as the media source. Saving a clip flips
    // VM VOD-state props (HasPlayableClips, etc.), which re-fires the property
    // handler → TryLoadMedia. Without this guard that re-set _mediaPlayer.Source
    // to the SAME file, which resets playback position to 0. Skip reloading when
    // the requested file is already loaded; clip playback uses a different path
    // so it still switches correctly.
    private string? _loadedMediaPath;

    // Stored as fields so AddHandler/RemoveHandler use the exact same delegate instances.
    private readonly KeyEventHandler _keyDownHandler;
    private readonly KeyEventHandler _keyUpHandler;

    // The ShellPage (window root content) — we hook KeyDown/KeyUp here so we sit above
    // the Frame in the visual tree and intercept before any button can act on Space.
    private UIElement? _windowRoot;
    private int? _pendingSeekTimeS;

    // v2.17.14: one-shot guard so a VOD that won't render (e.g. an Ascent capture
    // that didn't close cleanly → duplicate-moov MP4 that Media Foundation shows
    // as a black screen) is auto-repaired via ffmpeg remux exactly once. Keyed by
    // the source path so switching games re-arms it. Prevents an infinite
    // fail→repair→fail loop if the repaired file still won't play.
    private string? _repairAttemptedForPath;

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
        ViewModel.ClipPlaybackRequested += OnClipPlaybackRequested;
        Timeline.SeekRequested += OnTimelineSeek;
        FullscreenTimeline.SeekRequested += OnTimelineSeek;
        VideoContainer.AddHandler(
            PointerPressedEvent,
            new PointerEventHandler(OnVideoPointerPressed),
            handledEventsToo: true);
        TimelineInboxScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(TimelineInboxScroll_PointerWheelChanged),
            handledEventsToo: true);

        Loaded += (_, _) =>
        {
            AnimationHelper.AnimatePageEnter(RootGrid);
            UpdateSharedObjectiveLabels();
        };

        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.SelectedObjectiveId)
                || e.PropertyName == nameof(ViewModel.SelectedPromptId))
            {
                UpdateSharedObjectiveLabels();
            }
        };
        ViewModel.TagOptions.CollectionChanged += (_, _) =>
            UpdateSharedObjectiveLabels();
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

        // v2.16: replace v2.15.9 auto-fullscreen with a "minimum usable" window
        // resize. If the window is already big enough for the bookmark column
        // to render without clipping, leave it alone. Otherwise grow it to a
        // VOD-friendly minimum so the user doesn't end up in a takeover
        // fullscreen they didn't ask for.
        DispatcherQueue.TryEnqueue(EnsureMinimumVodWindowSize);

        TryLoadMedia();

        // Focus the video area so no button holds focus and eats Space.
        DispatcherQueue.TryEnqueue(FocusPlaybackSurface);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);

        // Unregister both handlers.
        _windowRoot?.RemoveHandler(KeyDownEvent, _keyDownHandler);
        _windowRoot?.RemoveHandler(KeyUpEvent, _keyUpHandler);
        _windowRoot = null;

        // v2.16: don't auto-restore Default presenter on leave. Auto-fullscreen
        // is gone (see OnNavigatedTo). If the user opted into fullscreen via
        // the manual button, it's their choice — leave them in it.

        if (_isFullscreen)
        {
            ExitVideoTheaterMode();
        }

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
            var totalS = _mediaPlayer.PlaybackSession.NaturalDuration.TotalSeconds;

            // v2.17.14: the file "opened" but has no decodable video track — the
            // silent black-screen case (MediaFailed never fires). Detect it via a
            // missing video dimension and route to the same repair path.
            if (_mediaPlayer.PlaybackSession.NaturalVideoHeight == 0)
            {
                TryRepairAndReload("opened with no video track");
                return;
            }

            if (totalS > 0)
            {
                ViewModel.UpdatePosition(0, totalS);
                ApplyPendingSeek();
            }
        });
        _mediaPlayer.MediaFailed += (s, ev) => DispatcherQueue.TryEnqueue(() =>
        {
            // v2.17.14: first failure on a given source → try a lossless ffmpeg
            // remux and reload (fixes Ascent captures MF can't decode). Only if
            // the repair also fails do we surface the error.
            if (TryRepairAndReload($"{ev.Error} — {ev.ErrorMessage}")) return;

            NoVodText.Text = $"Media error: {ev.Error} — {ev.ErrorMessage}";
            NoVodBorder.Visibility = Visibility.Visible;
        });
        _mediaPlayer.PlaybackSession.PlaybackStateChanged += (s, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_mediaPlayer == null) return;
            var playing = _mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            ViewModel.IsPlaying = playing;
            UpdatePlayPauseButtons(playing);
        });

        UpdatePlayPauseButtons(playing: false);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ViewModel.VodPath)
            or nameof(ViewModel.HasVod)
            or nameof(ViewModel.HasPlayableVod)
            or nameof(ViewModel.HasPlayableClips)
            or nameof(ViewModel.VodAvailabilityText))
            DispatcherQueue.TryEnqueue(() =>
            {
                TryLoadMedia();
                UpdatePlayPauseButtons(ViewModel.IsPlaying);
            });

        if (e.PropertyName is nameof(ViewModel.IsPlaying))
            DispatcherQueue.TryEnqueue(UpdateFullscreenTimelineCompactness);

        if (e.PropertyName is nameof(ViewModel.HasFfmpeg))
            DispatcherQueue.TryEnqueue(() =>
                FfmpegWarning.Visibility = ViewModel.HasFfmpeg ? Visibility.Collapsed : Visibility.Visible);
    }

    /// <summary>
    /// v2.17.7: while playing in fullscreen, collapse the timeline overlay to a
    /// slim dot-marker strip so it doesn't obscure the video. On pause, restore
    /// the full event timeline. Off-fullscreen this is a no-op — the docked
    /// timeline below the transport bar always renders full.
    /// </summary>
    private void UpdateFullscreenTimelineCompactness()
    {
        if (!_isFullscreen) return;

        var playing = ViewModel.IsPlaying;
        FullscreenTimeline.IsCompact = playing;
        // v2.17.8: compact target grown 18→28 so the scrub track is a real
        // click target. The full 80px is still triggered on pause for the
        // detailed marker view.
        FullscreenTimeline.Height = playing ? 28 : 80;
        FullscreenTimelineOverlay.Padding = playing
            ? new Thickness(10, 6, 10, 6)
            : new Thickness(10, 8, 10, 8);
    }

    private void TryLoadMedia()
    {
        if (_mediaPlayer == null) return;
        if (string.IsNullOrEmpty(ViewModel.VodPath) || !ViewModel.HasVod)
        {
            NoVodBorder.Visibility = ViewModel.IsLoading ? Visibility.Collapsed : Visibility.Visible;
            NoVodText.Text = ViewModel.VodAvailabilityText;
            VideoPlayPauseButton.Visibility = Visibility.Collapsed;
            return;
        }

        NoVodBorder.Visibility = Visibility.Collapsed;

        if (!System.IO.File.Exists(ViewModel.VodPath))
        {
            NoVodText.Text = ViewModel.VodAvailabilityText;
            NoVodBorder.Visibility = Visibility.Visible;
            VideoPlayPauseButton.Visibility = Visibility.Collapsed;
            return;
        }

        _ = LoadMediaAsync(ViewModel.VodPath);
    }

    /// <summary>
    /// v2.17.14: attempt a one-shot lossless repair of the currently-loaded VOD
    /// when it won't render (MediaFailed, or opened with no video track). Remuxes
    /// via the bundled ffmpeg through ClipService, then reloads from the repaired
    /// file. Returns true if a repair was kicked off (so the caller suppresses
    /// the error UI); false if there's nothing to repair or we already tried.
    /// </summary>
    private bool TryRepairAndReload(string reason)
    {
        var sourcePath = ViewModel.VodPath;
        if (string.IsNullOrEmpty(sourcePath)) return false;
        // Already tried repairing THIS source — don't loop.
        if (string.Equals(_repairAttemptedForPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return false;

        _repairAttemptedForPath = sourcePath;
        System.Diagnostics.Debug.WriteLine($"VOD playback failed ({reason}); attempting remux repair of {sourcePath}");

        NoVodText.Text = "This recording needs a quick repair — fixing now…";
        NoVodBorder.Visibility = Visibility.Visible;
        VideoPlayPauseButton.Visibility = Visibility.Collapsed;

        _ = RepairAndReloadAsync(sourcePath);
        return true;
    }

    private async Task RepairAndReloadAsync(string sourcePath)
    {
        try
        {
            var fixedPath = await ViewModel.RepairVodForPlaybackAsync(sourcePath);
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isDisposed || _mediaPlayer == null) return;
                if (string.IsNullOrEmpty(fixedPath) || !System.IO.File.Exists(fixedPath))
                {
                    NoVodText.Text = "This recording couldn't be repaired for playback. "
                        + "The clip exporter can still read it.";
                    NoVodBorder.Visibility = Visibility.Visible;
                    return;
                }

                NoVodBorder.Visibility = Visibility.Collapsed;
                _ = LoadMediaAsync(fixedPath);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"VOD repair/reload failed for {sourcePath}: {ex.Message}");
            DispatcherQueue.TryEnqueue(() =>
            {
                NoVodText.Text = "This recording couldn't be repaired for playback.";
                NoVodBorder.Visibility = Visibility.Visible;
            });
        }
    }

    private async Task LoadMediaAsync(string filePath)
    {
        if (_mediaPlayer == null) return;
        try
        {
            // v2.15.10: post-game lands on the VOD viewer immediately, but the
            // recorder may still be flushing the encoder + writing the moov
            // atom. Opening mid-finalize fires MediaFailed because the MP4
            // index hasn't landed yet. Wait until the file size is stable for
            // a beat — that's the cheapest stability signal and avoids the
            // "click out and back in" workaround.
            // Don't reload the file that's already playing — re-setting Source
            // resets the playback position to 0. (Clip-save re-fires the VM state
            // handler with the same VodPath; switching to a clip passes a
            // different path and still loads.)
            if (string.Equals(_loadedMediaPath, filePath, StringComparison.OrdinalIgnoreCase)
                && _mediaPlayer.Source is not null)
            {
                return;
            }

            await WaitForStableFileAsync(filePath);

            var file = await StorageFile.GetFileFromPathAsync(filePath);
            var source = MediaSource.CreateFromStorageFile(file);
            _mediaPlayer.Source = source;
            _loadedMediaPath = filePath;
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

    private async void OnClipPlaybackRequested(BookmarkItem bookmark)
    {
        if (string.IsNullOrWhiteSpace(bookmark.ClipPath) || !System.IO.File.Exists(bookmark.ClipPath))
        {
            NoVodText.Text = "Clip file not found";
            NoVodBorder.Visibility = Visibility.Visible;
            return;
        }

        NoVodBorder.Visibility = Visibility.Collapsed;
        await LoadMediaAsync(bookmark.ClipPath);
        FocusPlaybackSurface();
    }

    /// <summary>
    /// v2.15.10: poll the file size until two consecutive reads ~400ms apart
    /// match. Caps at ~3s so we don't hang forever on a recorder that never
    /// finishes (then we just try opening anyway and let MediaFailed trigger
    /// the existing error path).
    /// </summary>
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
            catch { return; } // file gone or locked — let the open try and fail
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

        // Reset so re-opening the same game in a later navigation reloads the file.
        _loadedMediaPath = null;
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

    private void UpdatePlayPauseButtons(bool playing)
    {
        PlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        VideoPlayPauseIcon.Glyph = playing ? "\uE769" : "\uE768";
        VideoPlayPauseButton.Visibility =
            ViewModel.HasVod && !ViewModel.IsLoading && NoVodBorder.Visibility != Visibility.Visible && !playing
                ? Visibility.Visible
                : Visibility.Collapsed;
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

    private void OnVideoBorderPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        VideoBorder.BorderBrush = (Brush)Application.Current.Resources["BrightBorderBrush"];
    }

    private void OnVideoBorderPointerExited(object sender, PointerRoutedEventArgs e)
    {
        VideoBorder.BorderBrush = (Brush)Application.Current.Resources["SubtleBorderBrush"];
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        FocusPlaybackSurface();
    }

    private void OnVideoPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (IsInteractiveChildTap(e.OriginalSource as DependencyObject))
        {
            return;
        }

        OnPlayPauseRequested();
        FocusPlaybackSurface();
    }

    private void FocusPlaybackSurface()
    {
        if (!VideoContainer.Focus(FocusState.Programmatic))
        {
            Focus(FocusState.Programmatic);
        }
    }

    private void OnClipButtonClick(object sender, RoutedEventArgs e)
    {
        FocusPlaybackSurface();
    }

    private void OnVideoPlayPauseClick(object sender, RoutedEventArgs e)
    {
        OnPlayPauseRequested();
        FocusPlaybackSurface();
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

    private void OnBookmarkTapped(object sender, TappedRoutedEventArgs e)
    {
        if (IsInteractiveChildTap(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (ResolveBookmarkItem(sender) is { } bookmark)
        {
            ViewModel.SeekToBookmarkCommand.Execute(bookmark);
        }

        FocusPlaybackSurface();
    }

    private void OnBookmarkJumpClick(object sender, RoutedEventArgs e)
    {
        if (ResolveBookmarkItem(sender) is { } bookmark)
        {
            ViewModel.SeekToBookmarkCommand.Execute(bookmark);
        }

        FocusPlaybackSurface();
    }

    private void OnBookmarkShareClick(object sender, RoutedEventArgs e)
    {
        if (ResolveBookmarkItem(sender) is { } bookmark)
        {
            ViewModel.ShareClipCommand.Execute(bookmark);
        }
    }

    private async void OnBookmarkQualityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        var quality = element.Tag as string;

        // Walk up the visual tree to find the BookmarkItem DataContext
        BookmarkItem? bookmark = null;
        DependencyObject? current = element;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is BookmarkItem bm)
            {
                bookmark = bm;
                break;
            }
            current = VisualTreeHelper.GetParent(current);
        }

        if (bookmark is null)
            return;

        await ViewModel.SetBookmarkQualityCommand.ExecuteAsync(new BookmarkQualityUpdateRequest(bookmark, quality));
        FocusPlaybackSurface();
    }

    // ── v2.15.7: ObjectivePicker event wiring ─────────────────────────
    //
    // Custom TextBox+Popup+ListView UserControl replaced AutoSuggestBox here
    // because the WinUI flyout lifecycle caused a two-click-to-select +
    // hover-confusion bug. Now every click on a suggestion commits instantly
    // through TagChosen. Tag rows are flat: Objective headers + indented
    // Prompt children, both committing through the same handler.

    private void OnNewBookmarkTagChosen(object sender, Controls.ObjectivePicker.TagChosenEventArgs e)
    {
        // Both the Quick Bookmark + Clip top-row pickers fire this. They share
        // VM.SelectedObjectiveId + SelectedPromptId as the "next save will use
        // this" slot. Prompt rows carry both ids; Objective rows clear PromptId.
        ViewModel.SelectedObjectiveId = e.Option.ObjectiveId;
        ViewModel.SelectedPromptId = e.Option.Kind == TagOption.OptionKind.Prompt
            ? e.Option.PromptId
            : null;
        UpdateSharedObjectiveLabels();
    }

    private async void OnClipTagChosen(object sender, Controls.ObjectivePicker.TagChosenEventArgs e)
    {
        // Per-saved-clip picker: Payload is the owning BookmarkItem.
        if (e.Payload is not BookmarkItem bookmark) return;

        var newObjectiveId = e.Option.ObjectiveId;
        var newPromptId = e.Option.Kind == TagOption.OptionKind.Prompt
            ? e.Option.PromptId
            : null;

        if (newObjectiveId == bookmark.ObjectiveId && newPromptId == bookmark.PromptId)
        {
            return;
        }

        await ViewModel.SetBookmarkTagCommand.ExecuteAsync(
            new BookmarkTagUpdateRequest(bookmark, newObjectiveId, newPromptId));
    }

    private async void OnEvidenceTagChosen(object sender, Controls.ObjectivePicker.TagChosenEventArgs e)
    {
        if (e.Payload is not EvidenceInboxItem evidence) return;

        var newObjectiveId = e.Option.ObjectiveId;
        if (newObjectiveId == evidence.ObjectiveId)
        {
            return;
        }

        await ViewModel.SetEvidenceObjectiveCommand.ExecuteAsync(
            new EvidenceObjectiveUpdateRequest(evidence, newObjectiveId));
    }

    private async void OnEvidenceStatusClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not EvidenceInboxItem evidence
            || button.Tag is not string status)
        {
            return;
        }

        await ViewModel.SetEvidenceStatusCommand.ExecuteAsync(
            new EvidenceStatusUpdateRequest(evidence, status));
    }

    private async void OnEvidencePolarityClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button
            || button.DataContext is not EvidenceInboxItem evidence
            || button.Tag is not string polarity)
        {
            return;
        }

        await ViewModel.SetEvidencePolarityCommand.ExecuteAsync(
            new EvidencePolarityUpdateRequest(evidence, polarity));
    }

    private async void OnEvidenceNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: EvidenceInboxItem evidence })
        {
            await ViewModel.SaveEvidenceNoteCommand.ExecuteAsync(evidence);
        }
    }

    private void TimelineInboxScroll_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (sender is ScrollViewer scroller)
        {
            ScrollInnerViewer(scroller, e);
        }
    }

    private static void ScrollInnerViewer(ScrollViewer scroller, PointerRoutedEventArgs e)
    {
        if (scroller.ScrollableHeight <= 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(scroller).Properties.MouseWheelDelta;
        var canScroll = delta < 0
            ? scroller.VerticalOffset < scroller.ScrollableHeight
            : scroller.VerticalOffset > 0;

        if (!canScroll)
        {
            return;
        }

        var nextOffset = Math.Clamp(scroller.VerticalOffset - delta, 0, scroller.ScrollableHeight);
        scroller.ChangeView(null, nextOffset, null, disableAnimation: true);
        e.Handled = true;
    }

    private void OnOpenEvidenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EvidenceInboxItem evidence })
        {
            ViewModel.OpenEvidenceCommand.Execute(evidence);
            FocusPlaybackSurface();
        }
    }

    // v2.15.7: both top-of-page pickers share VM.SelectedObjectiveId +
    // SelectedPromptId. When either flips, resolve a display title from the
    // matching TagOption row (preferring the Prompt row when a prompt is
    // selected) and push it into each picker's SelectedTitle DP so the
    // TextBox doubles as current-state display.
    private void UpdateSharedObjectiveLabels()
    {
        var objId = ViewModel.SelectedObjectiveId;
        var promptId = ViewModel.SelectedPromptId;

        string title = "";
        if (promptId is not null)
        {
            var promptRow = ViewModel.TagOptions.FirstOrDefault(t =>
                t.Kind == TagOption.OptionKind.Prompt && t.PromptId == promptId);
            // The box is narrow — showing only the prompt label fits without
            // ellipsis-clipping. Re-opening the dropdown shows the parent
            // objective via the indented row position.
            if (promptRow is not null) title = promptRow.Title;
        }
        if (string.IsNullOrEmpty(title) && objId is not null)
        {
            var objRow = ViewModel.TagOptions.FirstOrDefault(t =>
                t.Kind == TagOption.OptionKind.Objective && t.ObjectiveId == objId);
            if (objRow is not null) title = objRow.Title;
        }
        // v2.15.10: explicit "(no tag)" display when both ids are null. Beats
        // showing a blank pill that looks like "the dropdown didn't load".
        if (string.IsNullOrEmpty(title) && objId is null && promptId is null)
        {
            title = "(no tag)";
        }

        if (NewBookmarkObjectivePicker is not null)
            NewBookmarkObjectivePicker.SelectedTitle = title;
        if (NewClipObjectivePicker is not null)
            NewClipObjectivePicker.SelectedTitle = title;
    }

    private void OnTimelineEventTapped(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is TimelineEvent timelineEvent)
            ViewModel.SeekToEventCommand.Execute(timelineEvent);
        FocusPlaybackSurface();
    }

    private void OnDeleteBookmark(object sender, RoutedEventArgs e)
    {
        var bookmarkId = ResolveBookmarkId(sender);
        if (bookmarkId is > 0)
        {
            ViewModel.DeleteBookmarkCommand.Execute(bookmarkId);
        }

        FocusPlaybackSurface();
    }

    private async void OnBookmarkNoteLostFocus(object sender, RoutedEventArgs e)
    {
        if (ResolveBookmarkItem(sender) is { Id: > 0 } bookmark)
        {
            if (sender is TextBox textBox)
            {
                bookmark.Note = textBox.Text;
            }

            await ViewModel.SaveBookmarkNoteCommand.ExecuteAsync(bookmark);
        }
    }

    private void OnBookmarkNoteTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox { FocusState: FocusState.Unfocused })
        {
            return;
        }

        if (ResolveBookmarkItem(sender) is { Id: > 0 } bookmark)
        {
            if (sender is TextBox textBox)
            {
                bookmark.Note = textBox.Text;
            }

            ViewModel.ScheduleBookmarkNoteSaveCommand.Execute(bookmark);
        }
    }

    private void OnQuickBookmarkKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        ViewModel.AddBookmarkCommand.Execute(null);
        FocusPlaybackSurface();
        e.Handled = true;
    }

    private void OnQuickClipKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !ViewModel.HasClipRange)
        {
            return;
        }

        ViewModel.ExtractClipCommand.Execute(null);
        FocusPlaybackSurface();
        e.Handled = true;
    }

    private async void OnBookmarkNoteKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
        {
            return;
        }

        if (ResolveBookmarkItem(sender) is { Id: > 0 } bookmark)
        {
            if (sender is TextBox textBox)
            {
                bookmark.Note = textBox.Text;
            }

            await ViewModel.SaveBookmarkNoteCommand.ExecuteAsync(bookmark);
            FocusPlaybackSurface();
            e.Handled = true;
        }
    }

    private void OnMuteClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.IsMuted = !_mediaPlayer.IsMuted;
        VolumeIcon.Glyph = _mediaPlayer.IsMuted ? "\uE74F" : VolumeGlyph(_mediaPlayer.Volume);
        FocusPlaybackSurface();
    }

    private void OnFullscreenClick(object sender, RoutedEventArgs e)
    {
        ToggleVideoTheaterMode();
        FocusPlaybackSurface();
    }

    private bool _isFullscreen;
    private static readonly Thickness DefaultVodPagePadding = new(28, 20, 28, 28);
    private static readonly Thickness TheaterVodPagePadding = new(10, 10, 10, 10);

    /// <summary>
    /// v2.16: ensures the window is at least <c>MinVodWindowWidth</c> ×
    /// <c>MinVodWindowHeight</c> so the bookmark column in the 3*,* grid
    /// doesn't clip. Never shrinks a window the user has already sized
    /// larger; just bumps small windows up.
    /// </summary>
    private const int MinVodWindowWidth = 1400;
    private const int MinVodWindowHeight = 800;

    private void EnsureMinimumVodWindowSize()
    {
        try
        {
            var appWindow = App.MainWindow?.AppWindow;
            if (appWindow is null) return;
            if (_isFullscreen) return;

            // FullScreen / Maximized presenter already covers the screen — skip.
            if (appWindow.Presenter is Microsoft.UI.Windowing.FullScreenPresenter) return;
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter op
                && op.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
            {
                return;
            }

            var size = appWindow.Size;
            var targetW = Math.Max(size.Width, MinVodWindowWidth);
            var targetH = Math.Max(size.Height, MinVodWindowHeight);
            if (targetW != size.Width || targetH != size.Height)
            {
                appWindow.Resize(new Windows.Graphics.SizeInt32(targetW, targetH));
            }
        }
        catch
        {
            // Window APIs can throw if the window is closing; swallowing here
            // is fine because the layout still works at any size, just with
            // a clipped bookmark column at small widths.
        }
    }

    private void ToggleVideoTheaterMode()
    {
        ExitAppWindowFullscreenIfNeeded();

        if (_isFullscreen)
        {
            ExitVideoTheaterMode();
        }
        else
        {
            EnterVideoTheaterMode();
        }
    }

    private void EnterVideoTheaterMode()
    {
        _isFullscreen = true;
        SetShellSidebarVisible(false);
        VodHeader.Visibility = Visibility.Collapsed;
        BookmarkSidebar.Visibility = Visibility.Collapsed;
        TimelineBar.Visibility = Visibility.Collapsed;
        FullscreenTimelineOverlay.Visibility = Visibility.Visible;
        VodMainLayout.ColumnSpacing = 0;
        VideoColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
        SidebarColumnDefinition.Width = new GridLength(0);
        SidebarColumnDefinition.MinWidth = 0;
        VodPageLayout.Padding = TheaterVodPagePadding;
        VideoColumnScrollViewer.SetValue(Grid.ColumnSpanProperty, 2);
        VideoColumnStack.Padding = new Thickness(0);
        VideoBorder.Padding = new Thickness(0);
        VideoContainer.MinHeight = Math.Max(520, RootGrid.ActualHeight - TransportBar.ActualHeight - 56);
        VideoPlayPauseButton.Margin = new Thickness(0, 0, 0, 126);
        FullscreenIcon.Glyph = "\uE73F"; // ExitFullScreen
        UpdateFullscreenTimelineCompactness();
    }

    private void ExitVideoTheaterMode()
    {
        _isFullscreen = false;
        SetShellSidebarVisible(true);
        VodHeader.Visibility = Visibility.Visible;
        BookmarkSidebar.Visibility = Visibility.Visible;
        TimelineBar.Visibility = Visibility.Visible;
        FullscreenTimelineOverlay.Visibility = Visibility.Collapsed;
        // Reset overlay's child timeline so re-entering fullscreen on a paused
        // video doesn't start in compact mode.
        FullscreenTimeline.IsCompact = false;
        FullscreenTimeline.Height = 80;
        FullscreenTimelineOverlay.Padding = new Thickness(10, 8, 10, 8);
        VodMainLayout.ColumnSpacing = 16;
        VideoColumnDefinition.Width = new GridLength(2.4, GridUnitType.Star);
        SidebarColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
        SidebarColumnDefinition.MinWidth = 380;
        VodPageLayout.Padding = DefaultVodPagePadding;
        VideoColumnScrollViewer.SetValue(Grid.ColumnSpanProperty, 1);
        VideoColumnStack.Padding = new Thickness(0, 0, 0, 40);
        VideoBorder.Padding = new Thickness(6);
        VideoContainer.MinHeight = 420;
        VideoPlayPauseButton.Margin = new Thickness(0, 0, 0, 24);
        FullscreenIcon.Glyph = "\uE740"; // EnterFullScreen
    }

    /// <summary>
    /// v2.17.8: collapse / restore the global Revu sidebar via ShellPage so
    /// theater mode actually extends the video to the left window edge.
    /// Best-effort: if the ShellPage instance can't be reached (window is
    /// closing, content swapped, etc.) the page still works, the user just
    /// keeps seeing the sidebar.
    /// </summary>
    private static void SetShellSidebarVisible(bool visible)
    {
        try
        {
            if (App.MainWindow?.Content is ShellPage shell)
            {
                shell.SetSidebarVisible(visible);
            }
        }
        catch
        {
            // Non-fatal — theater mode still works without the sidebar collapse.
        }
    }

    private static void ExitAppWindowFullscreenIfNeeded()
    {
        try
        {
            var appWindow = App.MainWindow?.AppWindow;
            if (appWindow?.Presenter is Microsoft.UI.Windowing.FullScreenPresenter)
            {
                appWindow.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Default);
            }
        }
        catch
        {
            // Best-effort recovery for users already in the old window-level fullscreen mode.
        }
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

    private static bool IsInteractiveChildTap(DependencyObject? source)
    {
        var current = source;
        while (current != null)
        {
            // v2.17.8: TimelineControl gets the same "don't toggle play/pause"
            // treatment as buttons. The fullscreen timeline overlay sits INSIDE
            // VideoContainer, so its pointer events bubble up here; without this
            // the scrub click would also pause the video.
            if (current is Button or TextBox or Controls.TimelineControl)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static BookmarkItem? ResolveBookmarkItem(object sender)
    {
        if (sender is not FrameworkElement element)
        {
            return null;
        }

        return element.Tag as BookmarkItem ?? element.DataContext as BookmarkItem;
    }

    private static long? ResolveBookmarkId(object sender)
    {
        if (sender is not Button button)
        {
            return null;
        }

        return button.CommandParameter switch
        {
            long bookmarkId => bookmarkId,
            int bookmarkId => bookmarkId,
            BookmarkItem bookmark => bookmark.Id,
            _ => (button.DataContext as BookmarkItem)?.Id
        };
    }

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
            case Windows.System.VirtualKey.S:
                if (ViewModel.HasClipRange)
                    ViewModel.ExtractClipCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.B:
                ViewModel.AddBookmarkCommand.Execute(null);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.F:
                ToggleVideoTheaterMode();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.Escape:
                if (_isFullscreen)
                {
                    ExitVideoTheaterMode();
                    e.Handled = true;
                }
                break;
        }
    }

    /// <summary>
    /// v2.16: user-driven retry from the "No VOD linked" empty state. Asks
    /// VodService to scan again and reload the page if a match was found.
    /// Falls back to a friendly status if nothing turned up.
    /// </summary>
    private async void OnRetryVodLinkClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            btn.IsEnabled = false;
            btn.Content = "Linking...";
        }

        try
        {
            if (ViewModel.LoadCommand.CanExecute(ViewModel.GameId))
            {
                await ViewModel.LoadCommand.ExecuteAsync(ViewModel.GameId);
            }

            if (ViewModel.HasVod)
            {
                TryLoadMedia();
            }
            else if (sender is Button b)
            {
                b.Content = "Still no match — try scanning in Settings";
                // Re-enable so the user can click again after Ascent finishes.
                b.IsEnabled = true;
            }
        }
        catch
        {
            if (sender is Button b)
            {
                b.Content = "Try linking again";
                b.IsEnabled = true;
            }
        }
    }

    private void OnOpenSettingsFromNoVodClick(object sender, RoutedEventArgs e)
    {
        App.GetService<Revu.App.Contracts.INavigationService>().NavigateTo("settings");
    }
}
