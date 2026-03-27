#nullable enable

namespace LoLReview.Core.Services;

/// <summary>
/// Database backup service. Provides both automatic safety backups (always-on, no config needed)
/// and user-configured backups to a chosen folder.
/// </summary>
public interface IBackupService
{
    /// <summary>
    /// Create a timestamped safety backup of the DB in the data/backups/ directory.
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
}
