#nullable enable

using Revu.Core.Data;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Database backup service. Two modes:
/// 1. Safety backups: always-on, stored next to the live DB in the user data root,
///    no config needed, keep last 3.
/// 2. User backups: stored in user-configured folder, keep last 5.
/// Safety backups run before every schema migration and on startup.
/// NEVER overwrites, deletes, or modifies the live database file.
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>Maximum number of safety backup files to keep.</summary>
    private const int MaxSafetyBackups = 3;

    /// <summary>Maximum number of user backup files to keep.</summary>
    private const int MaxUserBackups = 5;

    private const string SafetyPrefix = "safety_backup_";
    private const string UserPrefix = "lol_review_backup_";

    /// <summary>
    /// Pre-migration backups dropped by the coach migration layer. They share
    /// the backups/ directory but aren't touched by the safety/user pruners,
    /// so they accumulate indefinitely. We cap them at 3 the same way.
    /// </summary>
    private const string CoachMigrationPrefix = "coach-pre-migration-";
    private const int MaxCoachMigrationBackups = 3;

    private readonly IConfigService _config;
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly ILogger<BackupService> _logger;

    public BackupService(
        IConfigService config,
        IDbConnectionFactory connectionFactory,
        ILogger<BackupService> logger)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CreateSafetyBackupAsync(string reason)
    {
        var dbPath = _connectionFactory.DatabasePath;
        if (!File.Exists(dbPath))
        {
            _logger.LogDebug("No database file to back up at {Path}", dbPath);
            return;
        }

        // Safety backups go in the user-data backups directory next to the DB
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");

        var backupName = $"{SafetyPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db";
        var dest = Path.Combine(backupDir, backupName);

        try
        {
            // v2.15.4: force a WAL checkpoint before copying so the backup
            // actually contains the latest writes. Without this, anything
            // written since the last implicit checkpoint lives only in
            // revu.db-wal and File.Copy of the main .db misses it.
            CheckpointDatabase();
            Directory.CreateDirectory(backupDir);
            File.Copy(dbPath, dest, overwrite: false);
            var fileSize = new FileInfo(dbPath).Length;
            _logger.LogInformation(
                "Safety backup created: {Dest} (reason: {Reason}, size: {Size} bytes)",
                dest, reason, fileSize);
        }
        catch (IOException ex) when (ex.Message.Contains("already exists"))
        {
            _logger.LogDebug("Safety backup already exists for this second, skipping");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Safety backup failed (reason: {Reason})", reason);
            return;
        }

        await PruneBackupsAsync(backupDir, SafetyPrefix, MaxSafetyBackups).ConfigureAwait(false);

        // Also prune coach-migration leftovers. They land in the same folder
        // but predate the safety pruner's prefix filter, so without this call
        // they just pile up over time.
        await PruneBackupsAsync(backupDir, CoachMigrationPrefix, MaxCoachMigrationBackups)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RunBackupAsync()
    {
        if (!_config.BackupEnabled)
        {
            _logger.LogDebug("User database backup is disabled");
            return;
        }

        var folder = _config.BackupFolder;
        if (string.IsNullOrEmpty(folder))
        {
            _logger.LogDebug("Backup enabled but no folder configured");
            return;
        }

        var dbPath = _connectionFactory.DatabasePath;
        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("Database file not found for backup");
            return;
        }

        var backupName = $"{UserPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db";
        var dest = Path.Combine(folder, backupName);

        try
        {
            Directory.CreateDirectory(folder);
            File.Copy(dbPath, dest, overwrite: false);
            _logger.LogInformation("Database backed up to {Dest}", dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database backup failed");
            return;
        }

        await PruneBackupsAsync(folder, UserPrefix, MaxUserBackups).ConfigureAwait(false);
    }

    // ── v2.15.0 reset + revert ──────────────────────────────────────

    private const string PreResetPrefix   = "pre_reset_";
    private const string PreRestorePrefix = "pre_restore_";

    public Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync()
    {
        return Task.Run<IReadOnlyList<BackupFileInfo>>(() =>
        {
            var dbPath = _connectionFactory.DatabasePath;
            var dataDir = Path.GetDirectoryName(dbPath);
            if (string.IsNullOrEmpty(dataDir)) return Array.Empty<BackupFileInfo>();
            var backupDir = Path.Combine(dataDir, "backups");
            if (!Directory.Exists(backupDir)) return Array.Empty<BackupFileInfo>();

            var results = new List<BackupFileInfo>();
            foreach (var file in Directory.EnumerateFiles(backupDir, "*.db"))
            {
                var fi = new FileInfo(file);
                // Derive a timestamp from the filename when possible
                // (our backups embed yyyyMMdd_HHmmss); fall back to mtime.
                var timestamp = TryParseBackupTimestamp(fi.Name) ?? fi.LastWriteTime;
                var label = BuildBackupLabel(fi.Name, timestamp, fi.Length);
                results.Add(new BackupFileInfo(
                    FilePath: fi.FullName,
                    FileName: fi.Name,
                    Timestamp: timestamp,
                    FileSizeBytes: fi.Length,
                    Label: label));
            }

            // Newest first so the UI's "most recent" is top of list.
            return results.OrderByDescending(b => b.Timestamp).ToList();
        });
    }

    public async Task<ResetResult> ResetAllDataAsync()
    {
        var dbPath = _connectionFactory.DatabasePath;
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");

        string backupFilePath;
        try
        {
            // v2.15.4: checkpoint first so WAL-resident writes (everything
            // between the last implicit checkpoint and now) land in the
            // main .db file BEFORE the copy. Otherwise the pre-reset
            // backup will be silently stale — missing any in-session edits.
            CheckpointDatabase();
            Directory.CreateDirectory(backupDir);
            backupFilePath = ResolveUniqueBackupPath(backupDir, PreResetPrefix);
            if (File.Exists(dbPath))
            {
                File.Copy(dbPath, backupFilePath, overwrite: false);
                _logger.LogInformation("Pre-reset backup: {Path}", backupFilePath);
            }
            else
            {
                _logger.LogWarning("Reset requested but no DB at {Path}", dbPath);
                backupFilePath = "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-reset backup failed");
            return new ResetResult(false, "", "Could not create a safety backup. Reset aborted so no data is lost.");
        }

        var deleteFailed = await TryDeleteSqliteFamilyAsync(dbPath).ConfigureAwait(false);
        if (deleteFailed.Count > 0)
        {
            _logger.LogError("Reset could not delete {Files}", string.Join(", ", deleteFailed));
            return new ResetResult(
                false,
                backupFilePath,
                $"Could not delete {string.Join(", ", deleteFailed)}. Close the app fully, then restore from the pre-reset backup if needed. Backup saved to: {backupFilePath}");
        }

        // Wipe config too — gets the user back to a clean onboarding path.
        try
        {
            var configPath = AppDataPaths.ConfigPath;
            if (File.Exists(configPath)) File.Delete(configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Config wipe failed (non-fatal)");
        }

        return new ResetResult(true, backupFilePath, null);
    }

    /// <summary>
    /// Clears SQLite pools + forces finalizers, then tries to delete the live
    /// DB plus its WAL/SHM sidecars. Returns the names of any files that
    /// remained after the retry budget — empty list means full success.
    /// Centralised so reset and restore use the same timing semantics.
    /// </summary>
    private async Task<List<string>> TryDeleteSqliteFamilyAsync(string dbPath)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var failed = new List<string>();
        foreach (var path in new[] { dbPath + "-wal", dbPath + "-shm", dbPath })
        {
            if (!File.Exists(path)) continue;

            var ok = false;
            // ~3s total budget. Windows file locks (AV scans, indexer,
            // pending finalizers) can linger past the old 750ms ceiling.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    File.Delete(path);
                    ok = true;
                    break;
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Delete attempt {Attempt} failed for {Path}", attempt + 1, path);
                    await Task.Delay(250).ConfigureAwait(false);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                }
                catch (UnauthorizedAccessException ex)
                {
                    // Same lock category as IOException on some Windows builds.
                    _logger.LogWarning(ex, "Delete attempt {Attempt} unauthorized for {Path}", attempt + 1, path);
                    await Task.Delay(250).ConfigureAwait(false);
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Unexpected delete failure for {Path}", path);
                    break;
                }
            }

            if (!ok) failed.Add(Path.GetFileName(path));
        }
        return failed;
    }

    /// <summary>
    /// Returns a fresh path under <paramref name="backupDir"/> using the
    /// timestamp-suffixed naming scheme. If the base name already exists
    /// (e.g. user pressed reset twice in the same second), appends "_2",
    /// "_3", … so File.Copy with overwrite:false never collides.
    /// </summary>
    private static string ResolveUniqueBackupPath(string backupDir, string prefix)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var basePath = Path.Combine(backupDir, $"{prefix}{stamp}.db");
        if (!File.Exists(basePath)) return basePath;

        for (int suffix = 2; suffix < 100; suffix++)
        {
            var candidate = Path.Combine(backupDir, $"{prefix}{stamp}_{suffix}.db");
            if (!File.Exists(candidate)) return candidate;
        }
        return basePath; // fall back; copy will surface the collision error.
    }

    public async Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath)
    {
        if (string.IsNullOrEmpty(backupFilePath) || !File.Exists(backupFilePath))
        {
            return new RestoreResult(false, null, "Backup file not found.");
        }

        var dbPath = _connectionFactory.DatabasePath;
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupDir);

        // Back up the current DB first so the restore is itself reversible.
        string? preRestorePath = null;
        try
        {
            // v2.15.4: checkpoint before backing up — see CreateSafetyBackupAsync.
            CheckpointDatabase();
            if (File.Exists(dbPath))
            {
                preRestorePath = ResolveUniqueBackupPath(backupDir, PreRestorePrefix);
                File.Copy(dbPath, preRestorePath, overwrite: false);
                _logger.LogInformation("Pre-restore backup: {Path}", preRestorePath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pre-restore backup failed");
            return new RestoreResult(false, null, "Could not back up your current DB. Restore aborted.");
        }

        var failedRestore = await TryDeleteSqliteFamilyAsync(dbPath).ConfigureAwait(false);
        if (failedRestore.Count > 0)
        {
            _logger.LogError("Restore could not delete {Files}", string.Join(", ", failedRestore));
            return new RestoreResult(false, preRestorePath,
                $"Could not replace the current database ({string.Join(", ", failedRestore)} locked). Your pre-restore snapshot is safe.");
        }

        try
        {
            File.Copy(backupFilePath, dbPath, overwrite: false);
            _logger.LogInformation("Restored DB from {Source}", backupFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy backup into place failed");
            return new RestoreResult(false, preRestorePath, "Could not copy the backup into place. Your pre-restore snapshot is safe.");
        }

        // Clear pools AGAIN after the file is back in place. Any connection
        // that gets opened next must see the restored file, not the old
        // cached empty-DB state from just before the delete.
        //
        // In production the app exits right after this call (see
        // SettingsViewModel) — so this is belt-and-suspenders. But tests
        // hold the same process and would otherwise hit "no such table"
        // against stale cached connections under SqliteCacheMode.Shared.
        // Combined pool clear + GC releases the shared-cache in-process
        // handle so the next CreateConnection sees the restored schema.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        await Task.CompletedTask;
        return new RestoreResult(true, preRestorePath, null);
    }

    // filename format: <prefix>yyyyMMdd_HHmmss.db
    private static DateTime? TryParseBackupTimestamp(string fileName)
    {
        // Strip the extension and any prefix word before the last underscore chunk.
        var core = Path.GetFileNameWithoutExtension(fileName);
        // Last 15 chars should be yyyyMMdd_HHmmss if our format holds.
        if (core.Length < 15) return null;
        var tail = core[^15..];
        if (DateTime.TryParseExact(tail, "yyyyMMdd_HHmmss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string BuildBackupLabel(string fileName, DateTime timestamp, long sizeBytes)
    {
        var kind = fileName.StartsWith(PreResetPrefix, StringComparison.OrdinalIgnoreCase)
            ? "Pre-reset"
            : fileName.StartsWith(PreRestorePrefix, StringComparison.OrdinalIgnoreCase)
                ? "Pre-restore"
                : fileName.StartsWith(SafetyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? "Safety"
                    : fileName.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase)
                        ? "User backup"
                        : fileName.StartsWith(CoachMigrationPrefix, StringComparison.OrdinalIgnoreCase)
                            ? "Coach migration"
                            : "Backup";
        var mb = sizeBytes / (1024.0 * 1024.0);
        return $"{kind} — {timestamp:MMM d yyyy, h:mm tt} — {mb:F1} MB";
    }

    /// <summary>
    /// v2.15.4: force a full WAL checkpoint so all pending writes land in the
    /// main .db file. Critical before any File.Copy of the DB — otherwise the
    /// copy misses everything in the WAL and the resulting backup is silently
    /// stale.
    /// </summary>
    private void CheckpointDatabase()
    {
        // If the DB file doesn't exist, don't create an empty one just to
        // checkpoint nothing. Callers guard their File.Copy on existence
        // already, so this is a no-op in that case.
        if (!File.Exists(_connectionFactory.DatabasePath)) return;

        try
        {
            using var conn = _connectionFactory.CreateConnection();
            using var cmd = conn.CreateCommand();
            // TRUNCATE: writes WAL into main DB + truncates WAL file to zero
            // length. Blocks until done, which is what we want here.
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint failed; backup may be stale");
        }
    }

    // ── Private helpers ─────────────────────────────────────────────

    private Task PruneBackupsAsync(string folder, string prefix, int maxKeep)
    {
        return Task.Run(() =>
        {
            try
            {
                var backups = new DirectoryInfo(folder)
                    .EnumerateFiles($"{prefix}*.db")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                while (backups.Count > maxKeep)
                {
                    var oldest = backups[0];
                    backups.RemoveAt(0);
                    try
                    {
                        oldest.Delete();
                        _logger.LogInformation("Pruned old backup: {Name}", oldest.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not delete old backup {Name}", oldest.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Backup pruning failed");
            }
        });
    }
}
