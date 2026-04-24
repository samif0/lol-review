#nullable enable

using Microsoft.Data.Sqlite;

namespace Revu.Core.Data.Repositories;

/// <summary>
/// CRUD for <c>objective_prompts</c>, <c>prompt_answers</c>, and
/// <c>pre_game_draft_prompts</c>. See <see cref="IPromptsRepository"/>.
/// </summary>
public sealed class PromptsRepository : IPromptsRepository
{
    private readonly IDbConnectionFactory _factory;

    public PromptsRepository(IDbConnectionFactory factory) => _factory = factory;

    // ── Prompt CRUD ─────────────────────────────────────────────────

    public async Task<long> CreatePromptAsync(long objectiveId, string phase, string label, int sortOrder)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objective_prompts (objective_id, phase, label, sort_order, created_at)
            VALUES (@objectiveId, @phase, @label, @sortOrder, @createdAt)
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        cmd.Parameters.AddWithValue("@phase", ObjectivePhases.Normalize(phase));
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    public async Task UpdatePromptAsync(long promptId, string phase, string label, int sortOrder)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objective_prompts
            SET phase = @phase,
                label = @label,
                sort_order = @sortOrder
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@phase", ObjectivePhases.Normalize(phase));
        cmd.Parameters.AddWithValue("@label", label);
        cmd.Parameters.AddWithValue("@sortOrder", sortOrder);
        cmd.Parameters.AddWithValue("@id", promptId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeletePromptAsync(long promptId)
    {
        using var conn = _factory.CreateConnection();

        using (var deleteAnswers = conn.CreateCommand())
        {
            deleteAnswers.CommandText = "DELETE FROM prompt_answers WHERE prompt_id = @id";
            deleteAnswers.Parameters.AddWithValue("@id", promptId);
            await deleteAnswers.ExecuteNonQueryAsync();
        }

        using (var deleteDrafts = conn.CreateCommand())
        {
            deleteDrafts.CommandText = "DELETE FROM pre_game_draft_prompts WHERE prompt_id = @id";
            deleteDrafts.Parameters.AddWithValue("@id", promptId);
            await deleteDrafts.ExecuteNonQueryAsync();
        }

        using (var deletePrompt = conn.CreateCommand())
        {
            deletePrompt.CommandText = "DELETE FROM objective_prompts WHERE id = @id";
            deletePrompt.Parameters.AddWithValue("@id", promptId);
            await deletePrompt.ExecuteNonQueryAsync();
        }
    }

    public async Task<IReadOnlyList<ObjectivePrompt>> GetPromptsForObjectiveAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, objective_id, phase, label, sort_order, created_at
            FROM objective_prompts
            WHERE objective_id = @objectiveId
            ORDER BY sort_order ASC, id ASC
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);

