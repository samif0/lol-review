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
        // Check for legacy database before initialising
        var legacyPath = FindLegacyDatabase();
        if (legacyPath is not null)
        {
            await MigrateFromLegacyAsync(legacyPath, cancellationToken);
        }

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

    // ── Legacy database migration ────────────────────────────────────

    /// <summary>
    /// Searches common locations for a pre-AppData database file.
    /// Returns the path if found, <c>null</c> otherwise.
    /// </summary>
    public string? FindLegacyDatabase()
    {
        var exePath = Environment.ProcessPath ?? typeof(DatabaseInitializer).Assembly.Location;
        var exeDir = Path.GetDirectoryName(exePath) ?? ".";

        var candidates = new[]
        {
            Path.Combine(exeDir, "data", "lol_review.db"),
            Path.Combine(exeDir, "..", "..", "data", "lol_review.db"),
            Path.Combine(exeDir, "..", "data", "lol_review.db"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "lol_review.db"),
        };

        foreach (var candidate in candidates)
        {
            var resolved = Path.GetFullPath(candidate);
            _logger.LogDebug("Legacy DB candidate: {Path} exists={Exists}", resolved, File.Exists(resolved));
            if (File.Exists(resolved))
            {
                _logger.LogInformation("Found legacy DB at {Path}", resolved);
                return resolved;
            }
        }

        return null;
    }

    /// <summary>
    /// Copies the legacy database to the AppData location if it contains more games.
    /// Creates a .db.bak backup of the existing database before replacing.
    /// </summary>
    public async Task MigrateFromLegacyAsync(string legacyPath, CancellationToken cancellationToken = default)
    {
        var targetPath = _connectionFactory.DatabasePath;

        if (!File.Exists(targetPath))
        {
            File.Copy(legacyPath, targetPath, overwrite: false);
            _logger.LogInformation("Migrated database from {Legacy} to {Target}", legacyPath, targetPath);
            return;
        }

        try
        {
            var legacyCount = await CountGamesAsync(legacyPath, cancellationToken);
            var targetCount = await CountGamesAsync(targetPath, cancellationToken);

            if (legacyCount > targetCount)
            {
                var backupPath = targetPath + ".bak";
                File.Copy(targetPath, backupPath, overwrite: true);
                File.Copy(legacyPath, targetPath, overwrite: true);
                _logger.LogInformation(
                    "Legacy DB has more data ({LegacyCount} vs {TargetCount} games). " +
                    "Replaced AppData DB. Backup at {Backup}",
                    legacyCount, targetCount, backupPath);
            }
            else
            {
                _logger.LogInformation(
                    "AppData DB is up-to-date ({TargetCount} >= {LegacyCount} games). No migration needed.",
                    targetCount, legacyCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Migration comparison failed");
        }
    }

    /// <summary>Counts the number of rows in the games table of a database file.</summary>
    private static async Task<long> CountGamesAsync(string dbFilePath, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbFilePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM games";

        try
        {
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? l : 0;
        }
        catch (SqliteException)
        {
            return 0;
        }
    }
}
