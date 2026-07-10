#nullable enable

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    /// <summary>
    /// v3.2 (schema v11): game_ids the map-state pass hasn't processed at
    /// <paramref name="currentVersion"/> yet (NULL or older analyzer version).
    /// Excludes hidden games and casual queues — the same filter as the other
    /// Match-V5 backfills; newest first so fresh games get their map state first.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetGameIdsMissingMapStateAsync(int currentVersion)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE (map_state_v IS NULL OR map_state_v < @version)
              {CasualFilter}
              AND (is_hidden IS NULL OR is_hidden = 0)
            ORDER BY timestamp DESC";
        cmd.Parameters.AddWithValue("@version", currentVersion);

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>v3.2 (schema v11): mark a game processed by the map-state pass.</summary>
    public async Task UpdateMapStateVersionAsync(long gameId, int version)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE games SET map_state_v = @version WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }
}
