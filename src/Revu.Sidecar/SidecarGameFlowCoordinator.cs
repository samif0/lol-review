#nullable enable

using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Revu.Core.Lcu;
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
    IRecipient<LcuConnectionChangedMessage>
{
    private readonly IMessenger _messenger;
    private readonly SidecarEventHub _eventHub;
    private readonly LcuLiveState _liveState;
    private readonly GameMonitorService _gameMonitor;
    private readonly WriteServices _write;
    private readonly ILogger<SidecarGameFlowCoordinator> _logger;

    public SidecarGameFlowCoordinator(
        IMessenger messenger,
        SidecarEventHub eventHub,
        LcuLiveState liveState,
        GameMonitorService gameMonitor,
        WriteServices write,
        ILogger<SidecarGameFlowCoordinator> logger)
    {
        _messenger = messenger;
        _eventHub = eventHub;
        _liveState = liveState;
        _gameMonitor = gameMonitor;
        _write = write;
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
        _eventHub.Publish("gameStarted", new { });
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
            var (mood, intention, intentionSource, _, practicedIds, sessionKey) = _liveState.TakeForGameEnd();

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
