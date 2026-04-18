#nullable enable

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Styling;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the VOD player page.</summary>
public partial class VodPlayerViewModel : ObservableObject
{
    private static readonly TimeSpan BookmarkNoteSaveDebounce = TimeSpan.FromMilliseconds(650);

    private readonly IVodRepository _vodRepo;
    private readonly IGameRepository _gameRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IDerivedEventsRepository _derivedEventsRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IClipService _clipService;
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<VodPlayerViewModel> _logger;
    private readonly object _bookmarkMutationQueueGate = new();
    private readonly object _bookmarkNoteSaveGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _bookmarkNoteSaveDelays = [];
    private Task _bookmarkMutationQueueTail = Task.CompletedTask;

    // â"€â"€ Game info â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _championName = "";
    [ObservableProperty] private bool _win;
    [ObservableProperty] private string _headerText = "VOD Review";
    [ObservableProperty] private string _vodPath = "";
    [ObservableProperty] private int _gameDurationS;

    // â"€â"€ Playback state â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _currentTimeS;
    [ObservableProperty] private string _currentTimeText = "0:00";
    [ObservableProperty] private string _totalTimeText = "0:00";
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private int _seekStepSeconds = 10;
    [ObservableProperty] private bool _hasVod;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasGameEvents;
    [ObservableProperty] private bool _showNoEventsHint;
    [ObservableProperty] private string _gameEventsStatusText = "No live events.";

    // â"€â"€ Clip extraction â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [ObservableProperty] private double _clipStartS = -1;
    [ObservableProperty] private double _clipEndS = -1;
    [ObservableProperty] private bool _hasClipRange;
    [ObservableProperty] private string _clipRangeText = "";
    [ObservableProperty] private string _clipDurationText = "";
    [ObservableProperty] private bool _hasFfmpeg;
    [ObservableProperty] private bool _isExtractingClip;
    [ObservableProperty] private string _clipStatusText = "Start, end, save.";
    [ObservableProperty] private string _bookmarkNote = "";
    [ObservableProperty] private string _clipNote = "";
    [ObservableProperty] private long? _selectedObjectiveId;
    [ObservableProperty] private string _selectedClipQuality = "";

    public IReadOnlyList<string> QualityOptions { get; } =
        ["", "good", "neutral", "bad"];

    // â"€â"€ Collections â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<TimelineEvent> GameEvents { get; } = new();
    public ObservableCollection<DerivedEventRegion> DerivedEvents { get; } = new();
    public ObservableCollection<ObjectiveOption> ObjectiveOptions { get; } = new();

