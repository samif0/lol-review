#nullable enable

namespace LoLReview.Core.Lcu;

internal sealed class GameMonitorRuntimeState
{
    public GamePhase LastPhase { get; set; } = GamePhase.None;

    public bool IsConnected { get; set; }

    public bool CurrentGameIsCasual { get; set; }

    public bool ReconcilePending { get; set; }

    public bool StartupReconcilePending { get; set; } = true;

    public int ConnectedTicks { get; set; }

    public int CredentialBackoffTicks { get; set; }

    /// <summary>
    /// Number of post-game reconcile retries remaining.
    /// Set when a game is detected ended but match history doesn't have it yet.
    /// The monitor retries every tick until this reaches 0.
    /// </summary>
    public int PostGameReconcileRetriesRemaining { get; set; }
}
