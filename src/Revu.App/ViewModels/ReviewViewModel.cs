#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

/// <summary>ViewModel for the inline game review page.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IReviewWorkflowService _reviewWorkflowService;
    private readonly INavigationService _navigationService;
    private readonly IPromptsRepository _promptsRepository;
    private readonly IObjectivesRepository _objectivesRepository;
    private readonly IVodRepository _vodRepository;
    private readonly ILogger<ReviewViewModel> _logger;

    [ObservableProperty] private long _gameId;
    [ObservableProperty] private string _championName = "";
    [ObservableProperty] private bool _win;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string _resultColorHex = AppSemanticPalette.PrimaryTextHex;
    [ObservableProperty] private string _kdaText = "";
    [ObservableProperty] private string _kdaRatioText = "";
    [ObservableProperty] private string _gameModeText = "";
    [ObservableProperty] private string _durationText = "";
    [ObservableProperty] private string _headerText = "Review";
    [ObservableProperty] private string _enemyLaner = "";
    [ObservableProperty] private bool _hasEnemyLaner;
    [ObservableProperty] private string _damageText = "";
    [ObservableProperty] private string _csText = "";
    [ObservableProperty] private string _csPerMinText = "";
    [ObservableProperty] private string _visionText = "";
    [ObservableProperty] private string _goldText = "";
    [ObservableProperty] private string _killParticipationText = "";
    [ObservableProperty] private string _damageTakenText = "";
    [ObservableProperty] private string _wardsPlacedText = "";
    [ObservableProperty] private bool _hasVod;
    [ObservableProperty] private int _bookmarkCount;
    [ObservableProperty] private int _mentalRating = 5;
    [ObservableProperty] private string _mentalRatingColorHex = AppSemanticPalette.AccentBlueHex;
    [ObservableProperty] private string _wentWell = "";
    [ObservableProperty] private string _mistakes = "";
    [ObservableProperty] private string _focusNext = "";
    [ObservableProperty] private string _reviewNotes = "";
    [ObservableProperty] private string _improvementNote = "";
    [ObservableProperty] private string _attribution = "";
    [ObservableProperty] private string _mentalHandled = "";
    [ObservableProperty] private string _spottedProblems = "";
    [ObservableProperty] private string _outsideControl = "";
    [ObservableProperty] private string _withinControl = "";
    [ObservableProperty] private string _personalContribution = "";
    [ObservableProperty] private string _matchupNote = "";
    [ObservableProperty] private bool _requireReviewNotes;
    [ObservableProperty] private string _saveBehaviorText = "";
    [ObservableProperty] private string _validationMessage = "";
    [ObservableProperty] private bool _hasValidationMessage;
    [ObservableProperty] private bool _showMentalReflection;
    [ObservableProperty] private bool _isLoading;
    private CancellationTokenSource? _vodRetryCts;
    [ObservableProperty] private bool _hasObjectives;
    [ObservableProperty] private bool _hasMatchupHistory;
    [ObservableProperty] private string _priorityObjectiveTitle = "";
    [ObservableProperty] private string _priorityObjectiveCriteria = "";
    [ObservableProperty] private bool _hasPriorityObjective;
    // v2.15.0: show the collapsible "legacy fields" card only when this game
    // was reviewed on an earlier version and has data in the cut columns.
    [ObservableProperty] private bool _showLegacyFields;

    public ObservableCollection<ConceptTagItem> AllTags { get; } = new();
    public ObservableCollection<long> SelectedTagIds { get; } = new();
    public ObservableCollection<ObjectiveAssessment> ObjectiveAssessments { get; } = new();
    public ObservableCollection<MatchupHistoryItem> MatchupHistory { get; } = new();

    public static IReadOnlyList<string> AttributionOptions { get; } =
    [
        "My play",
        "Team effort",
        "Teammates",
        "External"
    ];

    public string MatchupHeading => HasEnemyLaner
        ? $"{ChampionName} vs {EnemyLaner}"
        : $"{ChampionName} matchup notes";

    public ReviewViewModel(
        IReviewWorkflowService reviewWorkflowService,
        INavigationService navigationService,
        IPromptsRepository promptsRepository,
        IObjectivesRepository objectivesRepository,
        IVodRepository vodRepository,
        ILogger<ReviewViewModel> logger)
    {
        _reviewWorkflowService = reviewWorkflowService;
        _navigationService = navigationService;
        _promptsRepository = promptsRepository;
        _objectivesRepository = objectivesRepository;
        _vodRepository = vodRepository;
        _logger = logger;
    }

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        _vodRetryCts?.Cancel();
        ClearValidation();

        try
        {
            var screenData = await _reviewWorkflowService.LoadAsync(gameId);
            if (screenData is null)
            {
                _logger.LogWarning("Game {GameId} not found", gameId);
                SetValidation("Failed to load review data for this game.");
                return;
            }

            GameId = gameId;
            ApplyGameData(screenData);
            ApplySnapshot(screenData.Snapshot);
            ApplyTags(screenData.Tags);
            ApplyObjectives(screenData.ObjectiveAssessments, screenData.PriorityObjective);
            ApplyMatchupHistory(screenData.MatchupHistory);

            // v2.15.0: legacy-field visibility is recomputed from the already-
            // loaded snapshot (SelfAssessment etc. live in Snapshot). True when
            // any of the v2.14-and-earlier review fields are populated.
            ShowLegacyFields = !string.IsNullOrWhiteSpace(WentWell)
                            || !string.IsNullOrWhiteSpace(Mistakes)
                            || !string.IsNullOrWhiteSpace(FocusNext)
                            || !string.IsNullOrWhiteSpace(OutsideControl)
                            || !string.IsNullOrWhiteSpace(WithinControl)
                            || !string.IsNullOrWhiteSpace(Attribution)
                            || !string.IsNullOrWhiteSpace(PersonalContribution)
                            || !string.IsNullOrWhiteSpace(ImprovementNote);

            // v2.15.0: load custom prompts for each post-phase assessment and
            // hydrate previously-saved answers for this game.
            await HydratePromptsAsync(gameId);

            // v2.15.0: bookmark/clip autopopulate — injects [MM:SS] lines into
            // each assessment's general-notes field from bookmarks/clips
            // assigned to that objective on this game.
            await AutoPopulateBookmarkNotesAsync(gameId);

            RequireReviewNotes = screenData.RequireReviewNotes;
            HasVod = screenData.HasVod;
            BookmarkCount = screenData.BookmarkCount;
            UpdateSaveBehaviorText();

            if (!HasVod)
            {
                ScheduleVodRecheck(gameId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load review for game {GameId}", gameId);
            SetValidation("Failed to load review data for this game.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// v2.15.0: load in-game + post-game custom prompts for each active
    /// objective assessment, hydrate previously-saved answers for
    /// <paramref name="gameId"/>, and apply champion-gating to filter out
    /// objectives that don't apply to this game's champion (unless the user
    /// already interacted with them — we never hide data they wrote).
    /// Non-fatal — prompts UI just hides on failure.
    /// </summary>
    private async Task HydratePromptsAsync(long gameId)
    {
        try
        {
            // Pull saved answers once; group by prompt id for O(1) lookup below.
            var saved = await _promptsRepository.GetAnswersForGameAsync(gameId);
            var answersByPromptId = saved.ToDictionary(a => a.PromptId, a => a.AnswerText);

            // Track which assessments to drop due to champion mismatch.
            // Never drop an assessment the user already touched: practiced
            // toggle, execution note, or an existing prompt answer.
            var toRemove = new List<ObjectiveAssessment>();
            var currentChampion = (ChampionName ?? "").Trim();

            foreach (var assessment in ObjectiveAssessments)
            {
                var prompts = await _promptsRepository.GetPromptsForObjectiveAsync(assessment.ObjectiveId);
                assessment.Prompts.Clear();
                bool hasAnyAnswerForThisGame = false;
                foreach (var p in prompts)
                {
                    // Only render phases that are meaningful post-game: ingame + postgame.
                    // Pre-game prompts live on the champ-select surface, not here.
                    if (p.Phase != ObjectivePhases.InGame && p.Phase != ObjectivePhases.PostGame)
                        continue;

                    var text = answersByPromptId.TryGetValue(p.Id, out var t) ? t : "";
                    if (!string.IsNullOrWhiteSpace(text)) hasAnyAnswerForThisGame = true;

                    assessment.Prompts.Add(new PromptAnswerField
                    {
                        PromptId = p.Id,
                        Phase = p.Phase,
                        Label = p.Label,
                        AnswerText = text,
                    });
                }

                // Apply champion-gate filter — but only if the user hasn't
                // already touched this assessment for this game.
                if (!string.IsNullOrWhiteSpace(currentChampion))
                {
                    var userTouchedIt = assessment.Practiced
                                       || !string.IsNullOrWhiteSpace(assessment.ExecutionNote)
                                       || hasAnyAnswerForThisGame;
                    if (!userTouchedIt)
                    {
                        var champs = await _objectivesRepository.GetChampionsForObjectiveAsync(assessment.ObjectiveId);
                        if (champs.Count > 0
                            && !champs.Any(c => string.Equals(c, currentChampion, StringComparison.OrdinalIgnoreCase)))
                        {
                            toRemove.Add(assessment);
                        }
                    }
                }
            }

            foreach (var ass in toRemove) ObjectiveAssessments.Remove(ass);
            HasObjectives = ObjectiveAssessments.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to hydrate custom prompts for game {GameId}", gameId);
        }
    }

    /// <summary>
    /// v2.15.0: for each <see cref="ObjectiveAssessment"/>, format any bookmarks
    /// + clips assigned to it (on this game) as <c>[MM:SS] note</c> lines and
    /// merge them into the assessment's <c>ExecutionNote</c>. Never overwrites
    /// user content — uses substring detection to avoid re-appending on reload.
    /// </summary>
    private async Task AutoPopulateBookmarkNotesAsync(long gameId)
    {
        try
        {
            var bookmarks = await _vodRepository.GetBookmarksAsync(gameId);
            if (bookmarks.Count == 0) return;

            // Per-objective bookmarks → that objective's general notes.
            foreach (var assessment in ObjectiveAssessments)
            {
                var relevant = bookmarks
                    .Where(b => b.ObjectiveId == assessment.ObjectiveId)
                    .OrderBy(b => b.GameTimeSeconds)
                    .ToList();
                if (relevant.Count == 0) continue;
                AppendBookmarkLines(relevant, s => assessment.ExecutionNote ?? "",
                                    t => assessment.ExecutionNote = t);
            }

            // v2.15.5: bookmarks without an objective (bulk of the user's
            // existing data — most bookmarks shipped untagged) go into the
            // "anything else you noticed" box so they're not invisible.
            var untagged = bookmarks
                .Where(b => b.ObjectiveId is null)
                .OrderBy(b => b.GameTimeSeconds)
                .ToList();
            if (untagged.Count > 0)
            {
                AppendBookmarkLines(untagged, _ => SpottedProblems ?? "",
                                    t => SpottedProblems = t);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Bookmark autopopulate failed for game {GameId}", gameId);
        }
    }

    /// <summary>
    /// v2.15.5: shared append logic for autopopulating bookmark lines into a
    /// free-text field. Uses substring detection to avoid re-appending on
    /// reload, and appends with a "— Bookmarks/clips —" separator header
    /// when the target already has user-typed content.
    /// </summary>
    private static void AppendBookmarkLines(
        IReadOnlyList<Revu.Core.Data.Repositories.VodBookmarkRecord> bookmarks,
        Func<string, string> getCurrent,
        Action<string> setNext)
    {
        var lines = new List<string>(bookmarks.Count);
        foreach (var bm in bookmarks)
        {
            var ts = FormatGameTime(bm.GameTimeSeconds);
            var note = (bm.Note ?? "").Trim();
            var clipTag = (bm.ClipStartSeconds.HasValue && bm.ClipEndSeconds.HasValue)
                ? $" (clip {FormatGameTime(bm.ClipStartSeconds.Value)}–{FormatGameTime(bm.ClipEndSeconds.Value)})"
                : "";
            var line = string.IsNullOrEmpty(note)
                ? $"[{ts}]{clipTag}"
                : $"[{ts}] {note}{clipTag}";
            lines.Add(line);
        }

        var currentNote = getCurrent("");
        var toAdd = lines.Where(l => !currentNote.Contains(l, StringComparison.Ordinal)).ToList();
        if (toAdd.Count == 0) return;

        var additionBlock = string.Join("\n", toAdd);
        if (string.IsNullOrWhiteSpace(currentNote))
        {
            setNext(additionBlock);
        }
        else
        {
            var separator = currentNote.TrimEnd().EndsWith("— Bookmarks/clips —", StringComparison.Ordinal)
                ? "\n"
                : "\n\n— Bookmarks/clips —\n";
            setNext(currentNote + separator + additionBlock);
        }
    }

    private static string FormatGameTime(int seconds)
    {
        if (seconds < 0) seconds = 0;
        return $"{seconds / 60:D2}:{seconds % 60:D2}";
    }

    /// <summary>
    /// v2.15.0: persist current prompt answers for each assessment. Runs after
    /// SaveCoreAsync's main write path. Empty/whitespace answers delete the row.
    /// </summary>
    private async Task PersistPromptAnswersAsync(long gameId)
    {
        foreach (var assessment in ObjectiveAssessments)
        {
            foreach (var field in assessment.Prompts)
            {
                try
                {
                    await _promptsRepository.SaveAnswerAsync(field.PromptId, gameId, field.AnswerText ?? "");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to persist prompt answer game={GameId} prompt={PromptId}",
                        gameId, field.PromptId);
                }
            }
        }
    }

    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        await SaveCoreAsync(navigateBackOnSuccess: true);
    }

    [RelayCommand]
    private void Cancel()
    {
        _vodRetryCts?.Cancel();
        _navigationService.GoBack();
    }

    [RelayCommand]
    private async Task WatchVodAsync()
    {
        var saved = await _reviewWorkflowService.SaveDraftAsync(new ReviewDraftRequest(GameId, BuildSnapshot()));
        if (!saved)
        {
            SetValidation("Couldn't preserve your review draft before opening the VOD.");
            return;
        }

        _navigationService.NavigateTo("vodplayer", GameId);
    }

    [RelayCommand]
    private void ToggleTag(ConceptTagItem tag)
    {
        tag.IsSelected = !tag.IsSelected;
        if (tag.IsSelected)
        {
            if (!SelectedTagIds.Contains(tag.Id))
            {
                SelectedTagIds.Add(tag.Id);
            }
        }
        else
        {
            SelectedTagIds.Remove(tag.Id);
        }
    }

    public Task<bool> SaveForPostGameAsync() => SaveCoreAsync(navigateBackOnSuccess: false);

    partial void OnMentalRatingChanged(int value)
    {
        UpdateMentalColor();
        ShowMentalReflection = value <= 3;
    }

    partial void OnRequireReviewNotesChanged(bool value)
    {
        UpdateSaveBehaviorText();
    }

    partial void OnEnemyLanerChanged(string value)
    {
        HasEnemyLaner = !string.IsNullOrWhiteSpace(value);
        OnPropertyChanged(nameof(MatchupHeading));

        if (!IsLoading && GameId > 0)
        {
            _ = LoadMatchupHistoryAsync(value.Trim());
        }
    }

    private async Task<bool> SaveCoreAsync(bool navigateBackOnSuccess)
    {
        try
        {
            var result = await _reviewWorkflowService.SaveAsync(new SaveReviewRequest(
                GameId: GameId,
                ChampionName: ChampionName,
                Win: Win,
                RequireReviewNotes: RequireReviewNotes,
                Snapshot: BuildSnapshot()));

            if (!result.Success)
            {
                SetValidation(result.ErrorMessage);
                return false;
            }

            EnemyLaner = result.SavedEnemyLaner;
            ClearValidation();

            // v2.15.0: persist custom prompt answers alongside the legacy save.
            // Non-fatal on error — the main review save already succeeded.
            await PersistPromptAnswersAsync(GameId);

            if (navigateBackOnSuccess)
            {
                _navigationService.GoBack();
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save review for game {GameId}", GameId);
            SetValidation("Failed to save review. Check the logs and try again.");
            return false;
        }
    }

    private async Task LoadMatchupHistoryAsync(string enemyLaner)
    {
        try
        {
            var history = await _reviewWorkflowService.GetMatchupHistoryAsync(ChampionName, enemyLaner, GameId);
            ApplyMatchupHistory(history);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load matchup history for game {GameId}", GameId);
        }
    }

    private ReviewSnapshot BuildSnapshot()
    {
        return new ReviewSnapshot(
            MentalRating: MentalRating,
            WentWell: WentWell,
            Mistakes: Mistakes,
            FocusNext: FocusNext,
            ReviewNotes: ReviewNotes,
            ImprovementNote: ImprovementNote,
            Attribution: Attribution,
            MentalHandled: MentalHandled,
            SpottedProblems: SpottedProblems,
            OutsideControl: OutsideControl,
            WithinControl: WithinControl,
            PersonalContribution: PersonalContribution,
            EnemyLaner: EnemyLaner,
            MatchupNote: MatchupNote,
            SelectedTagIds: AllTags.Where(static tag => tag.IsSelected).Select(static tag => tag.Id).ToList(),
            ObjectivePractices: ObjectiveAssessments
                .Select(static assessment => new SaveObjectivePracticeRequest(
                    ObjectiveId: assessment.ObjectiveId,
                    Practiced: assessment.Practiced,
                    ExecutionNote: assessment.ExecutionNote))
                .ToList());
    }

    private void ApplyGameData(ReviewScreenData screenData)
    {
        var game = screenData.Game;
        ChampionName = game.ChampionName;
        Win = game.Win;
        ResultText = game.Win ? "VICTORY" : "DEFEAT";
        ResultColorHex = game.Win ? AppSemanticPalette.PositiveHex : AppSemanticPalette.NegativeHex;
        KdaText = $"{game.Kills} / {game.Deaths} / {game.Assists}";
        KdaRatioText = $"{game.KdaRatio:F2} KDA";
        GameModeText = game.DisplayGameMode;
        DurationText = game.GameDuration > 0 ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}" : "";
        HeaderText = $"Review -- {game.ChampionName} ({(game.Win ? "W" : "L")})";

        DamageText = FormatNumber(game.TotalDamageToChampions);
        CsText = game.CsTotal.ToString();
        CsPerMinText = $"{game.CsPerMin:F1}/m";
        VisionText = game.VisionScore.ToString();
        GoldText = FormatNumber(game.GoldEarned);
        KillParticipationText = game.KillParticipation > 0 ? $"{game.KillParticipation:F0}%" : "—";
        DamageTakenText = FormatNumber(game.TotalDamageTaken);
        WardsPlacedText = game.WardsPlaced.ToString();
    }

    private void ApplySnapshot(ReviewSnapshot snapshot)
    {
        MentalRating = snapshot.MentalRating;
        WentWell = snapshot.WentWell;
        Mistakes = snapshot.Mistakes;
        FocusNext = snapshot.FocusNext;
        ReviewNotes = snapshot.ReviewNotes;
        ImprovementNote = snapshot.ImprovementNote;
        Attribution = snapshot.Attribution;
        MentalHandled = snapshot.MentalHandled;
        SpottedProblems = snapshot.SpottedProblems;
        OutsideControl = snapshot.OutsideControl;
        WithinControl = snapshot.WithinControl;
        PersonalContribution = snapshot.PersonalContribution;
        EnemyLaner = snapshot.EnemyLaner;
        MatchupNote = snapshot.MatchupNote;
        UpdateMentalColor();
        ShowMentalReflection = MentalRating <= 3;
    }

    private void ApplyTags(IReadOnlyList<ReviewTagState> tags)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            AllTags.Clear();
            SelectedTagIds.Clear();

            foreach (var tag in tags)
            {
                AllTags.Add(new ConceptTagItem
                {
                    Id = tag.Id,
                    Name = tag.Name,
                    Polarity = tag.Polarity,
                    ColorHex = tag.ColorHex,
                    IsSelected = tag.IsSelected
                });

                if (tag.IsSelected)
                {
                    SelectedTagIds.Add(tag.Id);
                }
            }
        });
    }

    private void ApplyObjectives(
        IReadOnlyList<ReviewObjectiveState> objectives,
        Revu.Core.Data.Repositories.ObjectiveSummary? priorityObjective)
    {
        DispatcherHelper.RunOnUIThread(() =>
        {
            ObjectiveAssessments.Clear();
            foreach (var objective in objectives)
            {
                ObjectiveAssessments.Add(new ObjectiveAssessment
                {
                    ObjectiveId = objective.ObjectiveId,
                    Title = objective.Title,
                    Criteria = objective.Criteria,
                    Phase = objective.Phase,
                    IsPriority = objective.IsPriority,
                    Practiced = objective.Practiced,
                    ExecutionNote = objective.ExecutionNote,
                });
            }

            HasObjectives = ObjectiveAssessments.Count > 0;
            PriorityObjectiveTitle = priorityObjective?.Title ?? "";
            PriorityObjectiveCriteria = priorityObjective?.CompletionCriteria ?? "";
            HasPriorityObjective = !string.IsNullOrWhiteSpace(PriorityObjectiveTitle);
        });
    }

    private void ApplyMatchupHistory(IReadOnlyList<ReviewMatchupHistoryItem> history)
    {
        var items = history
            .Select(item => new MatchupHistoryItem
            {
                Note = item.Note,
                Helpful = item.Helpful,
                MetaText = BuildMatchupMetaText(item)
            })
            .Where(static item => !string.IsNullOrWhiteSpace(item.Note))
            .ToList();

        DispatcherHelper.RunOnUIThread(() =>
        {
            MatchupHistory.Clear();
            foreach (var item in items)
            {
                MatchupHistory.Add(item);
            }

            HasMatchupHistory = MatchupHistory.Count > 0;
        });
    }

    private void UpdateMentalColor()
    {
        MentalRatingColorHex = AppSemanticPalette.MentalRatingHex(MentalRating);
    }

    private void UpdateSaveBehaviorText()
    {
        SaveBehaviorText = RequireReviewNotes
            ? "Review notes are required before save."
            : "Review notes are optional. A blank save still marks the game reviewed.";
    }

    private void SetValidation(string message)
    {
        ValidationMessage = message;
        HasValidationMessage = !string.IsNullOrWhiteSpace(message);
    }

    private void ClearValidation()
    {
        ValidationMessage = "";
        HasValidationMessage = false;
    }

    private static string BuildMatchupMetaText(ReviewMatchupHistoryItem item)
    {
        var createdText = item.CreatedAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(item.CreatedAt.Value).LocalDateTime.ToString("MMM d, yyyy HH:mm")
            : "Unknown date";
        var helpfulText = item.Helpful switch
        {
            true => "Helpful",
            false => "Not helpful",
            null => "Unrated",
        };

        return item.GameId > 0
            ? $"Game {item.GameId}  •  {createdText}  •  {helpfulText}"
            : $"{createdText}  •  {helpfulText}";
    }

    private void ScheduleVodRecheck(long gameId)
    {
        _vodRetryCts?.Cancel();
        _vodRetryCts = new CancellationTokenSource();
        var token = _vodRetryCts.Token;

        _ = Task.Run(async () =>
        {
            // Retry a few times with increasing delays to catch recordings that
            // are still being finalized when the post-game page first loads.
            int[] delaysMs = [15_000, 30_000, 60_000];
            foreach (var delay in delaysMs)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);
                    var result = await _reviewWorkflowService.CheckVodAsync(gameId, token).ConfigureAwait(false);
                    if (result.HasVod)
                    {
                        Helpers.DispatcherHelper.RunOnUIThread(() =>
                        {
                            HasVod = true;
                            BookmarkCount = result.BookmarkCount;
                        });
                        return;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "VOD recheck failed for game {GameId}", gameId);
                }
            }
        }, token);
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };
}

