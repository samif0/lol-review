#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Data;

/// <summary>
/// Pre-flight integrity check for the database.
/// Runs before DatabaseInitializer to detect and prevent data loss scenarios.
/// Logs DB path, file size, and game count on every startup.
/// If the DB exists but has 0 games and a backup with >0 games exists, throws to prevent silent data loss.
/// </summary>
public sealed class DatabaseIntegrityChecker
{
    private readonly IDbConnectionFactory _connectionFactory;
    private readonly LegacyDatabaseMigrationService _legacyMigration;
    private readonly ILogger<DatabaseIntegrityChecker> _logger;

    public DatabaseIntegrityChecker(
        IDbConnectionFactory connectionFactory,
        LegacyDatabaseMigrationService legacyMigration,
        ILogger<DatabaseIntegrityChecker> logger)
    {
        _connectionFactory = connectionFactory;
        _legacyMigration = legacyMigration;
        _logger = logger;
    }

    /// <summary>
    /// Run all integrity checks. Call this before DatabaseInitializer.InitializeAsync().
    /// </summary>
    public void RunPreFlightChecks()
    {
        var dbPath = _connectionFactory.DatabasePath;

        _logger.LogInformation("=== DATABASE INTEGRITY CHECK ===");
        _logger.LogInformation("DB path: {Path}", dbPath);

        if (!File.Exists(dbPath))
        {
            _logger.LogInformation("DB does not exist yet (first run). Will be created by DatabaseInitializer.");
            return;
        }

        var fileInfo = new FileInfo(dbPath);
        _logger.LogInformation("DB file size: {Size} bytes ({SizeKb} KB)", fileInfo.Length, fileInfo.Length / 1024);

        long gameCount = CountGamesInFile(dbPath);
        _logger.LogInformation("DB game count: {Count}", gameCount);

        // Check for the dangerous scenario: DB exists with 0 games but backups
        // or an un-migrated legacy database still have data.
        if (gameCount == 0 && fileInfo.Length > 0)
        {
            var backupWithData = FindBackupWithGames(dbPath);
            if (backupWithData is not null)
            {
                _logger.LogCritical(
                    "DATA LOSS DETECTED: Database at {Path} has 0 games but backup {Backup} has games. " +
                    "The database may have been wiped. Refusing to proceed.",
                    dbPath, backupWithData);
                throw new InvalidOperationException(
                    $"Database integrity check failed: DB at {dbPath} has 0 games " +
                    $"but backup at {backupWithData} contains data. " +
                    "This likely indicates data loss. Please restore your database from the backup manually, " +
                    "or delete the empty database to start fresh.");
            }
        }

        _logger.LogInformation("=== INTEGRITY CHECK PASSED ({Count} games) ===", gameCount);
    }

    /// <summary>Count games in a database file without going through the connection factory.</summary>
    private long CountGamesInFile(string dbFilePath)
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = dbFilePath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            // Check if the games table exists first
            using var tableCheck = connection.CreateCommand();
            tableCheck.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='games'";
            if (tableCheck.ExecuteScalar() is null)
                return -1; // Table doesn't exist (brand new DB or schema not yet applied)

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM games";
            var result = cmd.ExecuteScalar();
            return result is long l ? l : 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count games in {Path}", dbFilePath);
            return -1;
        }
    }

    /// <summary>
    /// Look for backup files that contain games. Checks safety backups in data/backups/
    /// and the old-path DB at %LOCALAPPDATA%\LoLReview\lol_review.db.
    /// Returns the path of the first backup with games, or null.
    /// </summary>
    private string? FindBackupWithGames(string dbPath)
    {
        var dataDir = Path.GetDirectoryName(dbPath)!;

        // Check safety backups
        var backupDir = Path.Combine(dataDir, "backups");
        if (Directory.Exists(backupDir))
        {
            foreach (var backup in Directory.EnumerateFiles(backupDir, "*.db")
                         .OrderByDescending(f => File.GetLastWriteTimeUtc(f)))
            {
                if (CountGamesInFile(backup) > 0)
                    return backup;
            }
        }

        // Check old-path DB
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var oldPathDb = Path.Combine(localAppData, "LoLReview", "lol_review.db");
        if (File.Exists(oldPathDb) && !string.Equals(Path.GetFullPath(oldPathDb), Path.GetFullPath(dbPath), StringComparison.OrdinalIgnoreCase))
        {
            if (CountGamesInFile(oldPathDb) > 0)
                return oldPathDb;
        }

        var legacyWithMoreGames = _legacyMigration.FindLegacyDatabaseWithMoreGames(0);
        if (legacyWithMoreGames is not null)
        {
            return legacyWithMoreGames;
        }

        return null;
    }
}
