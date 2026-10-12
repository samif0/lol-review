using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>
/// Game delete runs with foreign keys off, so every ON DELETE CASCADE in the schema is
/// inert: each game-keyed table has to be on the delete list by hand. These tests fail
/// when a table with a game_id column is added without being accounted for.
/// </summary>
public sealed class GameDeleteCoverageTests
{
    [Fact]
    public async Task EveryTableWithAGameId_IsCoveredByGameDelete()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var uncovered = new List<string>();
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT m.name FROM sqlite_master m
            WHERE m.type = 'table' AND m.name NOT LIKE 'sqlite_%' AND m.name != 'games'
              AND EXISTS (SELECT 1 FROM pragma_table_info(m.name) c WHERE c.name = 'game_id')
            ORDER BY m.name
            """;
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            if (!GameRepository.GameDeleteCoveredTables.Contains(table)) uncovered.Add(table);
        }

        Assert.Empty(uncovered);
    }

    [Fact]
    public async Task Delete_RemovesTheEventProcessingReport()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8401));
        await scope.Games.SaveAsync(TestGameStatsFactory.Create(8402));
        await ExecAsync(scope, """
            INSERT INTO event_processing_reports (game_id, report_json) VALUES (8401, '{}'), (8402, '{}');
            """);

        await scope.Games.DeleteAsync(8401);

        Assert.Equal(0, await CountAsync(scope, "SELECT COUNT(*) FROM event_processing_reports WHERE game_id = 8401"));
        Assert.Equal(1, await CountAsync(scope, "SELECT COUNT(*) FROM event_processing_reports WHERE game_id = 8402"));
    }

    private static async Task<long> CountAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task ExecAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
