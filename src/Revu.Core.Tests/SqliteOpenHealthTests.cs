using Revu.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace Revu.Core.Tests;

/// <summary>
/// P-041: self-heal + forensics for SQLITE_CANTOPEN ("unable to open database
/// file") on the write path. These pin the two contracts the sidecar's scan fix
/// depends on: TryHeal repairs exactly the safe-to-repair causes (missing data
/// folder, read-only attributes) and Describe names the real cause in a
/// user-actionable sentence.
/// </summary>
public sealed class SqliteOpenHealthTests : IDisposable
{
    private readonly string _root;

    public SqliteOpenHealthTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "Revu.Core.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // Clear any read-only attributes tests left behind so cleanup succeeds.
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }
            try { Directory.Delete(_root, recursive: true); } catch { }
        }
    }

    private string DbPath(string sub = "data") => Path.Combine(_root, sub, "revu.db");

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
    }

    private static void SetReadOnly(string path) =>
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

    private static bool IsReadOnly(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;

    // ── TryHeal ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryHeal_RecreatesMissingDataDirectory()
    {
        var dbPath = DbPath();

        var healed = SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance);

        Assert.True(healed);
        Assert.True(Directory.Exists(Path.GetDirectoryName(dbPath)!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void TryHeal_ClearsReadOnlyAttribute_OnDbAndWalSidecars(string suffix)
    {
        var dbPath = DbPath();
        Touch(dbPath);
        Touch(dbPath + "-wal");
        Touch(dbPath + "-shm");
        SetReadOnly(dbPath + suffix);

        var healed = SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance);

        Assert.True(healed);
        Assert.False(IsReadOnly(dbPath + suffix));
    }

    [Fact]
    public void TryHeal_ClearsAllThreeReadOnlyAttributes_InOnePass()
    {
        var dbPath = DbPath();
        Touch(dbPath);
        Touch(dbPath + "-wal");
        Touch(dbPath + "-shm");
        SetReadOnly(dbPath);
        SetReadOnly(dbPath + "-wal");
        SetReadOnly(dbPath + "-shm");

        Assert.True(SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance));

        Assert.False(IsReadOnly(dbPath));
        Assert.False(IsReadOnly(dbPath + "-wal"));
        Assert.False(IsReadOnly(dbPath + "-shm"));
    }

    [Fact]
    public void TryHeal_ReturnsFalse_WhenNothingToRepair()
    {
        var dbPath = DbPath();
        Touch(dbPath);

        Assert.False(SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance));
    }

    [Fact]
    public void TryHeal_PreservesOtherAttributes_WhenClearingReadOnly()
    {
        var dbPath = DbPath();
        Touch(dbPath);
        File.SetAttributes(dbPath, FileAttributes.Hidden | FileAttributes.ReadOnly);

        SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance);

        var attributes = File.GetAttributes(dbPath);
        Assert.False((attributes & FileAttributes.ReadOnly) != 0);
        Assert.True((attributes & FileAttributes.Hidden) != 0);
    }

    [Fact]
    public void TryHeal_NeverThrows_WhenDirectoryCreationIsBlockedByAFile()
    {
        // Parent "directory" is actually a file — CreateDirectory throws inside;
        // TryHeal's contract is to swallow and return false.
        var blocker = Path.Combine(_root, "data");
        File.WriteAllText(blocker, "not a directory");
        var dbPath = Path.Combine(blocker, "revu.db");

        var healed = SqliteOpenHealth.TryHeal(dbPath, NullLogger.Instance);

        Assert.False(healed);
    }

    // ── Describe ─────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_NamesMissingDataFolder()
    {
        var dbPath = DbPath("gone");

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("data folder is missing", reason);
        Assert.Contains(Path.GetDirectoryName(dbPath)!, reason);
    }

    [Fact]
    public void Describe_NamesFileBlockingTheDataFolder()
    {
        var blocker = Path.Combine(_root, "data");
        File.WriteAllText(blocker, "not a directory");
        var dbPath = Path.Combine(blocker, "revu.db");

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("a file is blocking", reason);
    }

    [Fact]
    public void Describe_NamesFolderSquattingOnTheDbPath()
    {
        var dbPath = DbPath();
        Directory.CreateDirectory(dbPath); // a directory named revu.db

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("a folder exists where", reason);
    }

    [Fact]
    public void Describe_NamesMissingDatabaseFile()
    {
        var dbPath = DbPath();
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("database file is missing", reason);
        Assert.Contains(dbPath, reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public void Describe_NamesReadOnlyFile(string suffix)
    {
        var dbPath = DbPath();
        Touch(dbPath);
        Touch(dbPath + "-wal");
        Touch(dbPath + "-shm");
        SetReadOnly(dbPath + suffix);

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("read-only", reason);
        Assert.Contains(Path.GetFileName(dbPath + suffix), reason);
    }

    [Fact]
    public void Describe_NamesExclusiveLock()
    {
        var dbPath = DbPath();
        Touch(dbPath);

        using var exclusive = new FileStream(dbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("locked", reason);
    }

    [Fact]
    public void Describe_FallsBackToGenericSentence_WhenFileLooksHealthy()
    {
        var dbPath = DbPath();
        Touch(dbPath);

        var reason = SqliteOpenHealth.Describe(dbPath);

        Assert.Contains("could not be opened", reason);
        Assert.Contains(dbPath, reason);
    }

    // ── Error classification ────────────────────────────────────────────────

    [Theory]
    [InlineData(14, true)]  // SQLITE_CANTOPEN
    [InlineData(8, true)]   // SQLITE_READONLY (silent read-only downgrade at open)
    [InlineData(1, false)]  // SQLITE_ERROR (e.g. no such table) — NOT an open failure
    [InlineData(5, false)]  // SQLITE_BUSY
    public void IsOpenOrWriteAccessFailure_ClassifiesByBaseErrorCode(int code, bool expected)
    {
        var ex = new SqliteException("test", code);

        Assert.Equal(expected, SqliteOpenHealth.IsOpenOrWriteAccessFailure(ex));
    }

    [Fact]
    public void IsOpenOrWriteAccessFailure_UnwrapsInnerExceptions()
    {
        var inner = new SqliteException("unable to open database file", 14);
        var wrapped = new InvalidOperationException("outer", new IOException("mid", inner));

        Assert.True(SqliteOpenHealth.IsOpenOrWriteAccessFailure(wrapped));
    }

    [Fact]
    public void IsOpenOrWriteAccessFailure_False_ForNonSqliteExceptions()
    {
        Assert.False(SqliteOpenHealth.IsOpenOrWriteAccessFailure(new IOException("disk")));
    }

    [Fact]
    public void DatabaseUnavailableException_CarriesReasonAndPath()
    {
        var inner = new SqliteException("unable to open database file", 14);
        var ex = new DatabaseUnavailableException(@"C:\data\revu.db", "the reason", inner);

        Assert.Equal(@"C:\data\revu.db", ex.DatabasePath);
        Assert.Equal("the reason", ex.Reason);
        Assert.Contains("the reason", ex.Message);
        Assert.Same(inner, ex.InnerException);
    }
}