        var results = new List<ObjectivePrompt>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ObjectivePrompt(
                Id: reader.GetInt64(0),
                ObjectiveId: reader.GetInt64(1),
                Phase: reader.IsDBNull(2) ? ObjectivePhases.InGame : ObjectivePhases.Normalize(reader.GetString(2)),
                Label: reader.IsDBNull(3) ? "" : reader.GetString(3),
                SortOrder: reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                CreatedAt: reader.IsDBNull(5) ? null : reader.GetInt64(5)));
        }
        return results;
    }

    // ── Rendered views for pre/post-game UI ─────────────────────────

    public async Task<IReadOnlyList<ActivePrompt>> GetActivePromptsForPhaseAsync(string phase, string? championName = null)
    {
        var normalized = ObjectivePhases.Normalize(phase);
        // Validated column selection — no dynamic SQL from user input.
        string practiceCol = normalized switch
        {
            ObjectivePhases.PreGame  => "practice_pregame",
            ObjectivePhases.PostGame => "practice_postgame",
            _                        => "practice_ingame",
        };

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        // Champion gate: inclusive of objectives with no champion rows (they
        // apply to all champions). NULL championName skips the clause.
        var championClause = string.IsNullOrEmpty(championName)
            ? ""
            : """
              AND (
                  NOT EXISTS (SELECT 1 FROM objective_champions oc WHERE oc.objective_id = o.id)
               OR EXISTS (SELECT 1 FROM objective_champions oc
                          WHERE oc.objective_id = o.id AND oc.champion_name = @championName)
              )
              """;
        cmd.CommandText = $"""
            SELECT op.id, op.objective_id, o.title, o.is_priority,
                   op.phase, op.label, op.sort_order
            FROM objective_prompts op
            JOIN objectives o ON o.id = op.objective_id
            WHERE o.status = 'active'
              AND o.{practiceCol} = 1
              AND op.phase = @phase
              {championClause}
            ORDER BY o.is_priority DESC, o.created_at ASC, o.id ASC,
                     op.sort_order ASC, op.id ASC
            """;
        cmd.Parameters.AddWithValue("@phase", normalized);
        if (!string.IsNullOrEmpty(championName))
        {
            cmd.Parameters.AddWithValue("@championName", championName);
        }

        var results = new List<ActivePrompt>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new ActivePrompt(
                PromptId: reader.GetInt64(0),
                ObjectiveId: reader.GetInt64(1),
                ObjectiveTitle: reader.IsDBNull(2) ? "" : reader.GetString(2),
                IsPriority: !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                Phase: reader.IsDBNull(4) ? normalized : ObjectivePhases.Normalize(reader.GetString(4)),
                Label: reader.IsDBNull(5) ? "" : reader.GetString(5),
                SortOrder: reader.IsDBNull(6) ? 0 : reader.GetInt32(6)));
        }
        return results;
    }

    // ── Answers ─────────────────────────────────────────────────────

    public async Task SaveAnswerAsync(long promptId, long gameId, string answerText)
    {
        using var conn = _factory.CreateConnection();

        // Empty text → delete the row so GetAnswersForGameAsync stays clean and
        // we don't store meaningless "" records.
        if (string.IsNullOrWhiteSpace(answerText))
        {
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.CommandText = "DELETE FROM prompt_answers WHERE prompt_id = @promptId AND game_id = @gameId";
            deleteCmd.Parameters.AddWithValue("@promptId", promptId);
            deleteCmd.Parameters.AddWithValue("@gameId", gameId);
            await deleteCmd.ExecuteNonQueryAsync();
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO prompt_answers (prompt_id, game_id, answer_text, updated_at)
            VALUES (@promptId, @gameId, @answerText, @updatedAt)
            ON CONFLICT(prompt_id, game_id) DO UPDATE SET
                answer_text = excluded.answer_text,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@promptId", promptId);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@answerText", answerText);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<PromptAnswer>> GetAnswersForGameAsync(long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT pa.prompt_id, op.objective_id, o.title, o.is_priority,
                   op.phase, op.label, pa.answer_text
            FROM prompt_answers pa
            JOIN objective_prompts op ON op.id = pa.prompt_id
            JOIN objectives o ON o.id = op.objective_id
            WHERE pa.game_id = @gameId
            ORDER BY o.is_priority DESC, o.created_at ASC, o.id ASC,
                     op.sort_order ASC, op.id ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);

        var results = new List<PromptAnswer>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new PromptAnswer(
                PromptId: reader.GetInt64(0),
                ObjectiveId: reader.GetInt64(1),
                ObjectiveTitle: reader.IsDBNull(2) ? "" : reader.GetString(2),
                IsPriority: !reader.IsDBNull(3) && reader.GetInt64(3) != 0,
                Phase: reader.IsDBNull(4) ? ObjectivePhases.InGame : ObjectivePhases.Normalize(reader.GetString(4)),
                Label: reader.IsDBNull(5) ? "" : reader.GetString(5),
                AnswerText: reader.IsDBNull(6) ? "" : reader.GetString(6)));
        }
        return results;
    }

    // ── Pre-game draft answers (before a game row exists) ───────────

    public async Task SaveDraftAnswerAsync(string sessionKey, long promptId, string answerText)
    {
        using var conn = _factory.CreateConnection();

        if (string.IsNullOrWhiteSpace(answerText))
        {
            using var deleteCmd = conn.CreateCommand();
            deleteCmd.CommandText = """
                DELETE FROM pre_game_draft_prompts
                WHERE session_key = @sessionKey AND prompt_id = @promptId
                """;
            deleteCmd.Parameters.AddWithValue("@sessionKey", sessionKey);
            deleteCmd.Parameters.AddWithValue("@promptId", promptId);
            await deleteCmd.ExecuteNonQueryAsync();
            return;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO pre_game_draft_prompts (session_key, prompt_id, answer_text, updated_at)
            VALUES (@sessionKey, @promptId, @answerText, @updatedAt)
            ON CONFLICT(session_key, prompt_id) DO UPDATE SET
                answer_text = excluded.answer_text,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("@sessionKey", sessionKey);
        cmd.Parameters.AddWithValue("@promptId", promptId);
        cmd.Parameters.AddWithValue("@answerText", answerText);
        cmd.Parameters.AddWithValue("@updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<IReadOnlyList<(long PromptId, string AnswerText)>> GetDraftAnswersAsync(string sessionKey)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT prompt_id, answer_text
            FROM pre_game_draft_prompts
            WHERE session_key = @sessionKey
            ORDER BY prompt_id ASC
            """;
        cmd.Parameters.AddWithValue("@sessionKey", sessionKey);

        var results = new List<(long, string)>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add((
                reader.GetInt64(0),
                reader.IsDBNull(1) ? "" : reader.GetString(1)));
        }
        return results;
    }

    public async Task PromotePreGameDraftsAsync(string sessionKey, long gameId)
    {
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using (var promoteCmd = conn.CreateCommand())
        {
            promoteCmd.Transaction = tx;
            promoteCmd.CommandText = """
                INSERT INTO prompt_answers (prompt_id, game_id, answer_text, updated_at)
                SELECT prompt_id, @gameId, answer_text, @updatedAt
                FROM pre_game_draft_prompts
                WHERE session_key = @sessionKey
                  AND LENGTH(TRIM(COALESCE(answer_text, ''))) > 0
                ON CONFLICT(prompt_id, game_id) DO UPDATE SET
                    answer_text = excluded.answer_text,
                    updated_at = excluded.updated_at
                """;
            promoteCmd.Parameters.AddWithValue("@sessionKey", sessionKey);
            promoteCmd.Parameters.AddWithValue("@gameId", gameId);
            promoteCmd.Parameters.AddWithValue("@updatedAt", now);
            await promoteCmd.ExecuteNonQueryAsync();
        }

        using (var clearCmd = conn.CreateCommand())
        {
            clearCmd.Transaction = tx;
            clearCmd.CommandText = "DELETE FROM pre_game_draft_prompts WHERE session_key = @sessionKey";
            clearCmd.Parameters.AddWithValue("@sessionKey", sessionKey);
            await clearCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }
}
