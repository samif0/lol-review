#nullable enable

using Revu.Core.Data;
using Revu.Core.Models;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Services;

/// <summary>
/// Database backup service. Two modes:
/// 1. Safety backups: always-on, stored next to the live DB in the user data root,
///    no config needed, tiered retention (the last 3, the newest per day for 7 days,
///    the newest per week for 4 weeks). Game-delete and objective-delete backups each
///    have their own budget.
/// 2. User backups: when the Settings toggle is on, stored in the chosen local folder
///    (or beside the safety backups when none is chosen), at most one a day, same
///    tiered retention with a longer recent tier.
/// Every backup is an SQLite online backup (consistent while other connections write),
/// passes PRAGMA quick_check, and is written to a temp file before it takes its name.
/// NEVER overwrites, deletes, or modifies the live database file (restore swaps it only
/// after the replacement is validated; see BackupService.Restore.cs).
/// </summary>
public sealed partial class BackupService : IBackupService
{
    private const string SafetyPrefix = "safety_backup_";
    private const string UserPrefix = "lol_review_backup_";

    /// <summary>
    /// Backups taken before a game is deleted. A burst of deletes used to rotate the
    /// shared 3-slot safety pool, evicting the last good snapshot; they now keep their
    /// own budget and never touch the safety tiers.
    /// </summary>
    private const string DeletePrefix = "delete_backup_";
    private const int MaxDeleteBackups = 5;

    /// <summary>
    /// Backups taken before a learning objective is deleted. A small pool of their own:
    /// objective deletes are rare and each snapshot is the only way back to that
    /// objective's prompts and written answers, so a burst of game deletes or routine
    /// session backups never evicts one, and one never evicts theirs.
    /// </summary>
    private const string ObjectiveDeletePrefix = "delete_objective_backup_";
    private const int MaxObjectiveDeleteBackups = 3;

    private static readonly BackupRetention SafetyRetention = new(KeepRecent: 3, KeepDaily: 7, KeepWeekly: 4);
    private static readonly BackupRetention UserRetention = new(KeepRecent: 5, KeepDaily: 7, KeepWeekly: 4);

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

        // Callers name their reason; GameRepository passes "delete-game-<id>" and the
        // objective delete "delete-objective-<id>".
        var isDelete = (reason ?? "").StartsWith("delete-", StringComparison.OrdinalIgnoreCase);
        var isObjectiveDelete = (reason ?? "").StartsWith("delete-objective-", StringComparison.OrdinalIgnoreCase);
        var prefix = isObjectiveDelete ? ObjectiveDeletePrefix : isDelete ? DeletePrefix : SafetyPrefix;
        var dest = Path.Combine(backupDir, $"{prefix}{DateTime.Now:yyyyMMdd_HHmmss}.db");
        if (File.Exists(dest))
        {
            _logger.LogDebug("Safety backup already exists for this second, skipping");
            return;
        }

