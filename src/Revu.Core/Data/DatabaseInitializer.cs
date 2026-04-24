#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// Creates tables, runs migrations, and seeds default data.
/// Mirrors the Python ConnectionManager._init_db() logic exactly.
/// </summary>
public sealed class DatabaseInitializer
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<DatabaseInitializer> _logger;

    public DatabaseInitializer(
        IDbConnectionFactory connectionFactory,
        ILogger<DatabaseInitializer> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <summary>
    /// Initialises the database: creates tables, runs migrations, seeds defaults.
    /// Safe to call multiple times (all statements use IF NOT EXISTS / OR IGNORE).
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // The old-path → data/ migration is handled by SqliteConnectionFactory.GetDefaultDatabasePath()
        // and ConfigService's static ctor. Legacy exe-relative migration code has been removed entirely.

        using var connection = _connectionFactory.CreateConnection();

        // 1. Execute all CREATE TABLE / CREATE INDEX statements
        foreach (var statement in Schema.AllCreateStatements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // 2. Execute all ALTER TABLE migrations (tolerate "duplicate column" errors)
        foreach (var migration in Schema.AllMigrations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = migration;
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column"))
            {
                // Column already exists -- expected for previously-migrated databases
            }
        }

        // 2b. Normalize legacy/hybrid tables into the current schema.
        await NormalizeRulesTableAsync(connection, cancellationToken);
        await NormalizeObjectivesTableAsync(connection, cancellationToken);
        await BackfillObjectiveScoreFromLegacyGameObjectivesAsync(connection, cancellationToken);
        await NormalizeGameObjectivesTableAsync(connection, cancellationToken);
        await NormalizeObjectivePromptsTableAsync(connection, cancellationToken);
        await BackfillObjectivePracticePhasesAsync(connection, cancellationToken);

        // v2.15.0: indexes on objective_prompts(phase, sort_order) and
        // prompt_answers(game_id) have to land AFTER normalize — the columns
        // they reference only exist on the rewritten tables.
        await CreatePostMigrationIndexesAsync(connection, cancellationToken);

        // 3. Seed default concept tags if the table is empty
        await SeedConceptTagsAsync(connection, cancellationToken);

        // 4. Seed default derived event definitions if the table is empty
        await SeedDerivedEventsAsync(connection, cancellationToken);

        // 5. Seed default persistent_notes row if empty
        await SeedPersistentNotesAsync(connection, cancellationToken);

        // 6. Backfill objectives.game_count from game_objectives for existing data
        await BackfillObjectiveGameCountAsync(connection, cancellationToken);
        await BackfillObjectiveScoreFromPracticedGamesAsync(connection, cancellationToken);

        _logger.LogInformation("Database initialized at {Path}", _connectionFactory.DatabasePath);
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private async Task NormalizeRulesTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await GetTableColumnsAsync(connection, "rules", ct);
        if (columns.Count == 0)
        {
            return;
        }

        var needsRewrite =
            columns.Contains("title") ||
            columns.Contains("status") ||
            !columns.Contains("name") ||
            !columns.Contains("rule_type") ||
            !columns.Contains("condition_value") ||
            !columns.Contains("is_active");

        if (!needsRewrite)
        {
            return;
        }

        var nameExpr = columns.Contains("name") && columns.Contains("title")
            ? "COALESCE(NULLIF(name, ''), title, '')"
            : columns.Contains("name")
                ? "COALESCE(name, '')"
                : columns.Contains("title")
                    ? "COALESCE(title, '')"
                    : "''";

        var descriptionExpr = columns.Contains("description")
            ? "COALESCE(description, '')"
            : "''";

        var ruleTypeExpr = columns.Contains("rule_type")
            ? "COALESCE(NULLIF(rule_type, ''), 'custom')"
            : "'custom'";

        var conditionExpr = columns.Contains("condition_value")
            ? "COALESCE(condition_value, '')"
            : "''";

        var isActiveExpr = columns.Contains("is_active") && columns.Contains("status")
            ? "COALESCE(is_active, CASE WHEN lower(COALESCE(status, 'active')) = 'active' THEN 1 ELSE 0 END)"
            : columns.Contains("is_active")
                ? "COALESCE(is_active, 1)"
                : columns.Contains("status")
                    ? "CASE WHEN lower(COALESCE(status, 'active')) = 'active' THEN 1 ELSE 0 END"
                    : "1";

        var createdAtExpr = columns.Contains("created_at")
            ? "created_at"
            : "NULL";

        using var tx = connection.BeginTransaction();

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE rules__migrated (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    name            TEXT NOT NULL,
                    description     TEXT DEFAULT '',
                    rule_type       TEXT DEFAULT 'custom',
                    condition_value TEXT DEFAULT '',
                    is_active       INTEGER DEFAULT 1,
                    created_at      INTEGER
                )
                """;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var copyCmd = connection.CreateCommand())
        {
            copyCmd.Transaction = tx;
            copyCmd.CommandText = $"""
                INSERT INTO rules__migrated (id, name, description, rule_type, condition_value, is_active, created_at)
                SELECT
                    id,
                    {nameExpr},
                    {descriptionExpr},
                    {ruleTypeExpr},
                    {conditionExpr},
                    {isActiveExpr},
                    {createdAtExpr}
                FROM rules
                """;
            await copyCmd.ExecuteNonQueryAsync(ct);
        }

        using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE rules";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        using (var renameCmd = connection.CreateCommand())
        {
            renameCmd.Transaction = tx;
            renameCmd.CommandText = "ALTER TABLE rules__migrated RENAME TO rules";
            await renameCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation("Normalized legacy rules table to current schema");
    }

    private async Task NormalizeObjectivesTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await GetTableColumnsAsync(connection, "objectives", ct);
        if (columns.Count == 0)
        {
            return;
        }

        var needsRewrite =
            !columns.Contains("title") ||
            !columns.Contains("skill_area") ||
            !columns.Contains("type") ||
            !columns.Contains("phase") ||
            !columns.Contains("completion_criteria") ||
            !columns.Contains("description") ||
            !columns.Contains("status") ||
            !columns.Contains("is_priority") ||
            !columns.Contains("score") ||
            !columns.Contains("game_count") ||
            !columns.Contains("created_at") ||
            !columns.Contains("completed_at") ||
            // v2.15.0: three practice-phase bools. If any are missing we must
            // rewrite so the re-created objectives table has them (the
            // ALTER TABLE migrations run BEFORE this normalize step and get
            // wiped by the rewrite otherwise).
            !columns.Contains("practice_pregame") ||
            !columns.Contains("practice_ingame") ||
            !columns.Contains("practice_postgame");

        if (!needsRewrite)
        {
            return;
        }

        using var tx = connection.BeginTransaction();

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE objectives__migrated (
                    id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                    title               TEXT NOT NULL,
                    skill_area          TEXT DEFAULT '',
                    type                TEXT DEFAULT 'primary',
                    phase               TEXT DEFAULT 'ingame',
                    completion_criteria TEXT DEFAULT '',
                    description         TEXT DEFAULT '',
                    status              TEXT DEFAULT 'active',
                    is_priority         INTEGER DEFAULT 0,
                    score               INTEGER DEFAULT 0,
                    game_count          INTEGER DEFAULT 0,
                    created_at          INTEGER,
                    completed_at        INTEGER,
                    practice_pregame    INTEGER NOT NULL DEFAULT 0,
                    practice_ingame     INTEGER NOT NULL DEFAULT 0,
                    practice_postgame   INTEGER NOT NULL DEFAULT 0
                )
                """;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var copyCmd = connection.CreateCommand())
        {
            copyCmd.Transaction = tx;
            copyCmd.CommandText = $"""
                INSERT INTO objectives__migrated (
                    id, title, skill_area, type, phase, completion_criteria, description,
                    status, is_priority, score, game_count, created_at, completed_at,
                    practice_pregame, practice_ingame, practice_postgame
                )
                SELECT
                    id,
                    {GetTextColumnExpr(columns, "title")},
                    {GetTextColumnExpr(columns, "skill_area")},
                    CASE
                        WHEN {GetTextColumnExpr(columns, "type")} = '' THEN 'primary'
                        ELSE {GetTextColumnExpr(columns, "type")}
                    END,
                    CASE
                        WHEN {GetTextColumnExpr(columns, "phase")} = '' THEN 'ingame'
                        ELSE {GetTextColumnExpr(columns, "phase")}
                    END,
                    {GetTextColumnExpr(columns, "completion_criteria")},
                    {GetTextColumnExpr(columns, "description")},
                    CASE
                        WHEN {GetTextColumnExpr(columns, "status")} = '' THEN 'active'
                        ELSE {GetTextColumnExpr(columns, "status")}
                    END,
                    {GetIntColumnExpr(columns, "is_priority")},
                    {GetIntColumnExpr(columns, "score")},
                    {GetIntColumnExpr(columns, "game_count")},
                    {GetNullableColumnExpr(columns, "created_at")},
                    {GetNullableColumnExpr(columns, "completed_at")},
                    {GetIntColumnExpr(columns, "practice_pregame")},
                    {GetIntColumnExpr(columns, "practice_ingame")},
                    {GetIntColumnExpr(columns, "practice_postgame")}
                FROM objectives
                """;
            await copyCmd.ExecuteNonQueryAsync(ct);
        }

        using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE objectives";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        using (var renameCmd = connection.CreateCommand())
        {
            renameCmd.Transaction = tx;
            renameCmd.CommandText = "ALTER TABLE objectives__migrated RENAME TO objectives";
            await renameCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation("Normalized legacy objectives table to current schema");
    }

    private async Task BackfillObjectiveScoreFromLegacyGameObjectivesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await GetTableColumnsAsync(connection, "game_objectives", ct);
        if (!columns.Contains("score"))
        {
            return;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET score = (
                SELECT CAST(ROUND(COALESCE(SUM(COALESCE(game_objectives.score, 0)), 0)) AS INTEGER)
                FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            )
            WHERE COALESCE(score, 0) = 0
              AND EXISTS (
                    SELECT 1
                    FROM game_objectives
                    WHERE game_objectives.objective_id = objectives.id
                      AND COALESCE(game_objectives.score, 0) != 0
              )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task NormalizeGameObjectivesTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var columns = await GetTableColumnsAsync(connection, "game_objectives", ct);
        if (columns.Count == 0)
        {
            return;
        }

        var needsRewrite =
            columns.Contains("score") ||
            columns.Contains("notes") ||
            !columns.Contains("practiced") ||
            !columns.Contains("execution_note");

        if (!needsRewrite)
        {
            return;
        }

        var practicedExpr = columns.Contains("practiced")
            ? "COALESCE(practiced, 1)"
            : columns.Contains("score")
                ? "CASE WHEN COALESCE(score, 0) > 0 THEN 1 ELSE 0 END"
                : "1";

        var executionNoteExpr = columns.Contains("execution_note")
            ? "COALESCE(execution_note, '')"
            : columns.Contains("notes")
                ? "COALESCE(notes, '')"
                : "''";

        using var tx = connection.BeginTransaction();

        using (var createCmd = connection.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE game_objectives__migrated (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id         INTEGER NOT NULL,
                    objective_id    INTEGER NOT NULL,
                    practiced       INTEGER DEFAULT 1,
                    execution_note  TEXT DEFAULT '',
                    FOREIGN KEY (game_id) REFERENCES games(game_id),
                    FOREIGN KEY (objective_id) REFERENCES objectives(id),
                    UNIQUE(game_id, objective_id)
                )
                """;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var copyCmd = connection.CreateCommand())
        {
            copyCmd.Transaction = tx;
            copyCmd.CommandText = $"""
                INSERT OR REPLACE INTO game_objectives__migrated (
                    id, game_id, objective_id, practiced, execution_note
                )
                SELECT
                    id,
                    game_id,
                    objective_id,
                    {practicedExpr},
                    {executionNoteExpr}
                FROM game_objectives
                """;
            await copyCmd.ExecuteNonQueryAsync(ct);
        }

        using (var dropCmd = connection.CreateCommand())
        {
            dropCmd.Transaction = tx;
            dropCmd.CommandText = "DROP TABLE game_objectives";
            await dropCmd.ExecuteNonQueryAsync(ct);
        }

        using (var renameCmd = connection.CreateCommand())
        {
            renameCmd.Transaction = tx;
            renameCmd.CommandText = "ALTER TABLE game_objectives__migrated RENAME TO game_objectives";
            await renameCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        _logger.LogInformation("Normalized legacy game_objectives table to current schema");
    }

    private static async Task<HashSet<string>> GetTableColumnsAsync(
        SqliteConnection connection,
        string tableName,
        CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static string GetTextColumnExpr(HashSet<string> columns, string columnName)
    {
        return columns.Contains(columnName)
            ? $"COALESCE({columnName}, '')"
            : "''";
    }

    private static string GetIntColumnExpr(HashSet<string> columns, string columnName)
    {
        return columns.Contains(columnName)
            ? $"CAST(ROUND(COALESCE({columnName}, 0)) AS INTEGER)"
            : "0";
    }

    private static string GetNullableColumnExpr(HashSet<string> columns, string columnName)
    {
        return columns.Contains(columnName)
            ? columnName
            : "NULL";
    }

    private static async Task SeedConceptTagsAsync(SqliteConnection connection, CancellationToken ct)
    {
        foreach (var (name, polarity, color) in Schema.DefaultConceptTags)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT OR IGNORE INTO concept_tags (name, polarity, color) VALUES ($name, $polarity, $color)";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$polarity", polarity);
            cmd.Parameters.AddWithValue("$color", color);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SeedDerivedEventsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM derived_event_definitions";
        var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;

        if (count > 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        foreach (var (name, sourceTypes, minCount, windowSeconds, color) in Schema.DefaultDerivedEvents)
        {
            ct.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO derived_event_definitions
                    (name, source_types, min_count, window_seconds, color, is_default, created_at)
                VALUES ($name, $sourceTypes, $minCount, $windowSeconds, $color, 1, $createdAt)
                """;
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$sourceTypes", sourceTypes);
            cmd.Parameters.AddWithValue("$minCount", minCount);
            cmd.Parameters.AddWithValue("$windowSeconds", windowSeconds);
            cmd.Parameters.AddWithValue("$color", color);
            cmd.Parameters.AddWithValue("$createdAt", now);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task SeedPersistentNotesAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM persistent_notes";
        var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;

        if (count > 0) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "INSERT INTO persistent_notes (content, updated_at) VALUES ('', $now)";
        cmd.Parameters.AddWithValue("$now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackfillObjectiveGameCountAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives SET game_count = COALESCE((
                SELECT COUNT(*) FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            ), 0)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackfillObjectiveScoreFromPracticedGamesAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET score = MAX(
                COALESCE(score, 0),
                COALESCE((
                    SELECT SUM(CASE WHEN COALESCE(game_objectives.practiced, 0) != 0 THEN 2 ELSE 0 END)
                    FROM game_objectives
                    WHERE game_objectives.objective_id = objectives.id
                ), 0)
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // v2.15.0 schema rework — see docs/OBJECTIVES_CUSTOM_PROMPTS_PLAN.md.
    //
    // The legacy objective_prompts / prompt_answers tables were shaped for
    // yes/no event prompts (answer_type='yes_no', answer_value INTEGER,
    // event_instance_id, event_time_s). Zero app code paths ever wrote to
    // them. We repurpose the same tables for free-form text prompts the
    // user designs themselves. Copy-migrate pattern so any stray rows are
    // preserved (translated into best-effort text labels).
    private async Task NormalizeObjectivePromptsTableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var promptCols = await GetTableColumnsAsync(connection, "objective_prompts", ct);
        var answerCols = await GetTableColumnsAsync(connection, "prompt_answers", ct);

        if (promptCols.Count == 0 && answerCols.Count == 0)
        {
            return;
        }

        // Fresh schema indicators: `label` column on prompts + `answer_text`
        // column on answers. If both present, we're already on the new shape.
        var needsRewrite =
            !promptCols.Contains("label") ||
            !promptCols.Contains("phase") ||
            !answerCols.Contains("answer_text") ||
            // Old-shape sentinels — if any of these exist, we haven't migrated.
            promptCols.Contains("question_text") ||
            promptCols.Contains("answer_type") ||
            answerCols.Contains("answer_value");

        if (!needsRewrite)
        {
            return;
        }

        using var tx = connection.BeginTransaction();

        // ── Rebuild objective_prompts ──
        using (var createCmd = connection.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = """
                CREATE TABLE objective_prompts__migrated (
                    id              INTEGER PRIMARY KEY AUTOINCREMENT,
                    objective_id    INTEGER NOT NULL,
                    phase           TEXT NOT NULL DEFAULT 'ingame',
                    label           TEXT NOT NULL DEFAULT '',
                    sort_order      INTEGER NOT NULL DEFAULT 0,
                    created_at      INTEGER,
                    FOREIGN KEY (objective_id) REFERENCES objectives(id)
                )
                """;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        if (promptCols.Count > 0)
        {
            // Translate whatever we can: legacy question_text → label,
            // phase default 'ingame' (legacy had no phase concept).
            var labelExpr = promptCols.Contains("label")
                ? "COALESCE(label, '')"
                : promptCols.Contains("question_text")
                    ? "COALESCE(question_text, '')"
                    : "''";
            var phaseExpr = promptCols.Contains("phase")
                ? "COALESCE(phase, 'ingame')"
                : "'ingame'";
            var sortExpr = promptCols.Contains("sort_order")
                ? "COALESCE(sort_order, 0)"
                : "0";
            var createdExpr = promptCols.Contains("created_at")
                ? "created_at"
                : "NULL";

            using var copyPromptsCmd = connection.CreateCommand();
            copyPromptsCmd.Transaction = tx;
            copyPromptsCmd.CommandText = $"""
                INSERT INTO objective_prompts__migrated
                    (id, objective_id, phase, label, sort_order, created_at)
                SELECT id, objective_id,
                       {phaseExpr},
                       {labelExpr},
                       {sortExpr},
                       {createdExpr}
                FROM objective_prompts
                """;
            await copyPromptsCmd.ExecuteNonQueryAsync(ct);

            using var dropPromptsCmd = connection.CreateCommand();
            dropPromptsCmd.Transaction = tx;
            dropPromptsCmd.CommandText = "DROP TABLE objective_prompts";
            await dropPromptsCmd.ExecuteNonQueryAsync(ct);
        }

        using (var renamePromptsCmd = connection.CreateCommand())
        {
            renamePromptsCmd.Transaction = tx;
            renamePromptsCmd.CommandText =
                "ALTER TABLE objective_prompts__migrated RENAME TO objective_prompts";
            await renamePromptsCmd.ExecuteNonQueryAsync(ct);
        }

        // ── Rebuild prompt_answers ──
        using (var createAnswersCmd = connection.CreateCommand())
        {
            createAnswersCmd.Transaction = tx;
            createAnswersCmd.CommandText = """
                CREATE TABLE prompt_answers__migrated (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    prompt_id   INTEGER NOT NULL,
                    game_id     INTEGER NOT NULL,
                    answer_text TEXT NOT NULL DEFAULT '',
                    updated_at  INTEGER,
                    FOREIGN KEY (prompt_id) REFERENCES objective_prompts(id),
                    FOREIGN KEY (game_id) REFERENCES games(game_id),
                    UNIQUE(prompt_id, game_id)
                )
                """;
            await createAnswersCmd.ExecuteNonQueryAsync(ct);
        }

        if (answerCols.Count > 0)
        {
            // Legacy answer_value was 0/1 INTEGER — translate to "yes"/"no"/""
            // strings so at least the fact that the user answered is preserved.
            var textExpr = answerCols.Contains("answer_text")
                ? "COALESCE(answer_text, '')"
                : answerCols.Contains("answer_value")
                    ? "CASE answer_value WHEN 1 THEN 'yes' WHEN 0 THEN 'no' ELSE '' END"
                    : "''";
            var updatedExpr = answerCols.Contains("updated_at")
                ? "updated_at"
                : "NULL";

            // event_instance_id was part of the legacy unique key. We drop
            // to (prompt_id, game_id). Use INSERT OR REPLACE so duplicates
            // collapse to the most-recent row.
            using var copyAnswersCmd = connection.CreateCommand();
            copyAnswersCmd.Transaction = tx;
            copyAnswersCmd.CommandText = $"""
                INSERT OR REPLACE INTO prompt_answers__migrated
                    (prompt_id, game_id, answer_text, updated_at)
                SELECT prompt_id, game_id,
                       {textExpr},
                       {updatedExpr}
                FROM prompt_answers
                """;
            await copyAnswersCmd.ExecuteNonQueryAsync(ct);

            using var dropAnswersCmd = connection.CreateCommand();
            dropAnswersCmd.Transaction = tx;
            dropAnswersCmd.CommandText = "DROP TABLE prompt_answers";
            await dropAnswersCmd.ExecuteNonQueryAsync(ct);
        }

        using (var renameAnswersCmd = connection.CreateCommand())
        {
            renameAnswersCmd.Transaction = tx;
            renameAnswersCmd.CommandText =
                "ALTER TABLE prompt_answers__migrated RENAME TO prompt_answers";
            await renameAnswersCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        _logger.LogInformation("Normalized legacy objective_prompts / prompt_answers schema to v2.15.0 shape");
    }

    // v2.15.0: CREATE INDEX statements for tables that got rewritten by
    // NormalizeObjectivePromptsTableAsync. Called after normalize so the
    // referenced columns exist.
    private static async Task CreatePostMigrationIndexesAsync(SqliteConnection connection, CancellationToken ct)
    {
        string[] indexes =
        [
            Schema.CreateObjectivePromptsIndex,
            Schema.CreatePromptAnswersIndex,
        ];

        foreach (var sql in indexes)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    // Backfill the three new practice_<phase> bool columns on objectives
    // from the legacy single-string `phase` column. Idempotent — the
    // WHERE guard means this no-ops once any bool has been set.
    private static async Task BackfillObjectivePracticePhasesAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE objectives
            SET practice_pregame  = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'pregame'  THEN 1 ELSE 0 END,
                practice_ingame   = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'ingame'   THEN 1 ELSE 0 END,
                practice_postgame = CASE WHEN LOWER(COALESCE(phase, 'ingame')) = 'postgame' THEN 1 ELSE 0 END
            WHERE COALESCE(practice_pregame, 0) = 0
              AND COALESCE(practice_ingame, 0) = 0
              AND COALESCE(practice_postgame, 0) = 0
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
