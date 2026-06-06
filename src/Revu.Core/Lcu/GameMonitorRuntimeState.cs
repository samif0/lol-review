#nullable enable

namespace Revu.Core.Lcu;

internal sealed class GameMonitorRuntimeState
{
    public GamePhase LastPhase { get; set; } = GamePhase.None;

    public bool IsConnected { get; set; }

    public bool CurrentGameIsCasual { get; set; }

    /// <summary>
    /// v2.17.22: set once <see cref="GameInProgressMessage"/> has been sent for the
    /// current game, so the "confirmed in-game" signal fires exactly once (not on
    /// every InProgress poll tick). Reset when the game ends so the next game can
    /// fire it again.
    /// </summary>
    public bool GameInProgressNotified { get; set; }

    /// <summary>
    /// Set once <see cref="GameStartedMessage"/> has been sent (and the live-event
    /// collector started) for the current game. Used to suppress a SECOND
    /// GameStarted/collector-start when the LCU bounces InProgress → Reconnect →
    /// InProgress mid-game (which would restart the collector). Reset when the game
    /// ends or a new champ select begins so the next game fires fresh.
    /// </summary>
    public bool GameStartedNotified { get; set; }

    /// <summary>
    /// v2.18 (F5): set once <see cref="ChampSelectStartedMessage"/> has been sent
    /// for the current champ-select cycle. The "started" trigger normally fires
    /// only on the phase TRANSITION into champ select — but if that single fire
    /// is missed (LCU reconnect mid-select, a transient snapshot error, the page
    /// not yet listening), there was no recovery and the pre-game page never
    /// showed. This flag lets the monitor RE-FIRE the notification on a later tick
    /// while still in champ select. Reset when champ select ends so the next game
    /// fires fresh.
    /// </summary>
    public bool ChampSelectNotified { get; set; }

    public bool ReconcilePending { get; set; }

    public bool StartupReconcilePending { get; set; } = true;

    public int ConnectedTicks { get; set; }

    public int CredentialBackoffTicks { get; set; }

    /// <summary>
    /// Number of post-game reconcile retries remaining.
    /// Set when a game is detected ended but match history doesn't have it yet.
    /// </summary>
    public int PostGameReconcileRetriesRemaining { get; set; }

    /// <summary>
    /// Earliest UTC time at which the next post-game reconcile attempt is allowed.
    /// Used to implement exponential-ish backoff so the ~3-minute window is covered
    /// with far fewer LCU calls than the previous fixed 5s cadence.
    /// </summary>
    public DateTime PostGameReconcileNextAttemptUtc { get; set; } = DateTime.MinValue;

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
