using Microsoft.Data.Sqlite;
using Revu.Core.Data;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Schema v16: the event_key column, the ledger table and the eager import of the legacy
/// details.source='reviewed_encounter' rows, through the sidecar's additive startup path.
/// </summary>
public sealed class EventCorrectionsLegacyImportTests
{
    private const long GameId = 5001;
    private const string RequestId = "3c6b0a7e-2f1d-4d4a-9a1e-0f2b5c6d7e81";

    private const string ConvertedDetails =
        "{\"source\":\"reviewed_encounter\",\"reviewed\":true,\"classification_version\":1,\"classification\":\"all_in\",\"kind\":null," +
        "\"start_s\":100,\"end_s\":114,\"duration_s\":14,\"note\":\"Committed pursuit\"," +
        "\"original_type\":\"TRADE\",\"original_time_s\":110,\"original_details\":\"{\\\"kind\\\":\\\"extended\\\"}\"}";

    private const string AddedDetails =
        "{\"source\":\"reviewed_encounter\",\"request_id\":\"" + RequestId + "\",\"reviewed\":true,\"classification_version\":1," +
        "\"classification\":\"short\",\"kind\":\"short\",\"start_s\":191,\"end_s\":192,\"duration_s\":1,\"note\":\"Q bounce\"}";

    [Fact]
    public async Task ApplyAdditiveSchemaAsync_BringsV15DatabaseToV16_AndImportsReviewedRows()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        var (convertedId, addedId, malformedId) = await SeedV15Async(scope);

        await scope.Initializer.ApplyAdditiveSchemaAsync();

        using var conn = scope.OpenConnection();
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('game_events') WHERE name='event_key'"));
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_game_events_key'"));
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_corrections'"));
        Assert.Equal("16", (string?)await Scalar(conn, "SELECT value FROM schema_metadata WHERE key='app_schema_version'"));

        var ledger = await new EventCorrectionsRepository(scope.ConnectionFactory).GetActiveForGameAsync(GameId);
        Assert.Equal(2, ledger.Count);

        var converted = Assert.Single(ledger, c => c.Op == CorrectionOps.Retype);
        Assert.Equal(EventCorrectionsLegacyImport.LegacyCorrectionId(convertedId).ToString("D"), converted.CorrectionId);
        Assert.Equal("det:TRADE:110:", converted.SubjectKey);
        Assert.Equal("TRADE", converted.SubjectType);
        Assert.Equal(110, converted.SubjectTimeS);
        Assert.Equal("TRADE", converted.Original.EventType);
        Assert.Equal(110, converted.Original.GameTimeS);
        Assert.Contains("\"kind\":\"extended\"", converted.Original.Details);
        Assert.Equal("ALL_IN", converted.Patch.EventType);
        Assert.Equal(100, converted.Patch.GameTimeS);
        Assert.Equal(114, converted.Patch.EndS);
        Assert.Null(converted.Patch.Attrs);
        Assert.Equal("Committed pursuit", converted.Reason);
        Assert.Equal(CorrectionDetectors.ReviewedEncounter, converted.Detector);
        Assert.Equal(1, converted.DetectorVersion);
        Assert.Equal(-10, converted.DeltaS);
        Assert.Equal(convertedId, converted.AppliedEventId);
        Assert.Equal(CorrectionStates.Active, converted.State);

        var added = Assert.Single(ledger, c => c.Op == CorrectionOps.Add);
        Assert.Equal(RequestId, added.CorrectionId);
        Assert.Equal("usr:" + RequestId, added.SubjectKey);
        Assert.Equal("TRADE", added.SubjectType);
        Assert.Equal(191, added.SubjectTimeS);
        Assert.True(added.Original.IsNone);
        Assert.Equal("TRADE", added.Patch.EventType);
        Assert.Equal(191, added.Patch.GameTimeS);
        Assert.Equal(192, added.Patch.EndS);
        Assert.Equal("short", added.Patch.Attrs!["kind"]!.GetValue<string>());
        Assert.Equal("Q bounce", added.Reason);
        Assert.Null(added.DeltaS);
        Assert.Equal(addedId, added.AppliedEventId);

