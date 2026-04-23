#nullable enable

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Revu.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

/// <summary>Display model for an objective assessment in the review flow.</summary>
public partial class ObjectiveAssessment : ObservableObject
{
    public long ObjectiveId { get; init; }
    public string Title { get; init; } = "";
    public string Criteria { get; init; } = "";
    public string Phase { get; init; } = ObjectivePhases.InGame;
    public bool IsPriority { get; init; }
    public string PhaseLabel => ObjectivePhases.ToDisplayLabel(Phase);

    [ObservableProperty]
    private bool _practiced;

    [ObservableProperty]
    private string _executionNote = "";
}

/// <summary>ViewModel for the manual game entry page.</summary>
public partial class ManualEntryDialogViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
    private readonly IObjectivesRepository _objectivesRepo;
    private readonly ILogger<ManualEntryDialogViewModel> _logger;

    // ── Observable Properties ───────────────────────────────────────

    [ObservableProperty]
    private string _championName = "";

    [ObservableProperty]
    private bool _isVictory;

    [ObservableProperty]
    private int _kills;

    [ObservableProperty]
    private int _deaths;

    [ObservableProperty]
    private int _assists;

    // Text wrappers for TextBox binding (NumberBox removed to avoid XamlControlsResources dependency)
    public string KillsText
    {
        get => Kills.ToString();
        set { if (int.TryParse(value, out var v) && v >= 0) Kills = v; OnPropertyChanged(); }
    }

    public string DeathsText
    {
        get => Deaths.ToString();
        set { if (int.TryParse(value, out var v) && v >= 0) Deaths = v; OnPropertyChanged(); }
    }

    public string AssistsText
    {
        get => Assists.ToString();
        set { if (int.TryParse(value, out var v) && v >= 0) Assists = v; OnPropertyChanged(); }
    }

    [ObservableProperty]
    private string _gameMode = "Manual Entry";

    [ObservableProperty]
    private string _reviewNotes = "";

    [ObservableProperty]
    private string _mistakes = "";

    [ObservableProperty]
    private string _wentWell = "";

    [ObservableProperty]
    private string _focusNext = "";

    [ObservableProperty]
    private int _mentalRating = 5;

    [ObservableProperty]
    private bool _isValid;

    [ObservableProperty]
    private string _errorMessage = "";

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private bool _hasObjectives;

    public ObservableCollection<ObjectiveAssessment> Objectives { get; } = new();

    // ── Constructor ─────────────────────────────────────────────────

    public ManualEntryDialogViewModel(
        IGameRepository gameRepo,
        ISessionLogRepository sessionLogRepo,
        IObjectivesRepository objectivesRepo,
        ILogger<ManualEntryDialogViewModel> logger)
    {
        _gameRepo = gameRepo;
        _sessionLogRepo = sessionLogRepo;
        _objectivesRepo = objectivesRepo;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    [RelayCommand]
    private async Task LoadObjectivesAsync()
    {
        try
        {
            var active = (await _objectivesRepo.GetActiveAsync())
                .Where(objective => ObjectivePhases.ShowsInPostGame(objective.Phase))
                .ToList();
            Objectives.Clear();
            foreach (var obj in active)
            {
                Objectives.Add(new ObjectiveAssessment
                {
                    ObjectiveId = obj.Id,
                    Title = obj.Title,
                    Criteria = obj.CompletionCriteria,
                    Phase = obj.Phase,
                });
            }
            HasObjectives = Objectives.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load objectives");
        }
    }

    public async Task<bool> SaveAsync()
    {
        // Validate
        if (string.IsNullOrWhiteSpace(ChampionName))
        {
            ErrorMessage = "Champion name is required";
            HasError = true;
            IsValid = false;
            return false;
        }

        ErrorMessage = "";
        HasError = false;
        IsValid = true;

        try
        {
            var gameId = await _gameRepo.SaveManualAsync(
                championName: ChampionName.Trim(),
                win: IsVictory,
                kills: Kills,
                deaths: Deaths,
                assists: Assists,
                gameMode: string.IsNullOrWhiteSpace(GameMode) ? "Manual Entry" : GameMode.Trim(),
                notes: ReviewNotes.Trim(),
                mistakes: Mistakes.Trim(),
                wentWell: WentWell.Trim(),
                focusNext: FocusNext.Trim()
            );

            // Log to session
            if (gameId > 0)
            {
                await _sessionLogRepo.LogGameAsync(
                    gameId: gameId,
                    championName: ChampionName.Trim(),
                    win: IsVictory,
                    mentalRating: MentalRating
                );

                // Record objective assessments
                foreach (var obj in Objectives)
                {
                    await _objectivesRepo.RecordGameAsync(
                        gameId,
                        obj.ObjectiveId,
                        obj.Practiced,
                        obj.ExecutionNote);
                }
            }

            _logger.LogInformation("Manual game entry saved: {Champion} ({Result})",
                ChampionName, IsVictory ? "W" : "L");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save manual game entry");
            ErrorMessage = "Failed to save game entry";
            return false;
        }
    }

    // ── Property change validation ──────────────────────────────────

    partial void OnChampionNameChanged(string value)
    {
        IsValid = !string.IsNullOrWhiteSpace(value);
        if (IsValid)
        {
            ErrorMessage = "";
            HasError = false;
        }
    }
}
