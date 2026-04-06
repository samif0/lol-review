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
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the VOD player page.</summary>
public partial class VodPlayerViewModel : ObservableObject
{
    private readonly IVodRepository _vodRepo;
    private readonly IGameRepository _gameRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IDerivedEventsRepository _derivedEventsRepo;
    private readonly IClipService _clipService;
    private readonly IConfigService _configService;
    private readonly ICoachLabService _coachLabService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<VodPlayerViewModel> _logger;
    private readonly SemaphoreSlim _bookmarkMutationLock = new(1, 1);

    // â”€â”€ Game info â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _championName = "";
    [ObservableProperty] private bool _win;
    [ObservableProperty] private string _headerText = "VOD Review";
    [ObservableProperty] private string _vodPath = "";
    [ObservableProperty] private int _gameDurationS;

    // â”€â”€ Playback state â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _currentTimeS;
    [ObservableProperty] private string _currentTimeText = "0:00";
    [ObservableProperty] private string _totalTimeText = "0:00";
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private int _seekStepSeconds = 10;
    [ObservableProperty] private bool _hasVod;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasGameEvents;
    [ObservableProperty] private string _gameEventsStatusText = "No live events.";

    // â”€â”€ Clip extraction â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Collections â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<TimelineEvent> GameEvents { get; } = new();
    public ObservableCollection<DerivedEventRegion> DerivedEvents { get; } = new();

