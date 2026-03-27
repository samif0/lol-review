#nullable enable

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for game_events table -- timestamped in-game events.</summary>
public interface IGameEventsRepository
{
    /// <summary>
    /// Bulk-insert events for a game. Each event needs event_type, game_time_s,
    /// and optionally details (dict). Clears any existing events for the game first.
    /// </summary>
    Task SaveEventsAsync(long gameId, IReadOnlyList<Dictionary<string, object?>> events);

    /// <summary>Get all events for a game, sorted by timestamp. Parses JSON details.</summary>
    Task<IReadOnlyList<Dictionary<string, object?>>> GetEventsAsync(long gameId);

    Task<bool> HasEventsAsync(long gameId);

    Task<int> GetEventCountAsync(long gameId);

    Task DeleteEventsAsync(long gameId);
}
