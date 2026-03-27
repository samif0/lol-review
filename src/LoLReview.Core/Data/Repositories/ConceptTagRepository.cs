#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for concept_tags and game_concept_tags tables.</summary>
public sealed class ConceptTagRepository : IConceptTagRepository
{
    private readonly IDbConnectionFactory _factory;

    public ConceptTagRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM concept_tags ORDER BY polarity DESC, name ASC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<long> CreateAsync(string name, string polarity = "neutral", string color = "")
    {
        if (string.IsNullOrEmpty(color))
        {
            color = polarity switch
            {
                "positive" => "#22c55e",
                "negative" => "#ef4444",
                _ => "#3b82f6",
            };
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR IGNORE INTO concept_tags (name, polarity, color) VALUES (@name, @polarity, @color)";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@polarity", polarity);
        cmd.Parameters.AddWithValue("@color", color);
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<long>> GetIdsForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tag_id FROM game_concept_tags WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<long>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetInt64(0));
        }
        return results;
    }

    public async Task SetForGameAsync(long gameId, IReadOnlyList<long> tagIds)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var delCmd = conn.CreateCommand())
        {
            delCmd.CommandText = "DELETE FROM game_concept_tags WHERE game_id = @gameId";
            delCmd.Parameters.AddWithValue("@gameId", gameId);
            delCmd.Transaction = transaction;
            await delCmd.ExecuteNonQueryAsync();
        }

        using (var insCmd = conn.CreateCommand())
        {
            insCmd.CommandText = "INSERT OR IGNORE INTO game_concept_tags (game_id, tag_id) VALUES (@gameId, @tagId)";
            insCmd.Transaction = transaction;
            var pGameId = insCmd.Parameters.Add("@gameId", SqliteType.Integer);
            var pTagId = insCmd.Parameters.Add("@tagId", SqliteType.Integer);

            foreach (var tagId in tagIds)
            {
                pGameId.Value = gameId;
                pTagId.Value = tagId;
                await insCmd.ExecuteNonQueryAsync();
            }
        }

        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<TagFrequency>> GetTagFrequencyAsync(int limit = 20)
    {
        using var conn = _factory.CreateConnection();

        // Get total distinct game count
        using var countCmd = conn.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(DISTINCT game_id) FROM game_concept_tags";
        var totalGames = Convert.ToInt64(await countCmd.ExecuteScalarAsync());
        if (totalGames == 0)
            return [];

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT ct.name, ct.polarity, ct.color,
                   COUNT(*) as count,
                   ROUND(100.0 * COUNT(*) / @totalGames, 1) as game_pct
            FROM game_concept_tags gct
            JOIN concept_tags ct ON ct.id = gct.tag_id
            GROUP BY gct.tag_id
            ORDER BY COUNT(*) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@totalGames", totalGames);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<TagFrequency>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new TagFrequency(
                Name: reader.GetString(reader.GetOrdinal("name")),
                Polarity: reader.GetString(reader.GetOrdinal("polarity")),
                Color: reader.GetString(reader.GetOrdinal("color")),
                Count: reader.GetInt32(reader.GetOrdinal("count")),
                GamePercent: reader.GetDouble(reader.GetOrdinal("game_pct"))));
        }
        return results;
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAllRowsAsync(SqliteCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var dict = new Dictionary<string, object?>(reader.FieldCount);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            results.Add(dict);
        }
        return results;
    }
}
