#nullable enable

using Revu.Core.Models;
using Revu.Core.Services;

namespace Revu.Core.Lcu;

/// <summary>Sent when an end-of-game stats extraction completes.</summary>
public sealed record GameEndedMessage(GameStats Stats, bool IsRecovered = false);

/// <summary>Sent when recent unsaved finished games are detected from match history.</summary>
/// <param name="Games">Candidates that need review.</param>
/// <param name="IsPostGameReconcile">
/// True when reconciliation fired immediately after a game the monitor tracked (InProgress → idle),
/// meaning the user just finished a game. The shell should skip the selection dialog and open post-game directly.
/// </param>
public sealed record MissedReviewsDetectedMessage(IReadOnlyList<MissedGameCandidate> Games, bool IsPostGameReconcile = false);

/// <summary>Sent when champion select begins (non-casual queues only).</summary>
public sealed record ChampSelectStartedMessage(
    int QueueId,
    string MyChampion = "",
    string EnemyLaner = "",
    string MyPosition = "");

/// <summary>Sent while champ select is ongoing and the locally-picked or opposing-laner champion changes.</summary>
public sealed record ChampSelectUpdatedMessage(
    string MyChampion,
    string EnemyLaner,
    string MyPosition);

/// <summary>Sent when the game transitions to loading/in-progress.</summary>
public sealed record GameStartedMessage;

/// <summary>Sent when champ select is cancelled (dodge, queue pop expired, etc.).</summary>
public sealed record ChampSelectCancelledMessage;

/// <summary>Sent when the LCU connection state changes.</summary>
public sealed record LcuConnectionChangedMessage(bool IsConnected);

/// <summary>
/// Sent after a game is permanently deleted (rows purged from games + all
/// child tables, clip files removed). ViewModels that cache lists or
/// computed stats derived from the deleted game should refresh.
/// </summary>
public sealed record GameDeletedMessage(long GameId);
