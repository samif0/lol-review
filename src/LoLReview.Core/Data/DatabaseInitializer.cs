#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Data;

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

        // 3. Seed default concept tags if the table is empty
        await SeedConceptTagsAsync(connection, cancellationToken);

        // 4. Seed default derived event definitions if the table is empty
        await SeedDerivedEventsAsync(connection, cancellationToken);

        // 5. Seed default persistent_notes row if empty
        await SeedPersistentNotesAsync(connection, cancellationToken);

        // 6. Backfill objectives.game_count from game_objectives for existing data
        await BackfillObjectiveGameCountAsync(connection, cancellationToken);

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
            !columns.Contains("completion_criteria") ||
            !columns.Contains("description") ||
            !columns.Contains("status") ||
            !columns.Contains("score") ||
            !columns.Contains("game_count") ||
            !columns.Contains("created_at") ||
            !columns.Contains("completed_at");

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
                    completion_criteria TEXT DEFAULT '',
                    description         TEXT DEFAULT '',
                    status              TEXT DEFAULT 'active',
                    score               INTEGER DEFAULT 0,
                    game_count          INTEGER DEFAULT 0,
                    created_at          INTEGER,
                    completed_at        INTEGER
                )
                """;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        using (var copyCmd = connection.CreateCommand())
        {
            copyCmd.Transaction = tx;
            copyCmd.CommandText = $"""
                INSERT INTO objectives__migrated (
                    id, title, skill_area, type, completion_criteria, description,
                    status, score, game_count, created_at, completed_at
                )
                SELECT
                    id,
                    {GetTextColumnExpr(columns, "title")},
                    {GetTextColumnExpr(columns, "skill_area")},
                    CASE
                        WHEN {GetTextColumnExpr(columns, "type")} = '' THEN 'primary'
                        ELSE {GetTextColumnExpr(columns, "type")}
                    END,
                    {GetTextColumnExpr(columns, "completion_criteria")},
                    {GetTextColumnExpr(columns, "description")},
                    CASE
                        WHEN {GetTextColumnExpr(columns, "status")} = '' THEN 'active'
                        ELSE {GetTextColumnExpr(columns, "status")}
                    END,
                    {GetIntColumnExpr(columns, "score")},
                    {GetIntColumnExpr(columns, "game_count")},
                    {GetNullableColumnExpr(columns, "created_at")},
                    {GetNullableColumnExpr(columns, "completed_at")}
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
        using var countCmd = connection.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM concept_tags";
        var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;

        if (count > 0) return;

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

}
