#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Revu.Core.Constants;
using Revu.Core.Models;
using Revu.Core.Services;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Lcu;

/// <summary>
/// Game-end durability: the end-of-game capture with its full retry budget, the per-game
/// live timeline store, hand-off to match-history reconciliation, and a collector that
/// outlives a League client drop. No finished game is dropped silently: it is captured,
/// recovered as the tracked game, or offered to the player.
/// </summary>
public sealed partial class GameMonitorService
{
    // A game's live timeline (and the lobby the live client reported) waits here, keyed
    // by game id, until a save consumes it: a capture that is still retrying, a capture
    // handed to reconciliation, or a collector finished while the League client was
    // away. Bounded; the oldest entry goes first.
    private const int MaxPendingEvidence = 4;
    private readonly Dictionary<long, PendingEvidence> _pendingEvidence = [];
    private readonly List<long> _pendingEvidenceOrder = [];

    private sealed record PendingEvidence(
        List<GameEvent> Events,
        Services.EventProcessing.ProcessingReport? Report,
        LiveRoster? Roster);

    // The end-of-game capture runs as tracked work with the whole EOG retry budget
    // (GameEndCaptureService tries 12 times, 2 s apart). The tick that sees the game end
    // waits briefly so the usual capture publishes on that tick; a slower one is
    // collected by a later tick, so the loop never stalls on it. Reconciliation waits
    // until the capture settles, so a game is never both captured and recovered.
    private Task<GameStats?>? _captureTask;
    private CancellationTokenSource? _captureCts;
    private long _captureTaskGameId;
    private Services.EventProcessing.ProcessingReport? _captureTaskReport;

    // The tracked game whose capture failed or whose end was missed. The post-game
    // reconcile saves it as a recovered game end; 0 when there is none or its id is unknown.
    private long _reconcileTargetGameId;

    // Games this session already handed to the save path. Match history can list a game
    // before its background save lands, so reconciliation never offers these again.
    private const int MaxRememberedGames = 16;
    private readonly HashSet<long> _publishedGameIds = [];
    private readonly Queue<long> _publishedGameOrder = new();

    // The collector reads the game (127.0.0.1:2999), not the League client, so it keeps
    // running while the client is away. It is finished once its game stops answering.
    private DateTime? _collectorSilentSinceUtc;
    // Set when a collector was finished without seeing its game end; if the game turns
    // out to be running after all, the next in-game tick starts a fresh one.
    private bool _collectorStoppedUnseen;

