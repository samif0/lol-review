#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD + scoring for the objectives and game_objectives tables.</summary>
public sealed class ObjectivesRepository : IObjectivesRepository
{
    private readonly IDbConnectionFactory _factory;

    public ObjectivesRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreateAsync(string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "", string phase = ObjectivePhases.InGame)
    {
        using var conn = _factory.CreateConnection();
        var shouldBePriority = await ShouldNewObjectiveBecomePriorityAsync(conn);
        var normalizedPhase = ObjectivePhases.Normalize(phase);

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objectives
                (title, skill_area, type, phase, completion_criteria, description,
                 status, is_priority, score, game_count, created_at)
            VALUES (@title, @skillArea, @type, @phase, @completionCriteria, @description,
                    'active', @isPriority, 0, 0, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@phase", normalizedPhase);
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@isPriority", shouldBePriority ? 1 : 0);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<ObjectiveSummary>> GetAllAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives ORDER BY status ASC, is_priority DESC, type ASC, created_at DESC";
        return await ReadObjectivesAsync(cmd);
    }

    public async Task<IReadOnlyList<ObjectiveSummary>> GetActiveAsync()
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE status = 'active' ORDER BY is_priority DESC, type ASC, created_at ASC";
        return await ReadObjectivesAsync(cmd);
    }

