#nullable enable

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Revu.App.Contracts;
using Revu.App.Helpers;
using Revu.App.Services;
using Revu.Core.Data.Repositories;
using Revu.Core.Lcu;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.App.ViewModels;

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
    private readonly IMatchHistoryReconciliationService _matchHistoryReconciliationService;
    private readonly IMissedGameDecisionRepository _missedGameDecisionRepository;
    private readonly ISessionLogRepository _sessionLogRepository;
    private readonly IRulesRepository _rulesRepository;
    private readonly IConfigService _configService;
    private readonly IRiotAuthClient _riotAuthClient;
    private readonly IUpdateService _updateService;
    private readonly ILogger<ShellViewModel> _logger;
    private bool _hasInitialized;
    private bool _hasTriggeredConnectedMissedGamesCheck;
    private bool _isHandlingMissedGames;
    private string? _lastMissedGamesKey;

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

    [ObservableProperty]
    private bool _hasUnreviewedGames;

    // ── Account indicator (sidebar footer) ──────────────────────────

    /// <summary>"chapy" when logged in, "OFF" when logged out.</summary>
    [ObservableProperty]
    private string _accountStatusLabel = "OFF";

    /// <summary>Green when logged in + Riot linked, gold when logged in no ID, muted otherwise.</summary>
    [ObservableProperty]
    private Microsoft.UI.Xaml.Media.Brush? _accountDotBrush;

    /// <summary>Hover tooltip — "Logged in as x@y · chapy#hapy" or similar.</summary>
    [ObservableProperty]
    private string _accountTooltip = "Not signed in.";

    /// <summary>Flyout header line — "Logged in as email@..." or "Not signed in".</summary>
    [ObservableProperty]
    private string _accountFlyoutHeader = "Not signed in.";

    /// <summary>Flyout secondary line — "Riot ID: chapy#hapy (na1)" when linked.</summary>
    [ObservableProperty]
    private string _accountFlyoutSubline = "";

    /// <summary>Whether the logout button should be visible in the flyout.</summary>
    [ObservableProperty]
    private bool _canLogout;

    /// <summary>Short version string for the sidebar footer (e.g. "2.12").</summary>
    public string AppVersion
    {
        get
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            return v is null ? "" : $"{v.Major}.{v.Minor}";
        }
    }

    public ShellViewModel(
        INavigationService navigationService,
        IDialogService dialogService,
        IGameLifecycleWorkflowService gameLifecycleWorkflow,
        IMatchHistoryReconciliationService matchHistoryReconciliationService,
        IMissedGameDecisionRepository missedGameDecisionRepository,
        ISessionLogRepository sessionLogRepository,
        IRulesRepository rulesRepository,
        IConfigService configService,
        IRiotAuthClient riotAuthClient,
        IUpdateService updateService,
        ILogger<ShellViewModel> logger)
    {
        _navigationService = navigationService;
        _dialogService = dialogService;
        _gameLifecycleWorkflow = gameLifecycleWorkflow;
        _matchHistoryReconciliationService = matchHistoryReconciliationService;
        _missedGameDecisionRepository = missedGameDecisionRepository;
        _sessionLogRepository = sessionLogRepository;
        _rulesRepository = rulesRepository;
        _configService = configService;
        _riotAuthClient = riotAuthClient;
        _updateService = updateService;
        _logger = logger;
        RefreshAccountIndicator();

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

    public Task InitializeAsync()
    {
        if (_hasInitialized)
        {
            AppDiagnostics.WriteVerbose("startup.log", "ShellViewModel.InitializeAsync skipped (already initialized)");
            return Task.CompletedTask;
        }

        _hasInitialized = true;
        AppDiagnostics.WriteVerbose("startup.log", "ShellViewModel.InitializeAsync started");

        _ = CheckForMissedGamesOnStartupAsync();
        _ = CheckForUpdatesInBackgroundAsync();

        return Task.CompletedTask;
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        try
        {
            await _updateService.CheckForUpdateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Background update check failed");
        }
        finally
        {
            AppDiagnostics.WriteVerbose("startup.log", "ShellViewModel background update check completed");
        }
    }

    public void Receive(LcuConnectionChangedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            IsConnected = message.IsConnected;
            ConnectionStatusText = message.IsConnected ? "Connected" : "Waiting for League...";
        });

        if (message.IsConnected && !_hasTriggeredConnectedMissedGamesCheck)
        {
            _hasTriggeredConnectedMissedGamesCheck = true;
            AppDiagnostics.WriteVerbose("startup.log", "ShellViewModel triggering missed-games check after first LCU connection");
            _ = CheckForMissedGamesOnStartupAsync();
        }
    }

    public void Receive(ChampSelectStartedMessage message)
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            _logger.LogInformation("Champ select detected -- opening pre-game page (myChamp={MyChamp} enemy={Enemy})",
                message.MyChampion, message.EnemyLaner);
            WindowActivationHelper.BringMainWindowToFront();
            // Pass champion info so pre-game page can load matchup history
            var champInfo = new PreGameChampInfo(message.MyChampion, message.EnemyLaner);
            _navigationService.NavigateTo("pregame", champInfo);
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
        _ = Helpers.DispatcherHelper.RunOnUIThreadAsync(async () =>
        {
            try
            {
                // 1. Save game stats to DB
                var practicedIds = PreGameDialogViewModel.LastPracticedObjectiveIds;
                PreGameDialogViewModel.LastPracticedObjectiveIds = [];
                var result = await _gameLifecycleWorkflow.ProcessGameEndAsync(
                    new ProcessGameEndRequest(
                        message.Stats,
                        MentalRating: 5,
                        PreGameMood: message.IsRecovered ? 0 : _preGameMood,
                        PreGamePracticedObjectiveIds: message.IsRecovered ? null : practicedIds),
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

                // 2a. Post-loss tilt-check gate: if this loss trips the user's loss_streak rule
                // exactly on this game, route to the tilt check first. Post-game opens after.
                var tiltStreak = await TryGetFreshlyTrippedStreakAsync(result.GameId!.Value);
                if (tiltStreak is int streak)
                {
                    _logger.LogInformation(
                        "Loss streak tripped on game {GameId} (streak={Streak}) -- opening tilt check",
                        result.GameId!.Value, streak);
                    WindowActivationHelper.BringMainWindowToFront();
                    _navigationService.NavigateTo("tiltcheck", new TiltCheckInfo(result.GameId!.Value, streak));
                    return;
                }

                // 2b. Navigate to post-game review page
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

    /// <summary>
    /// Returns the consecutive-loss count for the post-loss ritual gate when the just-ended
    /// game makes the streak <em>exactly</em> equal to any active loss_streak rule's threshold.
    /// Returns null if the ritual should not fire (game was a win, no active loss_streak rule,
    /// or the streak is already past the trip point — ritual already fired earlier).
    /// </summary>
    private async Task<int?> TryGetFreshlyTrippedStreakAsync(long justEndedGameId)
    {
        try
        {
            var entries = await _sessionLogRepository.GetTodayAsync();
            if (entries.Count == 0) return null;

            // The just-ended game must be the last entry for this trip to count.
            if (entries[^1].GameId != justEndedGameId) return null;

            // Walk back from the most recent entry counting consecutive losses.
            var consecutive = 0;
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].Win) break;
                consecutive++;
            }
            if (consecutive == 0) return null;

            var rules = await _rulesRepository.GetActiveAsync();
            foreach (var rule in rules)
            {
                if (!string.Equals(rule.RuleType, "loss_streak", StringComparison.OrdinalIgnoreCase))
                    continue;

                var cv = rule.ConditionValue ?? "";
                var thresholdPart = cv.Split(':')[0];
                if (!int.TryParse(thresholdPart, out var threshold) || threshold <= 0)
                    continue;

                // Trip exactly when consecutive matches the threshold — no re-trigger on further losses.
                if (consecutive == threshold)
                {
                    return consecutive;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ritual trip check failed for game {GameId}", justEndedGameId);
        }
        return null;
    }

    public void Receive(MissedReviewsDetectedMessage message)
    {
        _ = Helpers.DispatcherHelper.RunOnUIThreadAsync(async () =>
        {
            // When reconcile fires immediately after a game the monitor tracked (InProgress → idle),
            // skip the selection dialog and go straight to post-game for the most recent candidate.
            if (message.IsPostGameReconcile && message.Games.Count > 0)
            {
                var justFinished = message.Games[0]; // candidates are ordered newest-first
                _logger.LogInformation(
                    "Post-game reconcile: skipping dialog, opening post-game for gameId={GameId}",
                    justFinished.GameId);
                AppDiagnostics.WriteVerbose("startup.log",
                    $"ShellViewModel post-game reconcile fast-path gameId={justFinished.GameId}");

                // Save the game silently first so we have a DB row to review
                var result = await _gameLifecycleWorkflow.ProcessGameEndAsync(
                    new ProcessGameEndRequest(justFinished.Stats, MentalRating: 5, PreGameMood: 0),
                    isRecovered: false);

                if (result.WasSaved)
                {
                    WindowActivationHelper.BringMainWindowToFront();
                    _navigationService.NavigateTo("postgame", result.GameId!.Value);
                }
                return;
            }

            await HandleMissedGamesAsync(message.Games);
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

    /// <summary>
    /// Refreshes the sidebar account status dot + label. Called on construction
    /// and any time the session/Riot-ID changes (e.g. from Settings).
    /// </summary>
    public void RefreshAccountIndicator()
    {
        Helpers.DispatcherHelper.RunOnUIThread(() =>
        {
            var loggedIn = _configService.HasValidRiotSession;
            var riotId = _configService.RiotId;
            var email = _configService.RiotSessionEmail;
            var hashPos = riotId.IndexOf('#');
            var gameName = hashPos > 0 ? riotId.Substring(0, hashPos) : "";

            if (loggedIn && !string.IsNullOrEmpty(gameName))
            {
                AccountStatusLabel = Shorten(gameName, 7).ToUpperInvariant();
                AccountDotBrush = (Microsoft.UI.Xaml.Media.Brush?)Microsoft.UI.Xaml.Application.Current.Resources["WinGreenBrush"];
                AccountTooltip = $"Logged in as {email} \u00b7 {riotId}";
                AccountFlyoutHeader = $"Logged in as {email}.";
                AccountFlyoutSubline = $"{riotId} ({_configService.RiotRegion})";
                CanLogout = true;
            }
            else if (loggedIn)
            {
                AccountStatusLabel = "NO ID";
                AccountDotBrush = (Microsoft.UI.Xaml.Media.Brush?)Microsoft.UI.Xaml.Application.Current.Resources["AccentGoldBrush"];
                AccountTooltip = $"Logged in as {email}. Link your Riot account in Settings.";
                AccountFlyoutHeader = $"Logged in as {email}.";
                AccountFlyoutSubline = "No Riot account linked yet.";
                CanLogout = true;
            }
            else
            {
                AccountStatusLabel = "OFF";
                AccountDotBrush = (Microsoft.UI.Xaml.Media.Brush?)Microsoft.UI.Xaml.Application.Current.Resources["MutedTextBrush"];
                AccountTooltip = "Not signed in. Restart the app to sign up.";
                AccountFlyoutHeader = "Not signed in.";
                AccountFlyoutSubline = "";
                CanLogout = false;
            }
        });
    }

    private static string Shorten(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);

    /// <summary>
    /// Clears the local session, fires a best-effort /auth/logout, refreshes the
    /// sidebar indicator. Stays on the current page — no data is destroyed.
    /// </summary>
    [RelayCommand]
    private async Task LogoutAsync()
    {
        try
        {
            var config = await _configService.LoadAsync();
            var token = config.RiotSessionToken;

            config.RiotSessionToken = "";
            config.RiotSessionEmail = "";
            config.RiotSessionExpiresAt = 0;
            // Reset OnboardingSkipped so a user who logs out is returned to
            // the auth flow on next launch. Otherwise a stale Skip from an
            // earlier session keeps OnboardingComplete true and the user
            // never sees the signup UI again.
            config.OnboardingSkipped = false;
            // Keep RiotId + RiotRegion: the user may log back in and they still apply.
            await _configService.SaveAsync(config);

            if (!string.IsNullOrEmpty(token))
            {
                await _riotAuthClient.LogoutAsync(token);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shell logout failed");
        }
        finally
        {
            RefreshAccountIndicator();
        }
    }

    private async Task CheckForMissedGamesOnStartupAsync()
    {
        try
        {
            AppDiagnostics.WriteVerbose("startup.log", "ShellViewModel startup missed-games check started");
            var candidates = await _matchHistoryReconciliationService.FindMissedGamesAsync(checkGameSaved: null);
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel startup missed-games candidates={candidates.Count}");

            if (candidates.Count == 0)
            {
                return;
            }

            await DispatcherHelper.RunOnUIThreadAsync(() => HandleMissedGamesAsync(candidates));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup missed games check failed");
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel startup missed-games failed: {ex.Message}");
        }
    }

    private async Task HandleMissedGamesAsync(IReadOnlyList<MissedGameCandidate> games)
    {
        if (games.Count == 0)
        {
            return;
        }

        var gamesKey = string.Join(
            ",",
            games.Select(static game => game.GameId)
                .Where(static gameId => gameId > 0)
                .OrderBy(static gameId => gameId));

        if (_isHandlingMissedGames || string.Equals(_lastMissedGamesKey, gamesKey, StringComparison.Ordinal))
        {
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel skipped duplicate missed-games dialog key={gamesKey}");
            return;
        }

        _isHandlingMissedGames = true;
        _lastMissedGamesKey = gamesKey;

        try
        {
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel showing missed-games dialog key={gamesKey}");
            WindowActivationHelper.BringMainWindowToFront();
            var selectedGames = await _dialogService.ShowMissedGamesSelectionAsync(games);
            var dismissedIds = games
                .Select(static game => game.GameId)
                .Except(selectedGames.Select(static game => game.GameId))
                .Where(static gameId => gameId > 0)
                .ToArray();

            if (selectedGames.Count == 0)
            {
                // Persist dismissals even when every game was declined so they don't
                // reappear on the next startup.
                if (dismissedIds.Length > 0)
                {
                    await _missedGameDecisionRepository.MarkDismissedAsync(dismissedIds).ConfigureAwait(false);
                }

                _logger.LogInformation(
                    "Missed games dialog dismissed; persisted {DismissedCount} declined game(s) out of {Count} candidates",
                    dismissedIds.Length,
                    games.Count);
                AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel missed-games dialog dismissed key={gamesKey}");
                return;
            }

            var result = await _gameLifecycleWorkflow.ReconcileMissedGamesAsync(
                new ReconcileMissedGamesRequest(
                    SelectedGames: selectedGames,
                    DismissedGameIds: dismissedIds,
                    MentalRating: 5,
                    PreGameMood: 0));

            if (result.IngestedCount == 1 && selectedGames.Count == 1)
            {
                var recoveredGameId = selectedGames[0].GameId;
                _logger.LogInformation(
                    "Recovered single missed game {GameId} -- opening post-game page",
                    recoveredGameId);
                AppDiagnostics.WriteVerbose(
                    "startup.log",
                    $"ShellViewModel opening postgame for recovered gameId={recoveredGameId}");
                WindowActivationHelper.BringMainWindowToFront();
                _navigationService.NavigateTo("postgame", recoveredGameId);
            }
            else if (result.IngestedCount > 0)
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
                games.Count);
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel missed-games handled key={gamesKey} ingested={result.IngestedCount}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process missed recent games");
            AppDiagnostics.WriteVerbose("startup.log", $"ShellViewModel missed-games failed key={gamesKey}: {ex.Message}");
            _lastMissedGamesKey = null;
        }
        finally
        {
            _isHandlingMissedGames = false;
        }
    }
}
