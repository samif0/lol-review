#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Revu.Core.Lcu;
using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// The sidecar's stand-in for the WinUI <c>ShellViewModel</c> message handling.
///
/// <para>
/// The hosted <see cref="GameMonitorService"/> polls the LCU and fires the same
/// IMessenger messages the desktop app's shell consumed (ChampSelectStarted /
/// Updated / Cancelled, GameStarted, GameInProgress, GameEnded, MissedReviews,
/// LcuConnectionChanged). In the WinUI app the ShellViewModel turned those into
/// (a) navigation and (b) the END-OF-GAME persistence hop. The webview replaces
/// the navigation half (over SSE) but the persistence half MUST still happen
/// somewhere or live-captured games stop recording — that is THIS class.
/// </para>
///
/// <para>
/// Two jobs, both critical:
///   1. <b>SSE fan-out</b>: every message → a {type,payload} event on the
///      <see cref="SidecarEventHub"/> for GET /api/events, plus a refresh of the
///      shared <see cref="LcuLiveState"/> so a late-connecting webview replays the
///      current champ-select / in-progress state.
///   2. <b>End-of-game write</b>: on <see cref="GameEndedMessage"/> it runs the
///      SAME <see cref="IGameLifecycleWorkflowService.ProcessGameEndAsync"/> the
///      WinUI app ran — saving the game row, session log, derived events, VOD
///      match — then promotes the champ-select prompt drafts (PromotePreGameDrafts)
///      using the deferred pre-game snapshots (mood / intent / practiced ids)
///      the frontend POSTed during champ select. All against the WRITE graph.
/// </para>
///
/// <para>
/// It also supplies <see cref="GameMonitorService.CheckGameSaved"/> so match-
/// history reconciliation skips games already in the DB (mirrors the WinUI wiring).
/// </para>
/// </summary>
public sealed class SidecarGameFlowCoordinator : IHostedService,
    IRecipient<ChampSelectStartedMessage>,
    IRecipient<ChampSelectUpdatedMessage>,
    IRecipient<ChampSelectCancelledMessage>,
    IRecipient<GameStartedMessage>,
    IRecipient<GameInProgressMessage>,
    IRecipient<GameEndedMessage>,
    IRecipient<MissedReviewsDetectedMessage>,
    IRecipient<LcuConnectionChangedMessage>,
    IRecipient<QueueDetectedMessage>
{
    private readonly IMessenger _messenger;
    private readonly SidecarEventHub _eventHub;
    private readonly LcuLiveState _liveState;
    private readonly GameMonitorService _gameMonitor;
    private readonly WriteServices _write;
    // v3.7: the hard stop (queue cancel while an enforced rule holds).
    private readonly HardStopEnforcer _hardStop;
    private readonly ILogger<SidecarGameFlowCoordinator> _logger;

    public SidecarGameFlowCoordinator(
        IMessenger messenger,
        SidecarEventHub eventHub,
        LcuLiveState liveState,
        GameMonitorService gameMonitor,
        WriteServices write,
        HardStopEnforcer hardStop,
        ILogger<SidecarGameFlowCoordinator> logger)
    {
        _messenger = messenger;
        _eventHub = eventHub;
        _liveState = liveState;
        _gameMonitor = gameMonitor;
        _write = write;
        _hardStop = hardStop;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Reconciliation dedupe: skip a match-history candidate already in the DB.
        // GetAsync is a quick keyed read; the monitor calls this synchronously, so
        // we block on the async read (same contract as the WinUI Func<long,bool>).
        _gameMonitor.CheckGameSaved = gameId =>
        {
            try
            {
                var game = _write.Games.GetAsync(gameId).GetAwaiter().GetResult();
                return game is not null;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CheckGameSaved probe failed for {GameId}", gameId);
                return false;
            }
        };

        _messenger.RegisterAll(this);
        _logger.LogInformation("Sidecar game-flow coordinator registered (LCU → SSE + EOG persistence)");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _messenger.UnregisterAll(this);
        return Task.CompletedTask;
    }

    // ── Champ select ─────────────────────────────────────────────────────────

    public void Receive(ChampSelectStartedMessage m)
    {
        var sessionKey = _liveState.BeginChampSelect(m.MyChampion, m.EnemyLaner, m.MyPosition, m.ParticipantMapJson);
        _eventHub.Publish("champSelectStarted", new
        {
            queueId = m.QueueId,
            myChampion = m.MyChampion,
            enemyLaner = m.EnemyLaner,
            myPosition = m.MyPosition,
            participantMapJson = m.ParticipantMapJson,
            sessionKey,
        });
        _logger.LogInformation("LCU champ select started (queue {QueueId}) → SSE", m.QueueId);
    }

    public void Receive(ChampSelectUpdatedMessage m)
    {
        _liveState.UpdateChampSelect(m.MyChampion, m.EnemyLaner, m.MyPosition, m.ParticipantMapJson);
        _eventHub.Publish("champSelectUpdated", new
        {
            myChampion = m.MyChampion,
            enemyLaner = m.EnemyLaner,
            myPosition = m.MyPosition,
            participantMapJson = m.ParticipantMapJson,
            sessionKey = _liveState.SessionKey,
        });
    }

    public void Receive(ChampSelectCancelledMessage m)
    {
        _liveState.CancelChampSelect();
        _eventHub.Publish("champSelectCancelled", new { });
        _logger.LogInformation("LCU champ select cancelled → SSE");
    }

    // ── Game lifecycle ───────────────────────────────────────────────────────

    public void Receive(GameStartedMessage m)
    {
        // A game is loading: whatever lock was showing is over for this game
        // (the player overrode it, or the rule stopped holding). Don't replay it.
        _liveState.SetHardStop(null);
        _eventHub.Publish("gameStarted", new { });
    }

    // ── Hard stop (v3.7) ──────────────────────────────────────────────────────

    public void Receive(QueueDetectedMessage m)
    {
        // Off the messenger thread: the decision reads rules + today's games and
        // may call the LCU. The enforcer debounces + serializes itself, so the
        // per-tick re-sends while the phase holds are cheap when nothing trips.
        var phase = m.Phase;
        _ = Task.Run(() => _hardStop.HandleQueueAsync(phase));
    }

    public void Receive(GameInProgressMessage m)
    {
        _liveState.SetGameInProgress(true);
        _eventHub.Publish("gameInProgress", new { });
        _logger.LogInformation("LCU game in progress → SSE (close pre-game, open in-game)");
    }

    public void Receive(GameEndedMessage m)
    {
        // The capture already happened (GameMonitorService → GameEndCaptureService
        // produced the stats). This is the WRITE hop that persists the game — the
        // single most important piece of live capture. Run it off the message
        // thread so a slow DB write never stalls the monitor's tick loop.
        var stats = m.Stats;
        var isRecovered = m.IsRecovered;
        _ = Task.Run(() => PersistGameEndAsync(stats, isRecovered));
    }

    private async Task PersistGameEndAsync(Revu.Core.Models.GameStats stats, bool isRecovered)
    {
        try
        {
            // Take a safety backup before the first write of the session (same
            // guard the manual write endpoints use).
            await _write.BackupGuard.EnsureBackedUpAsync().ConfigureAwait(false);

            // Read + clear the deferred pre-game snapshots (mood / intent /
            // practiced ids / session key). Recovered games skip these entirely —
            // a stale champ select would mislabel the wrong game (mirror Shell).
            var (mood, intention, intentionSource, _, practicedIds, sessionKey, champSelectPosition, champSelectMap) =
                _liveState.TakeForGameEnd();

            // v3.10.1: the matchup goes on the row NOW. The capture already tried the
            // EOG payload and the live roster; what is still blank gets the champ-
            // select snapshot (this lobby only), then a role-prior estimate — both
            // marked as estimates so the Match-V5 pass below confirms them.
            ApplyMatchupFallbacks(stats, isRecovered, sessionKey, champSelectPosition, champSelectMap);

            ProcessGameEndRequest request = isRecovered
                ? new ProcessGameEndRequest(stats, MentalRating: 5, PreGameMood: 0)
                : new ProcessGameEndRequest(
                    stats,
                    MentalRating: 5,
                    PreGameMood: mood,
                    PreGamePracticedObjectiveIds: practicedIds.Count > 0 ? practicedIds : null,
                    PregameIntention: intention,
                    IntentionSource: intentionSource);

            var result = await _write.GameLifecycle
                .ProcessGameEndAsync(request, isRecovered)
                .ConfigureAwait(false);

            if (result.WasSaved && result.GameId is long gameId)
            {
                // Promote champ-select draft prompt answers onto the real game row
                // (idempotent upsert) — only for a non-recovered live flow that had
                // a session key (mirror ShellViewModel.PromotePreGameDraftsAsync).
                if (!isRecovered && !string.IsNullOrEmpty(sessionKey))
                {
                    try
                    {
                        await _write.Prompts.PromotePreGameDraftsAsync(sessionKey, gameId).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Pre-game draft promotion failed for game {GameId}", gameId);
                    }
                }

                // v3.5: produce this game's pattern evidence (inferred regions,
                // gank deaths, review-signal anchors) while the events are fresh
                // — the write that keeps the Patterns page fed. Best-effort: a
                // failure leaves the game unstamped for the startup backfill.
                try
                {
                    await _write.PatternMaterializer.MaterializeForGameAsync(gameId).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Pattern-evidence materialization failed for game {GameId}", gameId);
                }

                _logger.LogInformation("Live game captured + saved: game {GameId} ({Champ})", gameId, stats.ChampionName);
                _eventHub.Publish("gameEnded", new
                {
                    gameId,
                    championName = stats.ChampionName,
                    win = stats.Win,
                    enemyLaner = stats.EnemyLaner,
                    saved = true,
                    isRecovered,
                });

                // P-022: link THIS game's Ascent recording as soon as it's available,
                // so the freshly-played game's VOD shows without a manual Settings scan.
                // The recording often finalises ~15s after EOG (P-007), so retry a few
                // times; TryLinkRecordingAsync is idempotent (returns true and no-ops if
                // already linked) and bounded (matches only against recordings spanning
                // the game window). Fully best-effort — a miss is healed by the startup
                // auto-match on next launch. Fire-and-forget so it never delays the save.
                stats.GameId = gameId; // the persisted id; the matcher keys off it
                _ = Task.Run(() => TryLinkRecordingWithRetryAsync(stats));

                // v3.2: derive the just-ended game's map-state (jungle proximity +
                // fog deaths) automatically, so the timeline markers appear without
                // a manual Settings backfill. Fire-and-forget like the VOD link.
                _ = Task.Run(() => TryMapStateWithRetryAsync(gameId));

                // v3.10: heal the matchup (enemy laner + role→champion map) from
                // Match-V5 when the LCU end-of-game payload left it blank, so the
                // Review page and the Matchups journal show "you vs them" without
                // the manual Settings backfill. Fire-and-forget like the others.
                _ = Task.Run(() => TryMatchupWithRetryAsync(gameId));
            }
            else
            {
                // Skipped (casual/remake the workflow declined to persist).
                _logger.LogInformation("Live game end processed but not saved (skipped/casual)");
                _eventHub.Publish("gameEnded", new
                {
                    gameId = (long?)null,
                    championName = stats.ChampionName,
                    win = stats.Win,
                    saved = false,
                    isRecovered,
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist live game end");
            _eventHub.Publish("gameEnded", new { saved = false, error = ex.Message });
        }
    }

    // P-022: try to link the just-ended game's Ascent recording, retrying to absorb
    // encode-finalisation lag (the recording's last-write time often lands ~15s after
    // EOG — P-007 — so an immediate attempt can miss the file). Attempts at roughly
    // +0s / +90s / +5min; stops as soon as a link succeeds (or the game already has
    // one — TryLinkRecordingAsync is idempotent). Best-effort: every failure is
    // swallowed, and the startup auto-match catches anything still unlinked next launch.
    private async Task TryLinkRecordingWithRetryAsync(Revu.Core.Models.GameStats game)
    {
        var delaysSeconds = new[] { 0, 90, 300 };
        foreach (var delay in delaysSeconds)
        {
            if (delay > 0)
                await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            try
            {
                if (await _write.VodScan.TryLinkRecordingAsync(game).ConfigureAwait(false))
                {
                    _logger.LogInformation("Auto-linked recording to game {GameId} after EOG", game.GameId);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-game VOD link attempt failed for game {GameId} (will retry/heal)", game.GameId);
            }
        }
    }

    // v3.2: run the map-state pass for the just-ended game. Riot's Match-V5 data
    // usually becomes fetchable ~1–2 minutes after EOG, so attempts at +90s / +5min
    // (the run is keyed on games.map_state_v, so a still-unavailable match simply
    // stays queued and the retry — or the next manual backfill — heals it).
    // maxGames is small on purpose: newest-first ordering means the fresh game is
    // first in the review-queue-scoped missing set; deep drains stay on the
    // Settings button. Skips quietly when signed out — the proxy would reject
    // anonymous calls, so attempting would just burn throttled 401s.
    private async Task TryMapStateWithRetryAsync(long gameId)
    {
        var delaysSeconds = new[] { 90, 300 };
        foreach (var delay in delaysSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            try
            {
                if (!_write.Config.HasValidRiotSession)
                {
                    _logger.LogDebug("Post-game map-state skipped for game {GameId}: no Riot session", gameId);
                    return;
                }

                var result = await _write.MapStateBackfill.RunAsync(maxGames: 5).ConfigureAwait(false);
                if (result.Scanned == 0 || result.Failed == 0)
                {
                    // Nothing queued (already processed) or everything queued landed —
                    // either way the fresh game is done.
                    _logger.LogInformation(
                        "Post-game map-state pass done ({Updated} updated, {Skipped} empty) after game {GameId}",
                        result.Updated, result.Skipped, gameId);
                    // Tell open pages the markers exist now — a VOD player already
                    // showing this game soft-refreshes its timeline (the pass lands
                    // ~90s after EOG, inside the window where the user may have the
                    // VOD open already). Fresh navigations always fetch fresh.
                    if (result.Updated > 0)
                        _eventHub.Publish("mapStateUpdated", new { gameId, updated = result.Updated });
                    return;
                }
                // Some fetch failed — most likely the fresh match isn't visible
                // upstream yet. Fall through to the next, longer delay.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-game map-state attempt failed for game {GameId} (will retry/heal)", gameId);
            }
        }
    }

    // v3.10.1: fill whatever the capture left blank from the sources the sidecar
    // holds (MatchupFallback.ApplyForGameEnd): nothing for a recovered game; the
    // champ-select snapshot only for the flow that had a session key, and only when
    // its own side holds the played champion, so a stale lobby can never label this
    // game; then the role-prior estimate over the EOG champion lists. The snapshot
    // comes from TakeForGameEnd, which clears it. Best-effort: a failure leaves the
    // row as captured and the Match-V5 pass fills it.
    private void ApplyMatchupFallbacks(
        Revu.Core.Models.GameStats stats,
        bool isRecovered,
        string? sessionKey,
        string champSelectPosition,
        string champSelectMap)
    {
        try
        {
            if (MatchupFallback.ApplyForGameEnd(stats, isRecovered, sessionKey, champSelectPosition, champSelectMap))
            {
                _logger.LogInformation(
                    "Matchup for game {GameId} estimated at game end ({Source}): {Position}, {Champion} vs {Enemy} (Match-V5 confirms shortly)",
                    stats.GameId, stats.MatchupSource, stats.Position, stats.ChampionName, stats.EnemyLaner);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Matchup fallback failed for game {GameId}; leaving the captured columns", stats.GameId);
        }
    }

    // v3.10 / v3.10.1: confirm the just-ended game's matchup from Match-V5. The row
    // already shows what game end resolved (live roster / EOG / champ select / role
    // priors); this pass is the authoritative check — Riot's teamPosition overwrites
    // an estimate and confirms a live-roster read. Match-V5 needs ~1–2 minutes after
    // EOG, so attempts at +90s / +5min, keyed on THIS game (a single lookup, no
    // throttle); a success also drains a few older rows still waiting. Publishes
    // matchupUpdated when the row changed so an open Review page or the Matchups
    // journal refreshes its header instead of waiting for a reload.
    private async Task TryMatchupWithRetryAsync(long gameId)
    {
        var delaysSeconds = new[] { 90, 300 };
        foreach (var delay in delaysSeconds)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            try
            {
                if (!_write.Config.HasValidRiotSession)
                {
                    _logger.LogDebug("Post-game matchup pass skipped for game {GameId}: no Riot session", gameId);
                    return;
                }

                var row = await _write.Games.GetAsync(gameId).ConfigureAwait(false);
                if (row is null || row.MatchupSource is MatchupSources.MatchV5 or MatchupSources.User)
                {
                    return; // gone, confirmed by an earlier attempt, or the player's own word
                }

                var outcome = await _write.EnemyLanerBackfill.BackfillGameAsync(gameId).ConfigureAwait(false);
                switch (outcome)
                {
                    case EnemyLanerBackfillOutcome.Updated:
                        _eventHub.Publish("matchupUpdated", new { gameId, updated = 1 });
                        _logger.LogInformation(
                            "Post-game matchup confirmed from Match-V5 for game {GameId} (game end had '{Source}')",
                            gameId, row.MatchupSource);
                        await DrainMatchupBacklogAsync(gameId).ConfigureAwait(false);
                        return;
                    case EnemyLanerBackfillOutcome.Skipped:
                        // Upstream has the match but no positions (a non-positional
                        // queue) — nothing more to learn; keep what game end wrote.
                        _logger.LogDebug("Post-game matchup pass: nothing to confirm upstream for game {GameId}", gameId);
                        return;
                    case EnemyLanerBackfillOutcome.NotConfigured:
                        return;
                    default:
                        // Failed: the fresh match isn't visible upstream yet — fall
                        // through to the longer delay (the startup heal covers the rest).
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Post-game matchup attempt failed for game {GameId} (will retry/heal)", gameId);
            }
        }
    }

    // A few older rows that never got their pass (the app was closed within five
    // minutes of a game, a version without the pass). Small maxGames keeps the deep
    // drain on the Settings button; RunAsync throttles itself.
    private async Task DrainMatchupBacklogAsync(long freshGameId)
    {
        try
        {
            var pending = await _write.Games.GetGameIdsMissingEnemyLanerAsync().ConfigureAwait(false);
            if (pending.Count == 0) return;
            var result = await _write.EnemyLanerBackfill.RunAsync(maxGames: 5).ConfigureAwait(false);
            if (result.Updated > 0)
            {
                _eventHub.Publish("matchupUpdated", new { gameId = (long?)null, updated = result.Updated });
                _logger.LogInformation(
                    "Matchup backlog: {Updated} older game(s) confirmed after game {GameId} ({Failed} not yet available)",
                    result.Updated, freshGameId, result.Failed);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Matchup backlog drain after game {GameId} failed (non-fatal)", freshGameId);
        }
    }

    public void Receive(MissedReviewsDetectedMessage m)
    {
        // Surface missed/unsaved finished games so the webview can prompt review.
        // The capture for these still flows through the SAME ProcessGameEnd path
        // when the user chooses to ingest them (future write endpoint); here we
        // just announce them.
        _eventHub.Publish("missedReviews", new
        {
            isPostGameReconcile = m.IsPostGameReconcile,
            games = m.Games.Select(g => new
            {
                gameId = g.GameId,
                timestamp = g.Timestamp,
                championName = g.Stats.ChampionName,
                win = g.Stats.Win,
                enemyLaner = g.Stats.EnemyLaner,
            }).ToList(),
        });
        _logger.LogInformation("LCU reported {Count} missed review(s) → SSE", m.Games.Count);
    }

    public void Receive(LcuConnectionChangedMessage m)
    {
        _liveState.SetLcuConnected(m.IsConnected);
        _eventHub.Publish("lcuConnection", new { connected = m.IsConnected });
    }
}
