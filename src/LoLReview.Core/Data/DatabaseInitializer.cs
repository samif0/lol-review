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

        // 2b. Normalize legacy/hybrid rules tables into the current schema.
        await NormalizeRulesTableAsync(connection, cancellationToken);

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
            UPDATE objectives SET game_count = (
                SELECT COUNT(*) FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            ) WHERE game_count = 0 AND EXISTS (
                SELECT 1 FROM game_objectives
                WHERE game_objectives.objective_id = objectives.id
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

}
