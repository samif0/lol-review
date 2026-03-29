#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;

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

    // ── Game info ────────────────────────────────────────────────────

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _championName = "";
    [ObservableProperty] private bool _win;
    [ObservableProperty] private string _headerText = "VOD Review";
    [ObservableProperty] private string _vodPath = "";
    [ObservableProperty] private int _gameDurationS;

    // ── Playback state ──────────────────────────────────────────────

    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _currentTimeS;
    [ObservableProperty] private string _currentTimeText = "0:00";
    [ObservableProperty] private string _totalTimeText = "0:00";
    [ObservableProperty] private double _playbackSpeed = 1.0;
    [ObservableProperty] private bool _hasVod;
    [ObservableProperty] private bool _isLoading;

    // ── Clip extraction ─────────────────────────────────────────────

    [ObservableProperty] private double _clipStartS = -1;
    [ObservableProperty] private double _clipEndS = -1;
    [ObservableProperty] private bool _hasClipRange;
    [ObservableProperty] private string _clipRangeText = "";
    [ObservableProperty] private string _clipDurationText = "";
    [ObservableProperty] private bool _hasFfmpeg;
    [ObservableProperty] private bool _isExtractingClip;
    [ObservableProperty] private string _clipStatusText = "";
    [ObservableProperty] private string _clipNote = "";

    // ── Collections ─────────────────────────────────────────────────

    public ObservableCollection<BookmarkItem> Bookmarks { get; } = new();
    public ObservableCollection<TimelineEvent> GameEvents { get; } = new();
    public ObservableCollection<DerivedEventRegion> DerivedEvents { get; } = new();

    public static IReadOnlyList<double> SpeedOptions { get; } =
        new[] { 0.25, 0.5, 1.0, 1.5, 2.0 };

    // ── Events for the view ─────────────────────────────────────────

    /// <summary>Raised when the view should seek the media player.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Raised when playback speed should change.</summary>
    public event Action<double>? SpeedChangeRequested;

    /// <summary>Raised when play/pause should toggle.</summary>
    public event Action? PlayPauseRequested;

    // ── Constructor ─────────────────────────────────────────────────

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

    // ── Load ────────────────────────────────────────────────────────

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
            HeaderText = $"VOD Review — {game.ChampionName} ({(game.Win ? "W" : "L")})";
            GameDurationS = game.GameDuration;
            TotalTimeText = FormatTime(game.GameDuration);

            // Load VOD metadata
            var vod = await _vodRepo.GetVodAsync(gameId);
            if (vod == null) { HasVod = false; return; }

            HasVod = true;
            VodPath = vod.TryGetValue("file_path", out var fp) ? fp?.ToString() ?? "" : "";

            if (vod.TryGetValue("duration_s", out var dur) && dur != null)
            {
                var vodDuration = Convert.ToInt32(dur);
                if (vodDuration > 0)
                {
                    GameDurationS = vodDuration;
                    TotalTimeText = FormatTime(vodDuration);
                }
            }

            // Load game events for timeline
            var events = await _eventsRepo.GetEventsAsync(gameId);
            DispatcherHelper.RunOnUIThread(() =>
            {
                GameEvents.Clear();
                foreach (var e in events)
                {
                    var eventType = e.TryGetValue("event_type", out var et) ? et?.ToString() ?? "" : "";
                    var timeS = e.TryGetValue("game_time_s", out var ts) ? Convert.ToDouble(ts ?? 0) : 0;
                    var details = e.TryGetValue("details", out var d) ? d?.ToString() ?? "" : "";

                    GameEvents.Add(new TimelineEvent
                    {
                        EventType = eventType,
                        GameTimeS = timeS,
                        Details = details,
                    });
                }
            });

            // Load derived events for timeline regions
            var derived = await _derivedEventsRepo.GetInstancesAsync(gameId);
            DispatcherHelper.RunOnUIThread(() =>
            {
                DerivedEvents.Clear();
                foreach (var de in derived)
                {
                    var startS = de.TryGetValue("start_time_s", out var ss) ? Convert.ToDouble(ss ?? 0) : 0;
                    var endS = de.TryGetValue("end_time_s", out var es) ? Convert.ToDouble(es ?? 0) : 0;
                    var color = de.TryGetValue("color", out var c) ? c?.ToString() ?? "#ff6b6b" : "#ff6b6b";
                    var name = de.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";

                    DerivedEvents.Add(new DerivedEventRegion
                    {
                        StartTimeS = startS,
                        EndTimeS = endS,
                        Color = color,
                        Name = name,
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

    // ── Playback commands ───────────────────────────────────────────

    [RelayCommand]
    private void PlayPause()
    {
        PlayPauseRequested?.Invoke();
    }

    [RelayCommand]
    private void SeekForward()
    {
        SeekRequested?.Invoke(CurrentTimeS + 10);
    }

    [RelayCommand]
    private void SeekBackward()
    {
        SeekRequested?.Invoke(Math.Max(0, CurrentTimeS - 10));
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

    // ── Bookmark commands ───────────────────────────────────────────

    [RelayCommand]
    private async Task AddBookmarkAsync()
    {
        try
        {
            var timeS = (int)CurrentTimeS;
            await _vodRepo.AddBookmarkAsync(GameId, timeS, "");
            await RefreshBookmarksAsync();
            _logger.LogInformation("Bookmark added at {Time}s for game {Id}", timeS, GameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add bookmark");
        }
    }

    [RelayCommand]
    private async Task DeleteBookmarkAsync(long bookmarkId)
    {
        try
        {
            await _vodRepo.DeleteBookmarkAsync(bookmarkId);
            await RefreshBookmarksAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete bookmark {Id}", bookmarkId);
        }
    }

    [RelayCommand]
    private void SeekToBookmark(BookmarkItem bookmark)
    {
        SeekRequested?.Invoke(bookmark.GameTimeS);
    }

    // ── Clip commands ───────────────────────────────────────────────

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
    }

    [RelayCommand]
    private async Task ExtractClipAsync()
    {
        if (!HasClipRange || ClipStartS < 0 || ClipEndS < 0) return;
        if (IsExtractingClip) return;

        IsExtractingClip = true;
        ClipStatusText = "Extracting clip...";

        try
        {
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
                ClipStatusText = "Clip saved!";
                _logger.LogInformation("Clip extracted: {Path}", clipPath);
            }
            else
            {
                ClipStatusText = "Clip extraction failed.";
            }
        }
        catch (Exception ex)
        {
            ClipStatusText = "Error extracting clip.";
            _logger.LogError(ex, "Clip extraction failed");
        }
        finally
        {
            IsExtractingClip = false;
        }
    }

    // ── Navigation ──────────────────────────────────────────────────

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }

    // ── Public methods for the view ─────────────────────────────────

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

    // ── Helpers ──────────────────────────────────────────────────────

    private async Task RefreshBookmarksAsync()
    {
        var bookmarks = await _vodRepo.GetBookmarksAsync(GameId);
        DispatcherHelper.RunOnUIThread(() =>
        {
            Bookmarks.Clear();
            foreach (var b in bookmarks)
            {
                var id = b.TryGetValue("id", out var idVal) ? Convert.ToInt64(idVal ?? 0) : 0;
                var timeS = b.TryGetValue("game_time_s", out var ts) ? Convert.ToInt32(ts ?? 0) : 0;
                var note = b.TryGetValue("note", out var n) ? n?.ToString() ?? "" : "";
                var clipPath = b.TryGetValue("clip_path", out var cp) ? cp?.ToString() ?? "" : "";
                var clipStartS = b.TryGetValue("clip_start_s", out var cs) ? (cs != null ? Convert.ToInt32(cs) : (int?)null) : null;
                var clipEndS = b.TryGetValue("clip_end_s", out var ce) ? (ce != null ? Convert.ToInt32(ce) : (int?)null) : null;

                Bookmarks.Add(new BookmarkItem
                {
                    Id = id,
                    GameTimeS = timeS,
                    TimeText = FormatTime(timeS),
                    Note = note,
                    IsClip = !string.IsNullOrEmpty(clipPath),
                    ClipRangeText = clipStartS != null && clipEndS != null
                        ? $"{FormatTime(clipStartS.Value)} – {FormatTime(clipEndS.Value)}"
                        : "",
                });
            }
        });
    }

    private void UpdateClipRange()
    {
        if (ClipStartS >= 0 && ClipEndS >= 0)
        {
            var startS = Math.Min(ClipStartS, ClipEndS);
            var endS = Math.Max(ClipStartS, ClipEndS);
            var duration = endS - startS;

            HasClipRange = duration >= 1;
            ClipRangeText = $"{FormatTime((int)startS)} – {FormatTime((int)endS)}";
            ClipDurationText = $"{FormatTime((int)duration)}";
        }
        else
        {
            HasClipRange = false;
            ClipRangeText = ClipStartS >= 0 ? $"{FormatTime((int)ClipStartS)} – ?" : "";
            ClipDurationText = "";
        }
    }

    internal static string FormatTime(int totalSeconds)
    {
        var m = totalSeconds / 60;
        var s = totalSeconds % 60;
        return $"{m}:{s:D2}";
    }
}

// ── Display models ──────────────────────────────────────────────────

public class BookmarkItem
{
    public long Id { get; set; }
    public int GameTimeS { get; set; }
    public string TimeText { get; set; } = "";
    public string Note { get; set; } = "";
    public bool IsClip { get; set; }
    public string ClipRangeText { get; set; } = "";
}

public class TimelineEvent
{
    public string EventType { get; set; } = "";
    public double GameTimeS { get; set; }
    public string Details { get; set; } = "";

    /// <summary>Get the display color for this event type.</summary>
    public string Color => EventType.ToUpperInvariant() switch
    {
        "KILL" => "#28c76f",
        "DEATH" => "#ea5455",
        "ASSIST" => "#0099ff",
        "DRAGON" => "#c89b3c",
        "BARON" => "#8b5cf6",
        "HERALD" => "#06b6d4",
        "TURRET" => "#f97316",
        "INHIBITOR" => "#ec4899",
        "FIRST_BLOOD" => "#ef4444",
        "MULTI_KILL" => "#fbbf24",
        "LEVEL_UP" => "#6366f1",
        _ => "#7070a0",
    };

    /// <summary>Get the marker shape for this event type.</summary>
    public MarkerShape Shape => EventType.ToUpperInvariant() switch
    {
        "KILL" or "FIRST_BLOOD" => MarkerShape.TriangleUp,
        "DEATH" => MarkerShape.TriangleDown,
        "ASSIST" => MarkerShape.Circle,
        "DRAGON" or "BARON" or "HERALD" => MarkerShape.Diamond,
        "TURRET" or "INHIBITOR" => MarkerShape.Square,
        "MULTI_KILL" => MarkerShape.Star,
        _ => MarkerShape.Circle,
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
