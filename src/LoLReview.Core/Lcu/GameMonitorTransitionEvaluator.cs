#nullable enable

namespace LoLReview.Core.Lcu;

internal sealed record GameMonitorTransitionPlan(
    bool ReconcileOnStartup,
    bool NotifyChampSelectStarted,
    bool NotifyChampSelectCancelled,
    bool NotifyGameStarted,
    bool HandleGameEnded,
    bool ReconcileMatchHistory,
    bool ResetCurrentGameCasual);

internal sealed class GameMonitorTransitionEvaluator
{
    public GameMonitorTransitionPlan Evaluate(GameMonitorRuntimeState state, GamePhase phase)
    {
        var reconcileOnStartup = state.StartupReconcilePending
            && state.LastPhase == GamePhase.None
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

        var handleGameEnded =
            IsPostGamePhase(phase)
            && !IsPostGamePhase(state.LastPhase);

        var reconcileMatchHistory =
            state.LastPhase is GamePhase.EndOfGame or GamePhase.PreEndOfGame or GamePhase.InProgress or GamePhase.GameStart or GamePhase.WaitingForStats
            && IsIdlePhase(phase)
            && (state.ReconcilePending || state.LastPhase == GamePhase.InProgress);

        return new GameMonitorTransitionPlan(
            ReconcileOnStartup: reconcileOnStartup,
            NotifyChampSelectStarted: notifyChampSelectStarted,
            NotifyChampSelectCancelled: notifyChampSelectCancelled,
            NotifyGameStarted: notifyGameStarted,
            HandleGameEnded: handleGameEnded,
            ReconcileMatchHistory: reconcileMatchHistory,
            ResetCurrentGameCasual: handleGameEnded);
    }

    private static bool IsIdlePhase(GamePhase phase) =>
        phase is GamePhase.Lobby or GamePhase.None or GamePhase.ReadyCheck or GamePhase.ChampSelect;

    private static bool IsPostGamePhase(GamePhase phase) =>
        phase is GamePhase.WaitingForStats or GamePhase.PreEndOfGame or GamePhase.EndOfGame;
}