/// <summary>Concept tag item with selection state for the tag selector.</summary>
public class ConceptTagItem : ObservableObject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Polarity { get; set; } = "neutral";

    private string _colorHex = AppSemanticPalette.AccentBlueHex;
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = value;
            OnPropertyChanged(nameof(TagAccentBrush));
            OnPropertyChanged(nameof(TagBackgroundBrush));
            OnPropertyChanged(nameof(TagBorderBrush));
        }
    }

    public SolidColorBrush TagAccentBrush => AppSemanticPalette.TagAccentBrush(Polarity, ColorHex);
    public SolidColorBrush TagBackgroundBrush => IsSelected
        ? AppSemanticPalette.TagSurfaceBrush(Polarity, ColorHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.TagSurfaceHex);
    public SolidColorBrush TagBorderBrush => IsSelected
        ? AppSemanticPalette.TagAccentBrush(Polarity, ColorHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.SubtleBorderHex);

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(TagBackgroundBrush));
                OnPropertyChanged(nameof(TagBorderBrush));
            }
        }
    }
}

/// <summary>Recent note for the same champion/enemy matchup.</summary>
public sealed class MatchupHistoryItem
{
    public string Note { get; init; } = "";
    public bool? Helpful { get; init; }
    public string MetaText { get; init; } = "";
}
