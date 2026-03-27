#nullable enable

using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Data.Repositories;

/// <summary>CRUD + scoring for the objectives and game_objectives tables.</summary>
public sealed class ObjectivesRepository : IObjectivesRepository
{
    private readonly IDbConnectionFactory _factory;

    public ObjectivesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objectives
                (title, skill_area, type, completion_criteria, description,
                 status, score, game_count, created_at)
            VALUES (@title, @skillArea, @type, @completionCriteria, @description,
                    'active', 0, 0, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives ORDER BY status ASC, type ASC, created_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE status = 'active' ORDER BY type ASC, created_at ASC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<Dictionary<string, object?>?> GetAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", objectiveId);
        return await ReadSingleRowAsync(cmd);
    }

    public async Task UpdateScoreAsync(long objectiveId, bool win)
    {
        int delta = win ? 2 : -1;
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET score = MAX(0, score + @delta), game_count = game_count + 1 WHERE id = @id";
        cmd.Parameters.AddWithValue("@delta", delta);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task MarkCompleteAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET status = 'completed', completed_at = @completedAt WHERE id = @id";
        cmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();

        using var cmd1 = conn.CreateCommand();
        cmd1.CommandText = "DELETE FROM game_objectives WHERE objective_id = @id";
        cmd1.Parameters.AddWithValue("@id", objectiveId);
        await cmd1.ExecuteNonQueryAsync();

        using var cmd2 = conn.CreateCommand();
        cmd2.CommandText = "DELETE FROM objectives WHERE id = @id";
        cmd2.Parameters.AddWithValue("@id", objectiveId);
        await cmd2.ExecuteNonQueryAsync();
    }

    public async Task RecordGameAsync(long gameId, long objectiveId, bool practiced, string executionNote = "")
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO game_objectives
                (game_id, objective_id, practiced, execution_note)
            VALUES (@gameId, @objectiveId, @practiced, @executionNote)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        cmd.Parameters.AddWithValue("@practiced", practiced ? 1 : 0);
        cmd.Parameters.AddWithValue("@executionNote", executionNote);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetGameObjectivesAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT go.*, o.title, o.completion_criteria, o.type
            FROM game_objectives go
            JOIN objectives o ON o.id = go.objective_id
            WHERE go.game_id = @gameId
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadAllRowsAsync(cmd);
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
