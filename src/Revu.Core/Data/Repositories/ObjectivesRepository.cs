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
        // Route through the multi-phase method so legacy callers stay working.
        // Map the single phase string to exactly one bool so existing behavior
        // is preserved.
        var normalized = ObjectivePhases.Normalize(phase);
        var pre  = string.Equals(normalized, ObjectivePhases.PreGame,  StringComparison.OrdinalIgnoreCase);
        var ing  = string.Equals(normalized, ObjectivePhases.InGame,   StringComparison.OrdinalIgnoreCase);
        var post = string.Equals(normalized, ObjectivePhases.PostGame, StringComparison.OrdinalIgnoreCase);
        // Defensive: if Normalize returned something unexpected, default to in-game.
        if (!pre && !ing && !post) ing = true;

        return await CreateWithPhasesAsync(title, skillArea, type, completionCriteria, description, pre, ing, post);
    }

    public async Task<long> CreateWithPhasesAsync(string title, string skillArea, string type,
        string completionCriteria, string description,
        bool practicePre, bool practiceIn, bool practicePost)
        => await CreateWithPhasesAndTargetAsync(
            title, skillArea, type, completionCriteria, description,
            practicePre, practiceIn, practicePost, targetGameCount: 0);

    /// <summary>
    /// v2.17.7: full create that also accepts a <paramref name="targetGameCount"/>
    /// for mini objectives. Use 0 for primary (no target).
    /// </summary>
    public async Task<long> CreateWithPhasesAndTargetAsync(string title, string skillArea, string type,
        string completionCriteria, string description,
        bool practicePre, bool practiceIn, bool practicePost,
        int targetGameCount)
    {
        using var conn = _factory.CreateConnection();
        var shouldBePriority = await ShouldNewObjectiveBecomePriorityAsync(conn);
        // Legacy phase column is set to the first true bool (pre→in→post).
        // Readers that haven't migrated to the bools still see something sane.
        var legacyPhase = practicePre  ? ObjectivePhases.PreGame
                        : practiceIn   ? ObjectivePhases.InGame
                        : practicePost ? ObjectivePhases.PostGame
                        : ObjectivePhases.InGame;

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO objectives
                (title, skill_area, type, phase, completion_criteria, description,
                 status, is_priority, score, game_count, target_game_count, created_at,
                 practice_pregame, practice_ingame, practice_postgame)
            VALUES (@title, @skillArea, @type, @phase, @completionCriteria, @description,
                    'active', @isPriority, 0, 0, @targetGameCount, @createdAt,
                    @practicePre, @practiceIn, @practicePost)
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@phase", legacyPhase);
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@isPriority", shouldBePriority ? 1 : 0);
        cmd.Parameters.AddWithValue("@targetGameCount", Math.Max(0, targetGameCount));
        cmd.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@practicePre", practicePre ? 1 : 0);
        cmd.Parameters.AddWithValue("@practiceIn", practiceIn ? 1 : 0);
        cmd.Parameters.AddWithValue("@practicePost", practicePost ? 1 : 0);
        await cmd.ExecuteNonQueryAsync();

        using var idCmd = conn.CreateCommand();
        idCmd.CommandText = "SELECT last_insert_rowid()";
        return (long)(await idCmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// v2.17.7: update the target game count for a mini objective. Pass 0 to
    /// clear the target.
    /// </summary>
    public async Task UpdateTargetGameCountAsync(long objectiveId, int targetGameCount)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET target_game_count = @targetGameCount
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@targetGameCount", Math.Max(0, targetGameCount));
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// v2.18 (schema v5): set or clear the structured criterion. Pass an empty
    /// metric to clear (the objective falls back to free-text criteria only).
    /// </summary>
    public async Task UpdateCriteriaAsync(long objectiveId, string criteriaMetric, string criteriaOp, double criteriaValue)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET criteria_metric = @metric,
                criteria_op = @op,
                criteria_value = @value
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@metric", criteriaMetric?.Trim() ?? "");
        cmd.Parameters.AddWithValue("@op", criteriaOp == "<=" ? "<=" : ">=");
        cmd.Parameters.AddWithValue("@value", criteriaValue);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// v2.18 (schema v5): stamp the structured-criterion outcome for one game.
    /// Only touches existing game_objectives rows — evaluation never creates a
    /// practice record on its own.
    /// </summary>
    public async Task SetCriteriaMetAsync(long gameId, long objectiveId, bool met)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE game_objectives
            SET criteria_met = @met
            WHERE game_id = @gameId AND objective_id = @objectiveId
            """;
        cmd.Parameters.AddWithValue("@met", met ? 1 : 0);
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// v2.18 (schema v5): per-objective criteria hit-rate over evaluated games.
    /// Returns (hits, evaluated) — evaluated counts only games where the
    /// criterion actually ran (criteria_met not null) on visible games.
    /// </summary>
    public async Task<(int Hits, int Evaluated)> GetCriteriaHitRateAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(CASE WHEN go.criteria_met = 1 THEN 1 ELSE 0 END), 0),
                   COUNT(go.criteria_met)
            FROM game_objectives go
            JOIN games g ON g.game_id = go.game_id
            WHERE go.objective_id = @objectiveId
              AND go.criteria_met IS NOT NULL
              AND COALESCE(g.is_hidden, 0) = 0
            """;
        cmd.Parameters.AddWithValue("@objectiveId", objectiveId);
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return (reader.GetInt32(0), reader.GetInt32(1));
        }
        return (0, 0);
    }

    /// <summary>
    /// v2.18 (schema v6 build): lowest criteria hit rate among active
    /// objectives, for the pre-game intent card. Null until the data gate is
    /// met: ≥ minEvaluatedTotal evaluated rows overall, ≥3 per objective.
    /// </summary>
    public async Task<ObjectiveAdherenceSummary?> GetLowestCriteriaAdherenceAsync(int minEvaluatedTotal = 10)
    {
        using var conn = _factory.CreateConnection();

        using (var gateCmd = conn.CreateCommand())
        {
            gateCmd.CommandText = """
                SELECT COUNT(go.criteria_met)
                FROM game_objectives go
                JOIN objectives o ON o.id = go.objective_id
                JOIN games g ON g.game_id = go.game_id
                WHERE go.criteria_met IS NOT NULL
                  AND o.status = 'active'
                  AND COALESCE(g.is_hidden, 0) = 0
                """;
            var total = Convert.ToInt32(await gateCmd.ExecuteScalarAsync() ?? 0);
            if (total < minEvaluatedTotal) return null;
        }

        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.title, o.completion_criteria,
                   COALESCE(SUM(CASE WHEN go.criteria_met = 1 THEN 1 ELSE 0 END), 0) AS hits,
                   COUNT(go.criteria_met) AS evaluated
            FROM game_objectives go
            JOIN objectives o ON o.id = go.objective_id
            JOIN games g ON g.game_id = go.game_id
            WHERE go.criteria_met IS NOT NULL
              AND o.status = 'active'
              AND COALESCE(g.is_hidden, 0) = 0
            GROUP BY o.id
            HAVING COUNT(go.criteria_met) >= 3
            ORDER BY CAST(SUM(CASE WHEN go.criteria_met = 1 THEN 1 ELSE 0 END) AS REAL) / COUNT(go.criteria_met) ASC,
                     COUNT(go.criteria_met) DESC
            LIMIT 1
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new ObjectiveAdherenceSummary(
                ObjectiveId: reader.GetInt64(0),
                Title: reader.IsDBNull(1) ? "" : reader.GetString(1),
                CompletionCriteria: reader.IsDBNull(2) ? "" : reader.GetString(2),
                Hits: reader.GetInt32(3),
                Evaluated: reader.GetInt32(4));
        }
        return null;
    }

    /// <summary>
    /// v2.18 (F2): set the game-phase focus used to match auto-clips to this
    /// objective. '' = auto-infer from title. See <see cref="ObjectiveFocusPhases"/>.
    /// </summary>
    public async Task UpdateFocusPhaseAsync(long objectiveId, string focusPhase)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET focus_phase = @focusPhase
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@focusPhase", ObjectiveFocusPhases.Normalize(focusPhase));
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// v2.17.7: archive any active mini objectives whose game_count has reached
    /// their target. Called after each game is added to game_objectives so the
    /// focus list stays current without manual cleanup.
    /// </summary>
    public async Task<IReadOnlyList<long>> ArchiveCompletedMiniObjectivesAsync()
    {
        using var conn = _factory.CreateConnection();

        // Collect ids first so we can return them (the UI may want to celebrate).
        var ids = new List<long>();
        using (var selectCmd = conn.CreateCommand())
        {
            selectCmd.CommandText = """
                SELECT id FROM objectives
                WHERE status = 'active'
                  AND LOWER(COALESCE(type, '')) = 'mini'
                  AND COALESCE(target_game_count, 0) > 0
                  AND COALESCE(game_count, 0) >= COALESCE(target_game_count, 0)
                """;
            using var reader = await selectCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        if (ids.Count == 0)
        {
            return ids;
        }

        using (var archiveCmd = conn.CreateCommand())
        {
            archiveCmd.CommandText = """
                UPDATE objectives
                SET status = 'completed',
                    is_priority = 0,
                    completed_at = @completedAt
                WHERE status = 'active'
                  AND LOWER(COALESCE(type, '')) = 'mini'
                  AND COALESCE(target_game_count, 0) > 0
                  AND COALESCE(game_count, 0) >= COALESCE(target_game_count, 0)
                """;
            archiveCmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await archiveCmd.ExecuteNonQueryAsync();
        }

        await EnsurePriorityObjectiveAsync(conn);
        return ids;
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

    public async Task<IReadOnlyList<ObjectiveSummary>> GetActiveByPhaseAsync(string phase, string? championName = null)
    {
        var normalized = ObjectivePhases.Normalize(phase);
        // Map to the specific bool column. No dynamic SQL — we validate the
        // column name explicitly to prevent any injection surface.
        string column = normalized switch
        {
            ObjectivePhases.PreGame  => "practice_pregame",
            ObjectivePhases.PostGame => "practice_postgame",
            _                        => "practice_ingame",
        };

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        // Champion filter: an objective passes when either (a) it has no rows
        // in objective_champions (applies to all champions), or (b) it has a
        // row for the given champion. NULL championName disables the filter.
        if (string.IsNullOrEmpty(championName))
        {
            cmd.CommandText = $"""
                SELECT * FROM objectives
                WHERE status = 'active' AND {column} = 1
                ORDER BY is_priority DESC, type ASC, created_at ASC
                """;
        }
        else
        {
            cmd.CommandText = $"""
                SELECT o.* FROM objectives o
                WHERE o.status = 'active' AND o.{column} = 1
                  AND (
                        NOT EXISTS (SELECT 1 FROM objective_champions oc WHERE oc.objective_id = o.id)
                     OR EXISTS (SELECT 1 FROM objective_champions oc
                                WHERE oc.objective_id = o.id AND oc.champion_name = @championName)
                  )
                ORDER BY o.is_priority DESC, o.type ASC, o.created_at ASC
                """;
            cmd.Parameters.AddWithValue("@championName", championName);
        }
        return await ReadObjectivesAsync(cmd);
    }

    // ── v2.15.0 champion gating ─────────────────────────────────────

    public async Task<IReadOnlyList<string>> GetChampionsForObjectiveAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT champion_name FROM objective_champions
            WHERE objective_id = @id
            ORDER BY champion_name ASC
            """;
        cmd.Parameters.AddWithValue("@id", objectiveId);
        var results = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
    }

    public async Task SetChampionsForObjectiveAsync(long objectiveId, IReadOnlyList<string> champions)
    {
        // Diff-save: compute current set, then add/remove only the delta.
        // Keeps PKs stable + avoids unnecessary churn.
        using var conn = _factory.CreateConnection();
        using var tx = conn.BeginTransaction();

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var readCmd = conn.CreateCommand())
        {
            readCmd.Transaction = tx;
            readCmd.CommandText = "SELECT champion_name FROM objective_champions WHERE objective_id = @id";
            readCmd.Parameters.AddWithValue("@id", objectiveId);
            using var reader = await readCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                existing.Add(reader.GetString(0));
            }
        }

        var desired = new HashSet<string>(
            champions.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()),
            StringComparer.OrdinalIgnoreCase);

        // Remove champions no longer in the desired set.
        foreach (var champ in existing)
        {
            if (desired.Contains(champ)) continue;
            using var delCmd = conn.CreateCommand();
            delCmd.Transaction = tx;
            delCmd.CommandText = """
                DELETE FROM objective_champions
                WHERE objective_id = @id AND champion_name = @champ
                """;
            delCmd.Parameters.AddWithValue("@id", objectiveId);
            delCmd.Parameters.AddWithValue("@champ", champ);
            await delCmd.ExecuteNonQueryAsync();
        }

        // Add newly-desired champions.
        foreach (var champ in desired)
        {
            if (existing.Contains(champ)) continue;
            using var insCmd = conn.CreateCommand();
            insCmd.Transaction = tx;
            insCmd.CommandText = """
                INSERT OR IGNORE INTO objective_champions (objective_id, champion_name)
                VALUES (@id, @champ)
                """;
            insCmd.Parameters.AddWithValue("@id", objectiveId);
            insCmd.Parameters.AddWithValue("@champ", champ);
            await insCmd.ExecuteNonQueryAsync();
        }

        await tx.CommitAsync();
    }

    public async Task<IReadOnlyList<string>> GetPlayedChampionsAsync(int limit = 30)
    {
        // Pulls distinct champion_name from games ordered by most recent use.
        // is_hidden excluded so soft-deleted games don't suggest champs the
        // user hasn't actually played.
        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT champion_name
            FROM games
            WHERE COALESCE(is_hidden, 0) = 0
              AND champion_name IS NOT NULL AND champion_name != ''
            GROUP BY champion_name
            ORDER BY MAX(COALESCE(timestamp, 0)) DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        var results = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }
        return results;
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
        var normalized = ObjectivePhases.Normalize(phase);
        var pre  = string.Equals(normalized, ObjectivePhases.PreGame,  StringComparison.OrdinalIgnoreCase);
        var ing  = string.Equals(normalized, ObjectivePhases.InGame,   StringComparison.OrdinalIgnoreCase);
        var post = string.Equals(normalized, ObjectivePhases.PostGame, StringComparison.OrdinalIgnoreCase);
        if (!pre && !ing && !post) ing = true;
        await UpdateWithPhasesAsync(objectiveId, title, skillArea, type, completionCriteria, description, pre, ing, post);
    }

    public async Task UpdateWithPhasesAsync(long objectiveId, string title, string skillArea, string type,
        string completionCriteria, string description,
        bool practicePre, bool practiceIn, bool practicePost)
    {
        var legacyPhase = practicePre  ? ObjectivePhases.PreGame
                        : practiceIn   ? ObjectivePhases.InGame
                        : practicePost ? ObjectivePhases.PostGame
                        : ObjectivePhases.InGame;

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET title = @title,
                skill_area = @skillArea,
                type = @type,
                phase = @phase,
                completion_criteria = @completionCriteria,
                description = @description,
                practice_pregame = @practicePre,
                practice_ingame = @practiceIn,
                practice_postgame = @practicePost
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@title", title);
        cmd.Parameters.AddWithValue("@skillArea", skillArea);
        cmd.Parameters.AddWithValue("@type", type);
        cmd.Parameters.AddWithValue("@phase", legacyPhase);
        cmd.Parameters.AddWithValue("@completionCriteria", completionCriteria);
        cmd.Parameters.AddWithValue("@description", description);
        cmd.Parameters.AddWithValue("@practicePre", practicePre ? 1 : 0);
        cmd.Parameters.AddWithValue("@practiceIn", practiceIn ? 1 : 0);
        cmd.Parameters.AddWithValue("@practicePost", practicePost ? 1 : 0);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdatePhaseAsync(long objectiveId, string phase)
    {
        var normalized = ObjectivePhases.Normalize(phase);
        var pre  = string.Equals(normalized, ObjectivePhases.PreGame,  StringComparison.OrdinalIgnoreCase);
        var ing  = string.Equals(normalized, ObjectivePhases.InGame,   StringComparison.OrdinalIgnoreCase);
        var post = string.Equals(normalized, ObjectivePhases.PostGame, StringComparison.OrdinalIgnoreCase);
        if (!pre && !ing && !post) ing = true;
        await UpdatePracticePhasesAsync(objectiveId, pre, ing, post);
    }

    public async Task UpdatePracticePhasesAsync(long objectiveId, bool practicePre, bool practiceIn, bool practicePost)
    {
        var legacyPhase = practicePre  ? ObjectivePhases.PreGame
                        : practiceIn   ? ObjectivePhases.InGame
                        : practicePost ? ObjectivePhases.PostGame
                        : ObjectivePhases.InGame;

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET practice_pregame = @practicePre,
                practice_ingame = @practiceIn,
                practice_postgame = @practicePost,
                phase = @phase
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@practicePre", practicePre ? 1 : 0);
        cmd.Parameters.AddWithValue("@practiceIn", practiceIn ? 1 : 0);
        cmd.Parameters.AddWithValue("@practicePost", practicePost ? 1 : 0);
        cmd.Parameters.AddWithValue("@phase", legacyPhase);
        cmd.Parameters.AddWithValue("@id", objectiveId);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(long objectiveId)
    {
        using var conn = _factory.CreateConnection();

        // v2.15.0: cascade-delete custom prompts + their answers first.
        // SQLite doesn't enforce FK cascade without PRAGMA foreign_keys=ON
        // per-connection, so we delete explicitly to match the existing pattern.
        using (var deleteAnswers = conn.CreateCommand())
        {
            deleteAnswers.CommandText = """
                DELETE FROM prompt_answers
                WHERE prompt_id IN (SELECT id FROM objective_prompts WHERE objective_id = @id)
                """;
            deleteAnswers.Parameters.AddWithValue("@id", objectiveId);
            await deleteAnswers.ExecuteNonQueryAsync();
        }

        using (var deletePrompts = conn.CreateCommand())
        {
            deletePrompts.CommandText = "DELETE FROM objective_prompts WHERE objective_id = @id";
            deletePrompts.Parameters.AddWithValue("@id", objectiveId);
            await deletePrompts.ExecuteNonQueryAsync();
        }

        using (var cmd1 = conn.CreateCommand())
        {
            cmd1.CommandText = "DELETE FROM game_objectives WHERE objective_id = @id";
            cmd1.Parameters.AddWithValue("@id", objectiveId);
            await cmd1.ExecuteNonQueryAsync();
        }

        // v2.15.0 champion gating cleanup
        using (var cmdChamps = conn.CreateCommand())
        {
            cmdChamps.CommandText = "DELETE FROM objective_champions WHERE objective_id = @id";
            cmdChamps.Parameters.AddWithValue("@id", objectiveId);
            await cmdChamps.ExecuteNonQueryAsync();
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

        // v2.17.7: if this objective is a mini that just hit its target, archive
        // it inside the same transaction so the user's focus list updates atomically.
        if (gameCountDelta > 0)
        {
            using var archiveCmd = conn.CreateCommand();
            archiveCmd.Transaction = tx;
            archiveCmd.CommandText = """
                UPDATE objectives
                SET status = 'completed',
                    is_priority = 0,
                    completed_at = @completedAt
                WHERE id = @objectiveId
                  AND status = 'active'
                  AND LOWER(COALESCE(type, '')) = 'mini'
                  AND COALESCE(target_game_count, 0) > 0
                  AND COALESCE(game_count, 0) >= COALESCE(target_game_count, 0)
                """;
            archiveCmd.Parameters.AddWithValue("@completedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            archiveCmd.Parameters.AddWithValue("@objectiveId", objectiveId);
            await archiveCmd.ExecuteNonQueryAsync();
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
              AND COALESCE(g.is_hidden, 0) = 0
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
              AND COALESCE(g.is_hidden, 0) = 0
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
                   o.title, o.completion_criteria, o.type, o.is_priority, o.phase,
                   go.criteria_met, o.criteria_metric, o.criteria_op, o.criteria_value
            FROM game_objectives go
            JOIN objectives o ON o.id = go.objective_id
            WHERE go.game_id = @gameId
            ORDER BY o.is_priority DESC, o.created_at ASC, o.id ASC
            """;
        cmd.Parameters.AddWithValue("@gameId", gameId);
        return await ReadGameObjectivesAsync(cmd);
    }

    public async Task<IReadOnlySet<long>> GetGamesWithPracticedObjectivesAsync(IReadOnlyCollection<long> gameIds)
    {
        if (gameIds.Count == 0)
        {
            return new HashSet<long>();
        }

        using var conn = _factory.CreateConnection();
        using var cmd = conn.CreateCommand();

        var placeholders = new List<string>(gameIds.Count);
        var index = 0;
        foreach (var gameId in gameIds.Distinct())
        {
            var parameterName = $"@gameId{index++}";
            placeholders.Add(parameterName);
            cmd.Parameters.AddWithValue(parameterName, gameId);
        }

        if (placeholders.Count == 0)
        {
            return new HashSet<long>();
        }

        cmd.CommandText = $"""
            SELECT DISTINCT game_id
            FROM game_objectives
            WHERE practiced = 1
              AND game_id IN ({string.Join(", ", placeholders)})
            """;

        var ids = new HashSet<long>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
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
                Phase: reader.IsDBNull(8) ? ObjectivePhases.InGame : ObjectivePhases.Normalize(reader.GetString(8)),
                CriteriaMet: reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetInt32(9) : null,
                CriteriaMetric: reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : "",
                CriteriaOp: reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : ">=",
                CriteriaValue: reader.FieldCount > 12 && !reader.IsDBNull(12) ? reader.GetDouble(12) : 0));
        }

        return results;
    }

    private static ObjectiveSummary ReadObjective(SqliteDataReader reader)
    {
        // v2.15.0: the three practice_<phase> columns. Tolerate missing columns
        // in case this reader is hit against a DB that hasn't been migrated yet
        // (shouldn't happen in production but makes the test harness easier).
        static bool OptBool(SqliteDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                return !r.IsDBNull(idx) && r.GetInt64(idx) != 0;
            }
            catch (IndexOutOfRangeException)
            {
                return false;
            }
        }

        // v2.17.7: target_game_count column is added by migration; tolerate its
        // absence on un-migrated rows the same way OptBool does for practice phases.
        static int OptInt(SqliteDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                return r.IsDBNull(idx) ? 0 : r.GetInt32(idx);
            }
            catch (IndexOutOfRangeException)
            {
                return 0;
            }
        }

        // v2.18 (F2): focus_phase column is added by migration; tolerate absence.
        static string OptText(SqliteDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                return r.IsDBNull(idx) ? "" : r.GetString(idx);
            }
            catch (IndexOutOfRangeException)
            {
                return "";
            }
        }

        // v2.18 (schema v5): criteria_value column; tolerate absence.
        static double OptDouble(SqliteDataReader r, string col)
        {
            try
            {
                var idx = r.GetOrdinal(col);
                return r.IsDBNull(idx) ? 0 : r.GetDouble(idx);
            }
            catch (IndexOutOfRangeException)
            {
                return 0;
            }
        }

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
            CompletedAt: reader.IsDBNull(reader.GetOrdinal("completed_at")) ? null : reader.GetInt64(reader.GetOrdinal("completed_at")),
            PracticePre: OptBool(reader, "practice_pregame"),
            PracticeIn: OptBool(reader, "practice_ingame"),
            PracticePost: OptBool(reader, "practice_postgame"),
            TargetGameCount: OptInt(reader, "target_game_count"),
            FocusPhase: OptText(reader, "focus_phase"),
            CriteriaMetric: OptText(reader, "criteria_metric"),
            CriteriaOp: OptText(reader, "criteria_op") is { Length: > 0 } op ? op : ">=",
            CriteriaValue: OptDouble(reader, "criteria_value"));
    }
}
