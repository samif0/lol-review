#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Lcu;

/// <summary>Sent when an end-of-game stats extraction completes.</summary>
public sealed record GameEndedMessage(GameStats Stats, bool IsRecovered = false);

/// <summary>Sent when recent unsaved finished games are detected from match history.</summary>
public sealed record MissedReviewsDetectedMessage(IReadOnlyList<GameStats> Games);

/// <summary>Sent when champion select begins (non-casual queues only).</summary>
public sealed record ChampSelectStartedMessage(int QueueId);

/// <summary>Sent when the game transitions to loading/in-progress.</summary>
public sealed record GameStartedMessage;

/// <summary>Sent when champ select is cancelled (dodge, queue pop expired, etc.).</summary>
public sealed record ChampSelectCancelledMessage;

/// <summary>Sent when the LCU connection state changes.</summary>
public sealed record LcuConnectionChangedMessage(bool IsConnected);
