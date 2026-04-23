#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.Core.Constants;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// Background service that monitors the League client for game phase transitions.
/// </summary>
public sealed class GameMonitorService : BackgroundService, IGameMonitorService
{
    private readonly ILcuCredentialDiscovery _credentialDiscovery;
    private readonly ILcuClient _lcuClient;
    private readonly ILiveEventApi _liveEventApi;
    private readonly IGameEndCaptureService _gameEndCaptureService;
    private readonly IMatchHistoryReconciliationService _matchHistoryReconciliationService;
    private readonly IMessenger _messenger;
    private readonly ILogger<GameMonitorService> _logger;
    private readonly GameMonitorTransitionEvaluator _transitionEvaluator = new();
    private readonly GameMonitorRuntimeState _state = new();

    private LiveEventCollector? _eventCollector;
    private CancellationTokenSource? _collectorCts;
    private Task? _collectorTask;

    private const int MaxCredentialBackoffTicks = 6;

    /// <summary>
    /// Queue IDs that skip the review flow — everything except ranked solo (420) and ranked flex (440).
    /// Normal Draft (400), Normal Blind (430), Quickplay (490), ARAM (450), Arena (1700), URF, etc.
    /// </summary>
    private static readonly HashSet<int> CasualQueueIds =
    [
        400,  // Normal Draft
        430,  // Normal Blind
        490,  // Quickplay / Normal
        450,  // ARAM
        1700, // Arena
        1900, // URF (pick)
        900,  // URF
        1010, // Snow URF
        1020, // One for All
        2070, // Swarm
        2000, // Tutorial
        2010, // Tutorial
        2020, // Tutorial
        0,    // Custom / unknown
    ];

    public GameMonitorService(
        ILcuCredentialDiscovery credentialDiscovery,
        ILcuClient lcuClient,
        ILiveEventApi liveEventApi,
        IGameEndCaptureService gameEndCaptureService,
        IMatchHistoryReconciliationService matchHistoryReconciliationService,
        IMessenger messenger,
        ILogger<GameMonitorService> logger)
    {
        _credentialDiscovery = credentialDiscovery;
        _lcuClient = lcuClient;
        _liveEventApi = liveEventApi;
        _gameEndCaptureService = gameEndCaptureService;
        _matchHistoryReconciliationService = matchHistoryReconciliationService;
        _messenger = messenger;
        _logger = logger;
    }

