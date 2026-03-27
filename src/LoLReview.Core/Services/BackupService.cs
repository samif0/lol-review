#nullable enable

using LoLReview.Core.Data;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Services;

/// <summary>
/// Database backup service. Two modes:
/// 1. Safety backups: always-on, stored in data/backups/, no config needed, keep last 3.
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

        // Safety backups go in data/backups/ next to the DB
        var dataDir = Path.GetDirectoryName(dbPath)!;
        var backupDir = Path.Combine(dataDir, "backups");

        var backupName = $"{SafetyPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db";
        var dest = Path.Combine(backupDir, backupName);

        try
        {
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
