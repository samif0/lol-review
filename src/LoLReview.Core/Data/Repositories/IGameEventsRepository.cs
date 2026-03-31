#nullable enable

using LoLReview.Core.Models;

namespace LoLReview.Core.Data.Repositories;

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
}
