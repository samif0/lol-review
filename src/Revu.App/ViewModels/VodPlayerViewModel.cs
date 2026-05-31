#nullable enable

using System.Collections.ObjectModel;
using System.IO;
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
    private const int EvidenceJumpPreRollSeconds = 5;
    private const string ReviewMomentFilterAuto = "auto";
    private const string ReviewMomentFilterSaved = "saved";
    private const string ReviewMomentFilterBookmarks = "bookmarks";

    private readonly IVodRepository _vodRepo;
    private readonly IGameRepository _gameRepo;
    private readonly IGameEventsRepository _eventsRepo;
    private readonly IDerivedEventsRepository _derivedEventsRepo;
    private readonly IEvidenceRepository _evidenceRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IPromptsRepository _promptsRepo;
    private readonly IClipService _clipService;
    private readonly IClipUploadService _clipUploadService;
    private readonly IRiotAuthClient _authClient;
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;
    private readonly ICoachSidecarNotifier _coachNotifier;
    private readonly IVodService _vodService;
    private readonly IMessenger _messenger;
    private readonly ILogger<VodPlayerViewModel> _logger;
    private readonly SerializedTaskQueue _bookmarkMutationQueue;
    private readonly object _bookmarkNoteSaveGate = new();
    private readonly Dictionary<long, CancellationTokenSource> _bookmarkNoteSaveDelays = [];
    private int _lastFormattedSecond = -1;

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
    [ObservableProperty] private bool _hasPlayableVod;
    [ObservableProperty] private bool _hasPlayableClips;
    [ObservableProperty] private string _vodAvailabilityText = "Loading recording...";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _hasGameEvents;
    [ObservableProperty] private bool _showNoEventsHint;
    [ObservableProperty] private string _gameEventsStatusText = "No live events.";
    [ObservableProperty] private bool _hasEvidenceInboxItems;

    /// <summary>
    /// v2.17.8: backs the dismissible hint that explains the auto-Timeline-Inbox
    /// toggle. Flips to false when the user clicks "Hide this hint" — the VM
    /// also persists <c>AutoTimelineClippingHintDismissed=true</c> so the hint
    /// stays gone across sessions. Refresh-from-config happens in <c>LoadAsync</c>.
    /// </summary>
    [ObservableProperty] private bool _autoClippingHintDismissed;

    /// <summary>
    /// v2.17.8: cached config value so the hint visibility binding can react
    /// when the user toggles auto-clipping off from Settings and comes back.
    /// </summary>
    [ObservableProperty] private bool _autoClippingEnabled = true;

    /// <summary>
    /// v2.17.8: composite visibility for the VOD-viewer hint. Show when the
    /// inbox has items (so the hint has something to point at), auto-clipping
    /// is currently on (no point telling the user to turn off something
    /// already off), and the user hasn't permanently hidden the hint.
    /// </summary>
    public bool ShowAutoClippingHint =>
        HasEvidenceInboxItems
        && AutoClippingEnabled
        && !AutoClippingHintDismissed;

    partial void OnHasEvidenceInboxItemsChanged(bool value)   => OnPropertyChanged(nameof(ShowAutoClippingHint));
    partial void OnAutoClippingEnabledChanged(bool value)     => OnPropertyChanged(nameof(ShowAutoClippingHint));
    partial void OnAutoClippingHintDismissedChanged(bool value) => OnPropertyChanged(nameof(ShowAutoClippingHint));

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

    // ── Share / login-to-share ────────────────────────────────────
    // Clips can be uploaded to revu.lol/<id> for public viewing. Sharing
    // requires a logged-in session; if logged out, an inline email→OTP prompt
    // (reusing the magic-link flow) appears, then the pending clip uploads.

    /// <summary>Max shareable clip length. Revu can't downscale source
    /// resolution, so duration is the size lever; the server also caps bytes.</summary>
    public const int MaxShareDurationSeconds = 90;

    [ObservableProperty] private bool _shareLoginVisible;
    [ObservableProperty] private string _shareEmail = "";
    [ObservableProperty] private string _shareOtp = "";
    [ObservableProperty] private bool _shareAwaitingOtp;
    [ObservableProperty] private bool _shareBusy;
    [ObservableProperty] private string _shareStatusText = "";

    // The clip the user clicked Share on while logged out — uploaded once login
    // completes. Cleared when the prompt is dismissed.
    private BookmarkItem? _pendingShareItem;

    // â"€â"€ Collections â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [ObservableProperty] private ObservableCollection<BookmarkItem> _bookmarks = new();
    [ObservableProperty] private ObservableCollection<BookmarkItem> _visibleBookmarkItems = new();
    [ObservableProperty] private ObservableCollection<TimelineEvent> _gameEvents = new();
    [ObservableProperty] private ObservableCollection<DerivedEventRegion> _derivedEvents = new();
    [ObservableProperty] private ObservableCollection<EvidenceInboxItem> _evidenceInbox = new();
    [ObservableProperty] private ObservableCollection<EvidenceInboxItem> _autoReviewMoments = new();
    [ObservableProperty] private ObservableCollection<EvidenceInboxItem> _savedClipReviewMoments = new();
    [ObservableProperty] private ObservableCollection<EvidenceInboxItem> _visibleReviewMoments = new();
    [ObservableProperty] private string _reviewMomentFilter = ReviewMomentFilterAuto;
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
    public string VodStatusLabel => HasPlayableVod
        ? "VOD linked"
        : HasPlayableClips
            ? "Clips available"
            : HasVod
                ? "VOD missing"
                : "No recording";
    public bool ShowClipFallbackHint => !HasPlayableVod && HasPlayableClips;
    public string PlaybackStateLabel => IsPlaying ? "Playing" : "Paused";
    public bool IsAutoReviewMomentFilterSelected =>
        string.Equals(ReviewMomentFilter, ReviewMomentFilterAuto, StringComparison.Ordinal);
    public bool IsSavedReviewMomentFilterSelected =>
        string.Equals(ReviewMomentFilter, ReviewMomentFilterSaved, StringComparison.Ordinal);
    public bool IsBookmarkReviewMomentFilterSelected =>
        string.Equals(ReviewMomentFilter, ReviewMomentFilterBookmarks, StringComparison.Ordinal);
    public int AutoReviewMomentCount => AutoReviewMoments.Count;
    public int SavedClipReviewMomentCount => SavedClipReviewMoments.Count;
    public int BookmarkReviewMomentCount => VisibleBookmarkItems.Count;
    public bool HasVisibleReviewMoments => VisibleReviewMoments.Count > 0;
    public bool HasVisibleBookmarkItems => VisibleBookmarkItems.Count > 0;
    /// <summary>Shows the Auto/Clips empty-state banner: moments list is empty and we are NOT on the Bookmarks tab.</summary>
    public bool ShowEmptyReviewMomentsState => !HasVisibleReviewMoments && !IsBookmarkReviewMomentFilterSelected;
    /// <summary>Shows the EvidenceInboxItem list: has items AND we are NOT on the Bookmarks tab.</summary>
    public bool ShowReviewMomentsList => HasVisibleReviewMoments && !IsBookmarkReviewMomentFilterSelected;
    public string EmptyReviewMomentsText => IsSavedReviewMomentFilterSelected
        ? "No saved clips are queued yet."
        : IsBookmarkReviewMomentFilterSelected
            ? "No plain bookmarks saved yet."
            : "No auto picks are queued right now.";
    public SolidColorBrush AutoReviewFilterBackgroundBrush => AppSemanticPalette.Brush(
        IsAutoReviewMomentFilterSelected ? AppSemanticPalette.AccentBlueHex : AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush AutoReviewFilterBorderBrush => AppSemanticPalette.Brush(
        IsAutoReviewMomentFilterSelected ? AppSemanticPalette.AccentBlueHex : AppSemanticPalette.SubtleBorderHex);
    public SolidColorBrush AutoReviewFilterForegroundBrush => AppSemanticPalette.Brush(
        IsAutoReviewMomentFilterSelected ? AppSemanticPalette.TagSurfaceHex : AppSemanticPalette.SecondaryTextHex);
    public SolidColorBrush SavedReviewFilterBackgroundBrush => AppSemanticPalette.Brush(
        IsSavedReviewMomentFilterSelected ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush SavedReviewFilterBorderBrush => AppSemanticPalette.Brush(
        IsSavedReviewMomentFilterSelected ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.SubtleBorderHex);
    public SolidColorBrush SavedReviewFilterForegroundBrush => AppSemanticPalette.Brush(
        IsSavedReviewMomentFilterSelected ? AppSemanticPalette.TagSurfaceHex : AppSemanticPalette.SecondaryTextHex);
    public SolidColorBrush BookmarkReviewFilterBackgroundBrush => AppSemanticPalette.Brush(
        IsBookmarkReviewMomentFilterSelected ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush BookmarkReviewFilterBorderBrush => AppSemanticPalette.Brush(
        IsBookmarkReviewMomentFilterSelected ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.SubtleBorderHex);
    public SolidColorBrush BookmarkReviewFilterForegroundBrush => AppSemanticPalette.Brush(
        IsBookmarkReviewMomentFilterSelected ? AppSemanticPalette.TagSurfaceHex : AppSemanticPalette.SecondaryTextHex);

    // Underline-tab style: active = accent color, inactive = muted text
    public SolidColorBrush AutoTabForegroundBrush => AppSemanticPalette.Brush(
        IsAutoReviewMomentFilterSelected ? AppSemanticPalette.AccentBlueHex : AppSemanticPalette.SecondaryTextHex);
    public SolidColorBrush SavedTabForegroundBrush => AppSemanticPalette.Brush(
        IsSavedReviewMomentFilterSelected ? AppSemanticPalette.AccentGoldHex : AppSemanticPalette.SecondaryTextHex);
    public SolidColorBrush BookmarkTabForegroundBrush => AppSemanticPalette.Brush(
        IsBookmarkReviewMomentFilterSelected ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.SecondaryTextHex);

    /// <summary>Raised when the view should seek the media player.</summary>
    public event Action<double>? SeekRequested;

    /// <summary>Raised when playback speed should change.</summary>
    public event Action<double>? SpeedChangeRequested;

    /// <summary>Raised when play/pause should toggle.</summary>
    public event Action? PlayPauseRequested;

    /// <summary>Raised when the original VOD is missing and a saved clip should play instead.</summary>
    public event Action<BookmarkItem>? ClipPlaybackRequested;

    // â"€â"€ Constructor â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    public VodPlayerViewModel(
        IVodRepository vodRepo,
        IGameRepository gameRepo,
        IGameEventsRepository eventsRepo,
        IDerivedEventsRepository derivedEventsRepo,
        IEvidenceRepository evidenceRepo,
        IObjectivesRepository objectivesRepo,
        IPromptsRepository promptsRepo,
        IClipService clipService,
        IClipUploadService clipUploadService,
        IRiotAuthClient authClient,
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
        _evidenceRepo = evidenceRepo;
        _objectivesRepo = objectivesRepo;
        _promptsRepo = promptsRepo;
        _clipService = clipService;
        _clipUploadService = clipUploadService;
        _authClient = authClient;
        _configService = configService;
        _navigationService = navigationService;
        _coachNotifier = coachNotifier;
        _vodService = vodService;
        _messenger = messenger;
        _logger = logger;
        _bookmarkMutationQueue = new SerializedTaskQueue(logger, "VOD bookmark mutation");
    }

    // â"€â"€ Load â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading) return;
        IsLoading = true;
        using var perf = PerformanceTrace.Time("VodPlayer.Load", $"gameId={gameId}");

        try
        {
            GameId = gameId;

            // v2.17.8: prime hint-banner state from config. Toggles in Settings
            // take effect on the next Load — refreshing each VOD open keeps the
            // VM in sync without needing a global config-change subscription.
            AutoClippingEnabled = _configService.AutoTimelineClippingEnabled;
            AutoClippingHintDismissed = _configService.AutoTimelineClippingHintDismissed;

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
            if (vod == null)
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

            HasVod = vod is not null;
            VodPath = vod?.FilePath ?? "";
            HasPlayableVod = vod is not null && FileProbeCache.Exists(vod.FilePath);
            VodAvailabilityText = HasPlayableVod
                ? "Recording ready."
                : vod is null
                    ? "No VOD linked to this game."
                    : "The linked VOD file is no longer available.";

            if (vod?.DurationSeconds > 0)
            {
                GameDurationS = vod.DurationSeconds;
                TotalTimeText = FormatTime(vod.DurationSeconds);
            }

            // Load game events for timeline
            var events = await _eventsRepo.GetEventsAsync(gameId);
            var loadedGameEvents = new ObservableCollection<TimelineEvent>();
            foreach (var e in events)
            {
                loadedGameEvents.Add(new TimelineEvent
                {
                    EventType = e.EventType,
                    GameTimeS = e.GameTimeS,
                    Details = e.Details,
                });
            }

            await DispatcherHelper.RunOnUIThreadAsync(() =>
            {
                GameEvents = loadedGameEvents;
                HasGameEvents = loadedGameEvents.Count > 0;
                ShowNoEventsHint = !HasGameEvents;
                GameEventsStatusText = HasGameEvents
                    ? $"{loadedGameEvents.Count} event(s). Click a marker to jump."
                    : "No live events.";
            });

            // Load derived events for the timeline regions. When the auto-fill
            // setting is OFF, the user wants a clean timeline with no auto game
            // events at all — so we skip populating these entirely (leaving only
            // their own bookmarks/clips on the bar).
            var inferredRegions = TimelineInferenceService.Infer(events);
            var loadedDerivedEvents = new ObservableCollection<DerivedEventRegion>();
            if (_configService.AutoTimelineClippingEnabled)
            {
                var derived = await _derivedEventsRepo.GetInstancesAsync(gameId);
                foreach (var de in derived)
                {
                    loadedDerivedEvents.Add(new DerivedEventRegion
                    {
                        StartTimeS = de.StartTimeSeconds,
                        EndTimeS = de.EndTimeSeconds,
                        Color = de.Color,
                        Name = de.DefinitionName,
                    });
                }

                foreach (var inferred in inferredRegions)
                {
                    loadedDerivedEvents.Add(new DerivedEventRegion
                    {
                        StartTimeS = inferred.StartTimeSeconds,
                        EndTimeS = inferred.EndTimeSeconds,
                        Color = inferred.Color,
                        Name = inferred.Name,
                        Tooltip = inferred.Tooltip,
                        IsInferred = true,
                    });
                }
            }

            await DispatcherHelper.RunOnUIThreadAsync(() =>
            {
                DerivedEvents = loadedDerivedEvents;
            });

            // Load bookmarks
            await RefreshBookmarksAsync();

            // Load active objectives for clip attachment
            await LoadObjectiveOptionsAsync();

            // v2.17.8: when the auto-fill setting is ON, generate NeedsReview
            // evidence rows from the inferred regions. When OFF, we skip this AND
            // skip the colored timeline regions above, so the timeline shows no
            // auto game events at all — only the user's own bookmarks/clips.
            if (_configService.AutoTimelineClippingEnabled)
            {
                await SyncEvidenceCandidatesAsync(inferredRegions);
            }
            await RefreshEvidenceInboxAsync();

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

    /// <summary>
    /// v2.17.14: losslessly remux a VOD that won't render in MediaPlayerElement
    /// (e.g. an Ascent capture that didn't close cleanly → a duplicate-moov MP4
    /// Media Foundation shows as a black screen). Returns the repaired file path
    /// the player should load instead, or null if repair wasn't possible.
    /// </summary>
    public async Task<string?> RepairVodForPlaybackAsync(string sourcePath)
    {
        try
        {
            var fixedPath = await _clipService.RemuxForPlaybackAsync(sourcePath);
            if (!string.IsNullOrEmpty(fixedPath))
            {
                _logger.LogInformation("Repaired VOD for playback: {Src} -> {Dst}", sourcePath, fixedPath);
            }
            return fixedPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RepairVodForPlaybackAsync failed for {Path}", sourcePath);
            return null;
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
                RefreshClipAvailabilityText();
                RefreshVisibleBookmarkItemsOnCurrentThread();
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

        BackgroundTaskRunner.Run(
            () => DeleteBookmarkQueuedAsync(bookmarkId, bookmark),
            _logger,
            $"delete VOD bookmark {bookmarkId}");
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
            if (bookmark.IsClip)
            {
                await _evidenceRepo.UpsertAsync(new EvidenceUpsert(
                    GameId: GameId,
                    SourceKind: EvidenceKinds.Clip,
                    SourceId: bookmark.Id,
                    SourceKey: $"clip:{bookmark.Id}",
                    StartTimeSeconds: bookmark.ClipStartSeconds ?? bookmark.GameTimeS,
                    EndTimeSeconds: bookmark.ClipStartSeconds ?? bookmark.GameTimeS,
                    Title: string.IsNullOrWhiteSpace(bookmark.Note) ? "Saved clip" : bookmark.Note,
                    Note: bookmark.Note,
                    ObjectiveId: request.ObjectiveId,
                    Polarity: string.IsNullOrWhiteSpace(bookmark.Quality) ? EvidencePolarities.Neutral : bookmark.Quality,
                    Status: string.IsNullOrWhiteSpace(bookmark.Quality) ? EvidenceStatuses.NeedsReview : EvidenceStatuses.Evidence));
                await RefreshEvidenceInboxAsync();
            }
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

        BackgroundTaskRunner.Run(
            () => SetBookmarkQualityQueuedAsync(originalBookmark, normalizedQuality),
            _logger,
            $"set VOD bookmark quality {originalBookmark.Id}");
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
            await _evidenceRepo.UpsertAsync(new EvidenceUpsert(
                GameId: GameId,
                SourceKind: EvidenceKinds.Clip,
                SourceId: originalBookmark.Id,
                SourceKey: $"clip:{originalBookmark.Id}",
                StartTimeSeconds: originalBookmark.ClipStartSeconds ?? originalBookmark.GameTimeS,
                EndTimeSeconds: originalBookmark.ClipStartSeconds ?? originalBookmark.GameTimeS,
                Title: string.IsNullOrWhiteSpace(originalBookmark.Note) ? "Saved clip" : originalBookmark.Note,
                Note: originalBookmark.Note,
                ObjectiveId: originalBookmark.ObjectiveId,
                Polarity: string.IsNullOrWhiteSpace(normalizedQuality) ? EvidencePolarities.Neutral : normalizedQuality,
                Status: string.IsNullOrWhiteSpace(normalizedQuality) ? EvidenceStatuses.NeedsReview : EvidenceStatuses.Evidence));
            await RefreshEvidenceInboxAsync();
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
        if (!HasPlayableVod && bookmark.HasPlayableClip)
        {
            ClipPlaybackRequested?.Invoke(bookmark);
            return;
        }

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

    [RelayCommand]
    private async Task SetEvidenceStatusAsync(EvidenceStatusUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var normalized = EvidenceStatuses.Normalize(request.Status);
        try
        {
            await _evidenceRepo.UpdateStatusAsync(evidence.Id, normalized);
            if (normalized == EvidenceStatuses.Dismissed)
            {
                DispatcherHelper.RunOnUIThread(() =>
                {
                    RemoveReviewMomentOnCurrentThread(evidence);
                });
            }
            else
            {
                evidence.Status = normalized;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update evidence status {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private void SetReviewMomentFilter(string? filter)
    {
        ReviewMomentFilter = filter?.ToLowerInvariant() switch
        {
            ReviewMomentFilterSaved => ReviewMomentFilterSaved,
            ReviewMomentFilterBookmarks => ReviewMomentFilterBookmarks,
            _ => ReviewMomentFilterAuto,
        };
    }

    /// <summary>
    /// Share a clip directly from the Clips tab (EvidenceInboxItem row). Resolves the
    /// underlying BookmarkItem via SourceId and delegates to ShareClipAsync.
    /// </summary>
    [RelayCommand]
    private async Task ShareEvidenceClipAsync(EvidenceInboxItem? evidence)
    {
        if (evidence is null || !evidence.IsSavedClip || evidence.SourceId is not long bookmarkId)
            return;
        var bookmark = Bookmarks.FirstOrDefault(b => b.Id == bookmarkId);
        if (bookmark is null) return;
        await ShareClipAsync(bookmark);
    }

    /// <summary>
    /// Delete a saved clip directly from the Clips tab (EvidenceInboxItem row). Resolves
    /// the underlying BookmarkItem via SourceId and delegates to DeleteBookmarkAsync.
    /// </summary>
    [RelayCommand]
    private Task DeleteEvidenceClipAsync(EvidenceInboxItem? evidence)
    {
        if (evidence is null || !evidence.IsSavedClip || evidence.SourceId is not long bookmarkId)
            return Task.CompletedTask;
        return DeleteBookmarkAsync(bookmarkId);
    }

    [RelayCommand]
    private async Task SetEvidencePolarityAsync(EvidencePolarityUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var normalized = EvidencePolarities.Normalize(request.Polarity);
        try
        {
            await _evidenceRepo.UpdatePolarityAsync(evidence.Id, normalized);
            evidence.Polarity = normalized;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update evidence polarity {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private async Task SetEvidenceObjectiveAsync(EvidenceObjectiveUpdateRequest? request)
    {
        if (request?.Evidence is not EvidenceInboxItem evidence || evidence.Id <= 0)
        {
            return;
        }

        var previous = evidence.ObjectiveId;
        evidence.ObjectiveId = request.ObjectiveId;
        try
        {
            await _evidenceRepo.UpdateObjectiveAsync(evidence.Id, request.ObjectiveId);
            if (request.ObjectiveId is long && evidence.Status == EvidenceStatuses.NeedsReview)
            {
                await _evidenceRepo.UpdateStatusAsync(evidence.Id, EvidenceStatuses.Evidence);
                evidence.Status = EvidenceStatuses.Evidence;
            }

            await MarkObjectivePracticedFromBookmarkAsync(request.ObjectiveId);
        }
        catch (Exception ex)
        {
            evidence.ObjectiveId = previous;
            _logger.LogError(ex, "Failed to set objective for evidence {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private async Task SaveEvidenceNoteAsync(EvidenceInboxItem? evidence)
    {
        if (evidence is null || evidence.Id <= 0)
        {
            return;
        }

        try
        {
            await _evidenceRepo.UpdateNoteAsync(evidence.Id, evidence.Note);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save evidence note {EvidenceId}", evidence.Id);
        }
    }

    [RelayCommand]
    private void OpenEvidence(EvidenceInboxItem? evidence)
    {
        if (evidence is null)
        {
            return;
        }

        if (evidence.SourceKind == EvidenceKinds.Clip
            && evidence.SourceId is long bookmarkId
            && Bookmarks.FirstOrDefault(b => b.Id == bookmarkId) is BookmarkItem bookmark
            && !HasPlayableVod
            && bookmark.HasPlayableClip)
        {
            ClipPlaybackRequested?.Invoke(bookmark);
            return;
        }

        if (evidence.StartTimeSeconds is int start)
        {
            SeekRequested?.Invoke(Math.Clamp(start - EvidenceJumpPreRollSeconds, 0, GameDurationS));
        }
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
                FileProbeCache.Invalidate(clipPath);
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
                    ClipStartSeconds = startS,
                    ClipEndSeconds = endS,
                    ClipPath = clipPath,
                    HasPlayableClip = FileProbeCache.Exists(clipPath),
                    Quality = quality,
                    ObjectiveId = objectiveId,
                    PromptId = promptId,
                    ObjectiveOptions = ObjectiveOptions,
                    TagOptions = TagOptions,
                });
                await MarkObjectivePracticedFromBookmarkAsync(objectiveId);
                await _evidenceRepo.UpsertAsync(new EvidenceUpsert(
                    GameId: GameId,
                    SourceKind: EvidenceKinds.Clip,
                    SourceId: bookmarkId,
                    SourceKey: $"clip:{bookmarkId}",
                    StartTimeSeconds: startS,
                    EndTimeSeconds: endS,
                    Title: note,
                    Note: note,
                    ObjectiveId: objectiveId,
                    Polarity: string.IsNullOrWhiteSpace(quality) ? EvidencePolarities.Neutral : quality,
                    Status: string.IsNullOrWhiteSpace(quality) ? EvidenceStatuses.NeedsReview : EvidenceStatuses.Evidence));
                await RefreshEvidenceInboxAsync();

                // Phase 4 hook: ask coach sidecar to generate frame descriptions.
                BackgroundTaskRunner.Run(
                    () => _coachNotifier.NotifyBookmarkCreatedAsync(bookmarkId),
                    _logger,
                    $"notify coach bookmark {bookmarkId}");

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

    /// <summary>
    /// v2.17.8: open Settings scrolled to the auto-Timeline-Inbox toggle. The
    /// parameter is the x:Name of the target card on SettingsPage; that page's
    /// OnNavigatedTo deep-links to it via StartBringIntoView.
    /// </summary>
    [RelayCommand]
    private void OpenAutoClippingSettings()
    {
        _navigationService.NavigateTo("settings", "AutoTimelineClippingCard");
    }

    /// <summary>
    /// v2.17.8: permanently hide the auto-clipping hint banner. Writes
    /// <c>AutoTimelineClippingHintDismissed=true</c> to config so the hint
    /// stays gone across sessions. Failures are logged but don't surface
    /// in UI — the user has dismissed something cosmetic.
    /// </summary>
    [RelayCommand]
    private async Task DismissAutoClippingHintAsync()
    {
        AutoClippingHintDismissed = true;
        try
        {
            var config = await _configService.LoadAsync();
            config.AutoTimelineClippingHintDismissed = true;
            await _configService.SaveAsync(config);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist AutoTimelineClippingHintDismissed");
        }
    }

    // â"€â"€ Public methods for the view â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

    /// <summary>Called by the view's position timer to update display.</summary>
    public void UpdatePosition(double seconds, double totalSeconds)
    {
        CurrentTimeS = seconds;
        var intSec = (int)seconds;
        if (intSec != _lastFormattedSecond)
        {
            _lastFormattedSecond = intSec;
            CurrentTimeText = FormatTime(intSec);
        }

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
        var loadedBookmarks = new ObservableCollection<BookmarkItem>();
        foreach (var b in bookmarks)
        {
            loadedBookmarks.Add(ToBookmarkItem(b));
        }

        await DispatcherHelper.RunOnUIThreadAsync(() =>
        {
            Bookmarks = loadedBookmarks;
            RefreshClipAvailabilityText();
            RefreshVisibleBookmarkItemsOnCurrentThread();
        });
    }

    private async Task SyncEvidenceCandidatesAsync(IReadOnlyList<InferredTimelineRegion> inferredRegions)
    {
        using var perf = PerformanceTrace.Time("VodPlayer.SyncEvidenceCandidates", $"gameId={GameId} inferred={inferredRegions.Count}");
        foreach (var inferred in inferredRegions)
        {
            await _evidenceRepo.UpsertAsync(new EvidenceUpsert(
                GameId: GameId,
                SourceKind: EvidenceKinds.TimelineRegion,
                SourceId: null,
                SourceKey: inferred.SourceKey,
                StartTimeSeconds: inferred.StartTimeSeconds,
                EndTimeSeconds: inferred.EndTimeSeconds,
                Title: inferred.Name,
                Note: inferred.Tooltip,
                Polarity: InferPolarityFromTitle(inferred.Name),
                Status: EvidenceStatuses.NeedsReview));
        }

        // Saved clips and inferred regions are both review moments. The UI
        // distinguishes the source, but keeps them in one queue so manual clips
        // and auto-picked regions do not feel like different workflows.
    }

    private async Task RefreshEvidenceInboxAsync()
    {
        var autoFill = _configService.AutoTimelineClippingEnabled;
        var rows = (await _evidenceRepo.GetForGameAsync(GameId))
            // When auto-fill is OFF, hide the auto-derived (timeline_region)
            // suggestions that are still untouched (NeedsReview) — that's the
            // noise the user turned off. Keep any the user actively promoted
            // (Evidence/Highlight) or wrote themselves so their work survives.
            .Where(row => autoFill
                || row.SourceKind != EvidenceKinds.TimelineRegion
                || row.Status != EvidenceStatuses.NeedsReview)
            .ToArray();

        // Build new item lists off the UI thread to keep dispatcher work minimal.
        var newEvidence = new List<EvidenceInboxItem>(rows.Length);
        var newAutoMoments = new List<EvidenceInboxItem>(rows.Length);
        var newSavedClipMoments = new List<EvidenceInboxItem>(rows.Length);
        foreach (var row in rows)
        {
            var item = ToEvidenceInboxItem(row);
            newEvidence.Add(item);
            if (item.IsSavedClip)
                newSavedClipMoments.Add(item);
            else
                newAutoMoments.Add(item);
        }

        await DispatcherHelper.RunOnUIThreadAsync(() =>
        {
            // Update collections in-place to avoid discarding all item containers.
            EvidenceInbox.Clear();
            foreach (var item in newEvidence) EvidenceInbox.Add(item);

            AutoReviewMoments.Clear();
            foreach (var item in newAutoMoments) AutoReviewMoments.Add(item);

            SavedClipReviewMoments.Clear();
            foreach (var item in newSavedClipMoments) SavedClipReviewMoments.Add(item);

            if (IsAutoReviewMomentFilterSelected && AutoReviewMoments.Count == 0 && SavedClipReviewMoments.Count > 0)
            {
                ReviewMomentFilter = ReviewMomentFilterSaved;
            }
            else if (IsSavedReviewMomentFilterSelected && SavedClipReviewMoments.Count == 0 && AutoReviewMoments.Count > 0)
            {
                ReviewMomentFilter = ReviewMomentFilterAuto;
            }
            else
            {
                RefreshVisibleReviewMomentsOnCurrentThread();
            }
            // Bookmarks tab has no auto-fallback — it's driven by plain bookmarks
            // that are always available regardless of evidence inbox content.
        });
    }

    private void RemoveReviewMomentOnCurrentThread(EvidenceInboxItem evidence)
    {
        EvidenceInbox.Remove(evidence);
        AutoReviewMoments.Remove(evidence);
        SavedClipReviewMoments.Remove(evidence);
        VisibleReviewMoments.Remove(evidence);
        RefreshReviewMomentSummaryOnCurrentThread();
    }

    private void RefreshVisibleReviewMomentsOnCurrentThread()
    {
        // Update in-place: avoid allocating a new collection and discarding
        // all item containers on every bookmark mutation.
        var source = IsSavedReviewMomentFilterSelected ? SavedClipReviewMoments : AutoReviewMoments;
        VisibleReviewMoments.Clear();
        foreach (var item in source) VisibleReviewMoments.Add(item);

        // Keep the plain-bookmark list in sync with whatever is in Bookmarks now.
        RefreshVisibleBookmarkItemsOnCurrentThread();

        RefreshReviewMomentSummaryOnCurrentThread();
    }

    private void RefreshReviewMomentSummaryOnCurrentThread()
    {
        HasEvidenceInboxItems = EvidenceInbox.Count > 0;
        OnPropertyChanged(nameof(AutoReviewMomentCount));
        OnPropertyChanged(nameof(SavedClipReviewMomentCount));
        OnPropertyChanged(nameof(BookmarkReviewMomentCount));
        OnPropertyChanged(nameof(HasVisibleReviewMoments));
        OnPropertyChanged(nameof(HasVisibleBookmarkItems));
        OnPropertyChanged(nameof(ShowEmptyReviewMomentsState));
        OnPropertyChanged(nameof(ShowReviewMomentsList));
        OnPropertyChanged(nameof(EmptyReviewMomentsText));
    }

    /// <summary>
    /// Syncs <see cref="VisibleBookmarkItems"/> (plain bookmarks, no clips) from the
    /// current <see cref="Bookmarks"/> collection. Must be called on the UI thread.
    /// </summary>
    private void RefreshVisibleBookmarkItemsOnCurrentThread()
    {
        VisibleBookmarkItems.Clear();
        foreach (var bm in Bookmarks)
        {
            if (!bm.IsClip) VisibleBookmarkItems.Add(bm);
        }
        OnPropertyChanged(nameof(BookmarkReviewMomentCount));
        OnPropertyChanged(nameof(HasVisibleBookmarkItems));
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

        BackgroundTaskRunner.Run(
            () => SaveBookmarkNoteQueuedAsync(bookmarkId, note, isClip, immediate, saveDelay),
            _logger,
            $"save VOD bookmark note {bookmarkId}");
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
        return _bookmarkMutationQueue.EnqueueAsync(async () =>
        {
            await mutation().ConfigureAwait(false);
            BroadcastBookmarkChanged();
        });
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

            // Already practiced -> leave alone. Otherwise, tagging a bookmark,
            // clip, or evidence item is the user's signal that this objective
            // was practiced; preserve any existing note while flipping it on.
            if (existingRow?.Practiced == true)
            {
                return;
            }

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
        return _bookmarkMutationQueue.EnqueueAsync(async () =>
        {
            var result = await mutation().ConfigureAwait(false);
            BroadcastBookmarkChanged();
            return result;
        });
    }

    private void BroadcastBookmarkChanged()
    {
        if (GameId <= 0) return;
        try { _messenger.Send(new Revu.Core.Lcu.BookmarkChangedMessage(GameId)); }
        catch (Exception ex) { _logger.LogDebug(ex, "BookmarkChanged broadcast failed"); }
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
                RefreshVisibleBookmarkItemsOnCurrentThread();
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
        RefreshClipAvailabilityText();
        RefreshVisibleBookmarkItemsOnCurrentThread();
    }

    private void RefreshClipAvailabilityText()
    {
        HasPlayableClips = Bookmarks.Any(bookmark => bookmark.HasPlayableClip);
        if (!HasPlayableVod && HasPlayableClips)
        {
            VodAvailabilityText = HasVod
                ? "The linked VOD file is missing. Saved clips can still be played here."
                : "No full VOD is linked. Saved clips can still be played here.";
        }
        else if (!HasPlayableVod)
        {
            VodAvailabilityText = HasVod
                ? "The linked VOD file is no longer available."
                : "No VOD linked to this game.";
        }
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
            ClipEndSeconds = record.ClipEndSeconds ?? 0,
            ClipPath = record.ClipPath,
            HasPlayableClip = isClip && FileProbeCache.Exists(record.ClipPath),
            ClipRangeText = record.ClipStartSeconds != null && record.ClipEndSeconds != null
                ? $"{FormatTime(record.ClipStartSeconds.Value)} - {FormatTime(record.ClipEndSeconds.Value)}"
                : "",
            Quality = record.Quality,
            ObjectiveId = record.ObjectiveId,
            PromptId = record.PromptId,
            ShareUrl = record.ShareUrl,
            ObjectiveOptions = ObjectiveOptions,
            TagOptions = TagOptions,
        };
    }

    private EvidenceInboxItem ToEvidenceInboxItem(EvidenceItemRecord record)
    {
        return new EvidenceInboxItem
        {
            Id = record.Id,
            GameId = record.GameId,
            SourceKind = record.SourceKind,
            SourceId = record.SourceId,
            SourceKey = record.SourceKey,
            StartTimeSeconds = record.StartTimeSeconds,
            EndTimeSeconds = record.EndTimeSeconds,
            Title = record.Title,
            Note = record.Note,
            ObjectiveId = record.ObjectiveId,
            ObjectiveOptions = ObjectiveOptions,
            TagOptions = TagOptions,
            Polarity = record.Polarity,
            Status = record.Status,
        };
    }

    private static string InferPolarityFromTitle(string title)
    {
        var normalized = (title ?? "").Trim().ToLowerInvariant();
        if (normalized.StartsWith("won ", StringComparison.Ordinal)
            || normalized.Contains(" pick", StringComparison.Ordinal))
        {
            return EvidencePolarities.Good;
        }

        if (normalized.StartsWith("lost ", StringComparison.Ordinal)
            || normalized.Contains("death", StringComparison.Ordinal))
        {
            return EvidencePolarities.Bad;
        }

        return EvidencePolarities.Neutral;
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

            await DispatcherHelper.RunOnUIThreadAsync(() =>
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

    partial void OnHasPlayableVodChanged(bool value)
    {
        OnPropertyChanged(nameof(VodStatusLabel));
        OnPropertyChanged(nameof(ShowClipFallbackHint));
    }

    partial void OnHasPlayableClipsChanged(bool value)
    {
        OnPropertyChanged(nameof(VodStatusLabel));
        OnPropertyChanged(nameof(ShowClipFallbackHint));
    }

    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlaybackStateLabel));

    partial void OnReviewMomentFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsAutoReviewMomentFilterSelected));
        OnPropertyChanged(nameof(IsSavedReviewMomentFilterSelected));
        OnPropertyChanged(nameof(IsBookmarkReviewMomentFilterSelected));
        OnPropertyChanged(nameof(AutoReviewFilterBackgroundBrush));
        OnPropertyChanged(nameof(AutoReviewFilterBorderBrush));
        OnPropertyChanged(nameof(AutoReviewFilterForegroundBrush));
        OnPropertyChanged(nameof(SavedReviewFilterBackgroundBrush));
        OnPropertyChanged(nameof(SavedReviewFilterBorderBrush));
        OnPropertyChanged(nameof(SavedReviewFilterForegroundBrush));
        OnPropertyChanged(nameof(BookmarkReviewFilterBackgroundBrush));
        OnPropertyChanged(nameof(BookmarkReviewFilterBorderBrush));
        OnPropertyChanged(nameof(BookmarkReviewFilterForegroundBrush));
        OnPropertyChanged(nameof(AutoTabForegroundBrush));
        OnPropertyChanged(nameof(SavedTabForegroundBrush));
        OnPropertyChanged(nameof(BookmarkTabForegroundBrush));
        OnPropertyChanged(nameof(ShowEmptyReviewMomentsState));
        OnPropertyChanged(nameof(ShowReviewMomentsList));
        RefreshVisibleReviewMomentsOnCurrentThread();
    }

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

    // ── Share commands ────────────────────────────────────────────

    /// <summary>
    /// Share a clip publicly. If already shared, copies the existing link.
    /// Otherwise uploads it (prompting inline login first if logged out) and
    /// copies the resulting revu.lol/&lt;id&gt; link.
    /// </summary>
    [RelayCommand]
    private async Task ShareClipAsync(BookmarkItem? item)
    {
        if (item is null || !item.IsClip) return;

        if (item.IsShared)
        {
            CopyToClipboard(item.ShareUrl);
            ShareStatusText = "Link copied.";
            return;
        }

        if (item.IsSharing) return;

        if (string.IsNullOrEmpty(item.ClipPath) || !File.Exists(item.ClipPath))
        {
            ShareStatusText = "Clip file not found on disk.";
            return;
        }

        // Enforce the 90s cap on the desktop (the server bounds bytes, not duration).
        var duration = Math.Max(0, item.ClipEndSeconds - (item.ClipStartSeconds ?? 0));
        if (duration > MaxShareDurationSeconds)
        {
            ShareStatusText = $"Clips can be up to {MaxShareDurationSeconds}s — trim and re-clip.";
            return;
        }

        var config = await _configService.LoadAsync();
        if (string.IsNullOrWhiteSpace(config.RiotSessionToken))
        {
            // Logged out → open the inline login prompt; upload after login.
            _pendingShareItem = item;
            ShareEmail = config.RiotSessionEmail ?? "";
            ShareOtp = "";
            ShareAwaitingOtp = false;
            ShareStatusText = "Log in to share clips.";
            ShareLoginVisible = true;
            return;
        }

        await UploadClipAsync(item, config.RiotSessionToken, duration);
    }

    private async Task UploadClipAsync(BookmarkItem item, string sessionToken, int duration)
    {
        item.IsSharing = true;
        ShareStatusText = "Uploading clip…";
        try
        {
            var result = await _clipUploadService.UploadAsync(
                item.ClipPath,
                sessionToken,
                title: string.IsNullOrWhiteSpace(item.Note) ? null : item.Note,
                champion: string.IsNullOrWhiteSpace(ChampionName) ? null : ChampionName,
                durationSeconds: duration > 0 ? duration : null);

            item.ShareUrl = result.Url;
            await _vodRepo.SetBookmarkShareUrlAsync(item.Id, result.Url);
            CopyToClipboard(result.Url);
            ShareStatusText = "Link copied — anyone can watch (expires in 30 days).";
            _logger.LogInformation("Shared clip {Id} -> {Url}", item.Id, result.Url);
        }
        catch (ClipUploadException ex)
        {
            ShareStatusText = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clip upload failed");
            ShareStatusText = "Couldn't share the clip.";
        }
        finally
        {
            item.IsSharing = false;
        }
    }

    /// <summary>Send the magic-link OTP to the entered email (login-to-share step 1).</summary>
    [RelayCommand]
    private async Task SendShareOtpAsync()
    {
        if (ShareBusy) return;
        var email = (ShareEmail ?? "").Trim();
        if (string.IsNullOrEmpty(email)) { ShareStatusText = "Enter your email."; return; }

        ShareBusy = true;
        try
        {
            await _authClient.LoginAsync(email);
            ShareAwaitingOtp = true;
            ShareStatusText = $"Check {email} for a code.";
            ShareOtp = "";
        }
        catch (RiotAuthException ex) { ShareStatusText = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Share login: send code failed");
            ShareStatusText = "Couldn't reach the server. Check your connection.";
        }
        finally { ShareBusy = false; }
    }

    /// <summary>Verify the OTP, store the session, then upload the pending clip
    /// (login-to-share step 2).</summary>
    [RelayCommand]
    private async Task VerifyShareOtpAsync()
    {
        if (ShareBusy) return;
        var code = (ShareOtp ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code)) { ShareStatusText = "Paste the code from your email."; return; }

        ShareBusy = true;
        try
        {
            var result = await _authClient.VerifyAsync(code);
            var config = await _configService.LoadAsync();
            config.RiotSessionToken = result.SessionToken;
            config.RiotSessionEmail = (ShareEmail ?? "").Trim();
            config.RiotSessionExpiresAt = result.ExpiresAt;
            await _configService.SaveAsync(config);

            ShareLoginVisible = false;
            ShareAwaitingOtp = false;

            var item = _pendingShareItem;
            _pendingShareItem = null;
            if (item is not null)
            {
                var duration = Math.Max(0, item.ClipEndSeconds - (item.ClipStartSeconds ?? 0));
                await UploadClipAsync(item, result.SessionToken, duration);
            }
        }
        catch (RiotAuthException ex) { ShareStatusText = ex.Message; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Share login: verify failed");
            ShareStatusText = "Couldn't verify the code.";
        }
        finally { ShareBusy = false; }
    }

    /// <summary>Dismiss the inline login prompt without sharing.</summary>
    [RelayCommand]
    private void CancelShareLogin()
    {
        ShareLoginVisible = false;
        ShareAwaitingOtp = false;
        ShareOtp = "";
        _pendingShareItem = null;
        ShareStatusText = "";
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
            pkg.SetText(text);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
        }
        catch
        {
            // Clipboard can throw transiently if another app holds it; the link
            // is still saved on the bookmark, so this is non-fatal.
        }
    }
}

// â"€â"€ Display models â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€â"€

public partial class EvidenceInboxItem : ObservableObject
{
    public long Id { get; set; }
    public long GameId { get; set; }
    public string SourceKind { get; set; } = EvidenceKinds.TimelineRegion;
    public long? SourceId { get; set; }
    public string SourceKey { get; set; } = "";
    public int? StartTimeSeconds { get; set; }
    public int? EndTimeSeconds { get; set; }
    public string Title { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNote))]
    private string _note = "";

    public bool HasNote => !string.IsNullOrWhiteSpace(Note);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailsToggleLabel))]
    [NotifyPropertyChangedFor(nameof(ExpandedDetails))]
    private bool _isExpanded;

    public string DetailsToggleLabel => IsExpanded ? "Hide details" : "Details";

    public EvidenceInboxItem? ExpandedDetails => IsExpanded ? this : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ObjectiveTitleDisplay))]
    private long? _objectiveId;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PolarityLabel))]
    [NotifyPropertyChangedFor(nameof(PolarityAccentBrush))]
    private string _polarity = EvidencePolarities.Neutral;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(StatusAccentBrush))]
    private string _status = EvidenceStatuses.NeedsReview;

    public ObservableCollection<ObjectiveOption>? ObjectiveOptions { get; set; }

    public ObservableCollection<TagOption>? TagOptions { get; set; }

    public bool IsSavedClip => EvidenceKinds.Normalize(SourceKind) == EvidenceKinds.Clip;

    public string SourceLabel => IsSavedClip ? "Saved clip" : "Auto pick";

    public string SourceShortLabel => IsSavedClip ? "CLIP" : "AUTO";

    public string SourceGlyph => IsSavedClip ? "\uE7C3" : "\uE8B7";

    public string ReviewActionLabel => IsSavedClip ? "Play clip" : "Open";

    public SolidColorBrush SourceAccentBrush => AppSemanticPalette.Brush(IsSavedClip
        ? AppSemanticPalette.AccentGoldHex
        : AppSemanticPalette.AccentBlueHex);

    public SolidColorBrush SourceSurfaceBrush => AppSemanticPalette.Brush(IsSavedClip
        ? AppSemanticPalette.AccentGoldDimHex
        : AppSemanticPalette.AccentBlueDimHex);

    public string TimeRangeText
    {
        get
        {
            if (StartTimeSeconds is not int start)
            {
                return "";
            }

            if (EndTimeSeconds is int end && end > start)
            {
                return $"{VodPlayerViewModel.FormatTime(start)} - {VodPlayerViewModel.FormatTime(end)}";
            }

            return VodPlayerViewModel.FormatTime(start);
        }
    }

    public string ObjectiveTitleDisplay
    {
        get
        {
            if (ObjectiveId is null) return "(no objective)";
            var match = ObjectiveOptions?.FirstOrDefault(o => o.Id == ObjectiveId);
            return match?.Title ?? "(no objective)";
        }
    }

    public string StatusLabel => EvidenceStatuses.Normalize(Status) switch
    {
        EvidenceStatuses.Evidence => "Evidence",
        EvidenceStatuses.Highlight => "Highlight",
        EvidenceStatuses.Dismissed => "Dismissed",
        _ => "Needs review",
    };

    public string PolarityLabel => EvidencePolarities.Normalize(Polarity) switch
    {
        EvidencePolarities.Good => "Good",
        EvidencePolarities.Bad => "Bad",
        _ => "Neutral",
    };

    public SolidColorBrush StatusAccentBrush => AppSemanticPalette.Brush(EvidenceStatuses.Normalize(Status) switch
    {
        EvidenceStatuses.Evidence => AppSemanticPalette.AccentTealHex,
        EvidenceStatuses.Highlight => AppSemanticPalette.AccentGoldHex,
        EvidenceStatuses.Dismissed => AppSemanticPalette.MutedTextHex,
        _ => AppSemanticPalette.AccentBlueHex,
    });

    public SolidColorBrush PolarityAccentBrush => AppSemanticPalette.Brush(EvidencePolarities.Normalize(Polarity) switch
    {
        EvidencePolarities.Good => AppSemanticPalette.PositiveHex,
        EvidencePolarities.Bad => AppSemanticPalette.NegativeHex,
        _ => AppSemanticPalette.NeutralHex,
    });
}

