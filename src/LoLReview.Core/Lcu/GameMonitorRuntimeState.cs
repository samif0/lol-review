#nullable enable

namespace LoLReview.Core.Lcu;

internal sealed class GameMonitorRuntimeState
{
    public GamePhase LastPhase { get; set; } = GamePhase.None;

    public bool IsConnected { get; set; }

    public bool CurrentGameIsCasual { get; set; }

    public bool ReconcilePending { get; set; }

    public bool StartupReconcilePending { get; set; } = true;

    public int CredentialBackoffTicks { get; set; }
}
