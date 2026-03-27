#nullable enable

namespace LoLReview.Core.Services;

/// <summary>
/// Automatic database backup on startup.
/// Ported from Python main.py App._maybe_backup_database.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Copy the .db file to the configured backup folder.
    /// Keeps only the 5 most recent backups.
    /// Called on startup if backup_enabled is true.
    /// </summary>
    Task RunBackupAsync();
}