public partial class BookmarkItem : ObservableObject
{
    public long Id { get; set; }
    public int GameTimeS { get; set; }
    public string TimeText { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryTitle))]
    private string _note = "";

    public bool IsClip { get; set; }
    /// <summary>v2.15.10: when this is a clip, where the clip range starts.
    /// Null for plain note bookmarks. Used by Jump to seek to the first frame
    /// of the clip rather than the marker time, which often sits in the middle.</summary>
    public int? ClipStartSeconds { get; set; }
    /// <summary>Clip range end in seconds; with ClipStartSeconds gives the
    /// shared clip's duration. 0 for plain note bookmarks.</summary>
    public int ClipEndSeconds { get; set; }
    public string ClipPath { get; set; } = "";
    public bool HasPlayableClip { get; set; }
    public string ClipRangeText { get; set; } = "";
    public string Quality { get; set; } = "";

    // ── Public sharing (revu.lol/<id>) ────────────────────────────
    // ShareUrl is empty until the clip has been uploaded. While uploading,
    // IsSharing flips the button to a busy state. These are observable so the
    // per-row Share button updates without rebuilding the list.

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShared))]
    [NotifyPropertyChangedFor(nameof(ShareActionLabel))]
    private string _shareUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareActionLabel))]
    private bool _isSharing;

    /// <summary>True once this clip has a public link.</summary>
    public bool IsShared => !string.IsNullOrEmpty(ShareUrl);

    /// <summary>Label for the per-row share/copy button.</summary>
    public string ShareActionLabel =>
        IsSharing ? "Sharing…" : IsShared ? "Copy link" : "Share";

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
    public string LibraryTitle => string.IsNullOrWhiteSpace(Note)
        ? IsClip ? "Saved clip" : "Bookmark"
        : Note;
    public string LibraryDetailText => IsClip && !string.IsNullOrWhiteSpace(ClipRangeText)
        ? ClipRangeText
        : $"At {TimeText}";
    public string JumpActionLabel => IsClip && HasPlayableClip ? "Play" : "Jump";
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
            ClipStartSeconds = ClipStartSeconds,
            ClipEndSeconds = ClipEndSeconds,
            ClipPath = ClipPath,
            HasPlayableClip = HasPlayableClip,
            ClipRangeText = ClipRangeText,
            Quality = quality,
            ObjectiveId = ObjectiveId,
            PromptId = PromptId,
            ShareUrl = ShareUrl,
            ObjectiveOptions = ObjectiveOptions,
            TagOptions = TagOptions,
        };
    }
}

