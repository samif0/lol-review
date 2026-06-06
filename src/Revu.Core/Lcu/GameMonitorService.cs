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

    /// <inheritdoc />
    public bool IsConnected => _state.IsConnected;

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
            _state.LastChampSelectMapJson = "";
            _state.ChampSelectNotified = false; // re-arm for the next champ select
            _messenger.Send(new ChampSelectCancelledMessage());
        }

        if (plan.NotifyGameStarted)
        {
            _logger.LogInformation("Game loading - starting live-event collector");
            // v2.18 (F5): champ select is over — re-arm the notify flag so the
            // NEXT game's champ select fires the pre-game page fresh.
            _state.ChampSelectNotified = false;
            _state.GameStartedNotified = true;
            _messenger.Send(new GameStartedMessage());

            if (!_state.CurrentGameIsCasual)
            {
                StartEventCollector();
            }
        }

        if (plan.GameInProgressCandidate)
        {
            // v2.17.25: only declare "in-game" — which closes the pre-game page and
            // minimizes the window — once the Live Client Data API actually responds.
            // The LCU reports InProgress throughout the loading screen, but the live
            // API doesn't serve until the player is past loading and in the game.
            // This keeps the pre-game page (matchup intel + objective prompt fields)
            // up for the WHOLE loading screen so there's time to type. Casual games
            // skip the live collector, so don't gate them on the live API — fall
            // back to closing as soon as InProgress is seen.
            var inGameForReal = _state.CurrentGameIsCasual
                || await IsLiveGameDataAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (inGameForReal)
            {
                _logger.LogInformation("Confirmed in-game (live data ready) - closing pre-game page");
                _state.GameInProgressNotified = true;
                _messenger.Send(new GameInProgressMessage());
            }
            else
            {
                CoreDiagnostics.WriteVerbose("LCU: InProgress but live data not ready yet - keeping pre-game page up (loading screen)");
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

            // Re-arm the "confirmed in-game" and "game started" one-shots for the next game.
            _state.GameInProgressNotified = false;
            _state.GameStartedNotified = false;
        }

        if (plan.ReconcileMatchHistory)
        {
            // Exponential-ish backoff: only attempt the reconcile HTTP call when
            // the next-eligible time has passed. This covers the same ~3-min window
            // as before but with far fewer LCU round-trips.
            // Backoff sequence after a miss (seconds): 15, 30, 60, 60, 60 (5 retries)
            var now = DateTime.UtcNow;
            var attemptDue = now >= _state.PostGameReconcileNextAttemptUtc;

            if (!attemptDue)
            {
                // Not yet due — skip this tick but keep ReconcilePending so we return here next tick.
                CoreDiagnostics.WriteVerbose(
                    $"LCU: PostGameReconcile waiting for backoff, due in {(_state.PostGameReconcileNextAttemptUtc - now).TotalSeconds:F0}s");
            }
            else
            {
                // Attempt to find and report the just-finished game from match history.
                // If nothing is found (Riot's API hasn't processed it yet), keep retrying.
                var found = await TryPublishPostGameReconcileAsync(cancellationToken).ConfigureAwait(false);
                if (found)
                {
                    _state.ReconcilePending = false;
                    _state.PostGameReconcileRetriesRemaining = 0;
                    _state.PostGameReconcileNextAttemptUtc = DateTime.MinValue;
                }
                else
                {
                    // Nothing in match history yet — schedule next attempt with backoff.
                    // Sequence: first miss→15s, 2nd→30s, 3rd+→60s. Max 5 retries total.
                    if (_state.PostGameReconcileRetriesRemaining <= 0)
                    {
                        // First miss — initialise the retry counter and set the first backoff delay.
                        _state.PostGameReconcileRetriesRemaining = 5;
                        _state.PostGameReconcileNextAttemptUtc = now.AddSeconds(15);
                    }
                    else
                    {
                        _state.PostGameReconcileRetriesRemaining--;
                        if (_state.PostGameReconcileRetriesRemaining <= 0)
                        {
                            // Gave up — stop retrying
                            _state.ReconcilePending = false;
                            _state.PostGameReconcileNextAttemptUtc = DateTime.MinValue;
                            CoreDiagnostics.WriteVerbose("LCU: PostGameReconcile gave up after retries");
                        }
                        else
                        {
                            // Schedule next attempt: 30s for the second retry, 60s thereafter.
                            var delaySecs = _state.PostGameReconcileRetriesRemaining >= 4 ? 30 : 60;
                            _state.PostGameReconcileNextAttemptUtc = now.AddSeconds(delaySecs);
                            CoreDiagnostics.WriteVerbose(
                                $"LCU: PostGameReconcile not found yet, retries left={_state.PostGameReconcileRetriesRemaining} nextIn={delaySecs}s");
                        }
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
        // Defensive re-arm: a fresh champ select always starts a new game cycle,
        // so the "confirmed in-game" and "game started" one-shots should be ready
        // even if the prior game's end was never observed (client restart, odd
        // phase jumps).
        _state.GameInProgressNotified = false;
        _state.GameStartedNotified = false;

        var modeLabel = _state.CurrentGameIsCasual ? "casual" : "ranked/normal";
        _logger.LogInformation("Champ select started (queue {QueueId} - {Mode})", queueId, modeLabel);

        // Best-effort: fetch champion picks to surface matchup history in pre-game
        var snap = await _lcuClient.GetChampSelectSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var myChampion = snap.MyChampion;
        var enemyLaner = snap.EnemyLaner;
        var myPosition = snap.MyPosition;
        var mapJson = SerializeParticipantMap(snap.ParticipantMap);
        _logger.LogDebug(
            "Champ select info: myChamp={MyChamp} enemy={Enemy} myPos={MyPos} mapKeys={MapKeys}",
            myChampion, enemyLaner, myPosition, snap.ParticipantMap.Count);
        CoreDiagnostics.WriteVerbose(
            $"LCU: ChampSelectStarted queue={queueId} myChamp='{myChampion}' enemy='{enemyLaner}' myPos='{myPosition}' mapKeys={snap.ParticipantMap.Count}");

        _state.LastChampSelectMy = myChampion ?? "";
        _state.LastChampSelectEnemy = enemyLaner ?? "";
        _state.LastChampSelectMyPosition = myPosition ?? "";
        _state.LastChampSelectMapJson = mapJson;
        _messenger.Send(new ChampSelectStartedMessage(queueId, myChampion ?? "", enemyLaner ?? "", myPosition ?? "", mapJson));
        // v2.18 (F5): mark the cycle notified so the evaluator stops re-firing the
        // recovery path. Only set AFTER a successful send — if any LCU call above
        // threw, we never reach here and the next tick retries.
        _state.ChampSelectNotified = true;
    }

    private static string SerializeParticipantMap(IReadOnlyDictionary<string, string> map)
    {
        if (map is null || map.Count == 0) return "";
        try { return System.Text.Json.JsonSerializer.Serialize(map); }
        catch { return ""; }
    }

    private async Task PollChampSelectUpdatesAsync(CancellationToken cancellationToken)
    {
        ChampSelectSnapshot snap;
        try
        {
            snap = await _lcuClient.GetChampSelectSnapshotAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CoreDiagnostics.WriteVerbose($"LCU: ChampSelect poll exception={ex.GetType().Name}:{ex.Message}");
            return;
        }

        var myChampion = snap.MyChampion ?? "";
        var enemyLaner = snap.EnemyLaner ?? "";
        var myPosition = snap.MyPosition ?? "";
        var mapJson = SerializeParticipantMap(snap.ParticipantMap);

        if (myChampion == _state.LastChampSelectMy
            && enemyLaner == _state.LastChampSelectEnemy
            && myPosition == _state.LastChampSelectMyPosition
            && mapJson == _state.LastChampSelectMapJson)
        {
            return;
        }

        _logger.LogInformation(
            "Champ select update: myChamp={MyChamp} enemy={Enemy} myPos={MyPos} mapKeys={MapKeys}",
            myChampion, enemyLaner, myPosition, snap.ParticipantMap.Count);
        CoreDiagnostics.WriteVerbose(
            $"LCU: ChampSelectUpdated myChamp='{myChampion}' enemy='{enemyLaner}' myPos='{myPosition}' mapKeys={snap.ParticipantMap.Count}");

        _state.LastChampSelectMy = myChampion;
        _state.LastChampSelectEnemy = enemyLaner;
        _state.LastChampSelectMyPosition = myPosition;
        _state.LastChampSelectMapJson = mapJson;
        _messenger.Send(new ChampSelectUpdatedMessage(myChampion, enemyLaner, myPosition, mapJson));
    }

    private void StartEventCollector()
    {
        // Snapshot the OLD collector fields into locals BEFORE reassigning them.
        // The fire-and-forget teardown must close over the old instances; if it
        // read the fields instead, it would cancel/dispose the NEW CancellationTokenSource
        // (assigned below) the moment a second StartEventCollector races it,
        // producing an ObjectDisposedException.
        var oldCts = _collectorCts;
        var oldCollector = _eventCollector;
        var oldTask = _collectorTask;

        _collectorCts = new CancellationTokenSource();
        _eventCollector = new LiveEventCollector(
            _liveEventApi,
            _logger,
            TimeSpan.FromSeconds(GameConstants.LiveEventPollIntervalS));
        _collectorTask = _eventCollector.StartAsync(_collectorCts.Token);

        // Tear down the OLD collector using the snapshotted locals (never the fields,
        // which now point at the new collector). Logger overload so failures surface.
        BackgroundTaskRunner.Run(
            async () => { await StopCollectorInstanceAsync(oldCts, oldCollector, oldTask).ConfigureAwait(false); },
            _logger,
            "stop previous live event collector");

        _logger.LogInformation("Live event collector task started");
    }

    /// <summary>
    /// Stops a specific collector instance via snapshotted local references, so the
    /// teardown of a previous collector can never touch the fields that now point at
    /// the current one.
    /// </summary>
    private static async Task StopCollectorInstanceAsync(
        CancellationTokenSource? cts,
        LiveEventCollector? collector,
        Task? task)
    {
        if (collector is null)
            return;

        if (cts is not null)
            await cts.CancelAsync().ConfigureAwait(false);

        if (task is not null)
        {
            try
            {
                await task.WaitAsync(
                    TimeSpan.FromSeconds(GameConstants.MonitorStopTimeoutS)).ConfigureAwait(false);
            }
            catch (TimeoutException) { }
            catch (OperationCanceledException) { }
        }

        await collector.StopAsync().ConfigureAwait(false);
        cts?.Dispose();
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
        // Capture in-game state BEFORE clearing it. If the LCU dropped while a
        // ranked/normal game was in progress, the post-game review would otherwise
        // never fire — so mark the game for reconcile when the client reconnects.
        var wasInGame = _state.GameInProgressNotified;

        _state.IsConnected = false;
        _state.ConnectedTicks = 0;

        // (Previously the collected events here were discarded silently.)
        await StopEventCollectorAsync().ConfigureAwait(false);

        if (wasInGame && !_state.CurrentGameIsCasual)
        {
            _logger.LogInformation(
                "LCU disconnected mid-game — marking reconcile pending so post-game review fires on reconnect");
            _state.ReconcilePending = true;
        }

        if (wasConnected)
        {
            _messenger.Send(new LcuConnectionChangedMessage(false));
        }
    }

    private static bool IsCasualQueue(int queueId) => CasualQueueIds.Contains(queueId);

    /// <summary>
    /// v2.17.25: true once the in-game Live Client Data API responds — i.e. the
    /// player is past the loading screen and actually in the game. During the
    /// loading screen the endpoint isn't up yet (connection refused / timeout),
    /// which surfaces as an exception we treat as "not ready".
    /// </summary>
    private async Task<bool> IsLiveGameDataAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _liveEventApi.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CoreDiagnostics.WriteVerbose($"LCU: live data availability check failed (loading?) {ex.GetType().Name}");
            return false;
        }
    }
}