    public static IReadOnlyList<double> SpeedOptions { get; } =
        new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };

    public static IReadOnlyList<int> SeekStepOptions { get; } =
        new[] { 1, 2, 5, 10, 15, 30, 60 };

    public string SeekStepText => $"{SeekStepSeconds}s";
    public string SeekStepHintText => $"Left/Right {SeekStepText} | Up/Down step";
    public string ClipStartActionText => ClipStartS >= 0 ? "Move Start" : "Start Clip";
    public string ClipEndActionText => ClipEndS >= 0 ? "Move End" : "End Clip";
    public string SelectedClipQualityText => string.IsNullOrWhiteSpace(SelectedClipQuality)
        ? "Select Good, Neutral, or Bad before saving."
        : $"{char.ToUpperInvariant(SelectedClipQuality[0])}{SelectedClipQuality[1..]} selected. Save Clip to apply it.";
    public QualityChipVisual GoodClipQualityVisual => QualityChipVisual.Create("good", SelectedClipQuality);
    public QualityChipVisual NeutralClipQualityVisual => QualityChipVisual.Create("neutral", SelectedClipQuality);
    public QualityChipVisual BadClipQualityVisual => QualityChipVisual.Create("bad", SelectedClipQuality);
    public SolidColorBrush GoodClipBackgroundBrush => GoodClipQualityVisual.BackgroundBrush;
    public SolidColorBrush GoodClipBorderBrush => GoodClipQualityVisual.BorderBrush;
    public SolidColorBrush GoodClipForegroundBrush => GoodClipQualityVisual.ForegroundBrush;
    public Visibility GoodClipCheckVisibility => GoodClipQualityVisual.CheckVisibility;
    public Thickness GoodClipBorderThickness => GoodClipQualityVisual.BorderThickness;
    public SolidColorBrush NeutralClipBackgroundBrush => NeutralClipQualityVisual.BackgroundBrush;
    public SolidColorBrush NeutralClipBorderBrush => NeutralClipQualityVisual.BorderBrush;
    public SolidColorBrush NeutralClipForegroundBrush => NeutralClipQualityVisual.ForegroundBrush;
    public Visibility NeutralClipCheckVisibility => NeutralClipQualityVisual.CheckVisibility;
    public Thickness NeutralClipBorderThickness => NeutralClipQualityVisual.BorderThickness;
    public SolidColorBrush BadClipBackgroundBrush => BadClipQualityVisual.BackgroundBrush;
    public SolidColorBrush BadClipBorderBrush => BadClipQualityVisual.BorderBrush;
    public SolidColorBrush BadClipForegroundBrush => BadClipQualityVisual.ForegroundBrush;
    public Visibility BadClipCheckVisibility => BadClipQualityVisual.CheckVisibility;
    public Thickness BadClipBorderThickness => BadClipQualityVisual.BorderThickness;

    // â"€â"€ Events for the view â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public string OutcomeLabel => Win ? "Victory" : "Defeat";
    public string VodStatusLabel => HasVod ? "VOD linked" : "No recording";
    public string PlaybackStateLabel => IsPlaying ? "Playing" : "Paused";

    /// <summary>Raised when the view should seek the media player.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Raised when playback speed should change.</summary>
    public event Action<double>? SpeedChangeRequested;

    /// <summary>Raised when play/pause should toggle.</summary>
    public event Action? PlayPauseRequested;

    // â"€â"€ Constructor â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public VodPlayerViewModel(
        IVodRepository vodRepo,
        IGameRepository gameRepo,
        IGameEventsRepository eventsRepo,
        IDerivedEventsRepository derivedEventsRepo,
        IObjectivesRepository objectivesRepo,
        IClipService clipService,
        IConfigService configService,
        INavigationService navigationService,
        ILogger<VodPlayerViewModel> logger)
    {
        _vodRepo = vodRepo;
        _gameRepo = gameRepo;
        _eventsRepo = eventsRepo;
        _derivedEventsRepo = derivedEventsRepo;
        _objectivesRepo = objectivesRepo;
        _clipService = clipService;
        _configService = configService;
        _navigationService = navigationService;
        _logger = logger;
    }

    // â"€â"€ Load â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading) return;
        IsLoading = true;

        try
        {
            GameId = gameId;

            // Load game info
            var game = await _gameRepo.GetAsync(gameId);
            if (game == null) { _logger.LogWarning("Game {Id} not found", gameId); return; }

            ChampionName = game.ChampionName;
            Win = game.Win;
            HeaderText = $"VOD Review - {game.ChampionName} ({(game.Win ? "W" : "L")})";
            GameDurationS = game.GameDuration;
            TotalTimeText = FormatTime(game.GameDuration);

            // Load VOD metadata
            var vod = await _vodRepo.GetVodAsync(gameId);
            if (vod == null) { HasVod = false; return; }

            HasVod = true;
            VodPath = vod.FilePath;

            if (vod.DurationSeconds > 0)
            {
                GameDurationS = vod.DurationSeconds;
                TotalTimeText = FormatTime(vod.DurationSeconds);
            }

            // Load game events for timeline
            var events = await _eventsRepo.GetEventsAsync(gameId);
            DispatcherHelper.RunOnUIThread(() =>
            {
                GameEvents.Clear();
                foreach (var e in events)
                {
                    GameEvents.Add(new TimelineEvent
                    {
                        EventType = e.EventType,
                        GameTimeS = e.GameTimeS,
                        Details = e.Details,
                    });
                }

                HasGameEvents = GameEvents.Count > 0;
                ShowNoEventsHint = !HasGameEvents;
                GameEventsStatusText = HasGameEvents
                    ? $"{GameEvents.Count} event(s). Click a marker to jump."
                    : "No live events.";
            });

            // Load derived events for timeline regions
            var derived = await _derivedEventsRepo.GetInstancesAsync(gameId);
            DispatcherHelper.RunOnUIThread(() =>
            {
                DerivedEvents.Clear();
                foreach (var de in derived)
                {
                    DerivedEvents.Add(new DerivedEventRegion
                    {
                        StartTimeS = de.StartTimeSeconds,
                        EndTimeS = de.EndTimeSeconds,
                        Color = de.Color,
                        Name = de.DefinitionName,
                    });
                }
            });

            // Load bookmarks
            await RefreshBookmarksAsync();

            // Load active objectives for clip attachment
            await LoadObjectiveOptionsAsync();

            // Check ffmpeg availability
            var ffmpegPath = await _clipService.FindFfmpegAsync();
            HasFfmpeg = !string.IsNullOrEmpty(ffmpegPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load VOD for game {Id}", gameId);
        }
        finally
        {
            IsLoading = false;
        }
    }

    // â"€â"€ Playback commands â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private void PlayPause()
    {
        PlayPauseRequested?.Invoke();
    }

    [RelayCommand]
    private void SeekForward()
    {
        SeekRequested?.Invoke(CurrentTimeS + SeekStepSeconds);
    }

    [RelayCommand]
    private void SeekBackward()
    {
        SeekRequested?.Invoke(Math.Max(0, CurrentTimeS - SeekStepSeconds));
    }

    [RelayCommand]
    private void SeekTo(double seconds)
    {
        SeekRequested?.Invoke(Math.Clamp(seconds, 0, GameDurationS));
    }

    [RelayCommand]
    private void SetSpeed(double speed)
    {
        PlaybackSpeed = speed;
        SpeedChangeRequested?.Invoke(speed);
    }

    [RelayCommand]
    private void IncreaseSeekStep()
    {
        AdjustSeekStep(1);
    }

    [RelayCommand]
    private void DecreaseSeekStep()
    {
        AdjustSeekStep(-1);
    }

    // â"€â"€ Bookmark commands â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        var timeS = (int)CurrentTimeS;
        var note = BookmarkNote.Trim();
        var objectiveId = SelectedObjectiveId;
        BookmarkNote = "";

        try
        {
            var bookmarkId = await EnqueueBookmarkMutationAsync(
                () => _vodRepo.AddBookmarkAsync(GameId, timeS, note, objectiveId: objectiveId));

            InsertBookmark(new BookmarkItem
            {
                Id = bookmarkId,
                GameTimeS = timeS,
                TimeText = FormatTime(timeS),
                Note = note,
                IsClip = false,
            });
            _logger.LogInformation("Bookmark added at {Time}s for game {Id}", timeS, GameId);
        }
        catch (Exception ex)
        {
            BookmarkNote = note;
            _logger.LogError(ex, "Failed to add bookmark");
        }
    }

    [RelayCommand]
    private Task DeleteBookmarkAsync(long bookmarkId)
    {
        var bookmark = Bookmarks.FirstOrDefault(item => item.Id == bookmarkId);
        var bookmarkKind = bookmark?.IsClip == true ? "clip" : "note";

        CancelPendingBookmarkNoteSave(bookmarkId);
        DispatcherHelper.RunOnUIThread(() =>
        {
            if (bookmark is not null)
            {
                Bookmarks.Remove(bookmark);
            }
        });

        AppDiagnostics.WriteVerbose(
            "vod-delete.log",
            $"delete queued bookmarkId={bookmarkId} kind={bookmarkKind} gameId={GameId}");
        _logger.LogInformation(
            "Queued deletion for {Kind} bookmark {BookmarkId} in game {GameId}",
            bookmarkKind,
            bookmarkId,
            GameId);

        _ = DeleteBookmarkQueuedAsync(bookmarkId, bookmark);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task SaveBookmarkNoteAsync(BookmarkItem? bookmark)
    {
        QueueBookmarkNoteSave(bookmark, immediate: true);
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void ScheduleBookmarkNoteSave(BookmarkItem? bookmark)
    {
        QueueBookmarkNoteSave(bookmark, immediate: false);
    }

    [RelayCommand]
    private Task SetBookmarkQualityAsync(BookmarkQualityUpdateRequest? request)
    {
        if (request is null || request.Bookmark is null || request.Bookmark.Id <= 0 || !request.Bookmark.IsClip)
        {
            return Task.CompletedTask;
        }

        var normalizedQuality = NormalizeClipQuality(request.Quality);
        var originalBookmark = request.Bookmark;
        var updatedBookmark = originalBookmark.WithQuality(normalizedQuality);
        DispatcherHelper.RunOnUIThread(() =>
        {
            var index = Bookmarks.IndexOf(originalBookmark);
            if (index < 0)
            {
                index = FindBookmarkIndex(originalBookmark.Id);
            }

            if (index >= 0)
            {
                Bookmarks[index] = updatedBookmark;
            }
        });

        _ = SetBookmarkQualityQueuedAsync(originalBookmark, normalizedQuality);
        return Task.CompletedTask;
    }

    private async Task DeleteBookmarkQueuedAsync(long bookmarkId, BookmarkItem? bookmark)
    {
        try
        {
            await EnqueueBookmarkMutationAsync(() => _vodRepo.DeleteBookmarkAsync(bookmarkId));
            AppDiagnostics.WriteVerbose(
                "vod-delete.log",
                $"delete completed bookmarkId={bookmarkId} remaining={Bookmarks.Count}");
            _logger.LogInformation("Deleted bookmark {BookmarkId}", bookmarkId);
        }
        catch (Exception ex)
        {
            if (bookmark is not null)
            {
                InsertBookmark(bookmark);
            }

            AppDiagnostics.WriteVerbose(
                "vod-delete.log",
                $"delete failed bookmarkId={bookmarkId} error={ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Failed to delete bookmark {Id}", bookmarkId);
        }
    }

    private async Task SetBookmarkQualityQueuedAsync(BookmarkItem originalBookmark, string normalizedQuality)
    {
        try
        {
            await EnqueueBookmarkMutationAsync(
                () => _vodRepo.UpdateBookmarkAsync(originalBookmark.Id, quality: normalizedQuality));
        }
        catch (Exception ex)
        {
            RestoreBookmarkIfQualityStillMatches(originalBookmark, normalizedQuality);
            _logger.LogError(ex, "Failed to save quality for bookmark {Id}", originalBookmark.Id);
        }
    }

    [RelayCommand]
    private void SeekToBookmark(BookmarkItem bookmark)
    {
        SeekRequested?.Invoke(bookmark.GameTimeS);
    }

    [RelayCommand]
    private void SeekToEvent(TimelineEvent? timelineEvent)
    {
        if (timelineEvent is null)
        {
            return;
        }

        SeekRequested?.Invoke(timelineEvent.GameTimeS);
    }

    // â"€â"€ Clip commands â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private void SetClipIn()
    {
        ClipStartS = CurrentTimeS;
        UpdateClipRange();
    }

    [RelayCommand]
    private void SetClipOut()
    {
        ClipEndS = CurrentTimeS;
        UpdateClipRange();
    }

    [RelayCommand]
    private void ClearClip()
    {
        ClipStartS = -1;
        ClipEndS = -1;
        HasClipRange = false;
        ClipRangeText = "";
        ClipDurationText = "";
        ClipStatusText = "Clip cleared.";
    }

    [RelayCommand]
    private async Task ExtractClipAsync()
    {
        if (!HasClipRange || ClipStartS < 0 || ClipEndS < 0) return;
        if (IsExtractingClip) return;

        IsExtractingClip = true;
        ClipStatusText = "Saving clip...";

        var startS = (int)Math.Min(ClipStartS, ClipEndS);
        var endS = (int)Math.Max(ClipStartS, ClipEndS);
        var note = string.IsNullOrWhiteSpace(ClipNote) ? "Clip" : ClipNote.Trim();
        var quality = SelectedClipQuality;
        var objectiveId = SelectedObjectiveId;

        try
        {
            var clipsFolder = _configService.ClipsFolder;

            var clipPath = await _clipService.ExtractClipAsync(
                VodPath, startS, endS, ChampionName, clipsFolder);

            if (!string.IsNullOrEmpty(clipPath))
            {
                var bookmarkId = await EnqueueBookmarkMutationAsync(
                    () => _vodRepo.AddBookmarkAsync(
                        GameId,
                        startS,
                        note,
                        clipStartSeconds: startS,
                        clipEndSeconds: endS,
                        clipPath: clipPath,
                        objectiveId: objectiveId,
                        quality: quality));

                InsertBookmark(new BookmarkItem
                {
                    Id = bookmarkId,
                    GameTimeS = startS,
                    TimeText = FormatTime(startS),
                    Note = note,
                    IsClip = true,
                    ClipRangeText = $"{FormatTime(startS)} - {FormatTime(endS)}",
                    Quality = quality,
                });

    
                ClipNote = "";
                ClearClip();
                ClipStatusText = string.IsNullOrWhiteSpace(quality)
                    ? "Clip saved."
                    : $"Clip saved as {quality.Trim().ToLowerInvariant()}.";
                _logger.LogInformation("Clip extracted: {Path}", clipPath);
            }
            else
            {
                ClipStatusText = "Clip save failed.";
            }
        }
        catch (Exception ex)
        {
            ClipStatusText = "Clip save error.";
            _logger.LogError(ex, "Clip extraction failed");
        }
        finally
        {
            IsExtractingClip = false;
        }
    }

    // â"€â"€ Navigation â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }

    // â"€â"€ Public methods for the view â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    /// <summary>Called by the view's position timer to update display.</summary>
    public void UpdatePosition(double seconds, double totalSeconds)
    {
        CurrentTimeS = seconds;
        CurrentTimeText = FormatTime((int)seconds);

        if (totalSeconds > 0 && GameDurationS == 0)
        {
            GameDurationS = (int)totalSeconds;
            TotalTimeText = FormatTime((int)totalSeconds);
        }
    }

    // â"€â"€ Helpers â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    private async Task RefreshBookmarksAsync()
    {
        var bookmarks = await _vodRepo.GetBookmarksAsync(GameId);
        DispatcherHelper.RunOnUIThread(() =>
        {
            Bookmarks.Clear();
            foreach (var b in bookmarks)
            {
                Bookmarks.Add(ToBookmarkItem(b));
            }
        });
    }

    private void QueueBookmarkNoteSave(BookmarkItem? bookmark, bool immediate)
    {
        if (bookmark is null || bookmark.Id <= 0)
        {
            return;
        }

        var bookmarkId = bookmark.Id;
        var note = bookmark.Note?.Trim() ?? "";
        var isClip = bookmark.IsClip;
        CancellationTokenSource saveDelay;

        lock (_bookmarkNoteSaveGate)
        {
            if (_bookmarkNoteSaveDelays.Remove(bookmarkId, out var existing))
            {
                existing.Cancel();
            }

            saveDelay = new CancellationTokenSource();
            _bookmarkNoteSaveDelays[bookmarkId] = saveDelay;
        }

        _ = SaveBookmarkNoteQueuedAsync(bookmarkId, note, isClip, immediate, saveDelay);
    }

    private async Task SaveBookmarkNoteQueuedAsync(
        long bookmarkId,
        string note,
        bool isClip,
        bool immediate,
        CancellationTokenSource saveDelay)
    {
        try
        {
            if (!immediate)
            {
                await Task.Delay(BookmarkNoteSaveDebounce, saveDelay.Token).ConfigureAwait(false);
            }

            await EnqueueBookmarkMutationAsync(
                () => _vodRepo.UpdateBookmarkAsync(bookmarkId, note: note)).ConfigureAwait(false);

            if (isClip)
            {
                }
        }
        catch (OperationCanceledException)
        {
            // A newer note value superseded this pending save.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save note for bookmark {Id}", bookmarkId);
        }
        finally
        {
            lock (_bookmarkNoteSaveGate)
            {
                if (_bookmarkNoteSaveDelays.TryGetValue(bookmarkId, out var current)
                    && ReferenceEquals(current, saveDelay))
                {
                    _bookmarkNoteSaveDelays.Remove(bookmarkId);
                }
            }

            saveDelay.Dispose();
        }
    }

    private void CancelPendingBookmarkNoteSave(long bookmarkId)
    {
        lock (_bookmarkNoteSaveGate)
        {
            if (_bookmarkNoteSaveDelays.Remove(bookmarkId, out var existing))
            {
                existing.Cancel();
            }
        }
    }

    private Task EnqueueBookmarkMutationAsync(Func<Task> mutation)
    {
        lock (_bookmarkMutationQueueGate)
        {
            var previous = _bookmarkMutationQueueTail;
            var next = Task.Run(() => RunQueuedBookmarkMutationAsync(previous, mutation));
            _bookmarkMutationQueueTail = next;
            return next;
        }
    }

    private Task<T> EnqueueBookmarkMutationAsync<T>(Func<Task<T>> mutation)
    {
        lock (_bookmarkMutationQueueGate)
        {
            var previous = _bookmarkMutationQueueTail;
            var next = Task.Run(() => RunQueuedBookmarkMutationAsync(previous, mutation));
            _bookmarkMutationQueueTail = next;
            return next;
        }
    }

    private async Task RunQueuedBookmarkMutationAsync(Task previous, Func<Task> mutation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Previous queued VOD bookmark mutation failed");
        }

        await mutation().ConfigureAwait(false);
    }

    private async Task<T> RunQueuedBookmarkMutationAsync<T>(Task previous, Func<Task<T>> mutation)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Previous queued VOD bookmark mutation failed");
        }

        return await mutation().ConfigureAwait(false);
    }

    private void InsertBookmark(BookmarkItem bookmark)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            InsertBookmarkOnCurrentThread(bookmark);
        });
    }

    private void ReplaceBookmark(BookmarkItem bookmark)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            var index = FindBookmarkIndex(bookmark.Id);
            if (index >= 0)
            {
                Bookmarks[index] = bookmark;
            }
            else
            {
                InsertBookmarkOnCurrentThread(bookmark);
            }
        });
    }

    private void RestoreBookmarkIfQualityStillMatches(BookmarkItem bookmark, string failedQuality)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            var index = FindBookmarkIndex(bookmark.Id);
            if (index >= 0 && NormalizeClipQuality(Bookmarks[index].Quality) == failedQuality)
            {
                Bookmarks[index] = bookmark;
            }
        });
    }

    private int FindBookmarkIndex(long bookmarkId)
    {
        for (var i = 0; i < Bookmarks.Count; i++)
        {
            if (Bookmarks[i].Id == bookmarkId)
            {
                return i;
            }
        }

        return -1;
    }

    private void InsertBookmarkOnCurrentThread(BookmarkItem bookmark)
    {
        var index = 0;
        while (index < Bookmarks.Count && Bookmarks[index].GameTimeS <= bookmark.GameTimeS)
        {
            index++;
        }

        Bookmarks.Insert(index, bookmark);
    }

    private static BookmarkItem ToBookmarkItem(VodBookmarkRecord record)
    {
        var isClip = !string.IsNullOrEmpty(record.ClipPath);
        return new BookmarkItem
        {
            Id = record.Id,
            GameTimeS = record.GameTimeSeconds,
            TimeText = FormatTime(record.GameTimeSeconds),
            Note = record.Note,
            IsClip = isClip,
            ClipRangeText = record.ClipStartSeconds != null && record.ClipEndSeconds != null
                ? $"{FormatTime(record.ClipStartSeconds.Value)} - {FormatTime(record.ClipEndSeconds.Value)}"
                : "",
            Quality = record.Quality,
        };
    }

    private void UpdateClipRange()
    {
        if (ClipStartS >= 0 && ClipEndS >= 0)
        {
            var startS = Math.Min(ClipStartS, ClipEndS);
            var endS = Math.Max(ClipStartS, ClipEndS);
            var duration = endS - startS;

            HasClipRange = duration >= 1;
            ClipRangeText = $"{FormatTime((int)startS)} - {FormatTime((int)endS)}";
            ClipDurationText = $"{FormatTime((int)duration)}";
            ClipStatusText = HasClipRange
                ? "Clip ready."
                : "Clip too short.";
        }
        else
        {
            HasClipRange = false;
            ClipRangeText = ClipStartS >= 0
                ? $"{FormatTime((int)ClipStartS)} - ?"
                : ClipEndS >= 0
                    ? $"? - {FormatTime((int)ClipEndS)}"
                    : "";
            ClipDurationText = "";
            ClipStatusText = ClipStartS >= 0
                ? "Start set. Move forward and end the clip."
                : ClipEndS >= 0
                    ? "End set. Move back and set the start."
                    : "Start, end, save.";
        }
    }

    internal static string FormatTime(int totalSeconds)
    {
        var m = totalSeconds / 60;
        var s = totalSeconds % 60;
        return $"{m}:{s:D2}";
    }

    private async Task LoadObjectiveOptionsAsync()
    {
        try
        {
            var objectives = await _objectivesRepo.GetActiveAsync();
            DispatcherHelper.RunOnUIThread(() =>
            {
                ObjectiveOptions.Clear();
                ObjectiveOptions.Add(new ObjectiveOption(null, "(none)"));
                foreach (var obj in objectives)
                {
                    ObjectiveOptions.Add(new ObjectiveOption(obj.Id, $"{obj.Title} ({ObjectivePhases.ToDisplayLabel(obj.Phase)})"));
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load objectives for VOD player");
        }
    }

    partial void OnSeekStepSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(SeekStepText));
        OnPropertyChanged(nameof(SeekStepHintText));
    }

    partial void OnClipStartSChanged(double value)
    {
        OnPropertyChanged(nameof(ClipStartActionText));
    }

    partial void OnClipEndSChanged(double value)
    {
        OnPropertyChanged(nameof(ClipEndActionText));
    }

    partial void OnSelectedClipQualityChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedClipQualityText));
        OnPropertyChanged(nameof(GoodClipQualityVisual));
        OnPropertyChanged(nameof(NeutralClipQualityVisual));
        OnPropertyChanged(nameof(BadClipQualityVisual));
        OnPropertyChanged(nameof(GoodClipBackgroundBrush));
        OnPropertyChanged(nameof(GoodClipBorderBrush));
        OnPropertyChanged(nameof(GoodClipForegroundBrush));
        OnPropertyChanged(nameof(GoodClipCheckVisibility));
        OnPropertyChanged(nameof(GoodClipBorderThickness));
        OnPropertyChanged(nameof(NeutralClipBackgroundBrush));
        OnPropertyChanged(nameof(NeutralClipBorderBrush));
        OnPropertyChanged(nameof(NeutralClipForegroundBrush));
        OnPropertyChanged(nameof(NeutralClipCheckVisibility));
        OnPropertyChanged(nameof(NeutralClipBorderThickness));
        OnPropertyChanged(nameof(BadClipBackgroundBrush));
        OnPropertyChanged(nameof(BadClipBorderBrush));
        OnPropertyChanged(nameof(BadClipForegroundBrush));
        OnPropertyChanged(nameof(BadClipCheckVisibility));
        OnPropertyChanged(nameof(BadClipBorderThickness));
    }

    partial void OnWinChanged(bool value) => OnPropertyChanged(nameof(OutcomeLabel));

    partial void OnHasVodChanged(bool value) => OnPropertyChanged(nameof(VodStatusLabel));

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlaybackStateLabel));

    private void AdjustSeekStep(int direction)
    {
        var currentIndex = -1;
        for (var i = 0; i < SeekStepOptions.Count; i++)
        {
            if (SeekStepOptions[i] == SeekStepSeconds)
            {
                currentIndex = i;
                break;
            }
        }

        if (currentIndex < 0)
        {
            SeekStepSeconds = 10;
            return;
        }

        var nextIndex = Math.Clamp(currentIndex + direction, 0, SeekStepOptions.Count - 1);
        SeekStepSeconds = SeekStepOptions[nextIndex];
        ClipStatusText = $"Seek step: {SeekStepText}";
    }

    [RelayCommand]
    private void SetClipQuality(string? quality)
    {
        SelectedClipQuality = NormalizeClipQuality(quality);
        if (!string.IsNullOrWhiteSpace(SelectedClipQuality))
        {
            ClipStatusText = $"{char.ToUpperInvariant(SelectedClipQuality[0])}{SelectedClipQuality[1..]} tag selected.";
        }
    }

    [RelayCommand]
    private void ClearClipQuality()
    {
        SelectedClipQuality = "";
    }

    private static string NormalizeClipQuality(string? quality)
    {
        var normalized = (quality ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "good" or "neutral" or "bad"
            ? normalized
            : "";
    }
}