public sealed record BookmarkQualityUpdateRequest(BookmarkItem Bookmark, string? Quality);

public sealed record BookmarkObjectiveUpdateRequest(BookmarkItem Bookmark, long? ObjectiveId);

/// <summary>v2.15.7: unified tag update — covers objective and optional prompt.</summary>
public sealed record BookmarkTagUpdateRequest(BookmarkItem Bookmark, long? ObjectiveId, long? PromptId);

public sealed record EvidenceStatusUpdateRequest(EvidenceInboxItem Evidence, string Status);

public sealed record EvidencePolarityUpdateRequest(EvidenceInboxItem Evidence, string Polarity);

public sealed record EvidenceObjectiveUpdateRequest(EvidenceInboxItem Evidence, long? ObjectiveId);

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

    /// <summary>Compact three-letter tag rendered next to the marker on the timeline,
    /// so users see *what* the marker means without hovering.</summary>
    public string ShortLabel => EventType.ToUpperInvariant() switch
    {
        "KILL"           => "KIL",
        "DEATH"          => "DTH",
        "ASSIST"         => "AST",
        "DRAGON"         => "DRG",
        "BARON"          => "BAR",
        "HERALD"         => "HRD",
        "TURRET"         => "TWR",
        "INHIBITOR"      => "INH",
        "FIRST_BLOOD"    => "FB",
        "MULTI_KILL"     => "MLT",
        "LEVEL_UP"       => "LVL",
        "FLASH"          => "FLASH",
        "SUMMONER_SPELL" => "SUM",
        _                => "EVT",
    };
    public bool IsCombatEvent => EventType.ToUpperInvariant() is "KILL" or "DEATH" or "ASSIST" or "FIRST_BLOOD" or "MULTI_KILL";
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
        // v2.17.7: Flash and other summoner spells. Flash gets the brighter teal
        // because it's the spell players hunt for in review; generic SUM uses
        // the calmer blue so it doesn't visually drown out kills/deaths.
        "FLASH" => AppSemanticPalette.AccentTealHex,
        "SUMMONER_SPELL" => AppSemanticPalette.AccentBlueHex,
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
        "FLASH" => AppSemanticPalette.AccentTealDimHex,
        "SUMMONER_SPELL" => AppSemanticPalette.AccentBlueDimHex,
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
        // v2.17.7: render summoner-spell casts as squares so they don't get
        // confused with the diamond shape used for assists/objectives.
        "FLASH" or "SUMMONER_SPELL" => MarkerShape.Square,
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
        "FLASH" => "Flash",
        "SUMMONER_SPELL" => "Summoner Spell",
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
                "FLASH" or "SUMMONER_SPELL" => ReadValue(root, "spell"),
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
    public string Tooltip { get; set; } = "";
    public bool IsInferred { get; set; }
    public string ShortLabel
    {
        get
        {
            var text = Name;
            foreach (var prefix in new[] { "Won ", "Lost " })
            {
                if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    text = text[prefix.Length..];
                    break;
                }
            }

            return text switch
            {
                var value when value.Contains("Dragon", StringComparison.OrdinalIgnoreCase) => "DRG FIGHT",
                var value when value.Contains("Baron", StringComparison.OrdinalIgnoreCase) => "BAR FIGHT",
                var value when value.Contains("Herald", StringComparison.OrdinalIgnoreCase) => "HRD FIGHT",
                var value when value.Contains("Teamfight", StringComparison.OrdinalIgnoreCase) => "TEAMFIGHT",
                var value when value.Contains("3v3", StringComparison.OrdinalIgnoreCase) => "3v3",
                var value when value.Contains("2v2", StringComparison.OrdinalIgnoreCase) => "2v2",
                var value when value.Contains("death", StringComparison.OrdinalIgnoreCase) => "DEATH",
                _ => "PICK",
            };
        }
    }
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
