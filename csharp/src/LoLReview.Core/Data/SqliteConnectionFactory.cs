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
    /// Returns the default database path: %LOCALAPPDATA%\LoLReview\lol_review.db
    /// </summary>
    private static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(localAppData))
        {
            // Fallback for non-Windows or unusual environments
            localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData", "Local");
        }

        return Path.Combine(localAppData, "LoLReview", "lol_review.db");
    }
}
