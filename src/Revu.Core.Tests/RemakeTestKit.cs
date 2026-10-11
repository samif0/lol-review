using System.Text.Json;
using Microsoft.Data.Sqlite;
using Revu.Core.Data.Repositories;

namespace Revu.Core.Tests;

/// <summary>3.14.1: shared seeding helpers for the remake (early surrender) tests.</summary>
internal static class RemakeTestKit
{
    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static JsonElement Fixture(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Revu.Core.Tests", "Fixtures", "StatsExtractor", fileName);
            if (File.Exists(candidate)) return Json(File.ReadAllText(candidate));
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Could not locate fixture {fileName}");
    }

    public static async Task InsertGameAsync(
        SqliteConnection conn, long gameId, bool win, DateTimeOffset at, bool hidden = false,
        string queue = "Ranked Solo/Duo", int duration = 1800)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO games (game_id, champion_name, win, timestamp, queue_type, is_hidden, game_duration, date_played)
            VALUES (@gameId, 'Ahri', @win, @timestamp, @queue, @hidden, @duration, @date)";
        cmd.Parameters.AddWithValue("@gameId", gameId);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", at.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("@queue", queue);
        cmd.Parameters.AddWithValue("@hidden", hidden ? 1 : 0);
        cmd.Parameters.AddWithValue("@duration", duration);
        cmd.Parameters.AddWithValue("@date", at.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task InsertSessionRowAsync(
        SqliteConnection conn, string date, long? gameId, bool win, int mental = 5, bool ruleBroken = false,
        DateTimeOffset? at = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO session_log (date, game_id, champion_name, win, mental_rating, rule_broken, timestamp)
            VALUES (@date, @gameId, 'Ahri', @win, @mental, @ruleBroken, @timestamp)";
        cmd.Parameters.AddWithValue("@date", date);
        cmd.Parameters.AddWithValue("@gameId", gameId.HasValue ? gameId.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("@win", win ? 1 : 0);
        cmd.Parameters.AddWithValue("@mental", mental);
        cmd.Parameters.AddWithValue("@ruleBroken", ruleBroken ? 1 : 0);
        cmd.Parameters.AddWithValue("@timestamp", (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    public static Task<RemakeWriteResult> MarkAsync(
        TestDatabaseScope scope, long gameId, bool isRemake = true, string source = RemakeSql.SourceEog) =>
        scope.Games.SetRemakeVerdictAsync(gameId, isRemake, false, source);

    public static async Task<long> CountAsync(TestDatabaseScope scope, string sql)
    {
        using var conn = scope.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public static string Today => DateTime.Today.ToString("yyyy-MM-dd");
}
