using Microsoft.Data.Sqlite;

namespace Revu.Core.Tests;

/// <summary>
/// The startup schema pass puts a database that arrived in rollback-journal mode (a
/// DataSnapshot restore, an old copy) back into WAL, the mode production runs in.
/// </summary>
public sealed class WalModeReassertTests
{
    private static string JournalMode(string path)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode;";
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    [Fact]
    public async Task ApplyAdditiveSchema_ReassertsWal_OnARollbackJournalDatabase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = scope.DatabasePath, Pooling = false,
               }.ToString()))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=DELETE;";
            Assert.Equal("delete", Convert.ToString(cmd.ExecuteScalar()));
        }
        Assert.Equal("delete", JournalMode(scope.DatabasePath));

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        Assert.Equal("wal", JournalMode(scope.DatabasePath));
    }
}
