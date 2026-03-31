#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using LoLReview.Core.Constants;
using LoLReview.Core.Models;
using LoLReview.Core.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

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

    /// <summary>Casual (non-ranked, non-normal) queue IDs.</summary>
    private static readonly HashSet<int> CasualQueueIds =
    [
        450,
        1700,
        1900,
        900,
        1010,
        1020,
        2070,
        2000,
        2010,
        2020,
        0,
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

        if (phase != _state.LastPhase)
        {
            _logger.LogInformation("Gameflow phase changed {PreviousPhase} -> {CurrentPhase}", _state.LastPhase, phase);
            CoreDiagnostics.WriteVerbose($"LCU: Phase changed {_state.LastPhase} -> {phase}");
        }

        var plan = _transitionEvaluator.Evaluate(_state, phase);

        if (plan.ReconcileOnStartup)
        {
            await PublishMissedGamesAsync(cancellationToken).ConfigureAwait(false);
            _state.StartupReconcilePending = false;
        }

        if (plan.NotifyChampSelectStarted)
        {
            await HandleChampSelectStartedAsync(cancellationToken).ConfigureAwait(false);
        }

        if (plan.NotifyChampSelectCancelled)
        {
            _logger.LogInformation("Champ select cancelled (went to {Phase})", phase);
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
            await PublishMissedGamesAsync(cancellationToken).ConfigureAwait(false);
            _state.ReconcilePending = false;
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
        _messenger.Send(new ChampSelectStartedMessage(queueId));
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

    private async Task PublishMissedGamesAsync(CancellationToken cancellationToken)
    {
        var candidates = await _matchHistoryReconciliationService
            .FindMissedGamesAsync(CheckGameSaved, cancellationToken)
            .ConfigureAwait(false);

        if (candidates.Count > 0)
        {
            _messenger.Send(new MissedReviewsDetectedMessage(candidates));
        }
    }

    private async Task HandleDisconnectedAsync()
    {
        var wasConnected = _state.IsConnected;
        _state.IsConnected = false;
        await StopEventCollectorAsync().ConfigureAwait(false);

        if (wasConnected)
        {
            _messenger.Send(new LcuConnectionChangedMessage(false));
        }
    }

    private static bool IsCasualQueue(int queueId) => CasualQueueIds.Contains(queueId);
}
