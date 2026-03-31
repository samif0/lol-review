#nullable enable

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.App.Contracts;
using LoLReview.App.Helpers;
using LoLReview.App.Services;
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
    private readonly IGameLifecycleWorkflowService _gameLifecycleWorkflow;
    private readonly IUpdateService _updateService;
    private readonly ILogger<ShellViewModel> _logger;
    private bool _hasInitialized;

    // Store pre-game mood from the page so we can pass it to ProcessGameEndAsync
    private int _preGameMood;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _connectionStatusText = "Waiting for League...";

    [ObservableProperty]
    private bool _showUpdateNotice;

    [ObservableProperty]
    private string _updateNoticeText = "";

    [ObservableProperty]
    private bool _showSettingsUpdateBadge;

    public ShellViewModel(
        INavigationService navigationService,
        IDialogService dialogService,
        IGameLifecycleWorkflowService gameLifecycleWorkflow,
        IUpdateService updateService,
        ILogger<ShellViewModel> logger)
    {
        _navigationService = navigationService;
        _dialogService = dialogService;
        _gameLifecycleWorkflow = gameLifecycleWorkflow;
        _updateService = updateService;
        _logger = logger;

        _updateService.StateChanged += OnUpdateStateChanged;
        ApplyUpdateState();

        // Activate the messenger so we receive messages
        IsActive = true;
    }

    [RelayCommand]
    private void Navigate(string pageKey)
    {
        _navigationService.NavigateTo(pageKey);
    }

    [RelayCommand]
    private void OpenUpdateSettings()
    {
        _navigationService.NavigateTo("settings");
    }

    public async Task InitializeAsync()
    {
        if (_hasInitialized)
        {
            return;
        }

        _hasInitialized = true;

        try
        {
            await _updateService.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background update check failed");
        }
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
                var result = await _gameLifecycleWorkflow.ProcessGameEndAsync(
                    new ProcessGameEndRequest(
                        message.Stats,
                        MentalRating: 5,
                        PreGameMood: message.IsRecovered ? 0 : _preGameMood),
                    isRecovered: message.IsRecovered);

                if (!result.WasSaved)
                {
                    _logger.LogInformation("Game skipped (casual/remake)");
                    return;
                }

                if (result.IsRecovered)
                {
                    _logger.LogInformation("Recovered missed game {GameId} saved silently for later review", result.GameId!.Value);
                    return;
                }

                _preGameMood = 0; // Reset for next game

                // 2. Navigate to post-game review page
                _logger.LogInformation("Game end processed for {GameId} -- opening post-game page", result.GameId!.Value);
                WindowActivationHelper.BringMainWindowToFront();
                _navigationService.NavigateTo("postgame", result.GameId!.Value);
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

                if (selectedGames.Count == 0)
                {
                    _logger.LogInformation(
                        "Missed games dialog dismissed; persisted {DismissedCount} declined game(s) out of {Count} candidates",
                        dismissedIds.Length,
                        message.Games.Count);
                    return;
                }

                var result = await _gameLifecycleWorkflow.ReconcileMissedGamesAsync(
                    new ReconcileMissedGamesRequest(
                        SelectedGames: selectedGames,
                        DismissedGameIds: dismissedIds,
                        MentalRating: 5,
                        PreGameMood: 0));

                if (result.IngestedCount > 0)
                {
                    await _dialogService.ShowMessageAsync(
                        "Recent Games Ingested",
                        result.IngestedCount == 1
                            ? "Ingested 1 recent game. It is ready for review."
                            : $"Ingested {result.IngestedCount} recent games. They are ready for review.");
                    _navigationService.NavigateTo("dashboard");
                }

                _logger.LogInformation(
                    "Missed games ingestion completed: selected={Selected} ingested={Ingested} dismissed={Dismissed} candidates={Candidates}",
                    selectedGames.Count,
                    result.IngestedCount,
                    dismissedIds.Length,
                    message.Games.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process missed recent games");
            }
        });
    }

    private void OnUpdateStateChanged(object? sender, EventArgs e)
    {
        Helpers.DispatcherHelper.RunOnUIThread(ApplyUpdateState);
    }

    private void ApplyUpdateState()
    {
        ShowUpdateNotice = _updateService.IsUpdateAvailable;
        ShowSettingsUpdateBadge = _updateService.IsUpdateAvailable;
        UpdateNoticeText = _updateService.IsUpdateAvailable
            ? $"Update ready: v{_updateService.AvailableVersion}"
            : "";
    }
}
