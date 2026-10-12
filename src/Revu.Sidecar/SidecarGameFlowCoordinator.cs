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
public sealed partial class SidecarGameFlowCoordinator : IHostedService,
    IRecipient<ChampSelectStartedMessage>,
    IRecipient<ChampSelectUpdatedMessage>,
    IRecipient<ChampSelectCancelledMessage>,
    IRecipient<GameStartedMessage>,
    IRecipient<GameInProgressMessage>,
    IRecipient<GameEndedMessage>,
    IRecipient<GameEndObservedMessage>,
    IRecipient<GameCaptureDeferredMessage>,
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
    private readonly SidecarBackgroundWork _backgroundWork;
    private readonly RecordingRegistrationService? _recordingRegistrations;

    public SidecarGameFlowCoordinator(
        IMessenger messenger,
        SidecarEventHub eventHub,
        LcuLiveState liveState,
        GameMonitorService gameMonitor,
        WriteServices write,
        HardStopEnforcer hardStop,
        ILogger<SidecarGameFlowCoordinator> logger,
        SidecarBackgroundWork? backgroundWork = null,
        RecordingRegistrationService? recordingRegistrations = null)
    {
        _messenger = messenger;
        _eventHub = eventHub;
        _liveState = liveState;
        _gameMonitor = gameMonitor;
        _write = write;
        _hardStop = hardStop;
        _logger = logger;
        _recordingRegistrations = recordingRegistrations;
        _backgroundWork = backgroundWork ?? new SidecarBackgroundWork(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SidecarBackgroundWork>.Instance);
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
                // Unknown is not unsaved: offering a game that may already be saved would
                // risk a duplicate save. This pass skips it; the next one asks again.
                _logger.LogDebug(ex, "CheckGameSaved probe failed for {GameId}", gameId);
                return true;
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
        _backgroundWork.TryRun("queue hard stop", () => _hardStop.HandleQueueAsync(phase));
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
        // The pre-game choices (mood / intent / practiced ids / session key) belong to
        // this game: they were kept when the monitor saw it end (GameEndObservedMessage),
        // before the capture's retries, so a champ select that begins in between can never
        // lend its lobby to this game. A live capture whose id was unknown then uses the
        // unknown-id entry. Any other recovered game gets none (a stale champ select would
        // mislabel it, mirror Shell).
        var preGame = TakeDeferredPreGame(stats.GameId);
        if (preGame is null && !isRecovered) preGame = TakeDeferredPreGame(0) ?? PreGameState.Take(_liveState);
        if (isRecovered) _backgroundWork.TryRun("game-end persistence", () => PersistRecoveredGameAsync(stats, preGame));
        else _backgroundWork.TryRun("game-end persistence", () => PersistGameEndAsync(stats, isRecovered, preGame));
    }

    private async Task<ProcessGameEndResult?> PersistGameEndAsync(
        Revu.Core.Models.GameStats stats, bool isRecovered, PreGameState? preGame)
    {
        try
        {
            // Take a safety backup before the first write of the session (same
            // guard the manual write endpoints use).
            await _write.BackupGuard.EnsureBackedUpAsync().ConfigureAwait(false);

            // A recovered game is saved once. If the live capture (or an earlier pass)
            // already stored it, that row and its live timeline stay as they are.
            if (isRecovered && await _write.Games.GetAsync(stats.GameId).ConfigureAwait(false) is not null)
            {
                _logger.LogInformation("Recovered game {GameId} is already saved; nothing to add", stats.GameId);
                ForgetMissedGame(stats.GameId);
                return new ProcessGameEndResult(stats.GameId, IsSkipped: false, IsRecovered: true);
            }

            // v3.10.1: the matchup goes on the row NOW. The capture already tried the
            // EOG payload and the live roster; what is still blank gets the champ-
            // select snapshot (this lobby only), then a role-prior estimate — both
            // marked as estimates so the Match-V5 pass below confirms them. A recovered
            // game qualifies only with its own pre-game state (the tracked game).
            ApplyMatchupFallbacks(stats, preGame is null, preGame?.SessionKey,
                preGame?.ChampSelectPosition ?? "", preGame?.ChampSelectMap ?? "");

            ProcessGameEndRequest request = preGame is null
                ? new ProcessGameEndRequest(stats, MentalRating: 5, PreGameMood: 0)
                : new ProcessGameEndRequest(
                    stats,
                    MentalRating: 5,
                    PreGameMood: preGame.Mood,
                    PreGamePracticedObjectiveIds: preGame.PracticedIds.Count > 0 ? preGame.PracticedIds : null,
                    PregameIntention: preGame.Intention,
                    IntentionSource: preGame.IntentionSource);

            var result = await _write.GameLifecycle
                .ProcessGameEndAsync(request, isRecovered)
                .ConfigureAwait(false);

            if (result.WasSaved && result.GameId is long gameId)
            {
                ForgetMissedGame(gameId);
                if (_recordingRegistrations is not null)
                    _backgroundWork.TryRun("post-game recording registration", () => _recordingRegistrations.ReconcileAsync(gameId));
                // Promote champ-select draft prompt answers onto the real game row
                // (idempotent upsert) — only for a flow that had a session key: the
                // live game, or the tracked game recovered with its own pre-game state
                // (mirror ShellViewModel.PromotePreGameDraftsAsync).
                if (preGame?.SessionKey is { Length: > 0 } sessionKey)
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

                // Prepare only when the player opted in and the captured pairing
                // is trustworthy. The confirmation pass retries incomplete games.
                // 3.14.1: a short game whose payload did not say "not a remake" waits
                // for the post-game remake check, so a remake never gets an automatic note.
                var deferMatchupNote = RemakeNoteMustWait(stats.EndedInEarlySurrender, stats.GameDuration);
                if (!deferMatchupNote) await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);

                _logger.LogInformation("Live game captured + saved: game {GameId} ({Champ})", gameId, stats.ChampionName);
                _eventHub.Publish(GameEndEventType(isRecovered, preGame), new
                {
                    gameId,
                    championName = stats.ChampionName,
                    win = stats.Win,
                    enemyLaner = stats.EnemyLaner,
                    saved = true,
                    isRecovered,
                    isRemake = stats.IsRemake,
                });

                // v3.2: derive the just-ended game's map-state (jungle proximity +
                // fog deaths) automatically, so the timeline markers appear without
                // a manual Settings backfill. Tracked background work.
                _backgroundWork.TryRun("post-game map state", () => TryMapStateWithRetryAsync(gameId));

                _backgroundWork.TryRun("post-game Ascent scan", () =>
                    AscentRecordingScan.RunWithRetryAsync(_write.VodScan, _write.Config, _write.Vod,
                        _backgroundWork, async () =>
                        {
                            await _write.BackupGuard.EnsureBackedUpAsync();
                        }, linkedId => _eventHub.Publish("vodLinked", new { gameId = linkedId }), _logger, gameId,
                        registrations: _recordingRegistrations));

                // v3.10: heal the matchup (enemy laner + role→champion map) from
                // Match-V5 when the LCU end-of-game payload left it blank, so the
                // Review page and the Matchups journal show "you vs them" without
                // the manual Settings backfill. Fire-and-forget like the others.
                var duration = stats.GameDuration;
                _backgroundWork.TryRun("post-game matchup", () => TryMatchupWithRetryAsync(gameId, duration, deferMatchupNote));
            }
            else
            {
                // Skipped (casual or short unflagged game the workflow declined to persist).
                _logger.LogInformation("Live game end processed but not saved (skipped/casual)");
                _eventHub.Publish(GameEndEventType(isRecovered, preGame), new
                {
                    gameId = (long?)null,
                    championName = stats.ChampionName,
                    win = stats.Win,
                    saved = false,
                    isRecovered,
                    isRemake = stats.IsRemake,
                });
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist live game end");
            _eventHub.Publish(GameEndEventType(isRecovered, preGame), new { saved = false, error = ex.Message });
            return null;
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
            await Task.Delay(TimeSpan.FromSeconds(delay), _backgroundWork.Stopping).ConfigureAwait(false);
            try
            {
                if (!_write.Config.HasValidRiotSession)
                {
                    _logger.LogDebug("Post-game map-state skipped for game {GameId}: no Riot session", gameId);
                    return;
                }

                var result = await _write.MapStateBackfill.RunAsync(maxGames: 5, _backgroundWork.Stopping).ConfigureAwait(false);
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
    // 3.14.1: the same timer confirms the remake verdict first (Match-V5 has Riot's
    // early-surrender flag), ahead of the matchup's checks and early returns, so a remake
    // is settled before any automatic matchup note. When the loop reaches +5 min, that
    // attempt reruns it only if +90 s recorded nothing; the startup check covers the rest.
    // A note deferred at game end is prepared once the remake verdict is recorded, or after
    // the last attempt either way (the preparation itself skips a game marked as a remake).
    private async Task TryMatchupWithRetryAsync(long gameId, int duration, bool deferredNote = false)
    {
        var delaysSeconds = new[] { 90, 300 };
        var remakeSettled = false;
        try
        {
            foreach (var delay in delaysSeconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), _backgroundWork.Stopping).ConfigureAwait(false);
                if (!remakeSettled) remakeSettled = await TryConfirmRemakeAsync(gameId, duration).ConfigureAwait(false);
                if (deferredNote && remakeSettled)
                {
                    deferredNote = false;
                    await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);
                }
                if (!await TryMatchupAttemptAsync(gameId, remakePending: deferredNote).ConfigureAwait(false)) return;
            }
        }
        finally
        {
            if (deferredNote && !_backgroundWork.Stopping.IsCancellationRequested)
                await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);
        }
    }

    /// <summary>3.14.1: whether the automatic matchup note waits for the post-game remake
    /// check. Only a payload that said "not a remake", or a game too long to be one, goes
    /// ahead at game end.</summary>
    internal static bool RemakeNoteMustWait(bool? endedInEarlySurrender, int durationS) =>
        endedInEarlySurrender != false && durationS < Revu.Core.Services.RemakeCheckService.CandidateMaxDurationS;

    // One matchup confirmation attempt. False ends the retries.
    private async Task<bool> TryMatchupAttemptAsync(long gameId, bool remakePending)
    {
        try
        {
            if (!_write.Config.HasValidRiotSession)
            {
                _logger.LogDebug("Post-game matchup pass skipped for game {GameId}: no Riot session", gameId);
                // A pending remake check (the client's match history) still gets its +5 min try.
                return remakePending;
            }

            var row = await _write.Games.GetAsync(gameId).ConfigureAwait(false);
            if (row is null) return false;
            if (row.MatchupSource is MatchupSources.MatchV5 or MatchupSources.User)
            {
                await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);
                return false; // gone, confirmed by an earlier attempt, or the player's own word
            }

            var outcome = await _write.EnemyLanerBackfill.BackfillGameAsync(gameId, _backgroundWork.Stopping).ConfigureAwait(false);
            switch (outcome)
            {
                case EnemyLanerBackfillOutcome.Updated:
                    await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);
                    _eventHub.Publish("matchupUpdated", new { gameId, updated = 1 });
                    _logger.LogInformation(
                        "Post-game matchup confirmed from Match-V5 for game {GameId} (game end had '{Source}')",
                        gameId, row.MatchupSource);
                    await DrainMatchupBacklogAsync(gameId).ConfigureAwait(false);
                    return false;
                case EnemyLanerBackfillOutcome.Skipped:
                    // Upstream has the match but no positions (a non-positional
                    // queue) — nothing more to learn; keep what game end wrote.
                    _logger.LogDebug("Post-game matchup pass: nothing to confirm upstream for game {GameId}", gameId);
                    return false;
                case EnemyLanerBackfillOutcome.NotConfigured:
                    return false;
                default:
                    // Failed: the fresh match isn't visible upstream yet — fall
                    // through to the longer delay (the startup heal covers the rest).
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Post-game matchup attempt failed for game {GameId} (will retry/heal)", gameId);
        }
        return true;
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
            var result = await _write.EnemyLanerBackfill.RunAsync(maxGames: 5, ct: _backgroundWork.Stopping).ConfigureAwait(false);
            if (result.Updated > 0)
            {
                foreach (var gameId in pending.Take(5))
                    await TryPrepareMatchupNoteAsync(gameId).ConfigureAwait(false);
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

    private async Task TryPrepareMatchupNoteAsync(long gameId)
    {
        try
        {
            var result = await MatchupJournalPreparation.TryAutoPrepareAsync(
                gameId, _write.Games, _write.Matchups, _write.Config, _backgroundWork.Stopping).ConfigureAwait(false);
            if (result?.Created == true)
            {
                _logger.LogInformation("Matchup note {CardId} prepared for game {GameId}", result.Id, gameId);
                _eventHub.Publish("matchupUpdated", new { gameId, updated = 1 });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optional matchup note preparation failed for game {GameId}", gameId);
        }
    }

    public void Receive(LcuConnectionChangedMessage m)
    {
        _liveState.SetLcuConnected(m.IsConnected);
        _eventHub.Publish("lcuConnection", new { connected = m.IsConnected });
    }
}
