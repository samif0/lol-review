#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// Renames the SQLite database file from <c>lol_review.db</c> to <c>revu.db</c>
/// inside the user data folder (<c>%LOCALAPPDATA%\LoLReviewData\</c>) on the
/// first launch after the Revu rename.
///
/// <para>
/// <b>Why only the DB file and not the whole folder:</b> the data folder also
/// holds the Coach sidecar's embedded Python distribution (<c>coach/core/</c>,
/// <c>coach/ml/</c> — ~265 MB combined), credential material, recorded VOD
/// clips, and other large artifacts. A full-tree copy can fail on any locked
/// file (Python DLLs loaded by a running sidecar, video clips open in an
/// external player, etc.), and the migration has no meaningful user-visible
/// benefit. The folder name is AppData-hidden, never shown in UI.
/// </para>
///
/// <para>
/// The DB rename is the only file operation Phase 2 actually needs — the
/// filename is referenced in docs, logs, and developer tooling, so moving it
/// to <c>revu.db</c> gives the rename end-to-end consistency without the
/// risk of the tree copy.
/// </para>
///
/// <para>
/// NEVER overwrites or deletes the legacy DB. The flow is:
/// </para>
/// <list type="number">
///   <item>If <c>revu.db</c> already exists, exit (<see cref="MigrationResult.AlreadyMigrated"/>).</item>
///   <item>If <c>lol_review.db</c> does not exist, exit (<see cref="MigrationResult.FreshInstall"/>).</item>
///   <item>Copy (not move) <c>lol_review.db</c> to <c>revu.db.tmp-&lt;guid&gt;</c>.</item>
///   <item>Smoke-query the copy read-only to confirm the games table exists.</item>
///   <item>Atomically rename the copy to <c>revu.db</c>.</item>
///   <item>Leave <c>lol_review.db</c> in place as a self-documenting backup.</item>
/// </list>
///
/// <para>
/// WAL/SHM files are not copied — SQLite recreates them on first open.
/// Leaving the legacy <c>lol_review.db</c> in the folder means users can
/// recover by renaming it back if anything goes wrong later.
/// </para>
/// </summary>
public static class AppDataMigrator
{
    public const string LegacyDatabaseFileName = "lol_review.db";
    public const string NewDatabaseFileName = "revu.db";

    public enum MigrationResult
    {
        FreshInstall,
        AlreadyMigrated,
        Migrated,
        SmokeFailedRolledBack,
        CopyFailed,
    }

    /// <summary>
    /// Runs the DB-file rename if needed. Safe to call on every startup;
    /// idempotent after the first successful run.
    /// </summary>
    /// <param name="userDataRoot">
    /// Absolute path to the user data folder (typically
    /// <c>%LOCALAPPDATA%\LoLReviewData</c>). Must exist or migration returns
    /// <see cref="MigrationResult.FreshInstall"/>.
    /// </param>
    public static MigrationResult RunIfNeeded(string userDataRoot, ILogger logger)
    {
        if (string.IsNullOrEmpty(userDataRoot) || !Directory.Exists(userDataRoot))
        {
            logger.LogDebug("AppData migration: user data root does not exist yet ({Path})", userDataRoot);
            return MigrationResult.FreshInstall;
        }

        var newDb = Path.Combine(userDataRoot, NewDatabaseFileName);
        var legacyDb = Path.Combine(userDataRoot, LegacyDatabaseFileName);

        if (File.Exists(newDb))
        {
            return MigrationResult.AlreadyMigrated;
        }

        if (!File.Exists(legacyDb))
        {
            return MigrationResult.FreshInstall;
        }

        var tempDb = Path.Combine(userDataRoot, $"{NewDatabaseFileName}.tmp-{Guid.NewGuid():N}");

        try
        {
            File.Copy(legacyDb, tempDb, overwrite: false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: copy {Source} -> {Temp} failed", legacyDb, tempDb);
            TryDelete(tempDb, logger);
            return MigrationResult.CopyFailed;
        }

        try
        {
            var connString = new SqliteConnectionStringBuilder
            {
                DataSource = tempDb,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using (var conn = new SqliteConnection(connString))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM games";
                var count = cmd.ExecuteScalar() is long c ? c : -1;
                logger.LogInformation("AppData migration: staged DB contains {Count} games", count);
            }

            // Microsoft.Data.Sqlite pools native handles by connection string
            // even after Dispose. Release them so File.Move below is not
            // blocked by a lingering file lock.
            SqliteConnection.ClearAllPools();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: staged DB smoke-query failed; rolling back");
            SqliteConnection.ClearAllPools();
            TryDelete(tempDb, logger);
            TryDelete(tempDb + "-shm", logger);
            TryDelete(tempDb + "-wal", logger);
            return MigrationResult.SmokeFailedRolledBack;
        }

        try
        {
            File.Move(tempDb, newDb);
            // Clean up any sidecar files left by the readonly smoke-query.
            // These are harmless but messy; SQLite recreates fresh ones
            // against revu.db on first real open.
            TryDelete(tempDb + "-shm", logger);
            TryDelete(tempDb + "-wal", logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: rename {Temp} -> {New} failed", tempDb, newDb);
            TryDelete(tempDb, logger);
            TryDelete(tempDb + "-shm", logger);
            TryDelete(tempDb + "-wal", logger);
            return MigrationResult.CopyFailed;
        }

        logger.LogInformation(
            "AppData migration: renamed {Legacy} -> {New} (legacy file preserved for recovery)",
            legacyDb, newDb);
        return MigrationResult.Migrated;
    }

    private static void TryDelete(string path, ILogger logger)
    {
        if (!File.Exists(path))
        {
            return;
        }
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AppData migration: could not clean up temporary file {Path}", path);
        }
    }
}