// â"€â"€ Display models â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

public class BookmarkItem
{
    public long Id { get; set; }
    public int GameTimeS { get; set; }
    public string TimeText { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsClip { get; set; }
    public string ClipRangeText { get; set; } = "";
    public string Quality { get; set; } = "";
    public string KindLabel => IsClip ? "CLIP" : "NOTE";
    public bool HasQuality => !string.IsNullOrWhiteSpace(Quality);
    public string QualityLabel => string.IsNullOrWhiteSpace(Quality)
        ? ""
        : char.ToUpperInvariant(Quality.Trim()[0]) + Quality.Trim()[1..].ToLowerInvariant();
    public string MarkerColorHex => IsClip ? QualityAccentHex : AppSemanticPalette.NeutralHex;
    public SolidColorBrush AccentBrush => AppSemanticPalette.Brush(MarkerColorHex);
    public SolidColorBrush SurfaceBrush => IsClip
        ? AppSemanticPalette.Brush(QualitySurfaceHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);

    public SolidColorBrush QualityAccentBrush => AppSemanticPalette.Brush(QualityAccentHex);
    public SolidColorBrush QualitySurfaceBrush => AppSemanticPalette.Brush(QualitySurfaceHex);
    public QualityChipVisual GoodQualityVisual => QualityChipVisual.Create("good", Quality);
    public QualityChipVisual NeutralQualityVisual => QualityChipVisual.Create("neutral", Quality);
    public QualityChipVisual BadQualityVisual => QualityChipVisual.Create("bad", Quality);

    private string QualityAccentHex => NormalizeQuality(Quality) switch
    {
        "good" => AppSemanticPalette.PositiveHex,
        "bad" => AppSemanticPalette.NegativeHex,
        "neutral" => AppSemanticPalette.AccentGoldHex,
        _ => AppSemanticPalette.AccentGoldHex,
    };

    private string QualitySurfaceHex => NormalizeQuality(Quality) switch
    {
        "good" => AppSemanticPalette.PositiveDimHex,
        "bad" => AppSemanticPalette.NegativeDimHex,
        "neutral" => AppSemanticPalette.AccentGoldDimHex,
        _ => AppSemanticPalette.AccentGoldDimHex,
    };

    private static string NormalizeQuality(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();

    public BookmarkItem WithQuality(string quality)
    {
        return new BookmarkItem
        {
            Id = Id,
            GameTimeS = GameTimeS,
            TimeText = TimeText,
            Note = Note,
            IsClip = IsClip,
            ClipRangeText = ClipRangeText,
            Quality = quality,
        };
    }
}

public sealed record BookmarkQualityUpdateRequest(BookmarkItem Bookmark, string? Quality);

public sealed class QualityChipVisual
{
    public SolidColorBrush BackgroundBrush { get; init; } = AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush BorderBrush { get; init; } = AppSemanticPalette.Brush(AppSemanticPalette.SubtleBorderHex);
    public SolidColorBrush ForegroundBrush { get; init; } = AppSemanticPalette.Brush(AppSemanticPalette.PrimaryTextHex);
    public Visibility CheckVisibility { get; init; } = Visibility.Collapsed;
    public Thickness BorderThickness { get; init; } = new(1);

    public static QualityChipVisual Create(string qualityKey, string? selectedQuality)
    {
        var normalizedKey = NormalizeQuality(qualityKey);
        var normalizedSelected = NormalizeQuality(selectedQuality);
        var isSelected = string.Equals(normalizedKey, normalizedSelected, StringComparison.Ordinal);

        var accentHex = normalizedKey switch
        {
            "good" => AppSemanticPalette.PositiveHex,
            "bad" => AppSemanticPalette.NegativeHex,
            "neutral" => AppSemanticPalette.NeutralHex,
            _ => AppSemanticPalette.NeutralHex,
        };

        var selectedForegroundHex = normalizedKey switch
        {
            "bad" => AppSemanticPalette.PrimaryTextHex,
            _ => AppSemanticPalette.TagSurfaceHex,
        };

        return new QualityChipVisual
        {
            BackgroundBrush = AppSemanticPalette.Brush(isSelected ? accentHex : AppSemanticPalette.TagSurfaceHex),
            BorderBrush = AppSemanticPalette.Brush(isSelected ? accentHex : AppSemanticPalette.SubtleBorderHex),
            ForegroundBrush = AppSemanticPalette.Brush(isSelected ? selectedForegroundHex : accentHex),
            CheckVisibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
            BorderThickness = isSelected ? new Thickness(2) : new Thickness(1),
        };
    }

    private static string NormalizeQuality(string? value) =>
        (value ?? string.Empty).Trim().ToLowerInvariant();
}

public class TimelineEvent
{
    public string EventType { get; set; } = "";
    public double GameTimeS { get; set; }
    public string Details { get; set; } = "";
    public string TimeText => VodPlayerViewModel.FormatTime((int)GameTimeS);
    public string Summary => FormatSummary();
    public string DisplayText => string.IsNullOrEmpty(Summary) ? Label : $"{Label}: {Summary}";
    public string TooltipText => $"{TimeText} {DisplayText}";
    public SolidColorBrush AccentBrush => AppSemanticPalette.Brush(Color);
    public SolidColorBrush SurfaceBrush => AppSemanticPalette.Brush(SurfaceColor);

    /// <summary>Get the display color for this event type.</summary>
    public string Color => EventType.ToUpperInvariant() switch
    {
        "KILL" => AppSemanticPalette.PositiveHex,
        "DEATH" => AppSemanticPalette.NegativeHex,
        "ASSIST" => AppSemanticPalette.AccentBlueHex,
        "DRAGON" => AppSemanticPalette.AccentGoldHex,
        "BARON" => AppSemanticPalette.AccentGoldHex,
        "HERALD" => AppSemanticPalette.AccentTealHex,
        "TURRET" => AppSemanticPalette.AccentTealHex,
        "INHIBITOR" => AppSemanticPalette.AccentGoldHex,
        "FIRST_BLOOD" => AppSemanticPalette.NegativeHex,
        "MULTI_KILL" => AppSemanticPalette.AccentGoldHex,
        "LEVEL_UP" => AppSemanticPalette.NeutralHex,
        _ => AppSemanticPalette.NeutralHex,
    };

    public string SurfaceColor => EventType.ToUpperInvariant() switch
    {
        "KILL" => AppSemanticPalette.PositiveDimHex,
        "DEATH" => AppSemanticPalette.NegativeDimHex,
        "ASSIST" => AppSemanticPalette.AccentBlueDimHex,
        "DRAGON" => AppSemanticPalette.AccentGoldDimHex,
        "BARON" => AppSemanticPalette.AccentGoldDimHex,
        "HERALD" => AppSemanticPalette.AccentTealDimHex,
        "TURRET" => AppSemanticPalette.AccentTealDimHex,
        "INHIBITOR" => AppSemanticPalette.AccentGoldDimHex,
        "FIRST_BLOOD" => AppSemanticPalette.NegativeDimHex,
        "MULTI_KILL" => AppSemanticPalette.AccentGoldDimHex,
        "LEVEL_UP" => AppSemanticPalette.TagSurfaceHex,
        _ => AppSemanticPalette.TagSurfaceHex,
    };

    /// <summary>Get the marker shape for this event type.</summary>
    public MarkerShape Shape => EventType.ToUpperInvariant() switch
    {
        "KILL" or "FIRST_BLOOD" => MarkerShape.TriangleUp,
        "DEATH" => MarkerShape.TriangleDown,
        "ASSIST" => MarkerShape.Diamond,
        "DRAGON" or "BARON" or "HERALD" => MarkerShape.Diamond,
        "TURRET" or "INHIBITOR" => MarkerShape.Square,
        "MULTI_KILL" => MarkerShape.Star,
        _ => MarkerShape.Square,
    };

    public string Label => EventType.ToUpperInvariant() switch
    {
        "KILL" => "Kill",
        "DEATH" => "Death",
        "ASSIST" => "Assist",
        "DRAGON" => "Dragon",
        "BARON" => "Baron",
        "HERALD" => "Herald",
        "TURRET" => "Turret",
        "INHIBITOR" => "Inhibitor",
        "FIRST_BLOOD" => "First Blood",
        "MULTI_KILL" => "Multi Kill",
        "LEVEL_UP" => "Level Up",
        _ => EventType,
    };

    private string FormatSummary()
    {
        if (string.IsNullOrWhiteSpace(Details) || Details == "{}")
        {
            return "";
        }

        try
        {
            using var doc = JsonDocument.Parse(Details);
            var root = doc.RootElement;

            return EventType.ToUpperInvariant() switch
            {
                "KILL" => ReadValue(root, "victim"),
                "DEATH" => ReadValue(root, "killer"),
                "ASSIST" => ReadValue(root, "victim"),
                "DRAGON" => FormatDragonSummary(root),
                "BARON" => FormatObjectiveSummary(root, "killer"),
                "HERALD" => FormatObjectiveSummary(root, "killer"),
                "TURRET" => FormatObjectiveSummary(root, "killer"),
                "INHIBITOR" => FormatObjectiveSummary(root, "killer"),
                "MULTI_KILL" => ReadValue(root, "label"),
                _ => "",
            };
        }
        catch
        {
            return "";
        }
    }

    private static string FormatDragonSummary(JsonElement root)
    {
        var dragonType = ReadValue(root, "dragon_type");
        var killer = ReadValue(root, "killer");
        var stolen = root.TryGetProperty("stolen", out var stolenProp)
            && stolenProp.ValueKind == JsonValueKind.True;

        var summary = string.IsNullOrWhiteSpace(dragonType)
            ? killer
            : string.IsNullOrWhiteSpace(killer)
                ? dragonType
                : $"{dragonType} by {killer}";

        return stolen && !string.IsNullOrWhiteSpace(summary)
            ? $"{summary} (stolen)"
            : summary;
    }

    private static string FormatObjectiveSummary(JsonElement root, string actorProperty)
    {
        var actor = ReadValue(root, actorProperty);
        if (string.IsNullOrWhiteSpace(actor))
        {
            return "";
        }

        return $"by {actor}";
    }

    private static string ReadValue(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return "";
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => "",
        };
    }
}

public enum MarkerShape
{
    TriangleUp,
    TriangleDown,
    Circle,
    Diamond,
    Square,
    Star,
}

public class DerivedEventRegion
{
    public double StartTimeS { get; set; }
    public double EndTimeS { get; set; }
    public string Color { get; set; } = "#ff6b6b";
    public string Name { get; set; } = "";
}

public sealed record ObjectiveOption(long? Id, string Title)
{
    public override string ToString() => Title;
}
