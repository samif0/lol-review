#nullable enable

using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>ViewModel for the VOD player page.</summary>
public partial class VodPlayerViewModel : ObservableObject
{
    private static readonly TimeSpan BookmarkNoteSaveDebounce = TimeSpan.FromMilliseconds(650);

    private readonly IVodRepository _vodRepo;
    private readonly IGameRepository _gameRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IDerivedEventsRepository _derivedEventsRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly IClipService _clipService;
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;
    private readonly ICoachSidecarNotifier _coachNotifier;
    private readonly IVodService _vodService;
    private readonly IMessenger _messenger;
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
    // v2.15.8: default to 1s steps so Left/Right does fine-grained scrubbing
    // out of the box. Up/Down ratchets through SeekStepOptions to expand.
    [ObservableProperty] private int _seekStepSeconds = 1;
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
    // v2.15.7: if the user picked a prompt-row in the unified tag picker,
    // _selectedPromptId tracks it. _selectedObjectiveId stays populated with
    // the prompt's parent objective so non-prompt queries still work.
    [ObservableProperty] private long? _selectedPromptId;
    [ObservableProperty] private string _selectedClipQuality = "";

    public IReadOnlyList<string> QualityOptions { get; } =
        ["", "good", "neutral", "bad"];

    // â"€â"€ Collections â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<TimelineEvent> GameEvents { get; } = new();
    public ObservableCollection<DerivedEventRegion> DerivedEvents { get; } = new();
    public ObservableCollection<ObjectiveOption> ObjectiveOptions { get; } = new();
    // v2.15.7: unified tag picker — flat list of objectives + their prompts
    // (indented). BookmarkItem.TagOptions shares this reference so per-clip
    // pickers see the same options without a round-trip.
    public ObservableCollection<TagOption> TagOptions { get; } = new();

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
        IPromptsRepository promptsRepo,
        IClipService clipService,
        IConfigService configService,
        INavigationService navigationService,
        ICoachSidecarNotifier coachNotifier,
        IVodService vodService,
        IMessenger messenger,
        ILogger<VodPlayerViewModel> logger)
    {
        _vodRepo = vodRepo;
        _gameRepo = gameRepo;
        _eventsRepo = eventsRepo;
        _derivedEventsRepo = derivedEventsRepo;
        _objectivesRepo = objectivesRepo;
        _promptsRepo = promptsRepo;
        _clipService = clipService;
        _configService = configService;
        _navigationService = navigationService;
        _coachNotifier = coachNotifier;
        _vodService = vodService;
        _messenger = messenger;
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

            // v2.16: if no link exists yet, try to match a recording right now.
            // Covers the case where ProcessGameEndAsync's 90s retry fired before
            // Ascent finished encoding — the user opens the VOD viewer minutes
            // later and the file is now ready. Mirrors ReviewWorkflowService.
            if (vod == null && _configService.IsAscentEnabled)
            {
                try
                {
                    await _vodService.TryLinkRecordingAsync(game);
                    vod = await _vodRepo.GetVodAsync(gameId);
                    if (vod is not null)
                    {
                        _logger.LogInformation("On-demand VOD link succeeded for game {Id}", gameId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "On-demand VOD link failed for game {Id}", gameId);
                }
            }

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
        var promptId = SelectedPromptId;
        BookmarkNote = "";

        try
        {
            var bookmarkId = await EnqueueBookmarkMutationAsync(
                () => _vodRepo.AddBookmarkAsync(GameId, timeS, note,
                    objectiveId: objectiveId,
                    promptId: promptId));

            InsertBookmark(new BookmarkItem
            {
                Id = bookmarkId,
                GameTimeS = timeS,
                TimeText = FormatTime(timeS),
                Note = note,
                IsClip = false,
                ObjectiveId = objectiveId,
                PromptId = promptId,
                ObjectiveOptions = ObjectiveOptions,
                TagOptions = TagOptions,
            });
            await MarkObjectivePracticedFromBookmarkAsync(objectiveId);
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
    private async Task SetBookmarkObjectiveAsync(BookmarkObjectiveUpdateRequest? request)
    {
        if (request is null || request.Bookmark is null || request.Bookmark.Id <= 0)
        {
            return;
        }

        var bookmark = request.Bookmark;
        var previousObjectiveId = bookmark.ObjectiveId;

        // Optimistic local update so the combo doesn't flicker back if the
        // write is slow.
        bookmark.ObjectiveId = request.ObjectiveId;

        try
        {
            await EnqueueBookmarkMutationAsync(
                () => _vodRepo.SetBookmarkObjectiveAsync(bookmark.Id, request.ObjectiveId));
            await MarkObjectivePracticedFromBookmarkAsync(request.ObjectiveId);
        }
        catch (Exception ex)
        {
            bookmark.ObjectiveId = previousObjectiveId;
            _logger.LogError(ex, "Failed to set objective on bookmark {Id}", bookmark.Id);
        }
    }

    // v2.15.7: per-clip tag edit. The picker can land on either an Objective
    // header (PromptId == null) or a Prompt child (both ids set). Persist both
    // atomically so post-game routing can decide where the [MM:SS] note goes.
    [RelayCommand]
    private async Task SetBookmarkTagAsync(BookmarkTagUpdateRequest? request)
    {
        if (request is null || request.Bookmark is null || request.Bookmark.Id <= 0)
        {
            return;
        }

        var bookmark = request.Bookmark;
        var prevObj = bookmark.ObjectiveId;
        var prevPrompt = bookmark.PromptId;

        bookmark.ObjectiveId = request.ObjectiveId;
        bookmark.PromptId = request.PromptId;

        try
        {
            await EnqueueBookmarkMutationAsync(
                () => _vodRepo.SetBookmarkTagAsync(bookmark.Id, request.ObjectiveId, request.PromptId));
            await MarkObjectivePracticedFromBookmarkAsync(request.ObjectiveId);
        }
        catch (Exception ex)
        {
            bookmark.ObjectiveId = prevObj;
            bookmark.PromptId = prevPrompt;
            _logger.LogError(ex, "Failed to set tag on bookmark {Id}", bookmark.Id);
        }
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
        // v2.15.10: clip rows jump to the clip's in-point (start of the
        // range), not the marker time — the marker is usually mid-action so
        // jumping to it dropped users into the middle of the clip.
        var target = bookmark.IsClip && bookmark.ClipStartSeconds is int start
            ? start
            : bookmark.GameTimeS;
        SeekRequested?.Invoke(target);
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
        var promptId = SelectedPromptId;

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
                        quality: quality,
                        promptId: promptId));

                InsertBookmark(new BookmarkItem
                {
                    Id = bookmarkId,
                    GameTimeS = startS,
                    TimeText = FormatTime(startS),
                    Note = note,
                    IsClip = true,
                    ClipRangeText = $"{FormatTime(startS)} - {FormatTime(endS)}",
                    Quality = quality,
                    ObjectiveId = objectiveId,
                    PromptId = promptId,
                    ObjectiveOptions = ObjectiveOptions,
                    TagOptions = TagOptions,
                });
                await MarkObjectivePracticedFromBookmarkAsync(objectiveId);

                // Phase 4 hook: ask coach sidecar to generate frame descriptions.
                _ = _coachNotifier.NotifyBookmarkCreatedAsync(bookmarkId)
                    .ContinueWith(
                        t => _logger.LogDebug(t.Exception, "Coach NotifyBookmarkCreatedAsync failed"),
                        TaskContinuationOptions.OnlyOnFaulted);

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

    /// <summary>Jump to the review page for the same gameId. Pairs with the
    /// "Review VOD" button on ReviewPage so users can flip back and forth
    /// without losing context.</summary>
    [RelayCommand]
    private void OpenReview()
    {
        if (GameId <= 0) return;
        _navigationService.NavigateTo("review", GameId);
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

    /// <summary>
    /// v2.16: tagging a bookmark/clip to an objective is itself an act of
    /// practice — record game_objectives(practiced=1) so the user doesn't have
    /// to remember the redundant toggle.
    ///
    /// v2.16.7: previously we skipped when ANY row existed for this
    /// game+objective. That broke the live VOD review case: the post-game
    /// pipeline auto-inserts a default <c>practiced=false</c> row, so the
    /// helper bailed out without flipping the toggle on. Now we only skip
    /// when the existing row already has <c>practiced=true</c> or a
    /// user-typed <c>ExecutionNote</c> — anything else means the user hasn't
    /// touched it yet and the bookmark is the act of practice.
    /// </summary>
    private async Task MarkObjectivePracticedFromBookmarkAsync(long? objectiveId)
    {
        if (objectiveId is null || objectiveId.Value <= 0 || GameId <= 0) return;

        try
        {
            var existing = await _objectivesRepo.GetGameObjectivesAsync(GameId).ConfigureAwait(false);
            var existingRow = existing.FirstOrDefault(g => g.ObjectiveId == objectiveId.Value);

            // Already practiced or has user content → leave alone.
            if (existingRow is not null
                && (existingRow.Practiced
                    || !string.IsNullOrWhiteSpace(existingRow.ExecutionNote)))
            {
                return;
            }

            // No row, or row exists but is the auto-inserted default
            // (practiced=false, empty note) — flip it on. Preserve any
            // existing executionNote (will be empty here by definition).
            var note = existingRow?.ExecutionNote ?? "Auto: tagged via VOD bookmark";

            await _objectivesRepo.RecordGameAsync(
                GameId,
                objectiveId.Value,
                practiced: true,
                executionNote: note).ConfigureAwait(false);
            _logger.LogInformation(
                "Auto-marked objective {ObjectiveId} as practiced for game {GameId} (bookmark tag)",
                objectiveId.Value, GameId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-mark objective {ObjectiveId} practiced", objectiveId);
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

    private void BroadcastBookmarkChanged()
    {
        if (GameId <= 0) return;
        try { _messenger.Send(new Revu.Core.Lcu.BookmarkChangedMessage(GameId)); }
        catch (Exception ex) { _logger.LogDebug(ex, "BookmarkChanged broadcast failed"); }
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
        BroadcastBookmarkChanged();
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

        var result = await mutation().ConfigureAwait(false);
        BroadcastBookmarkChanged();
        return result;
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

    private BookmarkItem ToBookmarkItem(VodBookmarkRecord record)
    {
        var isClip = !string.IsNullOrEmpty(record.ClipPath);
        return new BookmarkItem
        {
            Id = record.Id,
            GameTimeS = record.GameTimeSeconds,
            TimeText = FormatTime(record.GameTimeSeconds),
            Note = record.Note,
            IsClip = isClip,
            ClipStartSeconds = record.ClipStartSeconds,
            ClipRangeText = record.ClipStartSeconds != null && record.ClipEndSeconds != null
                ? $"{FormatTime(record.ClipStartSeconds.Value)} - {FormatTime(record.ClipEndSeconds.Value)}"
                : "",
            Quality = record.Quality,
            ObjectiveId = record.ObjectiveId,
            PromptId = record.PromptId,
            ObjectiveOptions = ObjectiveOptions,
            TagOptions = TagOptions,
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
            // v2.15.5: default the bookmark-tagger to the priority objective.
            var priority = objectives.FirstOrDefault(o => o.IsPriority) ?? objectives.FirstOrDefault();

            // v2.15.7: build the unified TagOptions tree. For each active
            // objective, emit one Objective row + one row per prompt (any phase).
            // Search indexes on SearchText, so typing "trade" matches prompts
            // whose label OR parent title contains "trade".
            //
            // v2.15.10: prepend an explicit "(no tag)" row so the user can
            // pick "no objective" without the picker fighting back to the
            // priority-default. Untagged clips/bookmarks route into Spotted
            // Problems on the post-game review.
            var tagRows = new List<TagOption>
            {
                new TagOption
                {
                    Kind = TagOption.OptionKind.None,
                    Title = "(no tag)",
                    SearchText = "no tag none clear",
                },
            };
            foreach (var obj in objectives)
            {
                tagRows.Add(new TagOption
                {
                    Kind = TagOption.OptionKind.Objective,
                    ObjectiveId = obj.Id,
                    Title = obj.Title,
                    SearchText = obj.Title,
                });
                IReadOnlyList<ObjectivePrompt> prompts;
                try
                {
                    prompts = await _promptsRepo.GetPromptsForObjectiveAsync(obj.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load prompts for objective {ObjectiveId}", obj.Id);
                    prompts = Array.Empty<ObjectivePrompt>();
                }
                foreach (var p in prompts.OrderBy(p => p.SortOrder).ThenBy(p => p.Id))
                {
                    tagRows.Add(new TagOption
                    {
                        Kind = TagOption.OptionKind.Prompt,
                        ObjectiveId = obj.Id,
                        PromptId = p.Id,
                        // Indent + sibling-of-objective placement already conveys
                        // "this is a child of <objective>". Showing only the
                        // prompt label here lets long prompt text fit in the
                        // dropdown column without ellipsis-clipping.
                        Title = p.Label,
                        ParentTitle = obj.Title,
                        SearchText = $"{obj.Title} {p.Label}",
                    });
                }
            }

            DispatcherHelper.RunOnUIThread(() =>
            {
                ObjectiveOptions.Clear();
                ObjectiveOptions.Add(new ObjectiveOption(null, "(none)"));
                foreach (var obj in objectives)
                {
                    ObjectiveOptions.Add(new ObjectiveOption(obj.Id, $"{obj.Title} ({ObjectivePhases.ToDisplayLabel(obj.Phase)})"));
                }

                TagOptions.Clear();
                foreach (var r in tagRows) TagOptions.Add(r);

                if (SelectedObjectiveId is null && priority is not null)
                {
                    SelectedObjectiveId = priority.Id;
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
        // v2.15.8: removed ClipStatusText hijack — that field is only visible
        // when the clip controls are open. The persistent inline pill next to
        // the player's timestamp is the surface now.
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

public partial class BookmarkItem : ObservableObject
{
    public long Id { get; set; }
    public int GameTimeS { get; set; }
    public string TimeText { get; set; } = "";

    [ObservableProperty]
    private string _note = "";

    public bool IsClip { get; set; }
    /// <summary>v2.15.10: when this is a clip, where the clip range starts.
    /// Null for plain note bookmarks. Used by Jump to seek to the first frame
    /// of the clip rather than the marker time, which often sits in the middle.</summary>
    public int? ClipStartSeconds { get; set; }
    public string ClipRangeText { get; set; } = "";
    public string Quality { get; set; } = "";

    /// <summary>
    /// Objective attached to this bookmark, or null if unset. v2.15.7: changed
    /// from a plain setter to an ObservableProperty so the picker-button's
    /// Content binding refreshes when SetBookmarkObjectiveAsync updates it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ObjectiveTitleDisplay))]
    private long? _objectiveId;

    /// <summary>
    /// v2.15.7: prompt tag on top of the objective. When set, review-time
    /// autopopulate routes this clip's text into the prompt's answer field.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ObjectiveTitleDisplay))]
    private long? _promptId;

    /// <summary>
    /// Reference to the same ObservableCollection the VM owns. Each bookmark
    /// item holds a pointer to it so the dialog-opener code-behind can
    /// enumerate the current options without walking up to the page's VM.
    /// </summary>
    public ObservableCollection<ObjectiveOption>? ObjectiveOptions { get; set; }

    /// <summary>v2.15.7: flat objectives+prompts tag list for the unified picker.</summary>
    public ObservableCollection<TagOption>? TagOptions { get; set; }

    /// <summary>v2.15.7: display label for the picker button. When tagged to
    /// a prompt we show only the prompt label — the box is narrow and the
    /// "Objective • Prompt" form ellipsis-clips. Re-opening the picker shows
    /// the full hierarchy via indent.</summary>
    public string ObjectiveTitleDisplay
    {
        get
        {
            if (PromptId is not null && TagOptions is not null)
            {
                var row = TagOptions.FirstOrDefault(t =>
                    t.Kind == TagOption.OptionKind.Prompt && t.PromptId == PromptId);
                if (row is not null) return row.Title;
            }
            if (ObjectiveId is null) return "(no tag)";
            var match = ObjectiveOptions?.FirstOrDefault(o => o.Id == ObjectiveId);
            return match?.Title ?? "(no tag)";
        }
    }
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
            ObjectiveId = ObjectiveId,
            PromptId = PromptId,
            ObjectiveOptions = ObjectiveOptions,
            TagOptions = TagOptions,
        };
    }
}

public sealed record BookmarkQualityUpdateRequest(BookmarkItem Bookmark, string? Quality);

public sealed record BookmarkObjectiveUpdateRequest(BookmarkItem Bookmark, long? ObjectiveId);

/// <summary>v2.15.7: unified tag update — covers objective and optional prompt.</summary>
public sealed record BookmarkTagUpdateRequest(BookmarkItem Bookmark, long? ObjectiveId, long? PromptId);

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

    /// <summary>v2.16: 2-4 char tag rendered next to the marker on the timeline,
    /// so users see *what* the marker means without hovering.</summary>
    public string ShortLabel => EventType.ToUpperInvariant() switch
    {
        "KILL"        => "KILL",
        "DEATH"       => "DEAD",
        "ASSIST"      => "AST",
        "DRAGON"      => "DRG",
        "BARON"       => "BAR",
        "HERALD"      => "HRD",
        "TURRET"      => "TWR",
        "INHIBITOR"   => "INH",
        "FIRST_BLOOD" => "FB",
        "MULTI_KILL"  => "MULTI",
        "LEVEL_UP"    => "LVL",
        _             => "EVT",
    };
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

/// <summary>
/// v2.15.7: tag-picker row that covers both objective headers and their
/// prompt children. Kind decides which fields the VM consumes:
/// Objective → ObjectiveId only; Prompt → both ObjectiveId + PromptId.
/// </summary>
public sealed class TagOption
{
    public enum OptionKind { Objective, Prompt, None }

    public OptionKind Kind { get; set; } = OptionKind.Objective;
    public long? ObjectiveId { get; set; }
    public long? PromptId { get; set; }
    /// <summary>Row text shown in the dropdown list. For Prompt rows this is
    /// only the prompt label (the indent + position implies the parent), so
    /// long prompt text doesn't get clipped by the column width.</summary>
    public string Title { get; set; } = "";
    /// <summary>Parent objective title — only set on Prompt rows so the
    /// current-state TextBox can render "Objective • Prompt" without re-
    /// looking-it-up.</summary>
    public string ParentTitle { get; set; } = "";
    /// <summary>Full searchable text — Title plus any parent-objective context for prompts.</summary>
    public string SearchText { get; set; } = "";
    /// <summary>Indent applied to prompt rows in the dropdown (px).</summary>
    public double Indent => Kind == OptionKind.Prompt ? 16.0 : 0.0;
}

// Plain class (not a record) because the WinUI XAML compiler-generated
// type-info metadata for DisplayMemberPath needs a public settable
// property, which positional records don't provide. We use this class
// in two binding contexts (the page-level dropdown and per-bookmark
// dropdowns), and the DataTemplate path requires init-or-set.
public sealed class ObjectiveOption
{
    public long? Id { get; set; }
    public string Title { get; set; } = "";

    public ObjectiveOption() { }
    public ObjectiveOption(long? id, string title)
    {
        Id = id;
        Title = title;
    }

    public override string ToString() => Title;
}
