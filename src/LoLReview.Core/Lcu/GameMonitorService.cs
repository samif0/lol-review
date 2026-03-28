#nullable enable

using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using LoLReview.Core.Constants;
using LoLReview.Core.Data.Repositories;
using LoLReview.Core.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Lcu;

/// <summary>
/// Background service that monitors the League client for game phase transitions.
/// Ported from Python monitor.py GameMonitor class.
///
/// Uses a 5-second polling interval to detect phase transitions and sends messages via IMessenger:
/// - <see cref="ChampSelectStartedMessage"/> when champ select begins (non-casual only)
/// - <see cref="GameStartedMessage"/> when game enters loading/in-progress
/// - <see cref="GameEndedMessage"/> when end-of-game stats are extracted
/// - <see cref="LcuConnectionChangedMessage"/> when connection state changes
/// </summary>
public sealed class GameMonitorService : BackgroundService, IGameMonitorService
{
    private readonly ILcuCredentialDiscovery _credentialDiscovery;
    private readonly ILcuClient _lcuClient;
    private readonly ILiveEventApi _liveEventApi;
    private readonly IGameRepository _gameRepository;
    private readonly IMessenger _messenger;
    private readonly ILogger<GameMonitorService> _logger;

    private GamePhase _lastPhase = GamePhase.None;
    private bool _connected;
    private bool _currentGameCasual;
    private LiveEventCollector? _eventCollector;
    private CancellationTokenSource? _collectorCts;
    private Task? _collectorTask;
    private bool _reconcilePending;
    private int _credFailCount;
    private const int MaxCredBackoff = 6; // Skip up to 6 ticks (~30s at 5s intervals)

    /// <inheritdoc />
    public Func<long, bool>? CheckGameSaved { get; set; }

    /// <summary>Casual (non-ranked, non-normal) queue IDs.</summary>
    private static readonly HashSet<int> CasualQueueIds =
    [
        450,   // ARAM
        1700,  // Arena (Cherry)
        1900,  // URF
        900,   // ARURF
        1010,  // Snow ARURF
        1020,  // One for All
        2070,  // ARAM Mayhem (Kiwi)
        2000,  // Tutorial 1
        2010,  // Tutorial 2
        2020,  // Tutorial 3
        0,     // Custom / Practice Tool
    ];

    public GameMonitorService(
        ILcuCredentialDiscovery credentialDiscovery,
        ILcuClient lcuClient,
        ILiveEventApi liveEventApi,
        IGameRepository gameRepository,
        IMessenger messenger,
        ILogger<GameMonitorService> logger)
    {
        _credentialDiscovery = credentialDiscovery;
        _lcuClient = lcuClient;
        _liveEventApi = liveEventApi;
        _gameRepository = gameRepository;
        _messenger = messenger;
        _logger = logger;
    }

