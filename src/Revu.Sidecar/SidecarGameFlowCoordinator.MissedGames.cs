#nullable enable

using Microsoft.Extensions.Logging;
using Revu.Core.Lcu;
using Revu.Core.Services;

namespace Revu.Sidecar;

/// <summary>
/// Finished games that never reached the save path the normal way.
///
/// <para>
/// The tracked game whose end-of-game capture failed (or whose end was missed while the
/// League client was away) comes back from match-history reconciliation as a recovered
/// <see cref="GameEndedMessage"/> and is saved automatically through
/// <see cref="PersistGameEndAsync"/>, with the pre-game choices kept here when the
/// monitor saw it end (<see cref="GameEndObservedMessage"/>).
/// </para>
///
/// <para>
/// Every other unsaved recent game (the app was closed, an older miss) waits in an
/// in-memory list for the player: <c>GET /api/missed-games</c> lists it and
/// <c>POST /api/missed-games/resolve</c> saves it through the same persistence path or
/// dismisses it for good (<c>IMissedGameDecisionRepository</c>). The startup reconcile
/// fills the list again after a restart. Changes are announced as the
/// <c>missedReviews</c> event for the Home card.
/// </para>
/// </summary>
public sealed partial class SidecarGameFlowCoordinator
{
    private const int MaxDeferredPreGame = 4;
    private readonly object _missedGate = new();
    private readonly Dictionary<long, PreGameState> _deferredPreGame = [];
    private readonly List<long> _deferredPreGameOrder = [];
    private List<MissedGameCandidate> _missedGames = [];
    private readonly SemaphoreSlim _missedResolve = new(1, 1);

    /// <summary>The pre-game choices of one ended game (<see cref="LcuLiveState.TakeForGameEnd"/>).</summary>
    private sealed record PreGameState(
        int Mood,
        string Intention,
        string IntentionSource,
        IReadOnlyList<long> PracticedIds,
        string? SessionKey,
        string ChampSelectPosition,
        string ChampSelectMap)
    {
        public static PreGameState Take(LcuLiveState liveState)
        {
            var (mood, intention, intentionSource, _, practicedIds, sessionKey, position, map) = liveState.TakeForGameEnd();
            return new PreGameState(mood, intention, intentionSource, practicedIds, sessionKey, position, map);
        }
    }

    public void Receive(GameEndObservedMessage m)
    {
        if (m.IsCasual)
        {
            // Never saved, but the flow is over: close it so pages leave the in-game surface.
            _liveState.TakeForGameEnd();
            _eventHub.Publish("gameEnded", new { gameId = (long?)null, saved = false, isRecovered = false });
            return;
        }
        lock (_missedGate)
        {
            // Taken now, on the monitor's thread, while no later champ select can have begun.
            // The first take belongs to the game: a second game-end pass for it (a collector
            // finished too early, then the real end) must not replace it with the already
            // cleared live state, but still ends the game. An unknown id (0) always holds the
            // latest game whose id was unknown.
            if (m.GameId > 0 && _deferredPreGame.ContainsKey(m.GameId))
            {
                _liveState.SetGameInProgress(false);
                return;
            }
            if (_deferredPreGame.Remove(m.GameId)) _deferredPreGameOrder.Remove(m.GameId);
            _deferredPreGame[m.GameId] = PreGameState.Take(_liveState);
            _deferredPreGameOrder.Add(m.GameId);
            while (_deferredPreGameOrder.Count > MaxDeferredPreGame)
            {
                _deferredPreGame.Remove(_deferredPreGameOrder[0]);
                _deferredPreGameOrder.RemoveAt(0);
            }
        }
    }

    public void Receive(GameCaptureDeferredMessage m)
    {
        if (m.GameId <= 0) return;
        _logger.LogInformation("Game {GameId} goes to match-history reconciliation; its pre-game choices are kept for the save", m.GameId);
        // The game is over: pages leave the live surface now, and the save follows once match
        // history has the game (gameEnded again, saved). A player already in the next lobby
        // or game stays where they are.
        if (NextFlowIsLive()) return;
        _eventHub.Publish("gameEnded", new { gameId = m.GameId, saved = false, pending = true });
    }

    /// <summary>
    /// A lobby or game is live that began after the last observed game end (that end closed
    /// its own flow in <see cref="LcuLiveState"/>).
    /// </summary>
    private bool NextFlowIsLive() => _liveState.SessionKey is not null || _liveState.IsGameInProgress;

    /// <summary>
    /// The event a finished game is announced with. Pages leave the live surface on
    /// <c>gameEnded</c>, so a recovered game whose flow closed when it ended, saved while the
    /// player is already in the next lobby or game, is announced as <c>gameRecovered</c>.
    /// </summary>
    private string GameEndEventType(bool isRecovered, PreGameState? preGame) =>
        isRecovered && preGame is not null && NextFlowIsLive() ? "gameRecovered" : "gameEnded";

    /// <summary>
    /// The tracked game's automatic save runs under the same gate as Home's Save, so the two
    /// never both pass the already-saved check for one game.
    /// </summary>
    private async Task PersistRecoveredGameAsync(Revu.Core.Models.GameStats stats, PreGameState? preGame)
    {
        await _missedResolve.WaitAsync().ConfigureAwait(false);
        try
        {
            await PersistGameEndAsync(stats, isRecovered: true, preGame).ConfigureAwait(false);
        }
        finally
        {
            _missedResolve.Release();
        }
    }

