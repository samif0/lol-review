#nullable enable

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>
/// Write-capable SQLite connection factory for the sidecar's WRITE endpoints only.
///
/// <para>
/// SAFETY POSTURE (Tauri migration, single-user, irreplaceable DB):
/// <list type="bullet">
///   <item><b>ReadWrite, NOT ReadWriteCreate.</b> The database must already exist
///   (the still-installed WinUI app created + migrated it). ReadWrite opens an
///   existing file and FAILS (SQLITE_CANTOPEN) if it's missing, rather than
///   silently creating a fresh empty DB — which would look like a wipe.</item>
///   <item><b>No directory creation, no migration.</b> This factory never calls
///   Directory.CreateDirectory and the sidecar never runs DatabaseInitializer.
///   Schema ownership stays with the WinUI app. This removes the single scariest
///   data-loss vector (a botched migration from a second writer).</item>
///   <item><b>WAL + shared cache + busy_timeout</b> mirror the WinUI app's
///   connection discipline so the two processes coexist safely: writes go to the
///   -wal file, busy_timeout backs off on contention instead of throwing.</item>
/// </list>
/// </para>
///
/// <para>
/// This factory is registered as a KEYED/named dependency used only by the write
/// repositories the write endpoints resolve — the read endpoints keep using the
/// ReadOnly factory, so a read path can never accidentally write.
/// </para>
/// </summary>
public sealed class WriteSqliteConnectionFactory : IDbConnectionFactory
{
    private readonly ILogger<WriteSqliteConnectionFactory> _logger;

    /// <inheritdoc />
    public string DatabasePath { get; }

    public WriteSqliteConnectionFactory(
        ILogger<WriteSqliteConnectionFactory> logger,
        string? dbPath = null)
    {
        _logger = logger;
        DatabasePath = dbPath ?? ResolveExistingDatabasePath(logger);
        _logger.LogInformation("Write-capable SQLite database path: {DatabasePath}", DatabasePath);
    }

    /// <summary>
    /// Resolve the canonical DB path (or legacy fallback). Unlike the read-only
    /// factory we do NOT invent a path when the file is missing — a missing DB is
    /// a hard error for writes (we must never create a blank one).
    /// </summary>
    private static string ResolveExistingDatabasePath(ILogger logger)
    {
        var preferred = AppDataPaths.DatabasePath;
        if (File.Exists(preferred)) return preferred;

        var legacyPath = Path.Combine(
            AppDataPaths.UserDataRoot,
            AppDataMigrator.LegacyDatabaseFileName);
        if (File.Exists(legacyPath))
        {
            logger.LogWarning(
                "Preferred DB {Preferred} missing; using legacy {Legacy} for writes",
                preferred, legacyPath);
            return legacyPath;
        }

        // Return the canonical path so the SQLITE_CANTOPEN error points at the
        // right place. Opening ReadWrite against a missing file throws — which is
        // the correct, non-destructive behavior (no blank DB gets created).
        return preferred;
    }

    /// <inheritdoc />
    public SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            // ReadWrite (existing file) — deliberately NOT ReadWriteCreate.
            Mode = SqliteOpenMode.ReadWrite,
            // PRIVATE cache (not Shared): a writer joining the read-only factory's
            // SHARED cache inherits its read-only restriction ("attempt to write a
            // readonly database"). WAL handles cross-connection visibility without
            // shared cache, so a private-cache writer is both correct and safe.
            Cache = SqliteCacheMode.Private,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        // Per-connection busy_timeout — matches the WinUI app so concurrent
        // writers back off rather than throwing SQLITE_BUSY.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA busy_timeout=5000;";
            cmd.ExecuteNonQuery();
        }

        return connection;
    }
}
