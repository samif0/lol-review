#nullable enable

namespace Revu.Core.Services;

/// <summary>
/// Database backup service. Provides both automatic safety backups (always-on, no config needed)
/// and user-configured backups to a chosen folder.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Create a timestamped safety backup of the DB in the user-data backups directory.
    /// Called automatically before schema migrations and on startup.
    /// Keeps only the 3 most recent safety backups. Requires no user configuration.
    /// </summary>
    Task CreateSafetyBackupAsync(string reason);

    /// <summary>
    /// Copy the .db file to the user-configured backup folder.
    /// Keeps only the 5 most recent backups.
    /// Called on startup if backup_enabled is true in config.
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
    /// Leaves the backups folder and Ascent VOD contents untouched.
    /// Caller is responsible for quitting the app afterward.
    /// </summary>
    Task<ResetResult> ResetAllDataAsync();

    /// <summary>
    /// Restore the live DB from a backup file. Backs up the current DB first
    /// (as <c>pre_restore_</c>) so the action is itself reversible.
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
