#nullable enable

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Revu.Core.Data;

namespace Revu.Sidecar;

/// <summary>
/// Write-capable SQLite connection factory for the sidecar's WRITE endpoints only.
///
/// <para>
/// SAFETY POSTURE (Tauri migration, single-user, irreplaceable DB):
/// <list type="bullet">
///   <item><b>Runtime connections are ReadWrite, NOT ReadWriteCreate.</b> A
///   mid-session path bug can never silently create a fresh empty DB — which
///   would look like a wipe.</item>
///   <item><b>Creation is missing-only, STAGED, and one-shot per data folder.</b>
///   <see cref="CreateFreshDatabaseIfMissing"/> runs only when NO database (or
///   leftover WAL journal) exists, builds the file + full additive schema at a
///   staging path, smoke-checks it, and atomically renames it into place — no
///   observer can ever see a schemaless half-created database, and a failed
///   schema pass leaves nothing behind at the real path.</item>
///   <item><b>Never resurrect (P-041).</b> A process-wide latch records every
///   database file observed by ANY factory instance (the startup migration
///   block's throwaway instance included). Recovery refuses whenever a database
///   for this data folder — canonical OR legacy filename — has ever been seen,
///   or when orphan <c>-wal</c>/<c>-shm</c> journals prove one existed before
///   this process. A database that vanishes therefore always fails loudly with
///   <see cref="DatabaseUnavailableException"/> (actionable reason from
///   <see cref="SqliteOpenHealth.Describe"/>); it is never silently replaced by
///   a blank one, and a stale legacy snapshot is never silently substituted for
///   a canonical DB this process has already used.</item>
///   <item><b>Open failures self-heal what is safe.</b> Every open runs
///   <see cref="SqliteOpenHealth.TryHeal"/> (recreate missing data folder, clear
///   read-only attributes on db/-wal/-shm) and drops pooled physical handles
///   after a heal or a failure — SQLite silently downgrades a write open of a
///   protected file to read-only, and the pool must not keep serving that dead
///   handle after the file is repaired.</item>
///   <item><b>WAL + busy_timeout</b> mirror the WinUI app's connection discipline.</item>
/// </list>
/// </para>
///
/// <para>
/// Registered only in the WRITE graph (<see cref="WriteServices"/>); read
/// endpoints keep using the ReadOnly factory, so a read path can never write.
/// </para>
/// </summary>
public sealed class WriteSqliteConnectionFactory : IDbConnectionFactory
{
    /// <summary>
    /// Process-wide "a database file has been observed at this full path" latch.
    /// Static ON PURPOSE: the startup migration block uses its own throwaway
    /// factory instance, while the DI write graph constructs another one lazily
    /// (possibly hours later, at the first write). Anchoring the never-resurrect
    /// guard to a per-instance snapshot would let a DB that vanished between
    /// startup and the first write be silently replaced by a blank one. Keyed by
    /// full path so tests (unique temp folders) never interfere with each other;
    /// the recovery gate checks BOTH sibling filenames for the data folder, so a
    /// canonical/legacy resolution shift across the vanish can't dodge it.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> s_dbObserved =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<WriteSqliteConnectionFactory> _logger;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly bool _explicitPath;
    private readonly object _recoveryLock = new();
    private bool _markedObserved;

    /// <inheritdoc />
    public string DatabasePath { get; }

