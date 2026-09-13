#nullable enable

namespace Revu.Core.Lcu;

/// <summary>Current LCU session identity; never populated from match history.</summary>
public sealed record RecordingGameContext(long? GameId, bool IsGameInProgress, double? GameTimeSeconds,
    long? LastEndedGameId = null, DateTimeOffset? ObservedAt = null,
    double? LastEndedGameTimeSeconds = null, DateTimeOffset? LastEndedObservedAt = null,
    DateTimeOffset? LastEndedConfirmedAt = null)
{
    public static RecordingGameContext Idle { get; } = new(null, false, null);
}
