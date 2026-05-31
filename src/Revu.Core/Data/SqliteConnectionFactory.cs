#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Revu.Core.Data;

/// <summary>
/// Creates SQLite connections configured for WAL mode with shared cache.
/// Default database location: %LOCALAPPDATA%\LoLReviewData\revu.db
/// </summary>
public sealed class SqliteConnectionFactory : IDbConnectionFactory
{
    private readonly ILogger<SqliteConnectionFactory> _logger;

    public string DatabasePath { get; }

    /// <param name="logger">Logger instance.</param>
    /// <param name="dbPath">
    /// Optional override for the database file path.
    /// When <c>null</c>, defaults to <c>%LOCALAPPDATA%\LoLReviewData\revu.db</c>
    /// with a safety-net fallback to the legacy <c>lol_review.db</c> filename
    /// when the new file does not exist but the legacy one does — this handles
    /// any case where <see cref="AppDataMigrator"/> did not run or failed.
    /// </param>
    public SqliteConnectionFactory(ILogger<SqliteConnectionFactory> logger, string? dbPath = null)
    {
        _logger = logger;
        DatabasePath = dbPath ?? ResolveDefaultDatabasePath(logger);

        // Ensure the directory exists
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _logger.LogInformation("SQLite database path: {DatabasePath}", DatabasePath);
    }

    private static string ResolveDefaultDatabasePath(ILogger logger)
    {
        var preferred = AppDataPaths.DatabasePath;
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var legacyPath = Path.Combine(AppDataPaths.UserDataRoot, AppDataMigrator.LegacyDatabaseFileName);
        if (File.Exists(legacyPath))
        {
            logger.LogWarning(
                "Preferred DB {Preferred} missing; falling back to legacy {Legacy}",
                preferred, legacyPath);
            return legacyPath;
        }

        return preferred;
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

        // WAL mode is persistent at the DB file level and is set once during
        // DatabaseInitializer.InitializeAsync. Repeating PRAGMA journal_mode=WAL
        // on every connection open is redundant after the first launch.
        // B4: removed per-open WAL pragma; see DatabaseInitializer.InitializeAsync.

        // busy_timeout is a per-connection setting (not stored in the file),
        // so it must be set on every new connection.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }

        return connection;
    }

}
