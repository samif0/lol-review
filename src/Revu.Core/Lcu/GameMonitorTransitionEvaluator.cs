#nullable enable

namespace Revu.Core.Lcu;

internal sealed record GameMonitorTransitionPlan(
    bool ReconcileOnStartup,
    bool NotifyChampSelectStarted,
    bool NotifyChampSelectCancelled,
    bool NotifyGameStarted,
    bool GameInProgressCandidate,
    bool HandleGameEnded,
    bool ReconcileMatchHistory,
    bool ResetCurrentGameCasual);

internal sealed class GameMonitorTransitionEvaluator
{
    public GameMonitorTransitionPlan Evaluate(GameMonitorRuntimeState state, GamePhase phase)
    {
        var reconcileOnStartup = state.StartupReconcilePending
            && !state.ReconcilePending
            && !IsPostGamePhase(state.LastPhase)
            && state.ConnectedTicks >= 2
            && IsIdlePhase(phase);

        // Fire on the transition INTO champ select, OR as a recovery if we're in
        // champ select but never successfully notified this cycle (reconnect
        // mid-select, transient snapshot error, page not yet listening). The
        // ChampSelectNotified flag is set by GameMonitorService only after the
        // message is actually sent, so this re-fires until it lands.
        var notifyChampSelectStarted =
            phase == GamePhase.ChampSelect
            && (state.LastPhase != GamePhase.ChampSelect || !state.ChampSelectNotified);

        // Reconnect means the player dropped from the game SERVER but can rejoin —
        // it is still an in-game phase, so it must not look like a champ-select cancel.
        var notifyChampSelectCancelled =
            state.LastPhase == GamePhase.ChampSelect
            && phase is not GamePhase.ChampSelect
                and not GamePhase.InProgress
                and not GamePhase.GameStart
                and not GamePhase.Reconnect;

        // Fire GameStarted on entry into the in-game phases, but exactly ONCE per
        // game (GameStartedNotified one-shot). This is what makes InProgress →
        // Reconnect → InProgress safe: the return-from-Reconnect tick won't re-fire
        // GameStarted (and won't restart the event collector, tripping the restart
        // race) because the flag is already set. A genuine first start that arrives
        // via Reconnect (ChampSelect → Reconnect → InProgress) still fires, because
        // the flag is not yet set. The phase-edge guard remains so a same-phase
        // re-poll (InProgress → InProgress) is a no-op even before the flag is set.
        var notifyGameStarted =
            phase is GamePhase.InProgress or GamePhase.GameStart
            && !state.GameStartedNotified
            && state.LastPhase is not GamePhase.InProgress and not GamePhase.GameStart;

        // v2.17.25: CANDIDATE for "confirmed in-game". The LCU flips to InProgress
        // the instant the game process launches — but the loading screen runs
        // DURING InProgress and can last 15-40s, so a short poll-count dwell tore
        // the pre-game page down mid-loading. The real "past the loading screen"
        // signal is the in-game Live Client Data API responding; that check is
        // async, so the evaluator only flags the candidate (we're in InProgress and
        // haven't fired yet) and GameMonitorService gates the actual notification on
        // ILiveEventApi.IsAvailableAsync().
        var gameInProgressCandidate =
            phase == GamePhase.InProgress
            && !state.GameInProgressNotified;

        var handleGameEnded =
            IsPostGamePhase(phase)
            && !IsPostGamePhase(state.LastPhase);

        var reconcileMatchHistory =
            IsIdlePhase(phase)
            && !handleGameEnded  // don't reconcile on the same tick we just triggered EOG capture
            && (
                // Transition from a game-related phase → idle (first time after game ends)
                state.LastPhase is GamePhase.EndOfGame or GamePhase.PreEndOfGame
                    or GamePhase.InProgress or GamePhase.GameStart or GamePhase.WaitingForStats
                    or GamePhase.Reconnect
                // OR match history not yet available — keep retrying until monitor clears the flag
                || state.ReconcilePending
            );

        return new GameMonitorTransitionPlan(
            ReconcileOnStartup: reconcileOnStartup,
            NotifyChampSelectStarted: notifyChampSelectStarted,
            NotifyChampSelectCancelled: notifyChampSelectCancelled,
            NotifyGameStarted: notifyGameStarted,
            GameInProgressCandidate: gameInProgressCandidate,
            HandleGameEnded: handleGameEnded,
            ReconcileMatchHistory: reconcileMatchHistory,
            ResetCurrentGameCasual: handleGameEnded);
    }

    private static bool IsIdlePhase(GamePhase phase) =>
        phase is GamePhase.Lobby or GamePhase.None or GamePhase.ReadyCheck or GamePhase.ChampSelect;

    private static bool IsPostGamePhase(GamePhase phase) =>
        phase is GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame;
}
