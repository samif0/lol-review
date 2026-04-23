#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>CRUD for objective_prompts and prompt_answers tables.</summary>
public sealed class PromptsRepository : IPromptsRepository
{
    private readonly IDbConnectionFactory _factory;

    public PromptsRepository(IDbConnectionFactory factory) => _factory = factory;

    public async Task<long> CreatePromptAsync(long objectiveId, string questionText,
        string eventTag = "", string answerType = "yes_no", int sortOrder = 0)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objective_prompts
                (objective_id, question_text, event_tag, answer_type, sort_order)
            VALUES (@objectiveId, @questionText, @eventTag, @answerType, @sortOrder)
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        cmd.Parameters.AddWithValue("@questionText", questionText);
        cmd.Parameters.AddWithValue("@eventTag", eventTag);
        cmd.Parameters.AddWithValue("@answerType", answerType);
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetPromptsForObjectiveAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM objective_prompts
            WHERE objective_id = @objectiveId
            ORDER BY sort_order ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        return await ReadAllRowsAsync(cmd);
    }

    public async Task DeletePromptsForObjectiveAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var transaction = conn.BeginTransaction();

        using (var cmd1 = conn.CreateCommand())
        {
            cmd1.CommandText = """
                DELETE FROM prompt_answers WHERE prompt_id IN
                    (SELECT id FROM objective_prompts WHERE objective_id = @objectiveId)
                """;
            cmd1.Parameters.AddWithValue("@objectiveId", objectiveId);
            cmd1.Transaction = transaction;
            await cmd1.ExecuteNonQueryAsync();
        }

        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "DELETE FROM objective_prompts WHERE objective_id = @objectiveId";
            cmd2.Parameters.AddWithValue("@objectiveId", objectiveId);
            cmd2.Transaction = transaction;
            await cmd2.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();
    }

    public async Task SaveAnswerAsync(long gameId, long promptId, int answerValue,
        long? eventInstanceId = null, int? eventTimeSeconds = null)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO prompt_answers
                (game_id, prompt_id, event_instance_id, event_time_s, answer_value)
            VALUES (@gameId, @promptId, @eventInstanceId, @eventTimeS, @answerValue)
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@promptId", promptId);
        cmd.Parameters.AddWithValue("@eventInstanceId",
            eventInstanceId.HasValue ? eventInstanceId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@eventTimeS",
            eventTimeSeconds.HasValue ? eventTimeSeconds.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@answerValue", answerValue);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetAnswersForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM prompt_answers WHERE game_id = @gameId";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadAllRowsAsync(cmd);
    }

    public async Task<IReadOnlyList<Dictionary<string, object?>>> GetProgressionDataAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pa.game_id, pa.answer_value, pa.prompt_id, g.timestamp
            FROM prompt_answers pa
            JOIN objective_prompts op ON op.id = pa.prompt_id
            JOIN games g ON g.game_id = pa.game_id
            WHERE op.objective_id = @objectiveId
            ORDER BY g.timestamp ASC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
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
