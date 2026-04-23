#nullable enable

using System.Text.Json;

namespace Revu.Core.Lcu;

/// <summary>
/// Interface for the League Live Client Data API (https://127.0.0.1:2999).
/// This API is only available while a game is actively running.
/// </summary>
public interface ILiveEventApi
{
    /// <summary>
    /// Get the local player's summoner name from the live game.
    /// </summary>
    Task<string?> GetActivePlayerNameAsync(CancellationToken ct = default);

    /// <summary>
    /// Check if the Live Client Data API is reachable (game is running).
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Fetch the raw event list from the Live Client Data API.
    /// Returns null if the API is not available.
    /// </summary>
    Task<List<JsonElement>?> FetchEventsAsync(CancellationToken ct = default);
}
