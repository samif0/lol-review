#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>
/// SAFETY-CRITICAL read-only SQLite connection factory for the sidecar.
///
/// <para>
/// Mirrors <see cref="SqliteConnectionFactory"/>'s database-path resolution
/// (<c>%LOCALAPPDATA%\LoLReviewData\revu.db</c>, with the legacy-filename
/// safety-net fallback) BUT opens every connection with
/// <see cref="SqliteOpenMode.ReadOnly"/>.
/// </para>
///
/// <para>
/// This is non-negotiable for the Tauri migration phase: the user has had
/// multiple data-loss incidents, so the sidecar must be physically incapable
/// of writing, creating, or migrating the database. ReadOnly mode means any
/// accidental INSERT/UPDATE/DELETE/CREATE issued against one of these
/// connections fails at the SQLite layer rather than corrupting user data.
/// We never call <c>Directory.CreateDirectory</c> and never run a migration —
/// the file is treated as a strictly external, owner-managed artifact.
/// </para>
///
/// <para>
/// WAL + shared cache parity: the WAL journal mode is persisted at the DB file
/// level by the WinUI app's DatabaseInitializer, so a read-only reader sees it
/// automatically. We request <see cref="SqliteCacheMode.Shared"/> to match the
/// app's connections, and set a busy_timeout so a reader briefly contending
/// with the app's writer backs off instead of throwing SQLITE_BUSY.
/// </para>
/// </summary>
public sealed class ReadOnlySqliteConnectionFactory : IDbConnectionFactory
{
    private readonly ILogger<ReadOnlySqliteConnectionFactory> _logger;

    /// <inheritdoc />
    public string DatabasePath { get; }

    /// <param name="logger">Logger instance.</param>
    /// <param name="dbPath">
    /// Optional explicit database path override (tests). When <c>null</c>,
    /// resolves the same default as the WinUI app's writable factory.
    /// </param>
    public ReadOnlySqliteConnectionFactory(
        ILogger<ReadOnlySqliteConnectionFactory> logger,
        string? dbPath = null)
    {
        _logger = logger;
        DatabasePath = dbPath ?? ResolveDefaultDatabasePath(logger);

        // NOTE: deliberately NO Directory.CreateDirectory here. A read-only
        // sidecar must never create the data tree; if the DB is missing the
        // app simply hasn't been run yet, which the /api/health probe reports
        // as "degraded".
        _logger.LogInformation("Read-only SQLite database path: {DatabasePath}", DatabasePath);
    }

    /// <summary>
    /// Mirror of <c>SqliteConnectionFactory.ResolveDefaultDatabasePath</c>:
    /// prefer the canonical <c>revu.db</c>, fall back to the legacy filename
    /// only if the preferred file does not exist but the legacy one does.
    /// </summary>
    private static string ResolveDefaultDatabasePath(ILogger logger)
    {
        var preferred = AppDataPaths.DatabasePath;
        if (File.Exists(preferred))
        {
            return preferred;
        }

        var legacyPath = Path.Combine(
            AppDataPaths.UserDataRoot,
            AppDataMigrator.LegacyDatabaseFileName);
        if (File.Exists(legacyPath))
        {
            logger.LogWarning(
                "Preferred DB {Preferred} missing; falling back to legacy {Legacy}",
                preferred, legacyPath);
            return legacyPath;
        }

        // Neither exists yet — return the preferred path so error messages point
        // at the canonical location. Opening will fail (SQLITE_CANTOPEN) under
        // ReadOnly mode, which is the correct, non-destructive behavior.
        return preferred;
    }

    /// <inheritdoc />
    public SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            // SAFETY-CRITICAL: ReadOnly. Never ReadWrite, never ReadWriteCreate.
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // busy_timeout is a per-connection setting (not stored in the file), so
        // it must be set on every new connection. This is a PRAGMA read/assign
        // that is permitted on read-only connections.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }

        return connection;
    }
}
