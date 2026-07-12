using Revu.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Revu.Sidecar.Tests;

/// <summary>
/// P-041: WriteSqliteConnectionFactory self-heal + recovery contracts. The field
/// failure was "Scan failed: SQLite Error 14: 'unable to open database file'" on
/// every scan — these tests pin each leg of the fix:
/// <list type="bullet">
///   <item>a fresh install whose startup DB creation failed recovers at write
///   time (creates + applies schema) instead of staying bricked forever;</item>
///   <item>read-only attributes (backup-restore / sync-tool leftovers) on the DB
///   or its -wal/-shm sidecars are cleared before opening;</item>
///   <item>a database that HAS existed is NEVER silently replaced by a blank one
///   — it fails loudly with an actionable DatabaseUnavailableException;</item>
///   <item>a legacy lol_review.db next door blocks creation (not a fresh install).</item>
/// </list>
/// </summary>
public sealed class WriteFactoryRecoveryTests : IDisposable
{
    private readonly string _root;

    public WriteFactoryRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Revu.Sidecar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private string NewDbPath() => Path.Combine(_root, Guid.NewGuid().ToString("N"), "revu.db");

    private static WriteSqliteConnectionFactory NewFactory(string dbPath) =>
        new(NullLogger<WriteSqliteConnectionFactory>.Instance, NullLoggerFactory.Instance, dbPath);

    private static void ExecuteNonQuery(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    /// <summary>Create a real, schema-complete DB at the path (mirrors a healthy install).</summary>
    private static void CreateHealthyDatabase(string dbPath)
    {
        var factory = NewFactory(dbPath);
        // First connection triggers never-created recovery: file + WAL + schema.
        using (var conn = factory.CreateConnection())
        {
            Assert.True(File.Exists(dbPath));
        }
        SqliteConnection.ClearAllPools();
    }

    // ── Fresh-install recovery (the bricked-user fix) ────────────────────────

    [Fact]
    public void FreshInstall_NeverCreatedDb_IsRecoveredAtWriteTime_WithFullSchema()
    {
        var dbPath = NewDbPath();
        var factory = NewFactory(dbPath);

        using var conn = factory.CreateConnection();

        Assert.True(File.Exists(dbPath));
        // The additive schema ran, not just an empty file: core tables exist.
        var tables = ScalarLong(conn,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('games','vod_files','objectives')");
        Assert.Equal(3, tables);
        // And the connection is genuinely writable (header write, no FK deps).
        ExecuteNonQuery(conn, "PRAGMA user_version = 7;");
        Assert.Equal(7, ScalarLong(conn, "PRAGMA user_version;"));
    }

    [Fact]
    public void FreshInstall_RecoveredDb_UsesWalJournalMode()
    {
        var dbPath = NewDbPath();
        var factory = NewFactory(dbPath);

        using var conn = factory.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", (string?)cmd.ExecuteScalar());
    }

    [Fact]
    public void FreshInstall_MissingParentDirectory_IsCreatedByRecovery()
    {
        // Parent dir never created — TryHeal + creation must build the tree.
        var dbPath = Path.Combine(_root, "deep", "nested", "tree", "revu.db");
        var factory = NewFactory(dbPath);

        using var conn = factory.CreateConnection();

        Assert.True(File.Exists(dbPath));
    }

    [Fact]
    public void FreshInstall_ConcurrentFirstWrites_RecoverExactlyOnce()
    {
        var dbPath = NewDbPath();
        var factory = NewFactory(dbPath);

        Parallel.For(0, 6, _ =>
        {
            using var conn = factory.CreateConnection();
            ExecuteNonQuery(conn, "SELECT COUNT(*) FROM games");
        });

        Assert.True(File.Exists(dbPath));
        using var check = factory.CreateConnection();
        Assert.Equal(1, ScalarLong(check,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='games'"));
    }

    // ── Never-resurrect: an existing DB must not be replaced by a blank one ──

    [Fact]
    public void RecoveryIsOneShot_ASecondVanish_FailsLoudly_InsteadOfCreatingAnotherBlankDb()
    {
        // Adversarial-review CRITICAL: the exact P-041 field environment (AV that
        // broke startup creation) can strike AGAIN after write-time recovery
        // created the DB and the user accumulated a session of data. The SAME
        // factory must then refuse to create a second blank DB — that would
        // silently discard the session.
        var dbPath = NewDbPath();
        var factory = NewFactory(dbPath);

        using (var conn = factory.CreateConnection()) // first write → recovery creates
        {
            ExecuteNonQuery(conn, "PRAGMA user_version = 21;");
        }
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath); // the AV/sync tool quarantines it again
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }

        var ex = Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());

        Assert.Contains("missing", ex.Reason);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void DbObservedByAnEarlierFactoryInstance_BlocksRecoveryInALaterOne()
    {
        // Adversarial-review MAJOR: the startup migration block uses a THROWAWAY
        // factory; the DI write factory is constructed lazily at the first write,
        // possibly hours later. If the DB vanishes in between, the late factory's
        // ctor never saw it — the process-wide latch (marked by the startup
        // instance) must still block recovery so months of data don't get
        // shadowed by a blank DB.
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath); // an earlier instance observed + created it

        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }

        var lateFactory = NewFactory(dbPath); // ctor sees nothing at the path
        var ex = Assert.Throws<DatabaseUnavailableException>(() => lateFactory.CreateConnection());

        Assert.Contains("missing", ex.Reason);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void DbThatExistedAtResolve_ThenVanished_FailsLoudly_AndIsNotRecreated()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);

