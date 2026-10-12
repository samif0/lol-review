#nullable enable

using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;

namespace Revu.Core.Services;

/// <summary>Reset and restore: the only paths that replace or remove the live database.</summary>
public sealed partial class BackupService
{
    public async Task<ResetResult> ResetAllDataAsync()
    {
        var dbPath = _connectionFactory.DatabasePath;
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");

        string backupFilePath;
        try
        {
            Directory.CreateDirectory(backupDir);
            backupFilePath = ResolveUniqueBackupPath(backupDir, PreResetPrefix);
            if (File.Exists(dbPath))
            {
                // Online backup: includes every write still in revu.db-wal.
                await Task.Run(() => CreateSnapshotOfLiveDatabase(dbPath, backupFilePath)).ConfigureAwait(false);
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

        // Wipe config too — gets the user back to a clean onboarding path. The
        // config that belongs to THIS database is the one beside it: never the
        // process-global AppDataPaths.ConfigPath, which is the live user's file
        // even when the connection factory points at a temp DB (a test scope).
        // That exact mistake deleted the real config.json on every local
        // `dotnet test` run for months (2026-07-10, 2026-09-05).
        try
        {
            var configPath = Path.Combine(
                Path.GetDirectoryName(dbPath) ?? AppDataPaths.UserDataRoot,
                Path.GetFileName(AppDataPaths.ConfigPath));
            if (File.Exists(configPath)) File.Delete(configPath);
            // ConfigService keeps a rolling config.json.bak and restores from it
            // when config.json is missing — a reset has to take that too.
            var backupCopy = configPath + ".bak";
            if (File.Exists(backupCopy)) File.Delete(backupCopy);
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
    /// Used by reset; restore never deletes before its replacement is in place.
    /// </summary>
    private async Task<List<string>> TryDeleteSqliteFamilyAsync(string dbPath)
    {
        ReleaseConnections();

        var failed = new List<string>();
        foreach (var path in new[] { dbPath + "-wal", dbPath + "-shm", dbPath })
        {
            if (!File.Exists(path)) continue;
            if (!await RetryFileOperationAsync(() => File.Delete(path), path).ConfigureAwait(false))
                failed.Add(Path.GetFileName(path));
        }
        return failed;
    }

    public async Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath)
    {
        // Only a backup from Revu's own list can be restored. The request names one; the
        // file itself is looked up here, so a renderer can never swap in an arbitrary or
        // network file as the database (and a UNC path is never even opened).
        var requested = (backupFilePath ?? "").Trim();
        var candidate = requested.Length == 0
            ? null
            : (await ListBackupsAsync().ConfigureAwait(false)).FirstOrDefault(b => SameBackup(b, requested));
        if (candidate is null)
        {
            return new RestoreResult(false, null, "Backup file not found. Choose a backup from the list.");
        }

        var dbPath = _connectionFactory.DatabasePath;
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");
        Directory.CreateDirectory(backupDir);

        // 1) Stage a copy next to the live file and validate it (SQLite header,
        //    quick_check, a Revu schema no newer than this build) BEFORE the live
        //    database is touched. A bad backup leaves everything as it was.
        var staged = Path.Combine(dataDir, $"{Path.GetFileName(dbPath)}.restoring-{Guid.NewGuid():N}.tmp");
        try
        {
            await Task.Run(() => StageAndValidate(candidate.FilePath, staged)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or SqliteException or IOException or UnauthorizedAccessException)
        {
            DeleteDatabaseFiles(staged);
            _logger.LogWarning(ex, "Restore refused: {File} did not validate", candidate.FileName);
            var reason = ex is InvalidDataException ? ex.Message : "That backup could not be read, so nothing was changed.";
            return new RestoreResult(false, null, reason);
        }

        // 2) Back up the current DB first so the restore is itself reversible.
        string? preRestorePath = null;
        var snapshotIsOnlineBackup = true;
        if (File.Exists(dbPath))
        {
            try
            {
                preRestorePath = ResolveUniqueBackupPath(backupDir, PreRestorePrefix);
                snapshotIsOnlineBackup = await Task.Run(() => CreateSnapshotOfLiveDatabase(dbPath, preRestorePath)).ConfigureAwait(false);
                _logger.LogInformation("Pre-restore backup: {Path}", preRestorePath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Pre-restore backup failed");
                DeleteDatabaseFiles(staged);
                return new RestoreResult(false, null, "Could not back up your current data, so nothing was changed.");
            }
        }

        // 3) Swap. Every pooled handle is released, the live family is moved aside (not
        //    deleted), and the validated copy takes the live name. Any failure moves the
        //    originals back, so the live database is never left missing or half-replaced.
        //    The old -wal never sits beside the restored file, so it cannot be replayed
        //    into it.
        var token = Guid.NewGuid().ToString("N");
        var moved = new List<(string Original, string Aside)>();
        ReleaseConnections();
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            if (!File.Exists(path)) continue;
            var aside = $"{path}.replaced-{token}.tmp";
            if (!await RetryFileOperationAsync(() => File.Move(path, aside), path).ConfigureAwait(false))
            {
                RollBack(moved);
                DeleteDatabaseFiles(staged);
                _logger.LogError("Restore could not move {File} aside", Path.GetFileName(path));
                return new RestoreResult(false, preRestorePath,
                    $"Could not replace the current database because {Path.GetFileName(path)} is in use. Nothing was changed. Try again in a moment.");
            }
            moved.Add((path, aside));
        }

        try
        {
            File.Move(staged, dbPath, overwrite: false);
            _logger.LogInformation("Restored DB from {Source}", candidate.FilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Moving the restored database into place failed");
            RollBack(moved);
            DeleteDatabaseFiles(staged);
            return new RestoreResult(false, preRestorePath,
                "Could not put the backup in place. Nothing was changed, and your pre-restore backup is safe.");
        }

        // 4) The replaced files are covered by the pre-restore backup; drop them. When that
        //    backup had to fall back to a plain file copy, writes that were only in the old
        //    -wal may be missing from it, so the replaced files stay beside the database.
        if (snapshotIsOnlineBackup)
        {
            foreach (var (_, aside) in moved) DeleteQuietly(aside);
        }
        else if (moved.Count > 0)
        {
            _logger.LogWarning("The pre-restore backup is a plain file copy; keeping the replaced files {Files}",
                string.Join(", ", moved.Select(m => Path.GetFileName(m.Aside))));
        }

        // Clear pools AGAIN after the file is back in place. Any connection
        // that gets opened next must see the restored file, not cached state
        // from before the swap. In production the app relaunches right after
        // this call; tests hold the same process and would otherwise hit
        // "no such table" against stale shared-cache connections.
        ReleaseConnections();
        return new RestoreResult(true, preRestorePath, null);
    }

    private static bool SameBackup(BackupFileInfo backup, string requested)
    {
        if (string.Equals(backup.FileName, requested, StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            // String normalization only; nothing here touches the file system.
            return string.Equals(Path.GetFullPath(backup.FilePath), Path.GetFullPath(requested),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");

    private static void StageAndValidate(string backupPath, string staged)
    {
        using (var input = new FileStream(backupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var header = new byte[SqliteHeader.Length];
            if (input.Read(header, 0, header.Length) != header.Length || !header.AsSpan().SequenceEqual(SqliteHeader))
                throw new InvalidDataException("That file is not a Revu backup, so nothing was changed.");
            using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.Position = 0;
            input.CopyTo(output);
            output.Flush(flushToDisk: true);
        }

        using var connection = OpenFile(staged, SqliteOpenMode.ReadWrite);
        UseRollbackJournal(connection);
        if (QuickCheck(connection) is not null)
            throw new InvalidDataException("That backup is damaged and can't be restored. Nothing was changed.");
        using (var games = connection.CreateCommand())
        {
            games.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'games'";
            if (Convert.ToInt64(games.ExecuteScalar()) == 0)
                throw new InvalidDataException("That file is not a Revu backup, so nothing was changed.");
        }
        if (DatabaseSchemaCompatibility.ReadVersion(connection) > Schema.CurrentAppSchemaVersion)
            throw new InvalidDataException("That backup was made by a newer version of Revu. Update Revu, then restore it.");
    }

    /// <summary>
    /// The pre-reset / pre-restore snapshot of the live database. The user may be
    /// replacing it BECAUSE it is damaged, so a failed integrity check keeps the copy
    /// rather than blocking, and if even the online backup cannot read it, the main file
    /// is copied after a checkpoint as the last resort. Returns false when that fallback
    /// was used.
    /// </summary>
    private bool CreateSnapshotOfLiveDatabase(string dbPath, string destination)
    {
        try
        {
            CreateVerifiedCopy(dbPath, destination, requireIntact: false);
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or InvalidDataException)
        {
            _logger.LogWarning(ex, "Online backup of the live database failed; copying the file instead");
            CheckpointDatabase();
            File.Copy(dbPath, destination, overwrite: false);
            return false;
        }
    }

    private void CheckpointDatabase()
    {
        try
        {
            using var conn = OpenFile(_connectionFactory.DatabasePath, SqliteOpenMode.ReadWrite);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var reader = cmd.ExecuteReader();
            if (reader.Read() && reader.GetInt64(0) != 0)
                _logger.LogWarning("WAL checkpoint was blocked by another connection; the file copy may miss recent writes");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WAL checkpoint failed; the file copy may miss recent writes");
        }
    }

    private void RollBack(List<(string Original, string Aside)> moved)
    {
        for (var i = moved.Count - 1; i >= 0; i--)
        {
            var (original, aside) = moved[i];
            try { File.Move(aside, original, overwrite: false); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not move {Aside} back to {Original}; it is kept beside the database", aside, original);
            }
        }
    }

    private static void ReleaseConnections()
    {
        SqliteConnection.ClearAllPools();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        SqliteConnection.ClearAllPools();
    }

    /// <summary>~3s budget: Windows file locks (AV scans, the indexer, pending
    /// finalizers) can linger after the pools are cleared.</summary>
    private async Task<bool> RetryFileOperationAsync(Action operation, string path)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                operation();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "File attempt {Attempt} failed for {Path}", attempt + 1, path);
                await Task.Delay(250).ConfigureAwait(false);
                SqliteConnection.ClearAllPools();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unexpected file failure for {Path}", path);
                return false;
            }
        }
        return false;
    }
}
