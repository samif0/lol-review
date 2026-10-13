#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// The sidecar's startup schema steps: additive table/column migrations, then
/// idempotent seeds, indexes and objective reconciliation. Nothing here drops or
/// rebuilds a table.
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

    private async Task ReassertWalModeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var wal = connection.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            var mode = Convert.ToString(await wal.ExecuteScalarAsync(cancellationToken));
            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
                _logger.LogWarning("Database stays in {Mode} journal mode; WAL could not be enabled", mode);
        }
        catch (SqliteException ex)
        {
            _logger.LogWarning(ex, "Could not re-assert WAL journal mode (non-fatal)");
        }
    }

    /// <summary>
    /// Runs ONLY the additive, non-destructive schema steps: every
    /// <c>CREATE TABLE/INDEX IF NOT EXISTS</c> and the versioned ALTER/CREATE
    /// migrations gated by app_schema_version. Idempotent and safe to call on every
    /// startup against an EXISTING DB.
    ///
    /// <para>This is the sidecar's migration entry point: without it, new versioned
    /// migrations never run and write endpoints hit "no such table".</para>
    /// </summary>
    public async Task ApplyAdditiveSchemaAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        // WAL persists once set, but a database that arrives in rollback-journal mode (a
        // DataSnapshot restore, an old copy) would otherwise stay there, where readers
        // block writers. Startup runs this on the first connection, before any other is
        // open; if SQLite cannot switch, the current mode is kept and logged.
        await ReassertWalModeAsync(connection, cancellationToken);

        // CREATE TABLE / INDEX IF NOT EXISTS — adds any table missing on this DB
        // (e.g. a v8 table on a DB last migrated at v7).
        foreach (var statement in Schema.AllCreateStatements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = statement;
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Versioned additive migrations (ALTER ADD COLUMN / CREATE IF NOT EXISTS),
        // gated by the recorded schema version. Databases from older builds have no
        // metadata row, so version 0 runs the legacy additive set with
        // duplicate-column tolerance.
        var appliedSchemaVersion = await GetAppliedSchemaVersionAsync(connection, cancellationToken);
        foreach (var migrationSet in Schema.VersionedMigrations
                     .Where(static migration => migration.Version > 0)
                     .OrderBy(static migration => migration.Version))
        {
            if (migrationSet.Version <= appliedSchemaVersion) continue;
            await ExecuteMigrationSetAsync(connection, migrationSet, cancellationToken);
            await SetAppliedSchemaVersionAsync(connection, migrationSet.Version, cancellationToken);
            appliedSchemaVersion = migrationSet.Version;
        }

        _logger.LogInformation(
            "Additive schema applied (now at v{Version}) at {Path}",
            appliedSchemaVersion, _connectionFactory.DatabasePath);
    }

    /// <summary>
    /// Default seeds (INSERT OR IGNORE / count-gated), post-migration indexes, and
    /// the objectives count/score reconciliation. Without this a fresh install has
    /// no persistent_notes row (notes silently never save), an empty
    /// derived_event_definitions table, no default concept tags, no visible-games
    /// indexes, and objective game counts that drift stale when games are hidden.
    /// Everything here is idempotent and touches no user data, so it is safe to run
    /// on every startup.
    /// </summary>
    public async Task ApplySeedsAndIndexesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.CreateConnection();

        await CreatePostMigrationIndexesAsync(connection, cancellationToken);
        await SeedConceptTagsAsync(connection, cancellationToken);
        await SeedDerivedEventsAsync(connection, cancellationToken);
        await SeedPersistentNotesAsync(connection, cancellationToken);

        // Reconciliation, not migration: keeps objectives.game_count / score in
        // sync with practiced game_objectives rows (hide/unhide, deletes). The
        // WHERE/COALESCE guards make both no-ops when nothing changed. Between them, the
        // practice-point re-base runs once per database (a schema_metadata marker).
        await BackfillObjectiveGameCountAsync(connection, cancellationToken);
        try
        {
            await RebaseLegacyPracticePointsOnceAsync(connection, cancellationToken);
        }
        catch (SqliteException ex)
        {
            // Rolled back with its marker unwritten, so the next launch tries again.
            _logger.LogWarning(ex, "Practice-point re-base skipped (non-fatal)");
        }
        await BackfillObjectiveScoreFromPracticedGamesAsync(connection, cancellationToken);

        _logger.LogInformation("Seeds, indexes and objective reconciliation applied at {Path}", _connectionFactory.DatabasePath);
    }

    /// <summary>schema_metadata key recording that <see cref="RebaseLegacyPracticePointsOnceAsync"/>
    /// ran on this database (a marker, not a schema version).</summary>
    internal const string PracticePointsRebasedKey = "objective_practice_points_rebased";

    /// <summary>
    /// Hide and restore, game delete and remake verdicts move a game's practice points on
    /// the rule that only a counted game (visible, not a remake) holds them. Builds up to
    /// 3.14.1 credited every practiced game and never moved points on a hide or a remake
    /// verdict, so on a database they last opened a practiced hidden game or remake still
    /// holds its points, and restoring it or clearing the remake would credit them twice.
    /// Once per database, those points are re-based off the score so it holds practice
    /// points for counted games only; clip points and older points stay. The marker
    /// commits with the re-base, so it never runs twice. A restored backup from an older
    /// build has no marker and is re-based on the launch that follows the restore.
    /// </summary>
    private async Task RebaseLegacyPracticePointsOnceAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var tx = connection.BeginTransaction();
        using (var done = connection.CreateCommand())
        {
            done.Transaction = tx;
            done.CommandText = "SELECT 1 FROM schema_metadata WHERE key = $key";
            done.Parameters.AddWithValue("$key", PracticePointsRebasedKey);
            if (await done.ExecuteScalarAsync(ct) is not null) return;
        }

        int rebased;
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE objectives
                SET score = MAX(0, COALESCE(score, 0) - {Repositories.ObjectivesRepository.PracticePoints} * (
                    SELECT COUNT(*) FROM game_objectives
                    JOIN games g ON g.game_id = game_objectives.game_id
                    WHERE game_objectives.objective_id = objectives.id
                      AND COALESCE(game_objectives.practiced, 0) != 0
                      AND NOT ({Repositories.CountedGamesSql.Counted("g")})
                ))
                WHERE id IN (
                    SELECT game_objectives.objective_id FROM game_objectives
                    JOIN games g ON g.game_id = game_objectives.game_id
                    WHERE COALESCE(game_objectives.practiced, 0) != 0
                      AND NOT ({Repositories.CountedGamesSql.Counted("g")})
                )
                """;
            rebased = await cmd.ExecuteNonQueryAsync(ct);
        }

        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = tx;
            mark.CommandText = """
                INSERT INTO schema_metadata (key, value, updated_at)
                VALUES ($key, '1', $updatedAt)
                """;
            mark.Parameters.AddWithValue("$key", PracticePointsRebasedKey);
            mark.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await mark.ExecuteNonQueryAsync(ct);
        }

        tx.Commit();
        if (rebased > 0)
            _logger.LogInformation(
                "Re-based practice points of {Count} objective(s) onto counted games (hidden games and remakes hold none)",
                rebased);
    }

    /// <summary>
    /// One-time, idempotent back-catalog account-scope repair: re-stamp the
    /// signed-in Riot PUUID onto every game row that was captured with a
    /// DIFFERENT non-empty account id — i.e. the UUID-shaped LCU
    /// <c>localPlayer.puuid</c> (e.g. 07f45763-…) that the pre-3.1.6 capture path
    /// saved instead of the encrypted Riot PUUID our login stores.
    ///
    /// <para>This is the history-sweep counterpart to
    /// <c>GameService.ProcessGameEndAsync</c>'s per-game reconcile (which only
    /// fixes games captured AFTER 3.1.6). Account scoping string-compares
    /// games.puuid to the configured PUUID
    /// (<c>SessionLogRepository.CurrentAccountFilter</c> / <c>GameRepository</c>),
    /// so a stale LCU id strands every such game behind the filter — the
    /// "GAMES 0 even though I played N" dashboard bug. Without a sweep, those rows
    /// stay hidden forever even after the user updates.</para>
    ///
    /// <para>Scope is deliberately NARROW (decided 2026-06-22): we touch rows
    /// where <c>puuid &lt;&gt; '' AND puuid &lt;&gt; @riotPuuid</c> only. Legacy
    /// <c>puuid = ''</c> rows are LEFT ALONE — the read filter's <c>= ''</c> leg
    /// already treats them as the current user's own, and claiming them risks
    /// re-tagging genuinely-foreign legacy imports. No-op when logged out
    /// (<paramref name="riotPuuid"/> empty), so it can run unconditionally on
    /// every startup. The WHERE guard makes it a no-op once reconciled, so it's
    /// safe to call repeatedly.</para>
    ///
    /// <para>Only <c>games.puuid</c> is rewritten — session_log / game_objectives
    /// / evidence join to games by <c>game_id</c>, so the account filter (which
    /// reads through the games row) sees the corrected value with no further
    /// writes.</para>
    /// </summary>
    /// <returns>The number of game rows re-stamped.</returns>
    public async Task<int> ReconcileGamePuuidAsync(
        string? riotPuuid,
        CancellationToken cancellationToken = default)
    {
        // Logged out / no resolved account: nothing to reconcile against.
        if (string.IsNullOrWhiteSpace(riotPuuid))
        {
            return 0;
        }

        using var connection = _connectionFactory.CreateConnection();

        // The puuid column was added by an ALTER TABLE migration; on a DB that
        // somehow predates it, skip rather than throw.
        var gameCols = await GetTableColumnsAsync(connection, "games", cancellationToken);
        if (!gameCols.Contains("puuid"))
        {
            return 0;
        }

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            UPDATE games
            SET puuid = $riotPuuid
            WHERE COALESCE(puuid, '') <> ''
              AND puuid <> $riotPuuid
            """;
        cmd.Parameters.AddWithValue("$riotPuuid", riotPuuid);
        var updated = await cmd.ExecuteNonQueryAsync(cancellationToken);

        if (updated > 0)
        {
            _logger.LogInformation(
                "Reconciled {Count} stranded game row(s) to the signed-in Riot PUUID (account-scope back-catalog repair)",
                updated);
        }

        return updated;
    }

    // ── Seeding helpers ──────────────────────────────────────────────

    private async Task ExecuteMigrationSetAsync(
        SqliteConnection connection,
        Schema.VersionedMigration migrationSet,
        CancellationToken cancellationToken)
    {
        foreach (var migration in migrationSet.Statements)
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
                // Column already exists -- expected for previously-migrated databases.
            }
        }

        // v16: the legacy reviewed-encounter rows become ledger rows exactly once per
        // database, right after the ALTER and before the version is recorded, so a
        // fresh DB is a no-op.
        if (migrationSet.Version == Schema.EventCorrectionsSchemaVersion)
        {
            var imported = await EventCorrectionsLegacyImport.RunAsync(connection, cancellationToken);
            if (imported > 0)
                _logger.LogInformation("Imported {Count} legacy reviewed encounters into event_corrections", imported);
        }

        _logger.LogInformation(
            "Applied schema migration set {MigrationName} version {Version}",
            migrationSet.Name,
            migrationSet.Version);
    }

    private static async Task<int> GetAppliedSchemaVersionAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT value
            FROM schema_metadata
            WHERE key = $key
            """;
        cmd.Parameters.AddWithValue("$key", Schema.AppSchemaVersionKey);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is not null && int.TryParse(Convert.ToString(value), out var version)
            ? version
            : 0;
    }

    private static async Task SetAppliedSchemaVersionAsync(
        SqliteConnection connection,
        int version,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO schema_metadata (key, value, updated_at)
            VALUES ($key, $value, $updatedAt)
            ON CONFLICT(key) DO UPDATE SET
                value = excluded.value,
                updated_at = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$key", Schema.AppSchemaVersionKey);
        cmd.Parameters.AddWithValue("$value", version.ToString());
        cmd.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
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
        // 3.14.1: remakes do not count toward an objective's games (re-bases on startup).
        cmd.CommandText = $"""
            UPDATE objectives SET game_count = COALESCE((
                SELECT COUNT(*) FROM game_objectives
                JOIN games g ON g.game_id = game_objectives.game_id
                WHERE game_objectives.objective_id = objectives.id
                  AND COALESCE(g.is_hidden, 0) = 0
                  AND {Repositories.RemakeSql.NotRemake("g.game_id")}
            ), 0)
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task BackfillObjectiveScoreFromPracticedGamesAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        // Only counted games (visible, not a remake) earn practice points, or this floor
        // would hand back the points a remake verdict took away on every launch.
        cmd.CommandText = $"""
            UPDATE objectives
            SET score = MAX(
                COALESCE(score, 0),
                COALESCE((
                    SELECT SUM(CASE WHEN COALESCE(game_objectives.practiced, 0) != 0 THEN 2 ELSE 0 END)
                    FROM game_objectives
                    JOIN games g ON g.game_id = game_objectives.game_id
                    WHERE game_objectives.objective_id = objectives.id
                      AND {Repositories.CountedGamesSql.Counted("g")}
                ), 0)
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // Indexes that must land after the versioned migrations (their columns are
    // added by ALTER TABLE). Each index is individually tolerant: on a DB whose
    // prompt tables still have the pre-v2.15 shape the referenced columns may not
    // exist, and one failed index must not block the others (or startup).
    private async Task CreatePostMigrationIndexesAsync(SqliteConnection connection, CancellationToken ct)
    {
        string[] indexes =
        [
            // Retire the old visible-games indexes: their "WHERE is_hidden = 0"
            // predicate is not implied by the COALESCE(is_hidden, 0) = 0 filter
            // every query actually uses, so SQLite never chose them.
            "DROP INDEX IF EXISTS idx_games_timestamp",
            "DROP INDEX IF EXISTS idx_games_champion",
            Schema.CreateObjectivePromptsIndex,
            Schema.CreatePromptAnswersIndex,
            // Partial indexes on games — is_hidden is added by an ALTER TABLE
            // migration, so these must run here, after migrations, not in
            // AllCreateStatements (which runs before the column exists).
            Schema.CreateGamesTimestampIndex,
            Schema.CreateGamesChampionIndex,
            // Additive lookup indexes (no version bump); see Schema.CreateVodBookmarksGameIndex.
            Schema.CreateVodBookmarksGameIndex,
            Schema.CreateMatchupsGameIndex,
            Schema.CreateMatchupNotesGameIndex,
            Schema.CreateGameObjectivesObjectiveIndex,
            Schema.CreateDerivedEventInstancesGameIndex,
            Schema.CreateGameEventsUnkeyedIndex,
        ];

        foreach (var sql in indexes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.CommandText = sql;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            catch (SqliteException ex)
            {
                _logger.LogWarning(ex, "Post-migration index statement skipped: {Sql}", sql);
            }
        }
    }

}