        var factory = NewFactory(dbPath); // resolves while the DB exists
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(dbPath + suffix)) File.Delete(dbPath + suffix);
        }

        var ex = Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());

        Assert.Contains("missing", ex.Reason);
        Assert.False(File.Exists(dbPath)); // no blank DB materialized over lost data
    }

    [Fact]
    public void LegacySiblingDb_BlocksFreshCreation_AndFailsWithDiagnosis()
    {
        var dbPath = NewDbPath();
        var dir = Path.GetDirectoryName(dbPath)!;
        Directory.CreateDirectory(dir);
        // A legacy-named DB exists → NOT a fresh install; creating revu.db could
        // shadow real data, so recovery must refuse.
        File.WriteAllBytes(Path.Combine(dir, AppDataMigrator.LegacyDatabaseFileName), [1]);

        var factory = NewFactory(dbPath);
        Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());

        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void LegacyPathObserved_BlocksCreationAtTheCanonicalSibling_AfterAVanish()
    {
        // Second-round adversarial CRITICAL: path RESOLUTION can shift across the
        // vanish. A factory that used dir\lol_review.db (legacy resolution) marks
        // that path; when the file vanishes, the next factory resolves the
        // canonical dir\revu.db — whose own key was never marked. The sibling-
        // aware latch must still refuse to create a blank canonical DB there.
        var legacyPath = Path.Combine(Path.GetDirectoryName(NewDbPath())!, AppDataMigrator.LegacyDatabaseFileName);
        CreateHealthyDatabase(legacyPath); // observed + created at the LEGACY name

        SqliteConnection.ClearAllPools();
        File.Delete(legacyPath);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            if (File.Exists(legacyPath + suffix)) File.Delete(legacyPath + suffix);
        }

        var canonicalPath = Path.Combine(Path.GetDirectoryName(legacyPath)!, AppDataMigrator.NewDatabaseFileName);
        var factory = NewFactory(canonicalPath);
        Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());

        Assert.False(File.Exists(canonicalPath));
    }

    [Fact]
    public void OrphanWalJournal_BlocksFreshCreation_WithAJournalDiagnosis()
    {
        // A leftover revu.db-wal proves a database existed (its frames are real
        // data) — creating a fresh DB beside it would let SQLite replay FOREIGN
        // wal frames over the new file. Creation must refuse and the reason must
        // point the user at the leftover journal.
        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath + "-wal", [1, 2, 3, 4, 5]);

        var factory = NewFactory(dbPath);
        var ex = Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());

        Assert.Contains("journal", ex.Reason);
        Assert.False(File.Exists(dbPath));
    }

    [Fact]
    public void StaleStagingLeftovers_AreSweptByCreation()
    {
        // A crash mid-recovery leaves revu.db.recovering-<guid> trios behind; the
        // next creation pass must sweep them instead of letting them accumulate
        // in the user's data folder forever.
        var dbPath = NewDbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var stale = dbPath + ".recovering-deadbeef";
        File.WriteAllBytes(stale, [1]);

        var factory = NewFactory(dbPath);
        using var conn = factory.CreateConnection(); // recovery creates the DB

        Assert.True(File.Exists(dbPath));
        Assert.False(File.Exists(stale));
    }

    [Fact]
    public void CreateFreshDatabaseIfMissing_NoOps_WhenDbAlreadyExists()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);
        var contentBefore = File.ReadAllBytes(dbPath);

        var factory = NewFactory(dbPath);
        Assert.False(factory.CreateFreshDatabaseIfMissing());

        Assert.Equal(contentBefore, File.ReadAllBytes(dbPath));
    }

    // ── Read-only attribute self-heal (backup-restore / sync-tool leftovers) ──

    [Fact]
    public void ReadOnlyDbFile_IsHealedBeforeOpen_AndWritesSucceed()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);
        File.SetAttributes(dbPath, File.GetAttributes(dbPath) | FileAttributes.ReadOnly);

        var factory = NewFactory(dbPath);
        using var conn = factory.CreateConnection();
        ExecuteNonQuery(conn, "PRAGMA user_version = 11;"); // a real write

        Assert.Equal(0L, (long)(File.GetAttributes(dbPath) & FileAttributes.ReadOnly));
    }

    [Fact]
    public void ReadOnlyWalSidecarFiles_AreHealedBeforeOpen_AndWritesSucceed()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);

        // The field scenario: a sync/backup tool re-created -wal/-shm with the
        // read-only attribute. Zero-byte WAL files are valid (no frames).
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = dbPath + suffix;
            if (!File.Exists(path)) File.WriteAllBytes(path, []);
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }

        var factory = NewFactory(dbPath);
        using var conn = factory.CreateConnection();
        ExecuteNonQuery(conn, "PRAGMA user_version = 13;"); // a real write (hits the WAL)

        Assert.Equal(13, ScalarLong(conn, "PRAGMA user_version;"));
    }

    [Fact]
    public void ExclusivelyLockedDb_FailsWithLockDiagnosis_AndIsNotTouched()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);
        var sizeBefore = new FileInfo(dbPath).Length;

        using (var hold = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var factory = NewFactory(dbPath);
            var ex = Assert.Throws<DatabaseUnavailableException>(() => factory.CreateConnection());
            Assert.Contains("locked", ex.Reason);
        }

        Assert.Equal(sizeBefore, new FileInfo(dbPath).Length);

        // Once the lock is released, the same path works again.
        using var conn = NewFactory(dbPath).CreateConnection();
        ExecuteNonQuery(conn, "SELECT COUNT(*) FROM games");
    }

    [Fact]
    public void WriteServicesDiGraph_ConstructsTheFactory_WithTheNewCtorShape()
    {
        // The factory ctor gained an ILoggerFactory parameter; every write
        // endpoint resolves it through WriteServices' DI container, so a ctor DI
        // can't satisfy would brick ALL writes at runtime while compiling fine.
        // Constructing the graph + touching DatabasePath proves resolution works
        // (ctor only resolves paths via File.Exists — it never opens the DB).
        using var services = new WriteServices(NullLoggerFactory.Instance);

        Assert.False(string.IsNullOrWhiteSpace(services.DatabasePath));
    }

    // ── Existing healthy DB keeps its data through factory opens ─────────────

    [Fact]
    public void ExistingData_SurvivesFactoryOpens_Unchanged()
    {
        var dbPath = NewDbPath();
        CreateHealthyDatabase(dbPath);

        using (var conn = NewFactory(dbPath).CreateConnection())
        {
            ExecuteNonQuery(conn, "CREATE TABLE IF NOT EXISTS recovery_probe (val TEXT)");
            ExecuteNonQuery(conn, "INSERT INTO recovery_probe (val) VALUES ('keep')");
        }
        SqliteConnection.ClearAllPools();

        using (var conn = NewFactory(dbPath).CreateConnection())
        {
            Assert.Equal(1, ScalarLong(conn, "SELECT COUNT(*) FROM recovery_probe WHERE val = 'keep'"));
        }
    }
}
