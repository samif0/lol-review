#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>
/// SQLite-backed persistence for missed-game ingest dismissals.
/// </summary>
public sealed class MissedGameDecisionRepository : IMissedGameDecisionRepository
{
    private readonly IDbConnectionFactory _factory;

    public MissedGameDecisionRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public async Task<HashSet<long>> GetDismissedGameIdsAsync(IEnumerable<long> gameIds)
    {
        var ids = gameIds.Where(static id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        var parameters = new List<string>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var name = $"@id{i}";
            parameters.Add(name);
            cmd.Parameters.AddWithValue(name, ids[i]);
        }

        cmd.CommandText = $"""
            SELECT game_id
            FROM missed_game_decisions
            WHERE decision = 'dismissed'
              AND game_id IN ({string.Join(", ", parameters)})
            """;

        var dismissed = new HashSet<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (!reader.IsDBNull(0))
            {
                dismissed.Add(reader.GetInt64(0));
            }
        }

        return dismissed;
    }

    public async Task MarkDismissedAsync(IEnumerable<long> gameIds)
    {
        var ids = gameIds.Where(static id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var gameId in ids)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO missed_game_decisions (game_id, decision, created_at, updated_at)
                VALUES (@gameId, 'dismissed', @now, @now)
                ON CONFLICT(game_id) DO UPDATE SET
                    decision = 'dismissed',
                    updated_at = excluded.updated_at
                """;
            cmd.Parameters.AddWithValue("@gameId", gameId);
            cmd.Parameters.AddWithValue("@now", now);
            await cmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}
