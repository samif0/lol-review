#nullable enable

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Lcu;
using LoLReview.Core.Services;
using Microsoft.Extensions.Logging;

namespace LoLReview.App.ViewModels;

/// <summary>
/// ViewModel for the main shell / navigation frame.
/// </summary>
public partial class ShellViewModel : ObservableRecipient,
    IRecipient<LcuConnectionChangedMessage>,
    IRecipient<ChampSelectStartedMessage>,
    IRecipient<ChampSelectCancelledMessage>,
    IRecipient<GameStartedMessage>,
    IRecipient<GameEndedMessage>,
    IRecipient<MissedReviewsDetectedMessage>
{
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly IGameService _gameService;
    private readonly IMissedGameDecisionRepository _missedGameDecisionRepository;
    private readonly ILogger<ShellViewModel> _logger;

    // Store pre-game mood from the page so we can pass it to ProcessGameEndAsync
    private int _preGameMood;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatusText = "Waiting for League...";

    public ShellViewModel(
        INavigationService navigationService,
        IDialogService dialogService,
        IGameService gameService,
        IMissedGameDecisionRepository missedGameDecisionRepository,
        ILogger<ShellViewModel> logger)
    {
        _navigationService = navigationService;
        _dialogService = dialogService;
        _gameService = gameService;
        _missedGameDecisionRepository = missedGameDecisionRepository;
        _logger = logger;

        // Activate the messenger so we receive messages
        IsActive = true;
    }

    [RelayCommand]
    private void Navigate(string pageKey)
    {
        _navigationService.NavigateTo(pageKey);
    }

    public void Receive(LcuConnectionChangedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            IsConnected = message.IsConnected;
            ConnectionStatusText = message.IsConnected ? "Connected" : "Waiting for League...";
        });
    }

    public void Receive(ChampSelectStartedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            _logger.LogInformation("Champ select detected -- opening pre-game page");
            WindowActivationHelper.BringMainWindowToFront();
            _navigationService.NavigateTo("pregame");
        });
    }

    public void Receive(ChampSelectCancelledMessage message)
    {
        // Dodge/cancel — leave pre-game page
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            if (_navigationService.CurrentPageKey == "pregame")
            {
                _navigationService.NavigateTo("session");
            }
        });
    }

    public void Receive(GameStartedMessage message)
    {
        // Game loading — leave pre-game page, go to session
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            if (_navigationService.CurrentPageKey == "pregame")
            {
                _navigationService.NavigateTo("session");
            }
        });
    }

    public void Receive(GameEndedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(async () =>
        {
            try
            {
                // 1. Save game stats to DB
                var gameId = await _gameService.ProcessGameEndAsync(
                    message.Stats,
                    mentalRating: 5,
                    preGameMood: message.IsRecovered ? 0 : _preGameMood);

                if (gameId == null)
                {
                    _logger.LogInformation("Game skipped (casual/remake)");
                    return;
                }

                if (message.IsRecovered)
                {
                    _logger.LogInformation("Recovered missed game {GameId} saved silently for later review", gameId.Value);
                    return;
                }

                _preGameMood = 0; // Reset for next game

                // 2. Navigate to post-game review page
                _logger.LogInformation("Game end processed for {GameId} -- opening post-game page", gameId.Value);
                WindowActivationHelper.BringMainWindowToFront();
                _navigationService.NavigateTo("postgame", gameId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process game end");
                _navigationService.NavigateTo("session");
            }
        });
    }

    public void Receive(MissedReviewsDetectedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(async () =>
        {
            try
            {
                var selectedGames = await _dialogService.ShowMissedGamesSelectionAsync(message.Games);
                var dismissedIds = message.Games
                    .Select(static game => game.GameId)
                    .Except(selectedGames.Select(static game => game.GameId))
                    .Where(static gameId => gameId > 0)
                    .ToArray();

                if (dismissedIds.Length > 0)
                {
                    await _missedGameDecisionRepository.MarkDismissedAsync(dismissedIds);
                }

                if (selectedGames.Count == 0)
                {
                    _logger.LogInformation(
                        "Missed games dialog dismissed; persisted {DismissedCount} declined game(s) out of {Count} candidates",
                        dismissedIds.Length,
                        message.Games.Count);
                    return;
                }

                var ingested = 0;
                foreach (var stats in selectedGames.OrderBy(s => s.Timestamp))
                {
                    var gameId = await _gameService.ProcessGameEndAsync(
                        stats,
                        mentalRating: 5,
                        preGameMood: 0);

                    if (gameId is not null)
                    {
                        ingested++;
                    }
                }

                if (ingested > 0)
                {
                    await _dialogService.ShowMessageAsync(
                        "Recent Games Ingested",
                        ingested == 1
                            ? "Ingested 1 recent game. It is ready for review."
                            : $"Ingested {ingested} recent games. They are ready for review.");
                    _navigationService.NavigateTo("dashboard");
                }

                _logger.LogInformation(
                    "Missed games ingestion completed: selected={Selected} ingested={Ingested} dismissed={Dismissed} candidates={Candidates}",
                    selectedGames.Count,
                    ingested,
                    dismissedIds.Length,
                    message.Games.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process missed recent games");
            }
        });
    }
}
