#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Lcu;

public interface IGameEndCaptureService
{
    /// <summary>
    /// Capture the just-ended game from the LCU end-of-game payload.
    /// <paramref name="roster"/> is the lobby the live client reported during the
    /// game (<see cref="LiveEventCollector.Roster"/>); when the payload carries no
    /// per-player positions it supplies the lanes, so the matchup is on the row at
    /// once. Null when the collector never reached the live client.
    /// </summary>
    Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        LiveRoster? roster,
        CancellationToken cancellationToken = default);

    /// <summary>Capture without a live roster (legacy callers and fakes).</summary>
    Task<GameStats?> CaptureAsync(
        IReadOnlyList<GameEvent> liveEvents,
        CancellationToken cancellationToken = default) =>
        CaptureAsync(liveEvents, roster: null, cancellationToken);
}
