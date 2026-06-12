#nullable enable

namespace Revu.Core.Data.Repositories;

public sealed partial class GameRepository
{
    /// <summary>
    /// v2.18 (schema v5): game_ids that have no laning-at-10 numbers yet, so
    /// the Match-V5 timeline backfill can fill them. Excludes hidden games and
    /// casual queues (the same filter as the enemy-laner backfill); newest
    /// first so fresh games get their numbers before the deep backlog.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetGameIdsMissingLaningAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT game_id FROM games
            WHERE cs_at_10 IS NULL
              {CasualFilter}
              AND (is_hidden IS NULL OR is_hidden = 0)
            ORDER BY timestamp DESC";

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }
        return ids;
    }

    /// <summary>v2.18 (schema v5): persist laning-at-10 numbers from the timeline backfill.</summary>
    public async Task UpdateLaningAt10Async(long gameId, double csAt10, int? goldDiffAt10, double? csDiffAt10)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE games
            SET cs_at_10 = @csAt10,
                gold_diff_at_10 = @goldDiff,
                cs_diff_at_10 = @csDiff
            WHERE game_id = @gameId
            """;
        cmd.Parameters.AddWithValue("@csAt10", csAt10);
        cmd.Parameters.AddWithValue("@goldDiff", goldDiffAt10.HasValue ? goldDiffAt10.Value : System.DBNull.Value);
        cmd.Parameters.AddWithValue("@csDiff", csDiffAt10.HasValue ? csDiffAt10.Value : System.DBNull.Value);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }
}