    /// <summary>
    /// Main background loop — polls every 5 seconds.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Game monitor started");

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(GameConstants.GameMonitorPollIntervalS));

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
            }
        }

        _logger.LogInformation("Game monitor stopped");
    }

    /// <summary>
    /// Single monitoring cycle: ensure connection + check phase transitions.
    /// </summary>
    private async Task TickAsync(CancellationToken ct)
    {
        // ── Ensure we have a connected LCU client ───────────────────────

        if (!_connected)
        {
            // Backoff: skip ticks when repeatedly failing to find credentials
            if (_credFailCount > 0)
            {
                _credFailCount--;
                return;
            }

            var creds = _credentialDiscovery.FindCredentials();
            if (creds is null)
            {
                _credFailCount = Math.Min(_credFailCount + 2, MaxCredBackoff);

                if (_connected)
                {
                    _connected = false;
                    _logger.LogInformation("League client disconnected -- credentials not found");
                    await StopEventCollectorAsync().ConfigureAwait(false);
                    _messenger.Send(new LcuConnectionChangedMessage(false));
                }

                return;
            }

            _credFailCount = 0;
            _lcuClient.Configure(creds);

            if (await _lcuClient.IsConnectedAsync(ct).ConfigureAwait(false))
            {
                _connected = true;
                _logger.LogInformation("Connected to League client");
                _messenger.Send(new LcuConnectionChangedMessage(true));
            }
            else
            {
                return;
            }
        }

        // ── Check current gameflow phase ────────────────────────────────

        GamePhase phase;
        try
        {
            phase = await _lcuClient.GetGameflowPhaseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Client might have closed
            _logger.LogWarning(ex, "League client connection lost");
            _connected = false;
            await StopEventCollectorAsync().ConfigureAwait(false);
            _messenger.Send(new LcuConnectionChangedMessage(false));
            return;
        }

        // ── Detect transition into ChampSelect ──────────────────────────

        if (phase == GamePhase.ChampSelect && _lastPhase != GamePhase.ChampSelect)
        {
            var queueId = await _lcuClient.GetLobbyQueueIdAsync(ct).ConfigureAwait(false);
            _currentGameCasual = IsCasualQueue(queueId);
            var modeLabel = _currentGameCasual ? "casual" : "ranked/normal";
            _logger.LogInformation("Champ select started (queue {QueueId} -- {Mode})", queueId, modeLabel);

            // Always send champ select message (even for casual/practice) for testing
            _messenger.Send(new ChampSelectStartedMessage(queueId));
        }

        // ── Detect champ select cancelled (dodge/leave) ──────────────────

        if (_lastPhase == GamePhase.ChampSelect
            && phase is not GamePhase.ChampSelect
                and not GamePhase.InProgress
                and not GamePhase.GameStart)
        {
            _logger.LogInformation("Champ select cancelled (went to {Phase})", phase);
            _messenger.Send(new ChampSelectCancelledMessage());
        }

        // ── Detect transition into InProgress/GameStart ─────────────────

        if (phase is GamePhase.InProgress or GamePhase.GameStart
            && _lastPhase is not GamePhase.InProgress and not GamePhase.GameStart)
        {
            _logger.LogInformation("Game loading -- closing pre-game window");
            _messenger.Send(new GameStartedMessage());

            if (!_currentGameCasual)
            {
                StartEventCollector();
            }
        }

        // ── Detect transition into EndOfGame ────────────────────────────

        if (phase == GamePhase.EndOfGame && _lastPhase != GamePhase.EndOfGame)
        {
            if (_currentGameCasual)
            {
                _logger.LogInformation("Casual game ended -- skipping");
            }
            else
            {
                _logger.LogInformation("Game ended -- fetching stats");
                await HandleGameEndAsync(ct).ConfigureAwait(false);
                _reconcilePending = true;
            }

            _currentGameCasual = false;
        }

        // ── Reconcile match history when returning to lobby ─────────────

        if (_lastPhase is GamePhase.EndOfGame or GamePhase.InProgress
                or GamePhase.GameStart or GamePhase.WaitingForStats
            && phase is GamePhase.Lobby or GamePhase.None
                or GamePhase.ReadyCheck or GamePhase.ChampSelect)
        {
            if (_reconcilePending || _lastPhase == GamePhase.InProgress)
            {
                await ReconcileMatchHistoryAsync(ct).ConfigureAwait(false);
                _reconcilePending = false;
            }
        }

        _lastPhase = phase;
    }

    // ── Event collector management ──────────────────────────────────────

    private void StartEventCollector()
    {
        // Clean up any previous collector first
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
            // Signal cancellation first
            if (_collectorCts is not null)
            {
                await _collectorCts.CancelAsync().ConfigureAwait(false);
            }

            // Wait for the collector task to finish
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
                    // Expected
                }
            }

            // Get the final events
            events = await _eventCollector.StopAsync().ConfigureAwait(false);
            _eventCollector = null;
        }

        _collectorCts?.Dispose();
        _collectorCts = null;
        _collectorTask = null;

        return events;
    }

    // ── Game end handling ────────────────────────────────────────────────

    private async Task HandleGameEndAsync(CancellationToken ct)
    {
        // Stop the live event collector and grab events
        var liveEvents = await StopEventCollectorAsync().ConfigureAwait(false);
        if (liveEvents.Count > 0)
        {
            _logger.LogInformation("Collected {Count} live events during game", liveEvents.Count);
        }

        // The EOG data might take a moment to be ready -- retry a few times
        for (var attempt = 0; attempt < GameConstants.EogStatsRetryAttempts; attempt++)
        {
            var eogData = await _lcuClient.GetEndOfGameStatsAsync(ct).ConfigureAwait(false);
            if (eogData is JsonElement eog)
            {
                var stats = StatsExtractor.ExtractFromEog(eog, _logger);
                if (stats is not null)
                {
                    var summonerName = await TryGetCurrentSummonerNameAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(summonerName))
                    {
                        stats.SummonerName = summonerName;
                    }

                    // Attach live events
                    stats.LiveEvents = liveEvents;

                    _messenger.Send(new GameEndedMessage(stats));
                    return;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        _logger.LogWarning("Could not retrieve end-of-game stats after retries");
        // Mark for reconciliation since we failed to get EOG stats
        _reconcilePending = true;
    }

    // ── Match history reconciliation ────────────────────────────────────

    private async Task ReconcileMatchHistoryAsync(CancellationToken ct)
    {
        List<JsonElement> matches;
        try
        {
            matches = await _lcuClient.GetMatchHistoryAsync(begin: 0, count: 5, ct: ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reconciliation: failed to fetch match history");
            return;
        }

        if (matches.Count == 0)
            return;

        var summonerName = await TryGetCurrentSummonerNameAsync(ct).ConfigureAwait(false);
        var backfilled = 0;
        foreach (var game in matches)
        {
            var gameId = game.GetPropertyLongOrDefault("gameId", 0);
            if (gameId == 0)
                continue;

            // Already saved?
            var alreadySaved = CheckGameSaved is not null
                ? CheckGameSaved(gameId)
                : await _gameRepository.GetAsync(gameId).ConfigureAwait(false) is not null;

            if (alreadySaved)
                continue;

            var stats = StatsExtractor.ExtractFromMatchHistory(game, _logger);
            if (stats is null)
                continue;

            if (!string.IsNullOrEmpty(summonerName))
            {
                stats.SummonerName = summonerName;
            }

            _logger.LogInformation(
                "Reconciliation: backfilling missed game {GameId} ({Champion} {Result})",
                gameId, stats.ChampionName, stats.Win ? "W" : "L");

            _messenger.Send(new GameEndedMessage(stats));
            backfilled++;
        }

        if (backfilled > 0)
        {
            _logger.LogInformation("Reconciliation: backfilled {Count} missed game(s)", backfilled);
        }
        else
        {
            _logger.LogDebug("Reconciliation: no missed games found");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task<string?> TryGetCurrentSummonerNameAsync(CancellationToken ct)
    {
        try
        {
            var summoner = await _lcuClient.GetCurrentSummonerAsync(ct).ConfigureAwait(false);
            if (summoner is not JsonElement summonerEl)
            {
                return null;
            }

            return summonerEl.GetPropertyOrDefault("displayName", "") is { Length: > 0 } displayName
                ? displayName
                : summonerEl.GetPropertyOrDefault("gameName", "Unknown");
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCasualQueue(int queueId) => CasualQueueIds.Contains(queueId);
}
