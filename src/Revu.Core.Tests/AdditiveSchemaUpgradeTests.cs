using Microsoft.Data.Sqlite;
using Revu.Core.Data;

namespace Revu.Core.Tests;

/// <summary>
/// Regression for the v3.0.15 "sidecar JSON parse failed" bug: after the WinUI app
/// (which owned DB migration) was deleted, NO process ran migrations, so a new
/// versioned migration (v8 objective_event_types) never created its table and write
/// endpoints hit "no such table". ApplyAdditiveSchemaAsync — the sidecar's new
/// startup migration step — must bring a v7-era DB forward additively (no data loss).
/// </summary>
public sealed class AdditiveSchemaUpgradeTests
{
    [Fact]
    public async Task ApplyAdditiveSchemaAsync_CreatesMissingTableOnV7EraDatabase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // Simulate the user's WinUI-era DB: drop the v8 table and pin the recorded
        // schema version back to 7, exactly as a DB last migrated by the old app looks.
        using (var conn = scope.OpenConnection())
        {
            await Exec(conn, "DROP TABLE IF EXISTS objective_event_types");
            await Exec(conn,
                "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','7',0) "
                + "ON CONFLICT(key) DO UPDATE SET value='7'");
        }

        // The table is gone — a write would throw "no such table" right now.
        Assert.False(await TableExists(scope, "objective_event_types"));

        // The sidecar's startup step brings it forward.
        await scope.Initializer.ApplyAdditiveSchemaAsync();

        Assert.True(await TableExists(scope, "objective_event_types"));

        // And the round-trip that the save endpoint performs now works.
        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Track smite", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "SPELL_SMITE" });
        Assert.Equal(new[] { "SPELL_SMITE" }, await scope.Objectives.GetEventTokensForObjectiveAsync(id));
    }

    [Fact]
    public async Task ApplyAdditiveSchemaAsync_IsIdempotent_PreservesExistingData()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var id = await scope.Objectives.CreateWithPhasesAsync(
            "Keep me", "", "primary", "", "",
            practicePre: true, practiceIn: false, practicePost: false);
        await scope.Objectives.SetEventTokensForObjectiveAsync(id, new[] { "DRAGON" });

        // Running the additive step again must not drop/clear anything.
        await scope.Initializer.ApplyAdditiveSchemaAsync();
        await scope.Initializer.ApplyAdditiveSchemaAsync();

        Assert.Equal(new[] { "DRAGON" }, await scope.Objectives.GetEventTokensForObjectiveAsync(id));
        var obj = await scope.Objectives.GetAsync(id);
        Assert.NotNull(obj);
        Assert.Equal("Keep me", obj!.Title);
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<bool> TableExists(TestDatabaseScope scope, string table)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", table);
        return await cmd.ExecuteScalarAsync() is not null;
    }
}
