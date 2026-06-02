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
    string MyPosition = "",
    string ParticipantMapJson = "");

/// <summary>Sent while champ select is ongoing and the locally-picked or opposing-laner champion changes.</summary>
public sealed record ChampSelectUpdatedMessage(
    string MyChampion,
    string EnemyLaner,
    string MyPosition,
    string ParticipantMapJson = "");

/// <summary>
/// Sent on the first transition into loading-or-in-game (GameStart or InProgress).
/// Kicks off the live-event collector. Does NOT mean the player is fully in the
/// game yet — at the 5s poll cadence the loading screen (GameStart) is often
/// skipped, so this can fire while the client is still on the loading screen.
/// Pre-game teardown is driven by <see cref="GameInProgressMessage"/> instead.
/// </summary>
public sealed record GameStartedMessage;

/// <summary>
/// v2.17.22: sent once the player is confirmed in the game — one poll tick after
/// <see cref="GamePhase.InProgress"/> is first observed. This is the signal to
/// leave the pre-game page and minimize the window, so the matchup/objectives
/// stay readable over League's loading screen until the game actually begins.
/// </summary>
public sealed record GameInProgressMessage;

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

/// <summary>v2.16.6: a VOD bookmark or clip was created, retagged, or
/// deleted. Lets the review/post-game pages re-run the bookmark→objective
/// auto-merge live instead of waiting for the user to leave + re-enter.</summary>
public sealed record BookmarkChangedMessage(long GameId);

/// <summary>Sent after a game is marked reviewed (including a skip-review
/// from a list view). Lets other open list views refresh their unreviewed /
/// reviewed bucketing without a full reload.</summary>
public sealed record GameReviewedMessage(long GameId);

/// <summary>
/// Sent after the Match-V5 backfill writes lane matchup metadata. Open game
/// lists should reload so old rows can switch from "champion vs enemy" to
/// role-aware duo labels when participant maps become available.
/// </summary>
public sealed record GameMatchupsBackfilledMessage(int Updated);
