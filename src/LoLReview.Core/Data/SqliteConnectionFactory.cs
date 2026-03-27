#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LoLReview.Core.Data;

/// <summary>
/// Creates SQLite connections configured for WAL mode with shared cache.
/// Default database location: %LOCALAPPDATA%\LoLReview\lol_review.db
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public string DatabasePath { get; }

    /// <param name="logger">Logger instance.</param>
    /// <param name="dbPath">
    /// Optional override for the database file path.
    /// When <c>null</c>, defaults to <c>%LOCALAPPDATA%\LoLReview\lol_review.db</c>.
    /// </param>
    public SqliteConnectionFactory(ILogger<SqliteConnectionFactory> logger, string? dbPath = null)
    {
        _logger = logger;
        DatabasePath = dbPath ?? GetDefaultDatabasePath();

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("SQLite database path: {DatabasePath}", DatabasePath);
    }

    /// <inheritdoc />
    public SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Enable WAL mode for better concurrent read performance
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        // Set busy timeout to 5 seconds to avoid immediate SQLITE_BUSY errors
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }

        return connection;
    }

    /// <summary>
    /// Returns the default database path: %LOCALAPPDATA%\LoLReview\data\lol_review.db
    /// The "data" subdirectory is used to keep user data separate from the Velopack
    /// install directory (current/, packages/) so installs/updates never wipe the DB.
    /// On first run, migrates the DB from the old location if it exists.
    /// </summary>
    private static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "Local");
        }

        var newPath = Path.Combine(localAppData, "LoLReview", "data", "lol_review.db");
        var oldPath = Path.Combine(localAppData, "LoLReview", "lol_review.db");

        // Migrate from old location if new doesn't exist but old does
        if (!File.Exists(newPath) && File.Exists(oldPath))
        {
            var dataDir = Path.GetDirectoryName(newPath)!;
            Directory.CreateDirectory(dataDir);
            File.Copy(oldPath, newPath);
            // Also copy WAL/SHM if present
            foreach (var ext in new[] { "-wal", "-shm" })
            {
                var src = oldPath + ext;
                if (File.Exists(src))
                    File.Copy(src, newPath + ext);
            }
        }

        return newPath;
    }
}
