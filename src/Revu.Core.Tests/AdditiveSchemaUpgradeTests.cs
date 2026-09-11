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

    /// <summary>
    /// v12 (coaching stints): a DB last migrated at v11 must gain the
    /// coaching_stints table AND the three sessions columns, with existing
    /// session rows untouched.
    /// </summary>
    [Fact]
    public async Task ApplyAdditiveSchemaAsync_BringsV11DatabaseToV12()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        // A pre-upgrade block that must survive the migration.
        await scope.SessionLog.SetSessionIntentionAsync("2026-07-01", "keep me");

        // Simulate a v11-era DB: drop the v12 table and pin the version back.
        // (The sessions columns can't be un-ALTERed, but the runner's
        // duplicate-column tolerance makes re-running the set a no-op for them.)
        using (var conn = scope.OpenConnection())
        {
            await Exec(conn, "DROP TABLE IF EXISTS coaching_stints");
            await Exec(conn,
                "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','11',0) "
                + "ON CONFLICT(key) DO UPDATE SET value='11'");
        }
        Assert.False(await TableExists(scope, "coaching_stints"));

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        Assert.True(await TableExists(scope, "coaching_stints"));

        // The stint round-trip the new endpoints perform now works, and the
        // migrated sessions columns accept a stint stamp.
        var id = await scope.CoachingStints.StartStintAsync("Violet", "2026-07-30", "2026-12-30");
        await scope.SessionLog.SetSessionIntentionAsync(
            "2026-07-30", "first stint block", withCoach: true, stintId: id, stintBlockNumber: 1);
        var counts = await scope.CoachingStints.GetBlockCountsAsync(id);
        Assert.Equal(1, counts.Total);
        Assert.Equal(1, counts.WithCoach);

        // Pre-upgrade data survived with NULL stint columns.
        var old = await scope.SessionLog.GetSessionAsync("2026-07-01");
        Assert.NotNull(old);
        Assert.Equal("keep me", old!.Intention);
        Assert.Null(old.StintId);
        Assert.False(old.WithCoach);
    }

    /// <summary>
    /// v13 (pattern evidence): a DB last migrated at v12 must gain the
    /// games.pattern_evidence_v backfill-marker column and record version 13.
    /// (The column can't be un-ALTERed here, so this pins the version advance +
    /// duplicate-column tolerance + the column being usable.)
    /// </summary>
    [Fact]
    public async Task ApplyAdditiveSchemaAsync_BringsV12DatabaseToV13()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        using (var conn = scope.OpenConnection())
        {
            await Exec(conn,
                "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','12',0) "
                + "ON CONFLICT(key) DO UPDATE SET value='12'");
        }

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        using (var conn = scope.OpenConnection())
        {
            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM schema_metadata WHERE key='app_schema_version'";
            Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), (string?)await versionCmd.ExecuteScalarAsync());

            // The backfill-marker column exists and defaults to NULL (= queued).
            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('games') WHERE name='pattern_evidence_v'";
            Assert.Equal(1L, await colCmd.ExecuteScalarAsync());
        }
    }

    /// <summary>
    /// v15 (matchup journal): a DB last migrated at v14 must gain the matchups
    /// table (and its lane index), record version 15, and accept a card.
    /// </summary>
    [Fact]
    public async Task ApplyAdditiveSchemaAsync_BringsV14DatabaseToV15()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        using (var conn = scope.OpenConnection())
        {
            await Exec(conn, "DROP TABLE IF EXISTS matchups");
            await Exec(conn,
                "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','14',0) "
                + "ON CONFLICT(key) DO UPDATE SET value='14'");
        }
        Assert.False(await TableExists(scope, "matchups"));

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        Assert.True(await TableExists(scope, "matchups"));
        using (var conn = scope.OpenConnection())
        {
            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM schema_metadata WHERE key='app_schema_version'";
            Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), (string?)await versionCmd.ExecuteScalarAsync());

            using var indexCmd = conn.CreateCommand();
            indexCmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='index' AND name='idx_matchups_lane_created'";
            Assert.NotNull(await indexCmd.ExecuteScalarAsync());
        }

        // The round-trip the write endpoint performs now works.
        var id = await scope.Matchups.CreateAsync("top", ["Aatrox"], ["Sett"], prior: "Respect level 2.");
        var card = await scope.Matchups.GetAsync(id);
        Assert.NotNull(card);
        Assert.Equal("Respect level 2.", card!.Prior);
    }

    /// <summary>
    /// v17 (matchup provenance): a DB last migrated at v16 must gain the
    /// games.matchup_source column (default '') and record version 17; a row
    /// written before the column reads back as an unstamped legacy row.
    /// </summary>
    [Fact]
    public async Task ApplyAdditiveSchemaAsync_BringsV16DatabaseToV17()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var legacy = TestGameStatsFactory.Create(1717, champion: "Miss Fortune");
        legacy.MatchupSource = "live";
        await scope.Games.SaveAsync(legacy);

        // Put the DB back into a real v16 shape: no matchup_source column at all.
        using (var conn = scope.OpenConnection())
        {
            await Exec(conn, "ALTER TABLE games DROP COLUMN matchup_source");
            await Exec(conn,
                "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','16',0) "
                + "ON CONFLICT(key) DO UPDATE SET value='16'");
            using var gone = conn.CreateCommand();
            gone.CommandText = "SELECT COUNT(*) FROM pragma_table_info('games') WHERE name='matchup_source'";
            Assert.Equal(0L, await gone.ExecuteScalarAsync());
        }

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        using (var conn = scope.OpenConnection())
        {
            using var versionCmd = conn.CreateCommand();
            versionCmd.CommandText = "SELECT value FROM schema_metadata WHERE key='app_schema_version'";
            Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), (string?)await versionCmd.ExecuteScalarAsync());

            using var colCmd = conn.CreateCommand();
            colCmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('games') WHERE name='matchup_source'";
            Assert.Equal(1L, await colCmd.ExecuteScalarAsync());
        }

        // The row written before the upgrade reads back as an unstamped legacy row,
        // and new writes carry the column again.
        Assert.Equal("", (await scope.Games.GetAsync(1717))!.MatchupSource);
        var fresh = TestGameStatsFactory.Create(1718, champion: "Miss Fortune");
        fresh.MatchupSource = "live";
        await scope.Games.SaveAsync(fresh);
        Assert.Equal("live", (await scope.Games.GetAsync(1718))!.MatchupSource);
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