        try
        {
            Directory.CreateDirectory(backupDir);
            // A game or objective delete goes ahead either way, so its snapshot is kept
            // even when quick_check finds a problem; it has its own budget and never
            // evicts a good safety backup.
            await Task.Run(() => CreateVerifiedCopy(dbPath, dest, requireIntact: !isDelete)).ConfigureAwait(false);
            _logger.LogInformation(
                "Safety backup created: {Dest} (reason: {Reason}, size: {Size} bytes)",
                dest, reason, new FileInfo(dest).Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Safety backup failed (reason: {Reason})", reason);
            return;
        }

        if (isObjectiveDelete)
            await PruneNewestAsync(backupDir, ObjectiveDeletePrefix, MaxObjectiveDeleteBackups).ConfigureAwait(false);
        else if (isDelete)
            await PruneNewestAsync(backupDir, DeletePrefix, MaxDeleteBackups).ConfigureAwait(false);
        else
            await PruneTieredAsync(backupDir, SafetyPrefix, SafetyRetention).ConfigureAwait(false);

        // Also prune coach-migration leftovers. They land in the same folder
        // but predate the safety pruner's prefix filter, so without this call
        // they just pile up over time.
        await PruneNewestAsync(backupDir, CoachMigrationPrefix, MaxCoachMigrationBackups)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RunBackupAsync()
    {
        // The sidecar runs this once per launch, which is also when the temp files of a
        // backup or restore cut short by a crash or a forced quit are cleared away.
        var dbPath = _connectionFactory.DatabasePath;
        await Task.Run(() => SweepLeftoverTempFiles(dbPath)).ConfigureAwait(false);

        AppConfig cfg;
        try
        {
            cfg = await _config.LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "User database backup skipped: settings unavailable");
            return;
        }

        // The chosen folder too, whether or not automatic backups are on now. The same
        // local-only rule as the backup applies before the folder is touched at all.
        if (LocalPathPolicy.TryGetLocalFullPath(cfg.BackupFolder, out var chosenFolder))
            await Task.Run(() => SweepLeftoverTempFiles(chosenFolder, CopyTempName.IsMatch)).ConfigureAwait(false);

        if (!cfg.BackupEnabled)
        {
            _logger.LogDebug("User database backup is disabled");
            return;
        }

        if (!TryResolveUserBackupFolder(cfg.BackupFolder, dbPath, out var folder)) return;

        // At most one routine backup a day. The newest file's timestamp comes from its
        // name (yyyyMMdd_HHmmss) rather than the OS mtime, which copies and sync tools
        // can change.
        try
        {
            var newest = new DirectoryInfo(folder)
                .EnumerateFiles($"{UserPrefix}*.db")
                .Select(f => TryParseBackupTimestamp(f.Name) ?? f.LastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
            if (newest.Date == DateTime.Now.Date)
            {
                _logger.LogDebug("Skipping routine backup: one was already made today ({Newest:g})", newest);
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Backup folder {Folder} is not readable; skipping the routine backup", folder);
            return;
        }

        if (!File.Exists(dbPath))
        {
            _logger.LogWarning("Database file not found for backup");
            return;
        }

        var dest = Path.Combine(folder, $"{UserPrefix}{DateTime.Now:yyyyMMdd_HHmmss}.db");
        try
        {
            await Task.Run(() => CreateVerifiedCopy(dbPath, dest, requireIntact: true)).ConfigureAwait(false);
            _logger.LogInformation("Database backed up to {Dest}", dest);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Database backup failed");
            return;
        }

        await PruneTieredAsync(folder, UserPrefix, UserRetention).ConfigureAwait(false);
    }

    /// <summary>
    /// Where the automatic backup goes. A blank setting means Revu's own backups folder
    /// beside the database (the "Default backup location" in Settings), where these
    /// backups also show up in the restore list. A chosen folder is used only when it is
    /// local and exists. The raw setting is checked with LocalPathPolicy BEFORE any file
    /// check, because even asking whether a <c>\\server\share</c> folder exists connects to
    /// that host, and the backup would copy the whole database there on every launch.
    /// </summary>
    private bool TryResolveUserBackupFolder(string? configured, string dbPath, out string folder)
    {
        var chosen = (configured ?? "").Trim();
        if (chosen.Length == 0)
        {
            folder = Path.Combine(Path.GetDirectoryName(dbPath)!, "backups");
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Automatic backup skipped: the default backup folder {Folder} could not be created", folder);
                return false;
            }
            _logger.LogDebug("No backup folder chosen; using the default {Folder}", folder);
            return true;
        }
        if (!LocalPathPolicy.TryGetLocalFullPath(chosen, out folder))
        {
            _logger.LogWarning("Automatic backup skipped: the backup folder is not a folder on this PC ({Folder})", chosen);
            return false;
        }
        if (!Directory.Exists(folder))
        {
            _logger.LogWarning("Automatic backup skipped: the backup folder {Folder} does not exist", folder);
            return false;
        }
        return true;
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

    /// <summary>
    /// Returns a fresh path under <paramref name="backupDir"/> using the
    /// timestamp-suffixed naming scheme. If the base name already exists
    /// (e.g. user pressed reset twice in the same second), appends "_2",
    /// "_3", … so a new backup never collides with an existing one.
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
        return basePath; // fall back; the final move will surface the collision error.
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
                    : fileName.StartsWith(DeletePrefix, StringComparison.OrdinalIgnoreCase)
                        ? "Before a game was deleted"
                        : fileName.StartsWith(ObjectiveDeletePrefix, StringComparison.OrdinalIgnoreCase)
                            ? "Before a learning objective was deleted"
                            : fileName.StartsWith(UserPrefix, StringComparison.OrdinalIgnoreCase)
                                ? "User backup"
                                : fileName.StartsWith(CoachMigrationPrefix, StringComparison.OrdinalIgnoreCase)
                                    ? "Coach migration"
                                    : "Backup";
        var mb = sizeBytes / (1024.0 * 1024.0);
        return $"{kind} · {timestamp:MMM d yyyy, h:mm tt} · {mb:F1} MB";
    }
}
