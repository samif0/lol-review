using Microsoft.Data.Sqlite;
using Revu.Core.Data;

namespace Revu.Core.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task StartupSchema_RecordsCurrentSchemaVersion()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await using var connection = scope.OpenConnection();
        var version = await ExecuteScalarAsync<string>(connection, """
            SELECT value
            FROM schema_metadata
            WHERE key = 'app_schema_version'
            """);

        Assert.Equal(Schema.CurrentAppSchemaVersion.ToString(), version);
    }

    [Fact]
    public async Task StartupSchema_BackfillsObjectiveScoreFromPracticedGames()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        var objectiveId = await scope.Objectives.CreateAsync("Crash wave before reset", "macro");
        var gameId = await scope.Games.SaveManualAsync("Ahri", true);

        await using (var connection = scope.OpenConnection())
        {
            await ExecuteNonQueryAsync(connection, """
                INSERT INTO game_objectives (game_id, objective_id, practiced, execution_note)
                VALUES (@gameId, @objectiveId, 1, 'Did it once')
                """,
                ("@gameId", gameId),
                ("@objectiveId", objectiveId));

            await ExecuteNonQueryAsync(connection, """
                UPDATE objectives
                SET score = 0, game_count = 0
                WHERE id = @objectiveId
                """,
                ("@objectiveId", objectiveId));
        }

        await scope.InitializeAsync();

        var refreshed = await scope.Objectives.GetAsync(objectiveId);
        Assert.NotNull(refreshed);
        Assert.Equal(2, refreshed!.Score);
        Assert.Equal(1, refreshed.GameCount);
    }

    [Fact]
    public async Task StartupSchema_BackfillsMissingDefaultConceptTagsIntoExistingDatabase()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        await using (var connection = scope.OpenConnection())
        {
            await ExecuteNonQueryAsync(connection, """
                DELETE FROM concept_tags
                WHERE name = 'Poor micro'
                """);

            var remainingCount = await ExecuteScalarAsync<long>(connection, """
                SELECT COUNT(*)
                FROM concept_tags
                """);
            Assert.True(remainingCount > 0);
        }

        await scope.InitializeAsync();

        await using var verificationConnection = scope.OpenConnection();
        var poorMicroCount = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT COUNT(*)
            FROM concept_tags
            WHERE name = 'Poor micro'
            """);

        Assert.Equal(1, poorMicroCount);
    }

    private static async Task ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T));
    }
}
