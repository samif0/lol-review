#nullable enable

namespace Revu.Core.Lcu;

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

    /// <summary>Last locally-picked champion reported during the current champ-select phase.</summary>
    public string LastChampSelectMy { get; set; } = "";

    /// <summary>Last opposing-laner champion reported during the current champ-select phase.</summary>
    public string LastChampSelectEnemy { get; set; } = "";

    /// <summary>Last known local-player position for this champ-select phase (TOP|JUNGLE|MIDDLE|BOTTOM|UTILITY).</summary>
    public string LastChampSelectMyPosition { get; set; } = "";

    /// <summary>v2.16.4: serialized role→champion JSON map for both teams,
    /// captured each champ-select tick. Drives 2v2 matchup pairings + per-
    /// enemy cooldown intel cards on PreGamePage.</summary>
    public string LastChampSelectMapJson { get; set; } = "";
}
