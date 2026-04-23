#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.App.Contracts;
using Revu.App.Styling;
using Revu.Core.Data.Repositories;
using Microsoft.UI.Xaml.Media;

namespace Revu.App.ViewModels;

public sealed record ObjectivePhaseUpdateRequest(long ObjectiveId, string Phase);

/// <summary>Display model for an objective card.</summary>
public sealed class ObjectiveDisplayItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string SkillArea { get; init; } = "";
    public string Type { get; init; } = "primary";
    public string CompletionCriteria { get; init; } = "";
    public string Description { get; init; } = "";
    public string Phase { get; init; } = ObjectivePhases.InGame;
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string Status { get; init; } = "active";
    public bool IsPriority { get; init; }

    // Level info
    public string LevelName { get; init; } = "Exploring";
    public int LevelIndex { get; init; }
    public double Progress { get; init; }
    public int? NextThreshold { get; init; }
    public bool CanComplete { get; init; }
    public bool SuggestComplete { get; init; }

    /// <summary>Color brush for the current level, distinct per progression stage.</summary>
    public SolidColorBrush LevelColorBrush => AppSemanticPalette.ObjectiveLevelBrush(LevelIndex);
    public SolidColorBrush TypeBadgeBackgroundBrush => AppSemanticPalette.TagSurfaceBrush(IsMental ? "neutral" : null, IsMental ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.AccentBlueHex);
    public SolidColorBrush TypeBadgeForegroundBrush => AppSemanticPalette.TagAccentBrush(IsMental ? "neutral" : null, IsMental ? AppSemanticPalette.AccentTealHex : AppSemanticPalette.AccentBlueHex);
    public SolidColorBrush PriorityBadgeBackgroundBrush => AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldDimHex);
    public SolidColorBrush PriorityBadgeForegroundBrush => AppSemanticPalette.Brush(AppSemanticPalette.AccentGoldHex);

    // Derived display properties
    public bool IsMental => Type == "mental";
    public string TypeBadge => IsMental ? "MENTAL" : "PRIMARY";
    public string PriorityBadge => IsPriority ? "PRIORITY" : "";
    public string ScoreText => $"{Score} pts  \u2022  {GameCount} games";
    public string ProgressText
    {
        get
        {
            if (NextThreshold.HasValue)
            {
                var needed = NextThreshold.Value - Score;
                return $"{needed} pts to next level";
            }
            return "Max level reached";
        }
    }
    public double ProgressPercent => Progress * 100;
    public bool HasSkillArea => !string.IsNullOrWhiteSpace(SkillArea);
    public bool HasCriteria => !string.IsNullOrWhiteSpace(CompletionCriteria);
    public int PhaseIndex => ObjectivePhases.ToIndex(Phase);
    public string PhaseLabel => ObjectivePhases.ToDisplayLabel(Phase);

    /// <summary>Cumulative score per game, oldest→newest, for sparkline rendering.</summary>
    public IReadOnlyList<int> ScoreHistory { get; init; } = [];
    public bool HasScoreHistory => ScoreHistory.Count >= 2;
    public string CriteriaText => $"Success: {CompletionCriteria}";
    public string RemainingText
    {
        get
        {
            if (CanComplete) return "";
            var needed = Math.Max(0, 50 - Score);
            return $"{needed} more pts to unlock completion (reach Ready level)";
        }
    }
}

/// <summary>Display model for a completed objective.</summary>
public sealed class CompletedObjectiveItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string PhaseLabel { get; init; } = "";
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string SummaryText => $"{Score} pts  \u2022  {GameCount} games";
}

/// <summary>Recent spotted-problem note shown as backlog context for future objectives.</summary>
public sealed class SpottedProblemItem
{
    public long GameId { get; init; }
    public string ChampionName { get; init; } = "";
    public string DatePlayed { get; init; } = "";
    public string ProblemText { get; init; } = "";
    public bool Win { get; init; }
    public string ResultText => Win ? "W" : "L";
}