    public WriteSqliteConnectionFactory(
        ILogger<WriteSqliteConnectionFactory> logger,
        ILoggerFactory? loggerFactory = null,
        string? dbPath = null)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _explicitPath = dbPath is not null;
        DatabasePath = dbPath ?? ResolveExistingDatabasePath(logger);
        if (File.Exists(DatabasePath))
        {
            MarkDbObserved();
        }
        _logger.LogInformation("Write-capable SQLite database path: {DatabasePath}", DatabasePath);
    }

    private void MarkDbObserved()
    {
        if (_markedObserved)
        {
            return;
        }

        s_dbObserved.TryAdd(Path.GetFullPath(DatabasePath), 0);
        _markedObserved = true;
    }

    /// <summary>
    /// Has ANY database for this data folder — this factory's file, the canonical
    /// <c>revu.db</c>, or the legacy <c>lol_review.db</c> — ever been observed in
    /// this process? Sibling-aware because path RESOLUTION can shift between
    /// factory instances across the very vanish event the latch defends against
    /// (a legacy-resolved DB disappears → the next instance resolves canonical).
    /// </summary>
    private bool AnySiblingDbObserved()
    {
        var fullPath = Path.GetFullPath(DatabasePath);
        var directory = Path.GetDirectoryName(fullPath) ?? "";
        if (s_dbObserved.ContainsKey(fullPath)) return true;
        return s_dbObserved.ContainsKey(Path.Combine(directory, AppDataMigrator.NewDatabaseFileName))
            || s_dbObserved.ContainsKey(Path.Combine(directory, AppDataMigrator.LegacyDatabaseFileName));
    }

    /// <summary>
    /// First-run DB creation. Now that WinUI (the former schema owner) is gone, the
    /// sidecar must create the database on a genuinely fresh install — otherwise
    /// every write throws SQLITE_CANTOPEN and the whole app is dead.
    ///
    /// <para>SAFETY: creates ONLY when no canonical, no legacy, and no orphan WAL
    /// journal exists (an orphan <c>revu.db-wal</c> proves a database existed —
    /// creating next to it would let SQLite replay FOREIGN wal frames over the
    /// fresh file). Creation is STAGED: file + full additive schema are built at
    /// <c>revu.db.recovering-&lt;guid&gt;</c>, smoke-checked, then atomically
    /// renamed into place, so a failure leaves nothing at the real path and no
    /// concurrent open can see a schemaless database. Returns true iff the DB was
    /// created. Never throws.</para>
    ///
    /// <para>With an explicit test path, the guards check THAT path's folder
    /// instead of the machine's real %LOCALAPPDATA% — so tests exercise creation
    /// without being short-circuited (or fooled) by the developer's live DB.</para>
    /// </summary>
    public bool CreateFreshDatabaseIfMissing()
    {
        try
        {
            SweepStaleStagingFiles();

            if (AnyDatabaseExists())
            {
                if (File.Exists(DatabasePath)) MarkDbObserved();
                return false;
            }

            if (File.Exists(DatabasePath + "-wal") || File.Exists(DatabasePath + "-shm"))
            {
                _logger.LogError(
                    "Refusing to create a fresh DB at {Path}: leftover -wal/-shm journals prove a database existed here.",
                    DatabasePath);
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);

            var staging = DatabasePath + $".recovering-{Guid.NewGuid():N}";
            try
            {
                MaterializeEmptyWalDatabase(staging);
                ApplyAdditiveSchema(staging);

                // Release every pooled handle on the staging file so its WAL is
                // checkpointed on close and the rename can't hit a live handle.
                SqliteConnection.ClearAllPools();
                if (File.Exists(staging + "-wal"))
                {
                    throw new IOException(
                        $"staging WAL {staging}-wal survived close; schema may not be checkpointed");
                }
                SmokeCheckStagedDatabase(staging);

                File.Move(staging, DatabasePath);
                // The read-only smoke check above may have recreated empty
                // staging -wal/-shm files (a WAL reader materializes them when
                // the directory is writable); the real WAL was already verified
                // checkpointed before the check, so these are frame-free litter.
                TryDeleteQuiet(staging + "-wal");
                TryDeleteQuiet(staging + "-shm");
                MarkDbObserved();
                _logger.LogWarning(
                    "No database found; created a fresh schema-complete DB at {Path} (first run).",
                    DatabasePath);
                return true;
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                TryDeleteQuiet(staging);
                TryDeleteQuiet(staging + "-wal");
                TryDeleteQuiet(staging + "-shm");
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fresh database creation failed for {Path}", DatabasePath);
            return false;
        }
    }

    /// <summary>
    /// Guard: is there ANY database file we must not shadow? Canonical + legacy
    /// filename, resolved against the real %LOCALAPPDATA% for the default path or
    /// against the explicit path's own directory for tests.
    /// </summary>
    private bool AnyDatabaseExists()
    {
        string canonical, legacy;
        if (_explicitPath)
        {
            canonical = DatabasePath;
            legacy = Path.Combine(
                Path.GetDirectoryName(DatabasePath) ?? "",
                AppDataMigrator.LegacyDatabaseFileName);
        }
        else
        {
            canonical = AppDataPaths.DatabasePath;
            legacy = Path.Combine(
                AppDataPaths.UserDataRoot, AppDataMigrator.LegacyDatabaseFileName);
        }

        return File.Exists(canonical) || File.Exists(legacy);
    }

    /// <summary>Create an empty SQLite DB in WAL mode at <paramref name="path"/>.</summary>
    private static void MaterializeEmptyWalDatabase(string path)
    {
        var createString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            // No pooling: the handle must be fully closed the moment we dispose,
            // so the staged file can be atomically renamed into place.
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(createString);
        conn.Open();
        // Adopt WAL up front so the created DB matches the read/write factories'
        // journal mode (they assume WAL).
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// The additive, CREATE-IF-NOT-EXISTS-only schema pass startup runs — applied
    /// against the STAGING file. Sync-over-async is safe: no SynchronizationContext
    /// in the sidecar host, and Microsoft.Data.Sqlite completes synchronously.
    /// </summary>
    private void ApplyAdditiveSchema(string stagingPath)
    {
        var stagingFactory = new SqliteConnectionFactory(
            _loggerFactory?.CreateLogger<SqliteConnectionFactory>()
                ?? (ILogger<SqliteConnectionFactory>)NullLogger<SqliteConnectionFactory>.Instance,
            stagingPath);
        var initLogger = _loggerFactory?.CreateLogger<DatabaseInitializer>()
            ?? (ILogger<DatabaseInitializer>)NullLogger<DatabaseInitializer>.Instance;
        new DatabaseInitializer(stagingFactory, initLogger)
            .ApplyAdditiveSchemaAsync().GetAwaiter().GetResult();
    }

    /// <summary>Read-only sanity check before promoting a staged DB: core schema present.</summary>
    private static void SmokeCheckStagedDatabase(string stagingPath)
    {
        var readString = new SqliteConnectionStringBuilder
        {
            DataSource = stagingPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        using var conn = new SqliteConnection(readString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='games'";
        if (cmd.ExecuteScalar() is not long count || count < 1)
        {
            throw new InvalidOperationException($"staged DB {stagingPath} is missing the core schema");
        }
    }

    /// <summary>Delete staging leftovers from recoveries a crash cut short.</summary>
    private void SweepStaleStagingFiles()
    {
        var directory = Path.GetDirectoryName(DatabasePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (var stale in Directory.GetFiles(
            directory, Path.GetFileName(DatabasePath) + ".recovering-*"))
        {
            TryDeleteQuiet(stale);
            _logger.LogWarning("Swept stale recovery staging file {Path}", stale);
        }
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

        // If the canonical DB was observed EARLIER THIS PROCESS and is now gone,
        // never silently retarget writes at the stale legacy snapshot (it is a
        // years-old pre-rename backup on migrated installs) — resolve canonical
        // so the open fails loudly with the missing-file diagnosis instead.
        if (s_dbObserved.ContainsKey(Path.GetFullPath(preferred)))
        {
            logger.LogError(
                "Canonical DB {Path} existed earlier this session but is now missing; refusing the legacy fallback.",
                preferred);
            return preferred;
        }

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
        // P-041 proactive self-heal: recreate a missing data folder and clear
        // read-only attributes (backup-restore / cloud-sync leftovers) BEFORE
        // opening, because WAL failures from those causes would otherwise surface
        // later, deep inside a repository query. Cheap (3 stats) and idempotent.
        // After a heal, drop pooled physical handles: SQLite silently downgrades
        // a ReadWrite open of a write-protected file to a read-only OS handle,
        // and the pool would keep serving that stale handle after the repair.
        if (SqliteOpenHealth.TryHeal(DatabasePath, _logger))
        {
            ClearOwnPool();
        }

        try
        {
            var connection = OpenCore();
            MarkDbObserved();
            return connection;
        }
        catch (SqliteException ex) when (SqliteOpenHealth.IsOpenOrWriteAccessFailure(ex))
        {
            if (TryRecoverNeverCreatedDatabase())
            {
                try
                {
                    var connection = OpenCore();
                    MarkDbObserved();
                    return connection;
                }
                catch (SqliteException retryEx) when (SqliteOpenHealth.IsOpenOrWriteAccessFailure(retryEx))
                {
                    throw Unavailable(retryEx);
                }
            }

            throw Unavailable(ex);
        }
    }

    private string BuildConnectionString() => new SqliteConnectionStringBuilder
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

    private SqliteConnection OpenCore()
    {
        var connection = new SqliteConnection(BuildConnectionString());
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

    /// <summary>Drop this factory's pooled physical connections (stale handles).</summary>
    private void ClearOwnPool()
    {
        try
        {
            using var probe = new SqliteConnection(BuildConnectionString());
            SqliteConnection.ClearPool(probe);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ClearPool failed for {Path} (continuing)", DatabasePath);
        }
    }

    /// <summary>
    /// Write-time retry of the one-shot first-run creation, for the fresh install
    /// whose STARTUP creation failed (antivirus holding the folder, transient disk
    /// error) and whose very first write would otherwise brick every write forever.
    /// All guards live in <see cref="CreateFreshDatabaseIfMissing"/> plus the
    /// sibling-aware never-resurrect latch here; see the class doc.
    /// </summary>
    private bool TryRecoverNeverCreatedDatabase()
    {
        lock (_recoveryLock)
        {
            if (File.Exists(DatabasePath))
            {
                // Another request just recovered it (or the file reappeared) —
                // the file demonstrably exists, so latch it and retry the open.
                MarkDbObserved();
                return true;
            }

            if (AnySiblingDbObserved())
            {
                return false; // a DB has existed this process → vanished → fail loudly.
            }

            if (!CreateFreshDatabaseIfMissing())
            {
                return false;
            }

            _logger.LogWarning(
                "Write-time recovery: database at {Path} had never been created " +
                "(startup first-run creation must have failed); created it fresh with schema.",
                DatabasePath);
            return true;
        }
    }

    private static void TryDeleteQuiet(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }

    private DatabaseUnavailableException Unavailable(SqliteException ex)
    {
        // Stale pooled handles must not outlive the failure: once the user fixes
        // the underlying cause (unlocks the file, restores permissions), the very
        // next attempt should open fresh instead of reusing a dead/downgraded
        // physical connection.
        ClearOwnPool();
        var reason = SqliteOpenHealth.Describe(DatabasePath);
        _logger.LogError(ex, "Write connection unavailable for {Path}: {Reason}", DatabasePath, reason);
        return new DatabaseUnavailableException(DatabasePath, reason, ex);
    }
}