    public async Task<ObjectiveSummary?> GetPriorityAsync()
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
            var row = await ReadSingleObjectiveAsync(cmd);
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
        return await ReadSingleObjectiveAsync(fallbackCmd);
    }

    public async Task<ObjectiveSummary?> GetAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM objectives WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", objectiveId);
        return await ReadSingleObjectiveAsync(cmd);
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

    public async Task UpdateAsync(long objectiveId, string title, string skillArea = "", string type = "primary",
        string completionCriteria = "", string description = "", string phase = ObjectivePhases.InGame)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET title = @title,
                skill_area = @skillArea,
                type = @type,
                phase = @phase,
                completion_criteria = @completionCriteria,
                description = @description
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@phase", ObjectivePhases.Normalize(phase));
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePhaseAsync(long objectiveId, string phase)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE objectives SET phase = @phase WHERE id = @id";
        cmd.Parameters.AddWithValue("@phase", ObjectivePhases.Normalize(phase));
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
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
        using var tx = conn.BeginTransaction();

        var newScoreContribution = practiced ? 2 : 0;
        var existingPracticed = default(bool?);

        using (var existingCmd = conn.CreateCommand())
        {
            existingCmd.Transaction = tx;
            existingCmd.CommandText = """
                SELECT practiced
                FROM game_objectives
                WHERE game_id = @gameId AND objective_id = @objectiveId
                LIMIT 1
                """;
            existingCmd.Parameters.AddWithValue("@gameId", gameId);
            existingCmd.Parameters.AddWithValue("@objectiveId", objectiveId);
            var existing = await existingCmd.ExecuteScalarAsync();
            if (existing is not null && existing != DBNull.Value)
            {
                existingPracticed = Convert.ToInt32(existing) != 0;
            }
        }

        using (var upsertCmd = conn.CreateCommand())
        {
            upsertCmd.Transaction = tx;
            upsertCmd.CommandText = """
                INSERT OR REPLACE INTO game_objectives
                    (game_id, objective_id, practiced, execution_note)
                VALUES (@gameId, @objectiveId, @practiced, @executionNote)
                """;
            upsertCmd.Parameters.AddWithValue("@gameId", gameId);
            upsertCmd.Parameters.AddWithValue("@objectiveId", objectiveId);
            upsertCmd.Parameters.AddWithValue("@practiced", practiced ? 1 : 0);
            upsertCmd.Parameters.AddWithValue("@executionNote", executionNote);
            await upsertCmd.ExecuteNonQueryAsync();
        }

        var previousScoreContribution = existingPracticed == true ? 2 : 0;
        var scoreDelta = newScoreContribution - previousScoreContribution;
        var gameCountDelta = existingPracticed.HasValue ? 0 : 1;

        if (scoreDelta != 0 || gameCountDelta != 0)
        {
            using var objectiveCmd = conn.CreateCommand();
            objectiveCmd.Transaction = tx;
            objectiveCmd.CommandText = """
                UPDATE objectives
                SET score = MAX(0, score + @scoreDelta),
                    game_count = MAX(0, game_count + @gameCountDelta)
                WHERE id = @objectiveId
                """;
            objectiveCmd.Parameters.AddWithValue("@scoreDelta", scoreDelta);
            objectiveCmd.Parameters.AddWithValue("@gameCountDelta", gameCountDelta);
            objectiveCmd.Parameters.AddWithValue("@objectiveId", objectiveId);
            await objectiveCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<ObjectiveGameEntry>> GetGamesForObjectiveAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT go.game_id, go.objective_id, go.practiced, go.execution_note,
                   g.champion_name, g.win, g.timestamp,
                   g.kills, g.deaths, g.assists, g.kda_ratio,
                   COALESCE(g.review_notes, '') AS review_notes,
                   CASE WHEN g.mental_rating IS NOT NULL THEN 1 ELSE 0 END AS has_review
            FROM game_objectives go
            JOIN games g ON g.game_id = go.game_id
            WHERE go.objective_id = @objectiveId
            ORDER BY g.timestamp DESC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);

        var results = new List<ObjectiveGameEntry>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ObjectiveGameEntry(
                GameId: reader.GetInt64(0),
                ObjectiveId: reader.GetInt64(1),
                Practiced: !reader.IsDBNull(2) && reader.GetInt64(2) != 0,
                ExecutionNote: reader.IsDBNull(3) ? "" : reader.GetString(3),
                ChampionName: reader.IsDBNull(4) ? "" : reader.GetString(4),
                Win: !reader.IsDBNull(5) && reader.GetInt64(5) != 0,
                Timestamp: reader.IsDBNull(6) ? 0 : reader.GetInt64(6),
                Kills: reader.IsDBNull(7) ? 0 : reader.GetDouble(7),
                Deaths: reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                Assists: reader.IsDBNull(9) ? 0 : reader.GetDouble(9),
                KdaRatio: reader.IsDBNull(10) ? 0 : reader.GetDouble(10),
                ReviewNotes: reader.IsDBNull(11) ? "" : reader.GetString(11),
                HasReview: !reader.IsDBNull(12) && reader.GetInt64(12) != 0));
        }

        return results;
    }

    public async Task<IReadOnlyList<int>> GetScoreHistoryAsync(long objectiveId, int limit = 20)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // Fetch per-game score contributions ordered oldest-first, capped to `limit` most-recent games.
        // Score contribution: +3 practiced, +1 unpracticed (win), -1 unpracticed (loss).
        // We reconstruct cumulative scores in C# from the raw contributions.
        cmd.CommandText = """
            SELECT go.practiced, g.win
            FROM game_objectives go
            JOIN games g ON g.game_id = go.game_id
            WHERE go.objective_id = @objectiveId
            ORDER BY g.timestamp DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        cmd.Parameters.AddWithValue("@limit", limit);

        var contributions = new List<int>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var practiced = !reader.IsDBNull(0) && reader.GetInt64(0) != 0;
            var win = !reader.IsDBNull(1) && reader.GetInt64(1) != 0;
            contributions.Add(practiced ? 3 : win ? 1 : -1);
        }

        // Contributions are newest-first; reverse to oldest-first for sparkline
        contributions.Reverse();

        // Build cumulative series starting from (currentScore - sum of contributions)
        // That way the last point = current score
        var total = contributions.Sum();
        // We only have the delta; normalise so the line shows relative trend from 0
        var cumulative = new List<int>(contributions.Count);
        var running = 0;
        foreach (var c in contributions)
        {
            running += c;
            cumulative.Add(running);
        }
        return cumulative;
    }

    public async Task<IReadOnlyList<GameObjectiveRecord>> GetGameObjectivesAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT go.game_id, go.objective_id, go.practiced, go.execution_note,
                   o.title, o.completion_criteria, o.type, o.is_priority, o.phase
            FROM game_objectives go
            JOIN objectives o ON o.id = go.objective_id
            WHERE go.game_id = @gameId
            ORDER BY o.is_priority DESC, o.created_at ASC, o.id ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadGameObjectivesAsync(cmd);
    }

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

    private static async Task<IReadOnlyList<ObjectiveSummary>> ReadObjectivesAsync(SqliteCommand cmd)
    {
        var results = new List<ObjectiveSummary>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadObjective(reader));
        }

        return results;
    }

    private static async Task<ObjectiveSummary?> ReadSingleObjectiveAsync(SqliteCommand cmd)
    {
        using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync() ? ReadObjective(reader) : null;
    }

    private static async Task<IReadOnlyList<GameObjectiveRecord>> ReadGameObjectivesAsync(SqliteCommand cmd)
    {
        var results = new List<GameObjectiveRecord>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new GameObjectiveRecord(
                GameId: reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
                ObjectiveId: reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
                Practiced: !reader.IsDBNull(2) && reader.GetInt64(2) != 0,
                ExecutionNote: reader.IsDBNull(3) ? "" : reader.GetString(3),
                Title: reader.IsDBNull(4) ? "" : reader.GetString(4),
                CompletionCriteria: reader.IsDBNull(5) ? "" : reader.GetString(5),
                Type: reader.IsDBNull(6) ? "primary" : reader.GetString(6),
                IsPriority: !reader.IsDBNull(7) && reader.GetInt64(7) != 0,
                Phase: reader.IsDBNull(8) ? ObjectivePhases.InGame : ObjectivePhases.Normalize(reader.GetString(8))));
        }

        return results;
    }

    private static ObjectiveSummary ReadObjective(SqliteDataReader reader)
    {
        return new ObjectiveSummary(
            Id: reader.IsDBNull(reader.GetOrdinal("id")) ? 0 : reader.GetInt64(reader.GetOrdinal("id")),
            Title: reader.IsDBNull(reader.GetOrdinal("title")) ? "" : reader.GetString(reader.GetOrdinal("title")),
            SkillArea: reader.IsDBNull(reader.GetOrdinal("skill_area")) ? "" : reader.GetString(reader.GetOrdinal("skill_area")),
            Type: reader.IsDBNull(reader.GetOrdinal("type")) ? "primary" : reader.GetString(reader.GetOrdinal("type")),
            CompletionCriteria: reader.IsDBNull(reader.GetOrdinal("completion_criteria")) ? "" : reader.GetString(reader.GetOrdinal("completion_criteria")),
            Description: reader.IsDBNull(reader.GetOrdinal("description")) ? "" : reader.GetString(reader.GetOrdinal("description")),
            Phase: reader.IsDBNull(reader.GetOrdinal("phase")) ? ObjectivePhases.InGame : ObjectivePhases.Normalize(reader.GetString(reader.GetOrdinal("phase"))),
            Status: reader.IsDBNull(reader.GetOrdinal("status")) ? "active" : reader.GetString(reader.GetOrdinal("status")),
            IsPriority: !reader.IsDBNull(reader.GetOrdinal("is_priority")) && reader.GetInt64(reader.GetOrdinal("is_priority")) != 0,
            Score: reader.IsDBNull(reader.GetOrdinal("score")) ? 0 : reader.GetInt32(reader.GetOrdinal("score")),
            GameCount: reader.IsDBNull(reader.GetOrdinal("game_count")) ? 0 : reader.GetInt32(reader.GetOrdinal("game_count")),
            CreatedAt: reader.IsDBNull(reader.GetOrdinal("created_at")) ? null : reader.GetInt64(reader.GetOrdinal("created_at")),
            CompletedAt: reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : reader.GetInt64(reader.GetOrdinal("completed_at")));
    }
}
