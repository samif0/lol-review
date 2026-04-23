#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// Migrates user data from the legacy <c>%LOCALAPPDATA%\LoLReviewData\</c> tree
/// to the new <c>%LOCALAPPDATA%\RevuData\</c> tree on first launch after the
/// Revu rename. The legacy tree is preserved as a timestamped backup so users
/// can recover if something goes wrong.
///
/// Migration is safe to run on every startup: it exits early when the new tree
/// already exists, or when there is no legacy tree to migrate from.
///
/// <para>
/// NEVER overwrites or deletes the legacy DB. The flow is:
/// </para>
/// <list type="number">
///   <item>Copy entire legacy tree into a <c>RevuData.tmp-&lt;guid&gt;</c> staging folder.</item>
///   <item>Rename <c>lol_review.db*</c> to <c>revu.db*</c> inside the staging folder.</item>
///   <item>Open the new DB read-only and smoke-query the <c>games</c> table.</item>
///   <item>Atomically rename the staging folder to the final <c>RevuData</c> path.</item>
///   <item>Rename the legacy folder to <c>LoLReviewData.migrated-backup-&lt;ts&gt;</c>.</item>
/// </list>
/// </summary>
public static class AppDataMigrator
{
    public const string LegacyUserDataFolderName = "LoLReviewData";
    public const string LegacyDatabaseFileName = "lol_review.db";

    public enum MigrationResult
    {
        FreshInstall,
        AlreadyMigrated,
        Migrated,
        SmokeFailedRolledBack,
        CopyFailed,
    }

    public static MigrationResult RunIfNeeded(ILogger logger)
    {
        var localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppDataRoot))
        {
            // No LocalAppData — nothing to migrate. Fresh install behavior.
            return MigrationResult.FreshInstall;
        }

        var newRoot = Path.Combine(localAppDataRoot, "RevuData");
        var newDb = Path.Combine(newRoot, "revu.db");
        var legacyRoot = Path.Combine(localAppDataRoot, LegacyUserDataFolderName);
        var legacyDb = Path.Combine(legacyRoot, LegacyDatabaseFileName);

        if (File.Exists(newDb))
        {
            logger.LogDebug("AppData migration: new DB already exists at {Path}", newDb);
            return MigrationResult.AlreadyMigrated;
        }

        if (!Directory.Exists(legacyRoot) || !File.Exists(legacyDb))
        {
            logger.LogDebug("AppData migration: no legacy data at {Path}", legacyRoot);
            return MigrationResult.FreshInstall;
        }

        var staging = Path.Combine(localAppDataRoot, $"RevuData.tmp-{Guid.NewGuid():N}");

        try
        {
            CopyTreeSkippingMigratedBackups(legacyRoot, staging, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: copy failed from {Source} to {Dest}", legacyRoot, staging);
            TryDeleteDirectory(staging, logger);
            return MigrationResult.CopyFailed;
        }

        // Rename lol_review.db* → revu.db* inside the staging tree.
        var stagingLegacyDb = Path.Combine(staging, LegacyDatabaseFileName);
        var stagingNewDb = Path.Combine(staging, "revu.db");
        try
        {
            if (File.Exists(stagingLegacyDb))
            {
                File.Move(stagingLegacyDb, stagingNewDb);
            }
            TryRename(staging, LegacyDatabaseFileName + "-shm", "revu.db-shm", logger);
            TryRename(staging, LegacyDatabaseFileName + "-wal", "revu.db-wal", logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: renaming DB files in staging failed");
            TryDeleteDirectory(staging, logger);
            return MigrationResult.CopyFailed;
        }

        // Smoke-query the new DB to confirm it is readable and has the games table.
        try
        {
            var connString = new SqliteConnectionStringBuilder
            {
                DataSource = stagingNewDb,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();
            using var conn = new SqliteConnection(connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            var count = cmd.ExecuteScalar() is long c ? c : -1;
            logger.LogInformation("AppData migration: staging DB smoke-query found {Count} games", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: staging DB smoke-query failed; rolling back");
            TryDeleteDirectory(staging, logger);
            return MigrationResult.SmokeFailedRolledBack;
        }

        // Atomically publish the staging folder as RevuData.
        try
        {
            Directory.Move(staging, newRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AppData migration: could not publish staging as {Dest}", newRoot);
            TryDeleteDirectory(staging, logger);
            return MigrationResult.CopyFailed;
        }

        // Rename the legacy folder to a timestamped backup. Best-effort — if this
        // fails the migration still succeeded; the legacy folder just stays in
        // place and we never touch it again (because newDb now exists).
        var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var backupFolder = Path.Combine(localAppDataRoot, $"{LegacyUserDataFolderName}.migrated-backup-{ts}");
        try
        {
            Directory.Move(legacyRoot, backupFolder);
            logger.LogInformation("AppData migration: legacy folder preserved at {Path}", backupFolder);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AppData migration: could not rename legacy folder; leaving in place");
        }

        logger.LogInformation("AppData migration: completed. New root = {Path}", newRoot);
        return MigrationResult.Migrated;
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private static void CopyTreeSkippingMigratedBackups(string source, string dest, ILogger logger)
    {
        Directory.CreateDirectory(dest);

        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, dir);
            // Skip any stale migration staging folder inside the legacy root
            if (rel.StartsWith("RevuData.tmp-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            Directory.CreateDirectory(Path.Combine(dest, rel));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            if (rel.StartsWith("RevuData.tmp-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var targetPath = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(file, targetPath, overwrite: false);
        }
    }

    private static void TryRename(string dir, string oldName, string newName, ILogger logger)
    {
        var src = Path.Combine(dir, oldName);
        var dst = Path.Combine(dir, newName);
        if (!File.Exists(src))
        {
            return;
        }
        try
        {
            File.Move(src, dst);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "AppData migration: could not rename {Old} to {New}", oldName, newName);
        }
    }

    private static void TryDeleteDirectory(string path, ILogger logger)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AppData migration: could not clean up staging folder {Path}", path);
        }
    }
}
