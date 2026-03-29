#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the inline game review page.</summary>
public partial class ReviewViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IConceptTagRepository _conceptTagRepo;
    private readonly IVodRepository _vodRepo;
    private readonly IVodService _vodService;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly IMatchupNotesRepository _matchupNotesRepo;
    private readonly IConfigService _configService;
    private readonly INavigationService _navigationService;
    private readonly ILogger<ReviewViewModel> _logger;

    private string _loadedEnemyLaner = "";

    // ── Game Data ──────────────────────────────────────────────────────

    [ObservableProperty]
    private long _gameId;

    [ObservableProperty]
    private string _championName = "";

    [ObservableProperty]
    private bool _win;

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private string _resultColorHex = "#e8e8f0";

    [ObservableProperty]
    private string _kdaText = "";

    [ObservableProperty]
    private string _kdaRatioText = "";

    [ObservableProperty]
    private string _gameModeText = "";

    [ObservableProperty]
    private string _durationText = "";

    [ObservableProperty]
    private string _headerText = "Review";

    [ObservableProperty]
    private string _enemyLaner = "";

    [ObservableProperty]
    private bool _hasEnemyLaner;

    // ── Stat display properties ───────────────────────────────────────

    [ObservableProperty]
    private string _damageText = "";

    [ObservableProperty]
    private string _csText = "";

    [ObservableProperty]
    private string _csPerMinText = "";

    [ObservableProperty]
    private string _visionText = "";

    [ObservableProperty]
    private string _goldText = "";

    [ObservableProperty]
    private string _killParticipationText = "";

    [ObservableProperty]
    private string _damageTakenText = "";

    [ObservableProperty]
    private string _wardsPlacedText = "";

    // ── VOD ────────────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _hasVod;

    [ObservableProperty]
    private int _bookmarkCount;

    // ── Review Fields ──────────────────────────────────────────────────

    [ObservableProperty]
    private int _mentalRating = 5;

    [ObservableProperty]
    private string _mentalRatingColorHex = "#0099ff";

    [ObservableProperty]
    private string _wentWell = "";

    [ObservableProperty]
    private string _mistakes = "";

    [ObservableProperty]
    private string _focusNext = "";

    [ObservableProperty]
    private string _reviewNotes = "";

    [ObservableProperty]
    private string _improvementNote = "";

    [ObservableProperty]
    private string _attribution = "";

    [ObservableProperty]
    private string _mentalHandled = "";

    [ObservableProperty]
    private string _spottedProblems = "";

    [ObservableProperty]
    private string _outsideControl = "";

    [ObservableProperty]
    private string _withinControl = "";

    [ObservableProperty]
    private string _personalContribution = "";

    [ObservableProperty]
    private string _matchupNote = "";

    [ObservableProperty]
    private bool _requireReviewNotes;

    [ObservableProperty]
    private string _saveBehaviorText = "";

    [ObservableProperty]
    private string _validationMessage = "";

    [ObservableProperty]
    private bool _hasValidationMessage;

    [ObservableProperty]
    private bool _showMentalReflection;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasObjectives;

    [ObservableProperty]
    private bool _hasMatchupHistory;

    [ObservableProperty]
    private string _priorityObjectiveTitle = "";

    [ObservableProperty]
    private string _priorityObjectiveCriteria = "";

    [ObservableProperty]
    private bool _hasPriorityObjective;

    // ── Concept Tags ───────────────────────────────────────────────────

    public ObservableCollection<ConceptTagItem> AllTags { get; } = new();
    public ObservableCollection<long> SelectedTagIds { get; } = new();
    public ObservableCollection<ObjectiveAssessment> ObjectiveAssessments { get; } = new();
    public ObservableCollection<MatchupHistoryItem> MatchupHistory { get; } = new();

    // ── Attribution Options ────────────────────────────────────────────

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

    // ── Constructor ────────────────────────────────────────────────────

    public ReviewViewModel(
        IGameRepository gameRepo,
        IConceptTagRepository conceptTagRepo,
        IVodRepository vodRepo,
        IVodService vodService,
        ISessionLogRepository sessionLogRepo,
        IObjectivesRepository objectivesRepo,
        IMatchupNotesRepository matchupNotesRepo,
        IConfigService configService,
        INavigationService navigationService,
        ILogger<ReviewViewModel> logger)
    {
        _gameRepo = gameRepo;
        _conceptTagRepo = conceptTagRepo;
        _vodRepo = vodRepo;
        _vodService = vodService;
        _sessionLogRepo = sessionLogRepo;
        _objectivesRepo = objectivesRepo;
        _matchupNotesRepo = matchupNotesRepo;
        _configService = configService;
        _navigationService = navigationService;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadAsync(long gameId)
    {
        if (IsLoading) return;
        IsLoading = true;
        ClearValidation();

        try
        {
            GameId = gameId;

            var config = await _configService.LoadAsync();
            RequireReviewNotes = config.RequireReviewNotes;
            UpdateSaveBehaviorText();

            var game = await _gameRepo.GetAsync(gameId);
            if (game == null)
            {
                _logger.LogWarning("Game {GameId} not found", gameId);
                return;
            }

            PopulateGameData(game);

            LoadExistingReview(game);

            await LoadVodStateAsync(game);
            await LoadSessionStateAsync(gameId);
            await LoadConceptTagsAsync(gameId);
            await LoadObjectiveAssessmentsAsync(gameId);
            await LoadMatchupSectionAsync(gameId);
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
    private void WatchVod()
    {
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

    // ── Property change handlers ───────────────────────────────────────

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
            _ = LoadMatchupHistoryOnlyAsync(ChampionName, value.Trim(), GameId);
        }
    }

    // ── Save pipeline ───────────────────────────────────────────────────

    private async Task<bool> SaveCoreAsync(bool navigateBackOnSuccess)
    {
        if (!ValidateBeforeSave())
        {
            return false;
        }

        try
        {
            var trimmedEnemy = EnemyLaner.Trim();
            var trimmedMatchupNote = MatchupNote.Trim();

            var review = new GameReview
            {
                Rating = 1,
                Notes = ReviewNotes.Trim(),
                Mistakes = Mistakes.Trim(),
                WentWell = WentWell.Trim(),
                FocusNext = FocusNext.Trim(),
                SpottedProblems = SpottedProblems.Trim(),
                OutsideControl = OutsideControl.Trim(),
                WithinControl = WithinControl.Trim(),
                Attribution = Attribution.Trim(),
                PersonalContribution = PersonalContribution.Trim(),
            };

            await _gameRepo.UpdateReviewAsync(GameId, review);

            await _sessionLogRepo.LogGameAsync(
                GameId,
                ChampionName,
                Win,
                MentalRating,
                ImprovementNote.Trim());

            await _sessionLogRepo.UpdateMentalHandledAsync(GameId, MentalHandled.Trim());

            if (!string.Equals(_loadedEnemyLaner, trimmedEnemy, StringComparison.Ordinal))
            {
                await _gameRepo.UpdateEnemyLanerAsync(GameId, trimmedEnemy);
                _loadedEnemyLaner = trimmedEnemy;
            }

            if (!string.IsNullOrWhiteSpace(trimmedEnemy) || !string.IsNullOrWhiteSpace(trimmedMatchupNote))
            {
                await _matchupNotesRepo.UpsertForGameAsync(GameId, ChampionName, trimmedEnemy, trimmedMatchupNote);
            }
            else
            {
                await _matchupNotesRepo.DeleteForGameAsync(GameId);
            }

            foreach (var objective in ObjectiveAssessments)
            {
                await _objectivesRepo.RecordGameAsync(
                    GameId,
                    objective.ObjectiveId,
                    objective.Practiced,
                    objective.ExecutionNote.Trim());
            }

            var selectedIds = AllTags
                .Where(t => t.IsSelected)
                .Select(t => t.Id)
                .ToList();
            await _conceptTagRepo.SetForGameAsync(GameId, selectedIds);

            ClearValidation();
            _logger.LogInformation("Review saved for game {GameId}", GameId);

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

    private bool ValidateBeforeSave()
    {
        if (!string.IsNullOrWhiteSpace(MatchupNote) && string.IsNullOrWhiteSpace(EnemyLaner))
        {
            SetValidation("Add the enemy champion before saving a matchup note.");
            return false;
        }

        if (RequireReviewNotes && !HasMeaningfulReviewContent())
        {
            SetValidation("Review notes are required in Settings. Add review content before saving.");
            return false;
        }

        ClearValidation();
        return true;
    }

    private bool HasMeaningfulReviewContent()
    {
        return !string.IsNullOrWhiteSpace(WentWell)
               || !string.IsNullOrWhiteSpace(Mistakes)
               || !string.IsNullOrWhiteSpace(FocusNext)
               || !string.IsNullOrWhiteSpace(ReviewNotes)
               || !string.IsNullOrWhiteSpace(ImprovementNote)
               || !string.IsNullOrWhiteSpace(MentalHandled)
               || !string.IsNullOrWhiteSpace(SpottedProblems)
               || !string.IsNullOrWhiteSpace(OutsideControl)
               || !string.IsNullOrWhiteSpace(WithinControl)
               || !string.IsNullOrWhiteSpace(PersonalContribution)
               || !string.IsNullOrWhiteSpace(Attribution)
               || !string.IsNullOrWhiteSpace(MatchupNote)
               || AllTags.Any(t => t.IsSelected)
               || ObjectiveAssessments.Any(o => o.Practiced || !string.IsNullOrWhiteSpace(o.ExecutionNote));
    }

    // ── Load helpers ────────────────────────────────────────────────────

    private void PopulateGameData(GameStats game)
    {
        ChampionName = game.ChampionName;
        Win = game.Win;
        ResultText = game.Win ? "VICTORY" : "DEFEAT";
        ResultColorHex = game.Win ? "#22c55e" : "#ef4444";
        KdaText = $"{game.Kills} / {game.Deaths} / {game.Assists}";
        KdaRatioText = $"{game.KdaRatio:F2} KDA";
        GameModeText = game.GameMode;
        DurationText = game.GameDuration > 0
            ? $"{game.GameDuration / 60}:{game.GameDuration % 60:D2}"
            : "";
        HeaderText = $"Review -- {game.ChampionName} ({(game.Win ? "W" : "L")})";
        EnemyLaner = game.EnemyLaner;
        _loadedEnemyLaner = game.EnemyLaner;

        DamageText = FormatNumber(game.TotalDamageToChampions);
        CsText = game.CsTotal.ToString();
        CsPerMinText = $"{game.CsPerMin:F1}/m";
        VisionText = game.VisionScore.ToString();
        GoldText = FormatNumber(game.GoldEarned);
        KillParticipationText = $"{game.KillParticipation:F0}%";
        DamageTakenText = FormatNumber(game.TotalDamageTaken);
        WardsPlacedText = game.WardsPlaced.ToString();
    }

    private async Task LoadVodStateAsync(GameStats game)
    {
        var vod = await _vodRepo.GetVodAsync(GameId);
        if (vod == null && _configService.IsAscentEnabled)
        {
            try
            {
                await _vodService.TryLinkRecordingAsync(game);
                vod = await _vodRepo.GetVodAsync(GameId);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "VOD lookup retry failed for game {GameId}", GameId);
            }
        }

        HasVod = vod != null;
        BookmarkCount = HasVod
            ? await _vodRepo.GetBookmarkCountAsync(GameId)
            : 0;
    }

    private async Task LoadSessionStateAsync(long gameId)
    {
        var sessionEntry = await _sessionLogRepo.GetEntryAsync(gameId);
        if (sessionEntry != null)
        {
            MentalRating = sessionEntry.MentalRating;
            ImprovementNote = sessionEntry.ImprovementNote;
            MentalHandled = sessionEntry.MentalHandled;
        }

        UpdateMentalColor();
        ShowMentalReflection = MentalRating <= 3;
    }

    private async Task LoadConceptTagsAsync(long gameId)
    {
        var allTags = await _conceptTagRepo.GetAllAsync();
        var existingTagIds = await _conceptTagRepo.GetIdsForGameAsync(gameId);

        DispatcherHelper.RunOnUIThread(() =>
        {
            AllTags.Clear();
            SelectedTagIds.Clear();

            foreach (var tag in allTags)
            {
                var id = tag.TryGetValue("id", out var idVal) ? Convert.ToInt64(idVal ?? 0) : 0;
                var name = tag.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                var color = tag.TryGetValue("color", out var c) ? c?.ToString() ?? "#3b82f6" : "#3b82f6";
                var isSelected = existingTagIds.Contains(id);

                AllTags.Add(new ConceptTagItem
                {
                    Id = id,
                    Name = name,
                    ColorHex = color,
                    IsSelected = isSelected
                });

                if (isSelected)
                {
                    SelectedTagIds.Add(id);
                }
            }
        });
    }

    private async Task LoadObjectiveAssessmentsAsync(long gameId)
    {
        var activeObjectives = await _objectivesRepo.GetActiveAsync();
        var priorityObjective = await _objectivesRepo.GetPriorityAsync();
        var priorityObjectiveId = priorityObjective is null
            ? 0L
            : Convert.ToInt64(priorityObjective.GetValueOrDefault("id") ?? 0L);
        var savedObjectives = await _objectivesRepo.GetGameObjectivesAsync(gameId);
        var savedById = savedObjectives.ToDictionary(
            row => Convert.ToInt64(row.GetValueOrDefault("objective_id") ?? 0),
            row => row);

        var assessments = new List<ObjectiveAssessment>();

        foreach (var objective in activeObjectives)
        {
            var objectiveId = Convert.ToInt64(objective.GetValueOrDefault("id", 0L));
            savedById.TryGetValue(objectiveId, out var saved);

            assessments.Add(CreateObjectiveAssessment(objective, saved, objectiveId == priorityObjectiveId));
            savedById.Remove(objectiveId);
        }

        foreach (var saved in savedById.Values)
        {
            var objectiveId = Convert.ToInt64(saved.GetValueOrDefault("objective_id") ?? 0L);
            assessments.Add(CreateObjectiveAssessment(saved, saved, objectiveId == priorityObjectiveId));
        }

        DispatcherHelper.RunOnUIThread(() =>
        {
            ObjectiveAssessments.Clear();
            foreach (var assessment in assessments)
            {
                ObjectiveAssessments.Add(assessment);
            }

            HasObjectives = ObjectiveAssessments.Count > 0;
            PriorityObjectiveTitle = priorityObjective?.GetValueOrDefault("title")?.ToString() ?? "";
            PriorityObjectiveCriteria = priorityObjective?.GetValueOrDefault("completion_criteria")?.ToString() ?? "";
            HasPriorityObjective = !string.IsNullOrWhiteSpace(PriorityObjectiveTitle);
        });
    }

    private async Task LoadMatchupSectionAsync(long gameId)
    {
        MatchupNote = "";

        var savedForGame = await _matchupNotesRepo.GetForGameAsync(gameId);
        if (savedForGame != null)
        {
            MatchupNote = savedForGame.GetValueOrDefault("note")?.ToString() ?? "";

            if (string.IsNullOrWhiteSpace(EnemyLaner))
            {
                EnemyLaner = savedForGame.GetValueOrDefault("enemy")?.ToString() ?? "";
                _loadedEnemyLaner = EnemyLaner;
            }
        }

        await LoadMatchupHistoryOnlyAsync(ChampionName, EnemyLaner.Trim(), gameId);
    }

    private async Task LoadMatchupHistoryOnlyAsync(string championName, string enemyLaner, long currentGameId)
    {
        if (string.IsNullOrWhiteSpace(championName) || string.IsNullOrWhiteSpace(enemyLaner))
        {
            DispatcherHelper.RunOnUIThread(() =>
            {
                MatchupHistory.Clear();
                HasMatchupHistory = false;
            });
            return;
        }

        var notes = await _matchupNotesRepo.GetForMatchupAsync(championName, enemyLaner);
        var items = notes
            .Where(n => Convert.ToInt64(n.GetValueOrDefault("game_id") ?? 0) != currentGameId)
            .Select(n => new MatchupHistoryItem
            {
                Note = n.GetValueOrDefault("note")?.ToString() ?? "",
                Helpful = ParseHelpful(n.GetValueOrDefault("helpful")),
                MetaText = BuildMatchupMetaText(n)
            })
            .Where(n => !string.IsNullOrWhiteSpace(n.Note))
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

    private void LoadExistingReview(GameStats game)
    {
        Mistakes = game.Mistakes;
        WentWell = game.WentWell;
        FocusNext = game.FocusNext;
        ReviewNotes = game.ReviewNotes;
        SpottedProblems = game.SpottedProblems;
        OutsideControl = game.OutsideControl;
        WithinControl = game.WithinControl;
        Attribution = game.Attribution;
        PersonalContribution = game.PersonalContribution;
    }

    private static ObjectiveAssessment CreateObjectiveAssessment(
        Dictionary<string, object?> objectiveRow,
        Dictionary<string, object?>? savedRow,
        bool isPriority)
    {
        return new ObjectiveAssessment
        {
            ObjectiveId = Convert.ToInt64(
                objectiveRow.GetValueOrDefault("id")
                ?? objectiveRow.GetValueOrDefault("objective_id")
                ?? 0L),
            Title = objectiveRow.GetValueOrDefault("title")?.ToString() ?? "",
            Criteria = objectiveRow.GetValueOrDefault("completion_criteria")?.ToString() ?? "",
            IsPriority = isPriority,
            Practiced = Convert.ToInt32(savedRow?.GetValueOrDefault("practiced") ?? 0) != 0,
            ExecutionNote = savedRow?.GetValueOrDefault("execution_note")?.ToString() ?? "",
        };
    }

    // ── Presentation helpers ───────────────────────────────────────────

    private void UpdateMentalColor()
    {
        MentalRatingColorHex = MentalRating switch
        {
            >= 8 => "#22c55e",
            >= 5 => "#0099ff",
            >= 4 => "#c89b3c",
            _ => "#ef4444"
        };
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

    private static string BuildMatchupMetaText(IReadOnlyDictionary<string, object?> row)
    {
        var gameId = Convert.ToInt64(row.GetValueOrDefault("game_id") ?? 0);
        var helpful = ParseHelpful(row.GetValueOrDefault("helpful"));
        var createdAt = Convert.ToInt64(row.GetValueOrDefault("created_at") ?? 0);
        var createdText = createdAt > 0
            ? DateTimeOffset.FromUnixTimeSeconds(createdAt).LocalDateTime.ToString("MMM d, yyyy HH:mm")
            : "Unknown date";
        var helpfulText = helpful switch
        {
            true => "Helpful",
            false => "Not helpful",
            null => "Unrated",
        };

        return gameId > 0
            ? $"Game {gameId}  •  {createdText}  •  {helpfulText}"
            : $"{createdText}  •  {helpfulText}";
    }

    private static bool? ParseHelpful(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return Convert.ToInt32(value) switch
        {
            1 => true,
            0 => false,
            _ => null,
        };
    }

    private static string FormatNumber(int n) => n switch
    {
        >= 1_000_000 => $"{n / 1_000_000.0:F1}M",
        >= 1_000 => $"{n / 1_000.0:F1}k",
        _ => n.ToString()
    };
}

// ── Display models ──────────────────────────────────────────────────────

/// <summary>Concept tag item with selection state for the tag selector.</summary>
public class ConceptTagItem : ObservableObject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";

    private string _colorHex = "#3b82f6";
    public string ColorHex
    {
        get => _colorHex;
        set
        {
            _colorHex = value;
            _colorBrush = null;
        }
    }

    private SolidColorBrush? _colorBrush;
    public SolidColorBrush ColorBrush
    {
        get
        {
            if (_colorBrush == null)
            {
                try
                {
                    var hex = ColorHex.TrimStart('#');
                    if (hex.Length == 6)
                    {
                        var r = Convert.ToByte(hex[..2], 16);
                        var g = Convert.ToByte(hex[2..4], 16);
                        var b = Convert.ToByte(hex[4..6], 16);
                        _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, r, g, b));
                    }
                    else
                    {
                        _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246));
                    }
                }
                catch
                {
                    _colorBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246));
                }
            }

            return _colorBrush;
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>Recent note for the same champion/enemy matchup.</summary>
public sealed class MatchupHistoryItem
{
    public string Note { get; init; } = "";
    public bool? Helpful { get; init; }
    public string MetaText { get; init; } = "";
}
