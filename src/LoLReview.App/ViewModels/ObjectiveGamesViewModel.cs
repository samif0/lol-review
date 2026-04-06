#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.App.Contracts;
using LoLReview.App.Styling;
using LoLReview.Core.Data.Repositories;
using Microsoft.UI.Xaml.Media;

namespace LoLReview.App.ViewModels;

/// <summary>One game row in the objective games list.</summary>
public sealed class ObjectiveGameRow
{
    public long GameId { get; init; }
    public bool Practiced { get; init; }
    public string ExecutionNote { get; init; } = "";
    public string ChampionName { get; init; } = "";
    public bool Win { get; init; }
    public string DateText { get; init; } = "";
    public string KdaText { get; init; } = "";
    public bool HasReview { get; init; }
    public bool HasExecutionNote { get; init; }

    public string ResultText => Win ? "W" : "L";
    public string PracticedText => Practiced ? "Practiced" : "Skipped";
    public SolidColorBrush PracticedBackgroundBrush => Practiced
        ? AppSemanticPalette.Brush(AppSemanticPalette.PositiveDimHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.NeutralDimHex);
    public SolidColorBrush PracticedForegroundBrush => Practiced
        ? AppSemanticPalette.Brush(AppSemanticPalette.PositiveHex)
        : AppSemanticPalette.Brush(AppSemanticPalette.NeutralHex);
}

/// <summary>ViewModel for the Objective Games page — shows all games linked to one objective.</summary>
public partial class ObjectiveGamesViewModel : ObservableObject
{
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly INavigationService _navigationService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _objectiveTitle = "";

    [ObservableProperty]
    private string _objectiveStatus = "";

    [ObservableProperty]
    private bool _hasGames;

    [ObservableProperty]
    private int _practicedCount;

    [ObservableProperty]
    private int _totalCount;

    private long _objectiveId;

    public ObservableCollection<ObjectiveGameRow> Games { get; } = new();

    public ObjectiveGamesViewModel(
        IObjectivesRepository objectivesRepo,
        INavigationService navigationService)
    {
        _objectivesRepo = objectivesRepo;
        _navigationService = navigationService;
    }

    [RelayCommand]
    private async Task LoadAsync(long objectiveId)
    {
        _objectiveId = objectiveId;
        IsLoading = true;
        try
        {
            var objective = await _objectivesRepo.GetAsync(objectiveId);
            if (objective is not null)
            {
                ObjectiveTitle = objective.Title;
                ObjectiveStatus = objective.Status == "active" ? "Active" : "Completed";
            }

            var entries = await _objectivesRepo.GetGamesForObjectiveAsync(objectiveId);
            Games.Clear();

            foreach (var entry in entries)
            {
                var date = entry.Timestamp > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(entry.Timestamp).LocalDateTime.ToString("MMM d, yyyy")
                    : "";

                var kda = $"{entry.Kills:F0}/{entry.Deaths:F0}/{entry.Assists:F0}";

                Games.Add(new ObjectiveGameRow
                {
                    GameId = entry.GameId,
                    Practiced = entry.Practiced,
                    ExecutionNote = entry.ExecutionNote,
                    ChampionName = entry.ChampionName,
                    Win = entry.Win,
                    DateText = date,
                    KdaText = kda,
                    HasReview = entry.HasReview,
                    HasExecutionNote = !string.IsNullOrWhiteSpace(entry.ExecutionNote),
                });
            }

            TotalCount = Games.Count;
            PracticedCount = Games.Count(g => g.Practiced);
            HasGames = Games.Count > 0;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenReview(long gameId)
    {
        _navigationService.NavigateTo("review", gameId);
    }

    [RelayCommand]
    private void GoBack()
    {
        _navigationService.GoBack();
    }
}
