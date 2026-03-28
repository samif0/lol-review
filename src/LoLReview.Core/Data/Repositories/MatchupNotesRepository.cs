#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD for matchup_notes table.</summary>
public sealed class MatchupNotesRepository : IMatchupNotesRepository
{
    private readonly IDbConnectionFactory _factory;

    public MatchupNotesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string champion, string enemy, string note = "",
        int? helpful = null, long? gameId = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO matchup_notes
                (champion, enemy, note, helpful, game_id, created_at)
            VALUES (@champion, @enemy, @note, @helpful, @gameId, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@champion", champion);
        cmd.Parameters.AddWithValue("@enemy", enemy);
        cmd.Parameters.AddWithValue("@note", note);
        cmd.Parameters.AddWithValue("@helpful", helpful.HasValue ? helpful.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@gameId", gameId.HasValue ? gameId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<Dictionary<string, object?>?> GetForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM matchup_notes
            WHERE game_id = @gameId
            ORDER BY created_at DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadSingleRowAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetForMatchupAsync(string champion, string enemy)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM matchup_notes
            WHERE champion = @champion AND enemy = @enemy
            ORDER BY created_at DESC
            """;
        cmd.Parameters.AddWithValue("@champion", champion);
        cmd.Parameters.AddWithValue("@enemy", enemy);
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM matchup_notes ORDER BY created_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<long?> UpsertForGameAsync(long gameId, string champion, string enemy, string note)
    {
        var trimmedNote = note.Trim();
        if (string.IsNullOrWhiteSpace(trimmedNote))
        {
            await DeleteForGameAsync(gameId);
            return null;
        }

        using var conn = _factory.CreateConnection();

        using var existingCmd = conn.CreateCommand();
        existingCmd.CommandText = "SELECT id FROM matchup_notes WHERE game_id = @gameId LIMIT 1";
        existingCmd.Parameters.AddWithValue("@gameId", gameId);
        var existingId = await existingCmd.ExecuteScalarAsync();

        if (existingId is not null)
        {
            using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE matchup_notes
                SET champion = @champion,
                    enemy = @enemy,
                    note = @note,
                    created_at = @createdAt
                WHERE id = @id
                """;
            updateCmd.Parameters.AddWithValue("@champion", champion);
            updateCmd.Parameters.AddWithValue("@enemy", enemy);
            updateCmd.Parameters.AddWithValue("@note", trimmedNote);
            updateCmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            updateCmd.Parameters.AddWithValue("@id", Convert.ToInt64(existingId));
            await updateCmd.ExecuteNonQueryAsync();
            return Convert.ToInt64(existingId);
        }

        return await CreateAsync(champion, enemy, trimmedNote, gameId: gameId);
    }

    public async Task DeleteForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM matchup_notes WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateHelpfulAsync(long noteId, int helpful)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE matchup_notes SET helpful = @helpful WHERE id = @id";
        cmd.Parameters.AddWithValue("@helpful", helpful);
        cmd.Parameters.AddWithValue("@id", noteId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<HelpfulnessStats> GetHelpfulnessStatsAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN helpful = 1 THEN 1 ELSE 0 END) as helpful_count,
                SUM(CASE WHEN helpful = 0 THEN 1 ELSE 0 END) as unhelpful_count,
                SUM(CASE WHEN helpful IS NULL THEN 1 ELSE 0 END) as unrated_count
            FROM matchup_notes
            """;

        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new HelpfulnessStats(
                Total: reader.IsDBNull(reader.GetOrdinal("total")) ? 0 : reader.GetInt32(reader.GetOrdinal("total")),
                HelpfulCount: reader.IsDBNull(reader.GetOrdinal("helpful_count")) ? 0 : reader.GetInt32(reader.GetOrdinal("helpful_count")),
                UnhelpfulCount: reader.IsDBNull(reader.GetOrdinal("unhelpful_count")) ? 0 : reader.GetInt32(reader.GetOrdinal("unhelpful_count")),
                UnratedCount: reader.IsDBNull(reader.GetOrdinal("unrated_count")) ? 0 : reader.GetInt32(reader.GetOrdinal("unrated_count")));
        }

        return new HelpfulnessStats(0, 0, 0, 0);
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> ReadAllRowsAsync(SqliteCommand cmd)
    {
        var results = new List<Dictionary<string, object?>>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadRow(reader));
        }
        return results;
    }

    private static async Task<Dictionary<string, object?>?> ReadSingleRowAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadRow(reader) : null;
    }

    private static Dictionary<string, object?> ReadRow(SqliteDataReader reader)
    {
        var dict = new Dictionary<string, object?>(reader.FieldCount);
        for (int i = 0; i < reader.FieldCount; i++)
        {
            dict[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        }
        return dict;
    }
}
