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
        var shouldBePriority = await ShouldNewObjectiveBecomePriorityAsync(conn);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objectives
                (title, skill_area, type, completion_criteria, description,
                 status, is_priority, score, game_count, created_at)
            VALUES (@title, @skillArea, @type, @completionCriteria, @description,
                    'active', @isPriority, 0, 0, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@isPriority", shouldBePriority ? 1 : 0);
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
        cmd.CommandText = "SELECT * FROM objectives ORDER BY status ASC, is_priority DESC, type ASC, created_at DESC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE status = 'active' ORDER BY is_priority DESC, type ASC, created_at ASC";
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<Dictionary<string, object?>?> GetPriorityAsync()
    {
        using var conn = _factory.CreateConnection();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT *
                FROM objectives
                WHERE status = 'active' AND is_priority = 1
                ORDER BY created_at ASC, id ASC
                LIMIT 1
                """;
            var row = await ReadSingleRowAsync(cmd);
            if (row is not null)
            {
                return row;
            }
        }

        await EnsurePriorityObjectiveAsync(conn);

        using var fallbackCmd = conn.CreateCommand();
        fallbackCmd.CommandText = """
            SELECT *
            FROM objectives
            WHERE status = 'active'
            ORDER BY is_priority DESC, type ASC, created_at ASC
            LIMIT 1
            """;
        return await ReadSingleRowAsync(fallbackCmd);
    }

    public async Task<Dictionary<string, object?>?> GetAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", objectiveId);
        return await ReadSingleRowAsync(cmd);
    }

    public async Task SetPriorityAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "UPDATE objectives SET is_priority = 0 WHERE status = 'active'";
            await clearCmd.ExecuteNonQueryAsync();
        }

        using (var setCmd = conn.CreateCommand())
        {
            setCmd.Transaction = tx;
            setCmd.CommandText = """
                UPDATE objectives
                SET is_priority = 1
                WHERE id = @id AND status = 'active'
                """;
            setCmd.Parameters.AddWithValue("@id", objectiveId);
            await setCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
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
        cmd.CommandText = "UPDATE objectives SET status = 'completed', is_priority = 0, completed_at = @completedAt WHERE id = @id";
        cmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
        await EnsurePriorityObjectiveAsync(conn);
    }

    public async Task DeleteAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();

        using (var cmd1 = conn.CreateCommand())
        {
            cmd1.CommandText = "DELETE FROM game_objectives WHERE objective_id = @id";
            cmd1.Parameters.AddWithValue("@id", objectiveId);
            await cmd1.ExecuteNonQueryAsync();
        }

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "DELETE FROM objectives WHERE id = @id";
            cmd2.Parameters.AddWithValue("@id", objectiveId);
            await cmd2.ExecuteNonQueryAsync();
        }

        await EnsurePriorityObjectiveAsync(conn);
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
            SELECT go.*, o.title, o.completion_criteria, o.type, o.is_priority
            FROM game_objectives go
            JOIN objectives o ON o.id = go.objective_id
            WHERE go.game_id = @gameId
            ORDER BY o.is_priority DESC, o.created_at ASC, o.id ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadAllRowsAsync(cmd);
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static async Task<bool> ShouldNewObjectiveBecomePriorityAsync(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM objectives
            WHERE status = 'active' AND is_priority = 1
            """;
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync() ?? 0);
        return count == 0;
    }

    private static async Task EnsurePriorityObjectiveAsync(SqliteConnection conn)
    {
        using (var checkCmd = conn.CreateCommand())
        {
            checkCmd.CommandText = """
                SELECT COUNT(*)
                FROM objectives
                WHERE status = 'active' AND is_priority = 1
                """;
            var existingPriorityCount = Convert.ToInt32(await checkCmd.ExecuteScalarAsync() ?? 0);
            if (existingPriorityCount > 0)
            {
                return;
            }
        }

        using var findCmd = conn.CreateCommand();
        findCmd.CommandText = """
            SELECT id
            FROM objectives
            WHERE status = 'active'
            ORDER BY type ASC, created_at ASC, id ASC
            LIMIT 1
            """;
        var nextId = await findCmd.ExecuteScalarAsync();
        if (nextId is null)
        {
            return;
        }

        using var promoteCmd = conn.CreateCommand();
        promoteCmd.CommandText = "UPDATE objectives SET is_priority = 1 WHERE id = @id";
        promoteCmd.Parameters.AddWithValue("@id", Convert.ToInt64(nextId));
        await promoteCmd.ExecuteNonQueryAsync();
    }

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
