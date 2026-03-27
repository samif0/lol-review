#nullable enable

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
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
    IRecipient<GameEndedMessage>
{
    private readonly INavigationService _navigationService;
    private readonly IDialogService _dialogService;
    private readonly IGameService _gameService;
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
        ILogger<ShellViewModel> logger)
    {
        _navigationService = navigationService;
        _dialogService = dialogService;
        _gameService = gameService;
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
                    message.Stats, mentalRating: 5, preGameMood: _preGameMood);

                _preGameMood = 0; // Reset for next game

                if (gameId == null)
                {
                    _logger.LogInformation("Game skipped (casual/remake)");
                    return;
                }

                // 2. Navigate to post-game review page
                _navigationService.NavigateTo("postgame", gameId.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process game end");
                _navigationService.NavigateTo("session");
            }
        });
    }
}