    /// <inheritdoc />
    public Func<long, bool>? CheckGameSaved { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Game monitor started");
        CoreDiagnostics.WriteVerbose("LCU: GameMonitor ExecuteAsync started");

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(GameConstants.GameMonitorPollIntervalS));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Monitor tick error");
                CoreDiagnostics.WriteVerbose($"LCU: Tick top-level exception={ex.GetType().Name}:{ex.Message}");
            }
        }

        _logger.LogInformation("Game monitor stopped");
        CoreDiagnostics.WriteVerbose("LCU: GameMonitor ExecuteAsync stopped");
    }

    internal Task TickOnceAsync(CancellationToken cancellationToken = default) => TickAsync(cancellationToken);

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        CoreDiagnostics.WriteVerbose(
            $"LCU: Tick start connected={_state.IsConnected} credBackoff={_state.CredentialBackoffTicks}");

        if (!await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        GamePhase phase;
        try
        {
            phase = await _lcuClient.GetGameflowPhaseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "League client connection lost");
            CoreDiagnostics.WriteVerbose($"LCU: Tick gameflow exception={ex.GetType().Name}:{ex.Message}");
            await HandleDisconnectedAsync().ConfigureAwait(false);
            return;
        }

        _state.ConnectedTicks++;

        if (phase != _state.LastPhase)
        {
            _logger.LogInformation("Gameflow phase changed {PreviousPhase} -> {CurrentPhase}", _state.LastPhase, phase);
            CoreDiagnostics.WriteVerbose($"LCU: Phase changed {_state.LastPhase} -> {phase}");
        }

        var plan = _transitionEvaluator.Evaluate(_state, phase);

        if (plan.ReconcileOnStartup)
        {
            CoreDiagnostics.WriteVerbose($"LCU: Startup reconcile triggered at connectedTicks={_state.ConnectedTicks} phase={phase}");
            await PublishMissedGamesAsync(cancellationToken, "startup").ConfigureAwait(false);
            _state.StartupReconcilePending = false;
        }

        if (plan.NotifyChampSelectStarted)
        {
            await HandleChampSelectStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (phase == GamePhase.ChampSelect)
        {
            await PollChampSelectUpdatesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (plan.NotifyChampSelectCancelled)
        {
            _logger.LogInformation("Champ select cancelled (went to {Phase})", phase);
            _state.LastChampSelectMy = "";
            _state.LastChampSelectEnemy = "";
            _state.LastChampSelectMyPosition = "";
            _messenger.Send(new ChampSelectCancelledMessage());
        }

        if (plan.NotifyGameStarted)
        {
            _logger.LogInformation("Game loading - closing pre-game window");
            _messenger.Send(new GameStartedMessage());

            if (!_state.CurrentGameIsCasual)
            {
                StartEventCollector();
            }
        }

        if (plan.HandleGameEnded)
        {
            if (_state.CurrentGameIsCasual)
            {
                _logger.LogInformation("Casual game ended - skipping");
            }
            else
            {
                _logger.LogInformation("Game ended - fetching stats");
                await PublishGameEndedAsync(cancellationToken).ConfigureAwait(false);
                _state.ReconcilePending = true;
            }

            if (plan.ResetCurrentGameCasual)
            {
                _state.CurrentGameIsCasual = false;
            }
        }

        if (plan.ReconcileMatchHistory)
        {
            // Attempt to find and report the just-finished game from match history.
            // If nothing is found (Riot's API hasn't processed it yet), keep retrying for up to 3 min.
            var found = await TryPublishPostGameReconcileAsync(cancellationToken).ConfigureAwait(false);
            if (found)
            {
                _state.ReconcilePending = false;
                _state.PostGameReconcileRetriesRemaining = 0;
            }
            else
            {
                // Nothing in match history yet — schedule retries via the ReconcilePending flag
                if (_state.PostGameReconcileRetriesRemaining <= 0)
                {
                    // First miss: allow up to ~36 retries × 5s poll = ~3 minutes of waiting
                    _state.PostGameReconcileRetriesRemaining = 36;
                }
                else
                {
                    _state.PostGameReconcileRetriesRemaining--;
                    if (_state.PostGameReconcileRetriesRemaining <= 0)
                    {
                        // Gave up — stop retrying
                        _state.ReconcilePending = false;
                        CoreDiagnostics.WriteVerbose("LCU: PostGameReconcile gave up after retries");
                    }
                    else
                    {
                        // Keep ReconcilePending = true so the next idle tick retries
                        CoreDiagnostics.WriteVerbose(
                            $"LCU: PostGameReconcile not found yet, retries left={_state.PostGameReconcileRetriesRemaining}");
                    }
                }
            }
        }

        _state.LastPhase = phase;
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_state.IsConnected)
        {
            return true;
        }

        if (_state.CredentialBackoffTicks > 0)
        {
            _state.CredentialBackoffTicks--;
            CoreDiagnostics.WriteVerbose($"LCU: Tick backing off remaining={_state.CredentialBackoffTicks}");
            return false;
        }

        var credentials = _credentialDiscovery.FindCredentials();
        if (credentials is null)
        {
            _state.CredentialBackoffTicks = Math.Min(_state.CredentialBackoffTicks + 2, MaxCredentialBackoffTicks);
            CoreDiagnostics.WriteVerbose($"LCU: Tick no credentials nextBackoff={_state.CredentialBackoffTicks}");
            return false;
        }

        _state.CredentialBackoffTicks = 0;
        _lcuClient.Configure(credentials);
        CoreDiagnostics.WriteVerbose($"LCU: Tick configured client port={credentials.Port}");

        if (!await _lcuClient.IsConnectedAsync(cancellationToken).ConfigureAwait(false))
        {
            CoreDiagnostics.WriteVerbose("LCU: Tick IsConnectedAsync=false");
            return false;
        }

        _state.IsConnected = true;
        _logger.LogInformation("Connected to League client");
        CoreDiagnostics.WriteVerbose("LCU: Tick IsConnectedAsync=true");
        _messenger.Send(new LcuConnectionChangedMessage(true));
        return true;
    }

    private async Task HandleChampSelectStartedAsync(CancellationToken cancellationToken)
    {
        var queueId = await _lcuClient.GetLobbyQueueIdAsync(cancellationToken).ConfigureAwait(false);
        _state.CurrentGameIsCasual = IsCasualQueue(queueId);

        var modeLabel = _state.CurrentGameIsCasual ? "casual" : "ranked/normal";
        _logger.LogInformation("Champ select started (queue {QueueId} - {Mode})", queueId, modeLabel);

        // Best-effort: fetch champion picks to surface matchup history in pre-game
        var (myChampion, enemyLaner, myPosition) = await _lcuClient.GetChampSelectInfoAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Champ select info: myChamp={MyChamp} enemy={Enemy} myPos={MyPos}",
            myChampion, enemyLaner, myPosition);
        CoreDiagnostics.WriteVerbose(
            $"LCU: ChampSelectStarted queue={queueId} myChamp='{myChampion}' enemy='{enemyLaner}' myPos='{myPosition}'");

        _state.LastChampSelectMy = myChampion ?? "";
        _state.LastChampSelectEnemy = enemyLaner ?? "";
        _state.LastChampSelectMyPosition = myPosition ?? "";
        _messenger.Send(new ChampSelectStartedMessage(queueId, myChampion, enemyLaner, myPosition ?? ""));
    }

    private async Task PollChampSelectUpdatesAsync(CancellationToken cancellationToken)
    {
        string myChampion, enemyLaner, myPosition;
        try
        {
            (myChampion, enemyLaner, myPosition) = await _lcuClient.GetChampSelectInfoAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CoreDiagnostics.WriteVerbose($"LCU: ChampSelect poll exception={ex.GetType().Name}:{ex.Message}");
            return;
        }

        myChampion ??= "";
        enemyLaner ??= "";
        myPosition ??= "";

        if (myChampion == _state.LastChampSelectMy
            && enemyLaner == _state.LastChampSelectEnemy
            && myPosition == _state.LastChampSelectMyPosition)
        {
            return;
        }

        _logger.LogInformation(
            "Champ select update: myChamp={MyChamp} enemy={Enemy} myPos={MyPos} (was myChamp={PrevMy} enemy={PrevEnemy} myPos={PrevPos})",
            myChampion, enemyLaner, myPosition,
            _state.LastChampSelectMy, _state.LastChampSelectEnemy, _state.LastChampSelectMyPosition);
        CoreDiagnostics.WriteVerbose(
            $"LCU: ChampSelectUpdated myChamp='{myChampion}' enemy='{enemyLaner}' myPos='{myPosition}' (was my='{_state.LastChampSelectMy}' enemy='{_state.LastChampSelectEnemy}' pos='{_state.LastChampSelectMyPosition}')");

        _state.LastChampSelectMy = myChampion;
        _state.LastChampSelectEnemy = enemyLaner;
        _state.LastChampSelectMyPosition = myPosition;
        _messenger.Send(new ChampSelectUpdatedMessage(myChampion, enemyLaner, myPosition));
    }

    private void StartEventCollector()
    {
        _ = StopEventCollectorAsync();

        _collectorCts = new CancellationTokenSource();
        _eventCollector = new LiveEventCollector(
            _liveEventApi,
            _logger,
            TimeSpan.FromSeconds(GameConstants.LiveEventPollIntervalS));

        _collectorTask = _eventCollector.StartAsync(_collectorCts.Token);
        _logger.LogInformation("Live event collector task started");
    }

    private async Task<List<GameEvent>> StopEventCollectorAsync()
    {
        var events = new List<GameEvent>();

        if (_eventCollector is not null)
        {
            if (_collectorCts is not null)
            {
                await _collectorCts.CancelAsync().ConfigureAwait(false);
            }

            if (_collectorTask is not null)
            {
                try
                {
                    await _collectorTask.WaitAsync(
                        TimeSpan.FromSeconds(GameConstants.MonitorStopTimeoutS)).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Event collector did not stop within timeout");
                }
                catch (OperationCanceledException)
                {
                }
            }

            events = await _eventCollector.StopAsync().ConfigureAwait(false);
            _eventCollector = null;
        }

        _collectorCts?.Dispose();
        _collectorCts = null;
        _collectorTask = null;

        return events;
    }

    private async Task PublishGameEndedAsync(CancellationToken cancellationToken)
    {
        var liveEvents = await StopEventCollectorAsync().ConfigureAwait(false);
        if (liveEvents.Count > 0)
        {
            _logger.LogInformation("Collected {Count} live events during game", liveEvents.Count);
        }

        var stats = await _gameEndCaptureService.CaptureAsync(liveEvents, cancellationToken).ConfigureAwait(false);
        if (stats is null)
        {
            _state.ReconcilePending = true;
            return;
        }

        _messenger.Send(new GameEndedMessage(stats));
    }

    /// <summary>
    /// Attempt to find the just-finished game from match history and publish it as a post-game reconcile.
    /// Returns true if at least one candidate was found and published, false if match history is empty.
    /// </summary>
    private async Task<bool> TryPublishPostGameReconcileAsync(CancellationToken cancellationToken)
    {
        CoreDiagnostics.WriteVerbose("LCU: TryPublishPostGameReconcile start");
        var candidates = await _matchHistoryReconciliationService
            .FindMissedGamesAsync(CheckGameSaved, cancellationToken)
            .ConfigureAwait(false);

        CoreDiagnostics.WriteVerbose($"LCU: TryPublishPostGameReconcile count={candidates.Count}");

        if (candidates.Count == 0)
            return false;

        _messenger.Send(new MissedReviewsDetectedMessage(candidates, IsPostGameReconcile: true));
        return true;
    }

    private async Task PublishMissedGamesAsync(CancellationToken cancellationToken, string reason)
    {
        // "postgame" reconcile fires right after InProgress → idle, meaning the user just finished
        // a game. Flag it so the shell can skip the selection dialog and go straight to post-game.
        var isPostGameReconcile = string.Equals(reason, "postgame", StringComparison.Ordinal);

        CoreDiagnostics.WriteVerbose($"LCU: PublishMissedGames start reason={reason} isPostGame={isPostGameReconcile}");
        var candidates = await _matchHistoryReconciliationService
            .FindMissedGamesAsync(CheckGameSaved, cancellationToken)
            .ConfigureAwait(false);

        CoreDiagnostics.WriteVerbose($"LCU: PublishMissedGames result reason={reason} count={candidates.Count}");

        if (candidates.Count > 0)
        {
            _messenger.Send(new MissedReviewsDetectedMessage(candidates, isPostGameReconcile));
        }
    }

    private async Task HandleDisconnectedAsync()
    {
        var wasConnected = _state.IsConnected;
        _state.IsConnected = false;
        _state.ConnectedTicks = 0;
        await StopEventCollectorAsync().ConfigureAwait(false);

        if (wasConnected)
        {
            _messenger.Send(new LcuConnectionChangedMessage(false));
        }
    }

    private static bool IsCasualQueue(int queueId) => CasualQueueIds.Contains(queueId);
}
