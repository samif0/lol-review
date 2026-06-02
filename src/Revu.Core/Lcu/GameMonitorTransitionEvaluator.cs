#nullable enable

namespace Revu.Core.Lcu;

internal sealed record GameMonitorTransitionPlan(
    bool ReconcileOnStartup,
    bool NotifyChampSelectStarted,
    bool NotifyChampSelectCancelled,
    bool NotifyGameStarted,
    bool NotifyGameInProgress,
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

        var notifyChampSelectStarted =
            phase == GamePhase.ChampSelect
            && state.LastPhase != GamePhase.ChampSelect;

        var notifyChampSelectCancelled =
            state.LastPhase == GamePhase.ChampSelect
            && phase is not GamePhase.ChampSelect
                and not GamePhase.InProgress
                and not GamePhase.GameStart;

        var notifyGameStarted =
            phase is GamePhase.InProgress or GamePhase.GameStart
            && state.LastPhase is not GamePhase.InProgress and not GamePhase.GameStart;

        // v2.17.22: "confirmed in-game" fires one poll tick after InProgress is
        // first observed (this tick and the last were both InProgress) and only
        // once per game. The one-tick dwell keeps the pre-game page + window up
        // through the loading screen — at the 5s cadence GameStart is frequently
        // skipped, so we cannot key off it directly; instead we lean on InProgress
        // persisting, which guarantees at least one full poll interval of read
        // time after champ select before teardown.
        var notifyGameInProgress =
            phase == GamePhase.InProgress
            && state.LastPhase == GamePhase.InProgress
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
                // OR match history not yet available — keep retrying until monitor clears the flag
                || state.ReconcilePending
            );

        return new GameMonitorTransitionPlan(
            ReconcileOnStartup: reconcileOnStartup,
            NotifyChampSelectStarted: notifyChampSelectStarted,
            NotifyChampSelectCancelled: notifyChampSelectCancelled,
            NotifyGameStarted: notifyGameStarted,
            NotifyGameInProgress: notifyGameInProgress,
            HandleGameEnded: handleGameEnded,
            ReconcileMatchHistory: reconcileMatchHistory,
            ResetCurrentGameCasual: handleGameEnded);
    }

    private static bool IsIdlePhase(GamePhase phase) =>
        phase is GamePhase.Lobby or GamePhase.None or GamePhase.ReadyCheck or GamePhase.ChampSelect;

    private static bool IsPostGamePhase(GamePhase phase) =>
        phase is GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame;
}