    public static IReadOnlyList<double> SpeedOptions { get; } =
        new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };

    public static IReadOnlyList<int> SeekStepOptions { get; } =
        new[] { 1, 2, 5, 10, 15, 30, 60 };

    public string SeekStepText => $"{SeekStepSeconds}s";
    public string SeekStepHintText => $"Left/Right {SeekStepText} | Up/Down step";
    public string ClipStartActionText => ClipStartS >= 0 ? "Move Start" : "Start Clip";
    public string ClipEndActionText => ClipEndS >= 0 ? "Move End" : "End Clip";

    // â”€â”€ Events for the view â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public string OutcomeLabel => Win ? "Victory" : "Defeat";
    public string VodStatusLabel => HasVod ? "VOD linked" : "No recording";
    public string PlaybackStateLabel => IsPlaying ? "Playing" : "Paused";

    /// <summary>Raised when the view should seek the media player.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Raised when playback speed should change.</summary>
    public event Action<double>? SpeedChangeRequested;

    /// <summary>Raised when play/pause should toggle.</summary>
    public event Action? PlayPauseRequested;

    // â”€â”€ Constructor â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    public VodPlayerViewModel(
        IVodRepository vodRepo,
        IGameRepository gameRepo,
        IGameEventsRepository eventsRepo,
        IDerivedEventsRepository derivedEventsRepo,
        IClipService clipService,
        IConfigService configService,
        ICoachLabService coachLabService,
        INavigationService navigationService,
        ILogger<VodPlayerViewModel> logger)
    {
        _vodRepo = vodRepo;
        _gameRepo = gameRepo;
        _eventsRepo = eventsRepo;
        _derivedEventsRepo = derivedEventsRepo;
        _clipService = clipService;
        _configService = configService;
        _coachLabService = coachLabService;
        _navigationService = navigationService;
        _logger = logger;
    }

    // â”€â”€ Load â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Playback commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Bookmark commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        await _bookmarkMutationLock.WaitAsync();
        try
        {
            await PersistVisibleBookmarkNotesAsync();
            var timeS = (int)CurrentTimeS;
            var note = BookmarkNote.Trim();
            await _vodRepo.AddBookmarkAsync(GameId, timeS, note);
            BookmarkNote = "";
            await RefreshBookmarksAsync();
            _logger.LogInformation("Bookmark added at {Time}s for game {Id}", timeS, GameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add bookmark");
        }
        finally
        {
            _bookmarkMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task DeleteBookmarkAsync(long bookmarkId)
    {
        await _bookmarkMutationLock.WaitAsync();
        try
        {
            var bookmark = Bookmarks.FirstOrDefault(item => item.Id == bookmarkId);
            var bookmarkKind = bookmark?.IsClip == true ? "clip" : "note";
            AppDiagnostics.WriteVerbose(
                "vod-delete.log",
                $"delete requested bookmarkId={bookmarkId} kind={bookmarkKind} gameId={GameId}");
            _logger.LogInformation(
                "Deleting {Kind} bookmark {BookmarkId} for game {GameId}",
                bookmarkKind,
                bookmarkId,
                GameId);
            await PersistVisibleBookmarkNotesAsync();
            await _vodRepo.DeleteBookmarkAsync(bookmarkId);
            await RefreshBookmarksAsync();
            AppDiagnostics.WriteVerbose(
                "vod-delete.log",
                $"delete completed bookmarkId={bookmarkId} remaining={Bookmarks.Count}");
            _logger.LogInformation(
                "Deleted bookmark {BookmarkId}; remaining bookmark count is {Count}",
                bookmarkId,
                Bookmarks.Count);
        }
        catch (Exception ex)
        {
            AppDiagnostics.WriteVerbose(
                "vod-delete.log",
                $"delete failed bookmarkId={bookmarkId} error={ex.GetType().Name}: {ex.Message}");
            _logger.LogError(ex, "Failed to delete bookmark {Id}", bookmarkId);
        }
        finally
        {
            _bookmarkMutationLock.Release();
        }
    }

    [RelayCommand]
    private async Task SaveBookmarkNoteAsync(BookmarkItem? bookmark)
    {
        if (bookmark is null || bookmark.Id <= 0)
        {
            return;
        }

        await _bookmarkMutationLock.WaitAsync();
        try
        {
            await _vodRepo.UpdateBookmarkAsync(bookmark.Id, note: bookmark.Note?.Trim() ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save note for bookmark {Id}", bookmark.Id);
        }
        finally
        {
            _bookmarkMutationLock.Release();
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

    // â”€â”€ Clip commands â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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
        await _bookmarkMutationLock.WaitAsync();

        try
        {
            await PersistVisibleBookmarkNotesAsync();
            var startS = (int)Math.Min(ClipStartS, ClipEndS);
            var endS = (int)Math.Max(ClipStartS, ClipEndS);
            var clipsFolder = _configService.ClipsFolder;

            var clipPath = await _clipService.ExtractClipAsync(
                VodPath, startS, endS, ChampionName, clipsFolder);

            if (!string.IsNullOrEmpty(clipPath))
            {
                var note = string.IsNullOrWhiteSpace(ClipNote) ? "Clip" : ClipNote.Trim();

                // Save as bookmark with clip metadata
                await _vodRepo.AddBookmarkAsync(
                    GameId, startS, note,
                    clipStartSeconds: startS,
                    clipEndSeconds: endS,
                    clipPath: clipPath);

                await RefreshBookmarksAsync();

                // Enforce folder size limit
                var maxBytes = (long)_configService.ClipsMaxSizeMb * 1024 * 1024;
                await _clipService.EnforceFolderSizeLimitAsync(clipsFolder, maxBytes);

                if (_coachLabService.IsEnabled)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _coachLabService.SyncMomentsAsync(includeAutoSamples: false);
                        }
                        catch (Exception syncEx)
                        {
                            _logger.LogDebug(syncEx, "Coach Lab clip sync failed after clip extraction");
                        }
                    });
                }

                ClipNote = "";
                ClipStatusText = "Clip saved.";
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
            _bookmarkMutationLock.Release();
            IsExtractingClip = false;
        }
    }

    // â”€â”€ Navigation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }

    // â”€â”€ Public methods for the view â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    // â”€â”€ Helpers â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private async Task RefreshBookmarksAsync()
    {
        var bookmarks = await _vodRepo.GetBookmarksAsync(GameId);
        DispatcherHelper.RunOnUIThread(() =>
        {
            Bookmarks.Clear();
            foreach (var b in bookmarks)
            {
                var id = b.Id;
                var timeS = b.GameTimeSeconds;
                var note = b.Note;
                var clipPath = b.ClipPath;
                var clipStartS = b.ClipStartSeconds;
                var clipEndS = b.ClipEndSeconds;

                Bookmarks.Add(new BookmarkItem
                {
                    Id = id,
                    GameTimeS = timeS,
                    TimeText = FormatTime(timeS),
                    Note = note,
                    IsClip = !string.IsNullOrEmpty(clipPath),
                    ClipRangeText = clipStartS != null && clipEndS != null
                        ? $"{FormatTime(clipStartS.Value)} - {FormatTime(clipEndS.Value)}"
                        : "",
                });
            }
        });
    }

    private async Task PersistVisibleBookmarkNotesAsync()
    {
        // Snapshot to avoid InvalidOperationException if the collection is
        // modified while we yield (e.g. LostFocus triggers during Clear).
        var snapshot = Bookmarks.ToList();
        foreach (var bookmark in snapshot)
        {
            if (bookmark.Id <= 0)
            {
                continue;
            }

            await _vodRepo.UpdateBookmarkAsync(bookmark.Id, note: bookmark.Note?.Trim() ?? "");
        }
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
}

// â”€â”€ Display models â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

public class BookmarkItem
{
    public long Id { get; set; }
    public int GameTimeS { get; set; }
    public string TimeText { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsClip { get; set; }
    public string ClipRangeText { get; set; } = "";
    public string KindLabel => IsClip ? "CLIP" : "NOTE";
    public string MarkerColorHex => IsClip ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.NeutralHex;
    public SolidColorBrush AccentBrush => AppSemanticPalette.Brush(MarkerColorHex);
    public SolidColorBrush SurfaceBrush => IsClip
        ? AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldDimHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);
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


