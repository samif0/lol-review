namespace Revu.Core.Tests;

/// <summary>
/// Pins the SQLite engine that actually loads behind the app's connection factory.
/// The native e_sqlite3 library comes from the SQLitePCLRaw bundle that
/// Microsoft.Data.Sqlite depends on, so its version moves with that bundle, not
/// with Microsoft.Data.Sqlite's own version. 3.50.2 carries the CVE-2025-6965 fix.
/// </summary>
public sealed class SqliteEngineVersionTests
{
    private static readonly Version Minimum = new(3, 50, 2);

    [Fact]
    public void ConnectionFactory_LoadsAnEngineWithTheCve2025_6965Fix()
    {
        using var scope = new TestDatabaseScope();
        using var connection = scope.OpenConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        var loaded = Convert.ToString(cmd.ExecuteScalar())!;

        Assert.True(Version.Parse(loaded) >= Minimum, $"SQLite {loaded} loaded; {Minimum} or newer is required.");
    }
}
