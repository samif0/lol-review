#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Database backup service. Provides both automatic safety backups (always-on, no config needed)
/// and user-configured backups to a chosen folder.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Create a timestamped safety backup of the DB in the user-data backups directory
    /// (an SQLite online backup that passed quick_check). Taken before the first real
    /// write of a session and before each game delete. Safety backups keep the last 3,
    /// the newest per day for 7 days and the newest per week for 4 weeks; a reason
    /// starting with "delete-" files the backup under a separate game-delete budget.
    /// Requires no user configuration.
    /// </summary>
    Task CreateSafetyBackupAsync(string reason);

    /// <summary>
    /// Back up the DB into the user-configured backup folder when "Create automatic
    /// backups" is on, at most once a day, with its own tiered retention. Run on
    /// startup by the sidecar.
    /// </summary>
    Task RunBackupAsync();

    // ── v2.15.0 reset + revert ──────────────────────────────────────

    /// <summary>
    /// List all backup .db files in the user-data backups directory, newest
    /// first. Returned records include the full file path + a human-readable
    /// timestamp derived from the filename or file mtime.
    /// </summary>
    Task<IReadOnlyList<BackupFileInfo>> ListBackupsAsync();

    /// <summary>
    /// Perform a user-initiated full reset:
    /// 1. Copy the live DB to a pre-reset backup (prefix <c>pre_reset_</c>,
    ///    so the standard safety-backup pruner leaves it alone).
    /// 2. Delete <c>revu.db</c> (and its sibling -shm/-wal files) + config.json.
    /// Leaves the backups folder and source recording files untouched.
    /// Caller is responsible for quitting the app afterward.
    /// </summary>
    Task<ResetResult> ResetAllDataAsync();

    /// <summary>
    /// Restore the live DB from a backup in <see cref="ListBackupsAsync"/> (matched by
    /// path or file name; any other path is refused). The backup is staged and validated
    /// (SQLite header, quick_check, schema no newer than this build) before the live DB
    /// is touched, the current DB is backed up first (as <c>pre_restore_</c>) so the
    /// action is itself reversible, and the swap rolls back on failure.
    /// Caller is responsible for quitting the app afterward.
    /// </summary>
    Task<RestoreResult> RestoreFromBackupAsync(string backupFilePath);
}

/// <summary>Metadata for a backup file surfaced in the restore picker.</summary>
public sealed record BackupFileInfo(
    string FilePath,
    string FileName,
    DateTime Timestamp,
    long FileSizeBytes,
    string Label);

public sealed record ResetResult(bool Success, string BackupFilePath, string? ErrorMessage);
public sealed record RestoreResult(bool Success, string? PreRestoreBackupFilePath, string? ErrorMessage);