        var rows = await EventCorrectionSql.LoadRowsAsync(conn, null, GameId);
        var convertedRow = Assert.Single(rows, r => r.Id == convertedId);
        Assert.Equal("det:TRADE:110:", convertedRow.EventKey);
        Assert.Equal((converted.CorrectionId, "retype"), Services.EventPatching.ReadMarker(convertedRow.Details));
        Assert.Contains("Committed pursuit", convertedRow.Details);
        Assert.Contains("\"original_type\":\"TRADE\"", convertedRow.Details);
        var addedRow = Assert.Single(rows, r => r.Id == addedId);
        Assert.Equal("usr:" + RequestId, addedRow.EventKey);
        Assert.Equal((RequestId, "add"), Services.EventPatching.ReadMarker(addedRow.Details));
        var malformedRow = Assert.Single(rows, r => r.Id == malformedId);
        Assert.Null(malformedRow.EventKey);
        Assert.Equal("not json", malformedRow.Details);
        Assert.Null(Assert.Single(rows, r => r.EventType == "KILL").EventKey);
    }

    [Fact]
    public async Task LegacyImport_IsIdempotent()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await SeedV15Async(scope);
        await scope.Initializer.ApplyAdditiveSchemaAsync();

        await scope.Initializer.ApplyAdditiveSchemaAsync();
        using var conn = scope.OpenConnection();
        Assert.Equal(0, await EventCorrectionsLegacyImport.RunAsync(conn));

        Assert.Equal(2L, await Scalar(conn, "SELECT COUNT(*) FROM event_corrections"));
        Assert.Equal(2L, await Scalar(conn, "SELECT COUNT(*) FROM game_events WHERE json_valid(details) AND json_extract(details, '$.correction') IS NOT NULL"));
        Assert.Equal("16", (string?)await Scalar(conn, "SELECT value FROM schema_metadata WHERE key='app_schema_version'"));
    }

    [Fact]
    public async Task InitializeAsync_FreshDatabase_HasEventKeyColumnAndLedger()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        using var conn = scope.OpenConnection();
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM pragma_table_info('game_events') WHERE name='event_key'"));
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='event_corrections'"));
        Assert.Equal(1L, await Scalar(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_event_corrections_cid'"));
        Assert.Equal(0L, await Scalar(conn, "SELECT COUNT(*) FROM event_corrections"));
        Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), (string?)await Scalar(conn, "SELECT value FROM schema_metadata WHERE key='app_schema_version'"));
    }

    /// <summary>Seeds the legacy rows, then makes the DB look like one last migrated at v15:
    /// no event_key column, no ledger table, version 15.</summary>
    private static async Task<(long Converted, long Added, long Malformed)> SeedV15Async(TestDatabaseScope scope)
    {
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(GameId));
        using var conn = scope.OpenConnection();
        await Exec(conn, "DROP INDEX IF EXISTS idx_game_events_key");
        await Exec(conn, "ALTER TABLE game_events DROP COLUMN event_key");
        await Exec(conn, "DROP TABLE IF EXISTS event_corrections");
        var converted = await Insert(conn, "ALL_IN", 100, ConvertedDetails);
        var added = await Insert(conn, "TRADE", 191, AddedDetails);
        var malformed = await Insert(conn, "TRADE", 300, "not json");
        await Insert(conn, "KILL", 115, "{\"victim\":\"Jinx\"}");
        await Exec(conn,
            "INSERT INTO schema_metadata (key, value, updated_at) VALUES ('app_schema_version','15',0) "
            + "ON CONFLICT(key) DO UPDATE SET value='15'");
        return (converted, added, malformed);
    }

    private static async Task<long> Insert(SqliteConnection conn, string type, int t, string details)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO game_events (game_id, event_type, game_time_s, details) VALUES (@g, @t, @s, @d) RETURNING id";
        cmd.Parameters.AddWithValue("@g", GameId);
        cmd.Parameters.AddWithValue("@t", type);
        cmd.Parameters.AddWithValue("@s", t);
        cmd.Parameters.AddWithValue("@d", details);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }
}
