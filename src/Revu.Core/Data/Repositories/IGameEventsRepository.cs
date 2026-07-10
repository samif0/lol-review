#nullable enable

using Revu.Core.Models;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for game_events table -- timestamped in-game events.</summary>
public interface IGameEventsRepository
{
    /// <summary>
    /// Bulk-insert events for a game. Clears any existing events for the game first.
    /// </summary>
    Task SaveEventsAsync(long gameId, IReadOnlyList<GameEvent> events);

    /// <summary>Get all events for a game, sorted by timestamp.</summary>
    Task<IReadOnlyList<GameEvent>> GetEventsAsync(long gameId);

    Task<bool> HasEventsAsync(long gameId);

    Task<int> GetEventCountAsync(long gameId);

    Task DeleteEventsAsync(long gameId);

    /// <summary>
    /// v3.2: insert derived rows WITHOUT clearing the game's existing events —
    /// the append path for post-game backfills (SaveEventsAsync is capture-time
    /// only and would wipe the live-feed rows).
    /// </summary>
    Task AppendEventsAsync(long gameId, IReadOnlyList<GameEvent> events);

    /// <summary>v3.2: delete only one event type's rows for a game — the
    /// idempotency half of a derived-event re-run (delete + append).</summary>
    Task DeleteEventsByTypeAsync(long gameId, string eventType);

    /// <summary>v3.2: rewrite one event row's Details JSON in place (persists
    /// post-game attribute stamps on existing rows, e.g. death map-state).</summary>
    Task UpdateEventDetailsAsync(int eventId, string details);
}
