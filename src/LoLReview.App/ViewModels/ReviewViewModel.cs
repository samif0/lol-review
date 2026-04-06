#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Styling;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the inline game review page.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IReviewWorkflowService _reviewWorkflowService;
    private readonly INavigationService _navigationService;
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
    [ObservableProperty] private bool _hasObjectives;
    [ObservableProperty] private bool _hasMatchupHistory;
    [ObservableProperty] private string _priorityObjectiveTitle = "";
    [ObservableProperty] private string _priorityObjectiveCriteria = "";
    [ObservableProperty] private bool _hasPriorityObjective;

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
        ILogger<ReviewViewModel> logger)
    {
        _reviewWorkflowService = reviewWorkflowService;
        _navigationService = navigationService;
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

            RequireReviewNotes = screenData.RequireReviewNotes;
            HasVod = screenData.HasVod;
            BookmarkCount = screenData.BookmarkCount;
            UpdateSaveBehaviorText();
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

    [RelayCommand]
    private async Task SaveReviewAsync()
    {
        await SaveCoreAsync(navigateBackOnSuccess: true);
    }

    [RelayCommand]
    private void Cancel()
    {
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
        GameModeText = game.GameMode;
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
        LoLReview.Core.Data.Repositories.ObjectiveSummary? priorityObjective)
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