/// <summary>ViewModel for the Objectives page.</summary>
public partial class ObjectivesViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private long? _editingObjectiveId;

    [ObservableProperty]
    private bool _hasObjectives;

    [ObservableProperty]
    private bool _hasActiveObjectives;

    [ObservableProperty]
    private bool _hasCompletedObjectives;

    [ObservableProperty]
    private bool _showCompleted;

    [ObservableProperty]
    private bool _hasSpottedProblems;

    // Celebration overlay state
    [ObservableProperty]
    private bool _showCelebration;

    [ObservableProperty]
    private string _celebrationTitle = "";

    [ObservableProperty]
    private string _celebrationStats = "";

    // Create form fields
    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newSkillArea = "";

    [ObservableProperty]
    private int _newTypeIndex; // 0 = primary, 1 = mental

    [ObservableProperty]
    private int _newPhaseIndex = 1; // 0 = pre-game, 1 = in-game, 2 = post-game

    [ObservableProperty]
    private string _newCriteria = "";

    [ObservableProperty]
    private string _newDescription = "";

    [ObservableProperty]
    private bool _canCreate;

    public bool IsEditingObjective => EditingObjectiveId.HasValue;
    public string ObjectiveFormTitle => IsEditingObjective ? "Edit Objective" : "New Objective";
    public string SaveObjectiveButtonText => IsEditingObjective ? "Save Changes" : "Create";

    public ObservableCollection<ObjectiveDisplayItem> ActiveObjectives { get; } = new();
    public ObservableCollection<CompletedObjectiveItem> CompletedObjectives { get; } = new();
    public ObservableCollection<SpottedProblemItem> SpottedProblems { get; } = new();

    public ObjectivesViewModel(
        IGameRepository gameRepo,
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService)
    {
        _gameRepo = gameRepo;
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
    }

    partial void OnNewTitleChanged(string value)
    {
        CanCreate = !string.IsNullOrWhiteSpace(value);
    }

    partial void OnEditingObjectiveIdChanged(long? value)
    {
        OnPropertyChanged(nameof(IsEditingObjective));
        OnPropertyChanged(nameof(ObjectiveFormTitle));
        OnPropertyChanged(nameof(SaveObjectiveButtonText));
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await RefreshDataAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void ToggleCreateForm()
    {
        IsCreating = !IsCreating;
        if (!IsCreating)
        {
            ClearForm();
            return;
        }

        EditingObjectiveId = null;
        ResetFormFields();
    }

    [RelayCommand]
    private async Task CreateObjectiveAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) return;

        var type = NewTypeIndex == 1 ? "mental" : "primary";
        if (EditingObjectiveId.HasValue)
        {
            await _objectivesRepo.UpdateAsync(
                EditingObjectiveId.Value,
                NewTitle.Trim(),
                NewSkillArea.Trim(),
                type,
                NewCriteria.Trim(),
                NewDescription.Trim(),
                phase: ObjectivePhases.FromIndex(NewPhaseIndex));
        }
        else
        {
            await _objectivesRepo.CreateAsync(
                NewTitle.Trim(),
                NewSkillArea.Trim(),
                type,
                NewCriteria.Trim(),
                NewDescription.Trim(),
                phase: ObjectivePhases.FromIndex(NewPhaseIndex));
        }

        ClearForm();
        IsCreating = false;
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task MarkCompleteAsync(long objectiveId)
    {
        var objective = ActiveObjectives.FirstOrDefault(o => o.Id == objectiveId);

        await _objectivesRepo.MarkCompleteAsync(objectiveId);
        await RefreshDataAsync();

        if (objective is not null)
        {
            CelebrationTitle = objective.Title;
            CelebrationStats = $"{objective.Score} pts  \u2022  {objective.GameCount} games played";
            ShowCelebration = true;
            _ = AutoDismissCelebrationAsync();
        }
    }

    [RelayCommand]
    private void DismissCelebration()
    {
        ShowCelebration = false;
    }

    private async Task AutoDismissCelebrationAsync()
    {
        await Task.Delay(5000);
        ShowCelebration = false;
    }

    [RelayCommand]
    private async Task SetPriorityAsync(long objectiveId)
    {
        await _objectivesRepo.SetPriorityAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task DeleteObjectiveAsync(long objectiveId)
    {
        await _objectivesRepo.DeleteAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task UpdateObjectivePhaseAsync(ObjectivePhaseUpdateRequest request)
    {
        await _objectivesRepo.UpdatePhaseAsync(request.ObjectiveId, request.Phase);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task BeginEditObjectiveAsync(long objectiveId)
    {
        var objective = await _objectivesRepo.GetAsync(objectiveId);
        if (objective is null)
        {
            return;
        }

        EditingObjectiveId = objective.Id;
        NewTitle = objective.Title;
        NewSkillArea = objective.SkillArea;
        NewTypeIndex = string.Equals(objective.Type, "mental", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        NewPhaseIndex = ObjectivePhases.ToIndex(objective.Phase);
        NewCriteria = objective.CompletionCriteria;
        NewDescription = objective.Description;
        CanCreate = !string.IsNullOrWhiteSpace(NewTitle);
        IsCreating = true;
    }

    [RelayCommand]
    private void ToggleCompleted()
    {
        ShowCompleted = !ShowCompleted;
    }

    [RelayCommand]
    private void ViewGames(long objectiveId)
    {
        _navigationService.NavigateTo("objectivegames", objectiveId);
    }

    [RelayCommand]
    private void ViewNotes(long objectiveId)
    {
        _navigationService.NavigateTo("objectivenotes", objectiveId);
    }

    private void ClearForm()
    {
        EditingObjectiveId = null;
        ResetFormFields();
    }

    private void ResetFormFields()
    {
        NewTitle = "";
        NewSkillArea = "";
        NewTypeIndex = 0;
        NewPhaseIndex = 1;
        NewCriteria = "";
        NewDescription = "";
    }

    private async Task RefreshDataAsync()
    {
        var allObjectives = await _objectivesRepo.GetAllAsync();
        var spottedProblems = await _gameRepo.GetRecentSpottedProblemsAsync(limit: 12);

        ActiveObjectives.Clear();
        CompletedObjectives.Clear();
        SpottedProblems.Clear();

        foreach (var obj in allObjectives)
        {
            if (string.Equals(obj.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                var levelInfo = IObjectivesRepository.GetLevelInfo(obj.Score, obj.GameCount);
                var scoreHistory = await _objectivesRepo.GetScoreHistoryAsync(obj.Id);

                ActiveObjectives.Add(new ObjectiveDisplayItem
                {
                    Id = obj.Id,
                    Title = obj.Title,
                    SkillArea = obj.SkillArea,
                    Type = obj.Type,
                    CompletionCriteria = obj.CompletionCriteria,
                    Description = obj.Description,
                    Phase = obj.Phase,
                    Score = obj.Score,
                    GameCount = obj.GameCount,
                    Status = obj.Status,
                    IsPriority = obj.IsPriority,
                    LevelName = levelInfo.LevelName,
                    LevelIndex = levelInfo.LevelIndex,
                    Progress = levelInfo.Progress,
                    NextThreshold = levelInfo.NextThreshold,
                    CanComplete = levelInfo.CanComplete,
                    SuggestComplete = levelInfo.SuggestComplete,
                    ScoreHistory = scoreHistory,
                });
            }
            else
            {
                CompletedObjectives.Add(new CompletedObjectiveItem
                {
                    Id = obj.Id,
                    Title = obj.Title,
                    PhaseLabel = ObjectivePhases.ToDisplayLabel(obj.Phase),
                    Score = obj.Score,
                    GameCount = obj.GameCount,
                });
            }
        }

        HasActiveObjectives = ActiveObjectives.Count > 0;
        HasCompletedObjectives = CompletedObjectives.Count > 0;
        foreach (var problem in spottedProblems)
        {
            SpottedProblems.Add(new SpottedProblemItem
            {
                GameId = problem.GameId,
                ChampionName = problem.ChampionName,
                DatePlayed = problem.DatePlayed,
                ProblemText = problem.SpottedProblems,
                Win = problem.Win
            });
        }

        HasSpottedProblems = SpottedProblems.Count > 0;
        HasObjectives = HasActiveObjectives || HasCompletedObjectives;
    }
}