    private PreGameState? TakeDeferredPreGame(long gameId)
    {
        lock (_missedGate)
        {
            if (!_deferredPreGame.Remove(gameId, out var state)) return null;
            _deferredPreGameOrder.Remove(gameId);
            return state;
        }
    }

    public void Receive(MissedReviewsDetectedMessage m)
    {
        // Each pass lists every unsaved, undismissed recent ranked game it found (the tracked
        // game of a post-game pass is already on its way to the save path), so the latest list
        // replaces the waiting one. A game already waiting keeps its entry: that one may carry
        // the live timeline the monitor attached when it first listed the game.
        var incoming = m.Games
            .Where(static g => g.GameId > 0)
            .GroupBy(static g => g.GameId)
            .Select(static g => g.First())
            .OrderByDescending(static g => g.Timestamp)
            .ToList();
        bool changed;
        lock (_missedGate)
        {
            var waiting = _missedGames.ToDictionary(static g => g.GameId);
            var next = incoming
                .Select(g => waiting.TryGetValue(g.GameId, out var kept)
                    && kept.Stats.LiveEvents.Count >= g.Stats.LiveEvents.Count ? kept : g)
                .ToList();
            changed = !next.Select(static g => g.GameId).SequenceEqual(_missedGames.Select(static g => g.GameId));
            _missedGames = next;
        }
        if (changed) PublishMissedGames(m.IsPostGameReconcile);
        _logger.LogInformation("LCU reported {Count} unsaved recent game(s) waiting for the player", incoming.Count);
    }

    /// <summary>The games waiting for the player, newest first.</summary>
    internal IReadOnlyList<MissedGameDto> MissedGames()
    {
        lock (_missedGate)
        {
            return _missedGames.Select(static g => new MissedGameDto(
                g.GameId,
                g.Timestamp,
                g.Stats.ChampionName,
                g.Stats.EnemyLaner,
                g.Stats.Win,
                g.Stats.Kills,
                g.Stats.Deaths,
                g.Stats.Assists,
                g.Stats.GameDuration)).ToList();
        }
    }

    /// <summary>
    /// Save a waiting game through the normal game-end path (safety backup, row, session
    /// log, live timeline, post-game passes). A game the save gate declines is dismissed so
    /// it never comes back.
    /// </summary>
    internal async Task<MissedGameOutcome> SaveMissedGameAsync(long gameId)
    {
        await _missedResolve.WaitAsync().ConfigureAwait(false);
        try
        {
            var candidate = FindMissedGame(gameId);
            if (candidate is null) return MissedGameOutcome.NotFound;

            var result = await PersistGameEndAsync(candidate.Stats, isRecovered: true, TakeDeferredPreGame(gameId))
                .ConfigureAwait(false);
            if (result is null) return MissedGameOutcome.Failed;
            if (result.WasSaved) return MissedGameOutcome.Saved;

            await _write.GameLifecycle.ReconcileMissedGamesAsync(
                new ReconcileMissedGamesRequest(SelectedGames: [], DismissedGameIds: [gameId])).ConfigureAwait(false);
            ForgetMissedGame(gameId);
            return MissedGameOutcome.Declined;
        }
        finally
        {
            _missedResolve.Release();
        }
    }

    /// <summary>Dismiss a waiting game for good (it is never offered again).</summary>
    internal async Task<MissedGameOutcome> DismissMissedGameAsync(long gameId)
    {
        await _missedResolve.WaitAsync().ConfigureAwait(false);
        try
        {
            if (FindMissedGame(gameId) is null) return MissedGameOutcome.NotFound;

            await _write.BackupGuard.EnsureBackedUpAsync().ConfigureAwait(false);
            await _write.GameLifecycle.ReconcileMissedGamesAsync(
                new ReconcileMissedGamesRequest(SelectedGames: [], DismissedGameIds: [gameId])).ConfigureAwait(false);
            TakeDeferredPreGame(gameId);
            ForgetMissedGame(gameId);
            _logger.LogInformation("Unsaved game {GameId} dismissed by the player", gameId);
            return MissedGameOutcome.Dismissed;
        }
        finally
        {
            _missedResolve.Release();
        }
    }

    private MissedGameCandidate? FindMissedGame(long gameId)
    {
        lock (_missedGate) return _missedGames.FirstOrDefault(g => g.GameId == gameId);
    }

    /// <summary>A game was saved or dismissed: it no longer waits.</summary>
    private void ForgetMissedGame(long gameId)
    {
        bool removed;
        lock (_missedGate) removed = _missedGames.RemoveAll(g => g.GameId == gameId) > 0;
        if (removed) PublishMissedGames(isPostGameReconcile: false);
    }

    private void PublishMissedGames(bool isPostGameReconcile) =>
        _eventHub.Publish("missedReviews", new { isPostGameReconcile, games = MissedGames() });
}

internal enum MissedGameOutcome { NotFound, Saved, Declined, Dismissed, Failed }

/// <summary>One unsaved recent game for the Home card. Timestamp is Unix seconds.</summary>
internal sealed record MissedGameDto(
    long GameId,
    long Timestamp,
    string ChampionName,
    string EnemyLaner,
    bool Win,
    int Kills,
    int Deaths,
    int Assists,
    int GameDuration);
