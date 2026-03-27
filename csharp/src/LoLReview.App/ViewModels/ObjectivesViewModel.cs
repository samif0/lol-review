#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.Core.Data.Repositories;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>Display model for an objective card.</summary>
public sealed class ObjectiveDisplayItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string SkillArea { get; init; } = "";
    public string Type { get; init; } = "primary";
    public string CompletionCriteria { get; init; } = "";
    public string Description { get; init; } = "";
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string Status { get; init; } = "active";

    // Level info
    public string LevelName { get; init; } = "Exploring";
    public int LevelIndex { get; init; }
    public double Progress { get; init; }
    public int? NextThreshold { get; init; }
    public bool CanComplete { get; init; }
    public bool SuggestComplete { get; init; }

    /// <summary>Color brush for the current level, distinct per progression stage.</summary>
    public SolidColorBrush LevelColorBrush => LevelIndex switch
    {
        0 => new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),  // Exploring: Gray #6b7280
        1 => new SolidColorBrush(ColorHelper.FromArgb(255, 59, 130, 246)),   // Drilling: Blue #3b82f6
        2 => new SolidColorBrush(ColorHelper.FromArgb(255, 139, 92, 246)),   // Ingraining: Purple #8b5cf6
        3 => new SolidColorBrush(ColorHelper.FromArgb(255, 200, 155, 60)),   // Ready: Gold #c89b3c
        _ => new SolidColorBrush(ColorHelper.FromArgb(255, 107, 114, 128)),  // fallback Gray
    };

    // Derived display properties
    public bool IsMental => Type == "mental";
    public string TypeBadge => IsMental ? "MENTAL" : "PRIMARY";
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
    public string CriteriaText => $"Success: {CompletionCriteria}";
    public string RemainingText
    {
        get
        {
            if (CanComplete) return "";
            var remaining = Math.Max(0, 30 - GameCount);
            return $"{remaining} more games to unlock completion";
        }
    }
}

/// <summary>Display model for a completed objective.</summary>
public sealed class CompletedObjectiveItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public int Score { get; init; }
    public int GameCount { get; init; }
    public string SummaryText => $"{Score} pts  \u2022  {GameCount} games";
}

/// <summary>ViewModel for the Objectives page.</summary>
public partial class ObjectivesViewModel : ObservableObject
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isCreating;

    [ObservableProperty]
    private bool _hasObjectives;

    [ObservableProperty]
    private bool _hasActiveObjectives;

    [ObservableProperty]
    private bool _hasCompletedObjectives;

    [ObservableProperty]
    private bool _showCompleted;

    // Create form fields
    [ObservableProperty]
    private string _newTitle = "";

    [ObservableProperty]
    private string _newSkillArea = "";

    [ObservableProperty]
    private int _newTypeIndex; // 0 = primary, 1 = mental

    [ObservableProperty]
    private string _newCriteria = "";

    [ObservableProperty]
    private string _newDescription = "";

    [ObservableProperty]
    private bool _canCreate;

    public ObservableCollection<ObjectiveDisplayItem> ActiveObjectives { get; } = new();
    public ObservableCollection<CompletedObjectiveItem> CompletedObjectives { get; } = new();

    public ObjectivesViewModel(
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService)
    {
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
    }

    partial void OnNewTitleChanged(string value)
    {
        CanCreate = !string.IsNullOrWhiteSpace(value);
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
        }
    }

    [RelayCommand]
    private async Task CreateObjectiveAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle)) return;

        var type = NewTypeIndex == 1 ? "mental" : "primary";
        await _objectivesRepo.CreateAsync(
            NewTitle.Trim(),
            NewSkillArea.Trim(),
            type,
            NewCriteria.Trim(),
            NewDescription.Trim());

        ClearForm();
        IsCreating = false;
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task MarkCompleteAsync(long objectiveId)
    {
        await _objectivesRepo.MarkCompleteAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private async Task DeleteObjectiveAsync(long objectiveId)
    {
        await _objectivesRepo.DeleteAsync(objectiveId);
        await RefreshDataAsync();
    }

    [RelayCommand]
    private void ToggleCompleted()
    {
        ShowCompleted = !ShowCompleted;
    }

    private void ClearForm()
    {
        NewTitle = "";
        NewSkillArea = "";
        NewTypeIndex = 0;
        NewCriteria = "";
        NewDescription = "";
    }

    private async Task RefreshDataAsync()
    {
        var allObjectives = await _objectivesRepo.GetAllAsync();

        ActiveObjectives.Clear();
        CompletedObjectives.Clear();

        foreach (var obj in allObjectives)
        {
            var status = obj.GetValueOrDefault("status")?.ToString() ?? "active";
            var id = Convert.ToInt64(obj.GetValueOrDefault("id") ?? 0);
            var title = obj.GetValueOrDefault("title")?.ToString() ?? "";
            var skillArea = obj.GetValueOrDefault("skill_area")?.ToString() ?? "";
            var type = obj.GetValueOrDefault("type")?.ToString() ?? "primary";
            var criteria = obj.GetValueOrDefault("completion_criteria")?.ToString() ?? "";
            var description = obj.GetValueOrDefault("description")?.ToString() ?? "";
            var score = Convert.ToInt32(obj.GetValueOrDefault("score") ?? 0);
            var gameCount = Convert.ToInt32(obj.GetValueOrDefault("game_count") ?? 0);

            if (status == "active")
            {
                var levelInfo = IObjectivesRepository.GetLevelInfo(score, gameCount);

                ActiveObjectives.Add(new ObjectiveDisplayItem
                {
                    Id = id,
                    Title = title,
                    SkillArea = skillArea,
                    Type = type,
                    CompletionCriteria = criteria,
                    Description = description,
                    Score = score,
                    GameCount = gameCount,
                    Status = status,
                    LevelName = levelInfo.LevelName,
                    LevelIndex = levelInfo.LevelIndex,
                    Progress = levelInfo.Progress,
                    NextThreshold = levelInfo.NextThreshold,
                    CanComplete = levelInfo.CanComplete,
                    SuggestComplete = levelInfo.SuggestComplete,
                });
            }
            else
            {
                CompletedObjectives.Add(new CompletedObjectiveItem
                {
                    Id = id,
                    Title = title,
                    Score = score,
                    GameCount = gameCount,
                });
            }
        }

        HasActiveObjectives = ActiveObjectives.Count > 0;
        HasCompletedObjectives = CompletedObjectives.Count > 0;
        HasObjectives = HasActiveObjectives || HasCompletedObjectives;
    }
}
