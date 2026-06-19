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
///   <item><b>Runtime connections are ReadWrite, NOT ReadWriteCreate.</b> By the
///   time a write endpoint runs, the DB is guaranteed to exist (startup either
///   found it or created it once via <see cref="CreateFreshDatabaseIfMissing"/>).
///   Keeping the per-request open at ReadWrite means a mid-session path bug can
///   still never silently create a fresh empty DB — which would look like a wipe.</item>
///   <item><b>First-run creation is explicit, one-shot, and missing-only.</b> The
///   WinUI app used to own DB creation; it's gone, so the sidecar now creates the
///   schema EXACTLY ONCE at startup and ONLY when no DB exists at all
///   (see <see cref="CreateFreshDatabaseIfMissing"/>). It never opens an existing
///   DB with Create mode, so an existing DB is never recreated or overwritten.</item>
///   <item><b>WAL + busy_timeout</b> mirror the WinUI app's connection discipline:
///   writes go to the -wal file, busy_timeout backs off on contention instead of
///   throwing.</item>
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
    /// First-run DB creation. Now that WinUI (the former schema owner) is gone, the
    /// sidecar must create the database on a genuinely fresh install — otherwise
    /// every write throws SQLITE_CANTOPEN and the whole app is dead.
    ///
    /// <para>SAFETY: this creates a file ONLY when BOTH the canonical
    /// (<c>revu.db</c>) and the legacy (<c>lol_review.db</c>) databases are absent —
    /// i.e. a true fresh install. If either exists, this is a no-op and returns
    /// false, so an existing DB is NEVER opened with Create mode, recreated, or
    /// touched. It materializes an EMPTY file (the caller then applies the additive
    /// schema, which is CREATE-IF-NOT-EXISTS only). Returns true iff it created the
    /// file.</para>
    /// </summary>
    public bool CreateFreshDatabaseIfMissing()
    {
        // Guard: only act when NO database exists anywhere we'd read from. This is
        // the single most important invariant — never create over existing data.
        var canonical = AppDataPaths.DatabasePath;
        var legacy = Path.Combine(
            AppDataPaths.UserDataRoot, AppDataMigrator.LegacyDatabaseFileName);
        if (File.Exists(canonical) || File.Exists(legacy))
        {
            return false;
        }

        // Fresh install: the resolved write path is the canonical one (no legacy to
        // fall back to). Ensure the user-data directory exists, then open ONE
        // ReadWriteCreate connection to materialize an empty DB file. We deliberately
        // do not use this mode for the per-request CreateConnection so a later path
        // bug can never silently create a blank DB mid-session.
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

        var createString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        using (var conn = new SqliteConnection(createString))
        {
            conn.Open();
            // Adopt WAL up front so the created DB matches the read/write factories'
            // journal mode (they assume WAL). This is the only write here; the schema
            // itself is applied by DatabaseInitializer.ApplyAdditiveSchemaAsync.
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        _logger.LogWarning(
            "No database found; created a fresh empty DB at {Path} (first run).",
            DatabasePath);
        return true;
    }

    /// <summary>
    /// Resolve the canonical DB path (or legacy fallback). Unlike the read-only
    /// factory we do NOT invent a path when the file is missing — a missing DB is
    /// a hard error for runtime writes (we must never create a blank one mid-session;
    /// first-run creation goes through <see cref="CreateFreshDatabaseIfMissing"/>).
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
