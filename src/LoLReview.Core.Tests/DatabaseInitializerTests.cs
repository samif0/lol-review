using Microsoft.Data.Sqlite;

namespace LoLReview.Core.Tests;

public sealed class DatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_NormalizesLegacyRulesObjectivesAndGameObjectivesTables()
    {
        using var scope = new TestDatabaseScope();
        await scope.InitializeAsync();

        using (var connection = scope.OpenConnection())
        {
            await ExecuteNonQueryAsync(connection, "DROP TABLE game_objectives");
            await ExecuteNonQueryAsync(connection, "DROP TABLE objectives");
            await ExecuteNonQueryAsync(connection, "DROP TABLE rules");

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT,
                    description TEXT,
                    status TEXT
                )
                """);

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO rules (id, title, description, status)
                VALUES (1, 'No tilt queueing', 'Stop after two losses', 'active')
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE objectives (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    title TEXT,
                    description TEXT
                )
                """);

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO objectives (id, title, description)
                VALUES (7, 'Track jungle', 'Watch first clear')
                """);

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO games (
                    game_id, timestamp, date_played, game_duration, game_mode,
                    game_type, queue_type, summoner_name, champion_name, champion_id,
                    team_id, position, role, win
                )
                VALUES (
                    1001, 1710000000, '2024-03-09 10:00', 1800, 'Ranked Solo',
                    'MATCHED_GAME', 'Ranked Solo/Duo', 'Tester', 'Ahri', 103,
                    100, 'MIDDLE', '', 1
                )
                """);

            await ExecuteNonQueryAsync(connection, """
                CREATE TABLE game_objectives (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    game_id INTEGER NOT NULL,
                    objective_id INTEGER NOT NULL,
                    score INTEGER DEFAULT 0,
                    notes TEXT DEFAULT ''
                )
                """);

            await ExecuteNonQueryAsync(connection, """
                INSERT INTO game_objectives (id, game_id, objective_id, score, notes)
                VALUES (9, 1001, 7, 4, 'Tracked both starts')
                """);
        }

        await scope.InitializeAsync();

        await using var verificationConnection = scope.OpenConnection();
        var ruleColumns = await GetColumnNamesAsync(verificationConnection, "rules");
        var objectiveColumns = await GetColumnNamesAsync(verificationConnection, "objectives");
        var gameObjectiveColumns = await GetColumnNamesAsync(verificationConnection, "game_objectives");

        Assert.Contains("name", ruleColumns);
        Assert.DoesNotContain("title", ruleColumns);

        Assert.Contains("skill_area", objectiveColumns);
        Assert.Contains("score", objectiveColumns);
        Assert.Contains("game_count", objectiveColumns);

        Assert.Contains("practiced", gameObjectiveColumns);
        Assert.Contains("execution_note", gameObjectiveColumns);
        Assert.DoesNotContain("score", gameObjectiveColumns);
        Assert.DoesNotContain("notes", gameObjectiveColumns);

        var objectiveScore = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT score
            FROM objectives
            WHERE id = 7
            """);
        var practiced = await ExecuteScalarAsync<long>(verificationConnection, """
            SELECT practiced
            FROM game_objectives
            WHERE id = 9
            """);
        var executionNote = await ExecuteScalarAsync<string>(verificationConnection, """
            SELECT execution_note
            FROM game_objectives
            WHERE id = 9
            """);

        Assert.Equal(4, objectiveScore);
        Assert.Equal(1, practiced);
        Assert.Equal("Tracked both starts", executionNote);
    }

    [Fact]
    public async Task InitializeAsync_BackfillsObjectiveScoreFromPracticedGames()
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
    public async Task InitializeAsync_BackfillsMissingDefaultConceptTagsIntoExistingDatabase()
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

    private static async Task<object?> ExecuteScalarObjectAsync(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<HashSet<string>> GetColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";

        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }
}