    /// <summary>How long the game-end tick waits for the capture before leaving it to later ticks.</summary>
    internal TimeSpan CaptureInlineWait { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>The capture deadline: the designed EOG retry schedule plus room for the requests themselves.</summary>
    internal TimeSpan CaptureRetryBudget { get; set; } = TimeSpan.FromSeconds(
        GameConstants.EogStatsRetryAttempts * GameConstants.EogStatsRetryDelayS + 20);

    /// <summary>How long a game may stay silent, while the client is away, before its collector is finished.</summary>
    internal TimeSpan OrphanedCollectorGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the tracked game is still looked for once the quick post-game retries missed it.</summary>
    internal TimeSpan TrackedGameSearchWindow { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Test seam: the clock the post-game reconcile backoff reads.</summary>
    internal Func<DateTime> UtcNow { get; set; } = static () => DateTime.UtcNow;

    private static readonly TimeSpan TrackedGameSlowRetry = TimeSpan.FromMinutes(5);
    private (long GameId, DateTime EndsUtc)? _trackedGameSearch;

    /// <summary>Test seam: the end-of-game capture still running, if any.</summary>
    internal Task? PendingCapture => _captureTask;

    /// <summary>
    /// The game just ended: stop the collector, keep its timeline under the game id, and
    /// start the end-of-game capture with its full retry budget. Waits up to
    /// <see cref="CaptureInlineWait"/> so a quick capture publishes on this tick; a slower
    /// one is collected by <see cref="SettleCaptureAsync"/> on a later tick.
    /// </summary>
    private async Task BeginGameEndCaptureAsync(CancellationToken cancellationToken)
    {
        // A capture still running from an earlier game end (not expected: the budget is far
        // shorter than any game) settles first so that game is not dropped.
        await SettleCaptureAsync(cancellationToken, waitForCompletion: true).ConfigureAwait(false);

        var (liveEvents, roster) = await StopEventCollectorAsync().ConfigureAwait(false);
        if (liveEvents.Count > 0)
        {
            _logger.LogInformation("Collected {Count} live events during game", liveEvents.Count);
        }
        var report = _processingReport;
        _processingReport = null;

        // The game id the collector read at game start; the post-game session still carries
        // it when that lookup failed, or when the monitor missed the game's start.
        var gameId = _captureGameId > 0
            ? _captureGameId
            : await TryGetCurrentGameIdAsync(cancellationToken).ConfigureAwait(false);
        // Used up: the next game reads its own id, so a game whose start the monitor
        // missed never takes this game's id (and with it this game's stored timeline).
        _captureGameId = 0;

        // The sidecar keeps this game's pre-game choices now, before the capture's retries
        // leave time for the next champ select to begin.
        _messenger.Send(new GameEndObservedMessage(gameId));

        if (gameId > 0)
        {
            // Never replaces a stored timeline with an empty one (a second game-end pass,
            // a collector finished while the client was away); the capture reads it back.
            var stored = StorePendingEvidence(gameId, liveEvents, report, roster);
            liveEvents = stored.Events;
            report = stored.Report;
            roster = stored.Roster;
        }

        // v3.10.1: hand the live roster to the capture so the lanes the game itself
        // assigned fill the matchup when the EOG payload carries none.
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(CaptureRetryBudget);
        _captureCts = budget;
        _captureTaskGameId = gameId;
        _captureTaskReport = report;
        _captureTask = CaptureWithinBudgetAsync(liveEvents, roster, budget.Token);

        try
        {
            await _captureTask.WaitAsync(CaptureInlineWait, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Still retrying, or already failed: SettleCaptureAsync reads the outcome.
        }
        await SettleCaptureAsync(cancellationToken, waitForCompletion: false).ConfigureAwait(false);
    }

    private async Task<GameStats?> CaptureWithinBudgetAsync(
        IReadOnlyList<GameEvent> liveEvents, LiveRoster? roster, CancellationToken budget)
    {
        // WaitAsync holds the deadline even for a capture that ignores its token.
        return await _gameEndCaptureService.CaptureAsync(liveEvents, roster, budget)
            .WaitAsync(budget).ConfigureAwait(false);
    }

    /// <summary>
    /// Publish a finished capture, or hand its game to match-history reconciliation when
    /// the capture failed (no stats within the budget, or an error). Waits for a running
    /// capture only when <paramref name="waitForCompletion"/> is set.
    /// </summary>
    private async Task SettleCaptureAsync(CancellationToken cancellationToken, bool waitForCompletion)
    {
        var task = _captureTask;
        if (task is null) return;
        if (!task.IsCompleted)
        {
            if (!waitForCompletion) return;
            try { await task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
            if (!task.IsCompleted) return;
        }

        var gameId = _captureTaskGameId;
        var report = _captureTaskReport;
        _captureTask = null;
        _captureTaskGameId = 0;
        _captureTaskReport = null;
        _captureCts?.Dispose();
        _captureCts = null;

        GameStats? stats = null;
        if (task.IsCompletedSuccessfully)
        {
            stats = task.Result;
        }
        else if (task.IsCanceled)
        {
            if (cancellationToken.IsCancellationRequested) return; // shutting down
            _logger.LogWarning("End-of-game stats did not arrive within {Seconds}s; the game goes to reconciliation",
                CaptureRetryBudget.TotalSeconds);
        }
        else
        {
            _logger.LogWarning(task.Exception?.GetBaseException(), "End-of-game capture failed; the game goes to reconciliation");
        }

        if (stats is null)
        {
            DeferToReconciliation(gameId);
            return;
        }

        stats.EventProcessingReport = report;
        TakePendingEvidence(stats.GameId, out _);
        PublishGameEnded(stats, isRecovered: false);
    }

    /// <summary>
    /// The tracked game will not be captured live: reconcile it from match history. Its
    /// timeline already waits in the evidence store, and the sidecar kept its pre-game
    /// choices when the game end was observed.
    /// </summary>
    private void DeferToReconciliation(long gameId)
    {
        _state.ReconcilePending = true;
        _state.PostGameReconcileRetriesRemaining = 0;
        _state.PostGameReconcileNextAttemptUtc = DateTime.MinValue;
        if (gameId <= 0)
        {
            _logger.LogWarning("The ended game's id is unknown; any unsaved game found later waits for the player on Home");
            return;
        }
        _reconcileTargetGameId = gameId;
        _messenger.Send(new GameCaptureDeferredMessage(gameId));
    }

    /// <summary>
    /// The quick post-game retries did not find the tracked game. Its timeline waits in the
    /// store, so match history is checked again every few minutes (while idle) for up to
    /// <see cref="TrackedGameSearchWindow"/>; a slow listing still ends in a saved game.
    /// False when there is no tracked game or its window is over.
    /// </summary>
    private bool KeepLookingForTrackedGame(DateTime now)
    {
        var gameId = _reconcileTargetGameId;
        if (gameId <= 0) return false;
        if (_trackedGameSearch?.GameId != gameId) _trackedGameSearch = (gameId, now + TrackedGameSearchWindow);
        if (now >= _trackedGameSearch.Value.EndsUtc) return false;
        // The next miss decrements this to zero and lands here again.
        _state.PostGameReconcileRetriesRemaining = 1;
        _state.PostGameReconcileNextAttemptUtc = now + TrackedGameSlowRetry;
        return true;
    }

    private void PublishGameEnded(GameStats stats, bool isRecovered)
    {
        RememberPublished(stats.GameId);
        if (stats.GameId > 0 && stats.GameId == _reconcileTargetGameId)
        {
            // The game the reconcile pass was looking for is on its way to the save path.
            _reconcileTargetGameId = 0;
            _state.ReconcilePending = false;
            _state.PostGameReconcileRetriesRemaining = 0;
            _state.PostGameReconcileNextAttemptUtc = DateTime.MinValue;
        }
        if (!isRecovered && stats.GameId > 0)
        {
            SetRecordingGameEnded(stats.GameId);
            ClearRecordingContext();
        }
        _messenger.Send(new GameEndedMessage(stats, isRecovered));
    }

    private void RememberPublished(long gameId)
    {
        if (gameId <= 0 || !_publishedGameIds.Add(gameId)) return;
        _publishedGameOrder.Enqueue(gameId);
        while (_publishedGameOrder.Count > MaxRememberedGames)
        {
            _publishedGameIds.Remove(_publishedGameOrder.Dequeue());
        }
    }

    private IReadOnlyList<MissedGameCandidate> WithoutPublished(IReadOnlyList<MissedGameCandidate> candidates) =>
        candidates.Any(c => _publishedGameIds.Contains(c.GameId))
            ? candidates.Where(c => !_publishedGameIds.Contains(c.GameId)).ToList()
            : candidates;

    /// <summary>
    /// Look for the just-finished game in match history. The tracked game (its capture
    /// failed or its end was missed) is saved at once as a recovered game end, carrying its
    /// live timeline, lobby and pre-game choices; any other unsaved game the pass finds
    /// waits for the player. With a tracked game, only finding it ends the retries, so an
    /// older unsaved game never stands in for the one just played.
    /// </summary>
    private async Task<bool> TryPublishPostGameReconcileAsync(CancellationToken cancellationToken)
    {
        CoreDiagnostics.WriteVerbose("LCU: TryPublishPostGameReconcile start");
        var candidates = WithoutPublished(await _matchHistoryReconciliationService
            .FindMissedGamesAsync(CheckGameSaved, cancellationToken)
            .ConfigureAwait(false));

        CoreDiagnostics.WriteVerbose($"LCU: TryPublishPostGameReconcile count={candidates.Count} target={_reconcileTargetGameId}");

        if (candidates.Count == 0)
            return false;

        var targetGameId = _reconcileTargetGameId;
        var tracked = targetGameId > 0 ? candidates.FirstOrDefault(c => c.GameId == targetGameId) : null;
        if (tracked is not null)
        {
            AttachPendingEvidence(tracked.Stats);
            _logger.LogInformation("Recovered the just-finished game {GameId} from match history", tracked.GameId);
            PublishGameEnded(tracked.Stats, isRecovered: true);
        }

        var others = tracked is null ? candidates : candidates.Where(c => c.GameId != tracked.GameId).ToList();
        if (others.Count > 0)
        {
            foreach (var candidate in others) AttachPendingEvidence(candidate.Stats);
            _messenger.Send(new MissedReviewsDetectedMessage(others, IsPostGameReconcile: true));
        }
        return targetGameId <= 0 || tracked is not null;
    }

    private async Task PublishMissedGamesAsync(CancellationToken cancellationToken, string reason)
    {
        // "postgame" reconcile fires right after InProgress → idle, meaning the user just finished
        // a game. Flag it so the shell can skip the selection dialog and go straight to post-game.
        var isPostGameReconcile = string.Equals(reason, "postgame", StringComparison.Ordinal);

        CoreDiagnostics.WriteVerbose($"LCU: PublishMissedGames start reason={reason} isPostGame={isPostGameReconcile}");
        var candidates = WithoutPublished(await _matchHistoryReconciliationService
            .FindMissedGamesAsync(CheckGameSaved, cancellationToken)
            .ConfigureAwait(false));

        CoreDiagnostics.WriteVerbose($"LCU: PublishMissedGames result reason={reason} count={candidates.Count}");

        if (candidates.Count > 0)
        {
            foreach (var candidate in candidates) AttachPendingEvidence(candidate.Stats);
            _messenger.Send(new MissedReviewsDetectedMessage(candidates, isPostGameReconcile));
        }
    }

    private void AttachPendingEvidence(GameStats stats)
    {
        if (!TakePendingEvidence(stats.GameId, out var captured)) return;
        stats.LiveEvents = captured.Events;
        stats.EventProcessingReport = captured.Report;
        ApplyRosterLanes(stats, captured.Roster);
    }

    /// <summary>
    /// Store a game's timeline, or merge it into the one already stored for that game:
    /// an empty list never replaces stored events, and a restarted collector's events
    /// join the earlier ones without duplicates.
    /// </summary>
    private PendingEvidence StorePendingEvidence(
        long gameId,
        List<GameEvent> events,
        Services.EventProcessing.ProcessingReport? report,
        LiveRoster? roster)
    {
        if (_pendingEvidence.TryGetValue(gameId, out var stored))
        {
            var merged = new PendingEvidence(
                MergeEvents(stored.Events, events),
                report ?? stored.Report,
                FullerRoster(roster, stored.Roster));
            _pendingEvidence[gameId] = merged;
            return merged;
        }

        while (_pendingEvidenceOrder.Count >= MaxPendingEvidence)
        {
            _pendingEvidence.Remove(_pendingEvidenceOrder[0]);
            _pendingEvidenceOrder.RemoveAt(0);
        }
        var fresh = new PendingEvidence(events, report, roster);
        _pendingEvidence[gameId] = fresh;
        _pendingEvidenceOrder.Add(gameId);
        return fresh;
    }

    private bool TakePendingEvidence(long gameId, out PendingEvidence evidence)
    {
        if (!_pendingEvidence.Remove(gameId, out evidence!)) return false;
        _pendingEvidenceOrder.Remove(gameId);
        return true;
    }

    internal static List<GameEvent> MergeEvents(List<GameEvent> stored, List<GameEvent> fresh)
    {
        if (fresh.Count == 0) return stored;
        if (stored.Count == 0) return fresh;
        var seen = new HashSet<(string, int, string)>();
        var merged = new List<GameEvent>(stored.Count + fresh.Count);
        foreach (var e in stored.Concat(fresh))
        {
            if (seen.Add((e.EventType, e.GameTimeS, e.Details))) merged.Add(e);
        }
        // Stable: same-second events keep their collection order.
        return [.. merged.OrderBy(static e => e.GameTimeS)];
    }

    private static LiveRoster? FullerRoster(LiveRoster? a, LiveRoster? b) =>
        a is null ? b : b is null ? a : a.Players.Count >= b.Players.Count ? a : b;

    private static readonly (string Position, string Suffix)[] RosterLanes =
        [("TOP", "Top"), ("JUNGLE", "Jg"), ("MIDDLE", "Mid"), ("BOTTOM", "Bot"), ("UTILITY", "Supp")];

    /// <summary>
    /// A recovered game's stats come from match history, which has a lane heuristic and no
    /// opponent. The lobby the live client reported for that game fills them: the player's
    /// lane, the role map and the lane opponent. Skipped unless the roster holds the
    /// played champion on its team (it then belongs to this game).
    /// </summary>
    internal static void ApplyRosterLanes(GameStats stats, LiveRoster? roster)
    {
        if (roster is null || !roster.HasPositions || stats.TeamId is not (100 or 200)) return;
        var position = roster.PositionOf(stats.ChampionName, stats.TeamId);
        if (position.Length == 0) return;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var player in roster.Players)
        {
            if (player.TeamId == 0) continue;
            var lane = Array.FindIndex(RosterLanes, l => l.Position == player.Position);
            if (lane < 0) continue;
            map[(player.TeamId == stats.TeamId ? "own" : "enemy") + RosterLanes[lane].Suffix] = player.ChampionName;
        }

        stats.Position = position;
        var mySuffix = RosterLanes[Array.FindIndex(RosterLanes, l => l.Position == position)].Suffix;
        if (map.TryGetValue("enemy" + mySuffix, out var opponent)) stats.EnemyLaner = opponent;
        if (map.Count > 0) stats.ParticipantMap = System.Text.Json.JsonSerializer.Serialize(map);
        stats.MatchupSource = MatchupSources.Live;
    }

    private async Task<long> TryGetCurrentGameIdAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lcuClient.GetCurrentGameIdAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static bool IsOutOfGamePhase(GamePhase phase) =>
        phase is GamePhase.None or GamePhase.Lobby or GamePhase.Matchmaking or GamePhase.ReadyCheck or GamePhase.ChampSelect;

    /// <summary>
    /// Connected and outside a game, yet the collector still runs: the game ended while the
    /// client was away (or the client skipped the post-game phases). A game that still
    /// answers keeps its collector; otherwise its timeline goes to reconciliation.
    /// </summary>
    private async Task FinishUnobservedGameAsync(CancellationToken cancellationToken)
    {
        if (await IsLiveGameDataAvailableAsync(cancellationToken).ConfigureAwait(false)) return;
        _logger.LogInformation("The game ended without a post-game phase; keeping its live timeline for reconciliation");
        await FinishCollectorWithoutCaptureAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The League client is unreachable. The collector keeps recording while its game still
    /// answers; it is finished once the collector gave up on its own or the game has been
    /// silent for <see cref="OrphanedCollectorGrace"/>, so it never polls a closed game forever.
    /// </summary>
    private async Task FinishOrphanedCollectorAsync(CancellationToken cancellationToken)
    {
        if (_eventCollector is null)
        {
            _collectorSilentSinceUtc = null;
            return;
        }

        if (_collectorTask is not { IsCompleted: true })
        {
            if (await IsLiveGameDataAvailableAsync(cancellationToken).ConfigureAwait(false))
            {
                _collectorSilentSinceUtc = null;
                return;
            }
            var now = DateTime.UtcNow;
            _collectorSilentSinceUtc ??= now;
            // A collector still waiting for its game (no player yet) gives up by itself.
            if (_eventCollector.PlayerName is null || now - _collectorSilentSinceUtc < OrphanedCollectorGrace) return;
        }

        _logger.LogInformation("The game ended while the League client was away; keeping its live timeline for reconciliation");
        await FinishCollectorWithoutCaptureAsync().ConfigureAwait(false);
    }

    private async Task FinishCollectorWithoutCaptureAsync()
    {
        var (events, roster) = await StopEventCollectorAsync().ConfigureAwait(false);
        var report = _processingReport;
        _processingReport = null;
        _collectorSilentSinceUtc = null;
        _collectorStoppedUnseen = true;
        // The game is over as far as the monitor can tell: re-arm the one-shots.
        _state.GameInProgressNotified = false;
        _state.GameStartedNotified = false;
        var gameId = _captureGameId;
        _captureGameId = 0;
        if (_state.CurrentGameIsCasual) return;

        if (gameId > 0)
        {
            StorePendingEvidence(gameId, events, report, roster);
        }
        else if (events.Count > 0)
        {
            _logger.LogWarning("Live events of a game with an unknown id cannot be matched to it later ({Count} skipped)", events.Count);
        }
        _messenger.Send(new GameEndObservedMessage(gameId));
        DeferToReconciliation(gameId);
    }
}
