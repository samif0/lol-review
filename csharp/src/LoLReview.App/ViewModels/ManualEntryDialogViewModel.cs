#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LoLReview.Core.Data.Repositories;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

/// <summary>ViewModel for the manual game entry dialog.</summary>
public partial class ManualEntryDialogViewModel : ObservableObject
{
    private readonly IGameRepository _gameRepo;
    private readonly ISessionLogRepository _sessionLogRepo;
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
    private bool _isValid;

    [ObservableProperty]
    private string _errorMessage = "";

    // ── Constructor ─────────────────────────────────────────────────

    public ManualEntryDialogViewModel(
        IGameRepository gameRepo,
        ISessionLogRepository sessionLogRepo,
        ILogger<ManualEntryDialogViewModel> logger)
    {
        _gameRepo = gameRepo;
        _sessionLogRepo = sessionLogRepo;
        _logger = logger;
    }

    // ── Commands ────────────────────────────────────────────────────

    public async Task<bool> SaveAsync()
    {
        // Validate
        if (string.IsNullOrWhiteSpace(ChampionName))
        {
            ErrorMessage = "Champion name is required";
            IsValid = false;
            return false;
        }

        ErrorMessage = "";
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
                    win: IsVictory
                );
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
        }
    }
}
