using Microsoft.Data.Sqlite;
using Revu.Core.Data;

namespace Revu.Core.Tests;

/// <summary>
/// Hot per-game lookups (bookmarks on every VOD and review load, the matchup journal,
/// objective reconciliation, the event-key sweep's startup count) used to full-scan.
/// The indexes ship without a schema version bump: the startup index pass creates them
/// on existing databases, and the planner must actually choose them.
/// </summary>
public sealed class LookupIndexTests
{
    private static readonly string[] Indexes =
    [
        "idx_vod_bookmarks_game",
        "idx_matchups_game",
        "idx_matchup_notes_game",
        "idx_game_objectives_objective",
        "idx_derived_instances_game",
        "idx_game_events_unkeyed",
    ];

    [Fact]
    public async Task ExistingDatabase_GainsTheIndexesOnStartup_WithoutAVersionBump()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        string versionBefore;
        using (var conn = scope.OpenConnection())
        {
            // A database from before this change: none of the indexes exist yet.
            foreach (var name in Indexes) await Exec(conn, $"DROP INDEX IF EXISTS {name}");
            versionBefore = await Text(conn, "SELECT value FROM schema_metadata WHERE key = 'app_schema_version'");
        }

        // The sidecar's startup path: additive schema, then seeds and indexes.
        await scope.Initializer.ApplyAdditiveSchemaAsync();
        await scope.Initializer.ApplySeedsAndIndexesAsync();

        using var check = scope.OpenConnection();
        foreach (var name in Indexes)
            Assert.Equal("1", await Text(check, $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{name}'"));
        Assert.Equal(versionBefore, await Text(check, "SELECT value FROM schema_metadata WHERE key = 'app_schema_version'"));
        Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), versionBefore);
    }

    [Theory]
    [InlineData("SELECT * FROM vod_bookmarks WHERE game_id = 1 ORDER BY game_time_s ASC", "idx_vod_bookmarks_game")]
    [InlineData("SELECT * FROM matchups WHERE game_id = 1 ORDER BY created_at DESC, id DESC LIMIT 1", "idx_matchups_game")]
    [InlineData("SELECT * FROM matchup_notes WHERE game_id = 1 ORDER BY created_at DESC LIMIT 1", "idx_matchup_notes_game")]
    [InlineData("SELECT game_id FROM game_objectives WHERE objective_id = 1", "idx_game_objectives_objective")]
    [InlineData("DELETE FROM derived_event_instances WHERE game_id = 1", "idx_derived_instances_game")]
    [InlineData("SELECT COUNT(*) FROM game_events WHERE event_key IS NULL", "idx_game_events_unkeyed")]
    [InlineData("SELECT game_id FROM game_events WHERE event_key IS NULL GROUP BY game_id ORDER BY game_id DESC", "idx_game_events_unkeyed")]
    public async Task TheHotLookups_UseTheirIndex(string sql, string index)
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        var plan = new List<string>();
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) plan.Add(reader.GetString(reader.FieldCount - 1));

        Assert.Contains(plan, step => step.Contains(index, StringComparison.Ordinal));
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<string> Text(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
    }
}
