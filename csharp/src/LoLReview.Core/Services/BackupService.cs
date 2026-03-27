#nullable enable

using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Automatic database backup on startup.
/// Ported from Python main.py App._maybe_backup_database.
/// </summary>
public sealed class BackupService : IBackupService
{
    /// <summary>Maximum number of backup files to keep.</summary>
    private const int MaxBackups = 5;

    /// <summary>Backup filename prefix for glob matching.</summary>
    private const string BackupPrefix = "lol_review_backup_";

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
    public async Task RunBackupAsync()
    {
        if (!_config.BackupEnabled)
        {
            _logger.LogDebug("Database backup is disabled");
            return;
        }

        var folder = _config.BackupFolder;
        if (string.IsNullOrEmpty(folder))
        {
            _logger.LogDebug("Backup enabled but no folder configured");
            return;
        }

        // Determine the database file path from the connection factory
        var dbPath = GetDatabasePath();
        if (dbPath is null || !File.Exists(dbPath))
        {
            _logger.LogWarning("Database file not found for backup");
            return;
        }

        var backupName = $"{BackupPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db";
        var dest = Path.Combine(folder, backupName);

        try
        {
            Directory.CreateDirectory(folder);
            File.Copy(dbPath, dest, overwrite: true);
            _logger.LogInformation("Database backed up to {Dest}", dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database backup failed");
            return;
        }

        // Prune: keep only the most recent backups
        await PruneBackupsAsync(folder).ConfigureAwait(false);
    }

    // ── Private helpers ─────────────────────────────────────────────

    private string? GetDatabasePath()
    {
        // The default database location
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultPath = Path.Combine(localAppData, "LoLReview", "lol_review.db");
        if (File.Exists(defaultPath)) return defaultPath;

        return null;
    }

    private Task PruneBackupsAsync(string folder)
    {
        return Task.Run(() =>
        {
            try
            {
                var backups = new DirectoryInfo(folder)
                    .EnumerateFiles($"{BackupPrefix}*.db")
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                while (backups.Count > MaxBackups)
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
